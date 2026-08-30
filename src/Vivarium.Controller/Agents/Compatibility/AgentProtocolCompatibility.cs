using Grpc.Core;
using Vivarium.Contracts.V1;

namespace Vivarium.Controller.Agents.Compatibility;

internal sealed class AgentProtocolException(StatusCode statusCode, string message)
    : Exception(message)
{
    public StatusCode StatusCode { get; } = statusCode;
}

internal sealed record AgentProtocolNegotiation(
    AgentProtocolMode Mode,
    uint SelectedVersion,
    IReadOnlyList<CapabilitySupport> AdvertisedCapabilities,
    IReadOnlyList<CapabilitySupport> NegotiatedCapabilities)
{
    public bool IsLegacy => Mode == AgentProtocolMode.Legacy;

    public bool SupportsBuildRunner => NegotiatedCapabilities.Any(
        capability => capability.CapabilityId == AgentProtocolCompatibility.BuildRunnerCapabilityId);
}

/// <summary>
/// Bounded, additive AgentHub negotiation. Empty range fields mean the pre-negotiation legacy
/// protocol; one empty bound or any other partially populated negotiation is rejected rather than
/// guessed. Unknown, well-formed capabilities remain advertised but are never selected by this
/// controller.
/// </summary>
internal static class AgentProtocolCompatibility
{
    public const uint MinimumSupportedVersion = (uint)AgentProtocolVersion.V1;
    public const uint CurrentVersion = (uint)AgentProtocolVersion.V1;
    public const uint MaximumAdvertisedVersion = 1024;
    public const int MaximumCapabilities = 64;
    public const int MaximumCapabilityIdLength = 128;
    public const int MaximumHostFactIssues = 32;
    public const int MaximumHostFactValues = 64;
    public const int MaximumHostFactKeyLength = 128;

    public const string BuildRunnerCapabilityId = "teamcity.build-runner.v1";
    public const string HostFactsCapabilityId = "agent-explorer.host-facts.v1";
    public const string BootstrapSupervisorCapabilityId = "vivarium.bootstrap-supervisor.v1";

    private static readonly IReadOnlyDictionary<string, uint> KnownCapabilities =
        new Dictionary<string, uint>(StringComparer.Ordinal)
        {
            [BuildRunnerCapabilityId] = 1,
            [HostFactsCapabilityId] = 1,
            [BootstrapSupervisorCapabilityId] = 1,
        };

    public static AgentProtocolNegotiation Negotiate(Hello hello)
    {
        ArgumentNullException.ThrowIfNull(hello);

        var minimum = hello.MinimumProtocolVersion;
        var current = hello.CurrentProtocolVersion;
        if (minimum == 0 && current == 0)
        {
            ValidateLegacyFields(hello);
            return new AgentProtocolNegotiation(
                AgentProtocolMode.Legacy,
                SelectedVersion: 0,
                AdvertisedCapabilities: [],
                NegotiatedCapabilities: []);
        }

        if (minimum == 0 || current == 0)
        {
            throw Invalid("minimum_protocol_version and current_protocol_version must both be set");
        }

        if (minimum > current)
        {
            throw Invalid("minimum_protocol_version cannot exceed current_protocol_version");
        }

        if (minimum > MaximumAdvertisedVersion || current > MaximumAdvertisedVersion)
        {
            throw Invalid($"protocol versions must not exceed {MaximumAdvertisedVersion}");
        }

        var selected = Math.Min(CurrentVersion, current);
        if (selected < Math.Max(MinimumSupportedVersion, minimum))
        {
            throw new AgentProtocolException(
                StatusCode.FailedPrecondition,
                $"agent protocol range {minimum}-{current} is incompatible with controller range " +
                $"{MinimumSupportedVersion}-{CurrentVersion}");
        }

        if (hello.CredentialGeneration > long.MaxValue)
        {
            throw Invalid("credential_generation exceeds the supported range");
        }

        ValidatePackageDigest(hello.AgentPackageSha256);
        ValidateOptionalText(hello.UpgradeOperationId, 128, "upgrade_operation_id");
        ValidateOptionalText(hello.UpgradeFailureCode, 64, "upgrade_failure_code");
        ValidateOptionalText(hello.ProcessInstanceId, 128, "process_instance_id");
        if (hello.ProcessInstanceId.Length > 0 &&
            (hello.ProcessInstanceId.Length < 16 ||
             hello.ProcessInstanceId.Any(character => !char.IsAsciiLetterOrDigit(character))))
        {
            throw Invalid("process_instance_id must contain 16-128 ASCII letters or digits");
        }
        var advertised = NormalizeCapabilities(hello.Capabilities);
        ValidateHostFacts(hello, advertised);
        var negotiated = advertised
            .Where(capability =>
                KnownCapabilities.TryGetValue(capability.CapabilityId, out var major) &&
                major == capability.ContractMajor)
            .Select(capability => capability.Clone())
            .ToArray();

        return new AgentProtocolNegotiation(
            AgentProtocolMode.Negotiated,
            selected,
            advertised,
            negotiated);
    }

