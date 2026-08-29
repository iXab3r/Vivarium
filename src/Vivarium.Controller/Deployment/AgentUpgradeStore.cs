using Microsoft.Data.Sqlite;
using Vivarium.Controller.Auditing;
using Vivarium.Controller.Persistence;
using Vivarium.Controller.Security;

namespace Vivarium.Controller.Deployment;

public sealed class AgentUpgradeStore(VivariumDatabase database, TimeProvider timeProvider)
{
    private static readonly AgentUpgradeState[] CoordinatedStates =
    [
        AgentUpgradeState.Draining,
        AgentUpgradeState.HandoffReady,
        AgentUpgradeState.AwaitingHealth,
        AgentUpgradeState.CommitPending,
        AgentUpgradeState.Finalizing,
        AgentUpgradeState.RollbackRequested,
    ];

    public Task<AgentUpgradeCreation?> FindReplayAsync(
        ManagementRequestContext context,
        string requestId,
        string requestHash) => database.ReadAsync(connection =>
    {
        var replay = FindByRequest(
            connection, null, context.Principal.ActorType, context.Principal.ActorId, requestId);
        if (replay is null)
        {
            return null;
        }

        if (!string.Equals(replay.Value.RequestHash, requestHash, StringComparison.Ordinal))
        {
            throw new AgentUpgradeException(
                "idempotency_key_reused",
                "The Idempotency-Key was already used for a different Agent upgrade request.");
        }

        return new AgentUpgradeCreation(Read(connection, null, replay.Value.OperationId)!, true);
    });

