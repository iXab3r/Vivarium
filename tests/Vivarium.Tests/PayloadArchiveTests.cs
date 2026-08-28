using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Vivarium.Agent;
using Vivarium.Cli;
using Vivarium.Cli.Configuration;

namespace Vivarium.Tests;

[TestFixture]
public sealed class PayloadArchiveTests
{
    private string root = null!;

    [SetUp]
    public void SetUp()
    {
        root = Path.Combine(Path.GetTempPath(), "vivarium-archive-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch
        {
            // Best effort: a failed symlink test must not hide its assertion.
        }
    }

    [Test]
    public async Task Create_is_deterministic_and_round_trips_files_directories_and_modes()
    {
        var source = Path.Combine(root, "source");
        Directory.CreateDirectory(Path.Combine(source, "z-empty"));
        Directory.CreateDirectory(Path.Combine(source, "bin"));
        var script = Path.Combine(source, "bin", "run.sh");
        await File.WriteAllTextAsync(script, "#!/bin/sh\necho ok\n");
        await File.WriteAllTextAsync(Path.Combine(source, "README.txt"), "payload");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                script,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        var first = await PayloadArchive.CreateAsync(source, Path.Combine(root, "first.zip"));
        var second = await PayloadArchive.CreateAsync(source, Path.Combine(root, "second.zip"));

        Assert.Multiple(() =>
        {
            Assert.That(File.ReadAllBytes(first.Path), Is.EqualTo(File.ReadAllBytes(second.Path)));
            Assert.That(first.Sha256, Is.EqualTo(second.Sha256));
            Assert.That(first.Sha256, Is.EqualTo(
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(first.Path))).ToLowerInvariant()));
        });

        using (var zip = ZipFile.OpenRead(first.Path))
        {
            Assert.That(
                zip.Entries.Select(entry => entry.FullName),
                Is.EqualTo(zip.Entries.Select(entry => entry.FullName).Order(StringComparer.Ordinal)));
            Assert.That(zip.Entries.All(entry => entry.LastWriteTime.DateTime == new DateTime(1980, 1, 1)), Is.True);
            var scriptEntry = zip.GetEntry("bin/run.sh")!;
            Assert.That(((scriptEntry.ExternalAttributes >> 16) & 0xF000), Is.EqualTo(0x8000));
            if (!OperatingSystem.IsWindows())
            {
                Assert.That(((scriptEntry.ExternalAttributes >> 16) & 0x49), Is.EqualTo(0x49));
            }
        }

