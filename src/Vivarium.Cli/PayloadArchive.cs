using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Vivarium.Cli;

/// <summary>Creates the portable, content-addressed payload archive described by D3.</summary>
public static class PayloadArchive
{
    private static readonly DateTimeOffset FixedTimestamp =
        new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const int RegularFile = 0x8000;
    private const int DirectoryFile = 0x4000;
    private const int SymbolicLink = 0xA000;
    private const int DefaultFilePermissions = 0x1A4; // 0644
    private const int DefaultDirectoryPermissions = 0x1ED; // 0755
    private const int DefaultLinkPermissions = 0x1FF; // 0777

    public static async Task<PayloadArchiveInfo> CreateAsync(
        string payloadRoot,
        string archivePath,
        CancellationToken cancellationToken = default)
    {
        return await CreateAsync(
            payloadRoot,
            archivePath,
            new HashSet<string>(PathComparer),
            cancellationToken);
    }

    internal static async Task<PayloadArchiveInfo> CreateAsync(
        string payloadRoot,
        string archivePath,
        IReadOnlySet<string> executableFiles,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentNullException.ThrowIfNull(executableFiles);

        var root = Path.GetFullPath(payloadRoot);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Payload root does not exist: '{root}'.");
        }

        if (IsReparsePoint(root))
        {
            throw new InvalidOperationException("The payload root cannot itself be a symbolic link or reparse point.");
        }

        var output = Path.GetFullPath(archivePath);
        if (IsUnder(root, output))
        {
            throw new InvalidOperationException("The payload archive must be written outside the payload root.");
        }

        var nodes = new List<ArchiveNode>();
        Collect(root, root, nodes);
        nodes.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.EntryName, right.EntryName));

        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var temporary = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8);
                foreach (var node in nodes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var entryName = node.Kind == ArchiveNodeKind.Directory
                        ? node.EntryName + "/"
                        : node.EntryName;
                    var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
                    entry.LastWriteTime = FixedTimestamp;
                    entry.ExternalAttributes = CreateExternalAttributes(
                        node,
                        node.Kind == ArchiveNodeKind.RegularFile && executableFiles.Contains(node.FullPath));

                    if (node.Kind == ArchiveNodeKind.Directory)
                    {
                        continue;
                    }

                    await using var destination = entry.Open();
                    if (node.Kind == ArchiveNodeKind.SymbolicLink)
                    {
                        var target = Encoding.UTF8.GetBytes(node.LinkTarget!);
                        await destination.WriteAsync(target, cancellationToken);
                    }
                    else
                    {
                        await using var source = new FileStream(
                            node.FullPath,
                            FileMode.Open,
                            FileAccess.Read,
                            FileShare.Read,
                            64 * 1024,
                            FileOptions.Asynchronous | FileOptions.SequentialScan);
                        await source.CopyToAsync(destination, cancellationToken);
                    }
                }
            }

            File.Move(temporary, output, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }

        await using var completed = new FileStream(
            output,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var sha256 = Convert.ToHexString(await SHA256.HashDataAsync(completed, cancellationToken))
            .ToLowerInvariant();
        return new PayloadArchiveInfo(output, sha256, completed.Length);
    }

    private static void Collect(string root, string directory, List<ArchiveNode> nodes)
    {
        foreach (var item in new DirectoryInfo(directory).EnumerateFileSystemInfos())
        {
            var entryName = Path.GetRelativePath(root, item.FullName).Replace('\\', '/');
            var linkTarget = item.LinkTarget;
            if (linkTarget is not null)
            {
                var portableLinkTarget = linkTarget.Replace('\\', '/');
                ValidateLinkTarget(root, item.FullName, portableLinkTarget);
                nodes.Add(new ArchiveNode(item.FullName, entryName, ArchiveNodeKind.SymbolicLink, portableLinkTarget));
                continue;
            }

            if (item is DirectoryInfo)
            {
                nodes.Add(new ArchiveNode(item.FullName, entryName, ArchiveNodeKind.Directory, null));
                Collect(root, item.FullName, nodes);
            }
            else
            {
                nodes.Add(new ArchiveNode(item.FullName, entryName, ArchiveNodeKind.RegularFile, null));
            }
        }
    }

    private static void ValidateLinkTarget(string root, string linkPath, string linkTarget)
    {
        if (Path.IsPathRooted(linkTarget) || HasDrivePrefix(linkTarget) || linkTarget.StartsWith('\\'))
        {
            throw new InvalidOperationException($"Symbolic link '{linkPath}' has a rooted target.");
        }

        var portableTarget = linkTarget.Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);
        var resolved = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(linkPath)!, portableTarget));
        if (!IsUnder(root, resolved))
        {
            throw new InvalidOperationException($"Symbolic link '{linkPath}' points outside the payload root.");
        }
    }

    private static int CreateExternalAttributes(ArchiveNode node, bool executable)
    {
        var permissions = node.Kind switch
        {
            ArchiveNodeKind.SymbolicLink => DefaultLinkPermissions,
            ArchiveNodeKind.Directory => GetPermissions(node.FullPath, DefaultDirectoryPermissions),
            _ => GetPermissions(node.FullPath, DefaultFilePermissions, executable),
        };
        var fileType = node.Kind switch
        {
            ArchiveNodeKind.SymbolicLink => SymbolicLink,
            ArchiveNodeKind.Directory => DirectoryFile,
            _ => RegularFile,
        };
        var dosDirectoryFlag = node.Kind == ArchiveNodeKind.Directory ? 0x10 : 0;
        return ((fileType | permissions) << 16) | dosDirectoryFlag;
    }

    private static int GetPermissions(string path, int fallback, bool executable = false)
    {
        if (OperatingSystem.IsWindows())
        {
            return executable ? fallback | 0x49 : fallback; // 0111
        }

        return (int)File.GetUnixFileMode(path) & 0x0FFF;
    }

    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static bool HasDrivePrefix(string value) =>
        value.Length >= 2 && char.IsAsciiLetter(value[0]) && value[1] == ':';

    private static bool IsUnder(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        return !Path.IsPathRooted(relative) &&
               !relative.Equals("..", StringComparison.Ordinal) &&
               !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private sealed record ArchiveNode(
        string FullPath,
        string EntryName,
        ArchiveNodeKind Kind,
        string? LinkTarget);

    private enum ArchiveNodeKind
    {
        RegularFile,
        Directory,
        SymbolicLink,
    }
}

public sealed record PayloadArchiveInfo(string Path, string Sha256, long Size);
