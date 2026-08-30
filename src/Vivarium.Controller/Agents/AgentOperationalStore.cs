using Microsoft.Data.Sqlite;
using Vivarium.Controller.Persistence;

namespace Vivarium.Controller.Agents;

public sealed record StoredAgentOperationalState(
    AgentOperationalHealth Health,
    bool Quarantined,
    string Reason,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Durable operational evidence is runtime state, not desired configuration. Quarantine survives
/// Controller restarts and can only be cleared by an explicit recovery operation (D31).
/// </summary>
public sealed class AgentOperationalStore(VivariumDatabase database)
{
    public Task<StoredAgentOperationalState?> GetAsync(string agentId) =>
        database.ReadAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT health, quarantined, reason_code, updated_unix_ms
                FROM agent_operational_health
                WHERE agent_id = $agentId;
                """;
            command.Parameters.AddWithValue("$agentId", agentId);
            using var reader = command.ExecuteReader();
            return reader.Read()
                ? new StoredAgentOperationalState(
                    ParseHealth(reader.GetString(0)),
                    reader.GetInt64(1) != 0,
                    reader.GetString(2),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)))
                : null;
        });

    public Task SetAsync(
        string agentId,
        AgentOperationalHealth health,
        bool quarantined,
        string reason,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 128 ||
            reason.Any(character => character is '\r' or '\n' or '\0'))
        {
            throw new ArgumentException(
                "operational reason must contain 1-128 safe characters", nameof(reason));
        }

        return database.WriteAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO agent_operational_health(
                    agent_id, health, quarantined, reason_code, updated_unix_ms)
                VALUES($agentId, $health, $quarantined, $reason, $now)
                ON CONFLICT(agent_id) DO UPDATE SET
                    health = excluded.health,
                    quarantined = excluded.quarantined,
                    reason_code = excluded.reason_code,
                    updated_unix_ms = excluded.updated_unix_ms;
                """;
            command.Parameters.AddWithValue("$agentId", agentId);
            command.Parameters.AddWithValue("$health", HealthValue(health));
            command.Parameters.AddWithValue("$quarantined", quarantined ? 1 : 0);
            command.Parameters.AddWithValue("$reason", reason);
            command.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
            command.ExecuteNonQuery();
            return true;
        });
    }

    private static string HealthValue(AgentOperationalHealth health) => health switch
    {
        AgentOperationalHealth.Unknown => "UNKNOWN",
        AgentOperationalHealth.Healthy => "HEALTHY",
        AgentOperationalHealth.Unhealthy => "UNHEALTHY",
        _ => throw new ArgumentOutOfRangeException(nameof(health)),
    };

    private static AgentOperationalHealth ParseHealth(string value) => value switch
    {
        "UNKNOWN" => AgentOperationalHealth.Unknown,
        "HEALTHY" => AgentOperationalHealth.Healthy,
        "UNHEALTHY" => AgentOperationalHealth.Unhealthy,
        _ => throw new InvalidDataException($"unknown persisted Agent health '{value}'"),
    };
}
