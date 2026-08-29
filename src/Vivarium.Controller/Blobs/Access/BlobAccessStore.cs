using Microsoft.Data.Sqlite;
using Vivarium.Controller.Auditing;
using Vivarium.Controller.Persistence;
using Vivarium.Controller.Security;

namespace Vivarium.Controller.Blobs.Access;

public sealed class BlobAccessStore(VivariumDatabase database) :
    IBlobBuildAttachmentParticipant,
    IBlobArtifactAttachmentParticipant
{
    internal sealed record StoredUploadItem(
        string StagingId,
        ManagementPrincipal Principal,
        string ProjectId,
        string Sha256,
        long Size,
        DateTimeOffset ExpiresAt,
        bool Ready);

    internal Task<BlobUploadPlan> CreatePlanAsync(
        ManagementRequestContext context,
        string projectId,
        IReadOnlyList<BlobDescriptor> items,
        string requestHash,
        DateTimeOffset now,
        DateTimeOffset expiresAt,
        IReadOnlySet<string> alreadyGranted) => database.WriteAsync(connection =>
    {
        using var transaction = connection.BeginTransaction();
        var existing = ReadPlanByRequest(
            connection,
            transaction,
            context.Principal,
            BlobAccessService.CreatePlanOperationKind,
            context.RequestId!);
        if (existing is not null)
        {
            if (!string.Equals(existing.Value.RequestHash, requestHash, StringComparison.Ordinal))
            {
                throw Conflict(
                    "idempotency_key_reused",
                    "The Idempotency-Key was already used for another blob upload plan.");
            }

            var replay = ReadPlan(connection, transaction, existing.Value.StagingId, replayed: true);
            transaction.Commit();
            return replay;
        }

        var stagingId = "stage-" + ManagementIdentifiers.NewId();
        using (var plan = connection.CreateCommand())
        {
            plan.Transaction = transaction;
            plan.CommandText = """
                INSERT INTO blob_upload_plans(
                    staging_id, actor_type, actor_id, project_id, operation_kind, request_id,
                    request_hash, created_unix_ms, expires_unix_ms)
                VALUES (
                    $stagingId, $actorType, $actorId, $projectId, $operationKind, $requestId,
                    $requestHash, $createdAt, $expiresAt);
                """;
            plan.Parameters.AddWithValue("$stagingId", stagingId);
            plan.Parameters.AddWithValue("$actorType", context.Principal.ActorType);
            plan.Parameters.AddWithValue("$actorId", context.Principal.ActorId);
            plan.Parameters.AddWithValue("$projectId", projectId);
            plan.Parameters.AddWithValue("$operationKind", BlobAccessService.CreatePlanOperationKind);
            plan.Parameters.AddWithValue("$requestId", context.RequestId!);
            plan.Parameters.AddWithValue("$requestHash", requestHash);
            plan.Parameters.AddWithValue("$createdAt", now.ToUnixTimeMilliseconds());
            plan.Parameters.AddWithValue("$expiresAt", expiresAt.ToUnixTimeMilliseconds());
            plan.ExecuteNonQuery();
        }

        foreach (var item in items)
        {
            using (var insertItem = connection.CreateCommand())
            {
                insertItem.Transaction = transaction;
                insertItem.CommandText = """
                    INSERT INTO blob_upload_plan_items(staging_id, sha256, declared_size)
                    VALUES ($stagingId, $sha256, $size);
                    """;
                insertItem.Parameters.AddWithValue("$stagingId", stagingId);
                insertItem.Parameters.AddWithValue("$sha256", item.Sha256);
                insertItem.Parameters.AddWithValue("$size", item.Size);
                insertItem.ExecuteNonQuery();
            }

            if (alreadyGranted.Contains(item.Sha256))
            {
                InsertUploadReceipt(
                    connection,
                    transaction,
                    stagingId,
                    item,
                    now);
            }
        }

        AuditEventStore.Append(
            connection,
            transaction,
            AuditEventDraft.Create(
                context,
                now,
                "blob-upload-plan.create",
                "blob-upload-plan",
                stagingId,
                details: new Dictionary<string, string>
                {
                    ["project_id"] = projectId,
                    ["item_count"] = items.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                }));
        transaction.Commit();
        return ReadPlan(connection, transaction: null, stagingId, replayed: false);
    });

    internal Task<StoredUploadItem?> GetUploadItemAsync(
        ManagementPrincipal principal,
        string stagingId,
        string sha256) => database.ReadAsync(connection =>
            ReadUploadItem(connection, transaction: null, principal, stagingId, sha256));

    internal Task<BlobUploadOutcome> CompleteUploadAsync(
        ManagementRequestContext context,
        StoredUploadItem item,
        DateTimeOffset now) => database.WriteAsync(connection =>
    {
        using var transaction = connection.BeginTransaction();
        var current = ReadUploadItem(
            connection,
            transaction,
            context.Principal,
            item.StagingId,
            item.Sha256) ?? throw NotFound();
        if (current.ExpiresAt <= now)
        {
            throw Expired();
        }

        if (current.Ready)
        {
            AuditEventStore.Append(
                connection,
                transaction,
                AuditEventDraft.Create(
                    context,
                    now,
                    "blob-staging.upload",
                    "blob-upload-plan",
                    current.StagingId,
                    AuditOutcome.NoChange,
                    "exact_replay"));
            transaction.Commit();
            return BlobUploadOutcome.ExactReplay;
        }

        InsertUploadReceipt(
            connection,
            transaction,
            current.StagingId,
            new BlobDescriptor(current.Sha256, current.Size),
            now);
        using (var grant = connection.CreateCommand())
        {
            grant.Transaction = transaction;
            grant.CommandText = """
                INSERT INTO blob_principal_project_grants(
                    actor_type, actor_id, project_id, sha256, declared_size,
                    source_staging_id, granted_unix_ms)
                VALUES (
                    $actorType, $actorId, $projectId, $sha256, $size,
                    $stagingId, $grantedAt)
                ON CONFLICT(actor_type, actor_id, project_id, sha256) DO NOTHING;
                """;
            grant.Parameters.AddWithValue("$actorType", context.Principal.ActorType);
            grant.Parameters.AddWithValue("$actorId", context.Principal.ActorId);
            grant.Parameters.AddWithValue("$projectId", current.ProjectId);
            grant.Parameters.AddWithValue("$sha256", current.Sha256);
            grant.Parameters.AddWithValue("$size", current.Size);
            grant.Parameters.AddWithValue("$stagingId", current.StagingId);
            grant.Parameters.AddWithValue("$grantedAt", now.ToUnixTimeMilliseconds());
            grant.ExecuteNonQuery();
        }

        AuditEventStore.Append(
            connection,
            transaction,
            AuditEventDraft.Create(
                context,
                now,
                "blob-staging.upload",
                "blob-upload-plan",
                current.StagingId,
                details: new Dictionary<string, string>
                {
                    ["project_id"] = current.ProjectId,
                }));
        transaction.Commit();
        return BlobUploadOutcome.Uploaded;
    });

    internal Task<IReadOnlySet<string>> FindExistingGrantsAsync(
        ManagementPrincipal principal,
        string projectId,
        IReadOnlyList<BlobDescriptor> items) => database.ReadAsync<IReadOnlySet<string>>(connection =>
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT 1
                FROM blob_principal_project_grants
                WHERE actor_type = $actorType
                  AND actor_id = $actorId
                  AND project_id = $projectId
                  AND sha256 = $sha256
                  AND declared_size = $size;
                """;
            command.Parameters.AddWithValue("$actorType", principal.ActorType);
            command.Parameters.AddWithValue("$actorId", principal.ActorId);
            command.Parameters.AddWithValue("$projectId", projectId);
            command.Parameters.AddWithValue("$sha256", item.Sha256);
            command.Parameters.AddWithValue("$size", item.Size);
            if (command.ExecuteScalar() is not null)
            {
                result.Add(item.Sha256);
            }
        }

        return result;
    });

    public BlobBuildAttachmentOutcome Attach(
        SqliteConnection connection,
        SqliteTransaction transaction,
        BlobBuildAttachmentRequest request)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(request);
        BlobAccessValidation.ValidatePrincipal(request.Principal);
        BlobAccessValidation.RequireBounded(request.OperationKind, 128, "operation kind");
        BlobAccessValidation.RequireBounded(request.RequestId, 256, "request ID");
        BlobAccessValidation.RequireBounded(request.StagingId, 64, "staging ID");
        BlobAccessValidation.ValidateProjectId(request.ProjectId);
        BlobAccessValidation.RequireBounded(request.MatrixBuildId, 256, "matrix build ID");
        var hashes = BlobAccessValidation.NormalizeDistinctHashes(request.DistinctAssignmentSha256);

        var plan = ReadPlanOwner(connection, transaction, request.StagingId) ?? throw NotFound();
        if (!string.Equals(
                plan.Principal.ActorType,
                request.Principal.ActorType,
                StringComparison.Ordinal) ||
            !string.Equals(
                plan.Principal.ActorId,
                request.Principal.ActorId,
                StringComparison.Ordinal) ||
            !string.Equals(plan.ProjectId, request.ProjectId, StringComparison.Ordinal))
        {
            throw NotFound();
        }

        if (plan.ExpiresAt <= request.Now)
        {
            throw Expired();
        }

        var planned = ReadPlanDescriptors(connection, transaction, request.StagingId);
        if (!planned.Select(item => item.Sha256).SequenceEqual(hashes, StringComparer.Ordinal))
        {
            throw Conflict(
                "blob_staging_set_mismatch",
                "The staging plan does not exactly match the build payload set.");
        }

        RequireAllUploadReceipts(connection, transaction, request.StagingId, planned);
        var existingByStage = ReadPayloadSetByStaging(connection, transaction, request.StagingId);
        var existingByBuild = ReadPayloadSetByBuild(connection, transaction, request.MatrixBuildId);
        if (existingByStage is not null || existingByBuild is not null)
        {
            if (existingByStage is not null && existingByBuild is not null &&
                existingByStage == existingByBuild &&
                PayloadSetMatches(existingByStage, request) &&
                ReadPayloadReferenceHashes(connection, transaction, request.MatrixBuildId)
                    .SequenceEqual(hashes, StringComparer.Ordinal))
            {
                return BlobBuildAttachmentOutcome.ExactReplay;
            }

            throw Conflict(
                "blob_staging_already_consumed",
                "The staging plan is already attached to another build request.");
        }

        using (var set = connection.CreateCommand())
        {
            set.Transaction = transaction;
            set.CommandText = """
                INSERT INTO blob_build_payload_sets(
                    matrix_build_id, staging_id, actor_type, actor_id, project_id,
                    operation_kind, request_id, attached_unix_ms)
                VALUES (
                    $matrixBuildId, $stagingId, $actorType, $actorId, $projectId,
                    $operationKind, $requestId, $attachedAt);
                """;
            set.Parameters.AddWithValue("$matrixBuildId", request.MatrixBuildId);
            set.Parameters.AddWithValue("$stagingId", request.StagingId);
            set.Parameters.AddWithValue("$actorType", request.Principal.ActorType);
            set.Parameters.AddWithValue("$actorId", request.Principal.ActorId);
            set.Parameters.AddWithValue("$projectId", request.ProjectId);
            set.Parameters.AddWithValue("$operationKind", request.OperationKind);
            set.Parameters.AddWithValue("$requestId", request.RequestId);
            set.Parameters.AddWithValue("$attachedAt", request.Now.ToUnixTimeMilliseconds());
            set.ExecuteNonQuery();
        }

        foreach (var item in planned)
        {
            using var reference = connection.CreateCommand();
            reference.Transaction = transaction;
            reference.CommandText = """
                INSERT INTO blob_build_payload_references(
                    matrix_build_id, sha256, declared_size, source_staging_id)
                VALUES ($matrixBuildId, $sha256, $size, $stagingId);
                """;
            reference.Parameters.AddWithValue("$matrixBuildId", request.MatrixBuildId);
            reference.Parameters.AddWithValue("$sha256", item.Sha256);
            reference.Parameters.AddWithValue("$size", item.Size);
            reference.Parameters.AddWithValue("$stagingId", request.StagingId);
            reference.ExecuteNonQuery();
        }

        return BlobBuildAttachmentOutcome.Attached;
    }

    public BlobArtifactAttachmentOutcome Attach(
        SqliteConnection connection,
        SqliteTransaction transaction,
        BlobArtifactAttachmentRequest request)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(request);
        BlobAccessValidation.RequireBounded(request.BuildId, 256, "build ID");
        BlobAccessValidation.RequireBounded(request.AgentId, 256, "Agent ID");
        BlobAccessValidation.RequireBounded(request.OwnerSessionId, 256, "owner session ID");
        if (request.ConnectionGeneration <= 0)
        {
            throw Validation("blob_connection_generation_invalid", "Connection generation must be positive.");
        }

        var artifacts = BlobAccessValidation.NormalizeArtifacts(request.Artifacts);
        RequireCurrentBuildOwner(connection, transaction, request);
        foreach (var artifact in artifacts)
        {
            RequireArtifactReceipt(connection, transaction, request, artifact);
        }

        var existing = ReadArtifactSet(connection, transaction, request.BuildId);
        if (existing is not null)
        {
            if (ArtifactSetMatches(existing, request) &&
                ReadArtifactReferences(connection, transaction, request.BuildId)
                    .SequenceEqual(artifacts))
            {
                return BlobArtifactAttachmentOutcome.ExactReplay;
            }

            throw Conflict(
                "blob_artifact_manifest_conflict",
                "A different artifact manifest is already attached to this build.");
        }

        using (var set = connection.CreateCommand())
        {
            set.Transaction = transaction;
            set.CommandText = """
                INSERT INTO blob_build_artifact_sets(
                    build_id, agent_id, owner_session_id, connection_generation, attached_unix_ms)
                VALUES ($buildId, $agentId, $sessionId, $generation, $attachedAt);
                """;
            set.Parameters.AddWithValue("$buildId", request.BuildId);
            set.Parameters.AddWithValue("$agentId", request.AgentId);
            set.Parameters.AddWithValue("$sessionId", request.OwnerSessionId);
            set.Parameters.AddWithValue("$generation", request.ConnectionGeneration);
            set.Parameters.AddWithValue("$attachedAt", request.Now.ToUnixTimeMilliseconds());
            set.ExecuteNonQuery();
        }

        foreach (var artifact in artifacts)
        {
            using var reference = connection.CreateCommand();
            reference.Transaction = transaction;
            reference.CommandText = """
                INSERT INTO blob_build_artifact_references(
                    build_id, artifact_id, relative_path, sha256, declared_size,
                    source_agent_id, source_session_id, source_connection_generation,
                    attached_unix_ms)
                VALUES (
                    $buildId, $artifactId, $relativePath, $sha256, $size,
                    $agentId, $sessionId, $generation, $attachedAt);
                """;
            reference.Parameters.AddWithValue("$buildId", request.BuildId);
            reference.Parameters.AddWithValue("$artifactId", artifact.ArtifactId);
            reference.Parameters.AddWithValue("$relativePath", artifact.RelativePath);
            reference.Parameters.AddWithValue("$sha256", artifact.Sha256);
            reference.Parameters.AddWithValue("$size", artifact.Size);
            reference.Parameters.AddWithValue("$agentId", request.AgentId);
            reference.Parameters.AddWithValue("$sessionId", request.OwnerSessionId);
            reference.Parameters.AddWithValue("$generation", request.ConnectionGeneration);
            reference.Parameters.AddWithValue("$attachedAt", request.Now.ToUnixTimeMilliseconds());
            reference.ExecuteNonQuery();
        }

        return BlobArtifactAttachmentOutcome.Attached;
    }

    internal Task<bool> CanReadAssignmentAsync(BlobAssignmentReadRequest request) =>
        database.ReadAsync(connection =>
        {
            BlobAccessValidation.RequireBounded(request.AgentId, 256, "Agent ID");
            BlobAccessValidation.RequireBounded(request.SessionId, 256, "session ID");
            BlobAccessValidation.RequireBounded(request.BuildId, 256, "build ID");
            BlobAccessValidation.ValidateSha256(request.Sha256);
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT 1
                FROM builds b
                JOIN matrix_build_cells c ON c.build_id = b.build_id
                JOIN blob_build_payload_references r
                  ON r.matrix_build_id = c.matrix_build_id
                WHERE b.build_id = $buildId
                  AND b.agent_id = $agentId
                  AND b.owner_session_id = $sessionId
                  AND b.state IN ('RUNNING', 'CANCEL_REQUESTED')
                  AND (b.reconnect_deadline_unix_ms IS NULL
                       OR b.reconnect_deadline_unix_ms > $now)
                  AND r.sha256 = $sha256;
                """;
            command.Parameters.AddWithValue("$buildId", request.BuildId);
            command.Parameters.AddWithValue("$agentId", request.AgentId);
            command.Parameters.AddWithValue("$sessionId", request.SessionId);
            command.Parameters.AddWithValue("$now", request.Now.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$sha256", request.Sha256);
            return command.ExecuteScalar() is not null;
        });

    internal Task<BlobArtifactUploadGrant?> StageArtifactUploadAsync(
        BlobArtifactUploadRequest request,
        DateTimeOffset expiresAt) => database.WriteAsync(connection =>
    {
        BlobAccessValidation.RequireBounded(request.AgentId, 256, "Agent ID");
        BlobAccessValidation.RequireBounded(request.SessionId, 256, "session ID");
        BlobAccessValidation.RequireBounded(request.BuildId, 256, "build ID");
        BlobAccessValidation.ValidateDescriptor(new BlobDescriptor(request.Sha256, request.Size));
        using var transaction = connection.BeginTransaction();
        var generation = ReadCurrentBuildGeneration(connection, transaction, request);
        if (generation is null)
        {
            transaction.Rollback();
            return null;
        }

        using var existing = connection.CreateCommand();
        existing.Transaction = transaction;
        existing.CommandText = """
            SELECT declared_size, expires_unix_ms
            FROM blob_artifact_upload_staging
            WHERE build_id = $buildId AND sha256 = $sha256
              AND agent_id = $agentId AND owner_session_id = $sessionId
              AND connection_generation = $generation;
            """;
        existing.Parameters.AddWithValue("$buildId", request.BuildId);
        existing.Parameters.AddWithValue("$sha256", request.Sha256);
        existing.Parameters.AddWithValue("$agentId", request.AgentId);
        existing.Parameters.AddWithValue("$sessionId", request.SessionId);
        existing.Parameters.AddWithValue("$generation", generation.Value);
        using var reader = existing.ExecuteReader();
        if (reader.Read())
        {
            var existingSize = reader.GetInt64(0);
            var existingExpiry = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1));
            reader.Close();
            if (existingSize != request.Size)
            {
                throw Conflict(
                    "blob_artifact_upload_conflict",
                    "The artifact upload was already staged with another declared size.");
            }

            transaction.Commit();
            return new BlobArtifactUploadGrant(
                request.BuildId,
                request.Sha256,
                request.Size,
                request.AgentId,
                request.SessionId,
                generation.Value,
                existingExpiry,
                Replayed: true);
        }

        reader.Close();
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO blob_artifact_upload_staging(
                build_id, sha256, declared_size, agent_id, owner_session_id,
                connection_generation, created_unix_ms, expires_unix_ms)
            VALUES (
                $buildId, $sha256, $size, $agentId, $sessionId,
                $generation, $createdAt, $expiresAt);
            """;
        insert.Parameters.AddWithValue("$buildId", request.BuildId);
        insert.Parameters.AddWithValue("$sha256", request.Sha256);
        insert.Parameters.AddWithValue("$size", request.Size);
        insert.Parameters.AddWithValue("$agentId", request.AgentId);
        insert.Parameters.AddWithValue("$sessionId", request.SessionId);
        insert.Parameters.AddWithValue("$generation", generation.Value);
        insert.Parameters.AddWithValue("$createdAt", request.Now.ToUnixTimeMilliseconds());
        insert.Parameters.AddWithValue("$expiresAt", expiresAt.ToUnixTimeMilliseconds());
        insert.ExecuteNonQuery();
        transaction.Commit();
        return new BlobArtifactUploadGrant(
            request.BuildId,
            request.Sha256,
            request.Size,
            request.AgentId,
            request.SessionId,
            generation.Value,
            expiresAt,
            Replayed: false);
    });

    internal Task<BlobUploadOutcome> CompleteArtifactUploadAsync(
        ManagementRequestContext context,
        BlobArtifactUploadGrant grant,
        DateTimeOffset now) => database.WriteAsync(connection =>
    {
        using var transaction = connection.BeginTransaction();
        var current = ReadArtifactStaging(connection, transaction, grant);
        if (current is null || current.Value.ExpiresAt <= now)
        {
            throw Expired("blob_artifact_upload_expired");
        }

        if (current.Value.Received)
        {
            AuditEventStore.Append(
                connection,
                transaction,
                AuditEventDraft.Create(
                    context,
                    now,
                    "blob-artifact.upload",
                    "build",
                    grant.BuildId,
                    AuditOutcome.NoChange,
                    "exact_replay"));
            transaction.Commit();
            return BlobUploadOutcome.ExactReplay;
        }

        using var receipt = connection.CreateCommand();
        receipt.Transaction = transaction;
        receipt.CommandText = """
            INSERT INTO blob_artifact_upload_receipts(
                build_id, sha256, declared_size, agent_id, owner_session_id,
                connection_generation, received_unix_ms)
            VALUES (
                $buildId, $sha256, $size, $agentId, $sessionId,
                $generation, $receivedAt);
            """;
        receipt.Parameters.AddWithValue("$buildId", grant.BuildId);
        receipt.Parameters.AddWithValue("$sha256", grant.Sha256);
        receipt.Parameters.AddWithValue("$size", grant.Size);
        receipt.Parameters.AddWithValue("$agentId", grant.AgentId);
        receipt.Parameters.AddWithValue("$sessionId", grant.OwnerSessionId);
        receipt.Parameters.AddWithValue("$generation", grant.ConnectionGeneration);
        receipt.Parameters.AddWithValue("$receivedAt", now.ToUnixTimeMilliseconds());
        receipt.ExecuteNonQuery();
        AuditEventStore.Append(
            connection,
            transaction,
            AuditEventDraft.Create(
                context,
                now,
                "blob-artifact.upload",
                "build",
                grant.BuildId));
        transaction.Commit();
        return BlobUploadOutcome.Uploaded;
    });

    internal Task<BlobDescriptor?> ResolveHumanArtifactAsync(
        BlobHumanArtifactReadRequest request) => database.ReadAsync(connection =>
    {
        BlobAccessValidation.RequireBounded(request.BuildId, 256, "build ID");
        BlobAccessValidation.RequireBounded(request.ArtifactId, 128, "artifact ID");
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sha256, declared_size
            FROM blob_build_artifact_references
            WHERE build_id = $buildId AND artifact_id = $artifactId;
            """;
        command.Parameters.AddWithValue("$buildId", request.BuildId);
        command.Parameters.AddWithValue("$artifactId", request.ArtifactId);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new BlobDescriptor(reader.GetString(0), reader.GetInt64(1))
            : null;
    });

    private static void InsertUploadReceipt(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string stagingId,
        BlobDescriptor item,
        DateTimeOffset receivedAt)
    {
        using var receipt = connection.CreateCommand();
        receipt.Transaction = transaction;
        receipt.CommandText = """
            INSERT INTO blob_upload_receipts(
                staging_id, sha256, declared_size, received_unix_ms)
            VALUES ($stagingId, $sha256, $size, $receivedAt);
            """;
        receipt.Parameters.AddWithValue("$stagingId", stagingId);
        receipt.Parameters.AddWithValue("$sha256", item.Sha256);
        receipt.Parameters.AddWithValue("$size", item.Size);
        receipt.Parameters.AddWithValue("$receivedAt", receivedAt.ToUnixTimeMilliseconds());
        receipt.ExecuteNonQuery();
    }

    private static StoredUploadItem? ReadUploadItem(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        ManagementPrincipal principal,
        string stagingId,
        string sha256)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT p.project_id, p.expires_unix_ms, i.declared_size,
                   CASE WHEN r.sha256 IS NULL THEN 0 ELSE 1 END
            FROM blob_upload_plans p
            JOIN blob_upload_plan_items i ON i.staging_id = p.staging_id
            LEFT JOIN blob_upload_receipts r
              ON r.staging_id = i.staging_id AND r.sha256 = i.sha256
            WHERE p.staging_id = $stagingId
              AND p.actor_type = $actorType
              AND p.actor_id = $actorId
              AND i.sha256 = $sha256;
            """;
        command.Parameters.AddWithValue("$stagingId", stagingId);
        command.Parameters.AddWithValue("$actorType", principal.ActorType);
        command.Parameters.AddWithValue("$actorId", principal.ActorId);
        command.Parameters.AddWithValue("$sha256", sha256);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new StoredUploadItem(
                stagingId,
                principal,
                reader.GetString(0),
                sha256,
                reader.GetInt64(2),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)),
                reader.GetInt64(3) != 0)
            : null;
    }

    private static (string StagingId, string RequestHash)? ReadPlanByRequest(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ManagementPrincipal principal,
        string operationKind,
        string requestId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT staging_id, request_hash
            FROM blob_upload_plans
            WHERE actor_type = $actorType AND actor_id = $actorId
              AND operation_kind = $operationKind AND request_id = $requestId;
            """;
        command.Parameters.AddWithValue("$actorType", principal.ActorType);
        command.Parameters.AddWithValue("$actorId", principal.ActorId);
        command.Parameters.AddWithValue("$operationKind", operationKind);
        command.Parameters.AddWithValue("$requestId", requestId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? (reader.GetString(0), reader.GetString(1)) : null;
    }

    private static BlobUploadPlan ReadPlan(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string stagingId,
        bool replayed)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT p.project_id, p.expires_unix_ms, i.sha256, i.declared_size,
                   CASE WHEN r.sha256 IS NULL THEN 1 ELSE 0 END
            FROM blob_upload_plans p
            JOIN blob_upload_plan_items i ON i.staging_id = p.staging_id
            LEFT JOIN blob_upload_receipts r
              ON r.staging_id = i.staging_id AND r.sha256 = i.sha256
            WHERE p.staging_id = $stagingId
            ORDER BY i.sha256 COLLATE BINARY;
            """;
        command.Parameters.AddWithValue("$stagingId", stagingId);
        using var reader = command.ExecuteReader();
        var items = new List<BlobUploadPlanItem>();
        string? projectId = null;
        DateTimeOffset expiresAt = default;
        while (reader.Read())
        {
            projectId ??= reader.GetString(0);
            expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1));
            var sha256 = reader.GetString(2);
            items.Add(new BlobUploadPlanItem(
                sha256,
                reader.GetInt64(3),
                reader.GetInt64(4) != 0,
                $"/blobs/{sha256}"));
        }

        return projectId is null
            ? throw new InvalidOperationException("blob upload plan has no items")
            : new BlobUploadPlan(stagingId, projectId, expiresAt, items, replayed);
    }

    private static (ManagementPrincipal Principal, string ProjectId, DateTimeOffset ExpiresAt)?
        ReadPlanOwner(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string stagingId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT actor_type, actor_id, project_id, expires_unix_ms
            FROM blob_upload_plans WHERE staging_id = $stagingId;
            """;
        command.Parameters.AddWithValue("$stagingId", stagingId);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? (new ManagementPrincipal(
                    reader.GetString(0),
                    reader.GetString(1),
                    "blob-staging-owner",
                    LegacyScope: null),
                reader.GetString(2),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)))
            : null;
    }

    private static IReadOnlyList<BlobDescriptor> ReadPlanDescriptors(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string stagingId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT sha256, declared_size FROM blob_upload_plan_items
            WHERE staging_id = $stagingId ORDER BY sha256 COLLATE BINARY;
            """;
        command.Parameters.AddWithValue("$stagingId", stagingId);
        using var reader = command.ExecuteReader();
        var result = new List<BlobDescriptor>();
        while (reader.Read())
        {
            result.Add(new BlobDescriptor(reader.GetString(0), reader.GetInt64(1)));
        }

        return result;
    }

    private static void RequireAllUploadReceipts(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string stagingId,
        IReadOnlyList<BlobDescriptor> planned)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*) FROM blob_upload_receipts WHERE staging_id = $stagingId;
            """;
        command.Parameters.AddWithValue("$stagingId", stagingId);
        if (Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) !=
            planned.Count)
        {
            throw Conflict(
                "blob_staging_incomplete",
                "Every staged payload must be uploaded before build submission.");
        }
    }

    private static PayloadSet? ReadPayloadSetByStaging(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string stagingId) => ReadPayloadSet(connection, transaction, "staging_id", stagingId);

    private static PayloadSet? ReadPayloadSetByBuild(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string matrixBuildId) => ReadPayloadSet(connection, transaction, "matrix_build_id", matrixBuildId);

    private static PayloadSet? ReadPayloadSet(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string column,
        string value)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT matrix_build_id, staging_id, actor_type, actor_id, project_id,
                   operation_kind, request_id
            FROM blob_build_payload_sets WHERE {column} = $value;
            """;
        command.Parameters.AddWithValue("$value", value);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new PayloadSet(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6))
            : null;
    }

    private static bool PayloadSetMatches(PayloadSet set, BlobBuildAttachmentRequest request) =>
        string.Equals(set.MatrixBuildId, request.MatrixBuildId, StringComparison.Ordinal) &&
        string.Equals(set.StagingId, request.StagingId, StringComparison.Ordinal) &&
        string.Equals(set.ActorType, request.Principal.ActorType, StringComparison.Ordinal) &&
        string.Equals(set.ActorId, request.Principal.ActorId, StringComparison.Ordinal) &&
        string.Equals(set.ProjectId, request.ProjectId, StringComparison.Ordinal) &&
        string.Equals(set.OperationKind, request.OperationKind, StringComparison.Ordinal) &&
        string.Equals(set.RequestId, request.RequestId, StringComparison.Ordinal);

    private static IReadOnlyList<string> ReadPayloadReferenceHashes(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string matrixBuildId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT sha256 FROM blob_build_payload_references
            WHERE matrix_build_id = $matrixBuildId ORDER BY sha256 COLLATE BINARY;
            """;
        command.Parameters.AddWithValue("$matrixBuildId", matrixBuildId);
        using var reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read())
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    private static void RequireCurrentBuildOwner(
        SqliteConnection connection,
        SqliteTransaction transaction,
        BlobArtifactAttachmentRequest request)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT 1
            FROM builds b JOIN agents a ON a.agent_id = b.agent_id
            WHERE b.build_id = $buildId AND b.agent_id = $agentId
              AND b.owner_session_id = $sessionId
              AND b.state IN ('RUNNING', 'CANCEL_REQUESTED')
              AND a.connection_generation = $generation;
            """;
        command.Parameters.AddWithValue("$buildId", request.BuildId);
        command.Parameters.AddWithValue("$agentId", request.AgentId);
        command.Parameters.AddWithValue("$sessionId", request.OwnerSessionId);
        command.Parameters.AddWithValue("$generation", request.ConnectionGeneration);
        if (command.ExecuteScalar() is null)
        {
            throw Conflict(
                "blob_artifact_owner_conflict",
                "The terminal artifact manifest is not owned by the current Agent session.");
        }
    }

    private static void RequireArtifactReceipt(
        SqliteConnection connection,
        SqliteTransaction transaction,
        BlobArtifactAttachmentRequest request,
        BlobArtifactAttachment artifact)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT 1 FROM blob_artifact_upload_receipts
            WHERE build_id = $buildId AND sha256 = $sha256 AND declared_size = $size
              AND agent_id = $agentId AND owner_session_id = $sessionId
              AND connection_generation = $generation;
            """;
        command.Parameters.AddWithValue("$buildId", request.BuildId);
        command.Parameters.AddWithValue("$sha256", artifact.Sha256);
        command.Parameters.AddWithValue("$size", artifact.Size);
        command.Parameters.AddWithValue("$agentId", request.AgentId);
        command.Parameters.AddWithValue("$sessionId", request.OwnerSessionId);
        command.Parameters.AddWithValue("$generation", request.ConnectionGeneration);
        if (command.ExecuteScalar() is null)
        {
            throw Conflict(
                "blob_artifact_receipt_missing",
                "The artifact manifest contains an upload not owned by this Agent session.");
        }
    }

    private static ArtifactSet? ReadArtifactSet(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string buildId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT agent_id, owner_session_id, connection_generation
            FROM blob_build_artifact_sets WHERE build_id = $buildId;
            """;
        command.Parameters.AddWithValue("$buildId", buildId);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new ArtifactSet(reader.GetString(0), reader.GetString(1), reader.GetInt64(2))
            : null;
    }

    private static bool ArtifactSetMatches(
        ArtifactSet set,
        BlobArtifactAttachmentRequest request) =>
        string.Equals(set.AgentId, request.AgentId, StringComparison.Ordinal) &&
        string.Equals(set.SessionId, request.OwnerSessionId, StringComparison.Ordinal) &&
        set.ConnectionGeneration == request.ConnectionGeneration;

    private static IReadOnlyList<BlobArtifactAttachment> ReadArtifactReferences(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string buildId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT artifact_id, relative_path, sha256, declared_size
            FROM blob_build_artifact_references
            WHERE build_id = $buildId ORDER BY artifact_id COLLATE BINARY;
            """;
        command.Parameters.AddWithValue("$buildId", buildId);
        using var reader = command.ExecuteReader();
        var result = new List<BlobArtifactAttachment>();
        while (reader.Read())
        {
            result.Add(new BlobArtifactAttachment(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3)));
        }

        return result;
    }

    private static long? ReadCurrentBuildGeneration(
        SqliteConnection connection,
        SqliteTransaction transaction,
        BlobArtifactUploadRequest request)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT a.connection_generation
            FROM builds b JOIN agents a ON a.agent_id = b.agent_id
            WHERE b.build_id = $buildId AND b.agent_id = $agentId
              AND b.owner_session_id = $sessionId
              AND b.state IN ('RUNNING', 'CANCEL_REQUESTED')
              AND (b.reconnect_deadline_unix_ms IS NULL
                   OR b.reconnect_deadline_unix_ms > $now);
            """;
        command.Parameters.AddWithValue("$buildId", request.BuildId);
        command.Parameters.AddWithValue("$agentId", request.AgentId);
        command.Parameters.AddWithValue("$sessionId", request.SessionId);
        command.Parameters.AddWithValue("$now", request.Now.ToUnixTimeMilliseconds());
        var value = command.ExecuteScalar();
        return value is null ? null : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static (DateTimeOffset ExpiresAt, bool Received)? ReadArtifactStaging(
        SqliteConnection connection,
        SqliteTransaction transaction,
        BlobArtifactUploadGrant grant)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT s.expires_unix_ms, CASE WHEN r.sha256 IS NULL THEN 0 ELSE 1 END
            FROM blob_artifact_upload_staging s
            LEFT JOIN blob_artifact_upload_receipts r
              ON r.build_id = s.build_id AND r.sha256 = s.sha256
             AND r.agent_id = s.agent_id AND r.owner_session_id = s.owner_session_id
             AND r.connection_generation = s.connection_generation
            WHERE s.build_id = $buildId AND s.sha256 = $sha256
              AND s.declared_size = $size AND s.agent_id = $agentId
              AND s.owner_session_id = $sessionId
              AND s.connection_generation = $generation;
            """;
        command.Parameters.AddWithValue("$buildId", grant.BuildId);
        command.Parameters.AddWithValue("$sha256", grant.Sha256);
        command.Parameters.AddWithValue("$size", grant.Size);
        command.Parameters.AddWithValue("$agentId", grant.AgentId);
        command.Parameters.AddWithValue("$sessionId", grant.OwnerSessionId);
        command.Parameters.AddWithValue("$generation", grant.ConnectionGeneration);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? (DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0)), reader.GetInt64(1) != 0)
            : null;
    }

    private static BlobAccessException Validation(string code, string message) =>
        new(BlobAccessFailure.Validation, code, message);

    private static BlobAccessException NotFound() =>
        new(
            BlobAccessFailure.NotFound,
            "blob_staging_not_found",
            "The blob staging resource does not exist or is not visible to this principal.");

    private static BlobAccessException Expired(string code = "blob_staging_expired") =>
        new(BlobAccessFailure.Expired, code, "The blob staging resource has expired.");

    private static BlobAccessException Conflict(string code, string message) =>
        new(BlobAccessFailure.Conflict, code, message);

    private sealed record PayloadSet(
        string MatrixBuildId,
        string StagingId,
        string ActorType,
        string ActorId,
        string ProjectId,
        string OperationKind,
        string RequestId);

    private sealed record ArtifactSet(
        string AgentId,
        string SessionId,
        long ConnectionGeneration);
}

