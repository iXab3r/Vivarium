using Vivarium.Contracts.V1;
using Vivarium.Controller.Builds;
using Vivarium.Controller.Management;
using Vivarium.Controller.Rest.Events;

namespace Vivarium.Controller.Rest.Builds;

internal sealed record BuildSummaryPageProjection(
    IReadOnlyList<BuildSummaryResource> Items,
    bool HasMore,
    DateTimeOffset? NextCreatedAt,
    string? NextId);

internal sealed record QueuePageProjection(
    IReadOnlyList<QueueEntryResource> Items,
    bool HasMore,
    long? NextQueueId);

internal sealed class BuildRestProjection
{
    private readonly MatrixBuildStore matrixBuilds;
    private readonly BuildQueueStore queue;
    private readonly BuildEventStore? events;

    public BuildRestProjection(
        MatrixBuildStore matrixBuilds,
        BuildQueueStore queue,
        BuildEventStore? events = null)
    {
        this.matrixBuilds = matrixBuilds;
        this.queue = queue;
        this.events = events;
    }

    public async Task<BuildSummaryPageProjection> ListBuildsAsync(MatrixBuildQuery query)
    {
        var page = await matrixBuilds.ListPageAsync(query);
        var items = await Task.WhenAll(page.Items.Select(ToSummaryAsync));
        var last = page.Items.LastOrDefault();
        return new BuildSummaryPageProjection(
            items,
            page.HasMore,
            last?.CreatedAt,
            last?.MatrixBuildId);
    }

    public async Task<BuildResource?> GetBuildAsync(string matrixBuildId)
    {
        var snapshot = await matrixBuilds.GetSnapshotAsync(matrixBuildId);
        if (snapshot is null)
        {
            return null;
        }

        var queueItems = await Task.WhenAll(snapshot.Cells.Select(cell => queue.GetAsync(cell.BuildId)));
        var children = snapshot.Cells
            .Select((cell, index) => ToCell(snapshot.Build.BuildId, cell, queueItems[index]))
            .ToArray();
        var updatedAt = queueItems
            .Where(item => item is not null)
            .Select(item => item!.RemovedAt ?? item.ClaimedAt ?? item.EnqueuedAt)
            .Append(DateTimeOffset.FromUnixTimeMilliseconds(snapshot.UpdatedUnixMs))
            .Max();
        var runtimeRevision = events is null
            ? null
            : await events.GetCurrentRuntimeRevisionAsync(snapshot.Build.BuildId);
        return new BuildResource(
            snapshot.Build.BuildId,
            $"/api/v1/builds/{Uri.EscapeDataString(snapshot.Build.BuildId)}",
            snapshot.Project,
            snapshot.Configuration,
            BuildState(snapshot.State),
            BuildOutcomeValue(snapshot.Outcome),
            snapshot.State == DurableBuildState.CancelRequested,
            children,
            DateTimeOffset.FromUnixTimeMilliseconds(snapshot.CreatedUnixMs),
            updatedAt,
            runtimeRevision ?? RuntimeRevision(updatedAt.ToUnixTimeMilliseconds()));
    }

    public async Task<QueuePageProjection> ListQueueAsync(BuildQueueQuery query)
    {
        var page = await queue.ListPendingPageAsync(query);
        var items = page.Items.Select(ToQueueEntry).ToArray();
        return new QueuePageProjection(
            items,
            page.HasMore,
            page.Items.LastOrDefault()?.QueueId);
    }

    private async Task<BuildSummaryResource> ToSummaryAsync(MatrixBuildSummary summary)
    {
        var runtimeRevision = events is null
            ? null
            : await events.GetCurrentRuntimeRevisionAsync(summary.MatrixBuildId);
        return new BuildSummaryResource(
            summary.MatrixBuildId,
            $"/api/v1/builds/{Uri.EscapeDataString(summary.MatrixBuildId)}",
            summary.Project,
            summary.Configuration,
            BuildState(summary.State),
            BuildOutcomeValue(summary.Outcome),
            summary.CellCount,
            summary.FinishedCellCount,
            summary.CreatedAt,
            summary.UpdatedAt,
            runtimeRevision ?? RuntimeRevision(summary.UpdatedAt.ToUnixTimeMilliseconds()));
    }

