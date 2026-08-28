using System.Collections.Concurrent;
using System.Text;
using Vivarium.Contracts.V1;
using Vivarium.Controller.Agents;

namespace Vivarium.Controller.Builds;

public enum TrackedBuildState
{
    Queued,
    Running,
    CancelRequested,
    Finished,
}

public sealed record TrackedBuildSnapshot(
    string BuildId,
    string AgentId,
    TrackedBuildState State,
    string? CancellationReason);

/// <summary>
/// Durable build ownership and cancellation. An authorized, enabled agent owns at most one build;
/// reconnects and controller restarts preserve ownership, while superseded sessions are fenced out.
/// </summary>
public sealed class BuildTracker
{
    private sealed record StartupReconnectCandidate(
        string BuildId,
        string AgentId,
        string? OwnerSessionId);

    private sealed class PendingBuild
    {
        public required string AgentId { get; init; }
        public required BuildAssignment Assignment { get; init; }
        public TaskCompletionSource<BuildResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Dispatched { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public StringBuilder Log { get; } = new();
        public object Gate { get; } = new();
        public TrackedBuildState State { get; set; } = TrackedBuildState.Running;
        public string? CancellationReason { get; set; }
        public string? AssignmentSessionId { get; set; }
    }

    private readonly AgentRegistry registry;
    private readonly BuildStore? store;
    private readonly BuildQueueStore? queueStore;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan reconnectGrace;
    private readonly ConcurrentDictionary<string, PendingBuild> builds = new();
    private readonly ConcurrentDictionary<string, BuildResult> completed = new();
    private readonly object startupReconnectGate = new();
    private List<StartupReconnectCandidate> startupReconnectCandidates = [];
    private bool startupReconnectGraceStarted;

    public BuildTracker(
        AgentRegistry registry,
        BuildStore? store = null,
        BuildQueueStore? queueStore = null,
        TimeProvider? timeProvider = null,
        TimeSpan? reconnectGrace = null)
    {
        this.registry = registry;
        this.store = store;
        this.queueStore = queueStore;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.reconnectGrace = reconnectGrace ?? TimeSpan.FromSeconds(60);
    }

    public async Task InitializeAsync()
    {
        if (store == null)
        {
            return;
        }

        var activeBuilds = await store.ListAssignedActiveAsync();
        foreach (var build in activeBuilds)
        {
            var pending = new PendingBuild
            {
                AgentId = build.AgentId!,
                Assignment = build.Assignment,
                State = build.State,
                CancellationReason = build.CancellationReason,
            };
            var queued = queueStore == null ? null : await queueStore.GetAsync(build.BuildId);
            if (queued?.State != BuildQueueItemState.Claimed)
            {
                pending.Dispatched.TrySetResult(true);
            }

            builds.TryAdd(build.BuildId, pending);
        }

        lock (startupReconnectGate)
        {
            if (!startupReconnectGraceStarted)
            {
                startupReconnectCandidates.AddRange(activeBuilds
                    .Where(build => build.ReconnectDeadline == null)
                    .Select(build => new StartupReconnectCandidate(
                        build.BuildId,
                        build.AgentId!,
                        build.OwnerSessionId)));
            }
        }

        var now = timeProvider.GetUtcNow();
        await SweepExpiredLeasesAsync(now);
    }

    /// <summary>
    /// Starts reconnect grace for active builds inherited from a previous controller only after
    /// Kestrel is accepting reconnects. Exact-owner CAS fencing lets a racing re-adoption win.
    /// </summary>
    public async Task ArmStartupReconnectGraceAsync()
    {
        if (store == null)
        {
            return;
        }

        StartupReconnectCandidate[] candidates;
        lock (startupReconnectGate)
        {
            if (startupReconnectGraceStarted)
            {
                return;
            }

            startupReconnectGraceStarted = true;
            candidates = [.. startupReconnectCandidates];
            startupReconnectCandidates.Clear();
        }

        var now = timeProvider.GetUtcNow();
        var deadline = now + reconnectGrace;
        foreach (var candidate in candidates)
        {
            await store.TryArmStartupReconnectGraceAsync(
                candidate.BuildId,
                candidate.AgentId,
                candidate.OwnerSessionId,
                deadline,
                now);
        }

        await SweepExpiredLeasesAsync(now);
    }

