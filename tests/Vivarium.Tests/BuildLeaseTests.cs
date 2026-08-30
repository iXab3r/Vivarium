using Microsoft.Extensions.Logging.Abstractions;
using Vivarium.Contracts.V1;
using Vivarium.Controller;
using Vivarium.Controller.Agents;
using Vivarium.Controller.Auditing;
using Vivarium.Controller.Builds;
using Vivarium.Controller.Persistence;
using Vivarium.Controller.Security;

namespace Vivarium.Tests;

[TestFixture]
public class BuildLeaseTests
{
    private string rootDir = null!;

    [SetUp]
    public void SetUp()
    {
        rootDir = Path.Combine(Path.GetTempPath(), "vivarium-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootDir);
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            Directory.Delete(rootDir, recursive: true);
        }
        catch
        {
            // best effort
        }
    }

    [Test]
    public async Task Lost_session_finishes_history_but_quarantines_ambiguous_runtime_occupancy()
    {
        await using var harness = await LeaseHarness.StartAsync(rootDir);
        var connection = harness.Connect("session-1");
        await harness.PrepareQueuedAsync("lease-expiry", connection);

        var resultTask = harness.Builds.WaitForResultAsync("lease-expiry", CancellationToken.None);
        var loss = harness.Registry.Disconnect(connection);
        Assert.That(loss, Is.Not.Null);
        Assert.That(await harness.Builds.OnSessionLostAsync(loss!), Is.True);

        var armed = await harness.BuildStore.GetAsync("lease-expiry");
        Assert.Multiple(() =>
        {
            Assert.That(armed?.State, Is.EqualTo(TrackedBuildState.Running));
            Assert.That(armed?.OwnerSessionId, Is.EqualTo("session-1"));
            Assert.That(armed?.ReconnectDeadline,
                Is.EqualTo(harness.Time.GetUtcNow() + LeaseHarness.Grace));
            Assert.That(harness.Queue.GetAsync("lease-expiry").Result?.State,
                Is.EqualTo(BuildQueueItemState.Claimed));
        });

        harness.Time.Advance(LeaseHarness.Grace - TimeSpan.FromMilliseconds(1));
        await harness.Monitor.SweepOnceAsync(harness.Time.GetUtcNow());
        Assert.That((await harness.BuildStore.GetAsync("lease-expiry"))?.State,
            Is.EqualTo(TrackedBuildState.Running));

        harness.Time.Advance(TimeSpan.FromMilliseconds(1));
        await harness.Monitor.SweepOnceAsync(harness.Time.GetUtcNow());

        var terminal = await harness.BuildStore.GetAsync("lease-expiry");
        var queue = await harness.Queue.GetAsync("lease-expiry");
        var result = await resultTask;
        Assert.Multiple(() =>
        {
            Assert.That(terminal?.State, Is.EqualTo(TrackedBuildState.Finished));
            Assert.That(terminal?.Result?.Outcome, Is.EqualTo(BuildOutcome.InfrastructureFailed));
            Assert.That(terminal?.Result?.StatusText,
                Is.EqualTo(BuildStore.ReconnectGraceExpiredStatus));
            Assert.That(queue?.State, Is.EqualTo(BuildQueueItemState.Removed));
            Assert.That(queue?.RemovalReason, Is.EqualTo(BuildStore.ReconnectGraceExpiredStatus));
            Assert.That(result.Outcome, Is.EqualTo(BuildOutcome.InfrastructureFailed));
            Assert.That(harness.Registry.Get("agent-1")?.CurrentBuildId, Is.EqualTo("lease-expiry"));
            Assert.That(harness.Registry.Get("agent-1")?.Quarantined, Is.True);
            Assert.That(harness.Registry.Get("agent-1")?.OperationalReason,
                Is.EqualTo("workload_ownership_expired_unconfirmed"));
            Assert.That(harness.Builds.GetSnapshots().Any(item => item.BuildId == "lease-expiry"),
                Is.False,
                "lease expiry must evict the same terminal PendingBuild state as an agent result");
        });

        Assert.That(
            harness.Registry.TryBeginBuild("agent-1", "unsafe-overlap", out _),
            Is.False);
    }