    private static BuildCellResource ToCell(
        string matrixBuildId,
        BuildCellSnapshot cell,
        BuildQueueItem? queueItem)
    {
        var reported = BuildRestDictionaries.Ordered(cell.AgentParameters);
        var custom = BuildRestDictionaries.Ordered(cell.AgentCustomParameters);
        var effectiveValues = new Dictionary<string, string>(reported, StringComparer.Ordinal);
        foreach (var pair in custom)
        {
            effectiveValues[pair.Key] = pair.Value;
        }

        var assignedAgent = string.IsNullOrWhiteSpace(cell.AgentId)
            ? null
            : new AssignedAgentResource(
                cell.AgentId,
                cell.AgentName,
                reported,
                custom,
                BuildRestDictionaries.Ordered(effectiveValues));
        var steps = cell.Steps
            .OrderBy(step => step.StepIndex)
            .Select(step => new BuildStepResource(
                step.StepIndex,
                step.ExitCode,
                step.TimedOut,
                step.Skipped))
            .ToArray();
        var artifacts = cell.Artifacts
            .Select((artifact, ordinal) => new BuildArtifactResource(
                ordinal,
                artifact.Path,
                artifact.Sha256,
                artifact.Size,
                $"/builds/{Uri.EscapeDataString(matrixBuildId)}/cells/" +
                $"{Uri.EscapeDataString(cell.BuildId)}/artifacts/{ordinal}"))
            .ToArray();
        return new BuildCellResource(
            cell.BuildId,
            cell.Name,
            cell.Rid,
            cell.AgentExpression,
            BuildState(cell.State),
            BuildOutcomeValue(cell.Outcome),
            string.IsNullOrWhiteSpace(cell.StatusText) ? null : cell.StatusText,
            cell.State == DurableBuildState.CancelRequested || cell.Outcome == BuildOutcome.Cancelled
                ? (string.IsNullOrWhiteSpace(cell.StatusText) ? null : cell.StatusText)
                : null,
            cell.QueueDeadlineUnixMs == 0
                ? null
                : DateTimeOffset.FromUnixTimeMilliseconds(cell.QueueDeadlineUnixMs),
            QueueWaitMilliseconds(queueItem),
            assignedAgent,
            steps,
            artifacts);
    }

    private static QueueEntryResource ToQueueEntry(BuildQueueEntry entry)
    {
        var updatedAt = entry.BuildUpdatedAt > (entry.ClaimedAt ?? entry.EnqueuedAt)
            ? entry.BuildUpdatedAt
            : entry.ClaimedAt ?? entry.EnqueuedAt;
        return new QueueEntryResource(
            entry.QueueId.ToString(global::System.Globalization.CultureInfo.InvariantCulture),
            "/api/v1/queue",
            entry.BuildId,
            entry.MatrixBuildId is null
                ? null
                : $"/api/v1/builds/{Uri.EscapeDataString(entry.MatrixBuildId)}",
            entry.Project,
            entry.Configuration,
            entry.CellName,
            entry.Rid,
            entry.AgentExpression,
            QueueState(entry.State),
            entry.ClaimedAgentId,
            entry.DispatchPrepared,
            entry.EnqueuedAt,
            entry.QueueDeadline,
            entry.ClaimedAt is null
                ? null
                : Math.Max(0, (long)(entry.ClaimedAt.Value - entry.EnqueuedAt).TotalMilliseconds),
            entry.ClaimedAt,
            RuntimeRevision(updatedAt.ToUnixTimeMilliseconds()));
    }

    private static long? QueueWaitMilliseconds(BuildQueueItem? item)
    {
        if (item is null)
        {
            return null;
        }

        var completedAt = item.ClaimedAt ?? item.RemovedAt;
        return completedAt is null
            ? null
            : Math.Max(0, (long)(completedAt.Value - item.EnqueuedAt).TotalMilliseconds);
    }

    internal static string BuildState(DurableBuildState state) => state switch
    {
        DurableBuildState.Queued => "queued",
        DurableBuildState.Running => "running",
        DurableBuildState.CancelRequested => "cancel-requested",
        DurableBuildState.Finished => "finished",
        _ => "unknown",
    };

    internal static string? BuildOutcomeValue(BuildOutcome outcome) => outcome switch
    {
        BuildOutcome.Succeeded => "succeeded",
        BuildOutcome.Failed => "failed",
        BuildOutcome.Cancelled => "cancelled",
        BuildOutcome.InfrastructureFailed => "infrastructure-failed",
        _ => null,
    };

    internal static string QueueState(BuildQueueItemState state) => state switch
    {
        BuildQueueItemState.Queued => "queued",
        BuildQueueItemState.Claimed => "claimed",
        BuildQueueItemState.Removed => "removed",
        _ => "unknown",
    };

    internal static string RuntimeRevision(long updatedUnixMs) => $"runtime:{updatedUnixMs}";
}
