using System.IO.Compression;
using System.Text;

namespace Vivarium.Agent;

/// <summary>Extracts D3 payload archives without allowing an entry to escape or pivot out of the workdir.</summary>
public static class PayloadArchiveExtractor
{
    private const int FileTypeMask = 0xF000;
    private const int RegularFile = 0x8000;
    private const int DirectoryFile = 0x4000;
    private const int SymbolicLink = 0xA000;
    private const int MaximumLinkTargetBytes = 32 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static void Extract(string archivePath, string destinationDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);

        var root = Path.GetFullPath(destinationDirectory);
        Directory.CreateDirectory(root);
        RejectReparsePoint(root, "The archive destination");

        using var archive = ZipFile.OpenRead(archivePath);
        var entries = ValidateEntries(archive);
        ValidateExistingPaths(root, entries);

        foreach (var entry in entries
                     .Where(static entry => entry.Kind == EntryKind.Directory)
                     .OrderBy(static entry => entry.Segments.Length)
                     .ThenBy(static entry => entry.NormalizedName, StringComparer.Ordinal))
        {
            EnsureDirectories(root, entry.Segments);
        }

        foreach (var entry in entries
                     .Where(static entry => entry.Kind == EntryKind.RegularFile)
                     .OrderBy(static entry => entry.NormalizedName, StringComparer.Ordinal))
        {
            ExtractRegularFile(root, entry);
        }

        foreach (var entry in entries
                     .Where(static entry => entry.Kind == EntryKind.SymbolicLink)
                     .OrderBy(static entry => entry.NormalizedName, StringComparer.Ordinal))
        {
            CreateSymbolicLink(root, entry, entries);
        }

