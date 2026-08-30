using Microsoft.Data.Sqlite;
using Vivarium.Contracts.V1;
using Vivarium.Controller.Auditing;
using Vivarium.Controller.Persistence;
using Vivarium.Controller.Security;

namespace Vivarium.Controller.Agents;

public enum AgentRestartState
{
    Requested,
    Acknowledged,
    Succeeded,
    Failed,
}

public sealed record AgentRestartOperation(
    string OperationId,
    string AgentId,
    AgentRestartState State,
    AgentRestartMode Mode,
    string Reason,
    long RequestedConnectionGeneration,
    string RequestedProcessInstanceId,
    long? AcknowledgedConnectionGeneration,
    long? ObservedConnectionGeneration,
    string? ObservedProcessInstanceId,
    DateTimeOffset Deadline,
    string FailureCode,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt);

public sealed class AgentRestartRequestConflictException : Exception;
public sealed class AgentRestartAlreadyActiveException : Exception;
public sealed record AgentRestartRequestReplay(
    AgentRestartOperation Operation,
    string RequestHash);

/// <summary>Restart-safe Agent restart intent and exact generation confirmation (D31).</summary>
public sealed class AgentRestartStore(VivariumDatabase database)
{
    public Task<AgentRestartOperation> CreateAsync(
        string agentId,
        AgentRestartMode mode,
        string reason,
        long connectionGeneration,
        string processInstanceId,
        string requestHash,
        ManagementRequestContext context,
        DateTimeOffset deadline,
        DateTimeOffset now,
        AuditEventDraft auditEvent) => database.WriteAsync(connection =>
    {
        using var transaction = connection.BeginTransaction();
        using (var existing = connection.CreateCommand())
        {
            existing.Transaction = transaction;
            existing.CommandText = """
                SELECT operation_id, request_hash
                FROM agent_restart_operations
                WHERE actor_type = $actorType AND actor_id = $actorId
                    AND request_id = $requestId;
                """;
            existing.Parameters.AddWithValue("$actorType", context.Principal.ActorType);
            existing.Parameters.AddWithValue("$actorId", context.Principal.ActorId);
            existing.Parameters.AddWithValue("$requestId", context.RequestId!);
            using var reader = existing.ExecuteReader();
            if (reader.Read())
            {
                var operationId = reader.GetString(0);
                var same = string.Equals(reader.GetString(1), requestHash, StringComparison.Ordinal);
                reader.Close();
                transaction.Commit();
                if (!same)
                {
                    throw new AgentRestartRequestConflictException();
                }
                return ReadRequired(connection, operationId);
            }
        }

        var newOperationId = ManagementIdentifiers.NewId();
        try
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO agent_restart_operations(
                    operation_id, agent_id, state, mode,
                    actor_type, actor_id, request_id, request_hash, correlation_id,
                    reason, requested_connection_generation, requested_process_instance_id,
                    deadline_unix_ms,
                    created_unix_ms, updated_unix_ms)
                VALUES(
                    $operationId, $agentId, 'REQUESTED', $mode,
                    $actorType, $actorId, $requestId, $requestHash, $correlationId,
                    $reason, $generation, $processInstanceId, $deadline, $now, $now);
                """;
            insert.Parameters.AddWithValue("$operationId", newOperationId);
            insert.Parameters.AddWithValue("$agentId", agentId);
            insert.Parameters.AddWithValue("$mode", ModeValue(mode));
            insert.Parameters.AddWithValue("$actorType", context.Principal.ActorType);
            insert.Parameters.AddWithValue("$actorId", context.Principal.ActorId);
            insert.Parameters.AddWithValue("$requestId", context.RequestId!);
            insert.Parameters.AddWithValue("$requestHash", requestHash);
            insert.Parameters.AddWithValue("$correlationId", context.CorrelationId);
            insert.Parameters.AddWithValue("$reason", reason);
            insert.Parameters.AddWithValue("$generation", connectionGeneration);
            insert.Parameters.AddWithValue("$processInstanceId", processInstanceId);
            insert.Parameters.AddWithValue("$deadline", deadline.ToUnixTimeMilliseconds());
            insert.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
            insert.ExecuteNonQuery();
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            transaction.Rollback();
            throw new AgentRestartAlreadyActiveException();
        }

        AuditEventStore.Append(connection, transaction, auditEvent);
        transaction.Commit();
        return ReadRequired(connection, newOperationId);
    });

    public Task<AgentRestartOperation?> FindAsync(string operationId) =>
        database.ReadAsync(connection => Read(connection, operationId));

    public Task<bool> HasActiveAsync(string agentId) => database.ReadAsync(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 1 FROM agent_restart_operations
            WHERE agent_id = $agentId AND state IN ('REQUESTED', 'ACKNOWLEDGED')
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$agentId", agentId);
        return command.ExecuteScalar() is not null;
    });

    public Task<AgentRestartRequestReplay?> FindByRequestAsync(
        ManagementRequestContext context) => database.ReadAsync(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT operation_id, request_hash
            FROM agent_restart_operations
            WHERE actor_type = $actorType AND actor_id = $actorId
                AND request_id = $requestId;
            """;
        command.Parameters.AddWithValue("$actorType", context.Principal.ActorType);
        command.Parameters.AddWithValue("$actorId", context.Principal.ActorId);
        command.Parameters.AddWithValue("$requestId", context.RequestId!);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var operationId = reader.GetString(0);
        var requestHash = reader.GetString(1);
        reader.Close();
        return new AgentRestartRequestReplay(
            ReadRequired(connection, operationId),
            requestHash);
    });

