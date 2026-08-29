using System.Collections.ObjectModel;

namespace Vivarium.Agent.Facts;

public enum PlatformFamily
{
    Unknown = 0,
    Windows,
    Linux,
    MacOS,
}

public enum PlatformFactCollectionOutcome
{
    Succeeded = 0,
    Partial,
    Degraded,
    PermissionDenied,
    TemporarilyUnavailable,
    Failed,
}

public sealed record PlatformFactIssue(
    string Code,
    string Field,
    string? NativeCode = null,
    string? Message = null);

public sealed record PlatformCapabilitySupport(string Id, int ContractMajor);

public sealed record AgentPackageIdentity
{
    public AgentPackageIdentity(
        string agentVersion,
        string packageVersion,
        string? packageDigestSha256 = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageVersion);

        if (packageDigestSha256 is not null &&
            (packageDigestSha256.Length != 64 || packageDigestSha256.Any(static c => !IsLowerHex(c))))
        {
            throw new ArgumentException(
                "The package digest must be 64 lowercase hexadecimal characters.",
                nameof(packageDigestSha256));
        }

        AgentVersion = agentVersion;
        PackageVersion = packageVersion;
        PackageDigestSha256 = packageDigestSha256;
    }

    public string AgentVersion { get; }

    public string PackageVersion { get; }

    public string? PackageDigestSha256 { get; }

    private static bool IsLowerHex(char value) =>
        value is >= '0' and <= '9' or >= 'a' and <= 'f';
}

public sealed record PlatformFactSnapshot(
    string Family,
    string? ProductName,
    string? ProductVersion,
    string? ProductBuild,
    string? KernelVersion,
    string OsArchitecture,
    string ProcessArchitecture,
    string Hostname,
    string AgentVersion,
    string PackageVersion,
    string? PackageDigestSha256,
    DateTimeOffset ObservedAt,
    string CollectorVersion,
    PlatformFactCollectionOutcome Outcome,
    bool Complete,
    IReadOnlyDictionary<string, string> Values,
    IReadOnlyList<PlatformFactIssue> Issues,
    IReadOnlyList<PlatformCapabilitySupport> Capabilities);

public interface IPlatformFactsCollector
{
    IReadOnlyList<PlatformCapabilitySupport> SupportedCapabilities { get; }

    ValueTask<PlatformFactSnapshot> CollectAsync(
        AgentPackageIdentity package,
        CancellationToken cancellationToken = default);
}

internal sealed class PlatformFactSnapshotBuilder
{
    public const int MaxIssues = 32;
    public const int MaxValues = 64;
    public const int MaxCodeLength = 64;
    public const int MaxFieldLength = 128;
    public const int MaxNativeCodeLength = 64;
    public const int MaxMessageLength = 256;
    public const int MaxValueLength = 1024;
    public const int MaxPrimaryFactLength = 256;
    public const int MaxVersionLength = 128;

    private readonly SortedDictionary<string, string> values = new(StringComparer.Ordinal);
    private readonly List<PlatformFactIssue> issues = [];

    public void AddValue(string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || values.Count >= MaxValues)
        {
            return;
        }

