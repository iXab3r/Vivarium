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