    public Task<IReadOnlyList<AgentRestartOperation>> ListActiveAsync() =>
        database.ReadAsync<IReadOnlyList<AgentRestartOperation>>(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT operation_id, agent_id, state, mode, reason,
                    requested_connection_generation, requested_process_instance_id,
                    acknowledged_connection_generation, observed_connection_generation,
                    observed_process_instance_id, deadline_unix_ms, failure_code,
                    created_unix_ms, updated_unix_ms, completed_unix_ms
                FROM agent_restart_operations
                WHERE state IN ('REQUESTED', 'ACKNOWLEDGED')
                ORDER BY created_unix_ms, operation_id;
                """;
            using var reader = command.ExecuteReader();
            var result = new List<AgentRestartOperation>();
            while (reader.Read())
            {
                result.Add(ReadOperation(reader));
            }
            return result;
        });

    public Task<bool> TryAcknowledgeAsync(
        string operationId,
        string agentId,
        long connectionGeneration,
        DateTimeOffset now) => database.WriteAsync(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE agent_restart_operations SET
                state = 'ACKNOWLEDGED',
                acknowledged_connection_generation = $generation,
                updated_unix_ms = $now
            WHERE operation_id = $operationId AND agent_id = $agentId
                AND state = 'REQUESTED'
                AND requested_connection_generation = $generation;
            """;
        command.Parameters.AddWithValue("$operationId", operationId);
        command.Parameters.AddWithValue("$agentId", agentId);
        command.Parameters.AddWithValue("$generation", connectionGeneration);
        command.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        return command.ExecuteNonQuery() == 1;
    });

    public Task<bool> TryCompleteAsync(
        string agentId,
        long observedConnectionGeneration,
        string observedProcessInstanceId,
        DateTimeOffset now) => database.WriteAsync(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE agent_restart_operations SET
                state = 'SUCCEEDED',
                observed_connection_generation = $generation,
                observed_process_instance_id = $processInstanceId,
                completed_unix_ms = $now,
                updated_unix_ms = $now
            WHERE agent_id = $agentId
                AND state IN ('REQUESTED', 'ACKNOWLEDGED')
                AND requested_connection_generation < $generation
                AND requested_process_instance_id <> $processInstanceId;
            """;
        command.Parameters.AddWithValue("$agentId", agentId);
        command.Parameters.AddWithValue("$generation", observedConnectionGeneration);
        command.Parameters.AddWithValue("$processInstanceId", observedProcessInstanceId);
        command.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        return command.ExecuteNonQuery() == 1;
    });

    public Task<IReadOnlyList<string>> FailDueAsync(DateTimeOffset now) =>
        database.WriteAsync<IReadOnlyList<string>>(connection =>
        {
            var agents = new List<string>();
            using (var select = connection.CreateCommand())
            {
                select.CommandText = """
                    SELECT agent_id FROM agent_restart_operations
                    WHERE state IN ('REQUESTED', 'ACKNOWLEDGED')
                        AND deadline_unix_ms <= $now;
                    """;
                select.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
                using var reader = select.ExecuteReader();
                while (reader.Read())
                {
                    agents.Add(reader.GetString(0));
                }
            }
            if (agents.Count == 0)
            {
                return agents;
            }

            using var update = connection.CreateCommand();
            update.CommandText = """
                UPDATE agent_restart_operations SET
                    state = 'FAILED',
                    failure_code = 'restart_deadline_expired',
                    completed_unix_ms = $now,
                    updated_unix_ms = $now
                WHERE state IN ('REQUESTED', 'ACKNOWLEDGED')
                    AND deadline_unix_ms <= $now;
                """;
            update.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
            update.ExecuteNonQuery();
            return agents;
        });

    private static AgentRestartOperation ReadRequired(SqliteConnection connection, string operationId) =>
        Read(connection, operationId)
        ?? throw new InvalidDataException("Agent restart operation disappeared after mutation");

    private static AgentRestartOperation? Read(SqliteConnection connection, string operationId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT operation_id, agent_id, state, mode, reason,
                requested_connection_generation, requested_process_instance_id,
                acknowledged_connection_generation, observed_connection_generation,
                observed_process_instance_id, deadline_unix_ms, failure_code,
                created_unix_ms, updated_unix_ms, completed_unix_ms
            FROM agent_restart_operations WHERE operation_id = $operationId;
            """;
        command.Parameters.AddWithValue("$operationId", operationId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadOperation(reader) : null;
    }

    private static AgentRestartOperation ReadOperation(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        ParseState(reader.GetString(2)),
        ParseMode(reader.GetString(3)),
        reader.GetString(4),
        reader.GetInt64(5),
        reader.GetString(6),
        reader.IsDBNull(7) ? null : reader.GetInt64(7),
        reader.IsDBNull(8) ? null : reader.GetInt64(8),
        reader.IsDBNull(9) ? null : reader.GetString(9),
        DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(10)),
        reader.GetString(11),
        DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(12)),
        DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(13)),
        reader.IsDBNull(14) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(14)));

    internal static string ModeValue(AgentRestartMode mode) => mode switch
    {
        AgentRestartMode.AfterCurrentWork => "AFTER_CURRENT_WORK",
        AgentRestartMode.CancelThenRestart => "CANCEL_THEN_RESTART",
        AgentRestartMode.Force => "FORCE",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    private static AgentRestartMode ParseMode(string value) => value switch
    {
        "AFTER_CURRENT_WORK" => AgentRestartMode.AfterCurrentWork,
        "CANCEL_THEN_RESTART" => AgentRestartMode.CancelThenRestart,
        "FORCE" => AgentRestartMode.Force,
        _ => throw new InvalidDataException($"unknown persisted Agent restart mode '{value}'"),
    };

    private static AgentRestartState ParseState(string value) => value switch
    {
        "REQUESTED" => AgentRestartState.Requested,
        "ACKNOWLEDGED" => AgentRestartState.Acknowledged,
        "SUCCEEDED" => AgentRestartState.Succeeded,
        "FAILED" => AgentRestartState.Failed,
        _ => throw new InvalidDataException($"unknown persisted Agent restart state '{value}'"),
    };
}
