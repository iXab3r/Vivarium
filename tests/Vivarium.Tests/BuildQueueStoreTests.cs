using Google.Protobuf;
using Microsoft.Data.Sqlite;
using Vivarium.Contracts.V1;
using Vivarium.Controller.Builds;
using Vivarium.Controller.Persistence;

namespace Vivarium.Tests;

[TestFixture]
public class BuildQueueStoreTests
{
    private static readonly DateTimeOffset TestNow =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

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
    public async Task Pending_queue_preserves_fifo_and_claims_across_restart()
    {
        await using (var database = new VivariumDatabase(rootDir))
        {
            var queue = new BuildQueueStore(database);
            await queue.EnqueueAsync(Assignment("b-1", "first"), "os.family == windows");
            await queue.EnqueueAsync(Assignment("b-2", "second"), "os.family == linux");
            await queue.EnqueueAsync(Assignment("b-3", "third"), "name == macbook");
            Assert.That(await queue.TryClaimAsync("b-2", "linux-agent"), Is.True);
        }

        await using var restartedDatabase = new VivariumDatabase(rootDir);
        var restartedQueue = new BuildQueueStore(restartedDatabase);
        var pending = await restartedQueue.ListPendingAsync();

        Assert.Multiple(() =>
        {
            Assert.That(pending.Select(item => item.BuildId), Is.EqualTo(new[] { "b-1", "b-2", "b-3" }));
            Assert.That(pending.Select(item => item.QueueId), Is.Ordered.Ascending);
            Assert.That(pending[0].Assignment.Parameters["label"], Is.EqualTo("first"));
            Assert.That(pending[1].State, Is.EqualTo(BuildQueueItemState.Claimed));
            Assert.That(pending[1].ClaimedAgentId, Is.EqualTo("linux-agent"));
            Assert.That(pending[1].DispatchPrepared, Is.False);
            Assert.That(pending[1].AgentExpression, Is.EqualTo("os.family == linux"));
        });
    }

    [Test]
    public async Task Claim_and_dispatch_lease_are_atomic_and_recoverable()
    {
        await using var database = new VivariumDatabase(rootDir);
        var queue = new BuildQueueStore(database);
        await queue.EnqueueAsync(Assignment("b-race", "race"), "os.family == windows");

        var claims = await Task.WhenAll(
            queue.TryClaimAsync("b-race", "agent-a"),
            queue.TryClaimAsync("b-race", "agent-b"));
        Assert.That(claims.Count(value => value), Is.EqualTo(1));
        var winner = claims[0] ? "agent-a" : "agent-b";
        var loser = claims[0] ? "agent-b" : "agent-a";

        Assert.That(
            await queue.TryPrepareDispatchAsync("b-race", loser, "loser-session", TestNow),
            Is.False);
        Assert.That(
            await queue.TryPrepareDispatchAsync("b-race", winner, "session-1", TestNow),
            Is.True);
        var prepared = await queue.GetAsync("b-race");
        Assert.Multiple(() =>
        {
            Assert.That(prepared?.State, Is.EqualTo(BuildQueueItemState.Claimed));
            Assert.That(prepared?.ClaimedAgentId, Is.EqualTo(winner));
            Assert.That(prepared?.DispatchPrepared, Is.True,
                "the recoverable lease remains visible until the wire send succeeds");
        });

        Assert.That(await queue.TryRequeueDispatchAsync("b-race", loser), Is.False);
        Assert.That(await queue.TryRequeueDispatchAsync("b-race", winner), Is.True);
        var requeued = await queue.GetAsync("b-race");
        Assert.Multiple(() =>
        {
            Assert.That(requeued?.State, Is.EqualTo(BuildQueueItemState.Queued));
            Assert.That(requeued?.ClaimedAgentId, Is.Null);
            Assert.That(requeued?.DispatchPrepared, Is.False);
        });

        Assert.That(await queue.TryClaimAsync("b-race", winner), Is.True);
        Assert.That(
            await queue.TryPrepareDispatchAsync("b-race", winner, "session-1", TestNow),
            Is.True);
        Assert.That(
            await new BuildStore(database).TryFinishAsync(new BuildResult
            {
                BuildId = "b-race",
                SessionId = "session-1",
                Outcome = BuildOutcome.Succeeded,
            }, winner, "session-1", TestNow),
            Is.True);
        Assert.That(
            await queue.CompleteDispatchAsync("b-race", winner, "wrong-session"),
            Is.False);
        Assert.That(
            await queue.CompleteDispatchAsync("b-race", winner, "session-1"),
            Is.True);
        Assert.That(await queue.ListPendingAsync(), Is.Empty);
        var dispatched = await queue.GetAsync("b-race");
        Assert.Multiple(() =>
        {
            Assert.That(dispatched?.State, Is.EqualTo(BuildQueueItemState.Removed));
            Assert.That(dispatched?.RemovalReason, Is.EqualTo("dispatched"));
            Assert.That(dispatched?.DispatchPrepared, Is.True);
        });
    }

