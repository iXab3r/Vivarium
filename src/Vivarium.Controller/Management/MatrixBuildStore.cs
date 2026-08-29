using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Microsoft.Data.Sqlite;
using Vivarium.Contracts.V1;
using Vivarium.Controller.Auditing;
using Vivarium.Controller.Blobs.Access;
using Vivarium.Controller.Persistence;
using Vivarium.Controller.Rest.Events;
using Vivarium.Controller.Security;

namespace Vivarium.Controller.Management;

public sealed class MatrixRequestConflictException(string requestId)
    : Exception($"request_id '{requestId}' was already used for different build content");

public sealed record MatrixBuildSummary(
    string MatrixBuildId,
    string Project,
    string Configuration,
    DurableBuildState State,
    BuildOutcome Outcome,
    int CellCount,
    int FinishedCellCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record MatrixBuildQuery(
    int Limit,
    string? Project = null,
    string? Configuration = null,
    DurableBuildState? State = null,
    BuildOutcome? Outcome = null,
    DateTimeOffset? BeforeCreatedAt = null,
    string? BeforeMatrixBuildId = null);

public sealed record MatrixBuildPage(
    IReadOnlyList<MatrixBuildSummary> Items,
    bool HasMore);

public sealed record MatrixBuildArtifact(
    int Ordinal,
    string Path,
    string Sha256,
    long Size);

internal sealed record MatrixChildCancellation(string BuildId, string Reason);

internal sealed record MatrixCancellationCommit(
    BuildSnapshot Snapshot,
    IReadOnlyList<MatrixChildCancellation> ActiveChildren);

internal sealed record MatrixBuildResponseReceipt(
    int Status,
    string Json,
    string ETag);

/// <summary>
/// Durable matrix aggregates. A matrix submission and all of its ordinary FIFO cell builds are one
/// serialized SQLite transaction; the aggregate is a projection over those child build rows.
/// </summary>
public sealed class MatrixBuildStore
{
    private readonly VivariumDatabase database;

    public MatrixBuildStore(VivariumDatabase database) => this.database = database;

    internal Task<BuildRef?> FindIdempotentAsync(
        ManagementPrincipal principal,
        string requestId,
        string operationKind,
        string requestHash)
    {
        ValidatePrincipal(principal);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestHash);
        return database.ReadAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT m.matrix_build_id, i.request_hash
                FROM matrix_build_idempotency i
                JOIN matrix_builds m ON m.matrix_build_id = i.matrix_build_id
                WHERE i.actor_type = $actorType
                    AND i.actor_id = $actorId
                    AND i.operation_kind = $operationKind
                    AND i.request_id = $requestId;
                """;
            command.Parameters.AddWithValue("$actorType", principal.ActorType);
            command.Parameters.AddWithValue("$actorId", principal.ActorId);
            command.Parameters.AddWithValue("$operationKind", operationKind);
            command.Parameters.AddWithValue("$requestId", requestId);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            if (!string.Equals(reader.GetString(1), requestHash, StringComparison.Ordinal))
            {
                throw new MatrixRequestConflictException(requestId);
            }

            return new BuildRef { BuildId = reader.GetString(0) };
        });
    }

    internal Task<BuildRef> SubmitAsync(
        ManagementPrincipal principal,
        SubmitBuildRequest canonicalRequest,
        string requestHash,
        string definitionHash,
        DateTimeOffset now,
        TimeSpan defaultQueueWaitTimeout,
        Func<string, AuditEventDraft>? auditEventFactory) => SubmitScopedAsync(
            principal,
            canonicalRequest,
            MatrixBuildSubmissionIdentity.LegacyOperationKind,
            requestHash,
            definitionHash,
            now,
            defaultQueueWaitTimeout,
            stagingId: null,
            attachmentParticipant: null,
            ManagementRequestContext.System(
                "legacy-matrix-build-store", canonicalRequest.RequestId),
            auditEventFactory);

    internal Task<BuildRef> SubmitScopedAsync(
        ManagementPrincipal principal,
        SubmitBuildRequest canonicalRequest,
        string operationKind,
        string requestHash,
        string definitionHash,
        DateTimeOffset now,
        TimeSpan defaultQueueWaitTimeout,
        string? stagingId,
        IBlobBuildAttachmentParticipant? attachmentParticipant,
        ManagementRequestContext requestContext,
        Func<string, AuditEventDraft>? auditEventFactory)
    {
        ValidatePrincipal(principal);
        ArgumentNullException.ThrowIfNull(canonicalRequest);
        return database.WriteAsync(connection =>
        {
            var nowUnixMs = now.ToUnixTimeMilliseconds();
            var defaultQueueWaitMilliseconds = TimeoutMilliseconds(defaultQueueWaitTimeout);
            using var transaction = connection.BeginTransaction();

        using (var existing = connection.CreateCommand())
        {
            existing.Transaction = transaction;
            existing.CommandText = """
                SELECT m.matrix_build_id, i.request_hash
                FROM matrix_build_idempotency i
                JOIN matrix_builds m ON m.matrix_build_id = i.matrix_build_id
                WHERE i.actor_type = $actorType
                    AND i.actor_id = $actorId
                    AND i.operation_kind = $operationKind
                    AND i.request_id = $requestId;
                """;
            existing.Parameters.AddWithValue("$actorType", principal.ActorType);
            existing.Parameters.AddWithValue("$actorId", principal.ActorId);
            existing.Parameters.AddWithValue("$operationKind", operationKind);
            existing.Parameters.AddWithValue("$requestId", canonicalRequest.RequestId);
            using var reader = existing.ExecuteReader();
            if (reader.Read())
            {
                var existingMatrixBuildId = reader.GetString(0);
                if (!string.Equals(reader.GetString(1), requestHash, StringComparison.Ordinal))
                {
                    throw new MatrixRequestConflictException(canonicalRequest.RequestId);
                }

                transaction.Commit();
                return new BuildRef { BuildId = existingMatrixBuildId };
            }
        }

        var matrixBuildId = Guid.NewGuid().ToString("N");
        var storageRequestId = StorageRequestId(
            principal, operationKind, canonicalRequest.RequestId);
        using (var matrix = connection.CreateCommand())
        {
            matrix.Transaction = transaction;
            matrix.CommandText = """
                INSERT INTO matrix_builds(
                    matrix_build_id, request_id, request_hash, request_payload,
                    project, configuration, definition_snapshot, definition_hash,
                    created_unix_ms, updated_unix_ms)
                VALUES (
                    $matrixBuildId, $requestId, $requestHash, $requestPayload,
                    $project, $configuration, $definitionSnapshot, $definitionHash,
                    $now, $now);
                """;
            matrix.Parameters.AddWithValue("$matrixBuildId", matrixBuildId);
            matrix.Parameters.AddWithValue("$requestId", storageRequestId);
            matrix.Parameters.AddWithValue("$requestHash", requestHash);
            matrix.Parameters.Add("$requestPayload", SqliteType.Blob).Value =
                canonicalRequest.ToByteArray();
            matrix.Parameters.AddWithValue("$project", canonicalRequest.Project);
            matrix.Parameters.AddWithValue("$configuration", canonicalRequest.Configuration);
            matrix.Parameters.Add("$definitionSnapshot", SqliteType.Blob).Value =
                canonicalRequest.DefinitionSnapshot.ToByteArray();
            matrix.Parameters.AddWithValue("$definitionHash", definitionHash);
            matrix.Parameters.AddWithValue("$now", nowUnixMs);
            matrix.ExecuteNonQuery();
        }

        using (var idempotency = connection.CreateCommand())
        {
            idempotency.Transaction = transaction;
            idempotency.CommandText = """
                INSERT INTO matrix_build_idempotency(
                    actor_type, actor_id, operation_kind, request_id, request_hash,
                    matrix_build_id, response_status, response_json, response_etag, created_unix_ms)
                VALUES (
                    $actorType, $actorId, $operationKind, $requestId, $requestHash,
                    $matrixBuildId, NULL, NULL, NULL, $created);
                """;
            idempotency.Parameters.AddWithValue("$actorType", principal.ActorType);
            idempotency.Parameters.AddWithValue("$actorId", principal.ActorId);
            idempotency.Parameters.AddWithValue("$operationKind", operationKind);
            idempotency.Parameters.AddWithValue("$requestId", canonicalRequest.RequestId);
            idempotency.Parameters.AddWithValue("$requestHash", requestHash);
            idempotency.Parameters.AddWithValue("$matrixBuildId", matrixBuildId);
            idempotency.Parameters.AddWithValue("$created", nowUnixMs);
            idempotency.ExecuteNonQuery();
        }

        for (var ordinal = 0; ordinal < canonicalRequest.Cells.Count; ordinal++)
        {
            var requestedCell = canonicalRequest.Cells[ordinal];
            var assignment = requestedCell.Assignment.Clone();
            assignment.BuildId = Guid.NewGuid().ToString("N");
            var assignmentBytes = assignment.ToByteArray();
            var queueWaitMilliseconds = requestedCell.QueueTimeoutSec == 0
                ? defaultQueueWaitMilliseconds
                : checked((long)requestedCell.QueueTimeoutSec * 1000);
            if (queueWaitMilliseconds <= 0)
            {
                throw new MatrixBuildValidationException(
                    $"matrix cell '{requestedCell.Name}' queue_timeout_sec cannot be negative");
            }
            var queueDeadlineUnixMs = checked(nowUnixMs + queueWaitMilliseconds);

            using (var build = connection.CreateCommand())
            {
                build.Transaction = transaction;
                build.CommandText = """
                    INSERT INTO builds(
                        build_id, agent_id, state, assignment, created_unix_ms, updated_unix_ms)
                    VALUES ($buildId, NULL, 'QUEUED', $assignment, $now, $now);
                    """;
                build.Parameters.AddWithValue("$buildId", assignment.BuildId);
                build.Parameters.Add("$assignment", SqliteType.Blob).Value = assignmentBytes;
                build.Parameters.AddWithValue("$now", nowUnixMs);
                build.ExecuteNonQuery();
            }

            using (var queue = connection.CreateCommand())
            {
                queue.Transaction = transaction;
                queue.CommandText = """
                    INSERT INTO build_queue(
                        build_id, agent_expression, state, enqueued_unix_ms,
                        queue_deadline_unix_ms)
                    VALUES ($buildId, $agentExpression, 'QUEUED', $now, $queueDeadline);
                    """;
                queue.Parameters.AddWithValue("$buildId", assignment.BuildId);
                queue.Parameters.AddWithValue("$agentExpression", requestedCell.AgentExpression);
                queue.Parameters.AddWithValue("$now", nowUnixMs);
                queue.Parameters.AddWithValue("$queueDeadline", queueDeadlineUnixMs);
                queue.ExecuteNonQuery();
            }

            using var cell = connection.CreateCommand();
            cell.Transaction = transaction;
            cell.CommandText = """
                INSERT INTO matrix_build_cells(
                    matrix_build_id, cell_name, ordinal, build_id, agent_expression, rid)
                VALUES ($matrixBuildId, $cellName, $ordinal, $buildId, $agentExpression, $rid);
                """;
            cell.Parameters.AddWithValue("$matrixBuildId", matrixBuildId);
            cell.Parameters.AddWithValue("$cellName", requestedCell.Name);
            cell.Parameters.AddWithValue("$ordinal", ordinal);
            cell.Parameters.AddWithValue("$buildId", assignment.BuildId);
            cell.Parameters.AddWithValue("$agentExpression", requestedCell.AgentExpression);
            cell.Parameters.AddWithValue("$rid", requestedCell.Rid);
            cell.ExecuteNonQuery();
        }

        if (stagingId is not null)
        {
            var participant = attachmentParticipant ?? throw new InvalidOperationException(
                "REST matrix submission requires the blob staging attachment participant");
            participant.Attach(
                connection,
                transaction,
                new BlobBuildAttachmentRequest(
                    principal,
                    operationKind,
                    canonicalRequest.RequestId,
                    stagingId,
                    canonicalRequest.Project,
                    matrixBuildId,
                    canonicalRequest.Cells
                        .SelectMany(cell => cell.Assignment.Payload)
                        .Select(payload => payload.Sha256)
                        .Distinct(StringComparer.Ordinal)
                        .Order(StringComparer.Ordinal)
                        .ToArray(),
                    now));
        }

        BuildEventStore.AppendForMatrix(
            connection,
            transaction,
            matrixBuildId,
            "build.created",
            now,
            requestContext);

        if (auditEventFactory is not null)
        {
            AuditEventStore.Append(connection, transaction, auditEventFactory(matrixBuildId));
        }

            transaction.Commit();
            return new BuildRef { BuildId = matrixBuildId };
        });
    }

    public Task<BuildSnapshot?> GetSnapshotAsync(string matrixBuildId) =>
        database.ReadAsync(connection => ReadSnapshot(connection, matrixBuildId));

    internal Task<MatrixBuildResponseReceipt?> FindResponseReceiptAsync(
        ManagementPrincipal principal,
        string operationKind,
        string requestId,
        string requestHash)
    {
        ValidatePrincipal(principal);
        return database.ReadAsync(connection => ReadResponseReceipt(
            connection,
            transaction: null,
            principal,
            operationKind,
            requestId,
            requestHash));
    }

    internal Task<MatrixBuildResponseReceipt> FinalizeResponseReceiptAsync(
        ManagementPrincipal principal,
        string operationKind,
        string requestId,
        string requestHash,
        MatrixBuildResponseReceipt proposed)
    {
        ValidatePrincipal(principal);
        ArgumentNullException.ThrowIfNull(proposed);
        if (proposed.Status is < 100 or > 599 || proposed.Json.Length is < 2 or > 1_048_576 ||
            string.IsNullOrWhiteSpace(proposed.ETag) || proposed.ETag.Length > 256)
        {
            throw new ArgumentException("the Build response receipt is not bounded", nameof(proposed));
        }

        return database.WriteAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            var existing = ReadResponseReceipt(
                connection,
                transaction,
                principal,
                operationKind,
                requestId,
                requestHash,
                requireRecord: true);
            if (existing is not null)
            {
                transaction.Commit();
                return existing;
            }

            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE matrix_build_idempotency SET
                    response_status = $status,
                    response_json = $json,
                    response_etag = $etag
                WHERE actor_type = $actorType
                    AND actor_id = $actorId
                    AND operation_kind = $operationKind
                    AND request_id = $requestId
                    AND request_hash = $requestHash
                    AND response_status IS NULL
                    AND response_json IS NULL
                    AND response_etag IS NULL;
                """;
            update.Parameters.AddWithValue("$status", proposed.Status);
            update.Parameters.AddWithValue("$json", proposed.Json);
            update.Parameters.AddWithValue("$etag", proposed.ETag);
            update.Parameters.AddWithValue("$actorType", principal.ActorType);
            update.Parameters.AddWithValue("$actorId", principal.ActorId);
            update.Parameters.AddWithValue("$operationKind", operationKind);
            update.Parameters.AddWithValue("$requestId", requestId);
            update.Parameters.AddWithValue("$requestHash", requestHash);
            if (update.ExecuteNonQuery() != 1)
            {
                throw new InvalidDataException(
                    "the Build idempotency response receipt changed during serialized finalization");
            }

            transaction.Commit();
            return proposed;
        });
    }

    public Task<string?> FindMatrixBuildIdForChildAsync(string buildId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(buildId);
        return database.ReadAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT matrix_build_id
                FROM matrix_build_cells
                WHERE build_id = $buildId;
                """;
            command.Parameters.AddWithValue("$buildId", buildId);
            return command.ExecuteScalar() as string;
        });
    }

    /// <summary>
    /// Atomically stops every non-terminal child of a matrix. Queued children become terminal in
    /// the same transaction that removes their queue claims; assigned children retain ownership and
    /// move to cancellation-requested so the ordinary BuildTracker handshake can finish them.
    /// </summary>
    internal Task<MatrixCancellationCommit?> CancelAsync(
        string matrixBuildId,
        string reason,
        DateTimeOffset now) => CancelAsync(
            matrixBuildId, reason, now, auditEvent: null, requestContext: null);

    internal Task<MatrixCancellationCommit?> CancelAsync(
        string matrixBuildId,
        string reason,
        DateTimeOffset now,
        AuditEventDraft? auditEvent,
        ManagementRequestContext? requestContext = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(matrixBuildId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        return database.WriteAsync(connection =>
        {
            var nowUnixMs = now.ToUnixTimeMilliseconds();
            using var transaction = connection.BeginTransaction();
            using (var exists = connection.CreateCommand())
            {
                exists.Transaction = transaction;
                exists.CommandText = """
                    SELECT 1 FROM matrix_builds WHERE matrix_build_id = $matrixBuildId;
                    """;
                exists.Parameters.AddWithValue("$matrixBuildId", matrixBuildId);
                if (exists.ExecuteScalar() is null)
                {
                    transaction.Rollback();
                    return null;
                }
            }

            var children = new List<(string BuildId, string State, string? Reason)>();
            using (var select = connection.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText = """
                    SELECT b.build_id, b.state, b.cancellation_reason
                    FROM matrix_build_cells c
                    JOIN builds b ON b.build_id = c.build_id
                    WHERE c.matrix_build_id = $matrixBuildId
                    ORDER BY c.ordinal;
                    """;
                select.Parameters.AddWithValue("$matrixBuildId", matrixBuildId);
                using var reader = select.ExecuteReader();
                while (reader.Read())
                {
                    children.Add((
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.IsDBNull(2) ? null : reader.GetString(2)));
                }
            }

            var changed = false;
            foreach (var child in children.Where(child => child.State == "QUEUED"))
            {
                var result = new BuildResult
                {
                    BuildId = child.BuildId,
                    Outcome = BuildOutcome.Cancelled,
                    StatusText = reason,
                }.ToByteArray();

                using (var queue = connection.CreateCommand())
                {
                    queue.Transaction = transaction;
                    queue.CommandText = """
                        UPDATE build_queue SET
                            state = 'REMOVED',
                            removed_unix_ms = $now,
                            removal_reason = $reason
                        WHERE build_id = $buildId
                            AND state IN ('QUEUED', 'CLAIMED');
                        """;
                    queue.Parameters.AddWithValue("$now", nowUnixMs);
                    queue.Parameters.AddWithValue("$reason", reason);
                    queue.Parameters.AddWithValue("$buildId", child.BuildId);
                    if (queue.ExecuteNonQuery() != 1)
                    {
                        throw new InvalidDataException(
                            $"queued matrix child '{child.BuildId}' has no active queue row");
                    }
                }

                using var build = connection.CreateCommand();
                build.Transaction = transaction;
                build.CommandText = """
                    UPDATE builds SET
                        state = 'FINISHED',
                        result = $result,
                        cancellation_reason = $reason,
                        updated_unix_ms = $now
                    WHERE build_id = $buildId
                        AND state = 'QUEUED';
                    """;
                build.Parameters.Add("$result", SqliteType.Blob).Value = result;
                build.Parameters.AddWithValue("$reason", reason);
                build.Parameters.AddWithValue("$now", nowUnixMs);
                build.Parameters.AddWithValue("$buildId", child.BuildId);
                if (build.ExecuteNonQuery() != 1)
                {
                    throw new InvalidDataException(
                        $"queued matrix child '{child.BuildId}' changed state during serialized cancellation");
                }

                changed = true;
            }

            foreach (var child in children.Where(child => child.State == "RUNNING" ||
                         child.State == "CANCEL_REQUESTED" && child.Reason is null))
            {
                using var build = connection.CreateCommand();
                build.Transaction = transaction;
                build.CommandText = """
                    UPDATE builds SET
                        state = 'CANCEL_REQUESTED',
                        cancellation_reason = COALESCE(cancellation_reason, $reason),
                        updated_unix_ms = $now
                    WHERE build_id = $buildId
                        AND state IN ('RUNNING', 'CANCEL_REQUESTED');
                    """;
                build.Parameters.AddWithValue("$reason", reason);
                build.Parameters.AddWithValue("$now", nowUnixMs);
                build.Parameters.AddWithValue("$buildId", child.BuildId);
                if (build.ExecuteNonQuery() != 1)
                {
                    throw new InvalidDataException(
                        $"active matrix child '{child.BuildId}' changed state during serialized cancellation");
                }

                changed = true;
            }

            if (changed)
            {
                using var matrix = connection.CreateCommand();
                matrix.Transaction = transaction;
                matrix.CommandText = """
                    UPDATE matrix_builds SET updated_unix_ms = $now
                    WHERE matrix_build_id = $matrixBuildId;
                    """;
                matrix.Parameters.AddWithValue("$now", nowUnixMs);
                matrix.Parameters.AddWithValue("$matrixBuildId", matrixBuildId);
                matrix.ExecuteNonQuery();

                if (auditEvent is not null)
                {
                    AuditEventStore.Append(connection, transaction, auditEvent);
                }

                BuildEventStore.AppendForMatrix(
                    connection,
                    transaction,
                    matrixBuildId,
                    "build.cancellation-requested",
                    now,
                    requestContext);
            }

            transaction.Commit();

            var activeChildren = new List<MatrixChildCancellation>();
            using (var active = connection.CreateCommand())
            {
                active.CommandText = """
                    SELECT b.build_id, b.cancellation_reason
                    FROM matrix_build_cells c
                    JOIN builds b ON b.build_id = c.build_id
                    WHERE c.matrix_build_id = $matrixBuildId
                        AND b.state = 'CANCEL_REQUESTED'
                    ORDER BY c.ordinal;
                    """;
                active.Parameters.AddWithValue("$matrixBuildId", matrixBuildId);
                using var reader = active.ExecuteReader();
                while (reader.Read())
                {
                    activeChildren.Add(new MatrixChildCancellation(
                        reader.GetString(0),
                        reader.IsDBNull(1) ? reason : reader.GetString(1)));
                }
            }

            var snapshot = ReadSnapshot(connection, matrixBuildId)
                ?? throw new InvalidDataException(
                    $"matrix build '{matrixBuildId}' disappeared after durable cancellation");
            return new MatrixCancellationCommit(snapshot, activeChildren);
        });
    }

    public Task<IReadOnlyList<MatrixBuildSummary>> ListRecentAsync(int limit = 25)
    {
        if (limit is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "limit must be between 1 and 100");
        }

        return database.ReadAsync<IReadOnlyList<MatrixBuildSummary>>(connection =>
        {
            var matrixBuildIds = new List<string>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    SELECT matrix_build_id
                    FROM matrix_builds
                    ORDER BY created_unix_ms DESC, matrix_build_id DESC
                    LIMIT $limit;
                    """;
                command.Parameters.AddWithValue("$limit", limit);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    matrixBuildIds.Add(reader.GetString(0));
                }
            }

            var summaries = new List<MatrixBuildSummary>(matrixBuildIds.Count);
            foreach (var matrixBuildId in matrixBuildIds)
            {
                var snapshot = ReadSnapshot(connection, matrixBuildId)
                    ?? throw new InvalidDataException(
                        $"matrix build '{matrixBuildId}' disappeared during a durable read");
                summaries.Add(new MatrixBuildSummary(
                    matrixBuildId,
                    snapshot.Project,
                    snapshot.Configuration,
                    snapshot.State,
                    snapshot.Outcome,
                    snapshot.Cells.Count,
                    snapshot.Cells.Count(cell => cell.State == DurableBuildState.Finished),
                    DateTimeOffset.FromUnixTimeMilliseconds(snapshot.CreatedUnixMs),
                    DateTimeOffset.FromUnixTimeMilliseconds(snapshot.UpdatedUnixMs)));
            }

            return summaries;
        });
    }

    /// <summary>
    /// Reads matrix aggregates in a deterministic newest-first order. State and outcome are derived
    /// from child builds, so filtered reads scan bounded database windows until they fill one page.
    /// </summary>
    public Task<MatrixBuildPage> ListPageAsync(MatrixBuildQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Limit is < 1 or > 200)
        {
            throw new ArgumentOutOfRangeException(
                nameof(query), "limit must be between 1 and 200");
        }

        if (query.BeforeCreatedAt.HasValue !=
            !string.IsNullOrWhiteSpace(query.BeforeMatrixBuildId))
        {
            throw new ArgumentException(
                "both cursor creation time and matrix build id are required", nameof(query));
        }

        return database.ReadAsync(connection =>
        {
            const int scanWindow = 200;
            var matches = new List<MatrixBuildSummary>(query.Limit + 1);
            var beforeCreatedUnixMs = query.BeforeCreatedAt?.ToUnixTimeMilliseconds();
            var beforeMatrixBuildId = query.BeforeMatrixBuildId;

            while (matches.Count <= query.Limit)
            {
                var candidates = new List<(string Id, long CreatedUnixMs)>(scanWindow);
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = """
                        SELECT matrix_build_id, created_unix_ms
                        FROM matrix_builds
                        WHERE ($project IS NULL OR project = $project)
                            AND ($configuration IS NULL OR configuration = $configuration)
                            AND (
                                $beforeCreatedUnixMs IS NULL
                                OR created_unix_ms < $beforeCreatedUnixMs
                                OR (created_unix_ms = $beforeCreatedUnixMs
                                    AND matrix_build_id < $beforeMatrixBuildId)
                            )
                        ORDER BY created_unix_ms DESC, matrix_build_id DESC
                        LIMIT $scanWindow;
                        """;
                    command.Parameters.AddWithValue(
                        "$project", (object?)NormalizeFilter(query.Project) ?? DBNull.Value);
                    command.Parameters.AddWithValue(
                        "$configuration",
                        (object?)NormalizeFilter(query.Configuration) ?? DBNull.Value);
                    command.Parameters.AddWithValue(
                        "$beforeCreatedUnixMs", (object?)beforeCreatedUnixMs ?? DBNull.Value);
                    command.Parameters.AddWithValue(
                        "$beforeMatrixBuildId", (object?)beforeMatrixBuildId ?? DBNull.Value);
                    command.Parameters.AddWithValue("$scanWindow", scanWindow);
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        candidates.Add((reader.GetString(0), reader.GetInt64(1)));
                    }
                }

                if (candidates.Count == 0)
                {
                    break;
                }

                foreach (var candidate in candidates)
                {
                    var snapshot = ReadSnapshot(connection, candidate.Id)
                        ?? throw new InvalidDataException(
                            $"matrix build '{candidate.Id}' disappeared during a durable read");
                    if ((query.State is not null && snapshot.State != query.State) ||
                        (query.Outcome is not null && snapshot.Outcome != query.Outcome))
                    {
                        continue;
                    }

                    matches.Add(ToSummary(snapshot));
                    if (matches.Count > query.Limit)
                    {
                        break;
                    }
                }

                var last = candidates[^1];
                beforeCreatedUnixMs = last.CreatedUnixMs;
                beforeMatrixBuildId = last.Id;
                if (candidates.Count < scanWindow)
                {
                    break;
                }
            }

            return new MatrixBuildPage(
                matches.Take(query.Limit).ToArray(),
                matches.Count > query.Limit);
        });
    }

    public Task<MatrixBuildArtifact?> FindArtifactAsync(
        string matrixBuildId,
        string cellBuildId,
        int ordinal)
    {
        if (ordinal < 0)
        {
            return Task.FromResult<MatrixBuildArtifact?>(null);
        }

        return database.ReadAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT b.result
                FROM matrix_build_cells c
                JOIN builds b ON b.build_id = c.build_id
                WHERE c.matrix_build_id = $matrixBuildId
                    AND c.build_id = $cellBuildId
                    AND b.result IS NOT NULL;
                """;
            command.Parameters.AddWithValue("$matrixBuildId", matrixBuildId);
            command.Parameters.AddWithValue("$cellBuildId", cellBuildId);
            var value = command.ExecuteScalar();
            if (value is not byte[] bytes)
            {
                return null;
            }

            var result = BuildResult.Parser.ParseFrom(bytes);
            if (ordinal >= result.Artifacts.Count)
            {
                return null;
            }

            var artifact = result.Artifacts[ordinal];
            return new MatrixBuildArtifact(
                ordinal, artifact.Path, artifact.Sha256, artifact.Size);
        });
    }

    private static BuildSnapshot? ReadSnapshot(SqliteConnection connection, string matrixBuildId)
    {
        string project;
        string configuration;
        long createdUnixMs;
        long matrixUpdatedUnixMs;
        using (var matrix = connection.CreateCommand())
        {
            matrix.CommandText = """
                SELECT project, configuration, created_unix_ms, updated_unix_ms
                FROM matrix_builds
                WHERE matrix_build_id = $matrixBuildId;
                """;
            matrix.Parameters.AddWithValue("$matrixBuildId", matrixBuildId);
            using var reader = matrix.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            project = reader.GetString(0);
            configuration = reader.GetString(1);
            createdUnixMs = reader.GetInt64(2);
            matrixUpdatedUnixMs = reader.GetInt64(3);
        }

        var cells = new List<BuildCellSnapshot>();
        var updatedUnixMs = matrixUpdatedUnixMs;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT c.cell_name, c.build_id, c.agent_expression,
                    b.state, b.agent_id, b.result, b.updated_unix_ms, c.rid,
                    q.queue_deadline_unix_ms, b.agent_name_snapshot,
                    b.agent_parameters_snapshot_json,
                    b.agent_custom_parameters_snapshot_json,
                    b.cancellation_reason
                FROM matrix_build_cells c
                JOIN builds b ON b.build_id = c.build_id
                JOIN build_queue q ON q.build_id = c.build_id
                WHERE c.matrix_build_id = $matrixBuildId
                ORDER BY c.ordinal;
                """;
            command.Parameters.AddWithValue("$matrixBuildId", matrixBuildId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var result = reader.IsDBNull(5)
                    ? null
                    : BuildResult.Parser.ParseFrom((byte[])reader[5]);
                var cell = new BuildCellSnapshot
                {
                    Name = reader.GetString(0),
                    BuildId = reader.GetString(1),
                    AgentExpression = reader.GetString(2),
                    State = ParseState(reader.GetString(3)),
                    AgentId = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                    Outcome = result?.Outcome ?? BuildOutcome.Unspecified,
                    StatusText = result?.StatusText ??
                        (reader.IsDBNull(12) ? string.Empty : reader.GetString(12)),
                    Rid = reader.GetString(7),
                    QueueDeadlineUnixMs = reader.IsDBNull(8) ? 0 : reader.GetInt64(8),
                    AgentName = reader.GetString(9),
                };
                var agentParameters = JsonSerializer.Deserialize<Dictionary<string, string>>(
                    reader.GetString(10)) ?? [];
                cell.AgentParameters.Add(agentParameters);
                var agentCustomParameters = JsonSerializer.Deserialize<Dictionary<string, string>>(
                    reader.GetString(11)) ?? [];
                cell.AgentCustomParameters.Add(agentCustomParameters);
                if (result is not null)
                {
                    cell.Artifacts.Add(result.Artifacts);
                    cell.Steps.Add(result.Steps);
                }

                cells.Add(cell);
                updatedUnixMs = Math.Max(updatedUnixMs, reader.GetInt64(6));
            }
        }

        var state = AggregateState(cells);
        var snapshot = new BuildSnapshot
        {
            Build = new BuildRef { BuildId = matrixBuildId },
            Project = project,
            Configuration = configuration,
            State = state,
            Outcome = state == DurableBuildState.Finished
                ? AggregateOutcome(cells)
                : BuildOutcome.Unspecified,
            CreatedUnixMs = createdUnixMs,
            UpdatedUnixMs = updatedUnixMs,
        };
        snapshot.Cells.Add(cells);
        return snapshot;
    }

    private static MatrixBuildSummary ToSummary(BuildSnapshot snapshot) => new(
        snapshot.Build.BuildId,
        snapshot.Project,
        snapshot.Configuration,
        snapshot.State,
        snapshot.Outcome,
        snapshot.Cells.Count,
        snapshot.Cells.Count(cell => cell.State == DurableBuildState.Finished),
        DateTimeOffset.FromUnixTimeMilliseconds(snapshot.CreatedUnixMs),
        DateTimeOffset.FromUnixTimeMilliseconds(snapshot.UpdatedUnixMs));

    private static string? NormalizeFilter(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static DurableBuildState ParseState(string state) => state switch
    {
        "QUEUED" => DurableBuildState.Queued,
        "RUNNING" => DurableBuildState.Running,
        "CANCEL_REQUESTED" => DurableBuildState.CancelRequested,
        "FINISHED" => DurableBuildState.Finished,
        _ => throw new InvalidDataException($"unknown persisted build state '{state}'"),
    };

    private static DurableBuildState AggregateState(IReadOnlyList<BuildCellSnapshot> cells)
    {
        if (cells.All(cell => cell.State == DurableBuildState.Finished))
        {
            return DurableBuildState.Finished;
        }

        if (cells.Any(cell => cell.State == DurableBuildState.CancelRequested))
        {
            return DurableBuildState.CancelRequested;
        }

        return cells.Any(cell => cell.State is DurableBuildState.Running or DurableBuildState.Finished)
            ? DurableBuildState.Running
            : DurableBuildState.Queued;
    }

    private static BuildOutcome AggregateOutcome(IReadOnlyList<BuildCellSnapshot> cells)
    {
        if (cells.Any(cell => cell.Outcome == BuildOutcome.InfrastructureFailed))
        {
            return BuildOutcome.InfrastructureFailed;
        }

        if (cells.Any(cell => cell.Outcome == BuildOutcome.Failed))
        {
            return BuildOutcome.Failed;
        }

        if (cells.Any(cell => cell.Outcome == BuildOutcome.Cancelled))
        {
            return BuildOutcome.Cancelled;
        }

        return cells.All(cell => cell.Outcome == BuildOutcome.Succeeded)
            ? BuildOutcome.Succeeded
            : BuildOutcome.Unspecified;
    }

    private static long TimeoutMilliseconds(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout), "queue wait timeout must be positive");
        }

        return checked((long)Math.Ceiling(timeout.TotalMilliseconds));
    }

    private static void ValidatePrincipal(ManagementPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentException.ThrowIfNullOrWhiteSpace(principal.ActorType);
        ArgumentException.ThrowIfNullOrWhiteSpace(principal.ActorId);
    }

    private static MatrixBuildResponseReceipt? ReadResponseReceipt(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        ManagementPrincipal principal,
        string operationKind,
        string requestId,
        string requestHash,
        bool requireRecord = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestHash);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT request_hash, response_status, response_json, response_etag
            FROM matrix_build_idempotency
            WHERE actor_type = $actorType
                AND actor_id = $actorId
                AND operation_kind = $operationKind
                AND request_id = $requestId;
            """;
        command.Parameters.AddWithValue("$actorType", principal.ActorType);
        command.Parameters.AddWithValue("$actorId", principal.ActorId);
        command.Parameters.AddWithValue("$operationKind", operationKind);
        command.Parameters.AddWithValue("$requestId", requestId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            if (requireRecord)
            {
                throw new InvalidDataException("the Build idempotency record is missing");
            }

            return null;
        }

        if (!string.Equals(reader.GetString(0), requestHash, StringComparison.Ordinal))
        {
            throw new MatrixRequestConflictException(requestId);
        }

        var valuesPresent = !reader.IsDBNull(1) && !reader.IsDBNull(2) && !reader.IsDBNull(3);
        var valuesAbsent = reader.IsDBNull(1) && reader.IsDBNull(2) && reader.IsDBNull(3);
        if (!valuesPresent && !valuesAbsent)
        {
            throw new InvalidDataException("the Build idempotency response receipt is incomplete");
        }

        return valuesPresent
            ? new MatrixBuildResponseReceipt(
                reader.GetInt32(1), reader.GetString(2), reader.GetString(3))
            : null;
    }

    private static string StorageRequestId(
        ManagementPrincipal principal,
        string operationKind,
        string clientRequestId)
    {
        var identity = JsonSerializer.Serialize(new[]
        {
            principal.ActorType,
            principal.ActorId,
            operationKind,
            clientRequestId,
        });
        return "principal:" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
    }

}