        if (!OperatingSystem.IsWindows())
        {
            foreach (var entry in entries
                         .Where(static entry => entry.Kind == EntryKind.Directory)
                         .OrderByDescending(static entry => entry.Segments.Length))
            {
                ApplyUnixMode(Combine(root, entry.Segments), entry.UnixMode);
            }
        }
    }

    private static List<ValidatedEntry> ValidateEntries(ZipArchive archive)
    {
        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var byName = new Dictionary<string, ValidatedEntry>(comparer);

        foreach (var archiveEntry in archive.Entries)
        {
            var (normalizedName, segments, nameSaysDirectory) = NormalizeEntryName(archiveEntry.FullName);
            var unixMode = (archiveEntry.ExternalAttributes >> 16) & 0xFFFF;
            var fileType = unixMode & FileTypeMask;
            var kind = fileType switch
            {
                0 => nameSaysDirectory ? EntryKind.Directory : EntryKind.RegularFile,
                RegularFile => EntryKind.RegularFile,
                DirectoryFile => EntryKind.Directory,
                SymbolicLink => EntryKind.SymbolicLink,
                _ => throw Invalid(archiveEntry, "unsupported Unix file type"),
            };

            if (nameSaysDirectory != (kind == EntryKind.Directory))
            {
                throw Invalid(archiveEntry, "the entry name and Unix file type disagree");
            }

            string? linkTarget = null;
            if (kind == EntryKind.SymbolicLink)
            {
                if (archiveEntry.Length > MaximumLinkTargetBytes)
                {
                    throw Invalid(archiveEntry, "symbolic-link target is too large");
                }

                using var source = archiveEntry.Open();
                using var buffer = new MemoryStream();
                source.CopyTo(buffer);
                try
                {
                    linkTarget = StrictUtf8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
                }
                catch (DecoderFallbackException exception)
                {
                    throw Invalid(archiveEntry, "symbolic-link target is not valid UTF-8", exception);
                }

                if (linkTarget.Length == 0 || linkTarget.Contains('\0'))
                {
                    throw Invalid(archiveEntry, "symbolic-link target is empty or contains NUL");
                }
            }

            var entry = new ValidatedEntry(
                archiveEntry,
                normalizedName,
                segments,
                kind,
                unixMode,
                linkTarget);
            if (!byName.TryAdd(normalizedName, entry))
            {
                throw Invalid(archiveEntry, $"duplicate normalized path '{normalizedName}'");
            }
        }

        foreach (var entry in byName.Values)
        {
            for (var length = 1; length < entry.Segments.Length; length++)
            {
                var parent = string.Join('/', entry.Segments, 0, length);
                if (byName.TryGetValue(parent, out var parentEntry) && parentEntry.Kind != EntryKind.Directory)
                {
                    throw Invalid(entry.Source, $"path descends through non-directory entry '{parent}'");
                }
            }
        }

        return byName.Values.ToList();
    }

    private static (string Name, string[] Segments, bool IsDirectory) NormalizeEntryName(string rawName)
    {
        if (string.IsNullOrEmpty(rawName) || rawName.Contains('\0'))
        {
            throw new InvalidDataException("ZIP contains an empty entry name or NUL.");
        }

        var name = rawName.Replace('\\', '/');
        if (name.StartsWith('/') || HasDrivePrefix(name))
        {
            throw new InvalidDataException($"ZIP entry has a rooted path: '{rawName}'.");
        }

        var isDirectory = name.EndsWith('/');
        if (isDirectory)
        {
            name = name[..^1];
        }

        var segments = name.Split('/');
        if (segments.Length == 0 ||
            segments.Any(static segment =>
                segment.Length == 0 || segment == "." || segment == ".."))
        {
            throw new InvalidDataException($"ZIP entry contains an empty, '.' or '..' path segment: '{rawName}'.");
        }

        if (OperatingSystem.IsWindows() && segments.Any(static segment =>
                segment.Contains(':') || segment.EndsWith('.') || segment.EndsWith(' ')))
        {
            throw new InvalidDataException(
                $"ZIP entry uses a Windows-ambiguous colon, trailing dot, or trailing space: '{rawName}'.");
        }

        if (OperatingSystem.IsWindows() && segments.Any(IsWindowsReservedDeviceName))
        {
            throw new InvalidDataException(
                $"ZIP entry uses a Windows reserved DOS device name: '{rawName}'.");
        }

        return (string.Join('/', segments), segments, isDirectory);
    }

    private static void ValidateExistingPaths(string root, IReadOnlyCollection<ValidatedEntry> entries)
    {
        foreach (var entry in entries)
        {
            var current = root;
            for (var index = 0; index < entry.Segments.Length; index++)
            {
                current = Path.Combine(current, entry.Segments[index]);
                if (!TryGetAttributes(current, out var attributes))
                {
                    break;
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw Invalid(entry.Source, $"path crosses existing reparse point '{current}'");
                }

                var isFinal = index == entry.Segments.Length - 1;
                if (!isFinal && (attributes & FileAttributes.Directory) == 0)
                {
                    throw Invalid(entry.Source, $"path crosses existing file '{current}'");
                }

                if (isFinal && entry.Kind == EntryKind.Directory !=
                    ((attributes & FileAttributes.Directory) != 0))
                {
                    throw Invalid(entry.Source, $"entry type conflicts with existing path '{current}'");
                }

                if (isFinal && entry.Kind == EntryKind.SymbolicLink)
                {
                    throw Invalid(entry.Source, $"symbolic-link destination already exists: '{current}'");
                }
            }
        }
    }

    private static void EnsureDirectories(string root, IReadOnlyList<string> segments)
    {
        var current = root;
        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);
            if (TryGetAttributes(current, out var attributes))
            {
                if ((attributes & FileAttributes.ReparsePoint) != 0 ||
                    (attributes & FileAttributes.Directory) == 0)
                {
                    throw new InvalidDataException($"Archive path cannot use non-directory '{current}'.");
                }
            }
            else
            {
                Directory.CreateDirectory(current);
                RejectReparsePoint(current, "An archive-created directory");
            }
        }
    }

    private static void ExtractRegularFile(string root, ValidatedEntry entry)
    {
        EnsureDirectories(root, entry.Segments[..^1]);
        var destination = Combine(root, entry.Segments);
        if (TryGetAttributes(destination, out var attributes))
        {
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                throw Invalid(entry.Source, $"regular file conflicts with '{destination}'");
            }

            File.Delete(destination);
        }

        using (var source = entry.Source.Open())
        using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            source.CopyTo(output);
        }

        if (!OperatingSystem.IsWindows())
        {
            ApplyUnixMode(destination, entry.UnixMode);
        }
    }

    private static void CreateSymbolicLink(
        string root,
        ValidatedEntry entry,
        IReadOnlyCollection<ValidatedEntry> entries)
    {
        EnsureDirectories(root, entry.Segments[..^1]);
        var destination = Combine(root, entry.Segments);
        if (TryGetAttributes(destination, out _))
        {
            throw Invalid(entry.Source, $"symbolic-link destination already exists: '{destination}'");
        }

        var targetSegments = ResolveLinkTarget(entry);
        RejectArchiveLinkPivot(entry, targetSegments, entries);
        RejectExistingLinkPivot(root, targetSegments, entry.Source);

        try
        {
            if (OperatingSystem.IsWindows())
            {
                var resolvedTarget = Combine(root, targetSegments);
                if (Directory.Exists(resolvedTarget))
                {
                    Directory.CreateSymbolicLink(destination, entry.LinkTarget!);
                }
                else if (File.Exists(resolvedTarget))
                {
                    File.CreateSymbolicLink(destination, entry.LinkTarget!);
                }
                else
                {
                    throw Invalid(
                        entry.Source,
                        "Windows cannot safely infer the type of a dangling symbolic-link target");
                }
            }
            else
            {
                File.CreateSymbolicLink(destination, entry.LinkTarget!);
            }
        }
        catch (Exception exception) when (
            OperatingSystem.IsWindows() &&
            exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            throw Invalid(
                entry.Source,
                "symbolic-link creation is unavailable on this Windows agent; enable Developer Mode or run with the required privilege",
                exception);
        }
    }

    private static string[] ResolveLinkTarget(ValidatedEntry entry)
    {
        var target = entry.LinkTarget!;
        var portableTarget = target.Replace('\\', '/');
        if (portableTarget.StartsWith('/') || HasDrivePrefix(portableTarget))
        {
            throw Invalid(entry.Source, "symbolic-link target is rooted");
        }

        var resolved = new List<string>(entry.Segments[..^1]);
        foreach (var segment in portableTarget.Split('/'))
        {
            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (resolved.Count == 0)
                {
                    throw Invalid(entry.Source, "symbolic-link target escapes the extraction root");
                }

                resolved.RemoveAt(resolved.Count - 1);
            }
            else
            {
                if (OperatingSystem.IsWindows() && IsWindowsReservedDeviceName(segment))
                {
                    throw Invalid(
                        entry.Source,
                        $"symbolic-link target uses Windows reserved DOS device name segment '{segment}'");
                }

                resolved.Add(segment);
            }
        }

        if (resolved.Count == 0)
        {
            throw Invalid(entry.Source, "symbolic-link target resolves to the extraction root");
        }

        return resolved.ToArray();
    }

    private static void RejectArchiveLinkPivot(
        ValidatedEntry link,
        IReadOnlyList<string> targetSegments,
        IReadOnlyCollection<ValidatedEntry> entries)
    {
        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        for (var length = 1; length <= targetSegments.Count; length++)
        {
            var prefix = string.Join('/', targetSegments.Take(length));
            if (entries.Any(entry =>
                    entry.Kind == EntryKind.SymbolicLink &&
                    !ReferenceEquals(entry, link) &&
                    comparer.Equals(entry.NormalizedName, prefix)))
            {
                throw Invalid(link.Source, $"symbolic-link target crosses archive link '{prefix}'");
            }
        }
    }

    private static void RejectExistingLinkPivot(
        string root,
        IReadOnlyList<string> targetSegments,
        ZipArchiveEntry source)
    {
        var current = root;
        foreach (var segment in targetSegments)
        {
            current = Path.Combine(current, segment);
            if (!TryGetAttributes(current, out var attributes))
            {
                break;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw Invalid(source, $"symbolic-link target crosses existing reparse point '{current}'");
            }
        }
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static void ApplyUnixMode(string path, int unixMode)
    {
        var permissions = unixMode & 0x0FFF;
        if (permissions != 0)
        {
            File.SetUnixFileMode(path, (UnixFileMode)permissions);
        }
    }

    private static string Combine(string root, IReadOnlyList<string> segments) =>
        segments.Aggregate(root, Path.Combine);

    private static bool HasDrivePrefix(string value) =>
        value.Length >= 2 && char.IsAsciiLetter(value[0]) && value[1] == ':';

    private static bool IsWindowsReservedDeviceName(string segment)
    {
        var dotIndex = segment.IndexOf('.');
        var baseName = segment.AsSpan(0, dotIndex >= 0 ? dotIndex : segment.Length);
        if (baseName.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            baseName.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            baseName.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            baseName.Equals("NUL", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return baseName.Length == 4 &&
               baseName[3] is >= '1' and <= '9' &&
               (baseName[..3].Equals("COM", StringComparison.OrdinalIgnoreCase) ||
                baseName[..3].Equals("LPT", StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryGetAttributes(string path, out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            attributes = default;
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            attributes = default;
            return false;
        }
    }

    private static void RejectReparsePoint(string path, string description)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"{description} is a symbolic link or reparse point: '{path}'.");
        }
    }

    private static InvalidDataException Invalid(
        ZipArchiveEntry entry,
        string reason,
        Exception? inner = null) =>
        new($"Unsafe ZIP entry '{entry.FullName}': {reason}.", inner);

    private sealed record ValidatedEntry(
        ZipArchiveEntry Source,
        string NormalizedName,
        string[] Segments,
        EntryKind Kind,
        int UnixMode,
        string? LinkTarget);

    private enum EntryKind
    {
        RegularFile,
        Directory,
        SymbolicLink,
    }
}
