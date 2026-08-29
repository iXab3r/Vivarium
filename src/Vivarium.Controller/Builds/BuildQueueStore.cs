using System.Text.Json;
using Google.Protobuf;
using Microsoft.Data.Sqlite;
using Vivarium.Contracts.V1;
using Vivarium.Controller.Auditing;
using Vivarium.Controller.Persistence;
using Vivarium.Controller.Rest.Events;

namespace Vivarium.Controller.Builds;

/// <summary>
/// Durable TeamCity-style Build Queue. The queue references the assignment stored in <c>builds</c>;
/// claims are leases that remain visible across controller restarts until dispatch is completed or
/// explicitly requeued.
/// </summary>
public sealed class BuildQueueStore
{
    public const string QueueTimeoutReason = "queue wait timeout expired";

    private readonly VivariumDatabase database;

    public BuildQueueStore(VivariumDatabase database) => this.database = database;

    public Task<int> InitializeQueueDeadlinesAsync(TimeSpan defaultTimeout)
    {
        var defaultMilliseconds = TimeoutMilliseconds(defaultTimeout);
        return database.WriteAsync(connection =>
        {
            var now = DateTimeOffset.UtcNow;
            using var transaction = connection.BeginTransaction();
            var buildIds = new List<string>();
            using (var select = connection.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText = """
                    SELECT build_id FROM build_queue
                    WHERE queue_deadline_unix_ms IS NULL ORDER BY queue_id;
                    """;
                using var reader = select.ExecuteReader();
                while (reader.Read())
                {
                    buildIds.Add(reader.GetString(0));
                }
            }

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    UPDATE build_queue
                    SET queue_deadline_unix_ms = enqueued_unix_ms + $defaultMilliseconds
                    WHERE queue_deadline_unix_ms IS NULL;
                    """;
                command.Parameters.AddWithValue("$defaultMilliseconds", defaultMilliseconds);
                if (command.ExecuteNonQuery() != buildIds.Count)
                {
                    throw new InvalidDataException(
                        "serialized queue deadline initialization changed row count");
                }
            }

            foreach (var buildId in buildIds)
            {
                BuildEventStore.AppendForChild(
                    connection, transaction, buildId, "build.queue-deadline-initialized", now);
            }

            transaction.Commit();
            return buildIds.Count;
        });
    }

    public Task<BuildQueueItem> EnqueueAsync(BuildAssignment assignment, string agentExpression) =>
        EnqueueAsync(
            assignment,
            agentExpression,
            DateTimeOffset.UtcNow,
            ControllerOptions.DefaultBuildQueueWaitTimeout);

    public Task<BuildQueueItem> EnqueueAsync(
        BuildAssignment assignment,
        string agentExpression,
        DateTimeOffset now,
        TimeSpan queueWaitTimeout)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        ArgumentNullException.ThrowIfNull(agentExpression);
        if (string.IsNullOrWhiteSpace(assignment.BuildId))
        {
            throw new ArgumentException("a queued assignment must have a build id", nameof(assignment));
        }

        var serializedAssignment = assignment.ToByteArray();
        var nowUnixMs = now.ToUnixTimeMilliseconds();
        var queueDeadlineUnixMs = checked(nowUnixMs + TimeoutMilliseconds(queueWaitTimeout));
        return database.WriteAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();

            using (var build = connection.CreateCommand())
            {
                build.Transaction = transaction;
                build.CommandText = """
                    INSERT INTO builds(
                        build_id, agent_id, state, assignment, created_unix_ms, updated_unix_ms)
                    VALUES ($buildId, NULL, 'QUEUED', $assignment, $now, $now);
                    """;
                build.Parameters.AddWithValue("$buildId", assignment.BuildId);
                build.Parameters.Add("$assignment", SqliteType.Blob).Value = serializedAssignment;
                build.Parameters.AddWithValue("$now", nowUnixMs);
                build.ExecuteNonQuery();
            }

            long queueId;
            using (var queue = connection.CreateCommand())
            {
                queue.Transaction = transaction;
                queue.CommandText = """
                    INSERT INTO build_queue(
                        build_id, agent_expression, state, enqueued_unix_ms,
                        queue_deadline_unix_ms)
                    VALUES ($buildId, $agentExpression, 'QUEUED', $now, $queueDeadline);
                    SELECT last_insert_rowid();
                    """;
                queue.Parameters.AddWithValue("$buildId", assignment.BuildId);
                queue.Parameters.AddWithValue("$agentExpression", agentExpression);
                queue.Parameters.AddWithValue("$now", nowUnixMs);
                queue.Parameters.AddWithValue("$queueDeadline", queueDeadlineUnixMs);
                queueId = (long)(queue.ExecuteScalar()
                    ?? throw new InvalidOperationException("SQLite did not return a queue id"));
            }

            transaction.Commit();
            return new BuildQueueItem(
                queueId,
                assignment.BuildId,
                BuildAssignment.Parser.ParseFrom(serializedAssignment),
                agentExpression,
                BuildQueueItemState.Queued,
                null,
                DispatchPrepared: false,
                DispatchSessionId: null,
                FromUnixMilliseconds(nowUnixMs),
                FromUnixMilliseconds(queueDeadlineUnixMs),
                null,
                null,
                null);
        });
    }

    /// <summary>
    /// Finishes every due build that has not begun execution. This mutation and dispatch preparation
    /// share the database writer, so a deadline and a transition to RUNNING cannot both win.
    /// </summary>
    public Task<IReadOnlyList<string>> ExpireDueAsync(DateTimeOffset now) =>
        database.WriteAsync<IReadOnlyList<string>>(connection =>
        {
            var nowUnixMs = now.ToUnixTimeMilliseconds();
            using var transaction = connection.BeginTransaction();
            var buildIds = new List<string>();
            using (var due = connection.CreateCommand())
            {
                due.Transaction = transaction;
                due.CommandText = """
                    SELECT q.build_id
                    FROM build_queue q
                    JOIN builds b ON b.build_id = q.build_id
                    WHERE q.state IN ('QUEUED', 'CLAIMED')
                        AND q.queue_deadline_unix_ms IS NOT NULL
                        AND q.queue_deadline_unix_ms <= $now
                        AND b.state = 'QUEUED'
                        AND b.agent_id IS NULL
                    ORDER BY q.queue_id;
                    """;
                due.Parameters.AddWithValue("$now", nowUnixMs);
                using var reader = due.ExecuteReader();
                while (reader.Read())
                {
                    buildIds.Add(reader.GetString(0));
                }
            }

            foreach (var buildId in buildIds)
            {
                var result = new BuildResult
                {
                    BuildId = buildId,
                    Outcome = BuildOutcome.InfrastructureFailed,
                    StatusText = QueueTimeoutReason,
                }.ToByteArray();

                using (var queue = connection.CreateCommand())
                {
                    queue.Transaction = transaction;
                    queue.CommandText = """
                        UPDATE build_queue SET
                            state = 'REMOVED',
                            removed_unix_ms = $now,
                            removal_reason = $reason
                        WHERE build_id = $buildId
                            AND state IN ('QUEUED', 'CLAIMED');
                        """;
                    queue.Parameters.AddWithValue("$now", nowUnixMs);
                    queue.Parameters.AddWithValue("$reason", QueueTimeoutReason);
                    queue.Parameters.AddWithValue("$buildId", buildId);
                    if (queue.ExecuteNonQuery() != 1)
                    {
                        throw new InvalidOperationException(
                            $"queue deadline lost its serialized claim for build '{buildId}'");
                    }
                }

                using var build = connection.CreateCommand();
                build.Transaction = transaction;
                build.CommandText = """
                    UPDATE builds SET
                        state = 'FINISHED',
                        result = $result,
                        cancellation_reason = NULL,
                        updated_unix_ms = $now
                    WHERE build_id = $buildId
                        AND state = 'QUEUED'
                        AND agent_id IS NULL;
                    """;
                build.Parameters.Add("$result", SqliteType.Blob).Value = result;
                build.Parameters.AddWithValue("$now", nowUnixMs);
                build.Parameters.AddWithValue("$buildId", buildId);
                if (build.ExecuteNonQuery() != 1)
                {
                    throw new InvalidOperationException(
                        $"queue deadline lost its serialized build for '{buildId}'");
                }

                BuildEventStore.AppendForChild(
                    connection, transaction, buildId, "build.finished", now);
            }

            transaction.Commit();
            return buildIds;
        });

    /// <summary>Returns queued and claimed rows in stable FIFO order. Removed history is excluded.</summary>
    public Task<IReadOnlyList<BuildQueueItem>> ListPendingAsync() =>
        database.ReadAsync<IReadOnlyList<BuildQueueItem>>(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                {SelectColumns}
                WHERE q.state <> 'REMOVED'
                ORDER BY q.queue_id;
                """;
            using var reader = command.ExecuteReader();
            var result = new List<BuildQueueItem>();
            while (reader.Read())
            {
                result.Add(ReadItem(reader));
            }

            return result;
        });

    /// <summary>
    /// Returns an externally consumable FIFO projection with its matrix-build relationship. Removed
    /// rows remain history on their Build resource and are deliberately excluded from the queue.
    /// </summary>
    public Task<BuildQueuePage> ListPendingPageAsync(BuildQueueQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Limit is < 1 or > 200)
        {
            throw new ArgumentOutOfRangeException(
                nameof(query), "limit must be between 1 and 200");
        }

        if (query.AfterQueueId is < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(query), "queue cursor cannot be negative");
        }

