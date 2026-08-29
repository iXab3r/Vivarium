using System.Collections.ObjectModel;

namespace Vivarium.Controller.Rest.Builds;

public sealed record BuildRestCollection<T>(
    IReadOnlyList<T> Items,
    BuildRestPage Page);

public sealed record BuildRestPage(
    string? NextCursor,
    int Limit);

public sealed record BuildSummaryResource(
    string Id,
    string Url,
    string Project,
    string Configuration,
    string State,
    string? Outcome,
    int ChildCount,
    int FinishedChildCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string RuntimeRevision);

public sealed record BuildResource(
    string Id,
    string Url,
    string Project,
    string Configuration,
    string State,
    string? Outcome,
    bool CancellationRequested,
    IReadOnlyList<BuildCellResource> Children,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string RuntimeRevision);

public sealed record BuildCellResource(
    string Id,
    string Name,
    string Rid,
    string AgentExpression,
    string State,
    string? Outcome,
    string? StatusText,
    string? CancellationReason,
    DateTimeOffset? QueueDeadline,
    long? QueueWaitMilliseconds,
    AssignedAgentResource? AssignedAgent,
    IReadOnlyList<BuildStepResource> Steps,
    IReadOnlyList<BuildArtifactResource> Artifacts);

public sealed record AssignedAgentResource(
    string Id,
    string Name,
    IReadOnlyDictionary<string, string> ReportedParameters,
    IReadOnlyDictionary<string, string> CustomParameters,
    IReadOnlyDictionary<string, string> EffectiveParameters);

public sealed record BuildStepResource(
    int Index,
    int ExitCode,
    bool TimedOut,
    bool Skipped);

public sealed record BuildArtifactResource(
    int Ordinal,
    string Path,
    string Sha256,
    long Size,
    string DownloadUrl);

public sealed record QueueEntryResource(
    string Id,
    string Url,
    string BuildId,
    string? BuildUrl,
    string? Project,
    string? Configuration,
    string? CellName,
    string? Rid,
    string AgentExpression,
    string State,
    string? ClaimedAgentId,
    bool DispatchPrepared,
    DateTimeOffset EnqueuedAt,
    DateTimeOffset? QueueDeadline,
    long? QueueWaitMilliseconds,
    DateTimeOffset? ClaimedAt,
    string RuntimeRevision);

internal static class BuildRestDictionaries
{
    public static IReadOnlyDictionary<string, string> Ordered(
        IEnumerable<KeyValuePair<string, string>> values) =>
        new ReadOnlyDictionary<string, string>(values
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
}
