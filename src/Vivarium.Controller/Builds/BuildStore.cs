using System.Globalization;
using Google.Protobuf;
using Microsoft.Data.Sqlite;
using Vivarium.Contracts.V1;
using Vivarium.Controller.Auditing;
using Vivarium.Controller.Blobs.Access;
using Vivarium.Controller.Persistence;
using Vivarium.Controller.Rest.Events;
using Vivarium.Controller.Security;

namespace Vivarium.Controller.Builds;

public sealed record StoredBuild(
    string BuildId,
    string? AgentId,
    string? OwnerSessionId,
    DateTimeOffset? ReconnectDeadline,
    TrackedBuildState State,
    string? CancellationReason,
    BuildAssignment Assignment,
    BuildResult? Result,
    string? StopOperationId = null,
    BuildStopMode StopMode = BuildStopMode.Unspecified,
    DateTimeOffset? StopDeadline = null,
    bool StopAcknowledged = false);

public sealed record CancellationRequestResult(
    bool Active,
    string? Reason,
    string? OperationId = null,
    BuildStopMode Mode = BuildStopMode.Unspecified,
    DateTimeOffset? Deadline = null,
    bool Acknowledged = false);

public sealed record DueBuildStop(
    string BuildId,
    string AgentId,
    string OperationId,
    BuildStopMode Mode,
    string Reason,
    DateTimeOffset Deadline);

public sealed record ExpiredAssignmentAttempt(
    string BuildId,
    string AgentId,
    string SessionId);

public sealed record ExpiredBuildLease(string BuildId, string AgentId, BuildResult Result);

/// <summary>Durable build ownership and terminal results; all mutations use the serialized writer.</summary>
public sealed class BuildStore
{
    private readonly VivariumDatabase database;
    private readonly IBlobArtifactAttachmentParticipant? artifactAttachments;

    public BuildStore(
        VivariumDatabase database,
        IBlobArtifactAttachmentParticipant? artifactAttachments = null)
    {
        this.database = database;
        this.artifactAttachments = artifactAttachments;
    }

