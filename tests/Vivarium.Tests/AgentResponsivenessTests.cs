using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Vivarium.Agent;
using Vivarium.Contracts.V1;
using Vivarium.Controller.Agents;
using Vivarium.Controller.Auditing;
using Vivarium.Controller.Builds;
using Vivarium.Controller.Persistence;
using Vivarium.Controller.Security;

namespace Vivarium.Tests;

[TestFixture]
public sealed class AgentResponsivenessTests
{
    private string rootDir = null!;

    [SetUp]
    public void SetUp()
    {
        rootDir = Path.Combine(
            Path.GetTempPath(), "vivarium-responsiveness-tests", Guid.NewGuid().ToString("N"));
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
            // Preserve the original test failure when the OS delays releasing a process or database.
        }
    }

    [Test]
    public void Build_stop_signal_is_monotonic_and_preserves_first_reason_and_deadline()
    {
        using var stop = new BuildStopController();
        var firstDeadline = DateTimeOffset.UtcNow.AddMinutes(1);

        Assert.That(
            stop.Request(BuildStopMode.Graceful, "first reason", firstDeadline),
            Is.EqualTo(BuildStopMode.Graceful));
        Assert.Multiple(() =>
        {
            Assert.That(stop.GracefulToken.IsCancellationRequested, Is.True);
            Assert.That(stop.ForceToken.IsCancellationRequested, Is.False);
            Assert.That(stop.Reason, Is.EqualTo("first reason"));
            Assert.That(stop.Deadline, Is.EqualTo(firstDeadline));
        });

        stop.Request(BuildStopMode.Graceful, "replacement reason", firstDeadline.AddMinutes(1));
        var forceDeadline = firstDeadline.AddMinutes(1);
        stop.Request(BuildStopMode.Force, "force reason", forceDeadline);
        stop.Request(BuildStopMode.Force, "later force reason", forceDeadline.AddMinutes(1));
        stop.Request(BuildStopMode.Graceful, "weaker reason", firstDeadline.AddMinutes(2));

        Assert.Multiple(() =>
        {
            Assert.That(stop.Mode, Is.EqualTo(BuildStopMode.Force));
            Assert.That(stop.GracefulToken.IsCancellationRequested, Is.True);
            Assert.That(stop.ForceToken.IsCancellationRequested, Is.True);
            Assert.That(stop.Reason, Is.EqualTo("first reason"));
            Assert.That(stop.Deadline, Is.EqualTo(forceDeadline));
        });
    }

    [Test]
    public void Active_build_journal_survives_restart_and_rejects_ambiguous_identity()
    {
        var journal = new ActiveBuildJournal(rootDir);
        journal.Accept("build-1");

        var reopened = new ActiveBuildJournal(rootDir);
        Assert.Multiple(() =>
        {
            Assert.That(reopened.Current?.BuildId, Is.EqualTo("build-1"));
            Assert.That(reopened.Current?.ProcessId, Is.Null);
        });

        reopened.Complete("different-build");
        Assert.That(new ActiveBuildJournal(rootDir).Current?.BuildId, Is.EqualTo("build-1"));
        reopened.Complete("build-1");
        Assert.That(new ActiveBuildJournal(rootDir).Current, Is.Null);

        File.WriteAllText(
            Path.Combine(rootDir, "active-build.json"),
            "{\"schemaVersion\":1,\"buildId\":\"build-2\",\"processId\":42}");
        Assert.That(() => new ActiveBuildJournal(rootDir),
            Throws.TypeOf<InvalidDataException>().With.Message.Contains("malformed"));
    }

    [Test]
    public async Task Session_writer_prioritizes_control_over_queued_logs()
    {
        var stream = new GatedAgentStreamWriter();
        var writer = new SessionWriter(stream);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await writer.SendAsync(Log("first"), CancellationToken.None);
        await writer.SendAsync(Log("second"), CancellationToken.None);
        var control = writer.SendAsync(new AgentMsg
        {
            Heartbeat = new Heartbeat { Sequence = 1 },
        }, CancellationToken.None);

        stream.ReleaseFirstWrite.TrySetResult(true);
        var pump = writer.RunAsync(CancellationToken.None);
        await control.WaitAsync(cancellation.Token);
        await WaitUntilAsync(() => stream.Messages.Count >= 3, cancellation.Token);
        writer.Complete();
        await pump;

        Assert.That(
            stream.Messages.Select(message => message.MsgCase),
            Is.EqualTo(new[]
            {
                AgentMsg.MsgOneofCase.Heartbeat,
                AgentMsg.MsgOneofCase.Log,
                AgentMsg.MsgOneofCase.Log,
            }));
    }

    [Test]
    public void Session_writer_sheds_logs_at_its_byte_limit_without_blocking_producers()
    {
        var writer = new SessionWriter(new GatedAgentStreamWriter());
        var payload = ByteString.CopyFrom(new byte[700 * 1024]);

        Assert.That(writer.SendAsync(new AgentMsg
        {
            Log = new LogChunk { BuildId = "build", Data = payload },
        }, CancellationToken.None).IsCompletedSuccessfully, Is.True);
        Assert.That(writer.SendAsync(new AgentMsg
        {
            Log = new LogChunk { BuildId = "build", Data = payload },
        }, CancellationToken.None).IsCompletedSuccessfully, Is.True);

        Assert.That(writer.DroppedLogBytes, Is.EqualTo(payload.Length));
        writer.Complete();
    }

    [Test]
    public async Task Graceful_deadline_quarantines_without_force_then_explicit_force_reopens_operation()
    {
        var time = new ManualTimeProvider(
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await using var database = new VivariumDatabase(rootDir);
        var store = new BuildStore(database);
        var registry = new AgentRegistry(timeProvider: time);
        var assignment = new BuildAssignment { BuildId = "stop-build" };
        await store.CreateAsync("agent-1", "session-1", assignment, time.GetUtcNow());
        var tracker = new BuildTracker(
            registry,
            store,
            timeProvider: time,
            gracefulStopTimeout: TimeSpan.FromSeconds(10),
            forceStopTimeout: TimeSpan.FromSeconds(5));
        await tracker.InitializeAsync();
        using var session = new CancellationTokenSource();
        var connection = registry.Register(
            new Hello { AgentId = "agent-1", SessionId = "session-1" },
            AgentAuth.Authorized,
            enabled: true,
            connectionGeneration: 1,
            session);
        Assert.That(registry.Reconcile(connection, assignment.BuildId), Is.True);

        Assert.That(await tracker.StopBuildFromControllerAsync(
            assignment.BuildId, "first reason", BuildStopMode.Graceful), Is.True);
        var graceful = ReadRequired(registry.Get("agent-1")!);
        var firstOperationId = graceful.Cancel.OperationId;
        await tracker.OnBuildStopAcknowledgedAsync(new BuildStopAcknowledged
        {
            BuildId = assignment.BuildId,
            OperationId = firstOperationId,
            Mode = BuildStopMode.Force,
            SessionId = connection.SessionId,
        }, connection);
        Assert.That((await store.GetAsync(assignment.BuildId))?.StopAcknowledged, Is.False,
            "a force acknowledgement cannot upgrade a graceful authorization");
        await tracker.OnBuildStopAcknowledgedAsync(new BuildStopAcknowledged
        {
            BuildId = assignment.BuildId,
            OperationId = firstOperationId,
            Mode = BuildStopMode.Graceful,
            SessionId = connection.SessionId,
        }, connection);

        time.Advance(TimeSpan.FromSeconds(10));
        await tracker.SweepDueStopsAsync(time.GetUtcNow());
        Assert.Multiple(() =>
        {
            Assert.That(registry.Get("agent-1")?.Quarantined, Is.True);
            Assert.That(registry.Get("agent-1")?.OperationalReason,
                Is.EqualTo("graceful_stop_deadline_expired"));
            Assert.That(registry.Get("agent-1")!.Outbox.Reader.TryRead(out _), Is.False,
                "graceful timeout must not synthesize an unauthorized force command");
        });

        Assert.That(await tracker.StopBuildFromControllerAsync(
            assignment.BuildId, "replacement reason", BuildStopMode.Force), Is.True);
        var force = ReadRequired(registry.Get("agent-1")!);
        var persisted = await store.GetAsync(assignment.BuildId);
        Assert.Multiple(() =>
        {
            Assert.That(force.Cancel.Mode, Is.EqualTo(BuildStopMode.Force));
            Assert.That(force.Cancel.OperationId, Is.EqualTo(firstOperationId));
            Assert.That(persisted?.CancellationReason, Is.EqualTo("first reason"));
            Assert.That(persisted?.StopDeadline,
                Is.EqualTo(time.GetUtcNow().AddSeconds(5)));
            Assert.That(persisted?.StopAcknowledged, Is.False);
        });
    }

    [Test]
    public async Task Assignment_ack_deadline_quarantines_and_requests_containment()
    {
        var time = new ManualTimeProvider(
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await using var database = new VivariumDatabase(rootDir);
        var store = new BuildStore(database);
        var assignment = new BuildAssignment { BuildId = "ambiguous-assignment" };
        await store.CreateAsync(
            "agent-1",
            "session-1",
            assignment,
            time.GetUtcNow(),
            time.GetUtcNow().AddSeconds(5));
        var registry = new AgentRegistry(timeProvider: time);
        var tracker = new BuildTracker(registry, store, timeProvider: time);
        await tracker.InitializeAsync();
        using var session = new CancellationTokenSource();
        var connection = registry.Register(
            new Hello { AgentId = "agent-1", SessionId = "session-1" },
            AgentAuth.Authorized,
            enabled: true,
            connectionGeneration: 1,
            session);
        Assert.That(registry.Reconcile(connection, assignment.BuildId), Is.True);

        time.Advance(TimeSpan.FromSeconds(5));
        await tracker.SweepDueAssignmentAttemptsAsync(time.GetUtcNow());

        var containment = ReadRequired(registry.Get("agent-1")!);
        Assert.Multiple(() =>
        {
            Assert.That(registry.Get("agent-1")?.Quarantined, Is.True);
            Assert.That(registry.Get("agent-1")?.OperationalReason,
                Is.EqualTo("assignment_acknowledgement_expired"));
            Assert.That(containment.Cancel.BuildId, Is.EqualTo(assignment.BuildId));
            Assert.That(containment.Cancel.Mode, Is.EqualTo(BuildStopMode.Force));
        });
    }

    [Test]
    public async Task Matching_workload_heartbeat_closes_a_lost_assignment_ack()
    {
        var time = new ManualTimeProvider(
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await using var database = new VivariumDatabase(rootDir);
        var store = new BuildStore(database);
        var assignment = new BuildAssignment { BuildId = "heartbeat-accepted" };
        await store.CreateAsync(
            "agent-1",
            "session-1",
            assignment,
            time.GetUtcNow(),
            time.GetUtcNow().AddSeconds(5));
        var registry = new AgentRegistry(timeProvider: time);
        var tracker = new BuildTracker(registry, store, timeProvider: time);
        await tracker.InitializeAsync();
        using var session = new CancellationTokenSource();
        var connection = registry.Register(
            new Hello { AgentId = "agent-1", SessionId = "session-1" },
            AgentAuth.Authorized,
            enabled: true,
            connectionGeneration: 1,
            session);
        Assert.That(registry.Reconcile(connection, assignment.BuildId), Is.True);

        await tracker.OnHeartbeatAsync(new Heartbeat
        {
            RunningBuildId = assignment.BuildId,
            Sequence = 1,
        }, connection);
        time.Advance(TimeSpan.FromSeconds(5));
        await tracker.SweepDueAssignmentAttemptsAsync(time.GetUtcNow());

        Assert.Multiple(() =>
        {
            Assert.That(registry.Get("agent-1")?.Quarantined, Is.False);
            Assert.That((store.GetAsync(assignment.BuildId).Result)?.StopOperationId, Is.Null);
        });
    }

    [Test]
    public async Task Reconnect_without_an_acknowledged_owned_workload_quarantines_immediately()
    {
        var time = new ManualTimeProvider(
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await using var database = new VivariumDatabase(rootDir);
        var store = new BuildStore(database);
        var assignment = new BuildAssignment { BuildId = "missing-on-reconnect" };
        await store.CreateAsync("agent-1", "session-1", assignment, time.GetUtcNow());
        var registry = new AgentRegistry(timeProvider: time);
        var tracker = new BuildTracker(registry, store, timeProvider: time);
        await tracker.InitializeAsync();

        using var firstSession = new CancellationTokenSource();
        var first = registry.Register(
            new Hello { AgentId = "agent-1", SessionId = "session-1" },
            AgentAuth.Authorized,
            enabled: true,
            connectionGeneration: 1,
            firstSession);
        Assert.That(registry.Reconcile(first, assignment.BuildId), Is.True);
        registry.Disconnect(first);

        using var replacementSession = new CancellationTokenSource();
        var replacement = registry.Register(
            new Hello { AgentId = "agent-1", SessionId = "session-2" },
            AgentAuth.Authorized,
            enabled: true,
            connectionGeneration: 2,
            replacementSession);
        await tracker.OnAgentReconnectedAsync(replacement, reportedBuildId: "");

        Assert.Multiple(() =>
        {
            Assert.That(registry.Get("agent-1")?.Quarantined, Is.True);
            Assert.That(registry.Get("agent-1")?.OperationalReason,
                Is.EqualTo("workload_assertion_mismatch"));
            Assert.That(registry.Get("agent-1")?.CurrentBuildId, Is.EqualTo(assignment.BuildId));
        });
    }

    [Test]
    public async Task Restart_requires_supervision_and_a_new_bootstrap_child_process()
    {
        var time = new ManualTimeProvider(
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await using var database = new VivariumDatabase(rootDir);
        await InsertAgentAsync(database, "agent-1");
        var store = new AgentRestartStore(database);
        var registry = new AgentRegistry(timeProvider: time);
        var authorization = new ManagementCommandAuthorizer(
            new ManagementAuthorizer(), new AuditEventStore(database), time);
        var service = new AgentRestartService(
            store, registry, authorization, time, NullLogger<AgentRestartService>.Instance);
        using var firstSession = new CancellationTokenSource();
        var firstProcessInstance = new string('a', 32);
        var first = registry.Register(
            SupervisedHello("agent-1", "session-1", firstProcessInstance),
            AgentAuth.Authorized,
            enabled: true,
            connectionGeneration: 1,
            firstSession);
        Assert.That(registry.Reconcile(first, currentBuildId: null), Is.True);
        var context = ManagementRequestContext.System("test", "restart-request");

        var operation = await service.CreateAsync(
            context,
            "agent-1",
            AgentRestartMode.AfterCurrentWork,
            "operator restart",
            TimeSpan.FromMinutes(1));
        var command = ReadRequired(registry.Get("agent-1")!);
        var hasActiveOperation = await service.HasActiveAsync("agent-1");
        Assert.Multiple(() =>
        {
            Assert.That(command.Restart.OperationId, Is.EqualTo(operation.OperationId));
            Assert.That(hasActiveOperation, Is.True);
            Assert.That(registry.Get("agent-1")?.MaintenanceDrain, Is.True);
            Assert.That(registry.TryBeginBuild("agent-1", "restart-race", out _), Is.False);
        });

        await service.OnAcknowledgedAsync(new AgentRestartAcknowledged
        {
            OperationId = operation.OperationId,
            Mode = AgentRestartMode.AfterCurrentWork,
            SessionId = first.SessionId,
        }, first);
        Assert.That((await store.FindAsync(operation.OperationId))?.State,
            Is.EqualTo(AgentRestartState.Acknowledged));

        registry.Disconnect(first);
        var replay = await service.CreateAsync(
            context,
            "agent-1",
            AgentRestartMode.AfterCurrentWork,
            "operator restart",
            TimeSpan.FromMinutes(1));
        Assert.That(replay.OperationId, Is.EqualTo(operation.OperationId),
            "idempotent replay must not require the old session to remain connected");

        using var sameProcessSession = new CancellationTokenSource();
        var sameProcess = registry.Register(
            SupervisedHello("agent-1", "session-2", firstProcessInstance),
            AgentAuth.Authorized,
            enabled: true,
            connectionGeneration: 2,
            sameProcessSession);
        Assert.That(registry.Reconcile(sameProcess, currentBuildId: null), Is.True);
        Assert.That(await service.OnAgentConnectedAsync(sameProcess), Is.False,
            "a network reconnect by the same Agent process is not restart proof");
        Assert.That((await store.FindAsync(operation.OperationId))?.State,
            Is.EqualTo(AgentRestartState.Acknowledged));

        registry.Disconnect(sameProcess);
        registry.Quarantine("agent-1", "prior_workload_ambiguity");
        using var restartedSession = new CancellationTokenSource();
        var restarted = registry.Register(
            SupervisedHello("agent-1", "session-3", new string('b', 32)),
            AgentAuth.Authorized,
            enabled: true,
            connectionGeneration: 3,
            restartedSession);
        Assert.That(registry.Reconcile(restarted, currentBuildId: "ambiguous-build"), Is.True);
        Assert.That(await service.OnAgentConnectedAsync(restarted), Is.True);
        Assert.That(
            registry.TryClearQuarantineAfterRestart(
                restarted, reportedBuildId: "", reason: "restart_reconciled"),
            Is.False,
            "a new process must not erase a workload assertion mismatch");
        Assert.That(registry.Get("agent-1")?.Quarantined, Is.True);
        Assert.That(registry.Reconcile(restarted, currentBuildId: null), Is.True);
        Assert.That(
            registry.TryClearQuarantineAfterRestart(
                restarted, reportedBuildId: "", reason: "restart_reconciled"),
            Is.True);
        var completed = await store.FindAsync(operation.OperationId);
        Assert.Multiple(() =>
        {
            Assert.That(completed?.State, Is.EqualTo(AgentRestartState.Succeeded));
            Assert.That(completed?.ObservedConnectionGeneration, Is.EqualTo(3));
            Assert.That(completed?.ObservedProcessInstanceId, Is.EqualTo(new string('b', 32)));
            Assert.That(registry.Get("agent-1")?.Quarantined, Is.False);
        });
    }

    [Test]
    public async Task Restart_rejects_an_agent_without_a_bootstrap_supervisor()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        await using var database = new VivariumDatabase(rootDir);
        await InsertAgentAsync(database, "agent-1");
        var registry = new AgentRegistry(timeProvider: time);
        using var session = new CancellationTokenSource();
        var connection = registry.Register(
            new Hello { AgentId = "agent-1", SessionId = "session-1" },
            AgentAuth.Authorized,
            enabled: true,
            connectionGeneration: 1,
            session);
        registry.Reconcile(connection, currentBuildId: null);
        var service = new AgentRestartService(
            new AgentRestartStore(database),
            registry,
            new ManagementCommandAuthorizer(
                new ManagementAuthorizer(), new AuditEventStore(database), time),
            time,
            NullLogger<AgentRestartService>.Instance);

        var exception = Assert.ThrowsAsync<AgentRestartUnavailableException>(() =>
            service.CreateAsync(
                ManagementRequestContext.System("test", "restart-request"),
                "agent-1",
                AgentRestartMode.Force,
                "restart",
                TimeSpan.FromMinutes(1)));
        Assert.That(exception?.Reason, Is.EqualTo("agent_restart_supervision_unavailable"));
    }

    private static AgentMsg Log(string text) => new()
    {
        Log = new LogChunk
        {
            BuildId = "build",
            Data = ByteString.CopyFromUtf8(text),
        },
    };

    private static ControllerMsg ReadRequired(ConnectedAgent agent)
    {
        Assert.That(agent.Outbox.Reader.TryRead(out var message), Is.True);
        return message!;
    }

    private static Hello SupervisedHello(
        string agentId,
        string sessionId,
        string processInstanceId)
    {
        var hello = new Hello
        {
            AgentId = agentId,
            SessionId = sessionId,
            ProcessInstanceId = processInstanceId,
        };
        hello.Capabilities.Add(new CapabilitySupport
        {
            CapabilityId = "vivarium.bootstrap-supervisor.v1",
            ContractMajor = 1,
        });
        return hello;
    }

    private static Task InsertAgentAsync(VivariumDatabase database, string agentId) =>
        database.WriteAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO agents(agent_id, name, authorized, first_seen_unix_ms, last_seen_unix_ms)
                VALUES($agentId, $agentId, 1, 1, 1);
                """;
            command.Parameters.AddWithValue("$agentId", agentId);
            command.ExecuteNonQuery();
            return true;
        });

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition())
        {
            await Task.Delay(10, cancellationToken);
        }
    }

    private sealed class GatedAgentStreamWriter : IClientStreamWriter<AgentMsg>
    {
        private readonly object gate = new();
        private readonly List<AgentMsg> messages = [];
        private int writes;

        public WriteOptions? WriteOptions { get; set; }
        public TaskCompletionSource<bool> FirstWriteStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ReleaseFirstWrite { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<AgentMsg> Messages
        {
            get
            {
                lock (gate)
                {
                    return messages.Select(message => message.Clone()).ToArray();
                }
            }
        }

        public Task CompleteAsync() => Task.CompletedTask;

        public async Task WriteAsync(AgentMsg message)
        {
            lock (gate)
            {
                messages.Add(message.Clone());
            }
            if (Interlocked.Increment(ref writes) == 1)
            {
                FirstWriteStarted.TrySetResult(true);
                await ReleaseFirstWrite.Task;
            }
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
        public void Advance(TimeSpan delta) => utcNow += delta;
    }
}