    public static AgentStaticObservation? CreateStaticObservation(
        Hello hello,
        AgentProtocolNegotiation negotiation,
        DateTimeOffset receivedAt,
        long credentialGeneration,
        long connectionGeneration)
    {
        ArgumentNullException.ThrowIfNull(hello);
        ArgumentNullException.ThrowIfNull(negotiation);
        if (hello.HostFacts is not { } facts)
        {
            return null;
        }

        var observedAt = facts.ObservedAtUnixMs == 0
            ? (DateTimeOffset?)null
            : DateTimeOffset.FromUnixTimeMilliseconds(facts.ObservedAtUnixMs);
        return new AgentStaticObservation(
            hello.AgentId,
            observedAt,
            receivedAt,
            facts.Outcome switch
            {
                HostFactsOutcome.Succeeded => AgentFactCollectorOutcome.Succeeded,
                HostFactsOutcome.Partial => AgentFactCollectorOutcome.Partial,
                HostFactsOutcome.Degraded => AgentFactCollectorOutcome.Degraded,
                HostFactsOutcome.PermissionDenied => AgentFactCollectorOutcome.PermissionDenied,
                HostFactsOutcome.TemporarilyUnavailable =>
                    AgentFactCollectorOutcome.TemporarilyUnavailable,
                HostFactsOutcome.Failed => AgentFactCollectorOutcome.Failed,
                _ => throw new InvalidOperationException("host facts were not validated"),
            },
            facts.Complete,
            facts.Issues.Select(issue => new AgentObservationIssue(
                issue.Code,
                issue.Field,
                NullIfEmpty(issue.NativeCode),
                NullIfEmpty(issue.Message))).ToArray(),
            CreateCapabilityObservation(negotiation),
            new AgentStaticFacts(
                facts.Hostname,
                facts.Family,
                facts.ProductName,
                facts.ProductVersion,
                facts.ProductBuild,
                facts.KernelVersion,
                facts.OsArchitecture,
                facts.ProcessArchitecture,
                hello.AgentVersion,
                facts.AgentPackageVersion,
                facts.CollectorVersion,
                hello.Interactive,
                new SortedDictionary<string, string>(facts.Values, StringComparer.Ordinal)),
            credentialGeneration,
            connectionGeneration,
            NullIfEmpty(hello.AgentPackageSha256));
    }

    public static IReadOnlyList<AgentCapabilitySupport> CreateCapabilityObservation(
        AgentProtocolNegotiation negotiation)
    {
        ArgumentNullException.ThrowIfNull(negotiation);
        return negotiation.AdvertisedCapabilities.Select(capability => new AgentCapabilitySupport(
            capability.CapabilityId,
            checked((int)capability.ContractMajor))).ToArray();
    }

    private static void ValidateLegacyFields(Hello hello)
    {
        if (hello.Capabilities.Count != 0 ||
            hello.HostFacts is not null ||
            hello.CredentialGeneration != 0 ||
            hello.AgentPackageSha256.Length != 0 ||
            hello.UpgradeOperationId.Length != 0 ||
            hello.UpgradeFailureCode.Length != 0 ||
            hello.WorkloadRecoveryOutcome != WorkloadRecoveryOutcome.Unspecified ||
            hello.WorkloadRecoveryBuildId.Length != 0 ||
            hello.WorkloadRecoveryFailureCode.Length != 0 ||
            hello.ProcessInstanceId.Length != 0)
        {
            throw Invalid(
                "legacy protocol requires empty capabilities, host_facts, credential_generation, " +
                "agent_package_sha256, upgrade, workload recovery, and process instance fields");
        }
    }

    private static IReadOnlyList<CapabilitySupport> NormalizeCapabilities(
        IEnumerable<CapabilitySupport> capabilities)
    {
        var materialized = capabilities.ToArray();
        if (materialized.Length > MaximumCapabilities)
        {
            throw Invalid($"capabilities must contain at most {MaximumCapabilities} entries");
        }

        var normalized = new SortedDictionary<string, CapabilitySupport>(StringComparer.Ordinal);
        foreach (var capability in materialized)
        {
            if (capability is null)
            {
                throw Invalid("capabilities cannot contain null entries");
            }

            ValidateCapability(capability);
            if (!normalized.TryAdd(capability.CapabilityId, capability.Clone()))
            {
                throw Invalid($"capability '{capability.CapabilityId}' is advertised more than once");
            }
        }

        return normalized.Values.ToArray();
    }

