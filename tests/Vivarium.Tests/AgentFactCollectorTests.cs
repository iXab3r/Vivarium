using System.Runtime.InteropServices;
using Vivarium.Agent.Facts;

namespace Vivarium.Tests;

[TestFixture]
public class AgentFactCollectorTests
{
    private static readonly DateTimeOffset ObservedAt =
        new(2026, 8, 29, 10, 15, 30, TimeSpan.Zero);

    private static readonly AgentPackageIdentity Package = new(
        "2.3.4",
        "2.3.4+linux-x64",
        new string('a', 64));

    [Test]
    public async Task Current_linux_host_reports_native_static_facts()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Ignore("Native Linux evidence runs on the Linux CI cells.");
        }

        var snapshot = await PlatformFactsCollector
            .CreateDefault(new FixedTimeProvider(ObservedAt))
            .CollectAsync(Package);

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Family, Is.EqualTo("linux"));
            Assert.That(snapshot.ProductName, Is.Not.Empty);
            Assert.That(snapshot.ProductVersion, Is.Not.Empty);
            Assert.That(snapshot.KernelVersion, Is.Not.Empty);
            Assert.That(snapshot.Hostname, Is.Not.Empty);
            Assert.That(snapshot.ObservedAt, Is.EqualTo(ObservedAt));
            Assert.That(
                snapshot.Capabilities,
                Does.Contain(new PlatformCapabilitySupport("agent-explorer.host-facts.v1", 1)));
        });
    }

    [Test]
    public async Task Current_macos_host_reports_native_static_facts()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Ignore("Native macOS evidence runs on the macOS CI cells.");
        }

        var snapshot = await PlatformFactsCollector
            .CreateDefault(new FixedTimeProvider(ObservedAt))
            .CollectAsync(Package);

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Family, Is.EqualTo("macos"));
            Assert.That(snapshot.ProductName, Is.EqualTo("macOS"));
            Assert.That(snapshot.ProductVersion, Is.Not.Empty);
            Assert.That(snapshot.ProductBuild, Is.Not.Empty);
            Assert.That(snapshot.KernelVersion, Is.Not.Empty);
            Assert.That(snapshot.Outcome, Is.EqualTo(PlatformFactCollectionOutcome.Succeeded));
            Assert.That(snapshot.Complete, Is.True);
        });
    }

    [Test]
    public async Task Linux_fixture_uses_distribution_identity_and_keeps_kernel_separate()
    {
        var source = new FakePlatformFactSource(PlatformFamily.Linux)
        {
            OsArchitecture = Architecture.Arm64,
            ProcessArchitecture = Architecture.X64,
            Hostname = "linux-fixture",
        };
        source.Files["/etc/os-release"] = PlatformFactReadResult.Available(
            "ID=ubuntu\nVERSION_ID=\"24.04\"\nPRETTY_NAME=\"Ubuntu 24.04 LTS\"\nVARIANT_ID=server\n");
        source.Commands[CommandKey("/usr/bin/uname", "-r")] =
            PlatformFactReadResult.Available("6.8.0-41-generic\n");

        var snapshot = await PlatformFactsCollector
            .Create(source, new FixedTimeProvider(ObservedAt))
            .CollectAsync(Package);

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Outcome, Is.EqualTo(PlatformFactCollectionOutcome.Succeeded));
            Assert.That(snapshot.Complete, Is.True);
            Assert.That(snapshot.ProductName, Is.EqualTo("Ubuntu 24.04 LTS"));
            Assert.That(snapshot.ProductVersion, Is.EqualTo("24.04"));
            Assert.That(snapshot.ProductBuild, Is.Null);
            Assert.That(snapshot.KernelVersion, Is.EqualTo("6.8.0-41-generic"));
            Assert.That(snapshot.OsArchitecture, Is.EqualTo("arm64"));
            Assert.That(snapshot.ProcessArchitecture, Is.EqualTo("x64"));
            Assert.That(snapshot.Values["system.os.linux.distribution_id"], Is.EqualTo("ubuntu"));
            Assert.That(snapshot.Values["system.process.emulated"], Is.EqualTo("true"));
            Assert.That(snapshot.Values.Keys, Is.EqualTo(snapshot.Values.Keys.Order(StringComparer.Ordinal)));
        });
    }

    [Test]
    public async Task Windows_fixture_reports_full_build_and_ubr()
    {
        var source = WindowsSource();
        source.Registry["ProductName"] = PlatformFactReadResult.Available("Windows 11 Pro");
        source.Registry["EditionID"] = PlatformFactReadResult.Available("Professional");
        source.Registry["DisplayVersion"] = PlatformFactReadResult.Available("23H2");
        source.Registry["InstallationType"] = PlatformFactReadResult.Available("Client");
        source.Registry["CurrentMajorVersionNumber"] = PlatformFactReadResult.Available("10");
        source.Registry["CurrentMinorVersionNumber"] = PlatformFactReadResult.Available("0");
        source.Registry["CurrentBuildNumber"] = PlatformFactReadResult.Available("22631");
        source.Registry["UBR"] = PlatformFactReadResult.Available("4037");

        var snapshot = await PlatformFactsCollector
            .Create(source, new FixedTimeProvider(ObservedAt))
            .CollectAsync(Package);

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Family, Is.EqualTo("windows"));
            Assert.That(snapshot.ProductName, Is.EqualTo("Windows 11 Pro"));
            Assert.That(snapshot.ProductVersion, Is.EqualTo("10.0"));
            Assert.That(snapshot.ProductBuild, Is.EqualTo("22631.4037"));
            Assert.That(snapshot.KernelVersion, Is.EqualTo("10.0.22631.4037"));
            Assert.That(snapshot.OsArchitecture, Is.EqualTo("x64"));
            Assert.That(snapshot.Outcome, Is.EqualTo(PlatformFactCollectionOutcome.Succeeded));
            Assert.That(snapshot.Complete, Is.True);
            Assert.That(snapshot.Values["system.os.windows.edition"], Is.EqualTo("Professional"));
            Assert.That(snapshot.AgentVersion, Is.EqualTo("2.3.4"));
            Assert.That(snapshot.PackageVersion, Is.EqualTo("2.3.4+linux-x64"));
            Assert.That(snapshot.PackageDigestSha256, Is.EqualTo(new string('a', 64)));
        });
    }

    [Test]
    public async Task Macos_fixture_reports_product_build_and_native_architecture()
    {
        var source = new FakePlatformFactSource(PlatformFamily.MacOS)
        {
            OsArchitecture = Architecture.Arm64,
            ProcessArchitecture = Architecture.Arm64,
            OperatingSystemVersion = new Version(15, 6),
            Hostname = "mac-fixture",
        };
        source.Commands[CommandKey("/usr/bin/sw_vers", "-productName")] =
            PlatformFactReadResult.Available("macOS");
        source.Commands[CommandKey("/usr/bin/sw_vers", "-productVersion")] =
            PlatformFactReadResult.Available("15.6.1");
        source.Commands[CommandKey("/usr/bin/sw_vers", "-buildVersion")] =
            PlatformFactReadResult.Available("24G90");
        source.Commands[CommandKey("/usr/bin/uname", "-r")] =
            PlatformFactReadResult.Available("24.6.0");

        var snapshot = await PlatformFactsCollector
            .Create(source, new FixedTimeProvider(ObservedAt))
            .CollectAsync(Package);

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Family, Is.EqualTo("macos"));
            Assert.That(snapshot.ProductName, Is.EqualTo("macOS"));
            Assert.That(snapshot.ProductVersion, Is.EqualTo("15.6.1"));
            Assert.That(snapshot.ProductBuild, Is.EqualTo("24G90"));
            Assert.That(snapshot.KernelVersion, Is.EqualTo("24.6.0"));
            Assert.That(snapshot.OsArchitecture, Is.EqualTo("arm64"));
            Assert.That(snapshot.ProcessArchitecture, Is.EqualTo("arm64"));
            Assert.That(snapshot.ObservedAt, Is.EqualTo(ObservedAt));
            Assert.That(snapshot.Issues, Is.Empty);
        });
    }

    [Test]
    public async Task Partial_collection_retains_capability_and_explicit_issue()
    {
        var source = WindowsSource();
        source.Registry["ProductName"] = PlatformFactReadResult.Available("Windows 10 Enterprise");
        source.Registry["CurrentMajorVersionNumber"] = PlatformFactReadResult.Available("10");
        source.Registry["CurrentMinorVersionNumber"] = PlatformFactReadResult.Available("0");
        source.Registry["CurrentBuildNumber"] = PlatformFactReadResult.Available("19045");
        source.Registry["UBR"] = PlatformFactReadResult.Unavailable(
            PlatformFactIssueCodes.AccessDenied,
            "registry:UBR",
            "5");

        var snapshot = await PlatformFactsCollector
            .Create(source, new FixedTimeProvider(ObservedAt))
            .CollectAsync(Package);

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.ProductBuild, Is.EqualTo("19045"));
            Assert.That(snapshot.Outcome, Is.EqualTo(PlatformFactCollectionOutcome.Partial));
            Assert.That(snapshot.Complete, Is.False);
            Assert.That(
                snapshot.Issues,
                Does.Contain(new PlatformFactIssue(
                    PlatformFactIssueCodes.AccessDenied,
                    "system.os.build",
                    "5")));
            Assert.That(
                snapshot.Capabilities,
                Is.EqualTo(new[]
                {
                    new PlatformCapabilitySupport("agent-explorer.host-facts.v1", 1),
                }));
        });
    }

    [Test]
    public async Task All_native_identity_reads_denied_reports_permission_denied()
    {
        var source = new FakePlatformFactSource(PlatformFamily.MacOS)
        {
            OsArchitecture = Architecture.Arm64,
            ProcessArchitecture = Architecture.Arm64,
            Hostname = "restricted-mac",
        };
        foreach (var key in new[]
                 {
                     CommandKey("/usr/bin/sw_vers", "-productName"),
                     CommandKey("/usr/bin/sw_vers", "-productVersion"),
                     CommandKey("/usr/bin/sw_vers", "-buildVersion"),
                     CommandKey("/usr/bin/uname", "-r"),
                 })
        {
            source.Commands[key] = PlatformFactReadResult.Unavailable(
                PlatformFactIssueCodes.AccessDenied,
                key,
                "13");
        }

        var snapshot = await PlatformFactsCollector
            .Create(source, new FixedTimeProvider(ObservedAt))
            .CollectAsync(Package);

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Outcome, Is.EqualTo(PlatformFactCollectionOutcome.PermissionDenied));
            Assert.That(snapshot.Complete, Is.False);
            Assert.That(snapshot.Issues, Has.Count.EqualTo(4));
            Assert.That(snapshot.Issues, Has.All.Property("Code").EqualTo(PlatformFactIssueCodes.AccessDenied));
            Assert.That(snapshot.Capabilities, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public async Task Primary_snapshot_fields_are_bounded_before_return()
    {
        var oversized = new string('x', 2048);
        var source = WindowsSource() with { Hostname = oversized };
        source.Registry["ProductName"] = PlatformFactReadResult.Available(oversized);
        source.Registry["CurrentMajorVersionNumber"] = PlatformFactReadResult.Available(oversized);
        source.Registry["CurrentMinorVersionNumber"] = PlatformFactReadResult.Available(oversized);
        source.Registry["CurrentBuildNumber"] = PlatformFactReadResult.Available(oversized);
        source.Registry["UBR"] = PlatformFactReadResult.Available(oversized);

        var snapshot = await PlatformFactsCollector
            .Create(source, new FixedTimeProvider(ObservedAt))
            .CollectAsync(new AgentPackageIdentity(oversized, oversized));

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.ProductName, Has.Length.LessThanOrEqualTo(256));
            Assert.That(snapshot.ProductVersion, Has.Length.LessThanOrEqualTo(256));
            Assert.That(snapshot.ProductBuild, Has.Length.LessThanOrEqualTo(256));
            Assert.That(snapshot.Hostname, Has.Length.LessThanOrEqualTo(256));
            Assert.That(snapshot.AgentVersion, Has.Length.LessThanOrEqualTo(128));
            Assert.That(snapshot.PackageVersion, Has.Length.LessThanOrEqualTo(128));
            Assert.That(snapshot.Outcome, Is.EqualTo(PlatformFactCollectionOutcome.Partial));
            Assert.That(
                snapshot.Issues,
                Has.Some.Property("Code").EqualTo(PlatformFactIssueCodes.ResourceExhausted));
        });
    }

    [Test]
    public void Package_digest_must_be_canonical_lowercase_sha256()
    {
        Assert.That(
            () => new AgentPackageIdentity("1.0.0", "1.0.0", new string('A', 64)),
            Throws.ArgumentException.With.Property("ParamName").EqualTo("packageDigestSha256"));
    }

    private static FakePlatformFactSource WindowsSource() => new(PlatformFamily.Windows)
    {
        OsArchitecture = Architecture.X64,
        ProcessArchitecture = Architecture.X64,
        OperatingSystemVersion = new Version(10, 0, 22631, 0),
        Hostname = "windows-fixture",
    };

    private static string CommandKey(string executable, params string[] arguments) =>
        string.Join('\u001f', [executable, .. arguments]);

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed record FakePlatformFactSource(PlatformFamily Family) : IPlatformFactSource
    {
        public Dictionary<string, PlatformFactReadResult> Files { get; } =
            new(StringComparer.Ordinal);

        public Dictionary<string, PlatformFactReadResult> Registry { get; } =
            new(StringComparer.Ordinal);

        public Dictionary<string, PlatformFactReadResult> Commands { get; } =
            new(StringComparer.Ordinal);

        public Architecture OsArchitecture { get; init; } = Architecture.X64;

        public Architecture ProcessArchitecture { get; init; } = Architecture.X64;

        public Version OperatingSystemVersion { get; init; } = new(1, 0);

        public string Hostname { get; init; } = "fixture";

        public ValueTask<PlatformFactReadResult> ReadTextFileAsync(
            string path,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Files.TryGetValue(path, out var result)
                ? result
                : PlatformFactReadResult.Unavailable(PlatformFactIssueCodes.NotFound, path));
        }

        public ValueTask<PlatformFactReadResult> ReadWindowsRegistryValueAsync(
            string keyPath,
            string valueName,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Registry.TryGetValue(valueName, out var result)
                ? result
                : PlatformFactReadResult.Unavailable(
                    PlatformFactIssueCodes.NotFound,
                    $"registry:{valueName}"));
        }

        public ValueTask<PlatformFactReadResult> RunCommandAsync(
            string executable,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = string.Join('\u001f', new[] { executable }.Concat(arguments));
            return ValueTask.FromResult(Commands.TryGetValue(key, out var result)
                ? result
                : PlatformFactReadResult.Unavailable(
                    PlatformFactIssueCodes.NotFound,
                    executable));
        }
    }
}