    [Test]
    public async Task One_agent_cannot_hold_claims_for_two_builds()
    {
        await using var database = new VivariumDatabase(rootDir);
        var queue = new BuildQueueStore(database);
        await queue.EnqueueAsync(Assignment("b-agent-race-1", "first"), string.Empty);
        await queue.EnqueueAsync(Assignment("b-agent-race-2", "second"), string.Empty);

        var claims = await Task.WhenAll(
            queue.TryClaimAsync("b-agent-race-1", "capacity-one-agent"),
            queue.TryClaimAsync("b-agent-race-2", "capacity-one-agent"));
        var pending = await queue.ListPendingAsync();

        Assert.Multiple(() =>
        {
            Assert.That(claims.Count(value => value), Is.EqualTo(1));
            Assert.That(pending.Count(item => item.State == BuildQueueItemState.Claimed), Is.EqualTo(1));
            Assert.That(pending.Count(item => item.State == BuildQueueItemState.Queued), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Prepared_dispatch_lease_survives_restart_and_can_be_requeued()
    {
        await using (var database = new VivariumDatabase(rootDir))
        {
            var queue = new BuildQueueStore(database);
            await queue.EnqueueAsync(Assignment("b-prepared", "prepared"), "name == windows-box");
            Assert.That(await queue.TryClaimAsync("b-prepared", "windows-box"), Is.True);
            Assert.That(
                await queue.TryPrepareDispatchAsync(
                    "b-prepared", "windows-box", "session-before-restart", TestNow),
                Is.True);
        }

        await using var restartedDatabase = new VivariumDatabase(rootDir);
        var restartedQueue = new BuildQueueStore(restartedDatabase);
        var recovered = (await restartedQueue.ListPendingAsync()).Single();
        Assert.Multiple(() =>
        {
            Assert.That(recovered.State, Is.EqualTo(BuildQueueItemState.Claimed));
            Assert.That(recovered.ClaimedAgentId, Is.EqualTo("windows-box"));
            Assert.That(recovered.DispatchPrepared, Is.True);
        });

        Assert.That(
            await restartedQueue.TryRequeueDispatchAsync("b-prepared", "windows-box"),
            Is.True);
        var requeued = await restartedQueue.GetAsync("b-prepared");
        Assert.Multiple(() =>
        {
            Assert.That(requeued?.State, Is.EqualTo(BuildQueueItemState.Queued));
            Assert.That(requeued?.DispatchPrepared, Is.False);
        });
    }

    [Test]
    public async Task Removing_a_queued_build_persists_a_cancelled_terminal_result()
    {
        await using var database = new VivariumDatabase(rootDir);
        var queue = new BuildQueueStore(database);
        await queue.EnqueueAsync(Assignment("b-remove", "remove"), "os.family == linux");

        Assert.That(await queue.TryRemoveAsync("b-remove", "removed by operator"), Is.True);
        Assert.That(await queue.TryRemoveAsync("b-remove", "duplicate"), Is.False);
        Assert.That(await queue.ListPendingAsync(), Is.Empty);

        var persisted = await database.ReadAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT state, result, cancellation_reason, agent_id
                FROM builds WHERE build_id = 'b-remove';
                """;
            using var reader = command.ExecuteReader();
            Assert.That(reader.Read(), Is.True);
            return (
                State: reader.GetString(0),
                Result: BuildResult.Parser.ParseFrom((byte[])reader[1]),
                Reason: reader.GetString(2),
                HasAgent: !reader.IsDBNull(3));
        });

        var removed = await queue.GetAsync("b-remove");
        Assert.Multiple(() =>
        {
            Assert.That(removed?.State, Is.EqualTo(BuildQueueItemState.Removed));
            Assert.That(removed?.RemovalReason, Is.EqualTo("removed by operator"));
            Assert.That(persisted.State, Is.EqualTo("FINISHED"));
            Assert.That(persisted.Result.Outcome, Is.EqualTo(BuildOutcome.Cancelled));
            Assert.That(persisted.Result.StatusText, Is.EqualTo("removed by operator"));
            Assert.That(persisted.Reason, Is.EqualTo("removed by operator"));
            Assert.That(persisted.HasAgent, Is.False);
        });
    }

    [Test]
    public async Task Existing_phase_one_build_rows_are_migrated_without_data_loss()
    {
        var assignment = Assignment("b-existing", "existing");
        var databasePath = Path.Combine(rootDir, "vivarium.db");
        using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE builds (
                    build_id TEXT PRIMARY KEY,
                    agent_id TEXT NOT NULL,
                    state TEXT NOT NULL CHECK (state IN ('RUNNING', 'CANCEL_REQUESTED', 'FINISHED')),
                    assignment BLOB NOT NULL,
                    result BLOB NULL,
                    cancellation_reason TEXT NULL,
                    created_unix_ms INTEGER NOT NULL,
                    updated_unix_ms INTEGER NOT NULL
                );
                CREATE UNIQUE INDEX builds_one_active_per_agent
                    ON builds(agent_id) WHERE state <> 'FINISHED';
                INSERT INTO builds(
                    build_id, agent_id, state, assignment, created_unix_ms, updated_unix_ms)
                VALUES ('b-existing', 'agent-existing', 'RUNNING', $assignment, 1, 1);
                """;
            command.Parameters.Add("$assignment", SqliteType.Blob).Value = assignment.ToByteArray();
            command.ExecuteNonQuery();
        }

        await using var database = new VivariumDatabase(rootDir);
        var buildStore = new BuildStore(database);
        var existing = await buildStore.GetAsync("b-existing");
        var queue = new BuildQueueStore(database);
        await queue.EnqueueAsync(Assignment("b-new", "new"), "os.family == windows");

        var schema = await database.ReadAsync(connection =>
        {
            using var sql = connection.CreateCommand();
            sql.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'builds';";
            var tableSql = (string)sql.ExecuteScalar()!;

            using var info = connection.CreateCommand();
            info.CommandText = "PRAGMA table_info(builds);";
            using var reader = info.ExecuteReader();
            var agentIsNullable = false;
            while (reader.Read())
            {
                if (reader.GetString(1) == "agent_id")
                {
                    agentIsNullable = reader.GetInt32(3) == 0;
                }
            }

            return (tableSql, agentIsNullable);
        });

        Assert.Multiple(() =>
        {
            Assert.That(existing?.AgentId, Is.EqualTo("agent-existing"));
            Assert.That(existing?.Assignment.Parameters["label"], Is.EqualTo("existing"));
            Assert.That(existing?.State, Is.EqualTo(TrackedBuildState.Running));
            Assert.That(schema.tableSql, Does.Contain("'QUEUED'"));
            Assert.That(schema.agentIsNullable, Is.True);
        });
    }

    private static BuildAssignment Assignment(string buildId, string label)
    {
        var assignment = new BuildAssignment { BuildId = buildId };
        assignment.Parameters["label"] = label;
        return assignment;
    }
}
