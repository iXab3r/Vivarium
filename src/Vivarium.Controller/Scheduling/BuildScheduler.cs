using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Vivarium.Contracts.V1;
using Vivarium.Controller.Agents;
using Vivarium.Controller.Builds;

namespace Vivarium.Controller.Scheduling;

/// <summary>
/// TeamCity-style dispatcher: queue order is stable FIFO, but the first runnable build wins so an
/// incompatible head cannot block independent capacity. A queue claim remains durable until the
/// accepting agent session acknowledges ownership.
/// </summary>
public sealed class BuildScheduler : BackgroundService
{
    private readonly BuildQueueStore store;
    private readonly BuildQueueService queue;
    private readonly AgentRegistry agents;
    private readonly AgentLifecycleCoordinator lifecycle;
    private readonly BuildTracker builds;
    private readonly ILogger<BuildScheduler> log;
    private readonly TimeProvider timeProvider;

    public BuildScheduler(
        BuildQueueStore store,
        BuildQueueService queue,
        AgentRegistry agents,
        AgentLifecycleCoordinator lifecycle,
        BuildTracker builds,
        ILogger<BuildScheduler> log,
        TimeProvider? timeProvider = null)
    {
        this.store = store;
        this.queue = queue;
        this.agents = agents;
        this.lifecycle = lifecycle;
        this.builds = builds;
        this.log = log;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        agents.Changed += OnCapacityChanged;
        builds.QueueChanged += OnQueueChanged;
        queue.NotifyChanged();
        try
        {
            while (await queue.WaitForWorkAsync(stoppingToken))
            {
                try
                {
                    await DispatchAvailableAsync(stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    log.LogError(ex, "build scheduler pass failed");
                    await Task.Delay(TimeSpan.FromMilliseconds(250), stoppingToken);
                    queue.NotifyChanged();
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            agents.Changed -= OnCapacityChanged;
            builds.QueueChanged -= OnQueueChanged;
        }
    }

    internal async Task DispatchAvailableAsync(CancellationToken cancellationToken)
    {
        var pending = await store.ListPendingAsync();

        // An unprepared claim has no possible wire delivery. It is safe to release after a crash and
        // retains its original queue_id, so FIFO order is unchanged.
        foreach (var item in pending.Where(item =>
                     item.State == BuildQueueItemState.Claimed && !item.DispatchPrepared))
        {
            if (item.ClaimedAgentId != null &&
                await store.TryRequeueDispatchAsync(item.BuildId, item.ClaimedAgentId))
            {
                queue.NotifyChanged();
            }
        }

        pending = await store.ListPendingAsync();
        var snapshots = await agents.GetSnapshotsAsync();

        // Prepared claims are recovered before new work. They are never requeued merely because a
        // send failed: only a fenced Hello/ACK can prove whether the prior delivery was accepted.
        foreach (var item in pending.Where(item =>
                     item.State == BuildQueueItemState.Claimed && item.DispatchPrepared))
        {
            await TryDeliverPreparedAsync(item, snapshots);
        }

        // Scan the whole FIFO once. Busy/incompatible head rows remain in place while later builds
        // may use unrelated capacity, matching TeamCity's global queue behavior.
        foreach (var item in pending.Where(item => item.State == BuildQueueItemState.Queued))
        {
            var candidate = snapshots
                .Where(IsDispatchEligible)
                .Where(agent => AgentCompatibilityMatcher.Match(
                    item.AgentExpression, agent.Name, agent.Parameters).Compatible)
                .OrderBy(agent => agent.LastCommunication)
                .ThenBy(agent => agent.AgentId, StringComparer.Ordinal)
                .FirstOrDefault();
            if (candidate == null)
            {
                continue;
            }

            await TryStartDispatchAsync(item, candidate, cancellationToken);
        }
    }

    private async Task TryStartDispatchAsync(
        BuildQueueItem item,
        AgentSnapshot candidate,
        CancellationToken cancellationToken)
    {
        var agentId = candidate.AgentId;
        AgentConnectionHandle? connection;
        await using (await lifecycle.AcquireAsync(agentId, cancellationToken))
        {
            if (!await store.TryClaimAsync(item.BuildId, agentId, timeProvider.GetUtcNow()))
            {
                return;
            }

            if (!agents.TryBeginBuild(
                    agentId, item.BuildId, candidate.ParameterGeneration,
                    out connection, out var reason))
            {
                await store.TryRequeueDispatchAsync(item.BuildId, agentId);
                log.LogDebug("dispatch of build {BuildId} lost capacity: {Reason}", item.BuildId, reason);
                return;
            }

            if (!await store.TryPrepareDispatchAsync(
                    item.BuildId,
                    agentId,
                    connection!.SessionId,
                    timeProvider.GetUtcNow(),
                    candidate.Name,
                    candidate.ReportedParameters,
                    candidate.CustomParameters))
            {
                agents.EndBuild(connection, item.BuildId);
                await store.TryRequeueDispatchAsync(item.BuildId, agentId);
                return;
            }
        }

        if (!builds.AttachPreparedBuild(agentId, item.Assignment))
        {
            log.LogError(
                "prepared build {BuildId} conflicts with the in-memory build projection",
                item.BuildId);
            return;
        }

        await builds.RefreshCancellationAsync(item.BuildId);
        if (!await builds.PrepareAssignmentAttemptAsync(item.BuildId, connection!))
        {
            await builds.OnSessionLostAsync(new AgentSessionLoss(
                connection.AgentId, connection.SessionId, item.BuildId));
            log.LogWarning(
                "prepared build {BuildId} could not persist its assignment attempt",
                item.BuildId);
        }
        else if (!agents.TrySend(connection!, new ControllerMsg { Build = item.Assignment }))
        {
            await builds.OnSessionLostAsync(new AgentSessionLoss(
                connection.AgentId, connection.SessionId, item.BuildId));
            log.LogDebug(
                "prepared build {BuildId} will wait for a fresh session after fenced send failed",
                item.BuildId);
        }
        else
        {
            builds.OnAssignmentSent(item.BuildId, connection);
        }

        queue.NotifyChanged();
    }

    private async Task TryDeliverPreparedAsync(
        BuildQueueItem item,
        IReadOnlyList<AgentSnapshot> snapshots)
    {
        var agentId = item.ClaimedAgentId;
        if (agentId == null)
        {
            return;
        }

        var snapshot = snapshots.FirstOrDefault(agent => agent.AgentId == agentId);
        if (snapshot is not { Connected: true, Reconciled: true })
        {
            return;
        }

        AgentConnectionHandle? connection;
        if (snapshot.CurrentBuildId == item.BuildId)
        {
            if (!agents.TryGetBuildConnection(agentId, item.BuildId, out connection))
            {
                return;
            }
        }
        else if (snapshot.Activity == AgentActivity.Idle &&
                 snapshot.Authorization == AgentAuth.Authorized &&
                 snapshot.Enabled &&
                 AgentCompatibilityMatcher.Match(
                     item.AgentExpression, snapshot.Name, snapshot.Parameters).Compatible)
        {
            if (!agents.TryBeginBuild(
                    agentId, item.BuildId, snapshot.ParameterGeneration,
                    out connection, out _))
            {
                return;
            }
        }
        else
        {
            return;
        }

        if (!builds.AttachPreparedBuild(agentId, item.Assignment))
        {
            return;
        }

        await builds.RefreshCancellationAsync(item.BuildId);
        await SendPreparedAsync(item, connection!);
    }

    private async Task SendPreparedAsync(
        BuildQueueItem item,
        AgentConnectionHandle connection)
    {
        // Persist the exact session fence before putting the assignment on its immutable outbox.
        // A crash on either side of TrySend leaves a claimed row that the next reconciled session
        // can safely retry idempotently.
        if (!await store.RecordDispatchAttemptAsync(
                item.BuildId,
                connection.AgentId,
                item.DispatchSessionId,
                connection.SessionId,
                timeProvider.GetUtcNow()))
        {
            return;
        }

        if (!await builds.PrepareAssignmentAttemptAsync(item.BuildId, connection))
        {
            await builds.OnSessionLostAsync(new AgentSessionLoss(
                connection.AgentId, connection.SessionId, item.BuildId));
            log.LogWarning(
                "prepared build {BuildId} could not persist its assignment attempt",
                item.BuildId);
        }
        else if (!agents.TrySend(connection, new ControllerMsg { Build = item.Assignment }))
        {
            await builds.OnSessionLostAsync(new AgentSessionLoss(
                connection.AgentId, connection.SessionId, item.BuildId));
            log.LogDebug(
                "prepared build {BuildId} will wait for a fresh session after fenced send failed",
                item.BuildId);
        }
        else
        {
            builds.OnAssignmentSent(item.BuildId, connection);
        }
    }

    private static bool IsDispatchEligible(AgentSnapshot agent) =>
        agent.Connected &&
        agent.Reconciled &&
        !agent.Quarantined &&
        agent.OperationalHealth == AgentOperationalHealth.Healthy &&
        !agent.ParametersChanging &&
        agent.Authorization == AgentAuth.Authorized &&
        agent.Enabled &&
        agent.Activity == AgentActivity.Idle;

    private void OnCapacityChanged() => queue.NotifyChanged();

    private void OnQueueChanged() => queue.NotifyChanged();
}