        key = Bound(key, MaxFieldLength);
        values[key] = Bound(value.Trim(), MaxValueLength);
    }

    public void AddIssue(PlatformFactIssue issue)
    {
        if (issues.Count >= MaxIssues)
        {
            return;
        }

        issues.Add(new PlatformFactIssue(
            Bound(issue.Code, MaxCodeLength),
            Bound(issue.Field, MaxFieldLength),
            BoundNullable(issue.NativeCode, MaxNativeCodeLength),
            BoundNullable(issue.Message, MaxMessageLength)));
    }

    public void AddMissingIssue(string field) =>
        AddIssue(new PlatformFactIssue(
            PlatformFactIssueCodes.NotSupported,
            field,
            Message: "The platform did not provide the required fact."));

    public PlatformFactSnapshot Build(
        string family,
        string? productName,
        string? productVersion,
        string? productBuild,
        string? kernelVersion,
        string osArchitecture,
        string processArchitecture,
        string hostname,
        AgentPackageIdentity package,
        DateTimeOffset observedAt,
        IReadOnlyList<PlatformCapabilitySupport> capabilities)
    {
        family = BoundPrimary("system.os.family", family, MaxPrimaryFactLength)!;
        productName = BoundPrimary("system.os.product.name", productName, MaxPrimaryFactLength);
        productVersion = BoundPrimary("system.os.version", productVersion, MaxPrimaryFactLength);
        productBuild = BoundPrimary("system.os.build", productBuild, MaxPrimaryFactLength);
        kernelVersion = BoundPrimary("system.os.kernel.version", kernelVersion, MaxPrimaryFactLength);
        osArchitecture = BoundPrimary("system.os.arch", osArchitecture, MaxPrimaryFactLength)!;
        processArchitecture = BoundPrimary("system.process.arch", processArchitecture, MaxPrimaryFactLength)!;
        hostname = BoundPrimary("system.hostname", hostname, MaxPrimaryFactLength)!;
        var agentVersion = BoundPrimary("agent_version", package.AgentVersion, MaxVersionLength)!;
        var packageVersion = BoundPrimary("package_version", package.PackageVersion, MaxVersionLength)!;

        var complete = issues.Count == 0;
        var outcome = DetermineOutcome(
            complete,
            productName,
            productVersion,
            productBuild,
            kernelVersion);
        return new PlatformFactSnapshot(
            family,
            productName,
            productVersion,
            productBuild,
            kernelVersion,
            osArchitecture,
            processArchitecture,
            hostname,
            agentVersion,
            packageVersion,
            package.PackageDigestSha256,
            observedAt,
            PlatformFactsCollector.CollectorVersion,
            outcome,
            complete,
            new ReadOnlyDictionary<string, string>(values),
            issues.AsReadOnly(),
            capabilities);
    }

    private string? BoundPrimary(string field, string? value, int maximum)
    {
        if (value is null)
        {
            return null;
        }

        value = value.Trim();
        if (value.Length <= maximum)
        {
            return value;
        }

        AddIssue(new PlatformFactIssue(
            PlatformFactIssueCodes.ResourceExhausted,
            field,
            Message: "The fact exceeded its output limit and was truncated."));
        return value[..maximum];
    }

    private PlatformFactCollectionOutcome DetermineOutcome(
        bool complete,
        string? productName,
        string? productVersion,
        string? productBuild,
        string? kernelVersion)
    {
        if (complete)
        {
            return PlatformFactCollectionOutcome.Succeeded;
        }

        // Field-level loss is partial because the runtime identity facts are still valid. A terminal
        // outcome is reserved for a collection that yielded no native product or kernel identity.
        if (productName is not null ||
            productVersion is not null ||
            productBuild is not null ||
            kernelVersion is not null)
        {
            return PlatformFactCollectionOutcome.Partial;
        }

        if (issues.Count > 0 && issues.All(static issue => issue.Code == PlatformFactIssueCodes.AccessDenied))
        {
            return PlatformFactCollectionOutcome.PermissionDenied;
        }

        if (issues.Count > 0 && issues.All(static issue => issue.Code == PlatformFactIssueCodes.TemporarilyUnavailable))
        {
            return PlatformFactCollectionOutcome.TemporarilyUnavailable;
        }

        return PlatformFactCollectionOutcome.Failed;
    }

    private static string Bound(string value, int maximum) =>
        value.Length <= maximum ? value : value[..maximum];

    private static string? BoundNullable(string? value, int maximum) =>
        value is null ? null : Bound(value, maximum);
}

public static class PlatformFactIssueCodes
{
    public const string AccessDenied = "access_denied";
    public const string NativeFailure = "native_failure";
    public const string NotFound = "not_found";
    public const string NotSupported = "not_supported";
    public const string ResourceExhausted = "resource_exhausted";
    public const string TemporarilyUnavailable = "temporarily_unavailable";
}