    [Test]
    public async Task Matching_reconnect_adopts_owner_and_old_loss_cannot_extend_or_rearm_lease()
    {
        await using var harness = await LeaseHarness.StartAsync(rootDir);
        var first = harness.Connect("session-1");
        await harness.PrepareQueuedAsync("re-adopt", first);
        var firstLoss = harness.Registry.Disconnect(first)!;
        Assert.That(await harness.Builds.OnSessionLostAsync(firstLoss), Is.True);

        harness.Time.Advance(TimeSpan.FromSeconds(20));
        var second = harness.Register("session-2", runningBuildId: "re-adopt");
        await harness.Builds.OnAgentReconnectedAsync(second, "re-adopt");

        var adopted = await harness.BuildStore.GetAsync("re-adopt");
        var queue = await harness.Queue.GetAsync("re-adopt");
        var reconnectMessages = Drain(harness.Registry.Get("agent-1")!);
        Assert.Multiple(() =>
        {
            Assert.That(adopted?.OwnerSessionId, Is.EqualTo("session-2"));
            Assert.That(adopted?.ReconnectDeadline, Is.Null);
            Assert.That(queue?.State, Is.EqualTo(BuildQueueItemState.Removed));
            Assert.That(queue?.RemovalReason, Is.EqualTo("dispatched"));
            Assert.That(reconnectMessages.Any(message =>
                    message.MsgCase == ControllerMsg.MsgOneofCase.Build),
                Is.False,
                "a matching Hello is positive ownership evidence and must not resend the build");
            Assert.That(harness.Registry.Get("agent-1")?.CurrentBuildId, Is.EqualTo("re-adopt"));
        });

        Assert.That(await harness.Builds.OnSessionLostAsync(firstLoss), Is.False,
            "the superseded owner cannot arm the adopted session");
        harness.Time.Advance(LeaseHarness.Grace);
        await harness.Builds.SweepExpiredLeasesAsync(harness.Time.GetUtcNow());
        Assert.That((await harness.BuildStore.GetAsync("re-adopt"))?.State,
            Is.EqualTo(TrackedBuildState.Running));

        await harness.Builds.OnResultAsync(new BuildResult
        {
            BuildId = "re-adopt",
            SessionId = "session-2",
            Outcome = BuildOutcome.Succeeded,
        }, second);
        Assert.That((await harness.BuildStore.GetAsync("re-adopt"))?.Result?.Outcome,
            Is.EqualTo(BuildOutcome.Succeeded));
    }

    [Test]
    public async Task Current_reconnect_retries_adoption_after_a_superseded_session_advances_the_owner()
    {
        await using var harness = await LeaseHarness.StartAsync(rootDir);
        const string buildId = "rapid-reconnect";
        var first = harness.Connect("session-1");
        await harness.PrepareQueuedAsync(buildId, first);

        using var writerEntered = new ManualResetEventSlim();
        using var releaseWriter = new ManualResetEventSlim();
        var blocker = harness.Database.WriteAsync(_ =>
        {
            writerEntered.Set();
            if (!releaseWriter.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("test did not release the serialized database writer");
            }

            return true;
        });
        Assert.That(writerEntered.Wait(TimeSpan.FromSeconds(5)), Is.True);

        Task secondReconcile;
        Task thirdReconcile;
        try
        {
            var second = harness.Register("session-2", runningBuildId: buildId);
            secondReconcile = harness.Builds.OnAgentReconnectedAsync(second, buildId);

            var third = harness.Register("session-3", runningBuildId: buildId);
            thirdReconcile = harness.Builds.OnAgentReconnectedAsync(third, buildId);
        }
        finally
        {
            releaseWriter.Set();
        }

        await blocker;
        await Task.WhenAll(secondReconcile!, thirdReconcile!);

        var persisted = await harness.BuildStore.GetAsync(buildId);
        var queue = await harness.Queue.GetAsync(buildId);
        var runtime = harness.Registry.Get("agent-1");
        Assert.Multiple(() =>
        {
            Assert.That(persisted?.OwnerSessionId, Is.EqualTo("session-3"),
                "the current session must retry after its stale session-1 CAS loses to session-2");
            Assert.That(persisted?.ReconnectDeadline, Is.Null);
            Assert.That(runtime?.SessionId, Is.EqualTo("session-3"));
            Assert.That(runtime?.Reconciled, Is.True);
            Assert.That(runtime?.CurrentBuildId, Is.EqualTo(buildId));
            Assert.That(queue?.State, Is.EqualTo(BuildQueueItemState.Removed));
            Assert.That(queue?.RemovalReason, Is.EqualTo("dispatched"));
        });
    }

