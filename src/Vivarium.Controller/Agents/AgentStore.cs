using System.Text.Json;
using System.Text;
using Microsoft.Data.Sqlite;
using Vivarium.Contracts.V1;
using Vivarium.Controller.Auditing;
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
    bool Interactive,
    long CredentialGeneration,
    long ConnectionGeneration)
{
    public IReadOnlyDictionary<string, string> Parameters { get; } =
        AgentParameterMaps.Merge(ReportedParameters, CustomParameters);
}

public enum AgentFactCollectorOutcome
{
    Succeeded,
    Partial,
    Degraded,
    PermissionDenied,
    TemporarilyUnavailable,
    Failed,
}

public sealed record AgentObservationIssue(
    string Code,
    string Field,
    string? NativeCode = null,
    string? Message = null);

public sealed record AgentCapabilitySupport(string CapabilityId, int ContractMajor);

public sealed record AgentStaticFacts(
    string Hostname,
    string OsFamily,
    string ProductName,
    string ProductVersion,
    string OsBuild,
    string KernelVersion,
    string OsArchitecture,
    string ProcessArchitecture,
    string AgentVersion,
    string PackageVersion,
    string CollectorVersion,
    bool Interactive,
    IReadOnlyDictionary<string, string> Extensions);

public sealed record AgentStaticObservation(
    string AgentId,
    DateTimeOffset? ObservedAt,
    DateTimeOffset ReceivedAt,
    AgentFactCollectorOutcome CollectorOutcome,
    bool Complete,
    IReadOnlyList<AgentObservationIssue> Issues,
    IReadOnlyList<AgentCapabilitySupport> Capabilities,
    AgentStaticFacts Facts,
    long CredentialGeneration,
    long ConnectionGeneration,
    string? PackageDigestSha256);

public sealed record StoredAgentObservation(
    long Revision,
    DateTimeOffset? ObservedAt,
    DateTimeOffset ReceivedAt,
    string Quality,
    AgentFactCollectorOutcome CollectorOutcome,
    bool Complete,
    IReadOnlyList<AgentObservationIssue> Issues,
    IReadOnlyList<AgentCapabilitySupport> Capabilities,
    AgentStaticFacts Facts,
    long CredentialGeneration,
    long ConnectionGeneration,
    string? PackageDigestSha256);

public sealed record AgentGenerationState(long CredentialGeneration, long ConnectionGeneration);

public sealed record StoredAgentProjection(
    StoredAgent Agent,
    StoredAgentObservation? Observation,
    IReadOnlyList<AgentCapabilitySupport> Capabilities);

public enum AgentStoreSort
{
    NameAscending,
    NameDescending,
    AgentIdAscending,
    AgentIdDescending,
}

public sealed record AgentStoreQuery(
    string? Search = null,
    IReadOnlyList<string>? AgentIds = null,
    bool? Authorized = null,
    bool? Enabled = null,
    IReadOnlyList<string>? OsFamilies = null,
    IReadOnlyList<string>? OsVersions = null,
    IReadOnlyList<string>? OsBuilds = null,
    IReadOnlyList<string>? Architectures = null,
    IReadOnlyList<string>? AgentVersions = null,
    IReadOnlyList<string>? Hostnames = null,
    IReadOnlyList<string>? Capabilities = null,
    IReadOnlyList<string>? PackageDigests = null,
    AgentStoreSort Sort = AgentStoreSort.NameAscending);

public sealed record AgentStoreCursor(
    string SortValue,
    string? SecondarySortValue,
    string AgentId);

public sealed record AgentStoreCandidate(StoredAgentProjection Projection, AgentStoreCursor Cursor)
{
    public StoredAgent Agent => Projection.Agent;
}

public sealed record AgentStorePage(
    IReadOnlyList<AgentStoreCandidate> Items,
    AgentStoreCursor? NextCursor);

/// <summary>Persistent TeamCity-style agent decisions, reported facts, and operator parameters.</summary>
public sealed class AgentStore
{
    private readonly VivariumDatabase database;

    public AgentStore(VivariumDatabase database) => this.database = database;

