using System.Text.Json;
using System.Text;
using Microsoft.Data.Sqlite;
using Vivarium.Controller.Persistence;
using Vivarium.Controller.Security;

namespace Vivarium.Controller.Auditing;

public enum AuditOutcome
{
    Succeeded,
    Denied,
    Failed,
    NoChange,
}

public sealed record AuditEventDraft(
    string AuditEventId,
    DateTimeOffset ReceivedAt,
    ManagementRequestContext RequestContext,
    string Action,
    string TargetType,
    string TargetId,
    AuditOutcome Outcome,
    string ReasonCode,
    IReadOnlyDictionary<string, string> Details,
    string? BaseRevision = null,
    string? ResultRevision = null)
{
    public static AuditEventDraft Create(
        ManagementRequestContext context,
        DateTimeOffset receivedAt,
        string action,
        string targetType,
        string targetId,
        AuditOutcome outcome = AuditOutcome.Succeeded,
        string reasonCode = "",
        IReadOnlyDictionary<string, string>? details = null) =>
        new(
            ManagementIdentifiers.NewId(),
            receivedAt,
            context,
            action,
            targetType,
            targetId,
            outcome,
            reasonCode,
            details ?? new Dictionary<string, string>());
}

public sealed record StoredAuditEvent(
    string AuditEventId,
    DateTimeOffset ReceivedAt,
    string ActorType,
    string ActorId,
    string CredentialKind,
    string CorrelationId,
    string? RequestId,
    string? Source,
    string Action,
    string TargetType,
    string TargetId,
    AuditOutcome Outcome,
    string ReasonCode,
    IReadOnlyDictionary<string, string> Details,
    string? BaseRevision,
    string? ResultRevision);

public sealed record AuditEventQuery(
    IReadOnlyList<string>? ActorIds = null,
    IReadOnlyList<string>? ActorTypes = null,
    IReadOnlyList<string>? Actions = null,
    IReadOnlyList<string>? TargetTypes = null,
    IReadOnlyList<string>? TargetIds = null,
    IReadOnlyList<AuditOutcome>? Outcomes = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null);

public sealed record AuditEventCursor(long ReceivedUnixMilliseconds, string AuditEventId);

public sealed record AuditEventCandidate(StoredAuditEvent AuditEvent, AuditEventCursor Cursor);

public sealed record AuditEventPage(
    IReadOnlyList<AuditEventCandidate> Items,
    AuditEventCursor? NextCursor);

public sealed class AuditEventStore(VivariumDatabase database)
{
    // Source is stored in the existing extensible JSON field so the checksummed audit schema remains
    // unchanged. Rows written before this metadata existed continue to project Source as null.
    private const string SourceMetadataKey = "_request_source";
    private static readonly string[] SensitiveKeyFragments =
        ["authorization", "cookie", "credential", "password", "secret", "token"];

    public Task AppendAsync(AuditEventDraft auditEvent) => database.WriteAsync(connection =>
    {
        using var transaction = connection.BeginTransaction();
        Append(connection, transaction, auditEvent);
        transaction.Commit();
        return true;
    });

    public Task<IReadOnlyList<StoredAuditEvent>> ListAsync(int limit = 100) =>
        database.ReadAsync<IReadOnlyList<StoredAuditEvent>>(connection =>
        {
            if (limit is < 1 or > 500)
            {
                throw new ArgumentOutOfRangeException(nameof(limit), "audit limit must be between 1 and 500");
            }

            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    audit_event_id, received_unix_ms, actor_type, actor_id, credential_kind,
                    correlation_id, request_id, action, target_type, target_id, outcome,
                    reason_code, details_json, base_revision, result_revision
                FROM audit_events
                ORDER BY received_unix_ms DESC, audit_event_id DESC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$limit", limit);
            using var reader = command.ExecuteReader();
            var result = new List<StoredAuditEvent>();
            while (reader.Read())
            {
                var (source, details) = DeserializeStoredDetails(reader.GetString(12));
                result.Add(new StoredAuditEvent(
                    reader.GetString(0),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    source,
                    reader.GetString(7),
                    reader.GetString(8),
                    reader.GetString(9),
                    ParseOutcome(reader.GetString(10)),
                    reader.GetString(11),
                    details,
                    reader.IsDBNull(13) ? null : reader.GetString(13),
                    reader.IsDBNull(14) ? null : reader.GetString(14)));
            }

            return result;
        });