    public async Task<BuildResult> RunBuildAsync(
        string agentId,
        BuildAssignment assignment,
        CancellationToken cancellationToken)
    {
        await DispatchBuildAsync(agentId, assignment);
        return await WaitForResultAsync(assignment.BuildId, cancellationToken);
    }

    /// <summary>Durably records and sends an assignment, returning once the agent owns it.</summary>
    public async Task DispatchBuildAsync(string agentId, BuildAssignment assignment)
    {
        BuildAdmission.EnsureSupported(assignment);
        if (!registry.TryBeginBuild(
                agentId, assignment.BuildId, out var connection, out var reason))
        {
            throw new InvalidOperationException(reason);
        }

        var pending = new PendingBuild { AgentId = agentId, Assignment = assignment.Clone() };
        if (!builds.TryAdd(assignment.BuildId, pending))
        {
            registry.EndBuild(agentId, assignment.BuildId);
            throw new InvalidOperationException($"build '{assignment.BuildId}' already exists");
        }

        var persisted = false;
        try
        {
            if (store != null)
            {
                await store.CreateAsync(
                    agentId,
                    connection!.SessionId,
                    assignment,
                    timeProvider.GetUtcNow());
                persisted = true;
            }

            if (!registry.TrySend(connection!, new ControllerMsg { Build = assignment }))
            {
                throw new InvalidOperationException($"agent '{agentId}' is not connected");
            }

            OnAssignmentSent(assignment.BuildId, connection!);
            await pending.Dispatched.Task;
        }
        catch
        {
            pending.Dispatched.TrySetResult(false);
            builds.TryRemove(assignment.BuildId, out _);
            if (persisted)
            {
                await store!.DeleteAsync(assignment.BuildId);
            }

            // The current runtime session may have superseded the dispatch handle while the durable
            // create was in flight. Once the authoritative row is gone, release that occupancy by
            // build identity instead of leaving the newer session permanently busy.
            registry.EndBuild(agentId, assignment.BuildId);

            throw;
        }
    }

    public async Task<bool> CancelBuildAsync(string buildId, string reason)
    {
        if (!builds.TryGetValue(buildId, out var build))
        {
            return false;
        }

        string? effectiveReason;
        lock (build.Gate)
        {
            if (build.State == TrackedBuildState.Finished)
            {
                return false;
            }

            if (build.State == TrackedBuildState.CancelRequested)
            {
                effectiveReason = build.CancellationReason ?? reason;
            }
            else
            {
                effectiveReason = null;
            }
        }

        if (effectiveReason is not null)
        {
            SendCancellationIfDeliverable(buildId, build, effectiveReason);
            return true;
        }

        if (store == null)
        {
            lock (build.Gate)
            {
                if (build.State == TrackedBuildState.Finished)
                {
                    return false;
                }

                if (build.State == TrackedBuildState.Running)
                {
                    build.State = TrackedBuildState.CancelRequested;
                    build.CancellationReason = reason;
                }

                effectiveReason = build.CancellationReason ?? reason;
            }

            SendCancellationIfDeliverable(buildId, build, effectiveReason);
            return true;
        }

        var request = await store.TryRequestCancellationAsync(buildId, reason);
        if (!request.Active)
        {
            return false;
        }

        effectiveReason = request.Reason ?? reason;

        lock (build.Gate)
        {
            if (build.State == TrackedBuildState.Finished)
            {
                return false;
            }

            build.State = TrackedBuildState.CancelRequested;
            build.CancellationReason = effectiveReason;
        }

        SendCancellationIfDeliverable(buildId, build, effectiveReason);
        return true;
    }

