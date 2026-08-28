using Microsoft.Extensions.Logging.Abstractions;
using Vivarium.Agent;
using Vivarium.Contracts.V1;
using Vivarium.Controller;
using Vivarium.Controller.Agents;
using Vivarium.Controller.Builds;
using Vivarium.Controller.Persistence;
using Vivarium.Controller.Scheduling;

namespace Vivarium.Tests;

[TestFixture]
public class BuildSchedulerTests
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
    public async Task Dispatch_waits_for_all_eligibility_axes_and_wakes_when_enabled()
    {
        await using var harness = await SchedulerHarness.StartAsync(rootDir);
        var connection = Connect(
            harness.Registry,
            "agent-a",
            "session-a",
            AgentAuth.Unauthorized,
            enabled: false,
            ("os.family", "windows"));

        await harness.Queue.EnqueueAsync(
            Assignment("eligibility"),
            "os.family == windows");
        await AssertRemainsQueuedAsync(harness.Store, "eligibility");

        harness.Registry.SetAuthorized("agent-a", authorized: true);
        await AssertRemainsQueuedAsync(harness.Store, "eligibility");

        harness.Registry.SetEnabled("agent-a", enabled: true);
        var claimed = await WaitForItemAsync(
            harness.Store,
            "eligibility",
            item => item.State == BuildQueueItemState.Claimed &&
                    item.DispatchSessionId == connection.SessionId);

        await AcceptAsync(harness, claimed, connection);
        Assert.That(await harness.Store.ListPendingAsync(), Is.Empty);
    }

    [Test]
    public async Task Capacity_one_agent_dispatches_in_fifo_order()
    {
        await using var harness = await SchedulerHarness.StartAsync(rootDir);
        var connection = Connect(
            harness.Registry,
            "agent-a",
            "session-a",
            AgentAuth.Authorized,
            enabled: true,
            ("os.family", "windows"));

        await harness.Queue.EnqueueAsync(Assignment("fifo-1"), "os.family == windows");
        await harness.Queue.EnqueueAsync(Assignment("fifo-2"), "os.family == windows");

        var first = await WaitForItemAsync(
            harness.Store,
            "fifo-1",
            item => item.State == BuildQueueItemState.Claimed && item.DispatchPrepared);
        Assert.That((await harness.Store.GetAsync("fifo-2"))?.State, Is.EqualTo(BuildQueueItemState.Queued));

        await AcceptAsync(harness, first, connection);
        await harness.Builds.OnResultAsync(new BuildResult
        {
            BuildId = first.BuildId,
            SessionId = connection.SessionId,
            Outcome = BuildOutcome.Succeeded,
        }, connection);

        var second = await WaitForItemAsync(
            harness.Store,
            "fifo-2",
            item => item.State == BuildQueueItemState.Claimed && item.DispatchPrepared);
        Assert.That(second.ClaimedAgentId, Is.EqualTo("agent-a"));
    }

    [Test]
    public async Task Incompatible_head_does_not_block_a_later_runnable_build()
    {
        await using var harness = await SchedulerHarness.StartAsync(rootDir);
        var linux = Connect(
            harness.Registry,
            "linux-agent",
            "linux-session",
            AgentAuth.Authorized,
            enabled: true,
            ("os.family", "linux"));
        harness.Registry.Disconnect(linux);
        var windows = Connect(
            harness.Registry,
            "windows-agent",
            "windows-session",
            AgentAuth.Authorized,
            enabled: true,
            ("os.family", "windows"));

        await harness.Queue.EnqueueAsync(Assignment("blocked-head"), "os.family == linux");
        await harness.Queue.EnqueueAsync(Assignment("runnable-later"), "os.family == windows");

        var dispatched = await WaitForItemAsync(
            harness.Store,
            "runnable-later",
            item => item.State == BuildQueueItemState.Claimed &&
                    item.DispatchSessionId == windows.SessionId);
        var blocked = await harness.Store.GetAsync("blocked-head");
        Assert.Multiple(() =>
        {
            Assert.That(dispatched.ClaimedAgentId, Is.EqualTo("windows-agent"));
            Assert.That(blocked?.State, Is.EqualTo(BuildQueueItemState.Queued));
        });
    }

    [Test]
    public async Task Prepared_dispatch_is_resent_and_acknowledged_after_controller_restart()
    {
        const string buildId = "restart-before-ack";
        await using (var first = await SchedulerHarness.StartAsync(rootDir))
        {
            var firstConnection = Connect(
                first.Registry,
                "agent-a",
                "session-before-restart",
                AgentAuth.Authorized,
                enabled: true,
                ("os.family", "windows"));
            await first.Queue.EnqueueAsync(Assignment(buildId), "os.family == windows");
            await WaitForItemAsync(
                first.Store,
                buildId,
                item => item.DispatchSessionId == firstConnection.SessionId);
        }

        await using var restarted = await SchedulerHarness.StartAsync(rootDir);
        var newConnection = restarted.Registry.Register(
            HelloFor("agent-a", "session-after-restart", ("os.family", "windows")),
            AgentAuth.Authorized,
            enabled: true,
            new CancellationTokenSource());
        await restarted.Builds.OnAgentReconnectedAsync(newConnection, reportedBuildId: string.Empty);

        var recovered = await WaitForItemAsync(
            restarted.Store,
            buildId,
            item => item.State == BuildQueueItemState.Claimed &&
                    item.DispatchSessionId == newConnection.SessionId);
        await AcceptAsync(restarted, recovered, newConnection);
        var pending = await restarted.Store.ListPendingAsync();

        Assert.Multiple(() =>
        {
            Assert.That(recovered.DispatchPrepared, Is.True);
            Assert.That(recovered.ClaimedAgentId, Is.EqualTo("agent-a"));
            Assert.That(pending, Is.Empty);
        });
    }

    [Test]
    public async Task Matching_reconnect_completes_prepared_claim_without_scheduler_resend()
    {
        const string buildId = "restart-running-without-ack";
        await using (var first = await SchedulerHarness.StartAsync(rootDir))
        {
            var firstConnection = Connect(
                first.Registry,
                "agent-a",
                "session-before-restart",
                AgentAuth.Authorized,
                enabled: true,
                ("os.family", "windows"));
            await first.Queue.EnqueueAsync(Assignment(buildId), "os.family == windows");
            await WaitForItemAsync(
                first.Store,
                buildId,
                item => item.DispatchSessionId == firstConnection.SessionId);
        }

        await using var restarted = await SchedulerHarness.StartAsync(rootDir);
        var hello = HelloFor(
            "agent-a", "session-after-restart", ("os.family", "windows"));
        hello.RunningBuildId = buildId;
        var connection = restarted.Registry.Register(
            hello,
            AgentAuth.Authorized,
            enabled: true,
            new CancellationTokenSource());
        await restarted.Builds.OnAgentReconnectedAsync(connection, buildId);
        await Task.Delay(100);

        var item = await restarted.Store.GetAsync(buildId);
        var messages = new List<ControllerMsg>();
        while (restarted.Registry.Get("agent-a")!.Outbox.Reader.TryRead(out var message))
        {
            messages.Add(message);
        }

        Assert.Multiple(() =>
        {
            Assert.That(item?.State, Is.EqualTo(BuildQueueItemState.Removed));
            Assert.That(item?.RemovalReason, Is.EqualTo("dispatched"));
            Assert.That(messages.Any(message =>
                    message.MsgCase == ControllerMsg.MsgOneofCase.Build),
                Is.False);
        });
    }

    [Test]
    public async Task Failed_send_after_prepare_arms_grace_for_the_prepared_session()
    {
        var time = new FixedTimeProvider(
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await using var database = new VivariumDatabase(rootDir);
        var registry = new AgentRegistry(timeProvider: time);
        var store = new BuildQueueStore(database);
        var buildStore = new BuildStore(database);
        var queue = new BuildQueueService(store, registry);
        var builds = new BuildTracker(registry, buildStore, store, time);
        await builds.InitializeAsync();
        var scheduler = new BuildScheduler(
            store, queue, registry, builds, NullLogger<BuildScheduler>.Instance, time);
        Connect(
            registry,
            "agent-a",
            "session-before-prepare",
            AgentAuth.Authorized,
            enabled: true,
            ("os.family", "windows"));
        Assert.That(registry.Get("agent-a")!.Outbox.Writer.TryComplete(), Is.True);

        AgentConnectionHandle? replacement = null;
        var replaced = 0;
        void ReplaceAfterPrepare()
        {
            var build = buildStore.GetAsync("send-after-prepare").Result;
            var item = store.GetAsync("send-after-prepare").Result;
            if (build?.State != TrackedBuildState.Running ||
                item?.DispatchSessionId != "session-before-prepare" ||
                Interlocked.CompareExchange(ref replaced, 1, 0) != 0)
            {
                return;
            }

            replacement = registry.Register(
                HelloFor("agent-a", "session-after-prepare", ("os.family", "windows")),
                AgentAuth.Authorized,
                enabled: true,
                new CancellationTokenSource());
        }

        database.Changed += ReplaceAfterPrepare;
        await scheduler.StartAsync(CancellationToken.None);
        try
        {
            await queue.EnqueueAsync(
                Assignment("send-after-prepare"),
                "os.family == windows");
            await WaitUntilAsync(
                () => buildStore.GetAsync("send-after-prepare").Result?.ReconnectDeadline != null,
                TimeSpan.FromSeconds(5));

            var persisted = await buildStore.GetAsync("send-after-prepare");
            Assert.Multiple(() =>
            {
                Assert.That(replacement?.SessionId, Is.EqualTo("session-after-prepare"));
                Assert.That(persisted?.OwnerSessionId, Is.EqualTo("session-before-prepare"));
                Assert.That(persisted?.ReconnectDeadline,
                    Is.EqualTo(time.GetUtcNow() + TimeSpan.FromSeconds(60)));
            });
        }
        finally
        {
            database.Changed -= ReplaceAfterPrepare;
            await scheduler.StopAsync(CancellationToken.None);
            scheduler.Dispose();
        }
    }

    [Test]
    public async Task Failed_send_after_recorded_retry_arms_grace_without_disturbing_newer_session()
    {
        var time = new FixedTimeProvider(
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await using var database = new VivariumDatabase(rootDir);
        var registry = new AgentRegistry(timeProvider: time);
        var store = new BuildQueueStore(database);
        var buildStore = new BuildStore(database);
        var queue = new BuildQueueService(store, registry);
        var builds = new BuildTracker(registry, buildStore, store, time);
        await builds.InitializeAsync();
        var first = Connect(
            registry,
            "agent-a",
            "session-original",
            AgentAuth.Authorized,
            enabled: true,
            ("os.family", "windows"));
        var assignment = Assignment("send-after-record");
        await store.EnqueueAsync(assignment, "os.family == windows");
        Assert.That(await store.TryClaimAsync(assignment.BuildId, first.AgentId), Is.True);
        Assert.That(
            registry.TryBeginBuild(
                first.AgentId, assignment.BuildId, out var reserved, out var reason),
            Is.True,
            reason);
        Assert.That(
            await store.TryPrepareDispatchAsync(
                assignment.BuildId, first.AgentId, reserved!.SessionId, time.GetUtcNow()),
            Is.True);
        Assert.That(builds.AttachPreparedBuild(first.AgentId, assignment), Is.True);

        var retry = registry.Register(
            HelloFor("agent-a", "session-retry", ("os.family", "windows")),
            AgentAuth.Authorized,
            enabled: true,
            new CancellationTokenSource());
        Assert.That(registry.Reconcile(retry, assignment.BuildId), Is.True);
        Assert.That(registry.Get("agent-a")!.Outbox.Writer.TryComplete(), Is.True);

        AgentConnectionHandle? replacement = null;
        var replaced = 0;
        void ReplaceAfterRecord()
        {
            var build = buildStore.GetAsync(assignment.BuildId).Result;
            var item = store.GetAsync(assignment.BuildId).Result;
            if (build?.OwnerSessionId != retry.SessionId ||
                item?.DispatchSessionId != retry.SessionId ||
                Interlocked.CompareExchange(ref replaced, 1, 0) != 0)
            {
                return;
            }

            replacement = registry.Register(
                HelloFor("agent-a", "session-newer", ("os.family", "windows")),
                AgentAuth.Authorized,
                enabled: true,
                new CancellationTokenSource());
        }

        database.Changed += ReplaceAfterRecord;
        var scheduler = new BuildScheduler(
            store, queue, registry, builds, NullLogger<BuildScheduler>.Instance, time);
        await scheduler.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(
                () => buildStore.GetAsync(assignment.BuildId).Result?.ReconnectDeadline != null,
                TimeSpan.FromSeconds(5));

            var persisted = await buildStore.GetAsync(assignment.BuildId);
            Assert.Multiple(() =>
            {
                Assert.That(replacement?.SessionId, Is.EqualTo("session-newer"));
                Assert.That(registry.Get("agent-a")?.SessionId, Is.EqualTo("session-newer"));
                Assert.That(persisted?.OwnerSessionId, Is.EqualTo("session-retry"));
                Assert.That(persisted?.ReconnectDeadline,
                    Is.EqualTo(time.GetUtcNow() + TimeSpan.FromSeconds(60)));
            });
        }
        finally
        {
            database.Changed -= ReplaceAfterRecord;
            await scheduler.StopAsync(CancellationToken.None);
            scheduler.Dispose();
        }
    }

    [Test]
    public async Task Queued_build_is_dispatched_acknowledged_and_reported_by_a_real_agent()
    {
        await using var controller = await VivariumControllerHost.StartAsync(new ControllerOptions
        {
            DataDir = Path.Combine(rootDir, "controller"),
            Host = "127.0.0.1",
            Port = 0,
        });
        var agent = new AgentRunner(new AgentOptions
        {
            ControllerUrl = controller.Url,
            CertFingerprintSha256 = controller.Certificate.FingerprintSha256,
            EnrollToken = await controller.Tokens.CreateEnrollTokenAsync(),
            DataDir = Path.Combine(rootDir, "agent"),
            HeartbeatInterval = TimeSpan.FromMilliseconds(200),
            ReconnectDelay = TimeSpan.FromMilliseconds(200),
        });

        using var stopping = new CancellationTokenSource();
        var agentTask = agent.RunAsync(stopping.Token);
        try
        {
            await WaitUntilAsync(
                () => controller.Registry.Get(agent.AgentId) is { Connected: true, Reconciled: true },
                TimeSpan.FromSeconds(20));
            await controller.AuthorizeAgentAsync(agent.AgentId);
            await agent.WaitAuthorizedAsync(TimeSpan.FromSeconds(20));

            await controller.BuildQueue.EnqueueAsync(
                Assignment("real-agent-queue"),
                $"os.family == {(OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux")}");
            await WaitUntilAsync(
                () => controller.BuildStore.GetAsync("real-agent-queue").GetAwaiter().GetResult()?.Result != null,
                TimeSpan.FromSeconds(20));

            var persisted = await controller.BuildStore.GetAsync("real-agent-queue");
            Assert.Multiple(() =>
            {
                Assert.That(persisted?.Result?.Outcome, Is.EqualTo(BuildOutcome.Succeeded));
                Assert.That(controller.BuildQueueStore.ListPendingAsync().GetAwaiter().GetResult(), Is.Empty);
            });
        }
        finally
        {
            stopping.Cancel();
            try
            {
                await agentTask;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private static AgentConnectionHandle Connect(
        AgentRegistry registry,
        string agentId,
        string sessionId,
        AgentAuth authorization,
        bool enabled,
        params (string Key, string Value)[] parameters)
    {
        var connection = registry.Register(
            HelloFor(agentId, sessionId, parameters),
            authorization,
            enabled,
            new CancellationTokenSource());
        Assert.That(registry.Reconcile(connection, currentBuildId: null), Is.True);
        return connection;
    }

    private static Hello HelloFor(
        string agentId,
        string sessionId,
        params (string Key, string Value)[] parameters)
    {
        var hello = new Hello { AgentId = agentId, SessionId = sessionId };
        foreach (var (key, value) in parameters)
        {
            hello.Parameters[key] = value;
        }

        return hello;
    }

    private static BuildAssignment Assignment(string buildId) => new() { BuildId = buildId };

    private static async Task AcceptAsync(
        SchedulerHarness harness,
        BuildQueueItem item,
        AgentConnectionHandle connection)
    {
        await harness.Builds.OnAssignmentAcceptedAsync(new AssignmentAccepted
        {
            BuildId = item.BuildId,
            SessionId = connection.SessionId,
        }, connection);
    }

    private static async Task AssertRemainsQueuedAsync(BuildQueueStore store, string buildId)
    {
        await Task.Delay(100);
        Assert.That((await store.GetAsync(buildId))?.State, Is.EqualTo(BuildQueueItemState.Queued));
    }

    private static async Task<BuildQueueItem> WaitForItemAsync(
        BuildQueueStore store,
        string buildId,
        Func<BuildQueueItem, bool> predicate)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var item = await store.GetAsync(buildId);
            if (item != null && predicate(item))
            {
                return item;
            }

            await Task.Delay(20);
        }

        throw new AssertionException($"build '{buildId}' did not reach the expected queue state");
    }

    private static async Task WaitUntilAsync(Func<bool> probe, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (probe())
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new AssertionException("condition was not reached within the timeout");
    }

    private sealed class SchedulerHarness : IAsyncDisposable
    {
        private SchedulerHarness(
            VivariumDatabase database,
            AgentRegistry registry,
            BuildQueueStore store,
            BuildQueueService queue,
            BuildTracker builds,
            BuildScheduler scheduler)
        {
            Database = database;
            Registry = registry;
            Store = store;
            Queue = queue;
            Builds = builds;
            Scheduler = scheduler;
        }

        public VivariumDatabase Database { get; }
        public AgentRegistry Registry { get; }
        public BuildQueueStore Store { get; }
        public BuildQueueService Queue { get; }
        public BuildTracker Builds { get; }
        public BuildScheduler Scheduler { get; }

        public static async Task<SchedulerHarness> StartAsync(string dataDir)
        {
            var database = new VivariumDatabase(dataDir);
            var registry = new AgentRegistry();
            var store = new BuildQueueStore(database);
            var queue = new BuildQueueService(store, registry);
            var builds = new BuildTracker(registry, new BuildStore(database), store);
            await builds.InitializeAsync();
            var scheduler = new BuildScheduler(
                store, queue, registry, builds, NullLogger<BuildScheduler>.Instance);
            await scheduler.StartAsync(CancellationToken.None);
            return new SchedulerHarness(database, registry, store, queue, builds, scheduler);
        }

        public async ValueTask DisposeAsync()
        {
            await Scheduler.StopAsync(CancellationToken.None);
            Scheduler.Dispose();
            await Database.DisposeAsync();
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset utcNow;

        public FixedTimeProvider(DateTimeOffset utcNow) => this.utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