    public Task<AuditEventPage> QueryPageAsync(
        AuditEventQuery query,
        AuditEventCursor? after,
        int limit) => database.ReadAsync(connection =>
    {
        ArgumentNullException.ThrowIfNull(query);
        if (limit is < 1 or > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "audit query limit must be between 1 and 200");
        }

        ValidateFilter(query.ActorIds, "actor IDs");
        ValidateFilter(query.ActorTypes, "actor types");
        ValidateFilter(query.Actions, "actions");
        ValidateFilter(query.TargetTypes, "target types");
        ValidateFilter(query.TargetIds, "target IDs");
        if (query.Outcomes is { Count: > 50 })
        {
            throw new ArgumentException("outcomes must contain at most 50 values", nameof(query));
        }

        if (query.From is { } from && query.To is { } to && from > to)
        {
            throw new ArgumentException("audit query 'from' must not be later than 'to'", nameof(query));
        }

        using var command = connection.CreateCommand();
        var sql = new StringBuilder("""
            SELECT
                audit_event_id, received_unix_ms, actor_type, actor_id, credential_kind,
                correlation_id, request_id, action, target_type, target_id, outcome,
                reason_code, details_json, base_revision, result_revision
            FROM audit_events
            WHERE 1 = 1
            """);
        AddTextFilter(sql, command, "actor_id", "actorId", query.ActorIds);
        AddTextFilter(sql, command, "actor_type", "actorType", query.ActorTypes);
        AddTextFilter(sql, command, "action", "action", query.Actions);
        AddTextFilter(sql, command, "target_type", "targetType", query.TargetTypes);
        AddTextFilter(sql, command, "target_id", "targetId", query.TargetIds);
        if (query.Outcomes is { Count: > 0 } outcomes)
        {
            sql.Append(" AND outcome IN (");
            for (var index = 0; index < outcomes.Count; index++)
            {
                if (index > 0)
                {
                    sql.Append(", ");
                }

                var parameterName = $"$outcome{index}";
                sql.Append(parameterName);
                command.Parameters.AddWithValue(parameterName, SerializeOutcome(outcomes[index]));
            }

            sql.Append(')');
        }

        if (query.From is { } fromTime)
        {
            sql.Append(" AND received_unix_ms >= $from");
            command.Parameters.AddWithValue("$from", fromTime.ToUnixTimeMilliseconds());
        }

        if (query.To is { } toTime)
        {
            sql.Append(" AND received_unix_ms <= $to");
            command.Parameters.AddWithValue("$to", toTime.ToUnixTimeMilliseconds());
        }

        if (after is not null)
        {
            if (string.IsNullOrWhiteSpace(after.AuditEventId))
            {
                throw new ArgumentException("audit cursor event ID is required", nameof(after));
            }

            sql.Append("""

                 AND (
                    received_unix_ms < $afterReceived
                    OR (received_unix_ms = $afterReceived AND audit_event_id < $afterEventId COLLATE BINARY)
                 )
                """);
            command.Parameters.AddWithValue("$afterReceived", after.ReceivedUnixMilliseconds);
            command.Parameters.AddWithValue("$afterEventId", after.AuditEventId);
        }

        sql.Append(" ORDER BY received_unix_ms DESC, audit_event_id COLLATE BINARY DESC LIMIT $limit;");
        command.Parameters.AddWithValue("$limit", limit + 1);
        command.CommandText = sql.ToString();
        using var reader = command.ExecuteReader();
        var candidates = new List<AuditEventCandidate>(limit + 1);
        while (reader.Read())
        {
            var auditEvent = ReadAuditEvent(reader);
            candidates.Add(new AuditEventCandidate(
                auditEvent,
                new AuditEventCursor(auditEvent.ReceivedAt.ToUnixTimeMilliseconds(), auditEvent.AuditEventId)));
        }

        AuditEventCursor? nextCursor = null;
        if (candidates.Count > limit)
        {
            candidates.RemoveAt(candidates.Count - 1);
            nextCursor = candidates[^1].Cursor;
        }

        return new AuditEventPage(candidates, nextCursor);
    });

    internal static void Append(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AuditEventDraft auditEvent)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        Validate(auditEvent);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO audit_events(
                audit_event_id, received_unix_ms, actor_type, actor_id, credential_kind,
                correlation_id, request_id, action, target_type, target_id, outcome,
                reason_code, details_json, base_revision, result_revision)
            VALUES (
                $eventId, $receivedAt, $actorType, $actorId, $credentialKind,
                $correlationId, $requestId, $action, $targetType, $targetId, $outcome,
                $reasonCode, $details, $baseRevision, $resultRevision);
            """;
        command.Parameters.AddWithValue("$eventId", auditEvent.AuditEventId);
        command.Parameters.AddWithValue("$receivedAt", auditEvent.ReceivedAt.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$actorType", auditEvent.RequestContext.Principal.ActorType);
        command.Parameters.AddWithValue("$actorId", auditEvent.RequestContext.Principal.ActorId);
        command.Parameters.AddWithValue("$credentialKind", auditEvent.RequestContext.Principal.CredentialKind);
        command.Parameters.AddWithValue("$correlationId", auditEvent.RequestContext.CorrelationId);
        command.Parameters.AddWithValue("$requestId", (object?)auditEvent.RequestContext.RequestId ?? DBNull.Value);
        command.Parameters.AddWithValue("$action", auditEvent.Action);
        command.Parameters.AddWithValue("$targetType", auditEvent.TargetType);
        command.Parameters.AddWithValue("$targetId", auditEvent.TargetId);
        command.Parameters.AddWithValue("$outcome", SerializeOutcome(auditEvent.Outcome));
        command.Parameters.AddWithValue("$reasonCode", auditEvent.ReasonCode);
        command.Parameters.AddWithValue(
            "$details",
            SerializeStoredDetails(auditEvent.Details, auditEvent.RequestContext.Source));
        command.Parameters.AddWithValue("$baseRevision", (object?)auditEvent.BaseRevision ?? DBNull.Value);
        command.Parameters.AddWithValue("$resultRevision", (object?)auditEvent.ResultRevision ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static void Validate(AuditEventDraft auditEvent)
    {
        RequireBounded(auditEvent.AuditEventId, 64, "audit event ID");
        RequireBounded(auditEvent.RequestContext.Principal.ActorType, 32, "actor type");
        RequireBounded(auditEvent.RequestContext.Principal.ActorId, 256, "actor ID");
        RequireBounded(auditEvent.RequestContext.Principal.CredentialKind, 64, "credential kind");
        RequireBounded(auditEvent.RequestContext.CorrelationId, 128, "correlation ID");
        RequireBounded(auditEvent.RequestContext.Source, 128, "request source");
        RequireBounded(auditEvent.Action, 128, "action");
        RequireBounded(auditEvent.TargetType, 64, "target type");
        RequireBounded(auditEvent.TargetId, 256, "target ID");
        RequireBounded(auditEvent.ReasonCode, 128, "reason code", allowEmpty: true);
        if (auditEvent.RequestContext.RequestId is { } requestId)
        {
            RequireBounded(requestId, 256, "request ID");
        }

        if (auditEvent.BaseRevision is { } baseRevision)
        {
            RequireBounded(baseRevision, 256, "base revision");
        }

        if (auditEvent.ResultRevision is { } resultRevision)
        {
            RequireBounded(resultRevision, 256, "result revision");
        }

        foreach (var (key, value) in auditEvent.Details)
        {
            if (string.Equals(key, SourceMetadataKey, StringComparison.Ordinal))
            {
                throw new ArgumentException($"audit detail key '{SourceMetadataKey}' is reserved");
            }

            RequireBounded(key, 64, "audit detail key");
            RequireBounded(value, 256, $"audit detail '{key}'", allowEmpty: true);
            if (SensitiveKeyFragments.Any(fragment => key.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArgumentException($"sensitive audit detail key '{key}' is forbidden");
            }
        }

        if (SerializeStoredDetails(auditEvent.Details, auditEvent.RequestContext.Source).Length > 2048)
        {
            throw new ArgumentException("audit details exceed the 2048-character bound");
        }
    }

    private static void RequireBounded(string value, int maximum, string name, bool allowEmpty = false)
    {
        if ((!allowEmpty && string.IsNullOrWhiteSpace(value)) || value.Length > maximum)
        {
            throw new ArgumentException($"{name} must be {(allowEmpty ? "at most" : "between 1 and")} {maximum} characters");
        }
    }

    private static string SerializeStoredDetails(
        IReadOnlyDictionary<string, string> details,
        string source)
    {
        var stored = details
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        stored.Add(SourceMetadataKey, source);
        return JsonSerializer.Serialize(stored);
    }

    private static (string? Source, IReadOnlyDictionary<string, string> Details) DeserializeStoredDetails(
        string json)
    {
        var details = JsonSerializer.Deserialize<Dictionary<string, string>>(json)
            ?? new Dictionary<string, string>();
        details.Remove(SourceMetadataKey, out var source);
        return (source, details);
    }

    private static StoredAuditEvent ReadAuditEvent(SqliteDataReader reader)
    {
        var (source, details) = DeserializeStoredDetails(reader.GetString(12));
        return new StoredAuditEvent(
            reader.GetString(0),
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            source,
            reader.GetString(7),
            reader.GetString(8),
            reader.GetString(9),
            ParseOutcome(reader.GetString(10)),
            reader.GetString(11),
            details,
            reader.IsDBNull(13) ? null : reader.GetString(13),
            reader.IsDBNull(14) ? null : reader.GetString(14));
    }

    private static void AddTextFilter(
        StringBuilder sql,
        SqliteCommand command,
        string column,
        string parameterPrefix,
        IReadOnlyList<string>? values)
    {
        if (values is null or { Count: 0 })
        {
            return;
        }

        sql.Append(" AND ").Append(column).Append(" COLLATE BINARY IN (");
        for (var index = 0; index < values.Count; index++)
        {
            if (index > 0)
            {
                sql.Append(", ");
            }

            var parameterName = $"${parameterPrefix}{index}";
            sql.Append(parameterName);
            command.Parameters.AddWithValue(parameterName, values[index]);
        }

        sql.Append(')');
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

    private static string SerializeOutcome(AuditOutcome outcome) => outcome switch
    {
        AuditOutcome.Succeeded => "SUCCEEDED",
        AuditOutcome.Denied => "DENIED",
        AuditOutcome.Failed => "FAILED",
        AuditOutcome.NoChange => "NO_CHANGE",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "unknown audit outcome"),
    };

    private static AuditOutcome ParseOutcome(string value) => value switch
    {
        "SUCCEEDED" => AuditOutcome.Succeeded,
        "DENIED" => AuditOutcome.Denied,
        "FAILED" => AuditOutcome.Failed,
        "NO_CHANGE" => AuditOutcome.NoChange,
        _ => throw new InvalidDataException($"unknown audit outcome '{value}'"),
    };
}
