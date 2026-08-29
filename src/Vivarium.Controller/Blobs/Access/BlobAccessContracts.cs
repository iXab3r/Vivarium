using Microsoft.Data.Sqlite;
using Vivarium.Controller.Security;

namespace Vivarium.Controller.Blobs.Access;

public static class BlobAccessLimits
{
    public const int MaximumPlanItems = 256;
    public const long MaximumBlobBytes = 2L * 1024 * 1024 * 1024;
    public const long MaximumPlanBytes = 8L * 1024 * 1024 * 1024;
    public static readonly TimeSpan PlanLifetime = TimeSpan.FromMinutes(15);
}

public sealed record BlobDescriptor(string Sha256, long Size);

public sealed record BlobUploadPlanItem(
    string Sha256,
    long Size,
    bool UploadRequired,
    string UploadUrl);

public sealed record BlobUploadPlan(
    string Id,
    string ProjectId,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<BlobUploadPlanItem> Items,
    bool Replayed);

public enum BlobUploadOutcome
{
    Uploaded,
    ExactReplay,
}

public enum BlobAccessFailure
{
    Validation,
    NotFound,
    Expired,
    Conflict,
}

public sealed class BlobAccessException(
    BlobAccessFailure failure,
    string code,
    string message) : Exception(message)
{
    public BlobAccessFailure Failure { get; } = failure;
    public string Code { get; } = code;
}

public sealed record BlobBuildAttachmentRequest(
    ManagementPrincipal Principal,
    string OperationKind,
    string RequestId,
    string StagingId,
    string ProjectId,
    string MatrixBuildId,
    IReadOnlyList<string> DistinctAssignmentSha256,
    DateTimeOffset Now);

public enum BlobBuildAttachmentOutcome
{
    Attached,
    ExactReplay,
}

/// <summary>
/// Participates synchronously in the matrix-build creation transaction. The caller inserts the
/// matrix and child rows first, invokes this participant, and commits only if attachment succeeds.
/// </summary>
public interface IBlobBuildAttachmentParticipant
{
    BlobBuildAttachmentOutcome Attach(
        SqliteConnection connection,
        SqliteTransaction transaction,
        BlobBuildAttachmentRequest request);
}

public sealed record BlobAssignmentReadRequest(
    string AgentId,
    string SessionId,
    string BuildId,
    string Sha256,
    DateTimeOffset Now);

public sealed record BlobArtifactUploadRequest(
    string AgentId,
    string SessionId,
    string BuildId,
    string Sha256,
    long Size,
    DateTimeOffset Now);

public sealed record BlobArtifactUploadGrant(
    string BuildId,
    string Sha256,
    long Size,
    string AgentId,
    string OwnerSessionId,
    long ConnectionGeneration,
    DateTimeOffset ExpiresAt,
    bool Replayed);

public sealed record BlobHumanArtifactReadRequest(
    ManagementRequestContext Context,
    string BuildId,
    string ArtifactId);

/// <summary>
/// Object-access boundary for the blob data plane. Assignment reads, artifact uploads, and human
/// artifact reads remain distinct checks; knowledge of a digest is never one of them.
/// </summary>
public interface IBlobObjectAccess
{
    Task<bool> CanReadAssignmentAsync(
        ManagementRequestContext context,
        BlobAssignmentReadRequest request,
        CancellationToken cancellationToken = default);

    Task<BlobArtifactUploadGrant?> StageArtifactUploadAsync(
        ManagementRequestContext context,
        BlobArtifactUploadRequest request,
        CancellationToken cancellationToken = default);

    Task<BlobDescriptor?> ResolveHumanArtifactAsync(
        BlobHumanArtifactReadRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record BlobArtifactAttachment(
    string ArtifactId,
    string RelativePath,
    string Sha256,
    long Size);

public sealed record BlobArtifactAttachmentRequest(
    string BuildId,
    string AgentId,
    string OwnerSessionId,
    long ConnectionGeneration,
    IReadOnlyList<BlobArtifactAttachment> Artifacts,
    DateTimeOffset Now);

public enum BlobArtifactAttachmentOutcome
{
    Attached,
    ExactReplay,
}

/// <summary>
/// Participates synchronously in terminal-result acceptance so artifact references and the first
/// accepted result commit together.
/// </summary>
public interface IBlobArtifactAttachmentParticipant
{
    BlobArtifactAttachmentOutcome Attach(
        SqliteConnection connection,
        SqliteTransaction transaction,
        BlobArtifactAttachmentRequest request);
}