    public Task<AgentUpgradeCreation> CreateAsync(
        ManagementRequestContext context,
        AgentPackage package,
        string agentId,
        string requestId,
        string requestHash,
        string reason,
        string? priorPackageSha256,
        long startingConnectionGeneration,
        TimeSpan timeout)
    {
        var now = timeProvider.GetUtcNow();
        return database.WriteAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            var replay = FindByRequest(
                connection, transaction, context.Principal.ActorType,
                context.Principal.ActorId, requestId);
            if (replay is not null)
            {
                if (!string.Equals(replay.Value.RequestHash, requestHash, StringComparison.Ordinal))
                {
                    throw new AgentUpgradeException(
                        "idempotency_key_reused",
                        "The Idempotency-Key was already used for a different Agent upgrade request.");
                }

                transaction.Commit();
                return new AgentUpgradeCreation(Read(connection, null, replay.Value.OperationId)!, true);
            }

            using (var knownAgent = connection.CreateCommand())
            {
                knownAgent.Transaction = transaction;
                knownAgent.CommandText = "SELECT 1 FROM agents WHERE agent_id = $agentId;";
                knownAgent.Parameters.AddWithValue("$agentId", agentId);
                if (knownAgent.ExecuteScalar() is null)
                {
                    throw new AgentUpgradeException(
                        "agent_not_found", $"Agent '{agentId}' does not exist.",
                        StatusCodes.Status404NotFound);
                }
            }

            var active = FindCoordinated(connection, transaction, agentId);
            if (active is not null)
            {
                throw new AgentUpgradeException(
                    "agent_upgrade_already_active",
                    $"Agent '{agentId}' already has active upgrade '{active.OperationId}'.");
            }

            using (var existingDrain = connection.CreateCommand())
            {
                existingDrain.Transaction = transaction;
                existingDrain.CommandText =
                    "SELECT operation_id FROM agent_maintenance_drains WHERE agent_id = $agentId;";
                existingDrain.Parameters.AddWithValue("$agentId", agentId);
                if (existingDrain.ExecuteScalar() is string blockingOperation)
                {
                    throw new AgentUpgradeException(
                        "agent_maintenance_drain_active",
                        $"Agent '{agentId}' remains drained by operation '{blockingOperation}'.");
                }
            }

            long fence;
            using (var nextFence = connection.CreateCommand())
            {
                nextFence.Transaction = transaction;
                nextFence.CommandText = """
                    SELECT COALESCE(MAX(maintenance_fence), 0) + 1
                    FROM agent_upgrade_operations WHERE agent_id = $agentId;
                    """;
                nextFence.Parameters.AddWithValue("$agentId", agentId);
                fence = Convert.ToInt64(nextFence.ExecuteScalar());
            }

            var operationId = Guid.NewGuid().ToString("N");
            var deadline = now.Add(timeout);
            using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO agent_upgrade_operations(
                        operation_id, agent_id, package_id, state,
                        actor_type, actor_id, credential_kind, request_id, request_hash,
                        correlation_id, reason, maintenance_fence, prior_package_sha256,
                        starting_connection_generation, observed_connection_generation,
                        restart_attempts, last_dispatch_connection_generation, next_restart_unix_ms,
                        cancellation_reason, failure_code, result_package_sha256,
                        created_unix_ms, updated_unix_ms, deadline_unix_ms, completed_unix_ms)
                    VALUES (
                        $operationId, $agentId, $packageId, 'DRAINING',
                        $actorType, $actorId, $credentialKind, $requestId, $requestHash,
                        $correlationId, $reason, $fence, $priorDigest,
                        $startingGeneration, NULL,
                        0, NULL, NULL, '', '', NULL,
                        $created, $updated, $deadline, NULL);
                    """;
                insert.Parameters.AddWithValue("$operationId", operationId);
                insert.Parameters.AddWithValue("$agentId", agentId);
                insert.Parameters.AddWithValue("$packageId", package.PackageId);
                insert.Parameters.AddWithValue("$actorType", context.Principal.ActorType);
                insert.Parameters.AddWithValue("$actorId", context.Principal.ActorId);
                insert.Parameters.AddWithValue("$credentialKind", context.Principal.CredentialKind);
                insert.Parameters.AddWithValue("$requestId", requestId);
                insert.Parameters.AddWithValue("$requestHash", requestHash);
                insert.Parameters.AddWithValue("$correlationId", context.CorrelationId);
                insert.Parameters.AddWithValue("$reason", reason);
                insert.Parameters.AddWithValue("$fence", fence);
                insert.Parameters.AddWithValue("$priorDigest", (object?)priorPackageSha256 ?? DBNull.Value);
                insert.Parameters.AddWithValue("$startingGeneration", startingConnectionGeneration);
                insert.Parameters.AddWithValue("$created", now.ToUnixTimeMilliseconds());
                insert.Parameters.AddWithValue("$updated", now.ToUnixTimeMilliseconds());
                insert.Parameters.AddWithValue("$deadline", deadline.ToUnixTimeMilliseconds());
                insert.ExecuteNonQuery();
            }

            using (var drain = connection.CreateCommand())
            {
                drain.Transaction = transaction;
                drain.CommandText = """
                    INSERT INTO agent_maintenance_drains(
                        agent_id, operation_id, fence, reason, acquired_unix_ms)
                    VALUES ($agentId, $operationId, $fence, 'agent-upgrade', $created);
                    """;
                drain.Parameters.AddWithValue("$agentId", agentId);
                drain.Parameters.AddWithValue("$operationId", operationId);
                drain.Parameters.AddWithValue("$fence", fence);
                drain.Parameters.AddWithValue("$created", now.ToUnixTimeMilliseconds());
                drain.ExecuteNonQuery();
            }

            AppendEvent(
                connection, transaction, operationId, "draining", "requested",
                startingConnectionGeneration > 0 ? startingConnectionGeneration : null,
                package.Sha256,
                now);
            AuditEventStore.Append(connection, transaction, AuditEventDraft.Create(
                context,
                now,
                "agent.upgrade.request",
                "agent-upgrade",
                operationId,
                details: new Dictionary<string, string>
                {
                    ["agent_id"] = agentId,
                    ["package_id"] = package.PackageId,
                    ["package_sha256"] = package.Sha256,
                    ["maintenance_fence"] = fence.ToString(
                        global::System.Globalization.CultureInfo.InvariantCulture),
                }));
            transaction.Commit();
            return new AgentUpgradeCreation(Read(connection, null, operationId)!, false);
        });
    }

    public Task<AgentUpgradeOperation?> FindAsync(string operationId) =>
        database.ReadAsync(connection => Read(connection, null, operationId));

    public Task<AgentUpgradeOperation?> FindActiveAsync(string agentId) =>
        database.ReadAsync(connection => FindCoordinated(connection, null, agentId));

    public Task<AgentUpgradeOperation?> FindDrainOwnerAsync(string agentId) => database.ReadAsync(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = SelectSql + """
             WHERE operations.agent_id = $agentId AND drains.operation_id IS NOT NULL
             ORDER BY operations.created_unix_ms DESC, operations.operation_id COLLATE BINARY
             LIMIT 1;
            """;
        command.Parameters.AddWithValue("$agentId", agentId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadOperation(reader) : null;
    });

    public Task<IReadOnlyList<AgentUpgradeOperation>> ListAsync(string? agentId = null) =>
        ListWhereAsync(agentId, coordinatedOnly: false);

    public Task<IReadOnlyList<AgentUpgradeOperation>> ListCoordinatedAsync() =>
        ListWhereAsync(agentId: null, coordinatedOnly: true);

    public Task<IReadOnlyList<AgentUpgradeEvent>> ListEventsAsync(string operationId) =>
        database.ReadAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT event_id, phase, code, connection_generation, package_sha256, created_unix_ms
                FROM (
                    SELECT event_id, phase, code, connection_generation, package_sha256, created_unix_ms
                    FROM agent_upgrade_events
                    WHERE operation_id = $operationId
                    ORDER BY event_id DESC
                    LIMIT 256
                )
                ORDER BY event_id;
                """;
            command.Parameters.AddWithValue("$operationId", operationId);
            using var reader = command.ExecuteReader();
            var events = new List<AgentUpgradeEvent>();
            while (reader.Read())
            {
                events.Add(new AgentUpgradeEvent(
                    reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetInt64(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5))));
            }