    public Task CreateAsync(
        string agentId,
        string ownerSessionId,
        BuildAssignment assignment,
        DateTimeOffset now,
        DateTimeOffset? assignmentAckDeadline = null) => database.WriteAsync(connection =>
    {
        var nowUnixMs = now.ToUnixTimeMilliseconds();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO builds(
                build_id, agent_id, owner_session_id, state, assignment,
                created_unix_ms, updated_unix_ms)
            VALUES ($buildId, $agentId, $ownerSessionId, 'RUNNING', $assignment, $now, $now);
            """;
        command.Parameters.AddWithValue("$buildId", assignment.BuildId);
        command.Parameters.AddWithValue("$agentId", agentId);
        command.Parameters.AddWithValue("$ownerSessionId", ownerSessionId);
        command.Parameters.Add("$assignment", SqliteType.Blob).Value = assignment.ToByteArray();
        command.Parameters.AddWithValue("$now", nowUnixMs);
        command.ExecuteNonQuery();
        if (assignmentAckDeadline is not null)
        {
            using var attempt = connection.CreateCommand();
            attempt.Transaction = transaction;
            attempt.CommandText = """
                INSERT INTO build_assignment_attempts(
                    build_id, agent_id, session_id, state, deadline_unix_ms,
                    created_unix_ms, updated_unix_ms)
                VALUES($buildId, $agentId, $sessionId, 'WAITING', $deadline, $now, $now);
                """;
            attempt.Parameters.AddWithValue("$buildId", assignment.BuildId);
            attempt.Parameters.AddWithValue("$agentId", agentId);
            attempt.Parameters.AddWithValue("$sessionId", ownerSessionId);
            attempt.Parameters.AddWithValue(
                "$deadline", assignmentAckDeadline.Value.ToUnixTimeMilliseconds());
            attempt.Parameters.AddWithValue("$now", nowUnixMs);
            attempt.ExecuteNonQuery();
        }
        transaction.Commit();
        return true;
    });

    public Task<CancellationRequestResult> TryRequestCancellationAsync(string buildId, string reason) =>
        TryRequestStopAsync(
            buildId,
            reason,
            BuildStopMode.Graceful,
            DateTimeOffset.UtcNow.AddSeconds(30),
            DateTimeOffset.UtcNow,
            auditEvent: null);

    internal Task<CancellationRequestResult> TryRequestCancellationAsync(
        string buildId,
        string reason,
        AuditEventDraft? auditEvent) => TryRequestStopAsync(
            buildId,
            reason,
            BuildStopMode.Graceful,
            DateTimeOffset.UtcNow.AddSeconds(30),
            DateTimeOffset.UtcNow,
            auditEvent);

    internal Task<CancellationRequestResult> TryRequestStopAsync(
        string buildId,
        string reason,
        BuildStopMode requestedMode,
        DateTimeOffset requestedDeadline,
        DateTimeOffset now,
        AuditEventDraft? auditEvent)
    {
        if (requestedMode is not (BuildStopMode.Graceful or BuildStopMode.Force))
        {
            throw new ArgumentOutOfRangeException(nameof(requestedMode));
        }
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 512)
        {
            throw new ArgumentException("stop reason must contain 1-512 characters", nameof(reason));
        }

        var operationId = ManagementIdentifiers.NewId();
        return database.WriteAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            string? state;
            string? effectiveReason;
            string? existingOperationId;
            string? existingMode;
            long? existingDeadline;
            string? stopState;
            using (var current = connection.CreateCommand())
            {
                current.Transaction = transaction;
                current.CommandText = """
                    SELECT builds.state, builds.cancellation_reason,
                        stops.operation_id, stops.mode, stops.deadline_unix_ms, stops.state
                    FROM builds
                    LEFT JOIN build_stop_operations AS stops
                        ON stops.build_id = builds.build_id
                    WHERE builds.build_id = $buildId;
                    """;
                current.Parameters.AddWithValue("$buildId", buildId);
                using var reader = current.ExecuteReader();
                if (!reader.Read())
                {
                    transaction.Commit();
                    return new CancellationRequestResult(false, null);
                }

                state = reader.GetString(0);
                effectiveReason = reader.IsDBNull(1) ? null : reader.GetString(1);
                existingOperationId = reader.IsDBNull(2) ? null : reader.GetString(2);
                existingMode = reader.IsDBNull(3) ? null : reader.GetString(3);
                existingDeadline = reader.IsDBNull(4) ? null : reader.GetInt64(4);
                stopState = reader.IsDBNull(5) ? null : reader.GetString(5);
            }

            if (state is not ("RUNNING" or "CANCEL_REQUESTED"))
            {
                transaction.Commit();
                return new CancellationRequestResult(false, null);
            }

            effectiveReason ??= reason.Trim();
            var effectiveMode = existingMode == "FORCE" || requestedMode == BuildStopMode.Force
                ? BuildStopMode.Force
                : BuildStopMode.Graceful;
            var requestedDeadlineMs = requestedDeadline.ToUnixTimeMilliseconds();
            var effectiveDeadlineMs = stopState == "GRACE_EXPIRED" &&
                requestedMode == BuildStopMode.Force
                    ? requestedDeadlineMs
                    : Math.Min(existingDeadline ?? long.MaxValue, requestedDeadlineMs);
            var changed = existingOperationId is null ||
                existingMode != StopModeValue(effectiveMode) ||
                existingDeadline != effectiveDeadlineMs;

            using (var build = connection.CreateCommand())
            {
                build.Transaction = transaction;
                build.CommandText = """
                    UPDATE builds SET
                        state = 'CANCEL_REQUESTED',
                        cancellation_reason = COALESCE(cancellation_reason, $reason),
                        updated_unix_ms = $now
                    WHERE build_id = $buildId
                        AND state IN ('RUNNING', 'CANCEL_REQUESTED');
                    """;
                build.Parameters.AddWithValue("$reason", effectiveReason);
                build.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
                build.Parameters.AddWithValue("$buildId", buildId);
                build.ExecuteNonQuery();
            }

            if (existingOperationId is null)
            {
                using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO build_stop_operations(
                        build_id, operation_id, state, mode, reason, deadline_unix_ms,
                        created_unix_ms, updated_unix_ms)
                    VALUES(
                        $buildId, $operationId, 'REQUESTED', $mode, $reason, $deadline,
                        $now, $now);
                    """;
                insert.Parameters.AddWithValue("$buildId", buildId);
                insert.Parameters.AddWithValue("$operationId", operationId);
                insert.Parameters.AddWithValue("$mode", StopModeValue(effectiveMode));
                insert.Parameters.AddWithValue("$reason", effectiveReason);
                insert.Parameters.AddWithValue("$deadline", effectiveDeadlineMs);
                insert.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
                insert.ExecuteNonQuery();
                existingOperationId = operationId;
                stopState = "REQUESTED";
            }
            else if (changed)
            {
                using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE build_stop_operations SET
                        state = 'REQUESTED',
                        mode = $mode,
                        deadline_unix_ms = $deadline,
                        acknowledged_mode = NULL,
                        acknowledged_session_id = NULL,
                        acknowledged_unix_ms = NULL,
                        completed_unix_ms = NULL,
                        updated_unix_ms = $now
                    WHERE build_id = $buildId
                        AND state IN ('REQUESTED', 'ACKNOWLEDGED', 'GRACE_EXPIRED');
                    """;
                update.Parameters.AddWithValue("$mode", StopModeValue(effectiveMode));
                update.Parameters.AddWithValue("$deadline", effectiveDeadlineMs);
                update.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
                update.Parameters.AddWithValue("$buildId", buildId);
                update.ExecuteNonQuery();
                stopState = "REQUESTED";
            }

            if (changed)
            {
                if (auditEvent is not null)
                {
                    AuditEventStore.Append(connection, transaction, auditEvent);
                }

                BuildEventStore.AppendForChild(
                    connection,
                    transaction,
                    buildId,
                    effectiveMode == BuildStopMode.Force
                        ? "build.force-stop-requested"
                        : "build.cancellation-requested",
                    now,
                    auditEvent?.RequestContext);
            }

            transaction.Commit();
            return new CancellationRequestResult(
                true,
                effectiveReason,
                existingOperationId,
                effectiveMode,
                DateTimeOffset.FromUnixTimeMilliseconds(effectiveDeadlineMs),
                stopState == "ACKNOWLEDGED");
        });
    }

    public Task<bool> TryAcknowledgeStopAsync(
        BuildStopAcknowledged acknowledged,
        string agentId,
        DateTimeOffset now) => database.WriteAsync(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE build_stop_operations AS stops SET
                state = 'ACKNOWLEDGED',
                acknowledged_mode = $acknowledgedMode,
                acknowledged_session_id = $sessionId,
                acknowledged_unix_ms = $now,
                updated_unix_ms = $now
            FROM builds
            WHERE stops.build_id = $buildId
                AND stops.operation_id = $operationId
                AND stops.state = 'REQUESTED'
                AND builds.build_id = stops.build_id
                AND builds.agent_id = $agentId
                AND builds.state = 'CANCEL_REQUESTED'
                AND stops.mode = $acknowledgedMode;
            """;
        command.Parameters.AddWithValue("$acknowledgedMode", StopModeValue(acknowledged.Mode));
        command.Parameters.AddWithValue("$sessionId", acknowledged.SessionId);
        command.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$buildId", acknowledged.BuildId);
        command.Parameters.AddWithValue("$operationId", acknowledged.OperationId);
        command.Parameters.AddWithValue("$agentId", agentId);
        if (command.ExecuteNonQuery() == 1)
        {
            return true;
        }

        using var duplicate = connection.CreateCommand();
        duplicate.CommandText = """
            SELECT 1
            FROM build_stop_operations AS stops
            JOIN builds ON builds.build_id = stops.build_id
            WHERE stops.build_id = $buildId
                AND stops.operation_id = $operationId
                AND stops.state = 'ACKNOWLEDGED'
                AND stops.acknowledged_mode = $acknowledgedMode
                AND stops.acknowledged_session_id = $sessionId
                AND builds.agent_id = $agentId;
            """;
        duplicate.Parameters.AddWithValue("$acknowledgedMode", StopModeValue(acknowledged.Mode));
        duplicate.Parameters.AddWithValue("$sessionId", acknowledged.SessionId);
        duplicate.Parameters.AddWithValue("$buildId", acknowledged.BuildId);
        duplicate.Parameters.AddWithValue("$operationId", acknowledged.OperationId);
        duplicate.Parameters.AddWithValue("$agentId", agentId);
        return duplicate.ExecuteScalar() is not null;
    });

