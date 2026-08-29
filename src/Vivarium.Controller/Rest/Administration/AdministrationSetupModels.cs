namespace Vivarium.Controller.Rest.Administration;

public sealed record SetupStatusResource(
    string Url,
    string InstanceId,
    string State,
    long StateVersion,
    string? OperationUrl,
    string? TokenDeliveryHint,
    DateTimeOffset UpdatedAt);

public sealed record SetupClaimRequest(string? Token);

public sealed record SetupClaimResource(
    string OperationId,
    string OperationUrl,
    string SetupSessionToken,
    DateTimeOffset ExpiresAt,
    long StateVersion,
    bool Resumed);

public sealed record RecoveryClaimRequest(string? Token);

public sealed record RecoveryClaimResource(
    string OperationId,
    string RecoverySessionToken,
    DateTimeOffset ExpiresAt);

public sealed record SetupOperationResource(
    string OperationId,
    string Url,
    string State,
    long StateVersion,
    SetupPendingAdministratorResource? Administrator,
    SetupRepositoryResource? Repository,
    string? CandidateCommit,
    string? LastFailureCode,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record SetupPendingAdministratorResource(
    string UserId,
    string Login,
    string DisplayName);

public sealed record SetupAdministratorRequest(
    long? StateVersion,
    string? Login,
    string? DisplayName,
    string? Password);

public sealed record SetupAdministratorResource(
    string OperationId,
    string UserId,
    string Login,
    string DisplayName,
    long StateVersion,
    bool Replayed);

public sealed record SetupRepositoryRequest(long? StateVersion, string? Mode);

public sealed record SetupRepositoryResource(
    string Mode,
    string RepositoryId,
    string ExpectedBaseCommit);

public sealed record SetupRepositoryChangeResource(
    string OperationId,
    SetupRepositoryResource Repository,
    long StateVersion,
    bool Replayed);

public sealed record SetupChangesResource(
    string OperationId,
    SetupRepositoryResource Repository,
    bool Valid,
    IReadOnlyList<SetupValidationErrorResource> Errors);

public sealed record SetupCompletionRequest(long? StateVersion);

public sealed record SetupCompletionResource(
    string OperationId,
    string UserId,
    string RepositoryId,
    string Commit,
    long StateVersion,
    bool Active);

public sealed record SetupValidationErrorResource(
    string Code,
    string? Path,
    string? Field,
    string Summary);
