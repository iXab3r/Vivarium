using System.Runtime.InteropServices;

namespace Vivarium.Agent.Facts;

public static class PlatformFactsCollector
{
    public const string HostFactsCapabilityId = "agent-explorer.host-facts.v1";
    public const int HostFactsContractMajor = 1;
    public const string CollectorVersion = "1";

    public static IPlatformFactsCollector CreateDefault(TimeProvider? timeProvider = null) =>
        Create(new SystemPlatformFactSource(), timeProvider);

    public static IPlatformFactsCollector Create(
        IPlatformFactSource source,
        TimeProvider? timeProvider = null) =>
        source.Family switch
        {
            PlatformFamily.Windows => new WindowsPlatformFactsCollector(source, timeProvider),
            PlatformFamily.Linux => new LinuxPlatformFactsCollector(source, timeProvider),
            PlatformFamily.MacOS => new MacOsPlatformFactsCollector(source, timeProvider),
            _ => throw new PlatformNotSupportedException(
                "Vivarium static host facts support Windows, Linux, and macOS."),
        };

    internal static readonly IReadOnlyList<PlatformCapabilitySupport> HostFactsCapabilities =
        Array.AsReadOnly<PlatformCapabilitySupport>(
            [new(HostFactsCapabilityId, HostFactsContractMajor)]);

    internal static string FormatArchitecture(Architecture architecture) => architecture switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "arm64",
        Architecture.X86 => "x86",
        Architecture.Arm => "arm",
        _ => architecture.ToString().ToLowerInvariant(),
    };
}

internal abstract class PlatformFactsCollectorBase : IPlatformFactsCollector
{
    protected PlatformFactsCollectorBase(IPlatformFactSource source, TimeProvider? timeProvider)
    {
        Source = source;
        TimeProvider = timeProvider ?? TimeProvider.System;
    }

    protected IPlatformFactSource Source { get; }

    protected TimeProvider TimeProvider { get; }

    public IReadOnlyList<PlatformCapabilitySupport> SupportedCapabilities =>
        PlatformFactsCollector.HostFactsCapabilities;

    public abstract ValueTask<PlatformFactSnapshot> CollectAsync(
        AgentPackageIdentity package,
        CancellationToken cancellationToken = default);

    protected PlatformFactSnapshotBuilder CreateBuilder(
        string family,
        out string osArchitecture,
        out string processArchitecture,
        out string hostname)
    {
        var builder = new PlatformFactSnapshotBuilder();
        osArchitecture = PlatformFactsCollector.FormatArchitecture(Source.OsArchitecture);
        processArchitecture = PlatformFactsCollector.FormatArchitecture(Source.ProcessArchitecture);
        hostname = Source.Hostname.Trim();

        if (Source.OsArchitecture is not (Architecture.X64 or Architecture.Arm64))
        {
            builder.AddIssue(new PlatformFactIssue(
                PlatformFactIssueCodes.NotSupported,
                "system.os.arch",
                Message: "The OS architecture is outside the current support matrix."));
        }

        if (string.IsNullOrWhiteSpace(hostname))
        {
            builder.AddMissingIssue("system.hostname");
        }

        builder.AddValue("system.os.family", family);
        builder.AddValue("system.os.arch", osArchitecture);
        builder.AddValue("system.process.arch", processArchitecture);
        builder.AddValue("system.hostname", hostname);
        if (!string.Equals(osArchitecture, processArchitecture, StringComparison.Ordinal))
        {
            builder.AddValue("system.process.emulated", "true");
        }

        return builder;
    }

    protected static string? AddReadValue(
        PlatformFactSnapshotBuilder builder,
        string field,
        PlatformFactReadResult read,
        bool required,
        bool includeFailure = true)
    {
        var value = string.IsNullOrWhiteSpace(read.Value) ? null : read.Value.Trim();
        if (value is not null)
        {
            return value;
        }

        if (includeFailure && read.Issue is not null)
        {
            builder.AddIssue(read.Issue with { Field = field });
        }
        else if (required)
        {
            builder.AddMissingIssue(field);
        }

        return null;
    }
}
