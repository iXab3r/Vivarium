using System.Text.Json;
using Microsoft.Data.Sqlite;
using Vivarium.Contracts.V1;
using Vivarium.Controller.Persistence;

namespace Vivarium.Controller.Agents;

public sealed record StoredAgent(
    string AgentId,
    string Name,
    bool Authorized,
    bool Enabled,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen,
    IReadOnlyDictionary<string, string> ReportedParameters,
    IReadOnlyDictionary<string, string> CustomParameters,
    string AgentVersion,
    string OsFamily,
    string OsVersion,
    string Architecture,
    bool Interactive)
{
    public IReadOnlyDictionary<string, string> Parameters { get; } =
        AgentParameterMaps.Merge(ReportedParameters, CustomParameters);
}

/// <summary>Persistent TeamCity-style agent decisions, reported facts, and operator parameters.</summary>
public sealed class AgentStore
{
    private readonly VivariumDatabase database;

    public AgentStore(VivariumDatabase database) => this.database = database;

    public Task ObserveHelloAsync(Hello hello) => database.WriteAsync(connection =>
    {
        var custom = ReadParameterMap(connection, hello.AgentId, "custom_parameters_json")
            ?? throw new InvalidOperationException($"unknown agent '{hello.AgentId}'");
        var reported = AgentParameterMaps.Normalize(hello.Parameters);
        _ = AgentParameterMaps.Merge(reported, custom);

        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE agents SET
                last_seen_unix_ms = $lastSeen,
                parameters_json = $parameters,
                agent_version = $version,
                os_family = $osFamily,
                os_version = $osVersion,
                architecture = $architecture,
                interactive = $interactive
            WHERE agent_id = $agentId;
            """;
        command.Parameters.AddWithValue("$lastSeen", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$parameters", JsonSerializer.Serialize(reported));
        command.Parameters.AddWithValue("$version", hello.AgentVersion);
        command.Parameters.AddWithValue("$osFamily", hello.Os?.Family ?? string.Empty);
        command.Parameters.AddWithValue("$osVersion", hello.Os?.Version ?? string.Empty);
        command.Parameters.AddWithValue("$architecture", hello.Os?.Arch ?? string.Empty);
        command.Parameters.AddWithValue("$interactive", hello.Interactive ? 1 : 0);
        command.Parameters.AddWithValue("$agentId", hello.AgentId);
        if (command.ExecuteNonQuery() == 0)
        {
            throw new InvalidOperationException($"unknown agent '{hello.AgentId}'");
        }

        return true;
    });

    public Task<StoredAgent?> GetAsync(string agentId) => database.ReadAsync(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM agents WHERE agent_id = $agentId;";
        command.Parameters.AddWithValue("$agentId", agentId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadAgent(reader) : null;
    });

    public Task<IReadOnlyList<StoredAgent>> ListAsync() => database.ReadAsync<IReadOnlyList<StoredAgent>>(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM agents ORDER BY name COLLATE NOCASE;";
        using var reader = command.ExecuteReader();
        var result = new List<StoredAgent>();
        while (reader.Read())
        {
            result.Add(ReadAgent(reader));
        }

        return result;
    });

    public Task SetAuthorizedAsync(string agentId, bool authorized) => database.WriteAsync(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE agents SET authorized = $authorized WHERE agent_id = $agentId;";
        command.Parameters.AddWithValue("$authorized", authorized ? 1 : 0);
        command.Parameters.AddWithValue("$agentId", agentId);
        if (command.ExecuteNonQuery() == 0)
        {
            throw new InvalidOperationException($"unknown agent '{agentId}'");
        }

        return true;
    });

    public Task SetEnabledAsync(string agentId, bool enabled) => database.WriteAsync(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE agents SET enabled = $enabled WHERE agent_id = $agentId;";
        command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        command.Parameters.AddWithValue("$agentId", agentId);
        if (command.ExecuteNonQuery() == 0)
        {
            throw new InvalidOperationException($"unknown agent '{agentId}'");
        }

        return true;
    });

    public Task RenameAsync(string agentId, string name) => database.WriteAsync(connection =>
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
        {
            throw new ArgumentException("agent name cannot be empty", nameof(name));
        }

        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE agents SET name = $name WHERE agent_id = $agentId;";
        command.Parameters.AddWithValue("$name", trimmed);
        command.Parameters.AddWithValue("$agentId", agentId);
        if (command.ExecuteNonQuery() == 0)
        {
            throw new InvalidOperationException($"unknown agent '{agentId}'");
        }

        return true;
    });

    public Task SetCustomParameterAsync(string agentId, string key, string value) =>
        database.WriteAsync(connection =>
        {
            var (normalizedKey, normalizedValue) = AgentParameterMaps.ValidateCustom(key, value);
            var reported = ReadParameterMap(connection, agentId, "parameters_json")
                ?? throw new InvalidOperationException($"unknown agent '{agentId}'");
            if (reported.ContainsKey(normalizedKey))
            {
                throw new InvalidOperationException(
                    $"custom parameter '{normalizedKey}' conflicts with a reported parameter");
            }

            var custom = ReadParameterMap(connection, agentId, "custom_parameters_json")!;
            var updated = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var parameter in custom)
            {
                updated.Add(parameter.Key, parameter.Value);
            }

            updated[normalizedKey] = normalizedValue;
            WriteCustomParameters(connection, agentId, updated);
            return true;
        });

    public Task DeleteCustomParameterAsync(string agentId, string key) =>
        database.WriteAsync(connection =>
        {
            var normalizedKey = AgentParameterMaps.ValidateCustomKey(key);
            var custom = ReadParameterMap(connection, agentId, "custom_parameters_json")
                ?? throw new InvalidOperationException($"unknown agent '{agentId}'");
            if (!custom.ContainsKey(normalizedKey))
            {
                throw new InvalidOperationException(
                    $"agent '{agentId}' has no custom parameter '{normalizedKey}'");
            }

            var updated = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var parameter in custom)
            {
                updated.Add(parameter.Key, parameter.Value);
            }

            updated.Remove(normalizedKey);
            WriteCustomParameters(connection, agentId, updated);
            return true;
        });

    public Task DeleteAsync(string agentId) => database.WriteAsync(connection =>
    {
        using var transaction = connection.BeginTransaction();
        using (var consumeEnrollment = connection.CreateCommand())
        {
            consumeEnrollment.Transaction = transaction;
            consumeEnrollment.CommandText = """
                DELETE FROM enroll_tokens
                WHERE token_hash = (
                    SELECT enroll_token_hash FROM agents WHERE agent_id = $agentId
                );
                """;
            consumeEnrollment.Parameters.AddWithValue("$agentId", agentId);
            consumeEnrollment.ExecuteNonQuery();
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM agents WHERE agent_id = $agentId;";
        command.Parameters.AddWithValue("$agentId", agentId);
        command.ExecuteNonQuery();
        transaction.Commit();
        return true;
    });

    private static StoredAgent ReadAgent(SqliteDataReader reader)
    {
        var reportedParameters = DeserializeParameters(
            reader.GetString(reader.GetOrdinal("parameters_json")));
        var customParameters = DeserializeParameters(
            reader.GetString(reader.GetOrdinal("custom_parameters_json")));
        return new StoredAgent(
            reader.GetString(reader.GetOrdinal("agent_id")),
            reader.GetString(reader.GetOrdinal("name")),
            reader.GetInt64(reader.GetOrdinal("authorized")) != 0,
            reader.GetInt64(reader.GetOrdinal("enabled")) != 0,
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(reader.GetOrdinal("first_seen_unix_ms"))),
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(reader.GetOrdinal("last_seen_unix_ms"))),
            reportedParameters,
            customParameters,
            reader.GetString(reader.GetOrdinal("agent_version")),
            reader.GetString(reader.GetOrdinal("os_family")),
            reader.GetString(reader.GetOrdinal("os_version")),
            reader.GetString(reader.GetOrdinal("architecture")),
            reader.GetInt64(reader.GetOrdinal("interactive")) != 0);
    }

    private static IReadOnlyDictionary<string, string>? ReadParameterMap(
        SqliteConnection connection,
        string agentId,
        string column)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {column} FROM agents WHERE agent_id = $agentId;";
        command.Parameters.AddWithValue("$agentId", agentId);
        var json = command.ExecuteScalar() as string;
        return json == null ? null : DeserializeParameters(json);
    }

    private static IReadOnlyDictionary<string, string> DeserializeParameters(string json)
    {
        var parameters = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];
        return AgentParameterMaps.Normalize(parameters);
    }

    private static void WriteCustomParameters(
        SqliteConnection connection,
        string agentId,
        IReadOnlyDictionary<string, string> parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE agents SET custom_parameters_json = $parameters
            WHERE agent_id = $agentId;
            """;
        command.Parameters.AddWithValue("$parameters", JsonSerializer.Serialize(parameters));
        command.Parameters.AddWithValue("$agentId", agentId);
        if (command.ExecuteNonQuery() == 0)
        {
            throw new InvalidOperationException($"unknown agent '{agentId}'");
        }
    }
}
