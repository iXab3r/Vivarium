using System.Security.Cryptography;
using System.Text;
using Vivarium.Controller.Auditing;
using Vivarium.Controller.Security;

namespace Vivarium.Controller.Blobs.Access;

public sealed class BlobAccessService(
    BlobAccessStore store,
    BlobStore blobs,
    ManagementCommandAuthorizer authorization,
    AuditEventStore audits,
    TimeProvider timeProvider) : IBlobObjectAccess
{
    public const string CreatePlanOperationKind = "blob-upload-plan.create";
    public static readonly TimeSpan ArtifactUploadLifetime = TimeSpan.FromHours(1);

    public async Task<BlobUploadPlan> CreateUploadPlanAsync(
        ManagementRequestContext context,
        string projectId,
        IReadOnlyList<BlobDescriptor> requestedItems,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        await authorization.DemandAsync(
            context,
            ManagementPermission.BlobDiscover,
            CreatePlanOperationKind,
            "project",
            projectId);
        cancellationToken.ThrowIfCancellationRequested();
        BlobAccessValidation.ValidatePrincipal(context.Principal);
        projectId = BlobAccessValidation.ValidateProjectId(projectId);
        if (string.IsNullOrWhiteSpace(context.RequestId) || context.RequestId.Length > 256)
        {
            throw Validation(
                "idempotency_key_invalid",
                "A bounded Idempotency-Key is required for blob upload plans.");
        }

        var items = BlobAccessValidation.NormalizeDescriptors(requestedItems);
        var requestHash = HashRequest(projectId, items);
        var granted = await store.FindExistingGrantsAsync(context.Principal, projectId, items);
        var presentGrants = granted
            .Where(blobs.Contains)
            .ToHashSet(StringComparer.Ordinal);
        var now = timeProvider.GetUtcNow();
        return await store.CreatePlanAsync(
            context,
            projectId,
            items,
            requestHash,
            now,
            now.Add(BlobAccessLimits.PlanLifetime),
            presentGrants);
    }

    public async Task<BlobUploadOutcome> UploadStagedAsync(
        ManagementRequestContext context,
        string stagingId,
        string sha256,
        Stream body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(body);
        await authorization.DemandAsync(
            context,
            ManagementPermission.BlobWrite,
            "blob-staging.upload",
            "blob-upload-plan",
            BoundAuditTarget(stagingId));
        BlobAccessValidation.ValidatePrincipal(context.Principal);
        BlobAccessValidation.RequireBounded(stagingId, 64, "staging ID");
        BlobAccessValidation.ValidateSha256(sha256);
        var item = await store.GetUploadItemAsync(context.Principal, stagingId, sha256)
            ?? throw NotFound();
        var now = timeProvider.GetUtcNow();
        if (item.ExpiresAt <= now)
        {
            throw Expired();
        }

        var write = await blobs.PutWithDispositionAsync(
            sha256,
            body,
            item.Size,
            BlobAccessLimits.MaximumBlobBytes,
            cancellationToken);
        if (write is BlobPutResult.DigestMismatch or
            BlobPutResult.SizeMismatch or
            BlobPutResult.SizeLimitExceeded)
        {
            if (item.Ready)
            {
                throw Conflict(
                    "blob_upload_replay_conflict",
                    "The staged blob was already completed with different bytes.");
            }

            throw Validation(
                write == BlobPutResult.DigestMismatch
                    ? "blob_digest_mismatch"
                    : "blob_size_mismatch",
                "The request body does not match its declared blob digest and size.");
        }

        return await store.CompleteUploadAsync(context, item, timeProvider.GetUtcNow());
    }

    public async Task<bool> CanReadAssignmentAsync(
        ManagementRequestContext context,
        BlobAssignmentReadRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        await authorization.DemandAsync(
            context,
            ManagementPermission.BlobRead,
            "blob-assignment.read",
            "build",
            request.BuildId);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsExactAgent(context.Principal, request.AgentId))
        {
            return false;
        }

        return await store.CanReadAssignmentAsync(request);
    }

    public async Task<BlobArtifactUploadGrant?> StageArtifactUploadAsync(
        ManagementRequestContext context,
        BlobArtifactUploadRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        await authorization.DemandAsync(
            context,
            ManagementPermission.BlobWrite,
            "blob-artifact.stage",
            "build",
            request.BuildId);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsExactAgent(context.Principal, request.AgentId))
        {
            return null;
        }

        return await store.StageArtifactUploadAsync(
            request,
            request.Now.Add(ArtifactUploadLifetime));
    }

    public async Task<BlobUploadOutcome> UploadArtifactAsync(
        ManagementRequestContext context,
        BlobArtifactUploadRequest request,
        Stream body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        var grant = await StageArtifactUploadAsync(context, request, cancellationToken)
            ?? throw new BlobAccessException(
                BlobAccessFailure.NotFound,
                "blob_artifact_upload_not_found",
                "The build is not owned by this Agent session.");
        var write = await blobs.PutWithDispositionAsync(
            request.Sha256,
            body,
            request.Size,
            BlobAccessLimits.MaximumBlobBytes,
            cancellationToken);
        if (write is BlobPutResult.DigestMismatch or
            BlobPutResult.SizeMismatch or
            BlobPutResult.SizeLimitExceeded)
        {
            if (grant.Replayed)
            {
                throw Conflict(
                    "blob_artifact_upload_replay_conflict",
                    "The artifact upload was already staged with different bytes.");
            }

            throw Validation(
                write == BlobPutResult.DigestMismatch
                    ? "blob_digest_mismatch"
                    : "blob_size_mismatch",
                "The artifact body does not match its declared digest and size.");
        }

        return await store.CompleteArtifactUploadAsync(
            context,
            grant,
            timeProvider.GetUtcNow());
    }

    public async Task<BlobDescriptor?> ResolveHumanArtifactAsync(
        BlobHumanArtifactReadRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await authorization.DemandAsync(
            request.Context,
            ManagementPermission.ArtifactRead,
            "blob-artifact.read",
            "build",
            request.BuildId);
        cancellationToken.ThrowIfCancellationRequested();
        var artifact = await store.ResolveHumanArtifactAsync(request);
        if (artifact is not null)
        {
            await audits.AppendAsync(AuditEventDraft.Create(
                request.Context,
                timeProvider.GetUtcNow(),
                "blob-artifact.read",
                "build-artifact",
                $"{BoundAuditTarget(request.BuildId)}:{BoundAuditTarget(request.ArtifactId)}"));
        }

        return artifact;
    }

    internal Task AuditDataPlaneMutationAsync(
        ManagementRequestContext context,
        string action,
        string targetType,
        string targetId,
        AuditOutcome outcome,
        string reasonCode) =>
        audits.AppendAsync(AuditEventDraft.Create(
            context,
            timeProvider.GetUtcNow(),
            action,
            targetType,
            BoundAuditTarget(targetId),
            outcome,
            BoundReasonCode(reasonCode)));

    private static bool IsExactAgent(ManagementPrincipal principal, string agentId) =>
        string.Equals(principal.ActorType, "agent", StringComparison.Ordinal) &&
        string.Equals(principal.ActorId, agentId, StringComparison.Ordinal);

    private static string HashRequest(string projectId, IReadOnlyList<BlobDescriptor> items)
    {
        var canonical = new StringBuilder(projectId.Length + items.Count * 96);
        canonical.Append(projectId).Append('\n');
        foreach (var item in items)
        {
            canonical.Append(item.Sha256).Append(':').Append(item.Size).Append('\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    private static string BoundAuditTarget(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? "(invalid)"
            : value.Length <= 256 ? value : value[..256];

    private static string BoundReasonCode(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? "request_failed"
            : value.Length <= 128 ? value : value[..128];

    private static BlobAccessException Validation(string code, string message) =>
        new(BlobAccessFailure.Validation, code, message);

    private static BlobAccessException NotFound() =>
        new(
            BlobAccessFailure.NotFound,
            "blob_staging_not_found",
            "The blob staging resource does not exist or is not visible to this principal.");

    private static BlobAccessException Expired() =>
        new(BlobAccessFailure.Expired, "blob_staging_expired", "The blob staging resource has expired.");

    private static BlobAccessException Conflict(string code, string message) =>
        new(BlobAccessFailure.Conflict, code, message);
}