internal static class BlobAccessValidation
{
    public static void ValidatePrincipal(ManagementPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        RequireBounded(principal.ActorType, 32, "actor type");
        RequireBounded(principal.ActorId, 256, "actor ID");
    }

    public static string ValidateProjectId(string value)
    {
        RequireBounded(value, 256, "project ID");
        if (value != value.Trim() || value.Any(char.IsControl))
        {
            throw Validation("blob_project_id_invalid", "Project ID is not canonical.");
        }

        return value;
    }

    public static IReadOnlyList<BlobDescriptor> NormalizeDescriptors(
        IReadOnlyList<BlobDescriptor> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count is < 1 or > BlobAccessLimits.MaximumPlanItems)
        {
            throw Validation(
                "blob_plan_item_count_invalid",
                $"A blob upload plan requires 1-{BlobAccessLimits.MaximumPlanItems} items.");
        }

        var result = items.Select(ValidateDescriptor)
            .OrderBy(item => item.Sha256, StringComparer.Ordinal)
            .ToArray();
        if (result.Select(item => item.Sha256).Distinct(StringComparer.Ordinal).Count() != result.Length)
        {
            throw Validation("blob_plan_duplicate_hash", "A blob upload plan cannot repeat a digest.");
        }

        long total = 0;
        foreach (var item in result)
        {
            try
            {
                total = checked(total + item.Size);
            }
            catch (OverflowException)
            {
                throw Validation("blob_plan_size_limit", "The blob upload plan exceeds its byte limit.");
            }
        }

