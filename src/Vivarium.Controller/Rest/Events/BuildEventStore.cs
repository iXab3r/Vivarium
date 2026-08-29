using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Vivarium.Controller.Persistence;
using Vivarium.Controller.Security;

namespace Vivarium.Controller.Rest.Events;

internal sealed record StoredBuildEvent(
    string Id,
    long Sequence,
    string MatrixBuildId,
    string Type,
    DateTimeOffset OccurredAt,
    string CorrelationId,
    string RuntimeRevision,
    string ResourceUrl);

internal sealed record BuildEventPage(
    IReadOnlyList<StoredBuildEvent> Items,
    long MinimumRetainedSequence,
    long LatestSequence);

internal sealed class BuildEventCursorException(string message, bool expired)
    : Exception(message)
{
    public bool Expired { get; } = expired;
}

/// <summary>
/// Bounded durable projection of matrix-build transitions. Domain stores call the static append
/// helpers while their own SQLite transaction is still open; standalone child builds are ignored.
/// </summary>
internal sealed class BuildEventStore(VivariumDatabase database)
{
    internal const int MaximumBatchSize = 100;
    private const string EventIdPrefix = "bevt_";
    private const int ResourceHashLength = 12;

    public Task<BuildEventPage> ReadAfterAsync(
        string matrixBuildId,
        string? afterEventId,
        int limit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(matrixBuildId);
        if (limit is < 1 or > MaximumBatchSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limit), $"event batch size must be between 1 and {MaximumBatchSize}");
        }

        return database.ReadAsync(connection =>
        {
            var stream = ReadStream(connection, matrixBuildId);
            if (stream is null)
            {
                if (afterEventId is not null)
                {
                    throw new BuildEventCursorException(
                        "The event cursor does not identify this Build stream.", expired: false);
                }

                return new BuildEventPage([], 1, 0);
            }

            var afterSequence = 0L;
            if (afterEventId is not null)
            {
                afterSequence = ParseEventId(afterEventId, matrixBuildId);
                if (afterSequence < stream.Value.MinimumRetainedSequence)
                {
                    throw new BuildEventCursorException(
                        "The event cursor is older than this Build stream's retention window.",
                        expired: true);
                }

                if (afterSequence > stream.Value.LatestSequence ||
                    !CursorExists(connection, matrixBuildId, afterEventId, afterSequence))
                {
                    throw new BuildEventCursorException(
                        "The event cursor does not identify this Build stream.", expired: false);
                }
            }

            var items = new List<StoredBuildEvent>(limit);
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT event_id, sequence, matrix_build_id, event_type, occurred_unix_ms,
                    correlation_id, runtime_revision, resource_url
                FROM build_events
                WHERE matrix_build_id = $matrixBuildId
                    AND sequence > $afterSequence
                ORDER BY sequence
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$matrixBuildId", matrixBuildId);
            command.Parameters.AddWithValue("$afterSequence", afterSequence);
            command.Parameters.AddWithValue("$limit", limit);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                items.Add(new StoredBuildEvent(
                    reader.GetString(0),
                    reader.GetInt64(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)),
                    reader.GetString(5),
                    reader.GetString(6),
                    reader.GetString(7)));
            }

            return new BuildEventPage(
                items,
                stream.Value.MinimumRetainedSequence,
                stream.Value.LatestSequence);
        });
    }

    public Task<string?> GetCurrentRuntimeRevisionAsync(string matrixBuildId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(matrixBuildId);
        return database.ReadAsync(connection =>
        {
            var stream = ReadStream(connection, matrixBuildId);
            return stream is null ? null : RuntimeRevision(stream.Value.LatestSequence);
        });
    }

    internal Task PruneBeforeAsync(string matrixBuildId, long firstRetainedSequence) =>
        database.WriteAsync(connection =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(matrixBuildId);
            if (firstRetainedSequence < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(firstRetainedSequence));
            }

            using var transaction = connection.BeginTransaction();
            var stream = ReadStream(connection, transaction, matrixBuildId)
                ?? throw new ArgumentException(
                    $"matrix Build '{matrixBuildId}' has no event stream", nameof(matrixBuildId));
            var bounded = Math.Min(
                Math.Max(firstRetainedSequence, stream.MinimumRetainedSequence),
                checked(stream.LatestSequence + 1));
            using (var remove = connection.CreateCommand())
            {
                remove.Transaction = transaction;
                remove.CommandText = """
                    DELETE FROM build_events
                    WHERE matrix_build_id = $matrixBuildId AND sequence < $firstRetained;
                    """;
                remove.Parameters.AddWithValue("$matrixBuildId", matrixBuildId);
                remove.Parameters.AddWithValue("$firstRetained", bounded);
                remove.ExecuteNonQuery();
            }

            using (var watermark = connection.CreateCommand())
            {
                watermark.Transaction = transaction;
                watermark.CommandText = """
                    UPDATE build_event_streams SET
                        minimum_retained_sequence = $firstRetained,
                        updated_unix_ms = $now
                    WHERE matrix_build_id = $matrixBuildId;
                    """;
                watermark.Parameters.AddWithValue("$firstRetained", bounded);
                watermark.Parameters.AddWithValue(
                    "$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                watermark.Parameters.AddWithValue("$matrixBuildId", matrixBuildId);
                watermark.ExecuteNonQuery();
            }

            transaction.Commit();
            return true;
        });

    internal static bool AppendForChild(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string childBuildId,
        string eventType,
        DateTimeOffset occurredAt,
        ManagementRequestContext? requestContext = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentException.ThrowIfNullOrWhiteSpace(childBuildId);
        using var lookup = connection.CreateCommand();
        lookup.Transaction = transaction;
        lookup.CommandText = """
            SELECT matrix_build_id
            FROM matrix_build_cells
            WHERE build_id = $buildId;
            """;
        lookup.Parameters.AddWithValue("$buildId", childBuildId);
        var matrixBuildId = lookup.ExecuteScalar() as string;
        return matrixBuildId is not null && AppendForMatrix(
            connection,
            transaction,
            matrixBuildId,
            eventType,
            occurredAt,
            requestContext);
    }

    internal static bool AppendForMatrix(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string matrixBuildId,
        string eventType,
        DateTimeOffset occurredAt,
        ManagementRequestContext? requestContext = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        RequireBounded(matrixBuildId, 256, nameof(matrixBuildId));
        RequireBounded(eventType, 128, nameof(eventType));
        var context = requestContext ?? ManagementRequestContext.System("build-event-projection");
        RequireBounded(context.Principal.ActorType, 64, "actor type");
        RequireBounded(context.Principal.ActorId, 256, "actor ID");
        RequireBounded(context.CorrelationId, 256, "correlation ID");

        if (!MatrixBuildExists(connection, transaction, matrixBuildId))
        {
            return false;
        }

        var resourceUrl = $"/api/v1/builds/{Uri.EscapeDataString(matrixBuildId)}";
        long expectedSequence;
        using (var nextSequence = connection.CreateCommand())
        {
            nextSequence.Transaction = transaction;
            nextSequence.CommandText = """
                SELECT COALESCE(
                    (SELECT seq FROM sqlite_sequence WHERE name = 'build_events'), 0) + 1;
                """;
            expectedSequence = Convert.ToInt64(
                nextSequence.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        var eventId = CreateEventId(expectedSequence, matrixBuildId);
        var runtimeRevision = RuntimeRevision(expectedSequence);
        long sequence;
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO build_events(
                    event_id, matrix_build_id, event_type, occurred_unix_ms,
                    correlation_id, actor_type, actor_id, runtime_revision, resource_url)
                VALUES (
                    $eventId, $matrixBuildId, $eventType, $occurred,
                    $correlationId, $actorType, $actorId, $runtimeRevision, $resourceUrl)
                RETURNING sequence;
                """;
            insert.Parameters.AddWithValue("$eventId", eventId);
            insert.Parameters.AddWithValue("$matrixBuildId", matrixBuildId);
            insert.Parameters.AddWithValue("$eventType", eventType);
            insert.Parameters.AddWithValue("$occurred", occurredAt.ToUnixTimeMilliseconds());
            insert.Parameters.AddWithValue("$correlationId", context.CorrelationId);
            insert.Parameters.AddWithValue("$actorType", context.Principal.ActorType);
            insert.Parameters.AddWithValue("$actorId", context.Principal.ActorId);
            insert.Parameters.AddWithValue("$runtimeRevision", runtimeRevision);
            insert.Parameters.AddWithValue("$resourceUrl", resourceUrl);
            sequence = Convert.ToInt64(
                insert.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        if (sequence != expectedSequence)
        {
            throw new InvalidDataException(
                "serialized Build event sequence allocation returned an unexpected value");
        }

        using (var stream = connection.CreateCommand())
        {
            stream.Transaction = transaction;
            stream.CommandText = """
                INSERT INTO build_event_streams(
                    matrix_build_id, minimum_retained_sequence, latest_sequence, updated_unix_ms)
                VALUES ($matrixBuildId, $sequence, $sequence, $updated)
                ON CONFLICT(matrix_build_id) DO UPDATE SET
                    latest_sequence = excluded.latest_sequence,
                    updated_unix_ms = excluded.updated_unix_ms;
                """;
            stream.Parameters.AddWithValue("$matrixBuildId", matrixBuildId);
            stream.Parameters.AddWithValue("$sequence", sequence);
            stream.Parameters.AddWithValue("$updated", occurredAt.ToUnixTimeMilliseconds());
            stream.ExecuteNonQuery();
        }

        return true;
    }

    internal static string CreateEventId(long sequence, string matrixBuildId)
    {
        if (sequence < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence));
        }

        return $"{EventIdPrefix}{sequence:x16}_{ResourceHash(matrixBuildId)}";
    }

    internal static string RuntimeRevision(long sequence) => $"runtime:{sequence}";

    private static long ParseEventId(string eventId, string matrixBuildId)
    {
        if (eventId.Length != EventIdPrefix.Length + 16 + 1 + ResourceHashLength ||
            !eventId.StartsWith(EventIdPrefix, StringComparison.Ordinal) ||
            eventId[EventIdPrefix.Length + 16] != '_' ||
            !long.TryParse(
                eventId.AsSpan(EventIdPrefix.Length, 16),
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture,
                out var sequence) ||
            sequence < 1 ||
            !string.Equals(
                eventId[(EventIdPrefix.Length + 17)..],
                ResourceHash(matrixBuildId),
                StringComparison.Ordinal))
        {
            throw new BuildEventCursorException(
                "The event cursor does not identify this Build stream.", expired: false);
        }

        return sequence;
    }

    private static string ResourceHash(string matrixBuildId) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(matrixBuildId)))[
            ..ResourceHashLength];

    private static (long MinimumRetainedSequence, long LatestSequence)? ReadStream(
        SqliteConnection connection,
        string matrixBuildId) => ReadStream(connection, transaction: null, matrixBuildId);

    private static (long MinimumRetainedSequence, long LatestSequence)? ReadStream(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string matrixBuildId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT minimum_retained_sequence, latest_sequence
            FROM build_event_streams
            WHERE matrix_build_id = $matrixBuildId;
            """;
        command.Parameters.AddWithValue("$matrixBuildId", matrixBuildId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? (reader.GetInt64(0), reader.GetInt64(1)) : null;
    }

    private static bool CursorExists(
        SqliteConnection connection,
        string matrixBuildId,
        string eventId,
        long sequence)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 1 FROM build_events
            WHERE sequence = $sequence
                AND event_id = $eventId
                AND matrix_build_id = $matrixBuildId;
            """;
        command.Parameters.AddWithValue("$sequence", sequence);
        command.Parameters.AddWithValue("$eventId", eventId);
        command.Parameters.AddWithValue("$matrixBuildId", matrixBuildId);
        return command.ExecuteScalar() is not null;
    }

    private static bool MatrixBuildExists(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string matrixBuildId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT 1 FROM matrix_builds WHERE matrix_build_id = $matrixBuildId;
            """;
        command.Parameters.AddWithValue("$matrixBuildId", matrixBuildId);
        return command.ExecuteScalar() is not null;
    }

    private static void RequireBounded(string value, int maximumLength, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength ||
            value.Any(character => character is '\r' or '\n' or '\0'))
        {
            throw new ArgumentException(
                $"{name} must contain 1-{maximumLength} safe characters", name);
        }
    }
}