    /// <summary>
    /// Refreshes the in-memory projection after another serialized operation (matrix cancellation)
    /// changed the durable child state before the scheduler attached or sent its assignment.
    /// </summary>
    internal async Task<bool> RefreshCancellationAsync(string buildId)
    {
        if (store == null || !builds.TryGetValue(buildId, out var build))
        {
            return false;
        }

        var persisted = await store.GetAsync(buildId);
        if (persisted?.State != TrackedBuildState.CancelRequested)
        {
            return false;
        }

        var reason = persisted.CancellationReason ?? "cancellation requested";
        lock (build.Gate)
        {
            if (build.State == TrackedBuildState.Finished)
            {
                return false;
            }

            build.State = TrackedBuildState.CancelRequested;
            build.CancellationReason = reason;
        }

        SendCancellationIfDeliverable(buildId, build, reason);
        return true;
    }

    /// <summary>
    /// Records that the assignment was put onto this exact session's outbox. A cancellation can now
    /// follow it on the same ordered stream even before AssignmentAccepted arrives.
    /// </summary>
    internal void OnAssignmentSent(string buildId, AgentConnectionHandle connection)
    {
        if (!builds.TryGetValue(buildId, out var build) ||
            build.AgentId != connection.AgentId)
        {
            return;
        }

        string? cancellationReason;
        lock (build.Gate)
        {
            build.AssignmentSessionId = connection.SessionId;
            cancellationReason = build.State == TrackedBuildState.CancelRequested
                ? build.CancellationReason
                : null;
        }

        if (cancellationReason is not null)
        {
            SendCancellation(buildId, connection, cancellationReason);
        }
    }

    /// <summary>
    /// Adds the durable RUNNING row prepared by the queue scheduler to the in-memory projection.
    /// This never inserts or changes durable state.
    /// </summary>
    public bool AttachPreparedBuild(string agentId, BuildAssignment assignment)
    {
        if (builds.TryGetValue(assignment.BuildId, out var existing))
        {
            return existing.AgentId == agentId && existing.Assignment.Equals(assignment);
        }

        return builds.TryAdd(assignment.BuildId, new PendingBuild
        {
            AgentId = agentId,
            Assignment = assignment.Clone(),
        });
    }

    public async Task OnAssignmentAcceptedAsync(
        AssignmentAccepted accepted,
        AgentConnectionHandle connection)
    {
        if (!registry.IsCurrent(connection) || accepted.SessionId != connection.SessionId)
        {
            return;
        }

        if (!builds.TryGetValue(accepted.BuildId, out var build) ||
            build.AgentId != connection.AgentId)
        {
            return;
        }

        if (queueStore != null)
        {
            var queued = await queueStore.GetAsync(accepted.BuildId);
            if (queued?.State == BuildQueueItemState.Claimed)
            {
                if (queued.ClaimedAgentId != connection.AgentId ||
                    !await queueStore.CompleteDispatchAsync(
                        accepted.BuildId, connection.AgentId, connection.SessionId))
                {
                    return;
                }

                QueueChanged?.Invoke();
            }
        }

        build.Dispatched.TrySetResult(true);
        string? cancellationReason;
        lock (build.Gate)
        {
            cancellationReason = build.State == TrackedBuildState.CancelRequested
                ? build.CancellationReason
                : null;
        }

        if (cancellationReason is not null)
        {
            SendCancellation(accepted.BuildId, connection, cancellationReason);
        }
    }

    public event Action? QueueChanged;