    private static void ValidateCapability(CapabilitySupport capability)
    {
        var id = capability.CapabilityId;
        if (id.Length is < 1 or > MaximumCapabilityIdLength ||
            id.Any(character => character > 0x7f || char.IsUpper(character) || char.IsWhiteSpace(character)))
        {
            throw Invalid(
                $"capability_id must be 1-{MaximumCapabilityIdLength} lowercase ASCII characters");
        }

        if (capability.ContractMajor is 0 or > MaximumAdvertisedVersion)
        {
            throw Invalid($"capability '{id}' has an unsupported contract_major");
        }

        var segments = id.Split('.');
        if (segments.Length < 2 || segments.Any(segment => !IsValidCapabilitySegment(segment)))
        {
            throw Invalid($"capability_id '{id}' is not a lowercase dotted identifier");
        }

        if (!string.Equals(
                segments[^1],
                $"v{capability.ContractMajor}",
                StringComparison.Ordinal))
        {
            throw Invalid(
                $"capability_id '{id}' does not match contract_major {capability.ContractMajor}");
        }
    }

    private static bool IsValidCapabilitySegment(string segment)
    {
        if (segment.Length == 0 || segment[0] == '-' || segment[^1] == '-')
        {
            return false;
        }

        return segment.All(character =>
            character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');
    }

    private static void ValidatePackageDigest(string digest)
    {
        if (digest.Length == 0)
        {
            return;
        }

        if (digest.Length != 64 || digest.Any(character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw Invalid("agent_package_sha256 must be empty or 64 lowercase hexadecimal characters");
        }
    }

    private static void ValidateHostFacts(
        Hello hello,
        IReadOnlyList<CapabilitySupport> capabilities)
    {
        var facts = hello.HostFacts;
        if (facts is null)
        {
            return;
        }

        if (!capabilities.Any(capability =>
                capability.CapabilityId == HostFactsCapabilityId && capability.ContractMajor == 1))
        {
            throw Invalid("host_facts requires the agent-explorer.host-facts.v1 capability");
        }

        if (facts.Issues.Count > MaximumHostFactIssues)
        {
            throw Invalid($"host_facts.issues must contain at most {MaximumHostFactIssues} entries");
        }

        if (facts.Values.Count > MaximumHostFactValues)
        {
            throw Invalid($"host_facts.values must contain at most {MaximumHostFactValues} entries");
        }

        if (facts.Outcome == HostFactsOutcome.Unspecified || !Enum.IsDefined(facts.Outcome))
        {
            throw Invalid("host_facts.outcome must be a recognized non-zero value");
        }

        if (facts.ObservedAtUnixMs != 0 &&
            (facts.ObservedAtUnixMs < DateTimeOffset.MinValue.ToUnixTimeMilliseconds() ||
             facts.ObservedAtUnixMs > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds()))
        {
            throw Invalid("host_facts.observed_at_unix_ms is outside the supported timestamp range");
        }

        ValidateOptionalText(facts.Hostname, 255, "host_facts.hostname");
        ValidateOptionalText(facts.Family, 128, "host_facts.family");
        ValidateOptionalText(facts.ProductName, 256, "host_facts.product_name");
        ValidateOptionalText(facts.ProductVersion, 128, "host_facts.product_version");
        ValidateOptionalText(facts.ProductBuild, 128, "host_facts.product_build");
        ValidateOptionalText(facts.KernelVersion, 256, "host_facts.kernel_version");
        ValidateOptionalText(facts.OsArchitecture, 128, "host_facts.os_architecture");
        ValidateOptionalText(facts.ProcessArchitecture, 128, "host_facts.process_architecture");
        ValidateOptionalText(hello.AgentVersion, 128, "agent_version");
        ValidateOptionalText(facts.AgentPackageVersion, 128, "host_facts.agent_package_version");
        ValidateOptionalText(facts.CollectorVersion, 128, "host_facts.collector_version");

        foreach (var issue in facts.Issues)
        {
            if (issue is null)
            {
                throw Invalid("host_facts.issues cannot contain null entries");
            }

            ValidateRequiredText(issue.Code, 64, "host_facts.issues.code");
            ValidateRequiredText(issue.Field, 128, "host_facts.issues.field");
            ValidateOptionalText(issue.NativeCode, 64, "host_facts.issues.native_code");
            ValidateOptionalText(issue.Message, 256, "host_facts.issues.message");
        }

        foreach (var (key, value) in facts.Values)
        {
            ValidateRequiredText(key, MaximumHostFactKeyLength, "host_facts.values key");
            ValidateOptionalText(value, 1024, $"host_facts.values['{key}']");
        }
    }

    private static void ValidateRequiredText(string value, int maximumLength, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength)
        {
            throw Invalid($"{field} must contain 1-{maximumLength} characters");
        }
    }

    private static void ValidateOptionalText(string value, int maximumLength, string field)
    {
        if (value.Length > maximumLength)
        {
            throw Invalid($"{field} must not exceed {maximumLength} characters");
        }
    }

    private static AgentProtocolException Invalid(string message) =>
        new(StatusCode.InvalidArgument, message);

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
}
