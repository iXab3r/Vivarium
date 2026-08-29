namespace Vivarium.Controller.Administration;

public enum AdministrationState
{
    Unclaimed,
    SetupInProgress,
    SetupWaitingForGit,
    SetupActivating,
    Active,
    RecoveryAvailable,
    RecoveryInProgress,
}

public enum SetupOperationState
{
    InProgress,
    WaitingForGit,
    Activating,
    Completed,
    Abandoned,
    Blocked,
}

public sealed record AdministrationStartup(
    string InstanceId,
    AdministrationState State,
    string? BootstrapGenerationId,
    string? BootstrapToken,
    DateTimeOffset? BootstrapExpiresAt);

public sealed record AdministrationStatus(
    string InstanceId,
    AdministrationState State,
    long StateVersion,
    string? SetupOperationId,
    string? TokenDeliveryHint,
    DateTimeOffset UpdatedAt);

public sealed record SetupOperationSnapshot(
    string OperationId,
    SetupOperationState State,
    long StateVersion,
    string? PendingUserId,
    string? PendingLogin,
    string? PendingDisplayName,
    string? RepositoryMode,
    string? RepositoryId,
    string? ExpectedBaseCommit,
    string? CandidateCommit,
    string LastFailureCode,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record SetupClaimResult(
    string OperationId,
    string SessionToken,
    DateTimeOffset SessionExpiresAt,
    long StateVersion,
    bool Resumed);

public sealed record RecoveryClaimResult(
    string OperationId,
    string SessionToken,
    DateTimeOffset SessionExpiresAt);

public sealed record RecoverySessionAuthentication(
    string SessionId,
    string OperationId);

public sealed record SetupSessionAuthentication(
    string SessionId,
    string OperationId,
    SetupOperationSnapshot Operation);

public sealed record SetupAdministratorReservation(
    string OperationId,
    string UserId,
    string Login,
    string DisplayName,
    long StateVersion,
    bool Replayed);

public sealed record SetupRepositoryReservation(
    string OperationId,
    string RepositoryMode,
    string RepositoryId,
    string ExpectedBaseCommit,
    long StateVersion,
    bool Replayed);

public sealed record SetupCompletionResult(
    string OperationId,
    string UserId,
    string RepositoryId,
    string Commit,
    long StateVersion,
    bool Active);

public sealed record LocalSetupToken(
    string GenerationId,
    string Token,
    DateTimeOffset ExpiresAt,
    string? OperationId);

public sealed class AdministrationBootstrapException(
    string code,
    string message) : Exception(message)
{
    public string Code { get; } = code;
}