    public async Task OnAgentReconnectedAsync(
        AgentConnectionHandle connection,
        string reportedBuildId)
    {
        var active = FindActiveBuild(connection.AgentId);
        if (active is { } owned)
        {
            string? cancellationReason;
            lock (owned.Value.Gate)
            {
                cancellationReason = owned.Value.State == TrackedBuildState.CancelRequested
                    ? owned.Value.CancellationReason
                    : null;
            }

            var persisted = store == null ? null : await store.GetAsync(owned.Key);
            var queued = queueStore == null ? null : await queueStore.GetAsync(owned.Key);
            var awaitingAcceptance = queued?.State == BuildQueueItemState.Claimed &&
                queued.DispatchPrepared;

            if (reportedBuildId == owned.Key)
            {
                if (store != null &&
                    !await TryAdoptCurrentSessionAsync(owned.Key, connection, persisted))
                {
                    // The grace deadline or a newer session won the serialized ownership race.
                    registry.Reconcile(connection, owned.Key);
                    await SweepExpiredLeasesAsync(timeProvider.GetUtcNow());
                    return;
                }

                if (awaitingAcceptance)
                {
                    if (queueStore == null)
                    {
                        return;
                    }

                    if (!await queueStore.CompleteDispatchAsync(
                            owned.Key, connection.AgentId, connection.SessionId))
                    {
                        var currentQueue = await queueStore.GetAsync(owned.Key);
                        if (currentQueue?.State != BuildQueueItemState.Removed ||
                            currentQueue.RemovalReason != "dispatched")
                        {
                            return;
                        }
                    }
                }

                // running_build_id is stronger positive ownership evidence than an assignment ACK:
                // the agent is already executing, including previous-version agents that do not send
                // AssignmentAccepted. Never resend an assignment after matching re-adoption.
                owned.Value.Dispatched.TrySetResult(true);
                registry.Reconcile(connection, owned.Key);
                if (awaitingAcceptance)
                {
                    QueueChanged?.Invoke();
                }

                if (cancellationReason != null)
                {
                    SendCancellation(owned.Key, connection.AgentId, cancellationReason);
                }

                return;
            }

            // A mismatched or empty Hello is not ownership proof. Keep the expected build occupying
            // runtime capacity while its durable reconnect deadline runs. Prepared queue work may
            // still be resent by the scheduler; acknowledged direct work must reconnect matching.
            if (store != null && persisted?.OwnerSessionId != null)
            {
                var now = timeProvider.GetUtcNow();
                await store.TryArmReconnectGraceAsync(
                    owned.Key,
                    connection.AgentId,
                    persisted.OwnerSessionId,
                    now + reconnectGrace,
                    now);
            }

            registry.Reconcile(connection, owned.Key);
            if (reportedBuildId.Length > 0)
            {
                SendCancellation(
                    reportedBuildId,
                    connection.AgentId,
                    $"controller expected build '{owned.Key}' after reconnect");
            }
            else if (awaitingAcceptance)
            {
                QueueChanged?.Invoke();
            }

            return;
        }

        if (reportedBuildId.Length > 0)
        {
            if (registry.Reconcile(connection, reportedBuildId))
            {
                SendCancellation(
                    reportedBuildId,
                    connection.AgentId,
                    "controller does not recognize this build after reconnect");
            }

            return;
        }

        registry.Reconcile(connection, currentBuildId: null);
    }

    private async Task<bool> TryAdoptCurrentSessionAsync(
        string buildId,
        AgentConnectionHandle connection,
        StoredBuild? persisted)
    {
        while (persisted != null)
        {
            var now = timeProvider.GetUtcNow();
            if (!registry.IsCurrent(connection) ||
                persisted.AgentId != connection.AgentId ||
                persisted.State is not (TrackedBuildState.Running or TrackedBuildState.CancelRequested) ||
                persisted.ReconnectDeadline is { } deadline && deadline <= now)
            {
                return false;
            }

            if (await store!.TryAdoptSessionAsync(
                    buildId,
                    connection.AgentId,
                    persisted.OwnerSessionId,
                    connection.SessionId,
                    now))
            {
                return true;
            }

            // A superseded reconnect may have advanced the durable owner after this current
            // session read it. Retry from the newly persisted owner while this handle remains
            // authoritative; otherwise a rapid A -> B reconnect can strand ownership on A.
            if (!registry.IsCurrent(connection))
            {
                return false;
            }

            persisted = await store.GetAsync(buildId);
        }

        return false;
    }

    public async Task<BuildResult> WaitForResultAsync(string buildId, CancellationToken cancellationToken)
    {
        if (builds.TryGetValue(buildId, out var pending))
        {
            return await pending.Completion.Task.WaitAsync(cancellationToken);
        }

        if (completed.TryGetValue(buildId, out var completedResult))
        {
            return completedResult;
        }

        if (store != null)
        {
            var persisted = await store.GetAsync(buildId);
            if (persisted?.Result != null)
            {
                return persisted.Result;
            }
        }

        throw new InvalidOperationException($"unknown build '{buildId}'");
    }

    public string GetLog(string buildId)
    {
        if (!builds.TryGetValue(buildId, out var build))
        {
            return string.Empty;
        }

        lock (build.Gate)
        {
            return build.Log.ToString();
        }
    }