        if (total > BlobAccessLimits.MaximumPlanBytes)
        {
            throw Validation("blob_plan_size_limit", "The blob upload plan exceeds its byte limit.");
        }

        return result;
    }

    public static BlobDescriptor ValidateDescriptor(BlobDescriptor item)
    {
        ArgumentNullException.ThrowIfNull(item);
        ValidateSha256(item.Sha256);
        if (item.Size is < 0 or > BlobAccessLimits.MaximumBlobBytes)
        {
            throw Validation(
                "blob_size_invalid",
                $"Blob size must be between 0 and {BlobAccessLimits.MaximumBlobBytes} bytes.");
        }

        return item;
    }

    public static void ValidateSha256(string value)
    {
        if (value is not { Length: 64 } ||
            value.Any(character =>
                !(character is >= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw Validation(
                "blob_sha256_invalid",
                "Blob SHA-256 must contain exactly 64 lowercase hexadecimal characters.");
        }
    }

    public static IReadOnlyList<string> NormalizeDistinctHashes(IReadOnlyList<string> hashes)
    {
        ArgumentNullException.ThrowIfNull(hashes);
        if (hashes.Count is < 1 or > BlobAccessLimits.MaximumPlanItems)
        {
            throw Validation(
                "blob_attachment_count_invalid",
                $"A build requires 1-{BlobAccessLimits.MaximumPlanItems} distinct payload hashes.");
        }

        foreach (var hash in hashes)
        {
            ValidateSha256(hash);
        }

        var result = hashes.Order(StringComparer.Ordinal).ToArray();
        if (result.Distinct(StringComparer.Ordinal).Count() != result.Length)
        {
            throw Validation(
                "blob_attachment_hashes_not_distinct",
                "Build attachment hashes must already be distinct.");
        }

        return result;
    }

    public static IReadOnlyList<BlobArtifactAttachment> NormalizeArtifacts(
        IReadOnlyList<BlobArtifactAttachment> artifacts)
    {
        ArgumentNullException.ThrowIfNull(artifacts);
        if (artifacts.Count > BlobAccessLimits.MaximumPlanItems)
        {
            throw Validation(
                "blob_artifact_count_invalid",
                $"A build may attach at most {BlobAccessLimits.MaximumPlanItems} artifacts.");
        }

        var result = artifacts.OrderBy(artifact => artifact.ArtifactId, StringComparer.Ordinal).ToArray();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var paths = new HashSet<string>(StringComparer.Ordinal);
        long total = 0;
        foreach (var artifact in result)
        {
            RequireBounded(artifact.ArtifactId, 128, "artifact ID");
            RequireBounded(artifact.RelativePath, 1024, "artifact path");
            if (!IsSafeRelativePath(artifact.RelativePath))
            {
                throw Validation(
                    "blob_artifact_path_invalid",
                    "Artifact paths must be canonical relative forward-slash paths.");
            }

            ValidateDescriptor(new BlobDescriptor(artifact.Sha256, artifact.Size));
            if (!ids.Add(artifact.ArtifactId) || !paths.Add(artifact.RelativePath))
            {
                throw Validation(
                    "blob_artifact_duplicate",
                    "Artifact IDs and paths must be unique within one build result.");
            }

            total = checked(total + artifact.Size);
        }

        if (total > BlobAccessLimits.MaximumPlanBytes)
        {
            throw Validation("blob_artifact_size_limit", "The artifact manifest exceeds its byte limit.");
        }

        return result;
    }

    public static void RequireBounded(string value, int maximum, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum)
        {
            throw Validation(
                "blob_input_invalid",
                $"{field} must contain 1-{maximum} characters.");
        }
    }

    private static bool IsSafeRelativePath(string value) =>
        !value.StartsWith("/", StringComparison.Ordinal) &&
        !value.Contains('\\') &&
        !value.Any(char.IsControl) &&
        value.Split('/').All(segment => segment.Length > 0 && segment is not "." and not "..");

    private static BlobAccessException Validation(string code, string message) =>
        new(BlobAccessFailure.Validation, code, message);
}