    [Test]
    public async Task Startup_grace_begins_only_when_listener_is_ready()
    {
        var time = new ManualTimeProvider(
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var grace = TimeSpan.FromMinutes(1);
        await using var database = new VivariumDatabase(rootDir);
        var store = new BuildStore(database);
        await store.CreateAsync(
            "agent-1", "session-1", new BuildAssignment { BuildId = "startup-delay" },
            time.GetUtcNow());
        var tracker = new BuildTracker(
            new AgentRegistry(timeProvider: time), store, timeProvider: time,
            reconnectGrace: grace);

        await tracker.InitializeAsync();
        time.Advance(TimeSpan.FromSeconds(45));

        Assert.That((await store.GetAsync("startup-delay"))?.ReconnectDeadline, Is.Null,
            "controller construction time must not consume the agent's reconnect window");

        await tracker.ArmStartupReconnectGraceAsync();
        var deadline = time.GetUtcNow() + grace;
        Assert.That((await store.GetAsync("startup-delay"))?.ReconnectDeadline,
            Is.EqualTo(deadline));

        time.Advance(grace - TimeSpan.FromMilliseconds(1));
        await tracker.SweepExpiredLeasesAsync(time.GetUtcNow());
        Assert.That((await store.GetAsync("startup-delay"))?.State,
            Is.EqualTo(TrackedBuildState.Running));

        time.Advance(TimeSpan.FromMilliseconds(1));
        await tracker.SweepExpiredLeasesAsync(time.GetUtcNow());
        Assert.That((await store.GetAsync("startup-delay"))?.State,
            Is.EqualTo(TrackedBuildState.Finished));
    }

    [Test]
    public async Task Reconnect_before_listener_ready_arming_wins_the_exact_owner_race()
    {
        var time = new ManualTimeProvider(
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await using var database = new VivariumDatabase(rootDir);
        var store = new BuildStore(database);
        var registry = new AgentRegistry(timeProvider: time);
        await store.CreateAsync(
            "agent-1", "session-1", new BuildAssignment { BuildId = "startup-race" },
            time.GetUtcNow());
        var tracker = new BuildTracker(
            registry, store, timeProvider: time, reconnectGrace: TimeSpan.FromMinutes(1));
        await tracker.InitializeAsync();

        var reconnected = registry.Register(
            new Hello
            {
                AgentId = "agent-1",
                SessionId = "session-2",
                RunningBuildId = "startup-race",
            },
            AgentAuth.Authorized,
            enabled: true,
            new CancellationTokenSource());
        await tracker.OnAgentReconnectedAsync(reconnected, "startup-race");

        await tracker.ArmStartupReconnectGraceAsync();
        await tracker.ArmStartupReconnectGraceAsync();

        var adopted = await store.GetAsync("startup-race");
        Assert.Multiple(() =>
        {
            Assert.That(adopted?.OwnerSessionId, Is.EqualTo("session-2"));
            Assert.That(adopted?.ReconnectDeadline, Is.Null);
            Assert.That(adopted?.State, Is.EqualTo(TrackedBuildState.Running));
        });
    }

    [Test]
    public async Task Restart_never_extends_existing_deadline_and_expires_overdue_build_on_initialize()
    {
        var time = new ManualTimeProvider(
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var grace = TimeSpan.FromMinutes(1);
        DateTimeOffset firstDeadline;

        await using (var database = new VivariumDatabase(rootDir))
        {
            var store = new BuildStore(database);
            await store.CreateAsync(
                "agent-1", "session-1", new BuildAssignment { BuildId = "restart-lease" },
                time.GetUtcNow());
            var tracker = new BuildTracker(
                new AgentRegistry(timeProvider: time), store, timeProvider: time,
                reconnectGrace: grace);
            await tracker.InitializeAsync();
            Assert.That((await store.GetAsync("restart-lease"))?.ReconnectDeadline, Is.Null);
            await tracker.ArmStartupReconnectGraceAsync();
            firstDeadline = (await store.GetAsync("restart-lease"))!.ReconnectDeadline!.Value;
        }

        time.Advance(TimeSpan.FromSeconds(30));
        await using (var database = new VivariumDatabase(rootDir))
        {
            var store = new BuildStore(database);
            var tracker = new BuildTracker(
                new AgentRegistry(timeProvider: time), store, timeProvider: time,
                reconnectGrace: grace);
            await tracker.InitializeAsync();
            time.Advance(TimeSpan.FromSeconds(10));
            await tracker.ArmStartupReconnectGraceAsync();
            Assert.That((await store.GetAsync("restart-lease"))?.ReconnectDeadline,
                Is.EqualTo(firstDeadline));
        }

        time.Advance(TimeSpan.FromSeconds(21));
        await using (var database = new VivariumDatabase(rootDir))
        {
            var store = new BuildStore(database);
            var tracker = new BuildTracker(
                new AgentRegistry(timeProvider: time), store, timeProvider: time,
                reconnectGrace: grace);
            await tracker.InitializeAsync();
            await tracker.ArmStartupReconnectGraceAsync();
            var terminal = await store.GetAsync("restart-lease");
            Assert.Multiple(() =>
            {
                Assert.That(terminal?.ReconnectDeadline, Is.EqualTo(firstDeadline));
                Assert.That(terminal?.State, Is.EqualTo(TrackedBuildState.Finished));
                Assert.That(terminal?.Result?.Outcome, Is.EqualTo(BuildOutcome.InfrastructureFailed));
            });
        }
    }

    [Test]
    public async Task Matching_reconnect_completes_direct_dispatch_without_resending()
    {
        await using var harness = await LeaseHarness.StartAsync(rootDir);
        harness.Connect("session-1");
        var assignment = new BuildAssignment { BuildId = "direct-re-adopt" };

        var dispatch = harness.Builds.DispatchBuildFromControllerAsync("agent-1", assignment);
        await WaitUntilAsync(
            async () => (await harness.BuildStore.GetAsync("direct-re-adopt"))?.State ==
                TrackedBuildState.Running);

        var second = harness.Register("session-2", runningBuildId: "direct-re-adopt");
        await harness.Builds.OnAgentReconnectedAsync(second, "direct-re-adopt");
        await dispatch.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Multiple(() =>
        {
            Assert.That(Drain(harness.Registry.Get("agent-1")!).Any(message =>
                    message.MsgCase == ControllerMsg.MsgOneofCase.Build),
                Is.False);
            Assert.That(harness.BuildStore.GetAsync("direct-re-adopt").Result?.OwnerSessionId,
                Is.EqualTo("session-2"));
        });
    }

    [Test]
    public async Task Failed_direct_send_after_reconnect_rolls_back_new_runtime_occupancy()
    {
        await using var harness = await LeaseHarness.StartAsync(rootDir);
        harness.Connect("session-1");
        Assert.That(harness.Registry.Get("agent-1")!.Outbox.Writer.TryComplete(), Is.True);
        AgentConnectionHandle? replacement = null;
        var reconciled = false;
        var replaced = 0;

        void ReplaceSessionAfterCreate()
        {
            var persisted = harness.BuildStore.GetAsync("direct-send-race").Result;
            if (persisted?.State != TrackedBuildState.Running ||
                Interlocked.CompareExchange(ref replaced, 1, 0) != 0)
            {
                return;
            }

            replacement = harness.Register("session-2", runningBuildId: null);
            reconciled = harness.Registry.Reconcile(replacement, "direct-send-race");
        }

        harness.Database.Changed += ReplaceSessionAfterCreate;
        Exception? dispatchFailure = null;
        try
        {
            try
            {
                await harness.Builds.DispatchBuildFromControllerAsync(
                        "agent-1", new BuildAssignment { BuildId = "direct-send-race" })
                    .WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                dispatchFailure = ex;
            }
        }
        finally
        {
            harness.Database.Changed -= ReplaceSessionAfterCreate;
        }

        var runtime = harness.Registry.Get("agent-1");
        Assert.Multiple(() =>
        {
            Assert.That(dispatchFailure, Is.TypeOf<InvalidOperationException>()
                .With.Message.Contains("not connected"));
            Assert.That(replacement?.SessionId, Is.EqualTo("session-2"));
            Assert.That(reconciled, Is.True);
            Assert.That(runtime?.SessionId, Is.EqualTo("session-2"));
            Assert.That(runtime?.Activity, Is.EqualTo(AgentActivity.Idle));
            Assert.That(runtime?.CurrentBuildId, Is.Null);
            Assert.That(harness.BuildStore.GetAsync("direct-send-race").Result, Is.Null);
        });
    }

    [Test]
    public async Task Result_before_deadline_wins_but_result_at_deadline_is_rejected_for_expiry()
    {
        await using var harness = await LeaseHarness.StartAsync(rootDir);
        var beforeDeadline = harness.Time.GetUtcNow() + LeaseHarness.Grace;
        await harness.BuildStore.CreateAsync(
            "agent-1",
            "session-before",
            new BuildAssignment { BuildId = "result-before-deadline" },
            harness.Time.GetUtcNow());
        Assert.That(
            await harness.BuildStore.TryArmReconnectGraceAsync(
                "result-before-deadline",
                "agent-1",
                "session-before",
                beforeDeadline,
                harness.Time.GetUtcNow()),
            Is.True);
        Assert.That(
            await harness.BuildStore.TryFinishAsync(
                new BuildResult
                {
                    BuildId = "result-before-deadline",
                    SessionId = "session-before",
                    Outcome = BuildOutcome.Succeeded,
                },
                "agent-1",
                "session-before",
                beforeDeadline - TimeSpan.FromMilliseconds(1)),
            Is.True);

        var first = harness.Connect("session-1");
        await harness.PrepareQueuedAsync("result-at-deadline", first);
        var loss = harness.Registry.Disconnect(first)!;
        await harness.Builds.OnSessionLostAsync(loss);
        harness.Time.Advance(LeaseHarness.Grace);

        Assert.That(
            await harness.BuildStore.TryFinishAsync(
                new BuildResult
                {
                    BuildId = "result-at-deadline",
                    SessionId = "session-1",
                    Outcome = BuildOutcome.Succeeded,
                },
                "agent-1",
                "session-1",
                harness.Time.GetUtcNow()),
            Is.False,
            "the persisted reconnect deadline fences results even before the sweeper runs");

        var expired = await harness.Builds.SweepExpiredLeasesAsync(harness.Time.GetUtcNow());
        var final = await harness.BuildStore.GetAsync("result-at-deadline");

        Assert.Multiple(() =>
        {
            Assert.That(expired.Select(item => item.BuildId),
                Is.EquivalentTo(new[] { "result-at-deadline" }));
            Assert.That(final?.State, Is.EqualTo(TrackedBuildState.Finished));
            Assert.That(final?.Result?.Outcome, Is.EqualTo(BuildOutcome.InfrastructureFailed));
            Assert.That(
                harness.BuildStore.GetAsync("result-before-deadline").Result?.Result?.Outcome,
                Is.EqualTo(BuildOutcome.Succeeded));
        });

        var second = harness.Register("session-2", runningBuildId: "result-at-deadline");
        await harness.Builds.OnAgentReconnectedAsync(second, "result-at-deadline");
        await harness.Builds.OnResultAsync(new BuildResult
        {
            BuildId = "result-at-deadline",
            SessionId = "session-2",
            Outcome = BuildOutcome.Succeeded,
        }, second);

        var messages = Drain(harness.Registry.Get("agent-1")!);

        Assert.Multiple(() =>
        {
            Assert.That(messages.Any(message =>
                    message.MsgCase == ControllerMsg.MsgOneofCase.ResultAccepted &&
                    message.ResultAccepted.BuildId == "result-at-deadline" &&
                    message.ResultAccepted.SessionId == "session-2"),
                Is.True);
            Assert.That(harness.BuildStore.GetAsync("result-at-deadline").Result?.Result?.Outcome,
                Is.EqualTo(BuildOutcome.InfrastructureFailed));
        });
    }

    [Test]
    public async Task Dispatch_attempt_at_reconnect_deadline_cannot_clear_the_expired_lease()
    {
        await using var harness = await LeaseHarness.StartAsync(rootDir);
        var first = harness.Connect("session-1");
        await harness.PrepareQueuedAsync("dispatch-at-deadline", first);
        var deadline = harness.Time.GetUtcNow() + LeaseHarness.Grace;
        Assert.That(
            await harness.BuildStore.TryArmReconnectGraceAsync(
                "dispatch-at-deadline",
                "agent-1",
                "session-1",
                deadline,
                harness.Time.GetUtcNow()),
            Is.True);
        harness.Time.Advance(LeaseHarness.Grace);

        Assert.That(
            await harness.Queue.RecordDispatchAttemptAsync(
                "dispatch-at-deadline",
                "agent-1",
                "session-1",
                "session-2",
                harness.Time.GetUtcNow()),
            Is.False,
            "an assignment retry must not revive a lease at its exact deadline");

        var beforeSweep = await harness.BuildStore.GetAsync("dispatch-at-deadline");
        var queue = await harness.Queue.GetAsync("dispatch-at-deadline");
        Assert.Multiple(() =>
        {
            Assert.That(beforeSweep?.OwnerSessionId, Is.EqualTo("session-1"));
            Assert.That(beforeSweep?.ReconnectDeadline, Is.EqualTo(deadline));
            Assert.That(queue?.DispatchSessionId, Is.EqualTo("session-1"));
        });

        var expired = await harness.Builds.SweepExpiredLeasesAsync(harness.Time.GetUtcNow());
        Assert.That(expired.Select(item => item.BuildId),
            Is.EquivalentTo(new[] { "dispatch-at-deadline" }));
    }

    [Test]
    public async Task Terminal_result_evicts_pending_state_but_remains_queryable_and_idempotent()
    {
        await using var harness = await LeaseHarness.StartAsync(rootDir);
        var connection = harness.Connect("session-1");
        const string buildId = "terminal-eviction";
        await harness.PrepareQueuedAsync(buildId, connection);
        harness.Builds.OnLog(new LogChunk
        {
            BuildId = buildId,
            Data = Google.Protobuf.ByteString.CopyFromUtf8("large terminal log buffer"),
        }, connection);
        var waiting = harness.Builds.WaitForResultAsync(buildId, CancellationToken.None);
        var result = new BuildResult
        {
            BuildId = buildId,
            SessionId = connection.SessionId,
            Outcome = BuildOutcome.Succeeded,
        };

        await harness.Builds.OnResultAsync(result, connection);

        Assert.That((await waiting).Outcome, Is.EqualTo(BuildOutcome.Succeeded),
            "an already-waiting caller must retain its completion source across eviction");
        var lateCancellation = await harness.Builds.CancelBuildAsync(
            ManagementRequestContext.System("test"), buildId, "too late");
        Assert.Multiple(() =>
        {
            Assert.That(harness.Builds.GetSnapshots().Any(item => item.BuildId == buildId), Is.False);
            Assert.That(harness.Builds.GetLog(buildId), Is.Empty,
                "the terminal PendingBuild and its log buffer must not be retained");
            Assert.That(lateCancellation, Is.False);
        });
        Assert.That(
            (await harness.Builds.WaitForResultAsync(buildId, CancellationToken.None)).Outcome,
            Is.EqualTo(BuildOutcome.Succeeded),
            "late readers must fall back to the durable terminal result");

        await harness.Builds.OnResultAsync(result.Clone(), connection);
        var acknowledgements = Drain(harness.Registry.Get("agent-1")!)
            .Where(message =>
                message.MsgCase == ControllerMsg.MsgOneofCase.ResultAccepted &&
                message.ResultAccepted.BuildId == buildId)
            .ToArray();
        Assert.That(acknowledgements, Has.Length.EqualTo(2),
            "both the first durable result and its retry must be acknowledged after eviction");
    }

    [Test]
    public async Task Expected_reboot_is_rejected_before_queue_or_direct_admission()
    {
        await using var harness = await LeaseHarness.StartAsync(rootDir);
        harness.Connect("session-1");
        var assignment = new BuildAssignment { BuildId = "expected-reboot" };
        assignment.Steps.Add(new Step { Program = "installer", ExpectedReboot = true });
        var queueService = new BuildQueueService(harness.Queue, harness.Registry);

        Assert.That(
            async () => await queueService.EnqueueFromControllerAsync(assignment, ""),
            Throws.TypeOf<NotSupportedException>()
                .With.Message.Contains("expected_reboot"));
        Assert.That(
            async () => await harness.Builds.DispatchBuildFromControllerAsync("agent-1", assignment),
            Throws.TypeOf<NotSupportedException>()
                .With.Message.Contains("expected_reboot"));
        Assert.That(await harness.BuildStore.GetAsync("expected-reboot"), Is.Null);
    }

    private static List<ControllerMsg> Drain(ConnectedAgent agent)
    {
        var messages = new List<ControllerMsg>();
        while (agent.Outbox.Reader.TryRead(out var message))
        {
            messages.Add(message);
        }

        return messages;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> probe)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await probe())
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new AssertionException("condition was not reached within the timeout");
    }

    private sealed class LeaseHarness : IAsyncDisposable
    {
        private LeaseHarness(
            VivariumDatabase database,
            ManualTimeProvider time,
            AgentRegistry registry,
            BuildStore buildStore,
            BuildQueueStore queue,
            BuildTracker builds,
            AgentHeartbeatMonitor monitor)
        {
            Database = database;
            Time = time;
            Registry = registry;
            BuildStore = buildStore;
            Queue = queue;
            Builds = builds;
            Monitor = monitor;
        }

        public static readonly TimeSpan Grace = TimeSpan.FromMinutes(1);
        public VivariumDatabase Database { get; }
        public ManualTimeProvider Time { get; }
        public AgentRegistry Registry { get; }
        public BuildStore BuildStore { get; }
        public BuildQueueStore Queue { get; }
        public BuildTracker Builds { get; }
        public AgentHeartbeatMonitor Monitor { get; }

        public static async Task<LeaseHarness> StartAsync(string dataDir)
        {
            var time = new ManualTimeProvider(
                new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
            var database = new VivariumDatabase(dataDir);
            var registry = new AgentRegistry(timeProvider: time);
            var buildStore = new BuildStore(database);
            var queue = new BuildQueueStore(database);
            var authorization = new ManagementCommandAuthorizer(
                new ManagementAuthorizer(), new AuditEventStore(database), time);
            var builds = new BuildTracker(
                registry,
                buildStore,
                queue,
                time,
                reconnectGrace: Grace,
                authorization: authorization);
            await builds.InitializeAsync();
            var options = new ControllerOptions
            {
                DataDir = dataDir,
                AgentHeartbeatTimeout = TimeSpan.FromSeconds(20),
                AgentReconnectGrace = Grace,
                TimeProvider = time,
            };
            var monitor = new AgentHeartbeatMonitor(
                registry, builds, options, time, NullLogger<AgentHeartbeatMonitor>.Instance);
            return new LeaseHarness(database, time, registry, buildStore, queue, builds, monitor);
        }

        public AgentConnectionHandle Connect(string sessionId)
        {
            var connection = Register(sessionId, runningBuildId: null);
            Assert.That(Registry.Reconcile(connection, currentBuildId: null), Is.True);
            return connection;
        }

        public AgentConnectionHandle Register(string sessionId, string? runningBuildId)
        {
            return Registry.Register(
                new Hello
                {
                    AgentId = "agent-1",
                    SessionId = sessionId,
                    RunningBuildId = runningBuildId ?? string.Empty,
                },
                AgentAuth.Authorized,
                enabled: true,
                new CancellationTokenSource());
        }

        public async Task PrepareQueuedAsync(
            string buildId,
            AgentConnectionHandle connection)
        {
            var assignment = new BuildAssignment { BuildId = buildId };
            await Queue.EnqueueAsync(assignment, "");
            Assert.That(await Queue.TryClaimAsync(buildId, connection.AgentId), Is.True);
            Assert.That(
                Registry.TryBeginBuild(
                    connection.AgentId, buildId, out var reserved, out var reason),
                Is.True,
                reason);
            Assert.That(reserved?.SessionId, Is.EqualTo(connection.SessionId));
            Assert.That(
                await Queue.TryPrepareDispatchAsync(
                    buildId, connection.AgentId, connection.SessionId, Time.GetUtcNow()),
                Is.True);
            Assert.That(Builds.AttachPreparedBuild(connection.AgentId, assignment), Is.True);
        }

        public ValueTask DisposeAsync() => Database.DisposeAsync();
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset utcNow;

        public ManualTimeProvider(DateTimeOffset utcNow) => this.utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => utcNow;

        public void Advance(TimeSpan delta) => utcNow += delta;
    }
}
