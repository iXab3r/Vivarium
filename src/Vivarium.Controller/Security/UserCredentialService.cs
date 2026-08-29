using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Vivarium.Controller.Auditing;
using Vivarium.Controller.Persistence;

namespace Vivarium.Controller.Security;

public sealed class UserCredentialService(
    VivariumDatabase database,
    AuditEventStore audits,
    TimeProvider timeProvider)
{
    private static readonly StoredPassword DummyPassword = new(
        210_000,
        new byte[16],
        new byte[32]);

    public async Task<ManagementPrincipal?> AuthenticatePasswordAsync(
        string login,
        string password,
        string? suppliedCorrelationId,
        string source,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var correlationId = SafeCorrelation(suppliedCorrelationId);
        var normalizedLogin = NormalizeLogin(login);
        var candidate = await database.ReadAsync(connection => ReadCandidate(connection, normalizedLogin));
        var passwordToCheck = password ?? string.Empty;
        var passwordMatches = Verify(passwordToCheck, candidate?.Password ?? DummyPassword);
        if (candidate is null || !candidate.DesiredActive ||
            !string.Equals(candidate.CredentialState, "ACTIVE", StringComparison.Ordinal) ||
            !passwordMatches)
        {
            await audits.AppendAsync(AuditEventDraft.Create(
                ManagementRequestContext.Anonymous(source, correlationId),
                timeProvider.GetUtcNow(),
                "authentication.login-failed",
                "panel-session",
                "interactive",
                AuditOutcome.Denied,
                "invalid_credentials"));
            return null;
        }

        var now = timeProvider.GetUtcNow();
        var principal = new ManagementPrincipal(
            "user",
            candidate.UserId,
            "password-session",
            LegacyScope: null,
            candidate.CredentialGeneration);
        var context = new ManagementRequestContext(principal, correlationId, null, source);
        return await database.WriteAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            using (var touch = connection.CreateCommand())
            {
                touch.Transaction = transaction;
                touch.CommandText = """
                    UPDATE authorization_user_credentials SET
                        last_authenticated_unix_ms = $now,
                        updated_unix_ms = MAX(updated_unix_ms, $now)
                    WHERE user_id = $userId AND credential_state = 'ACTIVE'
                        AND credential_generation = $generation;
                    """;
                touch.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
                touch.Parameters.AddWithValue("$userId", candidate.UserId);
                touch.Parameters.AddWithValue("$generation", candidate.CredentialGeneration);
                if (touch.ExecuteNonQuery() != 1)
                {
                    return null;
                }
            }

            AuditEventStore.Append(
                connection,
                transaction,
                AuditEventDraft.Create(
                    context,
                    now,
                    "authentication.login-succeeded",
                    "user",
                    candidate.UserId));
            transaction.Commit();
            return principal;
        });
    }

    public Task<bool> IsGenerationCurrentAsync(string userId, long generation) =>
        database.ReadAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT 1 FROM authorization_user_credentials
                WHERE user_id = $userId AND credential_state = 'ACTIVE'
                    AND credential_generation = $generation;
                """;
            command.Parameters.AddWithValue("$userId", userId);
            command.Parameters.AddWithValue("$generation", generation);
            return command.ExecuteScalar() is not null;
        });

    private static Candidate? ReadCandidate(SqliteConnection connection, string login)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT u.user_id, u.desired_active,
                   c.credential_state, c.password_iterations,
                   c.password_salt, c.password_verifier, c.credential_generation
            FROM authorization_desired_users u
            JOIN authorization_user_credentials c ON c.user_id = u.user_id
            WHERE u.login = $login COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$login", login);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new Candidate(
                reader.GetString(0),
                reader.GetInt64(1) == 1,
                reader.GetString(2),
                new StoredPassword(
                    reader.GetInt32(3),
                    (byte[])reader[4],
                    (byte[])reader[5]),
                reader.GetInt64(6))
            : null;
    }

    private static bool Verify(string password, StoredPassword stored)
    {
        var actual = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            stored.Salt,
            stored.Iterations,
            HashAlgorithmName.SHA256,
            stored.Verifier.Length);
        return CryptographicOperations.FixedTimeEquals(actual, stored.Verifier);
    }

    private static string NormalizeLogin(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        value = value.Trim();
        return value.Length <= 128 && value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-')
            ? value
            : string.Empty;
    }

    private static string SafeCorrelation(string? supplied)
    {
        try
        {
            return ManagementIdentifiers.NormalizeCorrelationId(supplied);
        }
        catch (ArgumentException)
        {
            return ManagementIdentifiers.NewId();
        }
    }

    private sealed record StoredPassword(int Iterations, byte[] Salt, byte[] Verifier);

    private sealed record Candidate(
        string UserId,
        bool DesiredActive,
        string CredentialState,
        StoredPassword Password,
        long CredentialGeneration);
}
