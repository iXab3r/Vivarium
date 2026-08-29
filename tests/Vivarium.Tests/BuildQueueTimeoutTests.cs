using Microsoft.Extensions.Logging.Abstractions;
using Vivarium.Contracts.V1;
using Vivarium.Controller;
using Vivarium.Controller.Agents;
using Vivarium.Controller.Auditing;
using Vivarium.Controller.Blobs;
using Vivarium.Controller.Builds;
using Vivarium.Controller.Management;
using Vivarium.Controller.Persistence;
using Vivarium.Controller.Scheduling;
using Vivarium.Controller.Security;

namespace Vivarium.Tests;

[TestFixture]
[NonParallelizable]
public class BuildQueueTimeoutTests
{
    private static readonly DateTimeOffset TestNow =
        new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);

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
            // Best effort on Windows, where SQLite or Kestrel may release a handle slightly later.
        }
    }

    [Test]
    public async Task Matrix_submission_persists_default_and_per_cell_deadlines_and_rejects_negative()
    {
        var time = new ManualTimeProvider(TestNow);
        var defaultTimeout = TimeSpan.FromMinutes(12);
        await using var fixture = await MatrixFixture.CreateAsync(
            rootDir, time, defaultTimeout);
        var request = Request("deadline-request", 0, 45);

        var build = await fixture.Submissions.SubmitAsync(
            ManagementRequestContext.System("test"), request);
        var pending = await fixture.QueueStore.ListPendingAsync();
        var snapshot = await fixture.MatrixBuilds.GetSnapshotAsync(build.BuildId);

        Assert.Multiple(() =>
        {
            Assert.That(pending, Has.Count.EqualTo(2));
            Assert.That(pending[0].QueueDeadline, Is.EqualTo(TestNow + defaultTimeout));
            Assert.That(pending[1].QueueDeadline, Is.EqualTo(TestNow + TimeSpan.FromSeconds(45)));
            Assert.That(snapshot!.Cells[0].QueueDeadlineUnixMs,
                Is.EqualTo((TestNow + defaultTimeout).ToUnixTimeMilliseconds()));
            Assert.That(snapshot.Cells[1].QueueDeadlineUnixMs,
                Is.EqualTo((TestNow + TimeSpan.FromSeconds(45)).ToUnixTimeMilliseconds()));
        });

        var invalid = Request("negative-deadline", -1);
        var exception = Assert.ThrowsAsync<MatrixBuildValidationException>(async () =>
            await fixture.Submissions.SubmitAsync(ManagementRequestContext.System("test"), invalid));
        Assert.That(exception!.Message, Does.Contain("queue_timeout_sec cannot be negative"));
        Assert.That(await fixture.QueueStore.ListPendingAsync(), Has.Count.EqualTo(2));
    }

    [Test]
    public async Task Sweep_expires_at_boundary_but_not_before_and_persists_infrastructure_failure()
    {
        await using var database = new VivariumDatabase(rootDir);
        var store = new BuildQueueStore(database);
        var time = new ManualTimeProvider(TestNow);
        var service = new BuildQueueService(
            store, new AgentRegistry(), time, TimeSpan.FromSeconds(10));
        var monitor = new BuildQueueTimeoutMonitor(
            store, service, time, NullLogger<BuildQueueTimeoutMonitor>.Instance);
        var deadline = TestNow + TimeSpan.FromSeconds(10);
        await store.EnqueueAsync(
            Assignment("boundary"), string.Empty, TestNow, TimeSpan.FromSeconds(10));

        Assert.That(await monitor.SweepOnceAsync(deadline.AddMilliseconds(-1)), Is.Empty);
        Assert.That((await store.GetAsync("boundary"))!.State, Is.EqualTo(BuildQueueItemState.Queued));

        Assert.That(await store.TryClaimAsync("boundary", "agent-at-boundary", deadline), Is.False,
            "a claim at the exact deadline cannot outrun expiry");
        Assert.That(await monitor.SweepOnceAsync(deadline), Is.EqualTo(new[] { "boundary" }));

        var removed = await store.GetAsync("boundary");
        var build = await new BuildStore(database).GetAsync("boundary");
        Assert.Multiple(() =>
        {
            Assert.That(removed!.State, Is.EqualTo(BuildQueueItemState.Removed));
            Assert.That(removed.RemovalReason, Is.EqualTo(BuildQueueStore.QueueTimeoutReason));
            Assert.That(build!.State, Is.EqualTo(TrackedBuildState.Finished));
            Assert.That(build.Result!.Outcome, Is.EqualTo(BuildOutcome.InfrastructureFailed));
            Assert.That(build.Result.StatusText, Is.EqualTo(BuildQueueStore.QueueTimeoutReason));
            Assert.That(build.CancellationReason, Is.Null);
            Assert.That(build.AgentId, Is.Null);
        });
    }

    [Test]
    public async Task Expiring_an_unprepared_claim_releases_agent_capacity()
    {
        await using var database = new VivariumDatabase(rootDir);
        var store = new BuildQueueStore(database);
        await store.EnqueueAsync(
            Assignment("due-claim"), string.Empty, TestNow, TimeSpan.FromSeconds(5));
        await store.EnqueueAsync(
            Assignment("next-claim"), string.Empty, TestNow, TimeSpan.FromHours(1));
        Assert.That(await store.TryClaimAsync("due-claim", "capacity-one", TestNow), Is.True);

        Assert.That(
            await store.ExpireDueAsync(TestNow + TimeSpan.FromSeconds(5)),
            Is.EqualTo(new[] { "due-claim" }));
        Assert.That(
            await store.TryClaimAsync(
                "next-claim", "capacity-one", TestNow + TimeSpan.FromSeconds(5)),
            Is.True,
            "REMOVED rows must release the partial unique claim on an agent");

        var expired = await store.GetAsync("due-claim");
        Assert.Multiple(() =>
        {
            Assert.That(expired!.State, Is.EqualTo(BuildQueueItemState.Removed));
            Assert.That(expired.ClaimedAgentId, Is.EqualTo("capacity-one"));
            Assert.That(expired.DispatchPrepared, Is.False);
        });
    }

    [Test]
    public async Task Prepare_and_expiry_are_fenced_and_running_work_never_expires()
    {
        await using var database = new VivariumDatabase(rootDir);
        var store = new BuildQueueStore(database);
        var deadline = TestNow + TimeSpan.FromSeconds(10);

        await store.EnqueueAsync(
            Assignment("boundary-prepare"), string.Empty, TestNow, TimeSpan.FromSeconds(10));
        Assert.That(await store.TryClaimAsync("boundary-prepare", "agent-boundary", TestNow), Is.True);
        Assert.That(
            await store.TryPrepareDispatchAsync(
                "boundary-prepare", "agent-boundary", "session-boundary", deadline),
            Is.False,
            "dispatch preparation at the exact deadline must be rejected");
        Assert.That(await store.ExpireDueAsync(deadline), Does.Contain("boundary-prepare"));

        await store.EnqueueAsync(
            Assignment("prepare-wins"), string.Empty, TestNow, TimeSpan.FromSeconds(10));
        Assert.That(await store.TryClaimAsync("prepare-wins", "agent-prepare", TestNow), Is.True);
        var prepareFirst = store.TryPrepareDispatchAsync(
            "prepare-wins", "agent-prepare", "session-prepare", deadline.AddMilliseconds(-1));
        var expireSecond = store.ExpireDueAsync(deadline);
        await Task.WhenAll(prepareFirst, expireSecond);
        Assert.Multiple(() =>
        {
            Assert.That(prepareFirst.Result, Is.True);
            Assert.That(expireSecond.Result, Does.Not.Contain("prepare-wins"));
        });
        Assert.That(
            await store.ExpireDueAsync(deadline + TimeSpan.FromHours(1)),
            Does.Not.Contain("prepare-wins"));
        Assert.That(
            (await new BuildStore(database).GetAsync("prepare-wins"))!.State,
            Is.EqualTo(TrackedBuildState.Running));

        await store.EnqueueAsync(
            Assignment("expiry-wins"), string.Empty, TestNow, TimeSpan.FromSeconds(10));
        Assert.That(await store.TryClaimAsync("expiry-wins", "agent-expiry", TestNow), Is.True);
        var expireFirst = store.ExpireDueAsync(deadline);
        var prepareSecond = store.TryPrepareDispatchAsync(
            "expiry-wins", "agent-expiry", "session-expiry", deadline.AddMilliseconds(-1));
        await Task.WhenAll(expireFirst, prepareSecond);
        Assert.Multiple(() =>
        {
            Assert.That(expireFirst.Result, Does.Contain("expiry-wins"));
            Assert.That(prepareSecond.Result, Is.False);
        });
    }

    [Test]
    public async Task Restart_backfills_null_deadline_once_without_extending_existing_deadlines()
    {
        var explicitDeadline = TestNow + TimeSpan.FromMinutes(5);
        await using (var database = new VivariumDatabase(rootDir))
        {
            var store = new BuildQueueStore(database);
            await store.EnqueueAsync(
                Assignment("explicit"), string.Empty, TestNow, TimeSpan.FromMinutes(5));
            await store.EnqueueAsync(
                Assignment("legacy-null"), string.Empty, TestNow, TimeSpan.FromMinutes(1));
            await database.WriteAsync(connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    UPDATE build_queue
                    SET queue_deadline_unix_ms = NULL,
                        enqueued_unix_ms = $enqueued
                    WHERE build_id = 'legacy-null';
                    """;
                command.Parameters.AddWithValue("$enqueued", TestNow.ToUnixTimeMilliseconds());
                return command.ExecuteNonQuery();
            });
        }

        DateTimeOffset backfilledDeadline;
        await using (var restarted = new VivariumDatabase(rootDir))
        {
            var store = new BuildQueueStore(restarted);
            Assert.That(await store.InitializeQueueDeadlinesAsync(TimeSpan.FromMinutes(20)), Is.EqualTo(1));
            var explicitItem = await store.GetAsync("explicit");
            var legacyItem = await store.GetAsync("legacy-null");
            Assert.That(explicitItem!.QueueDeadline, Is.EqualTo(explicitDeadline));
            backfilledDeadline = legacyItem!.QueueDeadline!.Value;
            Assert.That(backfilledDeadline, Is.EqualTo(TestNow + TimeSpan.FromMinutes(20)));
        }

        await using (var restartedAgain = new VivariumDatabase(rootDir))
        {
            var store = new BuildQueueStore(restartedAgain);
            Assert.That(await store.InitializeQueueDeadlinesAsync(TimeSpan.FromHours(2)), Is.Zero);
            Assert.That((await store.GetAsync("legacy-null"))!.QueueDeadline,
                Is.EqualTo(backfilledDeadline));
        }
    }

    [Test]
    public async Task Control_plane_watch_projection_observes_terminal_queue_timeout_snapshot()
    {
        var time = new ManualTimeProvider(TestNow);
        await using var fixture = await MatrixFixture.CreateAsync(
            rootDir, time, TimeSpan.FromSeconds(10));
        using var changes = new DatabaseChangeNotifier(fixture.Database);
        var submitted = await fixture.Submissions.SubmitAsync(
            ManagementRequestContext.System("test"), Request("watch-timeout", 1));
        var queued = await fixture.MatrixBuilds.GetSnapshotAsync(submitted.BuildId);
        var observedVersion = changes.Version;
        Assert.That(queued!.State, Is.EqualTo(DurableBuildState.Queued));

        Assert.That(
            await fixture.QueueStore.ExpireDueAsync(TestNow + TimeSpan.FromSeconds(1)),
            Has.Count.EqualTo(1));
        Assert.That(
            await changes.WaitForChangeAsync(
                observedVersion, TimeSpan.FromSeconds(1), CancellationToken.None),
            Is.GreaterThan(observedVersion),
            "the same durable database notification used by WatchBuild must wake after expiry");

        var terminal = await fixture.MatrixBuilds.GetSnapshotAsync(submitted.BuildId);
        Assert.Multiple(() =>
        {
            Assert.That(terminal!.State, Is.EqualTo(DurableBuildState.Finished));
            Assert.That(terminal.Outcome, Is.EqualTo(BuildOutcome.InfrastructureFailed));
            Assert.That(terminal.Cells.Single().State, Is.EqualTo(DurableBuildState.Finished));
            Assert.That(terminal.Cells.Single().Outcome, Is.EqualTo(BuildOutcome.InfrastructureFailed));
            Assert.That(terminal.Cells.Single().StatusText,
                Is.EqualTo(BuildQueueStore.QueueTimeoutReason));
        });
    }

    [Test]
    public void Controller_rejects_non_positive_default_queue_timeout()
    {
        var exception = Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await VivariumControllerHost.StartAsync(new ControllerOptions
            {
                DataDir = rootDir,
                Host = "127.0.0.1",
                Port = 0,
                BuildQueueWaitTimeout = TimeSpan.Zero,
            }));
        Assert.That(exception!.Message, Does.Contain("queue wait timeout must be positive"));
    }

    private static BuildAssignment Assignment(string buildId) => new() { BuildId = buildId };

    private static SubmitBuildRequest Request(string requestId, params int[] queueTimeouts)
    {
        var request = new SubmitBuildRequest
        {
            RequestId = requestId,
            Project = "Vivarium",
            Configuration = "queue-timeout-tests",
            DefinitionSnapshot = Google.Protobuf.ByteString.CopyFromUtf8("project: Vivarium"),
        };
        for (var index = 0; index < queueTimeouts.Length; index++)
        {
            request.Cells.Add(new MatrixBuildCell
            {
                Name = $"windows-{index + 1}",
                AgentExpression = "os.family == windows",
                Rid = "win-x64",
                QueueTimeoutSec = queueTimeouts[index],
                Assignment = new BuildAssignment(),
            });
        }

        return request;
    }

    private sealed class MatrixFixture : IAsyncDisposable
    {
        public required VivariumDatabase Database { get; init; }
        public required BuildQueueStore QueueStore { get; init; }
        public required MatrixBuildStore MatrixBuilds { get; init; }
        public required MatrixBuildSubmissionService Submissions { get; init; }

        public static async Task<MatrixFixture> CreateAsync(
            string dataDir,
            TimeProvider timeProvider,
            TimeSpan defaultQueueWaitTimeout)
        {
            var database = new VivariumDatabase(dataDir);
            var tokens = new TokenStore(dataDir, database);
            var agents = new AgentStore(database);
            var registry = new AgentRegistry(agents, timeProvider);
            var queueStore = new BuildQueueStore(database);
            var authorization = new ManagementCommandAuthorizer(
                new ManagementAuthorizer(), new AuditEventStore(database), timeProvider);
            var queue = new BuildQueueService(
                queueStore, registry, timeProvider, defaultQueueWaitTimeout);
            var blobs = new BlobStore(Path.Combine(dataDir, "blobs"));
            var matrixBuilds = new MatrixBuildStore(database);
            var submissions = new MatrixBuildSubmissionService(
                matrixBuilds,
                agents,
                blobs,
                queue,
                timeProvider,
                defaultQueueWaitTimeout,
                authorization);

            var enrollToken = await tokens.CreateEnrollTokenAsync();
            var hello = new Hello
            {
                AgentId = "known-windows",
                EnrollToken = enrollToken,
                SessionId = "fixture-session",
                Os = new OsInfo { Family = "windows", Arch = "x64", Version = "test" },
            };
            hello.Parameters["hostname"] = "known-windows";
            hello.Parameters["os.family"] = "windows";
            Assert.That(await tokens.AdmitAgentAsync(hello), Is.Not.Null);
            await agents.ObserveHelloAsync(hello);

            return new MatrixFixture
            {
                Database = database,
                QueueStore = queueStore,
                MatrixBuilds = matrixBuilds,
                Submissions = submissions,
            };
        }

        public ValueTask DisposeAsync() => Database.DisposeAsync();
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset utcNow;

        public ManualTimeProvider(DateTimeOffset utcNow) => this.utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