        var destination = Path.Combine(root, "destination");
        PayloadArchiveExtractor.Extract(first.Path, destination);
        Assert.Multiple(() =>
        {
            Assert.That(File.ReadAllText(Path.Combine(destination, "README.txt")), Is.EqualTo("payload"));
            Assert.That(File.ReadAllText(Path.Combine(destination, "bin", "run.sh")), Is.EqualTo("#!/bin/sh\necho ok\n"));
            Assert.That(Directory.Exists(Path.Combine(destination, "z-empty")), Is.True);
        });
        if (!OperatingSystem.IsWindows())
        {
            Assert.That(
                File.GetUnixFileMode(Path.Combine(destination, "bin", "run.sh")) & UnixFileMode.UserExecute,
                Is.EqualTo(UnixFileMode.UserExecute));
        }
    }

    [Test]
    public async Task Create_on_windows_promotes_only_declared_program_files_to_executable()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Ignore("This verifies the Windows permission fallback used for Unix payloads.");
        }

        var source = Path.Combine(root, "source");
        Directory.CreateDirectory(Path.Combine(source, "bin"));
        var program = Path.Combine(source, "bin", "runner");
        await File.WriteAllTextAsync(program, "program");
        await File.WriteAllTextAsync(Path.Combine(source, "bin", "data.txt"), "data");

        var result = await PayloadArchive.CreateAsync(
            source,
            Path.Combine(root, "payload.zip"),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { program });

        using var zip = ZipFile.OpenRead(result.Path);
        Assert.Multiple(() =>
        {
            Assert.That(Mode(zip, "bin/runner") & 0x1FF, Is.EqualTo(0x1ED)); // 0755
            Assert.That(Mode(zip, "bin/data.txt") & 0x1FF, Is.EqualTo(0x1A4)); // 0644
        });
    }

    [Test]
    public async Task Temporary_archives_union_unix_step_programs_for_a_shared_payload_deterministically()
    {
        var source = Path.Combine(root, "source");
        Directory.CreateDirectory(Path.Combine(source, "tools"));
        var linuxProgram = Path.Combine(source, "linux-runner");
        var macProgram = Path.Combine(source, "tools", "mac-runner");
        var windowsProgram = Path.Combine(source, "windows-runner.exe");
        var portableProgram = Path.Combine(source, "portable-runner");
        var dataFile = Path.Combine(source, "data.txt");
        foreach (var file in new[] { linuxProgram, macProgram, windowsProgram, portableProgram, dataFile })
        {
            await File.WriteAllTextAsync(file, Path.GetFileName(file));
        }

        if (!OperatingSystem.IsWindows())
        {
            var regularMode = UnixFileMode.UserRead | UnixFileMode.UserWrite |
                              UnixFileMode.GroupRead | UnixFileMode.OtherRead;
            File.SetUnixFileMode(linuxProgram, regularMode | UnixFileMode.UserExecute |
                UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            File.SetUnixFileMode(macProgram, regularMode | UnixFileMode.UserExecute |
                UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            File.SetUnixFileMode(windowsProgram, regularMode);
            File.SetUnixFileMode(portableProgram, regularMode | UnixFileMode.UserExecute |
                UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            File.SetUnixFileMode(dataFile, regularMode);
        }

        var cells = new[]
        {
            Cell(source, "linux", "linux-x64", new ResolvedVivariumStep(
                "linux-runner", [], new Dictionary<string, string>(), ".", null, VivariumStepPolicy.Default)),
            Cell(source, "mac", "osx-arm64", new ResolvedVivariumStep(
                "mac-runner", [], new Dictionary<string, string>(), "tools", null, VivariumStepPolicy.Default)),
            Cell(source, "linux-system", "linux-x64", new ResolvedVivariumStep(
                "/bin/sh", [], new Dictionary<string, string>(), ".", null, VivariumStepPolicy.Default)),
            Cell(source, "windows", "win-x64", new ResolvedVivariumStep(
                "windows-runner.exe", [], new Dictionary<string, string>(), ".", null, VivariumStepPolicy.Default)),
            Cell(source, "portable", null, new ResolvedVivariumStep(
                "portable-runner", [], new Dictionary<string, string>(), ".", null, VivariumStepPolicy.Default)),
        };

        var factory = new TemporaryPayloadArchiveFactory();
        await using var first = await factory.CreateAsync(cells, CancellationToken.None);
        await using var second = await factory.CreateAsync(cells.Reverse(), CancellationToken.None);
        var firstArchive = first.Archives[source];
        var secondArchive = second.Archives[source];

        Assert.Multiple(() =>
        {
            Assert.That(first.Archives, Has.Count.EqualTo(1));
            Assert.That(second.Archives, Has.Count.EqualTo(1));
            Assert.That(firstArchive.Sha256, Is.EqualTo(secondArchive.Sha256));
            Assert.That(File.ReadAllBytes(firstArchive.Path), Is.EqualTo(File.ReadAllBytes(secondArchive.Path)));
        });

        using var zip = ZipFile.OpenRead(firstArchive.Path);
        Assert.Multiple(() =>
        {
            Assert.That(Mode(zip, "linux-runner") & 0x49, Is.EqualTo(0x49));
            Assert.That(Mode(zip, "tools/mac-runner") & 0x49, Is.EqualTo(0x49));
            Assert.That(Mode(zip, "portable-runner") & 0x49, Is.EqualTo(0x49));
            Assert.That(Mode(zip, "windows-runner.exe") & 0x49, Is.Zero);
            Assert.That(Mode(zip, "data.txt") & 0x49, Is.Zero);
        });
    }

    [Test]
    public async Task Create_represents_symlink_and_extracts_it_where_supported()
    {
        var source = Path.Combine(root, "source");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "target.txt"), "target");
        var sourceLink = Path.Combine(source, "link.txt");
        try
        {
            File.CreateSymbolicLink(sourceLink, "target.txt");
        }
        catch (Exception exception) when (
            OperatingSystem.IsWindows() &&
            exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Ignore("Windows symbolic-link creation is unavailable on this host.");
        }

        var result = await PayloadArchive.CreateAsync(source, Path.Combine(root, "payload.zip"));
        using (var zip = ZipFile.OpenRead(result.Path))
        {
            var link = zip.GetEntry("link.txt")!;
            Assert.Multiple(() =>
            {
                Assert.That(((link.ExternalAttributes >> 16) & 0xF000), Is.EqualTo(0xA000));
                using var reader = new StreamReader(link.Open(), Encoding.UTF8);
                Assert.That(reader.ReadToEnd(), Is.EqualTo("target.txt"));
            });
        }

        var destination = Path.Combine(root, "destination");
        try
        {
            PayloadArchiveExtractor.Extract(result.Path, destination);
        }
        catch (InvalidDataException exception) when (OperatingSystem.IsWindows())
        {
            Assert.That(exception.Message, Does.Contain("symbolic-link creation is unavailable"));
            return;
        }

        Assert.Multiple(() =>
        {
            Assert.That(new FileInfo(Path.Combine(destination, "link.txt")).LinkTarget, Is.EqualTo("target.txt"));
            Assert.That(File.ReadAllText(Path.Combine(destination, "link.txt")), Is.EqualTo("target"));
        });
    }

    [TestCase("/absolute.txt")]
    [TestCase("C:/absolute.txt")]
    [TestCase("\\\\server\\share.txt")]
    [TestCase("../escape.txt")]
    [TestCase("..\\escape.txt")]
    public void Extract_rejects_rooted_and_traversal_entries(string entryName)
    {
        var zip = CreateZip(archive => AddFile(archive, entryName, "owned"));
        var destination = Path.Combine(root, "destination");

        Assert.That(
            () => PayloadArchiveExtractor.Extract(zip, destination),
            Throws.TypeOf<InvalidDataException>());
        Assert.That(File.Exists(Path.Combine(root, "escape.txt")), Is.False);
    }

    [Test]
    public void Extract_rejects_duplicate_normalized_names()
    {
        var zip = CreateZip(archive =>
        {
            AddFile(archive, "folder/file.txt", "one");
            AddFile(archive, "folder\\file.txt", "two");
        });

        Assert.That(
            () => PayloadArchiveExtractor.Extract(zip, Path.Combine(root, "destination")),
            Throws.TypeOf<InvalidDataException>().With.Message.Contains("duplicate"));
    }

    [TestCase("file.txt:stream")]
    [TestCase("file.")]
    [TestCase("file ")]
    public void Extract_rejects_Windows_ambiguous_names(string entryName)
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Ignore("These aliases are specific to Windows filesystem normalization.");
        }

        var zip = CreateZip(archive => AddFile(archive, entryName, "owned"));
        Assert.That(
            () => PayloadArchiveExtractor.Extract(zip, Path.Combine(root, "destination")),
            Throws.TypeOf<InvalidDataException>().With.Message.Contains("Windows-ambiguous"));
    }

    [TestCase("NUL")]
    [TestCase("NUL.txt")]
    [TestCase("folder/CON/file.txt")]
    [TestCase("folder/COM1.log")]
    public void Extract_rejects_Windows_reserved_DOS_device_names(string entryName)
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Ignore("DOS device names are specific to Windows filesystem normalization.");
        }

        var zip = CreateZip(archive => AddFile(archive, entryName, "owned"));
        Assert.That(
            () => PayloadArchiveExtractor.Extract(zip, Path.Combine(root, "destination")),
            Throws.TypeOf<InvalidDataException>().With.Message.Contains("reserved DOS device name"));
    }

    [Test]
    public void Extract_rejects_Windows_reserved_DOS_device_name_in_symlink_target()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Ignore("DOS device names are specific to Windows filesystem normalization.");
        }

        var zip = CreateZip(archive => AddLink(archive, "link", "folder/NUL.txt"));
        Assert.That(
            () => PayloadArchiveExtractor.Extract(zip, Path.Combine(root, "destination")),
            Throws.TypeOf<InvalidDataException>().With.Message.Contains("reserved DOS device name"));
    }

    [Test]
    public void Extract_rejects_archive_created_symlink_pivot()
    {
        var zip = CreateZip(archive =>
        {
            AddFile(archive, "inside/file.txt", "safe");
            AddLink(archive, "pivot", "inside");
            AddFile(archive, "pivot/owned.txt", "owned");
        });

        Assert.That(
            () => PayloadArchiveExtractor.Extract(zip, Path.Combine(root, "destination")),
            Throws.TypeOf<InvalidDataException>().With.Message.Contains("non-directory"));
    }

    [Test]
    public void Extract_rejects_symlink_target_escaping_root()
    {
        var zip = CreateZip(archive => AddLink(archive, "nested/link", "../../outside"));

        Assert.That(
            () => PayloadArchiveExtractor.Extract(zip, Path.Combine(root, "destination")),
            Throws.TypeOf<InvalidDataException>().With.Message.Contains("escapes"));
    }

    [Test]
    public void Extract_rejects_existing_filesystem_symlink_pivot()
    {
        var destination = Path.Combine(root, "destination");
        var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(destination);
        Directory.CreateDirectory(outside);
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(destination, "pivot"), outside);
        }
        catch (Exception exception) when (
            OperatingSystem.IsWindows() &&
            exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Ignore("Windows symbolic-link creation is unavailable on this host.");
        }

        var zip = CreateZip(archive => AddFile(archive, "pivot/owned.txt", "owned"));
        Assert.That(
            () => PayloadArchiveExtractor.Extract(zip, destination),
            Throws.TypeOf<InvalidDataException>().With.Message.Contains("reparse point"));
        Assert.That(File.Exists(Path.Combine(outside, "owned.txt")), Is.False);
    }

    private string CreateZip(Action<ZipArchive> populate)
    {
        var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        populate(archive);
        return path;
    }

    private static ResolvedVivariumCell Cell(
        string source,
        string name,
        string? rid,
        params ResolvedVivariumStep[] steps) => new(
            name,
            "name == agent",
            rid,
            new ResolvedPayload(source, "source"),
            steps,
            [],
            null,
            VivariumOnFail.None);

    private static int Mode(ZipArchive archive, string entryName) =>
        (archive.GetEntry(entryName)!.ExternalAttributes >> 16) & 0xFFFF;

    private static void AddFile(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        entry.ExternalAttributes = (0x8000 | 0x1A4) << 16;
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static void AddLink(ZipArchive archive, string name, string target)
    {
        var entry = archive.CreateEntry(name);
        entry.ExternalAttributes = (0xA000 | 0x1FF) << 16;
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(target);
    }
}
