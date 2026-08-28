using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Vivarium.Contracts.V1;
using Vivarium.Controller.Agents;
using Vivarium.Controller.Persistence;

namespace Vivarium.Controller.Security;

public sealed record AgentAdmission(AgentAuth Authorization, bool Enabled, string? AuthTokenToDeliver);

public enum BearerScope
{
    Agent,
    Submit,
    Admin,
}

/// <summary>
/// Controller and agent credentials (D4/D7). Enrollment tokens are short-lived and single-agent:
/// the first hello claims one, while reconnects from that pending agent may reuse the same proof.
/// </summary>
public sealed class TokenStore
{
    private static readonly TimeSpan DefaultEnrollLifetime = TimeSpan.FromHours(24);
    private readonly VivariumDatabase database;

    public string AdminToken { get; }
    public string SubmitToken { get; }

    public TokenStore(string dataDir, VivariumDatabase database)
    {
        this.database = database;
        AdminToken = LoadOrCreateToken(Path.Combine(dataDir, "admin.token"));
        SubmitToken = LoadOrCreateToken(Path.Combine(dataDir, "submit.token"));
    }

    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24));

    public async Task<string> CreateEnrollTokenAsync(TimeSpan? lifetime = null)
    {
        var token = NewToken();
        var hash = Hash(token);
        var expires = DateTimeOffset.UtcNow.Add(lifetime ?? DefaultEnrollLifetime).ToUnixTimeMilliseconds();
        await database.WriteAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO enroll_tokens(token_hash, expires_unix_ms, claimed_agent_id)
                VALUES ($hash, $expires, NULL);
                """;
            command.Parameters.AddWithValue("$hash", hash);
            command.Parameters.AddWithValue("$expires", expires);
            command.ExecuteNonQuery();
            return true;
        });
        return token;
    }

    public Task<AgentAdmission?> AdmitAgentAsync(Hello hello) => database.WriteAsync(connection =>
    {
        using var transaction = connection.BeginTransaction();
        var current = ReadIdentity(connection, transaction, hello.AgentId);

        if (current != null && hello.AuthToken.Length > 0 &&
            FixedEquals(current.AuthTokenHash, Hash(hello.AuthToken)))
        {
            if (current.PendingAuthToken != null)
            {
                ClearPendingToken(connection, transaction, hello.AgentId);
            }

            transaction.Commit();
            return new AgentAdmission(current.Authorized ? AgentAuth.Authorized : AgentAuth.Unauthorized,
                current.Enabled, null);
        }

        var enrollHash = hello.EnrollToken.Length > 0 ? Hash(hello.EnrollToken) : string.Empty;
        if (current != null && enrollHash.Length > 0 && FixedEquals(current.EnrollTokenHash, enrollHash))
        {
            transaction.Commit();
            return new AgentAdmission(current.Authorized ? AgentAuth.Authorized : AgentAuth.Unauthorized,
                current.Enabled, current.Authorized ? current.PendingAuthToken : null);
        }

        if (enrollHash.Length == 0 || !TryClaimEnrollToken(connection, transaction, enrollHash, hello.AgentId))
        {
            transaction.Rollback();
            return null;
        }

        if (current == null)
        {
            InsertAgent(connection, transaction, hello, enrollHash);
        }
        else
        {
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE agents SET enroll_token_hash = $enrollHash, authorized = 0
                WHERE agent_id = $agentId;
                """;
            update.Parameters.AddWithValue("$enrollHash", enrollHash);
            update.Parameters.AddWithValue("$agentId", hello.AgentId);
            update.ExecuteNonQuery();
        }

        transaction.Commit();
        return new AgentAdmission(AgentAuth.Unauthorized, current?.Enabled ?? true, null);
    });

    public Task<string?> AuthorizeAgentAsync(string agentId)
    {
        return database.WriteAsync<string?>(connection =>
        {
            using var transaction = connection.BeginTransaction();
            using var read = connection.CreateCommand();
            read.Transaction = transaction;
            read.CommandText = "SELECT auth_token_hash FROM agents WHERE agent_id = $agentId;";
            read.Parameters.AddWithValue("$agentId", agentId);
            var existingHash = read.ExecuteScalar();
            if (existingHash == null)
            {
                throw new InvalidOperationException($"unknown agent '{agentId}'");
            }

            if (existingHash != DBNull.Value)
            {
                using var reauthorize = connection.CreateCommand();
                reauthorize.Transaction = transaction;
                reauthorize.CommandText = "UPDATE agents SET authorized = 1 WHERE agent_id = $agentId;";
                reauthorize.Parameters.AddWithValue("$agentId", agentId);
                reauthorize.ExecuteNonQuery();
                transaction.Commit();
                return null;
            }

            var token = NewToken();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE agents SET
                    authorized = 1,
                    auth_token_hash = $tokenHash,
                    pending_auth_token = $token
                WHERE agent_id = $agentId;
                """;
            command.Parameters.AddWithValue("$tokenHash", Hash(token));
            command.Parameters.AddWithValue("$token", token);
            command.Parameters.AddWithValue("$agentId", agentId);
            if (command.ExecuteNonQuery() == 0)
            {
                throw new InvalidOperationException($"unknown agent '{agentId}'");
            }

            transaction.Commit();
            return token;
        });
    }

    public async Task<BearerScope?> ResolveBearerScopeAsync(string token)
    {
        if (FixedEquals(AdminToken, token))
        {
            return BearerScope.Admin;
        }

        if (FixedEquals(SubmitToken, token))
        {
            return BearerScope.Submit;
        }

        var hash = Hash(token);
        var agent = await database.ReadAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT EXISTS(
                    SELECT 1 FROM agents
                    WHERE auth_token_hash = $hash
                );
                """;
            command.Parameters.AddWithValue("$hash", hash);
            return Convert.ToInt32(command.ExecuteScalar()) != 0;
        });
        return agent ? BearerScope.Agent : null;
    }

    public async Task<bool> IsValidBearerAsync(string token) =>
        await ResolveBearerScopeAsync(token) != null;

    private static AgentIdentity? ReadIdentity(SqliteConnection connection, SqliteTransaction transaction, string agentId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT authorized, enabled, auth_token_hash, pending_auth_token, enroll_token_hash
            FROM agents WHERE agent_id = $agentId;
            """;
        command.Parameters.AddWithValue("$agentId", agentId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new AgentIdentity(
            reader.GetInt64(0) != 0,
            reader.GetInt64(1) != 0,
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4));
    }

    private static bool TryClaimEnrollToken(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tokenHash,
        string agentId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE enroll_tokens SET claimed_agent_id = $agentId
            WHERE token_hash = $hash
              AND expires_unix_ms >= $now
              AND (claimed_agent_id IS NULL OR claimed_agent_id = $agentId);
            """;
        command.Parameters.AddWithValue("$agentId", agentId);
        command.Parameters.AddWithValue("$hash", tokenHash);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        return command.ExecuteNonQuery() == 1;
    }

    private static void InsertAgent(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Hello hello,
        string enrollHash)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var requestedName = hello.Parameters.TryGetValue("hostname", out var hostname) && hostname.Length > 0
            ? hostname
            : $"agent-{hello.AgentId[..Math.Min(8, hello.AgentId.Length)]}";
        var name = UniqueName(connection, transaction, requestedName, hello.AgentId);

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO agents(
                agent_id, name, authorized, enabled, enroll_token_hash,
                first_seen_unix_ms, last_seen_unix_ms)
            VALUES ($agentId, $name, 0, 1, $enrollHash, $now, $now);
            """;
        command.Parameters.AddWithValue("$agentId", hello.AgentId);
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$enrollHash", enrollHash);
        command.Parameters.AddWithValue("$now", now);
        command.ExecuteNonQuery();
    }

    private static string UniqueName(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string requested,
        string agentId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM agents WHERE name = $name COLLATE NOCASE);";
        command.Parameters.AddWithValue("$name", requested);
        return Convert.ToInt32(command.ExecuteScalar()) == 0
            ? requested
            : $"{requested}-{agentId[..Math.Min(6, agentId.Length)]}";
    }

    private static void ClearPendingToken(SqliteConnection connection, SqliteTransaction transaction, string agentId)
    {
        using (var consume = connection.CreateCommand())
        {
            consume.Transaction = transaction;
            consume.CommandText = """
                DELETE FROM enroll_tokens
                WHERE token_hash = (
                    SELECT enroll_token_hash FROM agents WHERE agent_id = $agentId
                );
                """;
            consume.Parameters.AddWithValue("$agentId", agentId);
            consume.ExecuteNonQuery();
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE agents SET pending_auth_token = NULL, enroll_token_hash = NULL
            WHERE agent_id = $agentId;
            """;
        command.Parameters.AddWithValue("$agentId", agentId);
        command.ExecuteNonQuery();
    }

    private static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string LoadOrCreateToken(string path)
    {
        if (File.Exists(path))
        {
            PrivateStorage.RestrictSecretFile(path);
            return ValidatePersistedToken(path, File.ReadAllText(path).Trim());
        }

        var token = NewToken();
        PrivateStorage.WriteSecretText(path, token);
        return token;
    }

    private static string ValidatePersistedToken(string path, string token)
    {
        if (token.Length != 48 || !token.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException(
                $"controller token file '{path}' must contain exactly 48 hexadecimal characters");
        }

        return token;
    }

    private static bool FixedEquals(string? left, string? right)
    {
        if (left == null || right == null)
        {
            return false;
        }

        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length &&
            CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private sealed record AgentIdentity(
        bool Authorized,
        bool Enabled,
        string? AuthTokenHash,
        string? PendingAuthToken,
        string? EnrollTokenHash);
}