            return (IReadOnlyList<AgentUpgradeEvent>)events;
        });

    public Task<bool> IsDrainedAsync(string agentId) => database.ReadAsync(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM agent_maintenance_drains WHERE agent_id = $agentId;";
        command.Parameters.AddWithValue("$agentId", agentId);
        return command.ExecuteScalar() is not null;
    });

    public Task<bool> PrepareHandoffAsync(
        string operationId,
        long expectedFence,
        string priorPackageSha256,
        long startingConnectionGeneration) => database.WriteAsync(connection =>
    {
        using var transaction = connection.BeginTransaction();
        var operation = Read(connection, transaction, operationId);
        if (operation is null || operation.MaintenanceFence != expectedFence ||
            operation.State != AgentUpgradeState.Draining)
        {
            transaction.Commit();
            return false;
        }

        var now = timeProvider.GetUtcNow();
        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE agent_upgrade_operations
            SET state = 'HANDOFF_READY', prior_package_sha256 = $priorDigest,
                starting_connection_generation = $startingGeneration,
                updated_unix_ms = $now
            WHERE operation_id = $operationId AND maintenance_fence = $fence
                AND state = 'DRAINING';
            """;
        update.Parameters.AddWithValue("$priorDigest", priorPackageSha256);
        update.Parameters.AddWithValue("$startingGeneration", startingConnectionGeneration);
        update.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        update.Parameters.AddWithValue("$operationId", operationId);
        update.Parameters.AddWithValue("$fence", expectedFence);
        var changed = update.ExecuteNonQuery() == 1;
        if (changed)
        {
            AppendEvent(
                connection, transaction, operationId, "handoff-ready", "live-prior-bound",
                startingConnectionGeneration, priorPackageSha256, now);
            AppendAudit(connection, transaction, operation, now, "agent.upgrade.handoff-ready");
        }

        transaction.Commit();
        return changed;
    });

    public Task<bool> RecordRestartDispatchAsync(
        string operationId,
        long expectedFence,
        long connectionGeneration,
        TimeSpan retryAfter) => database.WriteAsync(connection =>
    {
        using var transaction = connection.BeginTransaction();
        var operation = Read(connection, transaction, operationId);
        var now = timeProvider.GetUtcNow();
        if (operation is null || operation.MaintenanceFence != expectedFence ||
            operation.State is not (AgentUpgradeState.HandoffReady or AgentUpgradeState.AwaitingHealth) ||
            operation.LastDispatchConnectionGeneration == connectionGeneration &&
            operation.NextRestartAt is { } next && next > now)
        {
            transaction.Commit();
            return false;
        }

        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE agent_upgrade_operations
            SET state = 'AWAITING_HEALTH', restart_attempts = restart_attempts + 1,
                last_dispatch_connection_generation = $generation,
                next_restart_unix_ms = $nextRestart, updated_unix_ms = $now,
                completed_unix_ms = NULL
            WHERE operation_id = $operationId AND maintenance_fence = $fence;
            """;
        update.Parameters.AddWithValue("$generation", connectionGeneration);
        update.Parameters.AddWithValue("$nextRestart", now.Add(retryAfter).ToUnixTimeMilliseconds());
        update.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        update.Parameters.AddWithValue("$operationId", operationId);
        update.Parameters.AddWithValue("$fence", expectedFence);
        update.ExecuteNonQuery();
        AppendEvent(
            connection, transaction, operationId, "restart-dispatch", "scheduled",
            connectionGeneration, operation.Package.Sha256, now);
        AppendAudit(connection, transaction, operation, now, "agent.upgrade.restart-dispatched");
        transaction.Commit();
        return true;
    });

    public Task<bool> ObserveCandidateAsync(
        string operationId,
        long expectedFence,
        long connectionGeneration) => database.WriteAsync(connection =>
    {
        using var transaction = connection.BeginTransaction();
        var operation = Read(connection, transaction, operationId);
        if (operation is null || operation.MaintenanceFence != expectedFence ||
            operation.State != AgentUpgradeState.AwaitingHealth ||
            operation.ObservedConnectionGeneration == connectionGeneration)
        {
            transaction.Commit();
            return false;
        }

        var now = timeProvider.GetUtcNow();
        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE agent_upgrade_operations
            SET observed_connection_generation = $generation, updated_unix_ms = $now
            WHERE operation_id = $operationId AND maintenance_fence = $fence
                AND state = 'AWAITING_HEALTH';
            """;
        update.Parameters.AddWithValue("$generation", connectionGeneration);
        update.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        update.Parameters.AddWithValue("$operationId", operationId);
        update.Parameters.AddWithValue("$fence", expectedFence);
        var changed = update.ExecuteNonQuery() == 1;
        if (changed)
        {
            AppendEvent(
                connection, transaction, operationId, "probation", "candidate-connected",
                connectionGeneration, operation.Package.Sha256, now);
        }

        transaction.Commit();
        return changed;
    });

    public Task<bool> BeginCommitAsync(
        string operationId,
        long expectedFence,
        long connectionGeneration,
        string digest) => TransitionAsync(
            operationId,
            expectedFence,
            [AgentUpgradeState.AwaitingHealth],
            AgentUpgradeState.CommitPending,
            failureCode: null,
            resultDigest: digest,
            observedGeneration: connectionGeneration,
            releaseDrain: false,
            auditAction: "agent.upgrade.commit-pending",
            eventCode: "launcher-promoted");

    public Task<bool> BeginFinalizationAsync(
        string operationId,
        long expectedFence,
        long connectionGeneration,
        string digest) => TransitionAsync(
            operationId,
            expectedFence,
            [AgentUpgradeState.CommitPending],
            AgentUpgradeState.Finalizing,
            failureCode: null,
            resultDigest: digest,
            observedGeneration: connectionGeneration,
            releaseDrain: false,
            auditAction: "agent.upgrade.finalizing",
            eventCode: "commit-marker-recorded");

    public Task<bool> ObserveFinalizingSessionAsync(
        string operationId,
        long expectedFence,
        long connectionGeneration) => database.WriteAsync(connection =>
    {
        using var transaction = connection.BeginTransaction();
        var operation = Read(connection, transaction, operationId);
        if (operation is null || operation.MaintenanceFence != expectedFence ||
            operation.State != AgentUpgradeState.Finalizing ||
            operation.ObservedConnectionGeneration == connectionGeneration)
        {
            transaction.Commit();
            return false;
        }

        var now = timeProvider.GetUtcNow();
        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE agent_upgrade_operations
            SET observed_connection_generation = $generation, updated_unix_ms = $now
            WHERE operation_id = $operationId AND maintenance_fence = $fence
                AND state = 'FINALIZING';
            """;
        update.Parameters.AddWithValue("$generation", connectionGeneration);
        update.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        update.Parameters.AddWithValue("$operationId", operationId);
        update.Parameters.AddWithValue("$fence", expectedFence);
        var changed = update.ExecuteNonQuery() == 1;
        if (changed)
        {
            AppendEvent(
                connection, transaction, operationId, "finalizing", "session-reconciled",
                connectionGeneration, operation.Package.Sha256, now);
        }

        transaction.Commit();
        return changed;
    });

    public Task<bool> ResetCommitForNewSessionAsync(
        string operationId,
        long expectedFence,
        long connectionGeneration) => database.WriteAsync(connection =>
    {
        using var transaction = connection.BeginTransaction();
        var operation = Read(connection, transaction, operationId);
        if (operation is null || operation.MaintenanceFence != expectedFence ||
            operation.State != AgentUpgradeState.CommitPending ||
            operation.ObservedConnectionGeneration == connectionGeneration)
        {
            transaction.Commit();
            return false;
        }

        var now = timeProvider.GetUtcNow();
        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE agent_upgrade_operations
            SET state = 'AWAITING_HEALTH', observed_connection_generation = $generation,
                updated_unix_ms = $now
            WHERE operation_id = $operationId AND maintenance_fence = $fence
                AND state = 'COMMIT_PENDING';
            """;
        update.Parameters.AddWithValue("$generation", connectionGeneration);
        update.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        update.Parameters.AddWithValue("$operationId", operationId);
        update.Parameters.AddWithValue("$fence", expectedFence);
        var changed = update.ExecuteNonQuery() == 1;
        if (changed)
        {
            AppendEvent(
                connection, transaction, operationId, "awaiting-health", "probation-restarted",
                connectionGeneration, operation.Package.Sha256, now);
        }

        transaction.Commit();
        return changed;
    });

    public Task<bool> CompleteAsync(
        string operationId,
        long expectedFence,
        long connectionGeneration,
        string digest) => TransitionAsync(
            operationId,
            expectedFence,
            [AgentUpgradeState.Finalizing],
            AgentUpgradeState.Succeeded,
            failureCode: null,
            resultDigest: digest,
            observedGeneration: connectionGeneration,
            releaseDrain: true,
            auditAction: "agent.upgrade.succeeded",
            eventCode: "server-receipt-confirmed");

    public Task<bool> RollbackObservedAsync(
        string operationId,
        long expectedFence,
        long connectionGeneration,
        string digest,
        string? failureCode = null) => TransitionAsync(
            operationId,
            expectedFence,
            [.. CoordinatedStates, AgentUpgradeState.Failed],
            AgentUpgradeState.RolledBack,
            failureCode: failureCode ?? "candidate_health_failed_rolled_back",
            resultDigest: digest,
            observedGeneration: connectionGeneration,
            releaseDrain: true,
            auditAction: "agent.upgrade.rolled-back",
            eventCode: "prior-package-reconciled");

    public Task<bool> FailAsync(
        string operationId,
        long expectedFence,
        string failureCode) => TransitionAsync(
            operationId,
            expectedFence,
            CoordinatedStates,
            AgentUpgradeState.Failed,
            failureCode,
            resultDigest: null,
            observedGeneration: null,
            releaseDrain: false,
            auditAction: "agent.upgrade.failed",
            eventCode: failureCode);

    public Task<bool> FailBeforeHandoffAsync(
        string operationId,
        long expectedFence,
        string failureCode) => TransitionAsync(
            operationId,
            expectedFence,
            [AgentUpgradeState.Draining],
            AgentUpgradeState.Failed,
            failureCode,
            resultDigest: null,
            observedGeneration: null,
            releaseDrain: true,
            auditAction: "agent.upgrade.failed-before-handoff",
            eventCode: failureCode);

    public Task<bool> RequestAutomaticRollbackAsync(
        string operationId,
        long expectedFence,
        string failureCode) => TransitionAsync(
            operationId,
            expectedFence,
            [
                AgentUpgradeState.HandoffReady,
                AgentUpgradeState.AwaitingHealth,
                AgentUpgradeState.CommitPending,
                AgentUpgradeState.Finalizing,
                AgentUpgradeState.Failed,
            ],
            AgentUpgradeState.RollbackRequested,
            failureCode,
            resultDigest: null,
            observedGeneration: null,
            releaseDrain: false,
            auditAction: "agent.upgrade.rollback-requested",
            eventCode: failureCode);

    public Task<bool> CancelOrRequestRollbackAsync(
        ManagementRequestContext context,
        string operationId,
        string reason) => database.WriteAsync(connection =>
    {
        using var transaction = connection.BeginTransaction();
        var operation = Read(connection, transaction, operationId)
            ?? throw new AgentUpgradeException(
                "agent_upgrade_not_found", $"Upgrade operation '{operationId}' does not exist.",
                StatusCodes.Status404NotFound);
        if (operation.State is AgentUpgradeState.Succeeded or AgentUpgradeState.RolledBack or
            AgentUpgradeState.Cancelled or AgentUpgradeState.RollbackRequested ||
            operation.State == AgentUpgradeState.Failed && !operation.DrainHeld)
        {
            transaction.Commit();
            return false;
        }

        var now = timeProvider.GetUtcNow();
        var boundedReason = BoundCode(reason);
        var cancelBeforeCommit = operation.State == AgentUpgradeState.Draining;
        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE agent_upgrade_operations
            SET state = $state,
                cancellation_reason = CASE
                    WHEN cancellation_reason = '' THEN $reason ELSE cancellation_reason END,
                failure_code = CASE WHEN $cancelled = 1 THEN failure_code ELSE $reason END,
                updated_unix_ms = $now,
                completed_unix_ms = CASE WHEN $cancelled = 1 THEN $now ELSE NULL END
            WHERE operation_id = $operationId;
            """;
        update.Parameters.AddWithValue("$state", cancelBeforeCommit ? "CANCELLED" : "ROLLBACK_REQUESTED");
        update.Parameters.AddWithValue("$cancelled", cancelBeforeCommit ? 1 : 0);
        update.Parameters.AddWithValue("$reason", boundedReason);
        update.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        update.Parameters.AddWithValue("$operationId", operationId);
        update.ExecuteNonQuery();
        if (cancelBeforeCommit)
        {
            DeleteDrain(connection, transaction, operationId, operation.MaintenanceFence);
        }

        var phase = cancelBeforeCommit ? "cancelled" : "rollback-requested";
        AppendEvent(
            connection, transaction, operationId, phase, boundedReason,
            operation.ObservedConnectionGeneration, operation.Package.Sha256, now);
        AuditEventStore.Append(connection, transaction, AuditEventDraft.Create(
            context,
            now,
            cancelBeforeCommit ? "agent.upgrade.cancelled" : "agent.upgrade.rollback-requested",
            "agent-upgrade",
            operationId,
            details: new Dictionary<string, string> { ["reason"] = boundedReason }));
        transaction.Commit();
        return true;
    });

    private Task<IReadOnlyList<AgentUpgradeOperation>> ListWhereAsync(
        string? agentId,
        bool coordinatedOnly) => database.ReadAsync(connection =>
    {
        var filters = new List<string>();
        if (agentId is not null)
        {
            filters.Add("operations.agent_id = $agentId");
        }

        if (coordinatedOnly)
        {
            filters.Add("operations.state IN ('DRAINING', 'HANDOFF_READY', 'AWAITING_HEALTH', " +
                "'COMMIT_PENDING', 'FINALIZING', 'ROLLBACK_REQUESTED')");
        }

        using var command = connection.CreateCommand();
        command.CommandText = SelectSql +
            (filters.Count == 0 ? "" : " WHERE " + string.Join(" AND ", filters)) +
            " ORDER BY operations.created_unix_ms DESC, operations.operation_id COLLATE BINARY;";
        if (agentId is not null)
        {
            command.Parameters.AddWithValue("$agentId", agentId);
        }

        using var reader = command.ExecuteReader();
        var result = new List<AgentUpgradeOperation>();
        while (reader.Read())
        {
            result.Add(ReadOperation(reader));
        }

        return (IReadOnlyList<AgentUpgradeOperation>)result;
    });

    private Task<bool> TransitionAsync(
        string operationId,
        long expectedFence,
        IReadOnlyCollection<AgentUpgradeState> allowed,
        AgentUpgradeState target,
        string? failureCode,
        string? resultDigest,
        long? observedGeneration,
        bool releaseDrain,
        string auditAction,
        string eventCode) => database.WriteAsync(connection =>
    {
        using var transaction = connection.BeginTransaction();
        var operation = Read(connection, transaction, operationId);
        if (operation is null || operation.MaintenanceFence != expectedFence ||
            !allowed.Contains(operation.State))
        {
            transaction.Commit();
            return false;
        }

        var now = timeProvider.GetUtcNow();
        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE agent_upgrade_operations
            SET state = $state,
                observed_connection_generation = COALESCE($observedGeneration, observed_connection_generation),
                failure_code = CASE WHEN $failureCode = '' THEN failure_code ELSE $failureCode END,
                result_package_sha256 = COALESCE($resultDigest, result_package_sha256),
                updated_unix_ms = $updated,
                completed_unix_ms = $completed
            WHERE operation_id = $operationId AND maintenance_fence = $fence;
            """;
        update.Parameters.AddWithValue("$state", ToDatabase(target));
        update.Parameters.AddWithValue("$observedGeneration", (object?)observedGeneration ?? DBNull.Value);
        update.Parameters.AddWithValue("$failureCode", failureCode is null ? "" : BoundCode(failureCode));
        update.Parameters.AddWithValue("$resultDigest", (object?)resultDigest ?? DBNull.Value);
        update.Parameters.AddWithValue("$updated", now.ToUnixTimeMilliseconds());
        update.Parameters.AddWithValue("$completed", target is AgentUpgradeState.Succeeded or
            AgentUpgradeState.RolledBack or AgentUpgradeState.Failed or AgentUpgradeState.Cancelled
                ? now.ToUnixTimeMilliseconds()
                : DBNull.Value);
        update.Parameters.AddWithValue("$operationId", operationId);
        update.Parameters.AddWithValue("$fence", expectedFence);
        update.ExecuteNonQuery();
        if (releaseDrain)
        {
            DeleteDrain(connection, transaction, operationId, expectedFence);
        }

        AppendEvent(
            connection, transaction, operationId, ToRestPhase(target), BoundCode(eventCode),
            observedGeneration, resultDigest ?? operation.Package.Sha256, now);
        AppendAudit(
            connection, transaction, operation, now, auditAction, failureCode,
            target == AgentUpgradeState.Failed ? AuditOutcome.Failed : AuditOutcome.Succeeded);
        transaction.Commit();
        return true;
    });

    private static void AppendAudit(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AgentUpgradeOperation operation,
        DateTimeOffset now,
        string action,
        string? code = null,
        AuditOutcome outcome = AuditOutcome.Succeeded)
    {
        var context = new ManagementRequestContext(
            new ManagementPrincipal(
                operation.ActorType, operation.ActorId, "upgrade-operation", LegacyScope: null),
            operation.CorrelationId,
            operation.RequestId,
            "agent-upgrade-coordinator");
        AuditEventStore.Append(connection, transaction, AuditEventDraft.Create(
            context,
            now,
            action,
            "agent-upgrade",
            operation.OperationId,
            outcome,
            code ?? string.Empty,
            new Dictionary<string, string>
            {
                ["agent_id"] = operation.AgentId,
                ["package_sha256"] = operation.Package.Sha256,
                ["maintenance_fence"] = operation.MaintenanceFence.ToString(
                    global::System.Globalization.CultureInfo.InvariantCulture),
            }));
    }

    private static void AppendEvent(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string operationId,
        string phase,
        string code,
        long? connectionGeneration,
        string? packageSha256,
        DateTimeOffset now)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO agent_upgrade_events(
                operation_id, phase, code, connection_generation, package_sha256, created_unix_ms)
            VALUES ($operationId, $phase, $code, $generation, $digest, $created);
            """;
        command.Parameters.AddWithValue("$operationId", operationId);
        command.Parameters.AddWithValue("$phase", phase);
        command.Parameters.AddWithValue("$code", BoundCode(code));
        command.Parameters.AddWithValue("$generation", (object?)connectionGeneration ?? DBNull.Value);
        command.Parameters.AddWithValue("$digest", (object?)packageSha256 ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", now.ToUnixTimeMilliseconds());
        command.ExecuteNonQuery();
    }

    private static void DeleteDrain(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string operationId,
        long fence)
    {
        using var release = connection.CreateCommand();
        release.Transaction = transaction;
        release.CommandText = """
            DELETE FROM agent_maintenance_drains
            WHERE operation_id = $operationId AND fence = $fence;
            """;
        release.Parameters.AddWithValue("$operationId", operationId);
        release.Parameters.AddWithValue("$fence", fence);
        release.ExecuteNonQuery();
    }

    private static (string OperationId, string RequestHash)? FindByRequest(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string actorType,
        string actorId,
        string requestId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT operation_id, request_hash FROM agent_upgrade_operations
            WHERE actor_type = $actorType AND actor_id = $actorId AND request_id = $requestId;
            """;
        command.Parameters.AddWithValue("$actorType", actorType);
        command.Parameters.AddWithValue("$actorId", actorId);
        command.Parameters.AddWithValue("$requestId", requestId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? (reader.GetString(0), reader.GetString(1)) : null;
    }

    private static AgentUpgradeOperation? FindCoordinated(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string agentId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = SelectSql + """
             WHERE operations.agent_id = $agentId
               AND operations.state IN (
                   'DRAINING', 'HANDOFF_READY', 'AWAITING_HEALTH', 'COMMIT_PENDING',
                   'FINALIZING', 'ROLLBACK_REQUESTED')
             ORDER BY operations.created_unix_ms, operations.operation_id COLLATE BINARY
             LIMIT 1;
            """;
        command.Parameters.AddWithValue("$agentId", agentId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadOperation(reader) : null;
    }

    private static AgentUpgradeOperation? Read(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string operationId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = SelectSql + " WHERE operations.operation_id = $operationId;";
        command.Parameters.AddWithValue("$operationId", operationId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadOperation(reader) : null;
    }

    private static AgentUpgradeOperation ReadOperation(SqliteDataReader reader)
    {
        var package = new AgentPackage(
            reader.GetString(25), reader.GetString(26), reader.GetString(27),
            reader.GetString(28), reader.GetInt64(29),
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(31)), reader.GetString(30));
        return new AgentUpgradeOperation(
            reader.GetString(0), reader.GetString(1), package, ParseState(reader.GetString(2)),
            reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6),
            reader.GetString(7), reader.GetInt64(8),
            reader.IsDBNull(9) ? null : reader.GetString(9), reader.GetInt64(10),
            reader.IsDBNull(11) ? null : reader.GetInt64(11), reader.GetInt32(12),
            reader.IsDBNull(13) ? null : reader.GetInt64(13),
            reader.IsDBNull(14) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(14)),
            string.IsNullOrEmpty(reader.GetString(15)) ? null : reader.GetString(15),
            string.IsNullOrEmpty(reader.GetString(16)) ? null : reader.GetString(16),
            reader.IsDBNull(17) ? null : reader.GetString(17),
            !reader.IsDBNull(32),
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(18)),
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(19)),
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(20)),
            reader.IsDBNull(21) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(21)));
    }

    private static AgentUpgradeState ParseState(string state) => state switch
    {
        "DRAINING" => AgentUpgradeState.Draining,
        "HANDOFF_READY" => AgentUpgradeState.HandoffReady,
        "AWAITING_HEALTH" => AgentUpgradeState.AwaitingHealth,
        "COMMIT_PENDING" => AgentUpgradeState.CommitPending,
        "FINALIZING" => AgentUpgradeState.Finalizing,
        "ROLLBACK_REQUESTED" => AgentUpgradeState.RollbackRequested,
        "SUCCEEDED" => AgentUpgradeState.Succeeded,
        "ROLLED_BACK" => AgentUpgradeState.RolledBack,
        "FAILED" => AgentUpgradeState.Failed,
        "CANCELLED" => AgentUpgradeState.Cancelled,
        _ => throw new InvalidDataException($"unknown Agent upgrade state '{state}'"),
    };

    private static string ToDatabase(AgentUpgradeState state) => state switch
    {
        AgentUpgradeState.Draining => "DRAINING",
        AgentUpgradeState.HandoffReady => "HANDOFF_READY",
        AgentUpgradeState.AwaitingHealth => "AWAITING_HEALTH",
        AgentUpgradeState.CommitPending => "COMMIT_PENDING",
        AgentUpgradeState.Finalizing => "FINALIZING",
        AgentUpgradeState.RollbackRequested => "ROLLBACK_REQUESTED",
        AgentUpgradeState.Succeeded => "SUCCEEDED",
        AgentUpgradeState.RolledBack => "ROLLED_BACK",
        AgentUpgradeState.Failed => "FAILED",
        AgentUpgradeState.Cancelled => "CANCELLED",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    private static string ToRestPhase(AgentUpgradeState state) => state switch
    {
        AgentUpgradeState.Draining => "draining",
        AgentUpgradeState.HandoffReady => "handoff-ready",
        AgentUpgradeState.AwaitingHealth => "awaiting-health",
        AgentUpgradeState.CommitPending => "commit-pending",
        AgentUpgradeState.Finalizing => "finalizing",
        AgentUpgradeState.RollbackRequested => "rollback-requested",
        AgentUpgradeState.Succeeded => "succeeded",
        AgentUpgradeState.RolledBack => "rolled-back",
        AgentUpgradeState.Failed => "failed",
        AgentUpgradeState.Cancelled => "cancelled",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    private static string BoundCode(string code)
    {
        code = string.IsNullOrWhiteSpace(code) ? "unspecified" : code.Trim();
        code = new string(code.Select(character => char.IsControl(character) ? '_' : character).ToArray());
        return code.Length <= 128 ? code : code[..128];
    }

    private const string SelectSql = """
        SELECT operations.operation_id, operations.agent_id, operations.state,
               operations.actor_type, operations.actor_id, operations.request_id,
               operations.correlation_id, operations.reason, operations.maintenance_fence,
               operations.prior_package_sha256, operations.starting_connection_generation,
               operations.observed_connection_generation, operations.restart_attempts,
               operations.last_dispatch_connection_generation, operations.next_restart_unix_ms,
               operations.cancellation_reason, operations.failure_code,
               operations.result_package_sha256,
               operations.created_unix_ms, operations.updated_unix_ms,
               operations.deadline_unix_ms, operations.completed_unix_ms,
               operations.credential_kind, operations.request_hash, operations.package_id,
               packages.package_id, packages.version, packages.rid, packages.sha256,
               packages.size, packages.source, packages.created_unix_ms,
               drains.operation_id
        FROM agent_upgrade_operations AS operations
        JOIN agent_packages AS packages ON packages.package_id = operations.package_id
        LEFT JOIN agent_maintenance_drains AS drains
            ON drains.operation_id = operations.operation_id
        """;
}