    public IReadOnlyList<TrackedBuildSnapshot> GetSnapshots() => builds.Select(pair =>
    {
        lock (pair.Value.Gate)
        {
            return new TrackedBuildSnapshot(
                pair.Key, pair.Value.AgentId, pair.Value.State, pair.Value.CancellationReason);
        }
    }).ToArray();

    public void OnLog(LogChunk chunk, AgentConnectionHandle connection)
    {
        if (registry.IsCurrent(connection) &&
            builds.TryGetValue(chunk.BuildId, out var build) &&
            build.AgentId == connection.AgentId)
        {
            lock (build.Gate)
            {
                build.Log.Append(chunk.Data.ToStringUtf8());
            }
        }
    }

    public void OnStatus(StepStatus status, AgentConnectionHandle connection)
    {
        if (registry.IsCurrent(connection) &&
            builds.TryGetValue(status.BuildId, out var build) &&
            build.AgentId == connection.AgentId)
        {
            lock (build.Gate)
            {
                build.Log.AppendLine($"[{status.Phase} step={status.StepIndex}]");
            }
        }
    }

    public async Task OnResultAsync(BuildResult result, AgentConnectionHandle connection)
    {
        if (!registry.IsCurrent(connection) || result.SessionId != connection.SessionId)
        {
            return;
        }

        if (!builds.TryGetValue(result.BuildId, out var build) ||
            build.AgentId != connection.AgentId)
        {
            if (await IsPersistedDuplicateAsync(result, connection.AgentId) ||
                await IsLateLeaseResultAsync(result.BuildId, connection.AgentId))
            {
                await AcceptAssignmentFromTerminalAsync(result.BuildId, connection);
                AcknowledgeResult(result, connection);
                registry.EndBuild(connection.AgentId, result.BuildId);
                return;
            }

            // A cancelled orphan reported its terminal result; it is now safe to release occupancy.
            var live = registry.Get(connection.AgentId);
            if (live?.CurrentBuildId == result.BuildId &&
                live.SessionId == connection.SessionId)
            {
                var expected = FindActiveBuild(connection.AgentId);
                registry.Reconcile(connection, expected?.Key);
            }

            return;
        }

        var accepted = store != null
            ? await store.TryFinishAsync(
                result,
                connection.AgentId,
                connection.SessionId,
                timeProvider.GetUtcNow())
            : completed.TryAdd(result.BuildId, result);
        if (!accepted)
        {
            if (!await IsPersistedDuplicateAsync(result, connection.AgentId) &&
                !await IsLateLeaseResultAsync(result.BuildId, connection.AgentId))
            {
                return; // a different terminal result already won
            }

            await AcceptAssignmentFromTerminalAsync(result.BuildId, connection);
            AcknowledgeResult(result, connection);
            registry.EndBuild(connection.AgentId, result.BuildId);
            return;
        }

        lock (build.Gate)
        {
            build.State = TrackedBuildState.Finished;
        }

        await AcceptAssignmentFromTerminalAsync(result.BuildId, connection);
        AcknowledgeResult(result, connection);
        registry.EndBuild(connection.AgentId, result.BuildId);
        builds.TryRemove(result.BuildId, out _);
        build.Completion.TrySetResult(result);
    }

    private async Task<bool> IsPersistedDuplicateAsync(BuildResult result, string agentId)
    {
        BuildResult? first;
        if (store != null)
        {
            var persisted = await store.GetAsync(result.BuildId);
            if (persisted?.AgentId != agentId)
            {
                return false;
            }

            first = persisted.Result;
        }
        else
        {
            completed.TryGetValue(result.BuildId, out first);
        }

        return first != null && SameTerminalResult(first, result);
    }

    private async Task AcceptAssignmentFromTerminalAsync(
        string buildId,
        AgentConnectionHandle connection)
    {
        if (builds.TryGetValue(buildId, out var build))
        {
            build.Dispatched.TrySetResult(true);
        }

        if (queueStore == null)
        {
            return;
        }

        var queued = await queueStore.GetAsync(buildId);
        if (queued?.State != BuildQueueItemState.Claimed ||
            queued.ClaimedAgentId != connection.AgentId)
        {
            return;
        }

        if (await queueStore.CompleteDispatchAsync(
                buildId, connection.AgentId, connection.SessionId))
        {
            QueueChanged?.Invoke();
        }
    }

