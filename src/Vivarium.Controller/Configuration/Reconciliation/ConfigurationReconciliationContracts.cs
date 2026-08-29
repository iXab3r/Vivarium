using Microsoft.Data.Sqlite;
using Vivarium.Controller.Configuration.Git;
using Vivarium.Controller.Security;

namespace Vivarium.Controller.Configuration.Reconciliation;

public enum ConfigurationRevisionSetState
{
    Invalid,
    Blocked,
    Active,
    Superseded,
}

public enum ConfigurationMutationState
{
    Pending,
    Committed,
    Conflict,
    Rejected,
    Applied,
}

public enum ConfigurationMutationBeginOutcome
{
    Created,
    Existing,
}

public enum ConfigurationReconciliationOutcome
{
    Applied,
    NoChange,
    Invalid,
    Blocked,
}

public enum ConfigurationHeadConvergenceState
{
    Converged,
    Degraded,
}

public sealed record ConfigurationHeadConvergence(
    ConfigurationHeadConvergenceState State,
    ConfigurationRevision ObservedAuthoritativeHead,
    int Attempts,
    ConfigurationValidationDiagnostic? Diagnostic);

public sealed record ConfigurationMutationTarget(
    string TargetType,
    string TargetId,
    string Path);

public sealed record ConfigurationRepositoryAttemptFailure(
    string AttemptId,
    string OperationId,
    string FailureCode,
    string FailureSummary,
    DateTimeOffset AttemptedAt);

public sealed record ConfigurationMutationIntent(
    string OperationId,
    string OperationKind,
    string MaterializationScope,
    ConfigurationRevision ExpectedBase,
    string RequestHash,
    IReadOnlyList<ConfigurationMutationTarget>? Targets = null);

public sealed record ConfigurationMutationOperation(
    string OperationId,
    string OperationKind,
    string MaterializationScope,
    ManagementRequestContext RequestContext,
    ConfigurationRevision ExpectedBase,
    string RequestHash,
    ConfigurationMutationState State,
    ConfigurationRevision? ResultRevision,
    string? CandidateAggregateContentHash,
    string FailureCode,
    string FailureSummary,
    string? RevisionSetId,
    ConfigurationRevision? ConflictRevision,
    IReadOnlyList<ConfigurationPathDiff> Diff,
    IReadOnlyList<ConfigurationMutationTarget> Targets,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ConfigurationMutationBeginResult(
    ConfigurationMutationBeginOutcome Outcome,
    ConfigurationMutationOperation Operation);

public sealed record StoredConfigurationRevisionMember(
    string RepositoryId,
    string RepositoryRole,
    string Commit,
    string TreeHash,
    string? ContentHash,
    string? SchemaVersion,
    string? ProjectBinding);

public sealed record StoredConfigurationRevisionSet(
    string RevisionSetId,
    string MaterializationScope,
    string? BaseRevisionSetId,
    ConfigurationRevisionSetState State,
    string OperationId,
    DateTimeOffset RequestedAt,
    DateTimeOffset ValidatedAt,
    DateTimeOffset? AppliedAt,
    ManagementRequestContext RequestContext,
    IReadOnlyList<ConfigurationValidationDiagnostic> Diagnostics,
    IReadOnlyList<StoredConfigurationRevisionMember> Members);

public sealed record ConfigurationMaterializationState(
    string MaterializationScope,
    StoredConfigurationRevisionSet? Active,
    StoredConfigurationRevisionSet? LastKnownGood,
    StoredConfigurationRevisionSet LatestAttempt,
    DateTimeOffset UpdatedAt);

public sealed record ConfigurationReconciliationResult(
    ConfigurationReconciliationOutcome Outcome,
    StoredConfigurationRevisionSet Attempt,
    ConfigurationMaterializationState State,
    ConfigurationHeadConvergence? HeadConvergence = null);

public sealed class ConfigurationIdempotencyConflictException(string operationId)
    : Exception("the configuration idempotency key was already used for different content")
{
    public string OperationId { get; } = operationId;
}

public sealed class ConfigurationProjectionException(
    string code,
    string? path,
    string? field,
    string summary)
    : Exception(summary)
{
    public string Code { get; } = code;
    public string? Path { get; } = path;
    public string? Field { get; } = field;
}

/// <summary>
/// Applies disposable typed projections inside the same SQLite transaction that advances the active
/// revision-set pointer. Implementations must be synchronous and must not perform external I/O.
/// </summary>
public interface IConfigurationProjectionApplier
{
    void Apply(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ValidatedConfigurationRevision revision,
        string revisionSetId,
        DateTimeOffset appliedAt);
}