        return database.ReadAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    q.queue_id,
                    q.build_id,
                    m.matrix_build_id,
                    m.project,
                    m.configuration,
                    c.cell_name,
                    c.rid,
                    q.agent_expression,
                    q.state,
                    q.claimed_agent_id,
                    q.dispatched_session_id,
                    q.enqueued_unix_ms,
                    q.queue_deadline_unix_ms,
                    q.claimed_unix_ms,
                    b.updated_unix_ms
                FROM build_queue q
                JOIN builds b ON b.build_id = q.build_id
                LEFT JOIN matrix_build_cells c ON c.build_id = q.build_id
                LEFT JOIN matrix_builds m ON m.matrix_build_id = c.matrix_build_id
                WHERE q.state <> 'REMOVED'
                    AND ($afterQueueId IS NULL OR q.queue_id > $afterQueueId)
                    AND ($state IS NULL OR q.state = $state)
                    AND ($project IS NULL OR m.project = $project)
                    AND ($configuration IS NULL OR m.configuration = $configuration)
                    AND ($claimedAgentId IS NULL OR q.claimed_agent_id = $claimedAgentId)
                ORDER BY q.queue_id
                LIMIT $limitPlusOne;
                """;
            command.Parameters.AddWithValue(
                "$afterQueueId", (object?)query.AfterQueueId ?? DBNull.Value);
            command.Parameters.AddWithValue(
                "$state", (object?)QueueState(query.State) ?? DBNull.Value);
            command.Parameters.AddWithValue(
                "$project", (object?)NormalizeFilter(query.Project) ?? DBNull.Value);
            command.Parameters.AddWithValue(
                "$configuration", (object?)NormalizeFilter(query.Configuration) ?? DBNull.Value);
            command.Parameters.AddWithValue(
                "$claimedAgentId",
                (object?)NormalizeFilter(query.ClaimedAgentId) ?? DBNull.Value);
            command.Parameters.AddWithValue("$limitPlusOne", query.Limit + 1);

            using var reader = command.ExecuteReader();
            var entries = new List<BuildQueueEntry>(query.Limit + 1);
            while (reader.Read())
            {
                entries.Add(new BuildQueueEntry(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.GetString(7),
                    ParseQueueState(reader.GetString(8)),
                    reader.IsDBNull(9) ? null : reader.GetString(9),
                    !reader.IsDBNull(10),
                    FromUnixMilliseconds(reader.GetInt64(11)),
                    reader.IsDBNull(12) ? null : FromUnixMilliseconds(reader.GetInt64(12)),
                    reader.IsDBNull(13) ? null : FromUnixMilliseconds(reader.GetInt64(13)),
                    FromUnixMilliseconds(reader.GetInt64(14))));
            }

            return new BuildQueuePage(
                entries.Take(query.Limit).ToArray(),
                entries.Count > query.Limit);
        });
    }

    public Task<BuildQueueItem?> GetAsync(string buildId) => database.ReadAsync(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            {SelectColumns}
            WHERE q.build_id = $buildId;
            """;
        command.Parameters.AddWithValue("$buildId", buildId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadItem(reader) : null;
    });

    /// <summary>Atomically leases one queued build to one agent. Competing claimers cannot both win.</summary>
    public Task<bool> TryClaimAsync(string buildId, string agentId) =>
        TryClaimAsync(buildId, agentId, DateTimeOffset.UtcNow);

    public Task<bool> TryClaimAsync(string buildId, string agentId, DateTimeOffset now) =>
        database.WriteAsync(connection =>
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE build_queue SET
                state = 'CLAIMED',
                claimed_agent_id = $agentId,
                claimed_unix_ms = $now
            WHERE build_id = $buildId
                AND state = 'QUEUED'
                AND queue_deadline_unix_ms > $now
                AND EXISTS (
                    SELECT 1 FROM builds b
                    WHERE b.build_id = build_queue.build_id
                        AND b.state = 'QUEUED'
                        AND b.agent_id IS NULL
                );
            """;
        command.Parameters.AddWithValue("$agentId", agentId);
        command.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$buildId", buildId);
        try
        {
            if (command.ExecuteNonQuery() != 1)
            {
                transaction.Rollback();
                return false;
            }

            BuildEventStore.AppendForChild(
                connection, transaction, buildId, "build.queue-claimed", now);
            transaction.Commit();
            return true;
        }
        catch (SqliteException ex) when (ex.SqliteExtendedErrorCode == SqliteConstraintUnique)
        {
            // Another build claimed this capacity-one agent first.
            transaction.Rollback();
            return false;
        }
    });

    /// <summary>
    /// Persists build ownership before a scheduler sends the assignment. The queue row deliberately
    /// remains claimed, making a crash between persistence and wire send recoverable.
    /// </summary>
    public Task<bool> TryPrepareDispatchAsync(
        string buildId,
        string agentId,
        string ownerSessionId,
        DateTimeOffset now) => TryPrepareDispatchAsync(
            buildId, agentId, ownerSessionId, now, agentName: null,
            agentParametersJson: null, agentCustomParametersJson: null);

    public Task<bool> TryPrepareDispatchAsync(
        string buildId,
        string agentId,
        string ownerSessionId,
        DateTimeOffset now,
        string agentName,
        IReadOnlyDictionary<string, string> agentParameters)
        => TryPrepareDispatchAsync(
            buildId,
            agentId,
            ownerSessionId,
            now,
            agentName,
            agentParameters,
            new Dictionary<string, string>());

    public Task<bool> TryPrepareDispatchAsync(
        string buildId,
        string agentId,
        string ownerSessionId,
        DateTimeOffset now,
        string agentName,
        IReadOnlyDictionary<string, string> agentParameters,
        IReadOnlyDictionary<string, string> agentCustomParameters)
    {
        ArgumentNullException.ThrowIfNull(agentName);
        ArgumentNullException.ThrowIfNull(agentParameters);
        ArgumentNullException.ThrowIfNull(agentCustomParameters);
        return TryPrepareDispatchAsync(
            buildId,
            agentId,
            ownerSessionId,
            now,
            agentName,
            SerializeParameters(agentParameters),
            SerializeParameters(agentCustomParameters));
    }

    private Task<bool> TryPrepareDispatchAsync(
        string buildId,
        string agentId,
        string ownerSessionId,
        DateTimeOffset now,
        string? agentName,
        string? agentParametersJson,
        string? agentCustomParametersJson) =>
        database.WriteAsync(connection =>
        {
            var nowUnixMs = now.ToUnixTimeMilliseconds();
            using var transaction = connection.BeginTransaction();
            try
            {
                using (var build = connection.CreateCommand())
                {
                    build.Transaction = transaction;
                    build.CommandText = """
                        UPDATE builds SET
                            state = 'RUNNING',
                            agent_id = $agentId,
                            agent_name_snapshot = COALESCE(
                                $agentName,
                                (SELECT name FROM agents WHERE agent_id = $agentId), ''),
                            agent_parameters_snapshot_json = COALESCE(
                                $agentParameters,
                                (SELECT parameters_json FROM agents WHERE agent_id = $agentId), '{}'),
                            agent_custom_parameters_snapshot_json = COALESCE(
                                $agentCustomParameters,
                                (SELECT custom_parameters_json FROM agents WHERE agent_id = $agentId), '{}'),
                            owner_session_id = $ownerSessionId,
                            reconnect_deadline_unix_ms = NULL,
                            updated_unix_ms = $now
                        WHERE build_id = $buildId
                            AND state = 'QUEUED'
                            AND agent_id IS NULL
                            AND EXISTS (
                                SELECT 1 FROM build_queue q
                                WHERE q.build_id = builds.build_id
                                    AND q.state = 'CLAIMED'
                                    AND q.claimed_agent_id = $agentId
                                    AND q.queue_deadline_unix_ms > $now
                            );
                        """;
                    build.Parameters.AddWithValue("$agentId", agentId);
                    build.Parameters.AddWithValue("$agentName", (object?)agentName ?? DBNull.Value);
                    build.Parameters.AddWithValue(
                        "$agentParameters", (object?)agentParametersJson ?? DBNull.Value);
                    build.Parameters.AddWithValue(
                        "$agentCustomParameters",
                        (object?)agentCustomParametersJson ?? DBNull.Value);
                    build.Parameters.AddWithValue("$ownerSessionId", ownerSessionId);
                    build.Parameters.AddWithValue("$now", nowUnixMs);
                    build.Parameters.AddWithValue("$buildId", buildId);
                    if (build.ExecuteNonQuery() != 1)
                    {
                        transaction.Rollback();
                        return false;
                    }
                }

                using (var queue = connection.CreateCommand())
                {
                    queue.Transaction = transaction;
                    queue.CommandText = """
                        UPDATE build_queue SET dispatched_session_id = $ownerSessionId
                        WHERE build_id = $buildId
                            AND state = 'CLAIMED'
                            AND claimed_agent_id = $agentId;
                        """;
                    queue.Parameters.AddWithValue("$buildId", buildId);
                    queue.Parameters.AddWithValue("$agentId", agentId);
                    queue.Parameters.AddWithValue("$ownerSessionId", ownerSessionId);
                    if (queue.ExecuteNonQuery() != 1)
                    {
                        transaction.Rollback();
                        return false;
                    }
                }

                BuildEventStore.AppendForChild(
                    connection, transaction, buildId, "build.running", now);

                transaction.Commit();
                return true;
            }
            catch (SqliteException ex) when (ex.SqliteExtendedErrorCode == SqliteConstraintUnique)
            {
                // The unique active-build index reports a busy agent as a lost dispatch race.
                transaction.Rollback();
                return false;
            }
        });

    /// <summary>
    /// Records the exact session a prepared assignment is about to be sent to. A newer reconnect
    /// replaces this fence before the scheduler retries delivery.
    /// </summary>
    public Task<bool> RecordDispatchAttemptAsync(
        string buildId,
        string agentId,
        string? previousOwnerSessionId,
        string sessionId,
        DateTimeOffset now) => database.WriteAsync(connection =>
        {
            var nowUnixMs = now.ToUnixTimeMilliseconds();
            using var transaction = connection.BeginTransaction();
            using (var build = connection.CreateCommand())
            {
                build.Transaction = transaction;
                build.CommandText = """
                    UPDATE builds SET
                        owner_session_id = $sessionId,
                        reconnect_deadline_unix_ms = NULL,
                        updated_unix_ms = $now
                    WHERE build_id = $buildId
                        AND state IN ('RUNNING', 'CANCEL_REQUESTED')
                        AND agent_id = $agentId
                        AND (($previousOwnerSessionId IS NULL AND owner_session_id IS NULL)
                            OR owner_session_id = $previousOwnerSessionId)
                        AND (reconnect_deadline_unix_ms IS NULL
                            OR reconnect_deadline_unix_ms > $now);
                    """;
                build.Parameters.AddWithValue("$buildId", buildId);
                build.Parameters.AddWithValue("$agentId", agentId);
                build.Parameters.AddWithValue(
                    "$previousOwnerSessionId",
                    (object?)previousOwnerSessionId ?? DBNull.Value);
                build.Parameters.AddWithValue("$sessionId", sessionId);
                build.Parameters.AddWithValue("$now", nowUnixMs);
                if (build.ExecuteNonQuery() != 1)
                {
                    transaction.Rollback();
                    return false;
                }
            }

            using (var queue = connection.CreateCommand())
            {
                queue.Transaction = transaction;
                queue.CommandText = """
                    UPDATE build_queue SET dispatched_session_id = $sessionId
                    WHERE build_id = $buildId
                        AND state = 'CLAIMED'
                        AND claimed_agent_id = $agentId
                        AND (($previousOwnerSessionId IS NULL AND dispatched_session_id IS NULL)
                            OR dispatched_session_id = $previousOwnerSessionId);
                    """;
                queue.Parameters.AddWithValue("$buildId", buildId);
                queue.Parameters.AddWithValue("$agentId", agentId);
                queue.Parameters.AddWithValue(
                    "$previousOwnerSessionId",
                    (object?)previousOwnerSessionId ?? DBNull.Value);
                queue.Parameters.AddWithValue("$sessionId", sessionId);
                if (queue.ExecuteNonQuery() != 1)
                {
                    transaction.Rollback();
                    return false;
                }
            }

            BuildEventStore.AppendForChild(
                connection, transaction, buildId, "build.owner-adopted", now);

            transaction.Commit();
            return true;
        });

    /// <summary>Removes a prepared claim only after the exact accepting session acknowledges it.</summary>
    public Task<bool> CompleteDispatchAsync(string buildId, string agentId, string sessionId) =>
        database.WriteAsync(connection =>
        {
            var now = DateTimeOffset.UtcNow;
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE build_queue SET
                    state = 'REMOVED',
                    removed_unix_ms = $now,
                    removal_reason = 'dispatched'
                WHERE build_id = $buildId
                    AND state = 'CLAIMED'
                    AND claimed_agent_id = $agentId
                    AND dispatched_session_id = $sessionId
                    AND EXISTS (
                        SELECT 1 FROM builds b
                        WHERE b.build_id = build_queue.build_id
                            AND b.state IN ('RUNNING', 'CANCEL_REQUESTED', 'FINISHED')
                            AND b.agent_id = $agentId
                            AND b.owner_session_id = $sessionId
                    );
                """;
            command.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$buildId", buildId);
            command.Parameters.AddWithValue("$agentId", agentId);
            command.Parameters.AddWithValue("$sessionId", sessionId);
            if (command.ExecuteNonQuery() != 1)
            {
                transaction.Rollback();
                return false;
            }

            BuildEventStore.AppendForChild(
                connection, transaction, buildId, "build.dispatch-completed", now);
            transaction.Commit();
            return true;
        });

    /// <summary>
    /// Releases either an unprepared claim or a prepared-but-unsent dispatch back to FIFO position.
    /// A build that advanced past RUNNING cannot be accidentally requeued by this operation.
    /// </summary>
    public Task<bool> TryRequeueDispatchAsync(string buildId, string agentId) =>
        database.WriteAsync(connection =>
        {
            var now = DateTimeOffset.UtcNow;
            using var transaction = connection.BeginTransaction();
            using (var build = connection.CreateCommand())
            {
                build.Transaction = transaction;
                build.CommandText = """
                    UPDATE builds SET
                        state = 'QUEUED',
                        agent_id = NULL,
                        agent_name_snapshot = '',
                        agent_parameters_snapshot_json = '{}',
                        agent_custom_parameters_snapshot_json = '{}',
                        owner_session_id = NULL,
                        reconnect_deadline_unix_ms = NULL,
                        updated_unix_ms = $now
                    WHERE build_id = $buildId
                        AND (
                            (state = 'QUEUED' AND agent_id IS NULL)
                            OR (state = 'RUNNING' AND agent_id = $agentId)
                        )
                        AND EXISTS (
                            SELECT 1 FROM build_queue q
                            WHERE q.build_id = builds.build_id
                                AND q.state = 'CLAIMED'
                                AND q.claimed_agent_id = $agentId
                        );
                    """;
                build.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
                build.Parameters.AddWithValue("$buildId", buildId);
                build.Parameters.AddWithValue("$agentId", agentId);
                if (build.ExecuteNonQuery() != 1)
                {
                    transaction.Rollback();
                    return false;
                }
            }

            using (var queue = connection.CreateCommand())
            {
                queue.Transaction = transaction;
                queue.CommandText = """
                    UPDATE build_queue SET
                        state = 'QUEUED',
                        claimed_agent_id = NULL,
                        claimed_unix_ms = NULL,
                        dispatched_session_id = NULL
                    WHERE build_id = $buildId
                        AND state = 'CLAIMED'
                        AND claimed_agent_id = $agentId;
                    """;
                queue.Parameters.AddWithValue("$buildId", buildId);
                queue.Parameters.AddWithValue("$agentId", agentId);
                if (queue.ExecuteNonQuery() != 1)
                {
                    transaction.Rollback();
                    return false;
                }
            }

            BuildEventStore.AppendForChild(
                connection, transaction, buildId, "build.queued", now);

            transaction.Commit();
            return true;
        });

    private static string SerializeParameters(IReadOnlyDictionary<string, string> parameters)
    {
        var ordered = parameters
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        return JsonSerializer.Serialize(ordered);
    }

    /// <summary>Cancels a build that has not entered RUNNING and removes it from the Build Queue.</summary>
    public Task<bool> TryRemoveAsync(string buildId, string reason)
        => TryRemoveAsync(buildId, reason, auditEvent: null);

    internal Task<bool> TryRemoveAsync(
        string buildId,
        string reason,
        AuditEventDraft? auditEvent)
    {
        ArgumentNullException.ThrowIfNull(reason);
        var result = new BuildResult
        {
            BuildId = buildId,
            Outcome = BuildOutcome.Cancelled,
            StatusText = reason,
        }.ToByteArray();

        return database.WriteAsync(connection =>
        {
            var occurredAt = DateTimeOffset.UtcNow;
            var now = occurredAt.ToUnixTimeMilliseconds();
            using var transaction = connection.BeginTransaction();
            using (var queue = connection.CreateCommand())
            {
                queue.Transaction = transaction;
                queue.CommandText = """
                    UPDATE build_queue SET
                        state = 'REMOVED',
                        removed_unix_ms = $now,
                        removal_reason = $reason
                    WHERE build_id = $buildId
                        AND state IN ('QUEUED', 'CLAIMED')
                        AND EXISTS (
                            SELECT 1 FROM builds b
                            WHERE b.build_id = build_queue.build_id
                                AND b.state = 'QUEUED'
                                AND b.agent_id IS NULL
                        );
                    """;
                queue.Parameters.AddWithValue("$now", now);
                queue.Parameters.AddWithValue("$reason", reason);
                queue.Parameters.AddWithValue("$buildId", buildId);
                if (queue.ExecuteNonQuery() != 1)
                {
                    transaction.Rollback();
                    return false;
                }
            }

            using (var build = connection.CreateCommand())
            {
                build.Transaction = transaction;
                build.CommandText = """
                    UPDATE builds SET
                        state = 'FINISHED',
                        result = $result,
                        cancellation_reason = $reason,
                        updated_unix_ms = $now
                    WHERE build_id = $buildId
                        AND state = 'QUEUED'
                        AND agent_id IS NULL;
                    """;
                build.Parameters.Add("$result", SqliteType.Blob).Value = result;
                build.Parameters.AddWithValue("$reason", reason);
                build.Parameters.AddWithValue("$now", now);
                build.Parameters.AddWithValue("$buildId", buildId);
                if (build.ExecuteNonQuery() != 1)
                {
                    transaction.Rollback();
                    return false;
                }
            }

            if (auditEvent is not null)
            {
                AuditEventStore.Append(connection, transaction, auditEvent);
            }

            BuildEventStore.AppendForChild(
                connection,
                transaction,
                buildId,
                "build.finished",
                occurredAt,
                auditEvent?.RequestContext);

            transaction.Commit();
            return true;
        });
    }

    private static BuildQueueItem ReadItem(SqliteDataReader reader)
    {
        var state = ParseQueueState(reader.GetString(4));
        return new BuildQueueItem(
            reader.GetInt64(0),
            reader.GetString(1),
            BuildAssignment.Parser.ParseFrom((byte[])reader[2]),
            reader.GetString(3),
            state,
            reader.IsDBNull(5) ? null : reader.GetString(5),
            !reader.IsDBNull(11),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            FromUnixMilliseconds(reader.GetInt64(7)),
            reader.IsDBNull(13) ? null : FromUnixMilliseconds(reader.GetInt64(13)),
            reader.IsDBNull(8) ? null : FromUnixMilliseconds(reader.GetInt64(8)),
            reader.IsDBNull(9) ? null : FromUnixMilliseconds(reader.GetInt64(9)),
            reader.IsDBNull(10) ? null : reader.GetString(10));
    }

    private static DateTimeOffset FromUnixMilliseconds(long value) =>
        DateTimeOffset.FromUnixTimeMilliseconds(value);

    private static BuildQueueItemState ParseQueueState(string value) => value switch
    {
        "QUEUED" => BuildQueueItemState.Queued,
        "CLAIMED" => BuildQueueItemState.Claimed,
        "REMOVED" => BuildQueueItemState.Removed,
        _ => throw new InvalidDataException($"unknown persisted queue state '{value}'"),
    };

    private static string? QueueState(BuildQueueItemState? value) => value switch
    {
        null => null,
        BuildQueueItemState.Queued => "QUEUED",
        BuildQueueItemState.Claimed => "CLAIMED",
        BuildQueueItemState.Removed => "REMOVED",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string? NormalizeFilter(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private const string SelectColumns = """
        SELECT
            q.queue_id,
            q.build_id,
            b.assignment,
            q.agent_expression,
            q.state,
            q.claimed_agent_id,
            q.dispatched_session_id,
            q.enqueued_unix_ms,
            q.claimed_unix_ms,
            q.removed_unix_ms,
            q.removal_reason,
            b.agent_id,
            b.state,
            q.queue_deadline_unix_ms
        FROM build_queue q
        JOIN builds b ON b.build_id = q.build_id
        """;

    private const int SqliteConstraintUnique = 2067;

    private static long TimeoutMilliseconds(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "queue wait timeout must be positive");
        }

        return checked((long)Math.Ceiling(timeout.TotalMilliseconds));
    }
}
