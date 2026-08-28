using Vivarium.Contracts.V1;
using Vivarium.Controller.Builds;

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

    public MatrixBuildCancellationService(
        MatrixBuildStore matrixBuilds,
        BuildTracker builds,
        BuildQueueService queue,
        TimeProvider? timeProvider = null)
    {
        this.matrixBuilds = matrixBuilds;
        this.builds = builds;
        this.queue = queue;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<BuildSnapshot?> CancelAsync(string matrixBuildId, string? reason = null)
    {
        var effectiveReason = string.IsNullOrWhiteSpace(reason) ? DefaultReason : reason.Trim();
        var committed = await matrixBuilds.CancelAsync(
            matrixBuildId, effectiveReason, timeProvider.GetUtcNow());
        if (committed is null)
        {
            return null;
        }

        foreach (var child in committed.ActiveChildren)
        {
            await builds.CancelBuildAsync(child.BuildId, child.Reason);
        }

        queue.NotifyChanged();
        return await matrixBuilds.GetSnapshotAsync(matrixBuildId)
            ?? throw new InvalidDataException(
                $"matrix build '{matrixBuildId}' disappeared after durable cancellation");
    }
}