    public Task<bool> RecordAssignmentAttemptAsync(
        string buildId,
        string agentId,
        string sessionId,
        DateTimeOffset deadline,
        DateTimeOffset now) => database.WriteAsync(connection =>
    {
        using var transaction = connection.BeginTransaction();
        using (var owner = connection.CreateCommand())
        {
            owner.Transaction = transaction;
            owner.CommandText = """
                SELECT 1 FROM builds
                WHERE build_id = $buildId AND agent_id = $agentId
                    AND owner_session_id = $sessionId
                    AND state IN ('RUNNING', 'CANCEL_REQUESTED');
                """;
            owner.Parameters.AddWithValue("$buildId", buildId);
            owner.Parameters.AddWithValue("$agentId", agentId);
            owner.Parameters.AddWithValue("$sessionId", sessionId);
            if (owner.ExecuteScalar() is null)
            {
                transaction.Rollback();
                return false;
            }
        }

        string? priorSession = null;
        string? priorState = null;
        long? priorDeadline = null;
        using (var current = connection.CreateCommand())
        {
            current.Transaction = transaction;
            current.CommandText = """
                SELECT session_id, state, deadline_unix_ms
                FROM build_assignment_attempts WHERE build_id = $buildId;
                """;
            current.Parameters.AddWithValue("$buildId", buildId);
            using var reader = current.ExecuteReader();
            if (reader.Read())
            {
                priorSession = reader.GetString(0);
                priorState = reader.GetString(1);
                priorDeadline = reader.GetInt64(2);
            }
        }

        if (priorSession == sessionId && priorState == "ACKNOWLEDGED")
        {
            transaction.Commit();
            return true;
        }

        var deadlineMs = priorSession == sessionId && priorState == "WAITING"
            ? Math.Min(priorDeadline!.Value, deadline.ToUnixTimeMilliseconds())
            : deadline.ToUnixTimeMilliseconds();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO build_assignment_attempts(
                build_id, agent_id, session_id, state, deadline_unix_ms,
                created_unix_ms, updated_unix_ms)
            VALUES($buildId, $agentId, $sessionId, 'WAITING', $deadline, $now, $now)
            ON CONFLICT(build_id) DO UPDATE SET
                agent_id = excluded.agent_id,
                session_id = excluded.session_id,
                state = 'WAITING',
                deadline_unix_ms = excluded.deadline_unix_ms,
                updated_unix_ms = excluded.updated_unix_ms,
                acknowledged_unix_ms = NULL;
            """;
        command.Parameters.AddWithValue("$buildId", buildId);
        command.Parameters.AddWithValue("$agentId", agentId);
        command.Parameters.AddWithValue("$sessionId", sessionId);
        command.Parameters.AddWithValue("$deadline", deadlineMs);
        command.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        command.ExecuteNonQuery();
        transaction.Commit();
        return true;
    });

    public Task<bool> TryAcknowledgeAssignmentAsync(
        string buildId,
        string agentId,
        string sessionId,
        DateTimeOffset now) => database.WriteAsync(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE build_assignment_attempts SET
                state = 'ACKNOWLEDGED',
                acknowledged_unix_ms = $now,
                updated_unix_ms = $now
            WHERE build_id = $buildId AND agent_id = $agentId
                AND session_id = $sessionId AND state = 'WAITING';
            """;
        command.Parameters.AddWithValue("$buildId", buildId);
        command.Parameters.AddWithValue("$agentId", agentId);
        command.Parameters.AddWithValue("$sessionId", sessionId);
        command.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        if (command.ExecuteNonQuery() == 1)
        {
            return true;
        }

        using var duplicate = connection.CreateCommand();
        duplicate.CommandText = """
            SELECT 1 FROM build_assignment_attempts
            WHERE build_id = $buildId AND agent_id = $agentId
                AND session_id = $sessionId AND state = 'ACKNOWLEDGED';
            """;
        duplicate.Parameters.AddWithValue("$buildId", buildId);
        duplicate.Parameters.AddWithValue("$agentId", agentId);
        duplicate.Parameters.AddWithValue("$sessionId", sessionId);
        return duplicate.ExecuteScalar() is not null;
    });

