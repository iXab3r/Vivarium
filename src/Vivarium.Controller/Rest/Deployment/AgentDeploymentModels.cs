namespace Vivarium.Controller.Rest.Deployment;

public sealed record AgentPackageResource(
    string PackageId,
    string Version,
    string Rid,
    string Sha256,
    long Size,
    DateTimeOffset CreatedAt,
    string Source);

public sealed record AgentPackageCollectionResource(
    IReadOnlyList<AgentPackageResource> Items);

public sealed record AgentUpgradeRequest(
    string? Reason,
    int? TimeoutSeconds);

public sealed record AgentUpgradeCancellationRequest(string? Reason);

public sealed record AgentUpgradeOperationResource(
    string OperationId,
    string AgentId,
    AgentPackageResource Package,
    string State,
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
    DateTimeOffset? CompletedAt,
    IReadOnlyList<AgentUpgradeEventResource> Events);

public sealed record AgentUpgradeEventResource(
    long Sequence,
    string Phase,
    string Code,
    long? ConnectionGeneration,
    string? PackageSha256,
    DateTimeOffset CreatedAt);

public sealed record AgentUpgradeOperationCollectionResource(
    IReadOnlyList<AgentUpgradeOperationResource> Items);

public sealed record BootstrapAgentManifest(
    int SchemaVersion,
    string Action,
    string OperationId,
    string Version,
    string Rid,
    string Sha256,
    string PriorSha256,
    long Size,
    string Url,
    int HealthTimeoutSeconds,
    long DeadlineUnixMs);

public sealed record BootstrapUpgradeFailureReport(
    int SchemaVersion,
    string OperationId,
    string FailureCode);
