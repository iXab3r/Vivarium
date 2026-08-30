using System.Collections.Concurrent;
using System.Text;
using Vivarium.Contracts.V1;
using Vivarium.Controller.Agents;
using Vivarium.Controller.Auditing;
using Vivarium.Controller.ResultAdapters.Trx;
using Vivarium.Controller.Security;

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

public sealed class AssignmentAcknowledgementTimeoutException(string buildId)
    : TimeoutException($"Agent did not acknowledge Build assignment '{buildId}' before its deadline");

/// <summary>
/// Durable build ownership and cancellation. An authorized, enabled agent owns at most one build;
/// reconnects and controller restarts preserve ownership, while superseded sessions are fenced out.
/// </summary>
public sealed class BuildTracker
{
    private const int MaximumRetainedLogCharacters = 1024 * 1024;
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
        public long DroppedLogCharacters { get; set; }
        public object Gate { get; } = new();
        public TrackedBuildState State { get; set; } = TrackedBuildState.Running;
        public string? CancellationReason { get; set; }
        public string? StopOperationId { get; set; }
        public BuildStopMode StopMode { get; set; }
        public DateTimeOffset? StopDeadline { get; set; }
        public string? AssignmentSessionId { get; set; }
    }

    private readonly AgentRegistry registry;
    private readonly BuildStore? store;
    private readonly BuildQueueStore? queueStore;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan reconnectGrace;
    private readonly TimeSpan gracefulStopTimeout;
    private readonly TimeSpan forceStopTimeout;
    private readonly TimeSpan assignmentAckTimeout;
    private readonly ManagementCommandAuthorizer? authorization;
    private readonly IBuildResultProjectionParticipant? resultProjections;
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
        TimeSpan? reconnectGrace = null,
        ManagementCommandAuthorizer? authorization = null,
        IBuildResultProjectionParticipant? resultProjections = null,
        TimeSpan? gracefulStopTimeout = null,
        TimeSpan? forceStopTimeout = null,
        TimeSpan? assignmentAckTimeout = null)
    {
        this.registry = registry;
        this.store = store;
        this.queueStore = queueStore;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.reconnectGrace = reconnectGrace ?? TimeSpan.FromSeconds(60);
        this.gracefulStopTimeout = gracefulStopTimeout ?? TimeSpan.FromSeconds(30);
        this.forceStopTimeout = forceStopTimeout ?? TimeSpan.FromSeconds(15);
        this.assignmentAckTimeout = assignmentAckTimeout ?? TimeSpan.FromSeconds(15);
        this.authorization = authorization;
        this.resultProjections = resultProjections;
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
            var recoveredStop = build.State == TrackedBuildState.CancelRequested &&
                build.StopOperationId is null
                    ? await store.TryRequestStopAsync(
                        build.BuildId,
                        build.CancellationReason ?? "cancellation requested before controller restart",
                        BuildStopMode.Graceful,
                        timeProvider.GetUtcNow() + gracefulStopTimeout,
                        timeProvider.GetUtcNow(),
                        auditEvent: null)
                    : null;
            var pending = new PendingBuild
            {
                AgentId = build.AgentId!,
                Assignment = build.Assignment,
                State = build.State,
                CancellationReason = build.CancellationReason,
                StopOperationId = recoveredStop?.OperationId ?? build.StopOperationId,
                StopMode = recoveredStop?.Mode ?? build.StopMode,
                StopDeadline = recoveredStop?.Deadline ?? build.StopDeadline,
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

    internal async Task<BuildResult> RunBuildFromControllerAsync(
        string agentId,
        BuildAssignment assignment,
        CancellationToken cancellationToken)
    {
        await DispatchBuildFromControllerAsync(agentId, assignment);
        return await WaitForResultAsync(assignment.BuildId, cancellationToken);
    }

    /// <summary>Durably records and sends an assignment, returning once the agent owns it.</summary>
    internal async Task DispatchBuildFromControllerAsync(
        string agentId,
        BuildAssignment assignment)
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
                var now = timeProvider.GetUtcNow();
                await store.CreateAsync(
                    agentId,
                    connection!.SessionId,
                    assignment,
                    now,
                    now + assignmentAckTimeout);
                persisted = true;
            }

            if (store is null && !await PrepareAssignmentAttemptAsync(assignment.BuildId, connection!))
            {
                throw new InvalidOperationException(
                    $"could not persist assignment attempt for build '{assignment.BuildId}'");
            }
            if (!registry.TrySend(connection!, new ControllerMsg { Build = assignment }))
            {
                // A reconnect can supersede the reserved stream after the durable create but before
                // this enqueue. If the newer Hello already asserts this exact Build, its subsequent
                // reconciliation is stronger ownership evidence than resending or rolling back.
                if (!HasNewerSessionReportedBuild(connection!, assignment.BuildId))
                {
                    throw new InvalidOperationException($"agent '{agentId}' is not connected");
                }

                await pending.Dispatched.Task;
                return;
            }

            OnAssignmentSent(assignment.BuildId, connection!);
            await pending.Dispatched.Task;
        }
        catch (AssignmentAcknowledgementTimeoutException)
        {
            // The assignment may already be executing. Durable ownership and quarantine remain;
            // deleting the row here would permit overlapping work.
            throw;
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

    private bool HasNewerSessionReportedBuild(
        AgentConnectionHandle superseded,
        string buildId)
    {
        var live = registry.Get(superseded.AgentId);
        if (live is null)
        {
            return false;
        }

        lock (live.Gate)
        {
            return live.Connected &&
                live.ConnectionGeneration > superseded.ConnectionGeneration &&
                string.Equals(live.Hello.RunningBuildId, buildId, StringComparison.Ordinal);
        }
    }

    internal Task<bool> CancelBuildFromControllerAsync(string buildId, string reason) =>
        RequestBuildStopAsync(buildId, reason, BuildStopMode.Graceful, auditEvent: null);

    internal Task<bool> StopBuildFromControllerAsync(
        string buildId,
        string reason,
        BuildStopMode mode) => RequestBuildStopAsync(buildId, reason, mode, auditEvent: null);

    public async Task<bool> CancelBuildAsync(
        ManagementRequestContext context,
        string buildId,
        string reason)
    {
        ArgumentNullException.ThrowIfNull(context);
        await (authorization ?? throw new InvalidOperationException(
                "application command authorization is not configured"))
            .DemandAsync(
                context,
                ManagementPermission.BuildCancel,
                "build.cancel",
                "build",
                buildId);
        return await RequestBuildStopAsync(
            buildId,
            reason,
            BuildStopMode.Graceful,
            AuditEventDraft.Create(
                context,
                timeProvider.GetUtcNow(),
                "build.cancel",
                "build",
                buildId));
    }

    public async Task<bool> ForceStopBuildAsync(
        ManagementRequestContext context,
        string buildId,
        string reason)
    {
        ArgumentNullException.ThrowIfNull(context);
        await (authorization ?? throw new InvalidOperationException(
                "application command authorization is not configured"))
            .DemandAsync(
                context,
                ManagementPermission.BuildForceStop,
                "build.force-stop",
                "build",
                buildId);
        return await RequestBuildStopAsync(
            buildId,
            reason,
            BuildStopMode.Force,
            AuditEventDraft.Create(
                context,
                timeProvider.GetUtcNow(),
                "build.force-stop",
                "build",
                buildId));
    }

    private async Task<bool> RequestBuildStopAsync(
        string buildId,
        string reason,
        BuildStopMode requestedMode,
        AuditEventDraft? auditEvent)
    {
        if (!builds.TryGetValue(buildId, out var build))
        {
            return false;
        }

        var now = timeProvider.GetUtcNow();
        var requestedDeadline = now + (requestedMode == BuildStopMode.Force
            ? forceStopTimeout
            : gracefulStopTimeout);

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
                build.StopOperationId ??= ManagementIdentifiers.NewId();
                if (requestedMode == BuildStopMode.Force ||
                    build.StopMode == BuildStopMode.Unspecified)
                {
                    build.StopMode = requestedMode;
                }
                build.StopDeadline = build.StopDeadline is null ||
                    requestedDeadline < build.StopDeadline
                        ? requestedDeadline
                        : build.StopDeadline;
            }

            SendCancellationIfDeliverable(buildId, build);
            return true;
        }

        var request = await store.TryRequestStopAsync(
            buildId, reason, requestedMode, requestedDeadline, now, auditEvent);
        if (!request.Active)
        {
            return false;
        }

        lock (build.Gate)
        {
            if (build.State == TrackedBuildState.Finished)
            {
                return false;
            }

            build.State = TrackedBuildState.CancelRequested;
            build.CancellationReason = request.Reason ?? reason;
            build.StopOperationId = request.OperationId;
            build.StopMode = request.Mode;
            build.StopDeadline = request.Deadline;
        }

        SendCancellationIfDeliverable(buildId, build);
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
            build.StopOperationId = persisted.StopOperationId;
            build.StopMode = persisted.StopMode;
            build.StopDeadline = persisted.StopDeadline;
        }

        SendCancellationIfDeliverable(buildId, build);
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

        var cancellationRequested = false;
        lock (build.Gate)
        {
            build.AssignmentSessionId = connection.SessionId;
            cancellationRequested = build.State == TrackedBuildState.CancelRequested;
        }

        if (cancellationRequested)
        {
            SendCancellation(buildId, build, connection);
        }
    }

    internal async Task<bool> PrepareAssignmentAttemptAsync(
        string buildId,
        AgentConnectionHandle connection)
    {
        if (!builds.TryGetValue(buildId, out var build) ||
            build.AgentId != connection.AgentId ||
            !registry.IsCurrent(connection))
        {
            return false;
        }

        var now = timeProvider.GetUtcNow();
        if (store is not null && !await store.RecordAssignmentAttemptAsync(
                buildId,
                connection.AgentId,
                connection.SessionId,
                now + assignmentAckTimeout,
                now))
        {
            return false;
        }

        lock (build.Gate)
        {
            build.AssignmentSessionId = connection.SessionId;
        }
        return true;
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

        if (store is not null && !await store.TryAcknowledgeAssignmentAsync(
                accepted.BuildId,
                connection.AgentId,
                connection.SessionId,
                timeProvider.GetUtcNow()))
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
        var cancellationRequested = false;
        lock (build.Gate)
        {
            cancellationRequested = build.State == TrackedBuildState.CancelRequested;
        }

        if (cancellationRequested)
        {
            SendCancellation(accepted.BuildId, build, connection);
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
            var cancellationRequested = false;
            lock (owned.Value.Gate)
            {
                cancellationRequested = owned.Value.State == TrackedBuildState.CancelRequested;
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

                if (cancellationRequested)
                {
                    SendCancellation(owned.Key, owned.Value, connection);
                }

                return;
            }

            // A mismatched or empty Hello is not ownership proof. Keep the expected build occupying
            // runtime capacity while its durable reconnect deadline runs and quarantine every
            // contradiction except an empty assertion for work that was never acknowledged. That
            // prepared queue work may still be resent; acknowledged work must reconnect matching.
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
            if (reportedBuildId.Length > 0 || !awaitingAcceptance)
            {
                registry.Quarantine(connection.AgentId, "workload_assertion_mismatch");
                await registry.PersistOperationalStateAsync(connection.AgentId);
            }
            if (reportedBuildId.Length > 0)
            {
                SendContainmentRequest(
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
                SendContainmentRequest(
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
            if (build.DroppedLogCharacters == 0)
            {
                return build.Log.ToString();
            }

            return build.Log + Environment.NewLine +
                $"[Vivarium truncated {build.DroppedLogCharacters} log characters]";
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
                AppendBounded(build, chunk.Data.ToStringUtf8());
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
                AppendBounded(build, $"[{status.Phase} step={status.StepIndex}]{Environment.NewLine}");
            }
        }
    }

    public async Task OnHeartbeatAsync(Heartbeat heartbeat, AgentConnectionHandle connection)
    {
        if (!registry.Heartbeat(connection, heartbeat))
        {
            await registry.PersistOperationalStateAsync(connection.AgentId);
            return;
        }

        var reportedBuildId = NullIfEmpty(heartbeat.RunningBuildId);
        var live = registry.Get(connection.AgentId);
        string? expectedBuildId;
        if (live is null)
        {
            return;
        }
        lock (live.Gate)
        {
            if (live.SessionId != connection.SessionId ||
                live.ConnectionGeneration != connection.ConnectionGeneration)
            {
                return;
            }
            expectedBuildId = live.CurrentBuildId;
        }

        if (string.Equals(reportedBuildId, expectedBuildId, StringComparison.Ordinal))
        {
            if (expectedBuildId is not null)
            {
                // A current heartbeat asserting the exact assigned Build is stronger execution
                // evidence than a potentially lost AssignmentAccepted message.
                await OnAssignmentAcceptedAsync(new AssignmentAccepted
                {
                    BuildId = expectedBuildId,
                    SessionId = connection.SessionId,
                }, connection);
            }
            return;
        }

        if (expectedBuildId is not null && reportedBuildId is null &&
            builds.TryGetValue(expectedBuildId, out var awaiting))
        {
            lock (awaiting.Gate)
            {
                if (!awaiting.Dispatched.Task.IsCompletedSuccessfully)
                {
                    return;
                }
            }
        }

        if (expectedBuildId is null && reportedBuildId is not null && store is not null)
        {
            var persisted = await store.GetAsync(reportedBuildId);
            if (persisted?.AgentId == connection.AgentId &&
                persisted.State == TrackedBuildState.Finished)
            {
                // The terminal ACK is queued but has not reached the Agent yet.
                return;
            }
        }

        registry.Quarantine(connection.AgentId, "workload_assertion_mismatch");
        await registry.PersistOperationalStateAsync(connection.AgentId);
        if (reportedBuildId is not null)
        {
            registry.TrySend(connection, new ControllerMsg
            {
                Cancel = new CancelBuild
                {
                    BuildId = reportedBuildId,
                    Reason = expectedBuildId is null
                        ? "controller does not recognize this running Build"
                        : $"controller expected Build '{expectedBuildId}'",
                    Mode = BuildStopMode.Force,
                    OperationId = $"reconcile-{connection.SessionId}",
                    DeadlineUnixMs = timeProvider.GetUtcNow().AddSeconds(10).ToUnixTimeMilliseconds(),
                },
            });
        }
    }

    public async Task OnBuildStopAcknowledgedAsync(
        BuildStopAcknowledged acknowledged,
        AgentConnectionHandle connection)
    {
        if (!registry.IsCurrent(connection) ||
            !string.Equals(acknowledged.SessionId, connection.SessionId, StringComparison.Ordinal) ||
            !builds.TryGetValue(acknowledged.BuildId, out var build) ||
            !string.Equals(build.AgentId, connection.AgentId, StringComparison.Ordinal))
        {
            return;
        }

        lock (build.Gate)
        {
            if (!string.Equals(
                    build.StopOperationId, acknowledged.OperationId, StringComparison.Ordinal) ||
                build.StopMode != acknowledged.Mode)
            {
                return;
            }
        }

        if (store is not null)
        {
            await store.TryAcknowledgeStopAsync(
                acknowledged, connection.AgentId, timeProvider.GetUtcNow());
        }
    }

    public async Task SweepDueStopsAsync(DateTimeOffset now)
    {
        if (store is null)
        {
            return;
        }

        foreach (var stop in await store.ListDueStopsAsync(now))
        {
            if (stop.Mode == BuildStopMode.Graceful)
            {
                if (!await store.TryExpireGracefulStopAsync(
                        stop.BuildId, stop.OperationId, now))
                {
                    continue;
                }
                registry.Quarantine(stop.AgentId, "graceful_stop_deadline_expired");
                await registry.PersistOperationalStateAsync(stop.AgentId);
                continue;
            }

            if (await store.TryExpireStopAsync(stop.BuildId, stop.OperationId, now))
            {
                registry.Quarantine(stop.AgentId, "force_stop_result_deadline_expired");
                await registry.PersistOperationalStateAsync(stop.AgentId);
            }
        }
    }

    public async Task SweepDueAssignmentAttemptsAsync(DateTimeOffset now)
    {
        if (store is null)
        {
            return;
        }

        foreach (var attempt in await store.ExpireDueAssignmentAttemptsAsync(now))
        {
            if (!builds.TryGetValue(attempt.BuildId, out var build))
            {
                continue;
            }

            build.Dispatched.TrySetException(
                new AssignmentAcknowledgementTimeoutException(attempt.BuildId));
            registry.Quarantine(attempt.AgentId, "assignment_acknowledgement_expired");
            await registry.PersistOperationalStateAsync(attempt.AgentId);
            await RequestBuildStopAsync(
                attempt.BuildId,
                "assignment acknowledgement deadline expired",
                BuildStopMode.Force,
                auditEvent: null);
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
                if (AcknowledgeResult(result, connection))
                {
                    registry.EndBuild(connection.AgentId, result.BuildId);
                }
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
                connection.ConnectionGeneration,
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
            if (AcknowledgeResult(result, connection))
            {
                registry.EndBuild(connection.AgentId, result.BuildId);
                builds.TryRemove(result.BuildId, out _);
                build.Completion.TrySetResult(result);
            }
            return;
        }

        lock (build.Gate)
        {
            build.State = TrackedBuildState.Finished;
        }

        await AcceptAssignmentFromTerminalAsync(result.BuildId, connection);
        if (!AcknowledgeResult(result, connection))
        {
            return;
        }
        registry.EndBuild(connection.AgentId, result.BuildId);
        builds.TryRemove(result.BuildId, out _);
        build.Completion.TrySetResult(result);
        if (resultProjections is not null)
        {
            await resultProjections.ProjectTerminalBuildAsync(result.BuildId);
        }
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

        if (store is not null)
        {
            await store.TryAcknowledgeAssignmentAsync(
                buildId,
                connection.AgentId,
                connection.SessionId,
                timeProvider.GetUtcNow());
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

    private bool AcknowledgeResult(BuildResult result, AgentConnectionHandle connection)
    {
        if (!registry.IsCurrent(connection))
        {
            return false;
        }

        return registry.TrySend(connection, new ControllerMsg
        {
            ResultAccepted = new BuildResultAccepted
            {
                BuildId = result.BuildId,
                SessionId = result.SessionId,
            },
        });
    }

    private static void AppendBounded(PendingBuild build, string value)
    {
        var available = MaximumRetainedLogCharacters - build.Log.Length;
        if (available <= 0)
        {
            build.DroppedLogCharacters += value.Length;
            return;
        }

        var retained = Math.Min(available, value.Length);
        build.Log.Append(value, 0, retained);
        build.DroppedLogCharacters += value.Length - retained;
    }

    private static string? NullIfEmpty(string value) =>
        string.IsNullOrEmpty(value) ? null : value;

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

    private void SendContainmentRequest(string buildId, string agentId, string reason) =>
        registry.TrySend(agentId, new ControllerMsg
        {
            Cancel = new CancelBuild
            {
                BuildId = buildId,
                Reason = reason,
                Mode = BuildStopMode.Force,
                OperationId = ManagementIdentifiers.NewId(),
                DeadlineUnixMs = timeProvider.GetUtcNow()
                    .Add(forceStopTimeout).ToUnixTimeMilliseconds(),
            },
        });

    private void SendCancellation(
        string buildId,
        PendingBuild build,
        AgentConnectionHandle connection)
    {
        CancelBuild cancellation;
        lock (build.Gate)
        {
            cancellation = new CancelBuild
            {
                BuildId = buildId,
                Reason = build.CancellationReason ?? "cancellation requested",
                Mode = build.StopMode,
                OperationId = build.StopOperationId ?? string.Empty,
                DeadlineUnixMs = build.StopDeadline?.ToUnixTimeMilliseconds() ?? 0,
            };
        }
        registry.TrySend(connection, new ControllerMsg { Cancel = cancellation });
    }

    private void SendCancellationIfDeliverable(
        string buildId,
        PendingBuild build)
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

        SendCancellation(buildId, build, connection);
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

            registry.Quarantine(lease.AgentId, "workload_ownership_expired_unconfirmed");
            await registry.PersistOperationalStateAsync(lease.AgentId);
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