    public Task<IReadOnlyList<ExpiredAssignmentAttempt>> ExpireDueAssignmentAttemptsAsync(
        DateTimeOffset now) => database.WriteAsync<IReadOnlyList<ExpiredAssignmentAttempt>>(connection =>
    {
        var result = new List<ExpiredAssignmentAttempt>();
        using (var select = connection.CreateCommand())
        {
            select.CommandText = """
                SELECT build_id, agent_id, session_id
                FROM build_assignment_attempts
                WHERE state = 'WAITING' AND deadline_unix_ms <= $now
                ORDER BY deadline_unix_ms, build_id;
                """;
            select.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new ExpiredAssignmentAttempt(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
        }
        if (result.Count == 0)
        {
            return result;
        }

        using var update = connection.CreateCommand();
        update.CommandText = """
            UPDATE build_assignment_attempts SET
                state = 'EXPIRED', updated_unix_ms = $now
            WHERE state = 'WAITING' AND deadline_unix_ms <= $now;
            """;
        update.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        update.ExecuteNonQuery();
        return result;
    });

    public Task<IReadOnlyList<DueBuildStop>> ListDueStopsAsync(DateTimeOffset now) =>
        database.ReadAsync<IReadOnlyList<DueBuildStop>>(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT stops.build_id, builds.agent_id, stops.operation_id,
                    stops.mode, stops.reason, stops.deadline_unix_ms
                FROM build_stop_operations AS stops
                JOIN builds ON builds.build_id = stops.build_id
                WHERE stops.state IN ('REQUESTED', 'ACKNOWLEDGED')
                    AND stops.deadline_unix_ms <= $now
                    AND builds.state = 'CANCEL_REQUESTED'
                ORDER BY stops.deadline_unix_ms, stops.operation_id;
                """;
            command.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
            using var reader = command.ExecuteReader();
            var result = new List<DueBuildStop>();
            while (reader.Read())
            {
                result.Add(new DueBuildStop(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    ParseStopMode(reader.GetString(3)),
                    reader.GetString(4),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5))));
            }
            return result;
        });

    public Task<bool> TryExpireGracefulStopAsync(
        string buildId,
        string operationId,
        DateTimeOffset now) => database.WriteAsync(connection =>
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE build_stop_operations SET
                    state = 'GRACE_EXPIRED',
                    completed_unix_ms = $now,
                    updated_unix_ms = $now
            WHERE build_id = $buildId
                AND operation_id = $operationId
                AND mode = 'GRACEFUL'
                AND state IN ('REQUESTED', 'ACKNOWLEDGED')
                AND deadline_unix_ms <= $now;
            """;
        command.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$buildId", buildId);
        command.Parameters.AddWithValue("$operationId", operationId);
        if (command.ExecuteNonQuery() != 1)
        {
            transaction.Rollback();
            return false;
        }

        BuildEventStore.AppendForChild(
            connection, transaction, buildId, "build.graceful-stop-expired", now);
        transaction.Commit();
        return true;
    });

