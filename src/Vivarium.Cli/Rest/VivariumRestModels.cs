using System.Text.Json;

namespace Vivarium.Cli.Rest;

internal sealed record RestSystemResource(
    string Id,
    string Url,
    string ApiVersion,
    string Status,
    string ControllerVersion);

internal sealed record RestBlobDescriptor(string Sha256, long Size);

internal sealed record RestBlobUploadPlanRequest(
    string ProjectId,
    IReadOnlyList<RestBlobDescriptor> Blobs);

internal sealed record RestBlobUploadPlan(
    string Id,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<RestBlobUploadPlanItem> Items);

internal sealed record RestBlobUploadPlanItem(
    string Sha256,
    long Size,
    bool UploadRequired,
    string UploadUrl);

internal sealed record RestBuildSubmissionRequest(
    string Project,
    string Configuration,
    byte[] DefinitionSnapshot,
    string BlobStagingId,
    IReadOnlyList<RestBuildCellRequest> Cells);

internal sealed record RestBuildCellRequest(
    string Name,
    string AgentExpression,
    string Rid,
    int QueueTimeoutSeconds,
    RestBuildAssignmentRequest Assignment);

internal sealed record RestBuildAssignmentRequest(
    IReadOnlyList<RestBuildPayloadRequest> Payload,
    IReadOnlyList<RestBuildStepRequest> Steps,
    IReadOnlyList<string> Collect,
    string OnFail,
    IReadOnlyDictionary<string, string> Parameters);

internal sealed record RestBuildPayloadRequest(
    string Sha256,
    string FileName,
    bool Archive,
    string UnpackTo);

internal sealed record RestBuildStepRequest(
    string Program,
    IReadOnlyList<string> Args,
    IReadOnlyDictionary<string, string> Env,
    string Cwd,
    int TimeoutSeconds,
    string Policy,
    bool ExpectedReboot);

internal sealed record RestBuildCancellationRequest(string Reason);

internal sealed record RestAgentPackageResource(
    string PackageId,
    string Version,
    string Rid,
    string Sha256,
    long Size,
    DateTimeOffset CreatedAt,
    string Source);

internal sealed record RestAgentUpgradeRequest(
    string Reason,
    int? TimeoutSeconds);

internal sealed record RestAgentUpgradeCancellationRequest(string Reason);

internal sealed record RestAgentUpgradeEventResource(
    long Sequence,
    string Phase,
    string Code,
    long? ConnectionGeneration,
    string? PackageSha256,
    DateTimeOffset CreatedAt);

internal sealed record RestAgentUpgradeOperationResource(
    string OperationId,
    string AgentId,
    RestAgentPackageResource Package,
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
    IReadOnlyList<RestAgentUpgradeEventResource> Events);

internal sealed record RestBuildResource(
    string Id,
    string Url,
    string Project,
    string Configuration,
    string State,
    string? Outcome,
    bool CancellationRequested,
    IReadOnlyList<RestBuildCellResource> Children,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string RuntimeRevision);

internal sealed record RestBuildCellResource(
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
    RestAssignedAgentResource? AssignedAgent,
    IReadOnlyList<RestBuildStepResource> Steps,
    IReadOnlyList<RestBuildArtifactResource> Artifacts);

internal sealed record RestAssignedAgentResource(
    string Id,
    string Name,
    IReadOnlyDictionary<string, string> ReportedParameters,
    IReadOnlyDictionary<string, string> CustomParameters,
    IReadOnlyDictionary<string, string> EffectiveParameters);

internal sealed record RestBuildStepResource(
    int Index,
    int ExitCode,
    bool TimedOut,
    bool Skipped);

internal sealed record RestBuildArtifactResource(
    int Ordinal,
    string Path,
    string Sha256,
    long Size,
    string DownloadUrl);

internal sealed record RestResourceReference(
    string Type,
    string Id,
    string? Url);

internal sealed record RestEventEnvelope(
    string Id,
    long Sequence,
    DateTimeOffset OccurredAt,
    string Type,
    RestResourceReference Resource,
    string? CorrelationId,
    JsonElement Data,
    string? ConfigurationRevision,
    string? ObservationRevision,
    string? RuntimeRevision);

internal sealed record RestBuildWatchUpdate(
    string EventId,
    RestBuildResource Build,
    string? ETag);

internal sealed record RestResourceResponse<T>(
    T Resource,
    string? ETag,
    Uri? Location);

internal sealed record RestProblemResponse(
    string? Type,
    string? Title,
    int? Status,
    string? Detail,
    string? Code,
    string? CorrelationId);

internal sealed class VivariumRestApiException(
    System.Net.HttpStatusCode statusCode,
    string code,
    string message,
    string? correlationId = null)
    : HttpRequestException(message, inner: null, statusCode)
{
    public string Code { get; } = code;

    public string? CorrelationId { get; } = correlationId;
}
