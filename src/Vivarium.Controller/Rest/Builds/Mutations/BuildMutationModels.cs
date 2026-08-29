namespace Vivarium.Controller.Rest.Builds.Mutations;

public sealed record BuildSubmissionRequest(
    string? Project,
    string? Configuration,
    byte[]? DefinitionSnapshot,
    string? BlobStagingId,
    IReadOnlyList<BuildSubmissionCellRequest>? Cells);

public sealed record BuildSubmissionCellRequest(
    string? Name,
    string? AgentExpression,
    string? Rid,
    int QueueTimeoutSeconds,
    BuildSubmissionAssignmentRequest? Assignment);

public sealed record BuildSubmissionAssignmentRequest(
    IReadOnlyList<BuildSubmissionPayloadRequest>? Payload,
    IReadOnlyList<BuildSubmissionStepRequest>? Steps,
    IReadOnlyList<string>? Collect,
    string? OnFail,
    IReadOnlyDictionary<string, string>? Parameters);

public sealed record BuildSubmissionPayloadRequest(
    string? Sha256,
    string? FileName,
    bool Archive,
    string? UnpackTo);

public sealed record BuildSubmissionStepRequest(
    string? Program,
    IReadOnlyList<string>? Args,
    IReadOnlyDictionary<string, string>? Env,
    string? Cwd,
    int TimeoutSeconds,
    string? Policy,
    bool ExpectedReboot);

public sealed record BuildCancellationRequest(string? Reason);

internal sealed record BuildSubmissionResponse(
    int Status,
    string Json,
    string ETag,
    string Location);
