using System.Collections.ObjectModel;

namespace Vivarium.Controller.Rest.Agents;

public sealed record AgentRestCollection<T>(
    IReadOnlyList<T> Items,
    AgentRestPage Page);

public sealed record AgentRestPage(
    string? NextCursor,
    int Limit);

public sealed record AgentResource(
    string Id,
    string Url,
    string DisplayName,
    string? Hostname,
    AgentStatusResource Status,
    AgentCurrentBuildResource? CurrentBuild,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastCommunicationAt,
    string Freshness,
    AgentSoftwareResource Software,
    AgentOperatingSystemResource OperatingSystem,
    bool Interactive,
    AgentParametersResource Parameters,
    string FactsUrl,
    AgentFactObservationSummaryResource FactObservation,
    IReadOnlyList<AgentCapabilityResource> Capabilities,
    AgentGenerationResource CurrentGenerations,
    string ObservationRevision,
    string RuntimeRevision);

public sealed record AgentStatusResource(
    bool Connected,
    bool Reconciled,
    string OperationalHealth,
    bool Quarantined,
    string OperationalReason,
    bool Authorized,
    bool Enabled,
    string Activity);

public sealed record AgentCurrentBuildResource(
    string Id,
    string? MatrixBuildId,
    string? Url);

public sealed record AgentSoftwareResource(
    string AgentVersion,
    string? PackageVersion,
    string? PackageDigestSha256,
    string? CollectorVersion);

public sealed record AgentOperatingSystemResource(
    string Family,
    string Version,
    string Architecture,
    string? ProductName,
    string? Build,
    string? KernelVersion,
    string? ProcessArchitecture);

public sealed record AgentCapabilityResource(
    string Id,
    int ContractMajor);

public sealed record AgentGenerationResource(
    long Credential,
    long Connection);

public sealed record AgentFactObservationSummaryResource(
    long Revision,
    string Quality,
    string Freshness,
    DateTimeOffset? ObservedAt,
    DateTimeOffset? ReceivedAt,
    int IssueCount,
    AgentGenerationResource? ObservationGenerations);

public sealed record AgentFactIssueResource(
    string Code,
    string Field,
    string? NativeCode);

public sealed record AgentFactsResource(
    string AgentId,
    string AgentUrl,
    long ObservationRevision,
    string Quality,
    string? CollectorOutcome,
    bool Complete,
    string Freshness,
    DateTimeOffset? ObservedAt,
    DateTimeOffset? ReceivedAt,
    AgentOperatingSystemResource OperatingSystem,
    string? Hostname,
    AgentSoftwareResource Software,
    bool? Interactive,
    IReadOnlyList<AgentCapabilityResource> Capabilities,
    IReadOnlyList<AgentFactIssueResource> Issues,
    IReadOnlyDictionary<string, string> ExtensionFacts,
    AgentGenerationResource CurrentGenerations,
    AgentGenerationResource? ObservationGenerations);

public sealed record AgentParametersResource(
    IReadOnlyDictionary<string, string> Reported,
    IReadOnlyDictionary<string, string> Custom,
    IReadOnlyDictionary<string, string> Effective);

internal static class AgentRestDictionaries
{
    public static IReadOnlyDictionary<string, string> Ordered(
        IEnumerable<KeyValuePair<string, string>> values) =>
        new ReadOnlyDictionary<string, string>(values
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
}
