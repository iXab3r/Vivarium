using Vivarium.Contracts.V1;
using Vivarium.Controller.Auditing;
using Vivarium.Controller.Builds;
using Vivarium.Controller.Security;

namespace Vivarium.Controller.Management;

/// <summary>
/// Controller-owned matrix stop operation: commit the whole aggregate first, then project the
/// persisted cancellation intent into live BuildTracker sessions. A crash between those phases is
/// safe because BuildTracker restores CANCEL_REQUESTED children and resends on reconnect.
/// </summary>
public sealed class MatrixBuildCancellationService
{
    public const string DefaultReason = "matrix build cancellation requested";

    private readonly MatrixBuildStore matrixBuilds;
    private readonly BuildTracker builds;
    private readonly BuildQueueService queue;
    private readonly TimeProvider timeProvider;
    private readonly ManagementCommandAuthorizer? authorization;

    public MatrixBuildCancellationService(
        MatrixBuildStore matrixBuilds,
        BuildTracker builds,
        BuildQueueService queue,
        TimeProvider? timeProvider = null,
        ManagementCommandAuthorizer? authorization = null)
    {
        this.matrixBuilds = matrixBuilds;
        this.builds = builds;
        this.queue = queue;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.authorization = authorization;
    }

    public async Task<BuildSnapshot?> CancelAsync(
        ManagementRequestContext context,
        string matrixBuildId,
        string? reason = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        await (authorization ?? throw new InvalidOperationException(
                "application command authorization is not configured"))
            .DemandAsync(
                context,
                ManagementPermission.BuildCancel,
                "matrix-build.cancel",
                "matrix-build",
                matrixBuildId);
        var effectiveReason = string.IsNullOrWhiteSpace(reason) ? DefaultReason : reason.Trim();
        var now = timeProvider.GetUtcNow();
        var committed = await matrixBuilds.CancelAsync(
            matrixBuildId,
            effectiveReason,
            now,
            AuditEventDraft.Create(
                context,
                now,
                "matrix-build.cancel",
                "matrix-build",
                matrixBuildId),
            context);
        if (committed is null)
        {
            return null;
        }

        foreach (var child in committed.ActiveChildren)
        {
            await builds.CancelBuildFromControllerAsync(child.BuildId, child.Reason);
        }

        queue.NotifyChanged();
        return await matrixBuilds.GetSnapshotAsync(matrixBuildId)
            ?? throw new InvalidDataException(
                $"matrix build '{matrixBuildId}' disappeared after durable cancellation");
    }
}
