using System.Globalization;
using Google.Protobuf;
using Microsoft.Data.Sqlite;
using Vivarium.Contracts.V1;
using Vivarium.Controller.Auditing;
using Vivarium.Controller.Blobs.Access;
using Vivarium.Controller.Persistence;
using Vivarium.Controller.Rest.Events;

namespace Vivarium.Controller.Builds;

public sealed record StoredBuild(
    string BuildId,
    string? AgentId,
    string? OwnerSessionId,
    DateTimeOffset? ReconnectDeadline,
    TrackedBuildState State,
    string? CancellationReason,
    BuildAssignment Assignment,
    BuildResult? Result);

public sealed record CancellationRequestResult(bool Active, string? Reason);

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
        DateTimeOffset now) => database.WriteAsync(connection =>
    {
        var nowUnixMs = now.ToUnixTimeMilliseconds();
        using var command = connection.CreateCommand();
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
        return true;
    });

    public Task<CancellationRequestResult> TryRequestCancellationAsync(string buildId, string reason) =>
        TryRequestCancellationAsync(buildId, reason, auditEvent: null);

    internal Task<CancellationRequestResult> TryRequestCancellationAsync(
        string buildId,
        string reason,
        AuditEventDraft? auditEvent) =>
        database.WriteAsync(connection =>
        {
            var now = DateTimeOffset.UtcNow;
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE builds SET
                    state = 'CANCEL_REQUESTED',
                    cancellation_reason = $reason,
                    updated_unix_ms = $now
                WHERE build_id = $buildId AND state = 'RUNNING';
                """;
            command.Parameters.AddWithValue("$reason", reason);
            command.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$buildId", buildId);
            if (command.ExecuteNonQuery() == 1)
            {
                if (auditEvent is not null)
                {
                    AuditEventStore.Append(connection, transaction, auditEvent);
                }

                BuildEventStore.AppendForChild(
                    connection,
                    transaction,
                    buildId,
                    "build.cancellation-requested",
                    now,
                    auditEvent?.RequestContext);

                transaction.Commit();
                return new CancellationRequestResult(true, reason);
            }

            using var current = connection.CreateCommand();
            current.Transaction = transaction;
            current.CommandText = "SELECT state, cancellation_reason FROM builds WHERE build_id = $buildId;";
            current.Parameters.AddWithValue("$buildId", buildId);
            using var reader = current.ExecuteReader();
            var result = reader.Read() && reader.GetString(0) == "CANCEL_REQUESTED"
                ? new CancellationRequestResult(
                    true, reader.IsDBNull(1) ? reason : reader.GetString(1))
                : new CancellationRequestResult(false, null);
            reader.Close();
            transaction.Commit();
            return result;
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
                SELECT build_id, agent_id, owner_session_id, reconnect_deadline_unix_ms,
                    state, cancellation_reason, assignment, result
                FROM builds
                WHERE state IN ('RUNNING', 'CANCEL_REQUESTED')
                ORDER BY created_unix_ms;
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
            SELECT build_id, agent_id, owner_session_id, reconnect_deadline_unix_ms,
                state, cancellation_reason, assignment, result
            FROM builds WHERE build_id = $buildId;
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
            reader.IsDBNull(7) ? null : BuildResult.Parser.ParseFrom((byte[])reader[7]));
    }

    public const string ReconnectGraceExpiredStatus = "agent reconnect grace expired";
}
