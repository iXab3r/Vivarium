namespace Vivarium.Controller.Deployment;

public enum AgentUpgradeState
{
    Draining,
    HandoffReady,
    AwaitingHealth,
    CommitPending,
    Finalizing,
    RollbackRequested,
    Succeeded,
    RolledBack,
    Failed,
    Cancelled,
}

public sealed record AgentUpgradeOperation(
    string OperationId,
    string AgentId,
    AgentPackage Package,
    AgentUpgradeState State,
    string ActorType,
    string ActorId,
    string RequestId,
    string CorrelationId,
    string Reason,
    long MaintenanceFence,
    string? PriorPackageSha256,
    long StartingConnectionGeneration,
    long? ObservedConnectionGeneration,
    int RestartAttempts,
    long? LastDispatchConnectionGeneration,
    DateTimeOffset? NextRestartAt,
    string? CancellationReason,
    string? FailureCode,
    string? ResultPackageSha256,
    bool DrainHeld,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset Deadline,
    DateTimeOffset? CompletedAt)
{
    public bool IsTerminal => State is
        AgentUpgradeState.Succeeded or
        AgentUpgradeState.RolledBack or
        AgentUpgradeState.Failed or
        AgentUpgradeState.Cancelled;
}

public sealed record AgentUpgradeEvent(
    long Sequence,
    string Phase,
    string Code,
    long? ConnectionGeneration,
    string? PackageSha256,
    DateTimeOffset CreatedAt);

public sealed record AgentUpgradeCreation(
    AgentUpgradeOperation Operation,
    bool Replayed);

public sealed class AgentUpgradeException(
    string code,
    string message,
    int statusCode = StatusCodes.Status409Conflict) : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}