    public Task<bool> TryExpireStopAsync(
        string buildId,
        string operationId,
        DateTimeOffset now) => database.WriteAsync(connection =>
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE build_stop_operations SET
                state = 'EXPIRED',
                completed_unix_ms = $now,
                updated_unix_ms = $now
            WHERE build_id = $buildId
                AND operation_id = $operationId
                AND mode = 'FORCE'
                AND state IN ('REQUESTED', 'ACKNOWLEDGED')
                AND deadline_unix_ms <= $now;
            """;
        command.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$buildId", buildId);
        command.Parameters.AddWithValue("$operationId", operationId);
        if (command.ExecuteNonQuery() != 1)
        {
            transaction.Rollback();
            return false;
        }

        BuildEventStore.AppendForChild(
            connection, transaction, buildId, "build.force-stop-expired", now);
        transaction.Commit();
        return true;
    });

    public Task<bool> TryFinishAsync(
        BuildResult result,
        string agentId,
        string ownerSessionId,
        DateTimeOffset now) => TryFinishAsync(
            result, agentId, ownerSessionId, connectionGeneration: null, now);

    public Task<bool> TryFinishAsync(
        BuildResult result,
        string agentId,
        string ownerSessionId,
        long connectionGeneration,
        DateTimeOffset now) => TryFinishAsync(
            result, agentId, ownerSessionId, (long?)connectionGeneration, now);

    private Task<bool> TryFinishAsync(
        BuildResult result,
        string agentId,
        string ownerSessionId,
        long? connectionGeneration,
        DateTimeOffset now) => database.WriteAsync(connection =>
    {
        var nowUnixMs = now.ToUnixTimeMilliseconds();
        using var transaction = connection.BeginTransaction();
        using (var eligible = connection.CreateCommand())
        {
            eligible.Transaction = transaction;
            eligible.CommandText = """
                SELECT 1 FROM builds
                WHERE build_id = $buildId
                    AND state IN ('RUNNING', 'CANCEL_REQUESTED')
                    AND agent_id = $agentId
                    AND owner_session_id = $ownerSessionId
                    AND (reconnect_deadline_unix_ms IS NULL
                        OR reconnect_deadline_unix_ms > $now);
                """;
            eligible.Parameters.AddWithValue("$buildId", result.BuildId);
            eligible.Parameters.AddWithValue("$agentId", agentId);
            eligible.Parameters.AddWithValue("$ownerSessionId", ownerSessionId);
            eligible.Parameters.AddWithValue("$now", nowUnixMs);
            if (eligible.ExecuteScalar() is null)
            {
                transaction.Rollback();
                return false;
            }
        }

        if (artifactAttachments is not null)
        {
            if (connectionGeneration is null or <= 0)
            {
                throw new InvalidOperationException(
                    "terminal result attachment requires the current Agent connection generation");
            }

            artifactAttachments.Attach(
                connection,
                transaction,
                new BlobArtifactAttachmentRequest(
                    result.BuildId,
                    agentId,
                    ownerSessionId,
                    connectionGeneration.Value,
                    result.Artifacts.Select((artifact, ordinal) =>
                        new BlobArtifactAttachment(
                            ordinal.ToString(CultureInfo.InvariantCulture),
                            artifact.Path,
                            artifact.Sha256,
                            artifact.Size)).ToArray(),
                    now));
        }
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE builds SET
                    state = 'FINISHED',
                    result = $result,
                    reconnect_deadline_unix_ms = NULL,
                    updated_unix_ms = $now
                WHERE build_id = $buildId
                    AND state IN ('RUNNING', 'CANCEL_REQUESTED')
                    AND agent_id = $agentId
                    AND owner_session_id = $ownerSessionId
                    AND (reconnect_deadline_unix_ms IS NULL
                        OR reconnect_deadline_unix_ms > $now);
                """;
            command.Parameters.Add("$result", SqliteType.Blob).Value = result.ToByteArray();
            command.Parameters.AddWithValue("$now", nowUnixMs);
            command.Parameters.AddWithValue("$buildId", result.BuildId);
            command.Parameters.AddWithValue("$agentId", agentId);
            command.Parameters.AddWithValue("$ownerSessionId", ownerSessionId);
            if (command.ExecuteNonQuery() != 1)
            {
                transaction.Rollback();
                return false;
            }
        }