    public Task ObserveHelloAsync(Hello hello) => database.WriteAsync(connection =>
    {
        var custom = ReadParameterMap(connection, null, hello.AgentId, "custom_parameters_json")
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

    /// <summary>
    /// Atomically accepts one authenticated connection. The caller must hold the Agent lifecycle
    /// lock and pass the credential generation established during admission.
    /// </summary>
    public Task<AgentGenerationState> AcceptSessionAsync(
        string agentId,
        long credentialGeneration)
    {
        ValidateIdentity(agentId, nameof(agentId));
        if (credentialGeneration < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(credentialGeneration),
                "credential generation cannot be negative");
        }

        return database.WriteAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            using var increment = connection.CreateCommand();
            increment.Transaction = transaction;
            increment.CommandText = """
                UPDATE agents
                SET connection_generation = connection_generation + 1
                WHERE agent_id = $agentId
                  AND credential_generation = $credentialGeneration
                  AND connection_generation < 9223372036854775807
                RETURNING credential_generation, connection_generation;
                """;
            increment.Parameters.AddWithValue("$agentId", agentId);
            increment.Parameters.AddWithValue("$credentialGeneration", credentialGeneration);
            using var reader = increment.ExecuteReader();
            if (reader.Read())
            {
                var accepted = new AgentGenerationState(reader.GetInt64(0), reader.GetInt64(1));
                reader.Close();
                transaction.Commit();
                return accepted;
            }

            reader.Close();
            var current = ReadGenerationState(connection, transaction, agentId)
                ?? throw new InvalidOperationException($"unknown agent '{agentId}'");
            transaction.Rollback();
            if (current.CredentialGeneration != credentialGeneration)
            {
                throw new InvalidOperationException(
                    $"agent '{agentId}' authenticated with stale credential generation " +
                    $"{credentialGeneration}; current generation is {current.CredentialGeneration}");
            }

            throw new InvalidOperationException(
                $"agent '{agentId}' connection generation is exhausted");
        });
    }

    public Task<AgentGenerationState?> GetGenerationStateAsync(string agentId)
    {
        ValidateIdentity(agentId, nameof(agentId));
        return database.ReadAsync(connection => ReadGenerationState(connection, transaction: null, agentId));
    }

    public Task ObserveCapabilitiesAsync(
        string agentId,
        long credentialGeneration,
        long connectionGeneration,
        IReadOnlyList<AgentCapabilitySupport> capabilities)
    {
        ValidateIdentity(agentId, nameof(agentId));
        if (credentialGeneration < 0 || connectionGeneration < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(credentialGeneration),
                "Agent generations cannot be negative.");
        }

        var normalized = NormalizeCapabilities(capabilities, nameof(capabilities));
        return database.WriteAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            EnsureCurrentGeneration(
                connection,
                transaction,
                agentId,
                credentialGeneration,
                connectionGeneration,
                "capability observation");
            ReplaceCapabilities(connection, transaction, agentId, normalized);
            transaction.Commit();
            return true;
        });
    }

    public Task<StoredAgentObservation> ObserveStaticFactsAsync(AgentStaticObservation observation)
    {
        var normalized = NormalizeObservation(observation);
        return database.WriteAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            EnsureCurrentGeneration(
                connection,
                transaction,
                normalized.AgentId,
                normalized.CredentialGeneration,
                normalized.ConnectionGeneration,
                "fact observation");

            var previousRevision = ReadObservationRevision(connection, transaction, normalized.AgentId);
            if (previousRevision == long.MaxValue)
            {
                throw new InvalidOperationException(
                    $"agent '{normalized.AgentId}' observation revision is exhausted");
            }

            var revision = previousRevision + 1;
            using (var upsert = connection.CreateCommand())
            {
                upsert.Transaction = transaction;
                upsert.CommandText = """
                    INSERT INTO agent_fact_observations(
                        agent_id, observation_revision, observed_unix_ms, received_unix_ms,
                        quality, collector_outcome, complete, issues_json,
                        credential_generation, connection_generation, package_digest_sha256,
                        hostname, os_family, product_name, product_version, os_build,
                        kernel_version, os_architecture, process_architecture, agent_version,
                        package_version, collector_version, interactive, extension_facts_json)
                    VALUES (
                        $agentId, $revision, $observedAt, $receivedAt,
                        $quality, $outcome, $complete, $issues,
                        $credentialGeneration, $connectionGeneration, $packageDigest,
                        $hostname, $osFamily, $productName, $productVersion, $osBuild,
                        $kernelVersion, $osArchitecture, $processArchitecture, $agentVersion,
                        $packageVersion, $collectorVersion, $interactive, $extensions)
                    ON CONFLICT(agent_id) DO UPDATE SET
                        observation_revision = excluded.observation_revision,
                        observed_unix_ms = excluded.observed_unix_ms,
                        received_unix_ms = excluded.received_unix_ms,
                        quality = excluded.quality,
                        collector_outcome = excluded.collector_outcome,
                        complete = excluded.complete,
                        issues_json = excluded.issues_json,
                        credential_generation = excluded.credential_generation,
                        connection_generation = excluded.connection_generation,
                        package_digest_sha256 = excluded.package_digest_sha256,
                        hostname = excluded.hostname,
                        os_family = excluded.os_family,
                        product_name = excluded.product_name,
                        product_version = excluded.product_version,
                        os_build = excluded.os_build,
                        kernel_version = excluded.kernel_version,
                        os_architecture = excluded.os_architecture,
                        process_architecture = excluded.process_architecture,
                        agent_version = excluded.agent_version,
                        package_version = excluded.package_version,
                        collector_version = excluded.collector_version,
                        interactive = excluded.interactive,
                        extension_facts_json = excluded.extension_facts_json;
                    """;
                upsert.Parameters.AddWithValue("$agentId", normalized.AgentId);
                upsert.Parameters.AddWithValue("$revision", revision);
                upsert.Parameters.AddWithValue(
                    "$observedAt",
                    normalized.ObservedAt is { } observedAt
                        ? observedAt.ToUnixTimeMilliseconds()
                        : DBNull.Value);
                upsert.Parameters.AddWithValue("$receivedAt", normalized.ReceivedAt.ToUnixTimeMilliseconds());
                upsert.Parameters.AddWithValue("$quality", QualityFor(normalized));
                upsert.Parameters.AddWithValue("$outcome", OutcomeValue(normalized.CollectorOutcome));
                upsert.Parameters.AddWithValue("$complete", normalized.Complete ? 1 : 0);
                upsert.Parameters.AddWithValue("$issues", JsonSerializer.Serialize(normalized.Issues));
                upsert.Parameters.AddWithValue("$credentialGeneration", normalized.CredentialGeneration);
                upsert.Parameters.AddWithValue("$connectionGeneration", normalized.ConnectionGeneration);
                upsert.Parameters.AddWithValue("$packageDigest", normalized.PackageDigestSha256 ?? string.Empty);
                upsert.Parameters.AddWithValue("$hostname", normalized.Facts.Hostname);
                upsert.Parameters.AddWithValue("$osFamily", normalized.Facts.OsFamily);
                upsert.Parameters.AddWithValue("$productName", normalized.Facts.ProductName);
                upsert.Parameters.AddWithValue("$productVersion", normalized.Facts.ProductVersion);
                upsert.Parameters.AddWithValue("$osBuild", normalized.Facts.OsBuild);
                upsert.Parameters.AddWithValue("$kernelVersion", normalized.Facts.KernelVersion);
                upsert.Parameters.AddWithValue("$osArchitecture", normalized.Facts.OsArchitecture);
                upsert.Parameters.AddWithValue("$processArchitecture", normalized.Facts.ProcessArchitecture);
                upsert.Parameters.AddWithValue("$agentVersion", normalized.Facts.AgentVersion);
                upsert.Parameters.AddWithValue("$packageVersion", normalized.Facts.PackageVersion);
                upsert.Parameters.AddWithValue("$collectorVersion", normalized.Facts.CollectorVersion);
                upsert.Parameters.AddWithValue("$interactive", normalized.Facts.Interactive ? 1 : 0);
                upsert.Parameters.AddWithValue("$extensions", JsonSerializer.Serialize(normalized.Facts.Extensions));
                upsert.ExecuteNonQuery();
            }

            transaction.Commit();
            return new StoredAgentObservation(
                revision,
                normalized.ObservedAt,
                normalized.ReceivedAt,
                QualityFor(normalized),
                normalized.CollectorOutcome,
                normalized.Complete,
                normalized.Issues,
                normalized.Capabilities,
                normalized.Facts,
                normalized.CredentialGeneration,
                normalized.ConnectionGeneration,
                normalized.PackageDigestSha256);
        });
    }

    public Task<StoredAgent?> GetAsync(string agentId) => database.ReadAsync(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM agents WHERE agent_id = $agentId;";
        command.Parameters.AddWithValue("$agentId", agentId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadAgent(reader) : null;
    });

    public Task<StoredAgentProjection?> GetProjectionAsync(string agentId) => database.ReadAsync(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM agents WHERE agent_id = $agentId;";
        command.Parameters.AddWithValue("$agentId", agentId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var agent = ReadAgent(reader);
        reader.Close();
        return ReadProjection(connection, agent);
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

    public Task<AgentStorePage> QueryPageAsync(
        AgentStoreQuery query,
        AgentStoreCursor? after,
        int limit) => database.ReadAsync(connection =>
    {
        ArgumentNullException.ThrowIfNull(query);
        if (limit is < 1 or > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "agent query limit must be between 1 and 200");
        }

        ValidateFilter(query.AgentIds, "agent IDs");
        ValidateFilter(query.OsFamilies, "OS families");
        ValidateFilter(query.OsVersions, "OS versions");
        ValidateFilter(query.OsBuilds, "OS builds");
        ValidateFilter(query.Architectures, "architectures");
        ValidateFilter(query.AgentVersions, "agent versions");
        ValidateFilter(query.Hostnames, "hostnames");
        ValidateFilter(query.Capabilities, "capabilities");
        ValidateFilter(query.PackageDigests, "package digests");

        using var command = connection.CreateCommand();
        var sql = new StringBuilder("""
            SELECT a.*
            FROM agents a
            LEFT JOIN agent_fact_observations o ON o.agent_id = a.agent_id
            WHERE 1 = 1
            """);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            sql.Append("""

                 AND (
                    lower(a.agent_id) LIKE $search ESCAPE '\'
                    OR lower(a.name) LIKE $search ESCAPE '\'
                    OR lower(COALESCE(NULLIF(o.hostname, ''), '')) LIKE $search ESCAPE '\'
                    OR lower(COALESCE(NULLIF(o.product_name, ''), '')) LIKE $search ESCAPE '\'
                    OR lower(COALESCE(NULLIF(o.product_version, ''), '')) LIKE $search ESCAPE '\'
                    OR lower(COALESCE(NULLIF(o.os_build, ''), '')) LIKE $search ESCAPE '\'
                    OR EXISTS (
                        SELECT 1 FROM json_each(a.parameters_json) AS reported
                        WHERE lower(reported.key) LIKE $search ESCAPE '\'
                           OR lower(CAST(reported.value AS TEXT)) LIKE $search ESCAPE '\'
                    )
                    OR EXISTS (
                        SELECT 1 FROM json_each(a.custom_parameters_json) AS custom
                        WHERE lower(custom.key) LIKE $search ESCAPE '\'
                           OR lower(CAST(custom.value AS TEXT)) LIKE $search ESCAPE '\'
                    )
                 )
                """);
            command.Parameters.AddWithValue(
                "$search",
                $"%{EscapeLike(query.Search.Trim().ToLowerInvariant())}%");
        }

        AddTextFilter(sql, command, "a.agent_id", "agentId", query.AgentIds, caseInsensitive: false);
        AddTextFilter(
            sql,
            command,
            "COALESCE(NULLIF(o.os_family, ''), a.os_family)",
            "osFamily",
            query.OsFamilies,
            caseInsensitive: true);
        AddTextFilter(
            sql,
            command,
            "COALESCE(NULLIF(o.product_version, ''), a.os_version)",
            "osVersion",
            query.OsVersions,
            caseInsensitive: true);
        AddTextFilter(sql, command, "o.os_build", "osBuild", query.OsBuilds, caseInsensitive: true);
        AddTextFilter(
            sql,
            command,
            "COALESCE(NULLIF(o.os_architecture, ''), a.architecture)",
            "architecture",
            query.Architectures,
            caseInsensitive: true);
        AddTextFilter(
            sql,
            command,
            "COALESCE(NULLIF(o.agent_version, ''), a.agent_version)",
            "agentVersion",
            query.AgentVersions,
            caseInsensitive: false);
        AddTextFilter(sql, command, "o.hostname", "hostname", query.Hostnames, caseInsensitive: true);
        AddTextFilter(
            sql,
            command,
            "o.package_digest_sha256",
            "packageDigest",
            query.PackageDigests,
            caseInsensitive: false);
        AddCapabilityFilter(sql, command, query.Capabilities);
        if (query.Authorized is { } authorized)
        {
            sql.Append(" AND a.authorized = $authorized");
            command.Parameters.AddWithValue("$authorized", authorized ? 1 : 0);
        }

        if (query.Enabled is { } enabled)
        {
            sql.Append(" AND a.enabled = $enabled");
            command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        }

        AddCursorPredicate(sql, command, query.Sort, after);
        sql.Append(' ').Append(OrderBy(query.Sort)).Append(" LIMIT $limit;");
        command.Parameters.AddWithValue("$limit", limit + 1);
        command.CommandText = sql.ToString();

        using var reader = command.ExecuteReader();
        var candidates = new List<(StoredAgent Agent, AgentStoreCursor Cursor)>(limit + 1);
        while (reader.Read())
        {
            var agent = ReadAgent(reader);
            candidates.Add((agent, CursorFor(agent, query.Sort)));
        }

        reader.Close();

        AgentStoreCursor? nextCursor = null;
        if (candidates.Count > limit)
        {
            candidates.RemoveAt(candidates.Count - 1);
            nextCursor = candidates[^1].Cursor;
        }

        return new AgentStorePage(
            candidates.Select(candidate => new AgentStoreCandidate(
                    ReadProjection(connection, candidate.Agent),
                    candidate.Cursor))
                .ToArray(),
            nextCursor);
    });

    public Task SetAuthorizedAsync(string agentId, bool authorized) =>
        SetAuthorizedAsync(agentId, authorized, auditEvent: null);

    internal Task SetAuthorizedAsync(
        string agentId,
        bool authorized,
        AuditEventDraft? auditEvent) => database.WriteAsync(connection =>
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE agents SET authorized = $authorized
            WHERE agent_id = $agentId AND authorized <> $authorized;
            """;
        command.Parameters.AddWithValue("$authorized", authorized ? 1 : 0);
        command.Parameters.AddWithValue("$agentId", agentId);
        if (command.ExecuteNonQuery() == 0)
        {
            EnsureAgentExists(connection, transaction, agentId);
            transaction.Commit();
            return true;
        }

        AppendAudit(connection, transaction, auditEvent);
        transaction.Commit();
        return true;
    });

    public Task RenameAsync(string agentId, string name) =>
        RenameAsync(agentId, name, auditEvent: null);

    internal Task RenameAsync(
        string agentId,
        string name,
        AuditEventDraft? auditEvent) => database.WriteAsync(connection =>
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
        {
            throw new ArgumentException("agent name cannot be empty", nameof(name));
        }

        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE agents SET name = $name
            WHERE agent_id = $agentId AND name <> $name COLLATE BINARY;
            """;
        command.Parameters.AddWithValue("$name", trimmed);
        command.Parameters.AddWithValue("$agentId", agentId);
        if (command.ExecuteNonQuery() == 0)
        {
            EnsureAgentExists(connection, transaction, agentId);
            transaction.Commit();
            return true;
        }

        AppendAudit(connection, transaction, auditEvent);
        transaction.Commit();
        return true;
    });

    public Task SetCustomParameterAsync(string agentId, string key, string value) =>
        SetCustomParameterAsync(agentId, key, value, auditEvent: null);

    internal Task SetCustomParameterAsync(
        string agentId,
        string key,
        string value,
        AuditEventDraft? auditEvent) =>
        database.WriteAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            var (normalizedKey, normalizedValue) = AgentParameterMaps.ValidateCustom(key, value);
            var reported = ReadParameterMap(connection, transaction, agentId, "parameters_json")
                ?? throw new InvalidOperationException($"unknown agent '{agentId}'");
            if (reported.ContainsKey(normalizedKey))
            {
                throw new InvalidOperationException(
                    $"custom parameter '{normalizedKey}' conflicts with a reported parameter");
            }

            var custom = ReadParameterMap(connection, transaction, agentId, "custom_parameters_json")!;
            if (custom.TryGetValue(normalizedKey, out var existingValue) &&
                string.Equals(existingValue, normalizedValue, StringComparison.Ordinal))
            {
                transaction.Commit();
                return true;
            }

            var updated = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var parameter in custom)
            {
                updated.Add(parameter.Key, parameter.Value);
            }

            updated[normalizedKey] = normalizedValue;
            WriteCustomParameters(connection, transaction, agentId, updated);
            AppendAudit(connection, transaction, auditEvent);
            transaction.Commit();
            return true;
        });

    public Task DeleteCustomParameterAsync(string agentId, string key) =>
        DeleteCustomParameterAsync(agentId, key, auditEvent: null);

    internal Task DeleteCustomParameterAsync(
        string agentId,
        string key,
        AuditEventDraft? auditEvent) =>
        database.WriteAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            var normalizedKey = AgentParameterMaps.ValidateCustomKey(key);
            var custom = ReadParameterMap(connection, transaction, agentId, "custom_parameters_json")
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
            WriteCustomParameters(connection, transaction, agentId, updated);
            AppendAudit(connection, transaction, auditEvent);
            transaction.Commit();
            return true;
        });

    public Task DeleteAsync(string agentId) => DeleteAsync(agentId, auditEvent: null);

    internal Task DeleteAsync(string agentId, AuditEventDraft? auditEvent) => database.WriteAsync(connection =>
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
        if (command.ExecuteNonQuery() == 0)
        {
            throw new InvalidOperationException($"unknown agent '{agentId}'");
        }

        AppendAudit(connection, transaction, auditEvent);
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
            reader.GetInt64(reader.GetOrdinal("interactive")) != 0,
            reader.GetInt64(reader.GetOrdinal("credential_generation")),
            reader.GetInt64(reader.GetOrdinal("connection_generation")));
    }

    private static AgentStoreCursor CursorFor(StoredAgent agent, AgentStoreSort sort) => sort switch
    {
        AgentStoreSort.NameAscending or AgentStoreSort.NameDescending =>
            new(agent.Name.ToLowerInvariant(), agent.Name, agent.AgentId),
        AgentStoreSort.AgentIdAscending or AgentStoreSort.AgentIdDescending =>
            new(agent.AgentId, null, agent.AgentId),
        _ => throw new ArgumentOutOfRangeException(nameof(sort), sort, "unknown agent sort"),
    };

    private static void AddCursorPredicate(
        StringBuilder sql,
        SqliteCommand command,
        AgentStoreSort sort,
        AgentStoreCursor? after)
    {
        if (after is null)
        {
            return;
        }

        if (string.IsNullOrEmpty(after.AgentId) || string.IsNullOrEmpty(after.SortValue))
        {
            throw new ArgumentException("agent cursor position is incomplete", nameof(after));
        }

        command.Parameters.AddWithValue("$afterSort", after.SortValue);
        command.Parameters.AddWithValue("$afterAgentId", after.AgentId);
        switch (sort)
        {
            case AgentStoreSort.NameAscending:
            case AgentStoreSort.NameDescending:
                if (after.SecondarySortValue is null)
                {
                    throw new ArgumentException("name cursor position is incomplete", nameof(after));
                }

                command.Parameters.AddWithValue("$afterSecondary", after.SecondarySortValue);
                var nameComparison = sort == AgentStoreSort.NameAscending ? ">" : "<";
                sql.Append($"""
                     AND (
                        lower(a.name) {nameComparison} $afterSort COLLATE BINARY
                        OR (lower(a.name) = $afterSort COLLATE BINARY AND a.name {nameComparison} $afterSecondary COLLATE BINARY)
                        OR (lower(a.name) = $afterSort COLLATE BINARY AND a.name = $afterSecondary COLLATE BINARY
                            AND a.agent_id {nameComparison} $afterAgentId COLLATE BINARY)
                     )
                    """);
                break;
            case AgentStoreSort.AgentIdAscending:
            case AgentStoreSort.AgentIdDescending:
                var idComparison = sort == AgentStoreSort.AgentIdAscending ? ">" : "<";
                sql.Append($" AND a.agent_id {idComparison} $afterAgentId COLLATE BINARY");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(sort), sort, "unknown agent sort");
        }
    }

    private static string OrderBy(AgentStoreSort sort) => sort switch
    {
        AgentStoreSort.NameAscending =>
            "ORDER BY lower(a.name) ASC, a.name COLLATE BINARY ASC, a.agent_id COLLATE BINARY ASC",
        AgentStoreSort.NameDescending =>
            "ORDER BY lower(a.name) DESC, a.name COLLATE BINARY DESC, a.agent_id COLLATE BINARY DESC",
        AgentStoreSort.AgentIdAscending => "ORDER BY a.agent_id COLLATE BINARY ASC",
        AgentStoreSort.AgentIdDescending => "ORDER BY a.agent_id COLLATE BINARY DESC",
        _ => throw new ArgumentOutOfRangeException(nameof(sort), sort, "unknown agent sort"),
    };

    private static void AddTextFilter(
        StringBuilder sql,
        SqliteCommand command,
        string column,
        string parameterPrefix,
        IReadOnlyList<string>? values,
        bool caseInsensitive)
    {
        if (values is null or { Count: 0 })
        {
            return;
        }

        sql.Append(" AND ");
        if (caseInsensitive)
        {
            sql.Append("lower(").Append(column).Append(")");
        }
        else
        {
            sql.Append(column).Append(" COLLATE BINARY");
        }

        sql.Append(" IN (");
        for (var index = 0; index < values.Count; index++)
        {
            if (index > 0)
            {
                sql.Append(", ");
            }

            var parameterName = $"${parameterPrefix}{index}";
            sql.Append(parameterName);
            command.Parameters.AddWithValue(
                parameterName,
                caseInsensitive ? values[index].ToLowerInvariant() : values[index]);
        }

        sql.Append(')');
    }

    private static void AddCapabilityFilter(
        StringBuilder sql,
        SqliteCommand command,
        IReadOnlyList<string>? values)
    {
        if (values is null or { Count: 0 })
        {
            return;
        }

        sql.Append("""
             AND EXISTS (
                SELECT 1 FROM agent_capabilities capability
                WHERE capability.agent_id = a.agent_id
                  AND capability.capability_id COLLATE BINARY IN (
            """);
        for (var index = 0; index < values.Count; index++)
        {
            if (index > 0)
            {
                sql.Append(", ");
            }

            var parameterName = $"$capability{index}";
            sql.Append(parameterName);
            command.Parameters.AddWithValue(parameterName, values[index]);
        }

        sql.Append("))");
    }

    private static void ValidateFilter(IReadOnlyList<string>? values, string name)
    {
        if (values is null)
        {
            return;
        }

        if (values.Count > 50 || values.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException($"{name} must contain 1-50 non-empty values", name);
        }
    }

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    private static AgentGenerationState? ReadGenerationState(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string agentId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT credential_generation, connection_generation
            FROM agents WHERE agent_id = $agentId;
            """;
        command.Parameters.AddWithValue("$agentId", agentId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? new AgentGenerationState(reader.GetInt64(0), reader.GetInt64(1)) : null;
    }

    private static long ReadObservationRevision(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string agentId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT observation_revision
            FROM agent_fact_observations WHERE agent_id = $agentId;
            """;
        command.Parameters.AddWithValue("$agentId", agentId);
        return command.ExecuteScalar() is long revision ? revision : 0;
    }

    private static StoredAgentObservation? ReadObservation(
        SqliteConnection connection,
        string agentId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT observation_revision, observed_unix_ms, received_unix_ms,
                   quality, collector_outcome, complete, issues_json,
                   credential_generation, connection_generation, package_digest_sha256,
                   hostname, os_family, product_name, product_version, os_build,
                   kernel_version, os_architecture, process_architecture, agent_version,
                   package_version, collector_version, interactive, extension_facts_json
            FROM agent_fact_observations
            WHERE agent_id = $agentId;
            """;
        command.Parameters.AddWithValue("$agentId", agentId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var issues = JsonSerializer.Deserialize<AgentObservationIssue[]>(reader.GetString(6)) ?? [];
        var extensions = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(22)) ?? [];
        var observation = new StoredAgentObservation(
            reader.GetInt64(0),
            reader.IsDBNull(1) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)),
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)),
            reader.GetString(3),
            ParseOutcome(reader.GetString(4)),
            reader.GetInt64(5) != 0,
            issues,
            [],
            new AgentStaticFacts(
                reader.GetString(10),
                reader.GetString(11),
                reader.GetString(12),
                reader.GetString(13),
                reader.GetString(14),
                reader.GetString(15),
                reader.GetString(16),
                reader.GetString(17),
                reader.GetString(18),
                reader.GetString(19),
                reader.GetString(20),
                reader.GetInt64(21) != 0,
                new SortedDictionary<string, string>(extensions, StringComparer.Ordinal)),
            reader.GetInt64(7),
            reader.GetInt64(8),
            EmptyToNull(reader.GetString(9)));
        return observation;
    }

    private static StoredAgentProjection ReadProjection(
        SqliteConnection connection,
        StoredAgent agent)
    {
        var observation = ReadObservation(connection, agent.AgentId);
        var capabilities = ReadCapabilities(connection, agent.AgentId);
        return new StoredAgentProjection(
            agent,
            observation is null ? null : observation with { Capabilities = capabilities },
            capabilities);
    }

    private static IReadOnlyList<AgentCapabilitySupport> ReadCapabilities(
        SqliteConnection connection,
        string agentId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT capability_id, contract_major
            FROM agent_capabilities
            WHERE agent_id = $agentId
            ORDER BY capability_id COLLATE BINARY;
            """;
        command.Parameters.AddWithValue("$agentId", agentId);
        using var reader = command.ExecuteReader();
        var supports = new List<AgentCapabilitySupport>();
        while (reader.Read())
        {
            supports.Add(new AgentCapabilitySupport(reader.GetString(0), reader.GetInt32(1)));
        }

        return supports;
    }

    private static void EnsureCurrentGeneration(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string agentId,
        long credentialGeneration,
        long connectionGeneration,
        string observationKind)
    {
        var current = ReadGenerationState(connection, transaction, agentId)
            ?? throw new InvalidOperationException($"unknown agent '{agentId}'");
        if (current.CredentialGeneration != credentialGeneration ||
            current.ConnectionGeneration != connectionGeneration)
        {
            throw new InvalidOperationException(
                $"agent '{agentId}' {observationKind} belongs to a superseded session");
        }
    }

    private static void ReplaceCapabilities(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string agentId,
        IReadOnlyList<AgentCapabilitySupport> capabilities)
    {
        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM agent_capabilities WHERE agent_id = $agentId;";
            delete.Parameters.AddWithValue("$agentId", agentId);
            delete.ExecuteNonQuery();
        }

        foreach (var capability in capabilities)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO agent_capabilities(agent_id, capability_id, contract_major)
                VALUES ($agentId, $capabilityId, $contractMajor);
                """;
            insert.Parameters.AddWithValue("$agentId", agentId);
            insert.Parameters.AddWithValue("$capabilityId", capability.CapabilityId);
            insert.Parameters.AddWithValue("$contractMajor", capability.ContractMajor);
            insert.ExecuteNonQuery();
        }
    }

    private static AgentStaticObservation NormalizeObservation(AgentStaticObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(observation.Facts);
        ValidateIdentity(observation.AgentId, nameof(observation.AgentId));
        if (observation.CredentialGeneration < 0 || observation.ConnectionGeneration < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(observation),
                "Agent generations cannot be negative.");
        }

        var issues = observation.Issues ?? throw new ArgumentException(
            "Agent observation issues are required.", nameof(observation));
        if (issues.Count > 32)
        {
            throw new ArgumentException("Agent observations may contain at most 32 issues.", nameof(observation));
        }

        var normalizedIssues = issues.Select(issue =>
        {
            ArgumentNullException.ThrowIfNull(issue);
            return new AgentObservationIssue(
                RequireBounded(issue.Code, 64, "issue code"),
                RequireBounded(issue.Field, 128, "issue field"),
                OptionalBounded(issue.NativeCode, 64, "issue native code"),
                OptionalBounded(issue.Message, 256, "issue message"));
        }).ToArray();

        var normalizedCapabilities = NormalizeCapabilities(
            observation.Capabilities,
            nameof(observation));
        var extensions = observation.Facts.Extensions ?? throw new ArgumentException(
            "Agent extension facts are required.", nameof(observation));
        if (extensions.Count > 64)
        {
            throw new ArgumentException(
                "Agent observations may contain at most 64 extension facts.", nameof(observation));
        }

        var normalizedExtensions = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var extension in extensions)
        {
            var key = RequireBounded(extension.Key, 128, "extension fact key");
            if (!normalizedExtensions.TryAdd(
                    key,
                    RequireBounded(extension.Value, 1024, $"extension fact '{key}' value", allowEmpty: true)))
            {
                throw new ArgumentException($"Duplicate extension fact '{key}'.", nameof(observation));
            }
        }

        string? digest = null;
        if (!string.IsNullOrEmpty(observation.PackageDigestSha256))
        {
            digest = observation.PackageDigestSha256;
            if (digest.Length != 64 || digest.Any(character =>
                    !(char.IsAsciiDigit(character) || character is >= 'a' and <= 'f')))
            {
                throw new ArgumentException(
                    "Agent package digest must be a lowercase 64-character SHA-256 value.",
                    nameof(observation));
            }
        }

        var facts = observation.Facts;
        return observation with
        {
            Issues = normalizedIssues,
            Capabilities = normalizedCapabilities,
            PackageDigestSha256 = digest,
            Facts = new AgentStaticFacts(
                OptionalText(facts.Hostname, 255, "hostname"),
                OptionalText(facts.OsFamily, 128, "OS family"),
                OptionalText(facts.ProductName, 256, "product name"),
                OptionalText(facts.ProductVersion, 128, "product version"),
                OptionalText(facts.OsBuild, 128, "OS build"),
                OptionalText(facts.KernelVersion, 256, "kernel version"),
                OptionalText(facts.OsArchitecture, 128, "OS architecture"),
                OptionalText(facts.ProcessArchitecture, 128, "process architecture"),
                OptionalText(facts.AgentVersion, 128, "agent version"),
                OptionalText(facts.PackageVersion, 128, "package version"),
                OptionalText(facts.CollectorVersion, 128, "collector version"),
                facts.Interactive,
                normalizedExtensions),
        };
    }

    private static IReadOnlyList<AgentCapabilitySupport> NormalizeCapabilities(
        IReadOnlyList<AgentCapabilitySupport>? capabilities,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(capabilities, parameterName);
        if (capabilities.Count > 64)
        {
            throw new ArgumentException(
                "Agent observations may contain at most 64 capabilities.", parameterName);
        }

        var normalizedCapabilities = capabilities.Select(capability =>
        {
            ArgumentNullException.ThrowIfNull(capability);
            var id = RequireBounded(capability.CapabilityId, 128, "capability ID");
            var segments = id.Split('.');
            if (capability.ContractMajor is <= 0 or > 1024 ||
                !id.EndsWith($".v{capability.ContractMajor}", StringComparison.Ordinal) ||
                segments.Length < 2 ||
                segments.Any(segment => !IsCapabilitySegment(segment)))
            {
                throw new ArgumentException(
                    $"Agent capability '{id}' has an invalid ID or contract major.",
                    parameterName);
            }

            return new AgentCapabilitySupport(id, capability.ContractMajor);
        }).OrderBy(capability => capability.CapabilityId, StringComparer.Ordinal).ToArray();
        if (normalizedCapabilities.Select(capability => capability.CapabilityId).Distinct(StringComparer.Ordinal).Count() !=
            normalizedCapabilities.Length)
        {
            throw new ArgumentException("Agent capability IDs must be unique.", parameterName);
        }

        return normalizedCapabilities;
    }

    private static bool IsCapabilitySegment(string segment) =>
        segment.Length > 0 &&
        segment[0] != '-' &&
        segment[^1] != '-' &&
        segment.All(character => char.IsAsciiLetterLower(character) ||
            char.IsAsciiDigit(character) || character == '-');

    private static string QualityFor(AgentStaticObservation observation) =>
        observation.CollectorOutcome switch
        {
            AgentFactCollectorOutcome.Succeeded when observation.Complete => "complete",
            AgentFactCollectorOutcome.Succeeded or
                AgentFactCollectorOutcome.Partial or
                AgentFactCollectorOutcome.Degraded => "partial",
            _ => "unavailable",
        };

    private static string OutcomeValue(AgentFactCollectorOutcome outcome) => outcome switch
    {
        AgentFactCollectorOutcome.Succeeded => "succeeded",
        AgentFactCollectorOutcome.Partial => "partial",
        AgentFactCollectorOutcome.Degraded => "degraded",
        AgentFactCollectorOutcome.PermissionDenied => "permission_denied",
        AgentFactCollectorOutcome.TemporarilyUnavailable => "temporarily_unavailable",
        AgentFactCollectorOutcome.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "unknown collector outcome"),
    };

    private static AgentFactCollectorOutcome ParseOutcome(string outcome) => outcome switch
    {
        "succeeded" => AgentFactCollectorOutcome.Succeeded,
        "partial" => AgentFactCollectorOutcome.Partial,
        "degraded" => AgentFactCollectorOutcome.Degraded,
        "permission_denied" => AgentFactCollectorOutcome.PermissionDenied,
        "temporarily_unavailable" => AgentFactCollectorOutcome.TemporarilyUnavailable,
        "failed" => AgentFactCollectorOutcome.Failed,
        _ => throw new InvalidDataException($"unknown persisted Agent collector outcome '{outcome}'"),
    };

    private static void ValidateIdentity(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256)
        {
            throw new ArgumentException("Agent identity must contain 1-256 characters.", parameterName);
        }
    }

    private static string RequireBounded(
        string? value,
        int maximumLength,
        string field,
        bool allowEmpty = false)
    {
        if (value is null || value.Length > maximumLength || (!allowEmpty && string.IsNullOrWhiteSpace(value)))
        {
            throw new ArgumentException(
                $"Agent {field} must contain {(allowEmpty ? "0" : "1")}-{maximumLength} characters.",
                field);
        }

        return value;
    }

    private static string OptionalText(string? value, int maximumLength, string field) =>
        RequireBounded(value ?? string.Empty, maximumLength, field, allowEmpty: true);

    private static string? OptionalBounded(string? value, int maximumLength, string field) =>
        value is null ? null : RequireBounded(value, maximumLength, field, allowEmpty: true);

    private static string? EmptyToNull(string value) => value.Length == 0 ? null : value;

    private static IReadOnlyDictionary<string, string>? ReadParameterMap(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string agentId,
        string column)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
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
        SqliteTransaction transaction,
        string agentId,
        IReadOnlyDictionary<string, string> parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
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

    private static void AppendAudit(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AuditEventDraft? auditEvent)
    {
        if (auditEvent is not null)
        {
            AuditEventStore.Append(connection, transaction, auditEvent);
        }
    }

    private static void EnsureAgentExists(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string agentId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM agents WHERE agent_id = $agentId;";
        command.Parameters.AddWithValue("$agentId", agentId);
        if (command.ExecuteScalar() is null)
        {
            throw new InvalidOperationException($"unknown agent '{agentId}'");
        }
    }
}
