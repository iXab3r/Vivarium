namespace Vivarium.Controller.Builds.Runtime;

// Fresh-install PG authority. Queue/history are lifecycle projections of this same numeric Build;
// the opaque Agent workload and immutable execution input are separate identities.
public sealed class RuntimeBuild
{
    public long Id { get; set; }
    public long BuildTypeId { get; set; }
    public long ProjectId { get; set; }
    public long? Counter { get; set; }
    public string? Number { get; set; }
    public Guid ActorId { get; set; }
    public required string CredentialKind { get; set; }
    public required string CorrelationId { get; set; }
    public string State { get; set; } = "QUEUED";
    public string Status { get; set; } = "UNKNOWN";
    public string StatusText { get; set; } = "Build settings have not been finalized";
    public DateTimeOffset QueuedAt { get; set; }
    public DateTimeOffset? QueueDeadline { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public DateTimeOffset? CancelRequestedAt { get; set; }
    public string? CancelReason { get; set; }
    public Guid? CancelOperationId { get; set; }
    public Guid? CancelActorId { get; set; }
    public string? StopMode { get; set; }
    public DateTimeOffset? StopDeadline { get; set; }
    public DateTimeOffset? StopAcknowledgedAt { get; set; }
    public bool FailedToStart { get; set; }
}

public sealed class RuntimeBuildCounter
{
    public long BuildTypeId { get; set; }
    public long NextValue { get; set; } = 1;
}

// Current queue resolution, flattened under the queue fence. Original request/input and every
// replacement audit event remain retained; an assigned/finished physical execution is never replaced.
public sealed class RuntimeBuildResolution
{
    public long RequestedBuildId { get; set; }
    public long ResolvedBuildId { get; set; }
    public long BuildTypeId { get; set; }
    public long ProjectId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

// This is the actually collected settings relation, not a writable PG desired-settings override.
// Settings and the Source declaration are collected at admission; workload revision and actual
// checkout remain separate later facts, never a rewrite of this input or of the historical plan.
public sealed class RuntimeBuildInput
{
    public long BuildId { get; set; }
    public long BuildTypeId { get; set; }
    public long ProjectId { get; set; }
    public Guid RevisionId { get; set; }
    public DateTimeOffset CollectedAt { get; set; }
    public required string OverridesJson { get; set; }
    public required string AdmissionJson { get; set; }
    public byte[] ProtectedEnvironment { get; set; } = [];
    public long? VcsRootInstanceId { get; set; }
    public string? SourceJson { get; set; }
}

// Immutable effective VCS properties; checkout attachment rules remain in each collected input.
public sealed class RuntimeVcsRootInstance
{
    public long Id { get; set; }
    public long ProjectId { get; set; }
    public string RootId { get; set; } = "";
    public string PropertiesHash { get; set; } = "";
    public string PropertiesJson { get; set; } = "";
}

// The coordinator's selected workload revision, distinct from settings and Agent checkout evidence.
public sealed class RuntimeBuildSourceSelection
{
    public long BuildId { get; set; }
    public string Version { get; set; } = "";
    public DateTimeOffset SelectedAt { get; set; }
}

public sealed class RuntimeBuildSourceReceipt
{
    public long BuildId { get; set; }
    public byte[] Evidence { get; set; } = [];
    public DateTimeOffset ReceivedAt { get; set; }
}

// Actual selections between ordinary Builds; desired edges remain in the immutable Git plan.
public sealed class RuntimeBuildDependency
{
    public long BuildId { get; set; }
    public string Kind { get; set; } = "";
    public long UpstreamBuildTypeId { get; set; }
    public long UpstreamBuildId { get; set; }
    public DateTimeOffset SelectedAt { get; set; }
}

// File-level consumption provenance is committed with the assignment, not inferred from latest outputs.
public sealed class RuntimeBuildConsumedArtifact
{
    public long BuildId { get; set; }
    public int Ordinal { get; set; }
    public long ProducerBuildId { get; set; }
    public int ProducerArtifactOrdinal { get; set; }
    public string DestinationPath { get; set; } = "";
}

public sealed class RuntimeBuildExecution
{
    public long BuildId { get; set; }
    public required string WorkloadId { get; set; }
    public required string AgentId { get; set; }
    public long LeaseFence { get; set; }
    public long CredentialGeneration { get; set; }
    public required string AssignedSessionId { get; set; }
    public long AssignedConnectionGeneration { get; set; }
    public required string CurrentSessionId { get; set; }
    public long CurrentConnectionGeneration { get; set; }
    public required string SelectionSnapshotJson { get; set; }
    public string? TargetSnapshotJson { get; set; }
    public required byte[] Assignment { get; set; }
    public int AssignmentProtectionVersion { get; set; }
    public string Phase { get; set; } = "PREPARING";
    public DateTimeOffset PreparedAt { get; set; }
    public DateTimeOffset PreparationDeadline { get; set; }
    public DateTimeOffset? AcknowledgementDeadline { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public DateTimeOffset? AcceptedAt { get; set; }
    public DateTimeOffset? ExecutionDeadline { get; set; }
    public DateTimeOffset? ReconnectDeadline { get; set; }
    public DateTimeOffset? TerminalReceivedAt { get; set; }
    public DateTimeOffset? OutputDeadline { get; set; }
    public DateTimeOffset? ReleasedAt { get; set; }
}

public sealed class RuntimeBuildResult
{
    public long BuildId { get; set; }
    public required byte[] Evidence { get; set; }
    public required string SemanticHash { get; set; }
    public required string OriginalOutcome { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
}

// Append-only, lease-fenced process observations, separate from the final result/output receipt.
public sealed class RuntimeBuildObservation
{
    public long BuildId { get; set; }
    public int Ordinal { get; set; }
    public required string Phase { get; set; }
    public int StepIndex { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public int? ExitCode { get; set; }
    public bool? TimedOut { get; set; }
    public bool? Skipped { get; set; }
    public byte[]? ContainerEvidence { get; set; }
}

public sealed class RuntimeBuildPayload
{
    public long BuildId { get; set; }
    public required string Sha256 { get; set; }
    public required string ObjectKey { get; set; }
    public long Size { get; set; }
}

public sealed class RuntimeArtifactUpload
{
    public long BuildId { get; set; }
    public required string Sha256 { get; set; }
    public required string ObjectKey { get; set; }
    public long Size { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class RuntimeBuildArtifact
{
    public long BuildId { get; set; }
    public int Ordinal { get; set; }
    public required string Path { get; set; }
    public required string Sha256 { get; set; }
    public long Size { get; set; }
    public DateTimeOffset? ModifiedAt { get; set; }
}

public sealed class RuntimeBuildOutput
{
    public long BuildId { get; set; }
    public string State { get; set; } = "OPEN";
    public long NextPosition { get; set; } = 1;
    public long RetentionEpoch { get; set; }
}

// Availability metadata does not rewrite the immutable producer range or its published position.
public sealed class RuntimeOutputExpiry
{
    public Guid RangeId { get; set; }
    public DateTimeOffset ExpiredAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

// Pending private output/artifact objects remain until reference adoption or retryable deletion.
public sealed class RuntimeOutputDeletion
{
    public string ObjectKey { get; set; } = "";
    public long BuildId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class RuntimeOutputStream
{
    public long BuildId { get; set; }
    public int StepIndex { get; set; }
    public int Stream { get; set; }
    public decimal CommittedSequence { get; set; }
    public decimal CommittedBytes { get; set; }
    public decimal? FinalSequence { get; set; }
    public decimal? FinalBytes { get; set; }
    public bool TailStarted { get; set; }
}

public sealed class RuntimeOutputRange
{
    public Guid Id { get; set; }
    public long BuildId { get; set; }
    public int StepIndex { get; set; }
    public int Stream { get; set; }
    public decimal Start { get; set; }
    public decimal? End { get; set; }
    public decimal ByteStart { get; set; }
    public decimal? ByteEnd { get; set; }
    public string Kind { get; set; } = "HEAD";
    public string? Sha256 { get; set; }
    public string? ObjectKey { get; set; }
    public string? Reason { get; set; }
    public long? Position { get; set; }
    public long ReceiptOrder { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
}