        using (var stop = connection.CreateCommand())
        {
            stop.Transaction = transaction;
            stop.CommandText = """
                UPDATE build_stop_operations SET
                    state = 'COMPLETED',
                    completed_unix_ms = $now,
                    updated_unix_ms = $now
                WHERE build_id = $buildId
                    AND state IN ('REQUESTED', 'ACKNOWLEDGED', 'GRACE_EXPIRED', 'EXPIRED');
                """;
            stop.Parameters.AddWithValue("$now", nowUnixMs);
            stop.Parameters.AddWithValue("$buildId", result.BuildId);
            stop.ExecuteNonQuery();
        }

        BuildEventStore.AppendForChild(
            connection, transaction, result.BuildId, "build.finished", now);
        transaction.Commit();
        return true;
    });

    /// <summary>
    /// Transfers an active build to a matching reconnected session. The previous owner is a CAS
    /// fence, preventing a delayed superseded stream from stealing ownership back.
    /// </summary>
    public Task<bool> TryAdoptSessionAsync(
        string buildId,
        string agentId,
        string? previousOwnerSessionId,
        string newOwnerSessionId,
        DateTimeOffset now) => database.WriteAsync(connection =>
        {
            var nowUnixMs = now.ToUnixTimeMilliseconds();
            using var transaction = connection.BeginTransaction();
            using (var build = connection.CreateCommand())
            {
                build.Transaction = transaction;
                build.CommandText = """
                    UPDATE builds SET
                        owner_session_id = $newOwnerSessionId,
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
                build.Parameters.AddWithValue("$newOwnerSessionId", newOwnerSessionId);
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
                    UPDATE build_queue SET dispatched_session_id = $newOwnerSessionId
                    WHERE build_id = $buildId
                        AND state = 'CLAIMED'
                        AND claimed_agent_id = $agentId;
                    """;
                queue.Parameters.AddWithValue("$buildId", buildId);
                queue.Parameters.AddWithValue("$agentId", agentId);
                queue.Parameters.AddWithValue("$newOwnerSessionId", newOwnerSessionId);
                queue.ExecuteNonQuery();
            }

            using (var assignmentAttempt = connection.CreateCommand())
            {
                assignmentAttempt.Transaction = transaction;
                assignmentAttempt.CommandText = """
                    UPDATE build_assignment_attempts SET
                        session_id = $newOwnerSessionId,
                        state = 'ACKNOWLEDGED',
                        acknowledged_unix_ms = $now,
                        updated_unix_ms = $now
                    WHERE build_id = $buildId AND agent_id = $agentId
                        AND state = 'WAITING';
                    """;
                assignmentAttempt.Parameters.AddWithValue("$buildId", buildId);
                assignmentAttempt.Parameters.AddWithValue("$agentId", agentId);
                assignmentAttempt.Parameters.AddWithValue("$newOwnerSessionId", newOwnerSessionId);
                assignmentAttempt.Parameters.AddWithValue("$now", nowUnixMs);
                assignmentAttempt.ExecuteNonQuery();
            }

            BuildEventStore.AppendForChild(
                connection, transaction, buildId, "build.owner-adopted", now);

            transaction.Commit();
            return true;
        });

    /// <summary>Arms one non-extending reconnect deadline for the exact lost owner session.</summary>
    public Task<bool> TryArmReconnectGraceAsync(
        string buildId,
        string agentId,
        string lostOwnerSessionId,
        DateTimeOffset deadline,
        DateTimeOffset now) => database.WriteAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE builds SET
                    reconnect_deadline_unix_ms = $deadline,
                    updated_unix_ms = $now
                WHERE build_id = $buildId
                    AND state IN ('RUNNING', 'CANCEL_REQUESTED')
                    AND agent_id = $agentId
                    AND owner_session_id = $ownerSessionId
                    AND reconnect_deadline_unix_ms IS NULL;
                """;
            command.Parameters.AddWithValue("$buildId", buildId);
            command.Parameters.AddWithValue("$agentId", agentId);
            command.Parameters.AddWithValue("$ownerSessionId", lostOwnerSessionId);
            command.Parameters.AddWithValue("$deadline", deadline.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
            if (command.ExecuteNonQuery() != 1)
            {
                transaction.Rollback();
                return false;
            }

            BuildEventStore.AppendForChild(
                connection, transaction, buildId, "build.reconnect-grace-armed", now);
            transaction.Commit();
            return true;
        });

    /// <summary>
    /// Arms startup grace for one active row only if its original owner still matches. The nullable
    /// owner comparison covers rows created by an older schema without overwriting a re-adoption.
    /// </summary>
    public Task<bool> TryArmStartupReconnectGraceAsync(
        string buildId,
        string agentId,
        string? originalOwnerSessionId,
        DateTimeOffset deadline,
        DateTimeOffset now) => database.WriteAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE builds SET
                    reconnect_deadline_unix_ms = $deadline,
                    updated_unix_ms = $now
                WHERE build_id = $buildId
                    AND state IN ('RUNNING', 'CANCEL_REQUESTED')
                    AND agent_id = $agentId
                    AND (($ownerSessionId IS NULL AND owner_session_id IS NULL)
                        OR owner_session_id = $ownerSessionId)
                    AND reconnect_deadline_unix_ms IS NULL;
                """;
            command.Parameters.AddWithValue("$buildId", buildId);
            command.Parameters.AddWithValue("$agentId", agentId);
            command.Parameters.AddWithValue(
                "$ownerSessionId",
                (object?)originalOwnerSessionId ?? DBNull.Value);
            command.Parameters.AddWithValue("$deadline", deadline.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
            if (command.ExecuteNonQuery() != 1)
            {
                transaction.Rollback();
                return false;
            }

            BuildEventStore.AppendForChild(
                connection, transaction, buildId, "build.reconnect-grace-armed", now);
            transaction.Commit();
            return true;
        });

    /// <summary>
    /// Finalizes every due reconnect lease and releases any durable queue claim in one transaction.
    /// The non-null deadline is retained as provenance for safely acknowledging late agent results.
    /// </summary>
    public Task<IReadOnlyList<ExpiredBuildLease>> ExpireDueReconnectLeasesAsync(
        DateTimeOffset now) => database.WriteAsync<IReadOnlyList<ExpiredBuildLease>>(connection =>
        {
            var nowUnixMs = now.ToUnixTimeMilliseconds();
            var due = new List<(string BuildId, string AgentId)>();
            using (var select = connection.CreateCommand())
            {
                select.CommandText = """
                    SELECT build_id, agent_id
                    FROM builds
                    WHERE state IN ('RUNNING', 'CANCEL_REQUESTED')
                        AND reconnect_deadline_unix_ms IS NOT NULL
                        AND reconnect_deadline_unix_ms <= $now
                    ORDER BY reconnect_deadline_unix_ms, build_id;
                    """;
                select.Parameters.AddWithValue("$now", nowUnixMs);
                using var reader = select.ExecuteReader();
                while (reader.Read())
                {
                    due.Add((reader.GetString(0), reader.GetString(1)));
                }
            }

            if (due.Count == 0)
            {
                return Array.Empty<ExpiredBuildLease>();
            }

            var expired = new List<ExpiredBuildLease>(due.Count);
            using var transaction = connection.BeginTransaction();
            foreach (var item in due)
            {
                var result = new BuildResult
                {
                    BuildId = item.BuildId,
                    Outcome = BuildOutcome.InfrastructureFailed,
                    StatusText = ReconnectGraceExpiredStatus,
                };
                using (var build = connection.CreateCommand())
                {
                    build.Transaction = transaction;
                    build.CommandText = """
                        UPDATE builds SET
                            state = 'FINISHED',
                            result = $result,
                            updated_unix_ms = $now
                        WHERE build_id = $buildId
                            AND state IN ('RUNNING', 'CANCEL_REQUESTED')
                            AND reconnect_deadline_unix_ms IS NOT NULL
                            AND reconnect_deadline_unix_ms <= $now;
                        """;
                    build.Parameters.Add("$result", SqliteType.Blob).Value = result.ToByteArray();
                    build.Parameters.AddWithValue("$now", nowUnixMs);
                    build.Parameters.AddWithValue("$buildId", item.BuildId);
                    if (build.ExecuteNonQuery() != 1)
                    {
                        continue;
                    }
                }

                using (var queue = connection.CreateCommand())
                {
                    queue.Transaction = transaction;
                    queue.CommandText = """
                        UPDATE build_queue SET
                            state = 'REMOVED',
                            removed_unix_ms = $now,
                            removal_reason = $reason
                        WHERE build_id = $buildId AND state = 'CLAIMED';
                        """;
                    queue.Parameters.AddWithValue("$now", nowUnixMs);
                    queue.Parameters.AddWithValue("$reason", ReconnectGraceExpiredStatus);
                    queue.Parameters.AddWithValue("$buildId", item.BuildId);
                    queue.ExecuteNonQuery();
                }

                BuildEventStore.AppendForChild(
                    connection,
                    transaction,
                    item.BuildId,
                    "build.finished",
                    now);

                expired.Add(new ExpiredBuildLease(item.BuildId, item.AgentId, result));
            }

            transaction.Commit();
            return expired;
        });

    public Task<bool> IsReconnectLeaseFailureAsync(string buildId, string agentId) =>
        database.ReadAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT result, reconnect_deadline_unix_ms
                FROM builds
                WHERE build_id = $buildId AND agent_id = $agentId AND state = 'FINISHED';
                """;
            command.Parameters.AddWithValue("$buildId", buildId);
            command.Parameters.AddWithValue("$agentId", agentId);
            using var reader = command.ExecuteReader();
            if (!reader.Read() || reader.IsDBNull(0) || reader.IsDBNull(1))
            {
                return false;
            }

            var result = BuildResult.Parser.ParseFrom((byte[])reader[0]);
            return result.Outcome == BuildOutcome.InfrastructureFailed &&
                result.StatusText == ReconnectGraceExpiredStatus;
        });

    public Task DeleteAsync(string buildId) => database.WriteAsync(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM builds WHERE build_id = $buildId;";
        command.Parameters.AddWithValue("$buildId", buildId);
        command.ExecuteNonQuery();
        return true;
    });

    public Task<IReadOnlyList<StoredBuild>> ListAssignedActiveAsync() =>
        database.ReadAsync<IReadOnlyList<StoredBuild>>(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT builds.build_id, builds.agent_id, builds.owner_session_id,
                    builds.reconnect_deadline_unix_ms,
                    builds.state, builds.cancellation_reason, builds.assignment, builds.result,
                    stops.operation_id, stops.mode, stops.deadline_unix_ms, stops.state
                FROM builds
                LEFT JOIN build_stop_operations AS stops
                    ON stops.build_id = builds.build_id
                WHERE builds.state IN ('RUNNING', 'CANCEL_REQUESTED')
                ORDER BY builds.created_unix_ms;
                """;
            using var reader = command.ExecuteReader();
            var result = new List<StoredBuild>();
            while (reader.Read())
            {
                result.Add(ReadBuild(reader));
            }

            return result;
        });

    public Task<StoredBuild?> GetAsync(string buildId) => database.ReadAsync(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT builds.build_id, builds.agent_id, builds.owner_session_id,
                builds.reconnect_deadline_unix_ms,
                builds.state, builds.cancellation_reason, builds.assignment, builds.result,
                stops.operation_id, stops.mode, stops.deadline_unix_ms, stops.state
            FROM builds
            LEFT JOIN build_stop_operations AS stops ON stops.build_id = builds.build_id
            WHERE builds.build_id = $buildId;
            """;
        command.Parameters.AddWithValue("$buildId", buildId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadBuild(reader) : null;
    });

    private static StoredBuild ReadBuild(SqliteDataReader reader)
    {
        var state = reader.GetString(4) switch
        {
            "QUEUED" => TrackedBuildState.Queued,
            "RUNNING" => TrackedBuildState.Running,
            "CANCEL_REQUESTED" => TrackedBuildState.CancelRequested,
            "FINISHED" => TrackedBuildState.Finished,
            var value => throw new InvalidDataException($"unknown persisted build state '{value}'"),
        };
        return new StoredBuild(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3)
                ? null
                : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)),
            state,
            reader.IsDBNull(5) ? null : reader.GetString(5),
            BuildAssignment.Parser.ParseFrom((byte[])reader[6]),
            reader.IsDBNull(7) ? null : BuildResult.Parser.ParseFrom((byte[])reader[7]),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? BuildStopMode.Unspecified : ParseStopMode(reader.GetString(9)),
            reader.IsDBNull(10)
                ? null
                : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(10)),
            !reader.IsDBNull(11) && reader.GetString(11) == "ACKNOWLEDGED");
    }

    private static string StopModeValue(BuildStopMode mode) => mode switch
    {
        BuildStopMode.Graceful => "GRACEFUL",
        BuildStopMode.Force => "FORCE",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    private static BuildStopMode ParseStopMode(string value) => value switch
    {
        "GRACEFUL" => BuildStopMode.Graceful,
        "FORCE" => BuildStopMode.Force,
        _ => throw new InvalidDataException($"unknown persisted Build stop mode '{value}'"),
    };

    public const string ReconnectGraceExpiredStatus = "agent reconnect grace expired";
}
