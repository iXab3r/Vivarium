using Vivarium.Contracts.V1;

namespace Vivarium.Controller.Builds;

public enum BuildQueueItemState
{
    Queued,
    Claimed,
    Removed,
}

/// <summary>A durable Build Queue row joined with its authoritative build assignment.</summary>
public sealed record BuildQueueItem(
    long QueueId,
    string BuildId,
    BuildAssignment Assignment,
    string AgentExpression,
    BuildQueueItemState State,
    string? ClaimedAgentId,
    bool DispatchPrepared,
    string? DispatchSessionId,
    DateTimeOffset EnqueuedAt,
    DateTimeOffset? QueueDeadline,
    DateTimeOffset? ClaimedAt,
    DateTimeOffset? RemovedAt,
    string? RemovalReason);

public sealed record BuildQueueQuery(
    int Limit,
    long? AfterQueueId = null,
    BuildQueueItemState? State = null,
    string? Project = null,
    string? Configuration = null,
    string? ClaimedAgentId = null);

public sealed record BuildQueueEntry(
    long QueueId,
    string BuildId,
    string? MatrixBuildId,
    string? Project,
    string? Configuration,
    string? CellName,
    string? Rid,
    string AgentExpression,
    BuildQueueItemState State,
    string? ClaimedAgentId,
    bool DispatchPrepared,
    DateTimeOffset EnqueuedAt,
    DateTimeOffset? QueueDeadline,
    DateTimeOffset? ClaimedAt,
    DateTimeOffset BuildUpdatedAt);

public sealed record BuildQueuePage(
    IReadOnlyList<BuildQueueEntry> Items,
    bool HasMore);