    private static bool SameTerminalResult(BuildResult first, BuildResult retry)
    {
        var normalizedFirst = first.Clone();
        var normalizedRetry = retry.Clone();
        normalizedFirst.SessionId = string.Empty;
        normalizedRetry.SessionId = string.Empty;
        return normalizedFirst.Equals(normalizedRetry);
    }

    private void AcknowledgeResult(BuildResult result, AgentConnectionHandle connection)
    {
        if (!registry.IsCurrent(connection))
        {
            return;
        }

        connection.Outbox.Writer.TryWrite(new ControllerMsg
        {
            ResultAccepted = new BuildResultAccepted
            {
                BuildId = result.BuildId,
                SessionId = result.SessionId,
            },
        });
    }

    private KeyValuePair<string, PendingBuild>? FindActiveBuild(string agentId)
    {
        foreach (var pair in builds)
        {
            lock (pair.Value.Gate)
            {
                if (pair.Value.AgentId == agentId && pair.Value.State != TrackedBuildState.Finished)
                {
                    return pair;
                }
            }
        }

        return null;
    }

    private void SendCancellation(string buildId, string agentId, string reason) =>
        registry.TrySend(agentId, new ControllerMsg
        {
            Cancel = new CancelBuild { BuildId = buildId, Reason = reason },
        });

    private void SendCancellation(
        string buildId,
        AgentConnectionHandle connection,
        string reason) => registry.TrySend(connection, new ControllerMsg
        {
            Cancel = new CancelBuild { BuildId = buildId, Reason = reason },
        });

    private void SendCancellationIfDeliverable(
        string buildId,
        PendingBuild build,
        string reason)
    {
        string? assignmentSessionId;
        bool accepted;
        lock (build.Gate)
        {
            assignmentSessionId = build.AssignmentSessionId;
            accepted = build.Dispatched.Task.IsCompletedSuccessfully && build.Dispatched.Task.Result;
        }

        if (!registry.TryGetBuildConnection(build.AgentId, buildId, out var connection) ||
            connection is null ||
            !accepted && assignmentSessionId != connection.SessionId)
        {
            return;
        }

        SendCancellation(buildId, connection, reason);
    }

    public Task<bool> OnSessionLostAsync(AgentSessionLoss loss)
    {
        if (store == null || loss.CurrentBuildId == null)
        {
            return Task.FromResult(false);
        }

        var now = timeProvider.GetUtcNow();
        return store.TryArmReconnectGraceAsync(
            loss.CurrentBuildId,
            loss.AgentId,
            loss.SessionId,
            now + reconnectGrace,
            now);
    }

    public async Task<IReadOnlyList<ExpiredBuildLease>> SweepExpiredLeasesAsync(DateTimeOffset now)
    {
        if (store == null)
        {
            return Array.Empty<ExpiredBuildLease>();
        }

        var expired = await store.ExpireDueReconnectLeasesAsync(now);
        foreach (var lease in expired)
        {
            PendingBuild? completedBuild = null;
            if (builds.TryGetValue(lease.BuildId, out var build))
            {
                lock (build.Gate)
                {
                    build.State = TrackedBuildState.Finished;
                }

                // A direct dispatcher may still be waiting for AssignmentAccepted. Expiry is the
                // authoritative terminal outcome, so let it advance to the result waiter.
                build.Dispatched.TrySetResult(true);
                builds.TryRemove(lease.BuildId, out _);
                completedBuild = build;
            }

            registry.EndBuild(lease.AgentId, lease.BuildId);
            completedBuild?.Completion.TrySetResult(lease.Result);
        }

        if (expired.Count > 0)
        {
            QueueChanged?.Invoke();
        }

        return expired;
    }

    private Task<bool> IsLateLeaseResultAsync(string buildId, string agentId) =>
        store == null
            ? Task.FromResult(false)
            : store.IsReconnectLeaseFailureAsync(buildId, agentId);
}
