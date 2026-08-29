using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Vivarium.Controller.Auditing;
using Vivarium.Controller.Configuration.Git;
using Vivarium.Controller.Persistence;
using Vivarium.Controller.Security;

namespace Vivarium.Controller.Configuration.Reconciliation;

public sealed class ConfigurationReconciler
{
    private const int MaximumHeadConvergenceAttempts = 4;
    private readonly VivariumDatabase database;
    private readonly TimeProvider timeProvider;
    private readonly IReadOnlyList<IConfigurationProjectionApplier> projections;

    public ConfigurationReconciler(
        VivariumDatabase database,
        TimeProvider timeProvider,
        IEnumerable<IConfigurationProjectionApplier>? projections = null)
    {
        this.database = database;
        this.timeProvider = timeProvider;
        Operations = new ConfigurationOperationStore(database, timeProvider);
        this.projections =
        [
            new AgentDesiredConfigurationProjection(),
            new AuthorizationPolicyProjection(),
            .. projections ?? [],
        ];
    }

    public ConfigurationOperationStore Operations { get; }

    public async Task<ConfigurationReconciliationResult> ReconcileAuthoritativeHeadAsync(
        ManagementRequestContext context,
        string materializationScope,
        IConfigurationRepository repository,
        string? operationId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ConfigurationMutationOperation? requestedOperation = null;
        if (!string.IsNullOrWhiteSpace(operationId))
        {
            requestedOperation = await Operations.GetAsync(operationId);
        }

        ConfigurationReconciliationResult? last = null;
        for (var attempt = 1; attempt <= MaximumHeadConvergenceAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var head = await repository.GetAuthoritativeHeadAsync(cancellationToken);
            var validation = await repository.ValidateRevisionAsync(head, cancellationToken);
            var matchingOperationId = requestedOperation switch
            {
                null when attempt == 1 => operationId,
                { State: ConfigurationMutationState.Pending } when
                    validation.Validated?.Descriptor.ControllerProvenance?.OperationId ==
                    requestedOperation.OperationId => requestedOperation.OperationId,
                { ResultRevision: { } resultRevision } when resultRevision == head =>
                    requestedOperation.OperationId,
                _ => null,
            };
            last = await ReconcileAsync(
                context,
                materializationScope,
                validation,
                matchingOperationId,
                cancellationToken);
            var observedAfterApply = await repository.GetAuthoritativeHeadAsync(cancellationToken);
            if (observedAfterApply == head)
            {
                return last with
                {
                    HeadConvergence = new ConfigurationHeadConvergence(
                        ConfigurationHeadConvergenceState.Converged,
                        observedAfterApply,
                        attempt,
                        Diagnostic: null),
                };
            }

            if (attempt == MaximumHeadConvergenceAttempts)
            {
                return last with
                {
                    HeadConvergence = new ConfigurationHeadConvergence(
                        ConfigurationHeadConvergenceState.Degraded,
                        observedAfterApply,
                        attempt,
                        new ConfigurationValidationDiagnostic(
                            "configuration_head_unstable",
                            Path: null,
                            Field: null,
                            "the authoritative configuration head kept moving during bounded reconciliation")),
                };
            }
        }

        throw new InvalidOperationException("configuration head convergence loop did not produce a result");
    }

    public async Task<ConfigurationReconciliationResult> ReconcileAsync(
        ManagementRequestContext context,
        string materializationScope,
        ConfigurationRevisionValidation validation,
        string? operationId = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(validation);
        RequireBounded(materializationScope, 128, "materialization scope");
        ValidateValidation(validation);

        ConfigurationMutationOperation? operation = null;
        if (validation.Validated is { } validated)
        {
            operation = await Operations.RecoverCommittedHeadAsync(validated);
        }

        if (operation is null && !string.IsNullOrWhiteSpace(operationId))
        {
            operation = await Operations.GetAsync(operationId);
        }

        if (operation is not null)
        {
            if (!string.Equals(
                    operation.MaterializationScope,
                    materializationScope,
                    StringComparison.Ordinal) ||
                operation.ResultRevision is not null && operation.ResultRevision != validation.Revision)
            {
                throw new InvalidOperationException(
                    "configuration operation does not match the reconciliation candidate");
            }

            context = operation.RequestContext;
            operationId = operation.OperationId;
        }

        operationId = string.IsNullOrWhiteSpace(operationId)
            ? ManagementIdentifiers.NewId()
            : operationId;
        RequireBounded(operationId, 128, "operation ID");

        if (!validation.IsValid)
        {
            return await RecordRejectedAttemptAsync(
                context,
                materializationScope,
                operationId,
                validation,
                ConfigurationRevisionSetState.Invalid,
                NormalizeDiagnostics(validation.Diagnostics, "configuration_invalid"));
        }

        try
        {
            return await ApplyAsync(
                context,
                materializationScope,
                operationId,
                operation,
                validation.Validated!);
        }
        catch (ConfigurationProjectionException exception)
        {
            var diagnostic = new ConfigurationValidationDiagnostic(
                exception.Code,
                exception.Path,
                exception.Field,
                exception.Message);
            return await RecordRejectedAttemptAsync(
                context,
                materializationScope,
                operationId,
                validation,
                ConfigurationRevisionSetState.Blocked,
                NormalizeDiagnostics([diagnostic], "configuration_projection_blocked"));
        }
    }

    public Task<ConfigurationMaterializationState?> GetStateAsync(string materializationScope) =>
        database.ReadAsync(connection =>
        {
            RequireBounded(materializationScope, 128, "materialization scope");
            return ReadState(connection, transaction: null, materializationScope);
        });

    private Task<ConfigurationReconciliationResult> ApplyAsync(
        ManagementRequestContext context,
        string materializationScope,
        string operationId,
        ConfigurationMutationOperation? operation,
        ValidatedConfigurationRevision revision) => database.WriteAsync(connection =>
    {
        var descriptor = revision.Descriptor;
        var now = timeProvider.GetUtcNow();
        var appliedAt = operation is not null && operation.CreatedAt > now
            ? operation.CreatedAt
            : now;
        var revisionSetId = ComputeRevisionSetId(
            materializationScope,
            descriptor.Revision,
            descriptor.TreeHash,
            descriptor.AggregateContentHash);
        using var transaction = connection.BeginTransaction();
        var before = ReadState(connection, transaction, materializationScope);
        if (before?.Active?.RevisionSetId == revisionSetId)
        {
            MarkOperationAppliedIfNeeded(
                connection,
                transaction,
                operation,
                revisionSetId,
                descriptor,
                appliedAt);
            UpsertScope(
                connection,
                transaction,
                materializationScope,
                revisionSetId,
                revisionSetId,
                revisionSetId,
                appliedAt);
            transaction.Commit();
            var unchanged = ReadState(connection, transaction: null, materializationScope)
                ?? throw new InvalidOperationException("active configuration state is missing");
            return new ConfigurationReconciliationResult(
                ConfigurationReconciliationOutcome.NoChange,
                before.Active,
                unchanged);
        }

        if (before?.Active is { } prior)
        {
            using var supersede = connection.CreateCommand();
            supersede.Transaction = transaction;
            supersede.CommandText = """
                UPDATE configuration_revision_sets SET state = 'SUPERSEDED'
                WHERE revision_set_id = $revisionSetId AND state = 'ACTIVE';
                """;
            supersede.Parameters.AddWithValue("$revisionSetId", prior.RevisionSetId);
            if (supersede.ExecuteNonQuery() != 1)
            {
                throw new InvalidOperationException("active configuration revision changed unexpectedly");
            }
        }

        UpsertRevisionSet(
            connection,
            transaction,
            revisionSetId,
            materializationScope,
            before?.Active?.RevisionSetId,
            ConfigurationRevisionSetState.Active,
            operationId,
            operation?.CreatedAt ?? appliedAt,
            appliedAt,
            appliedAt,
            context,
            diagnosticsJson: "[]");
        UpsertControlMember(
            connection,
            transaction,
            revisionSetId,
            descriptor.Revision,
            descriptor.TreeHash,
            descriptor.AggregateContentHash,
            descriptor.SchemaVersion);

        foreach (var projection in projections)
        {
            projection.Apply(connection, transaction, revision, revisionSetId, appliedAt);
        }

        UpsertScope(
            connection,
            transaction,
            materializationScope,
            revisionSetId,
            revisionSetId,
            revisionSetId,
            appliedAt);
        MarkOperationAppliedIfNeeded(
            connection,
            transaction,
            operation,
            revisionSetId,
            descriptor,
            appliedAt);

        AuditEventStore.Append(
            connection,
            transaction,
            AuditEventDraft.Create(
                context,
                now,
                "configuration.revision.applied",
                "configuration-scope",
                materializationScope,
                details: new Dictionary<string, string>
                {
                    ["operation_id"] = operationId,
                    ["repository_id"] = descriptor.Revision.RepositoryId,
                    ["revision_set_id"] = revisionSetId,
                }) with
            {
                BaseRevision = before?.Active is null ? null : ControlRevision(before.Active).Canonical,
                ResultRevision = descriptor.Revision.Canonical,
            });
        transaction.Commit();
        var after = ReadState(connection, transaction: null, materializationScope)
            ?? throw new InvalidOperationException("applied configuration state is missing");
        return new ConfigurationReconciliationResult(
            ConfigurationReconciliationOutcome.Applied,
            after.LatestAttempt,
            after);
    });

    private Task<ConfigurationReconciliationResult> RecordRejectedAttemptAsync(
        ManagementRequestContext context,
        string materializationScope,
        string operationId,
        ConfigurationRevisionValidation validation,
        ConfigurationRevisionSetState state,
        IReadOnlyList<ConfigurationValidationDiagnostic> diagnostics) =>
        database.WriteAsync(connection =>
        {
            var now = timeProvider.GetUtcNow();
            var contentHash = validation.Validated?.Descriptor.AggregateContentHash;
            var schemaVersion = validation.Validated?.Descriptor.SchemaVersion;
            var treeHash = validation.TreeHash
                ?? throw new InvalidOperationException(
                    "an unreadable configuration revision cannot be recorded as a validated attempt");
            var revisionSetId = ComputeRevisionSetId(
                materializationScope,
                validation.Revision,
                treeHash,
                contentHash ?? "invalid");
            var diagnosticsJson = SerializeDiagnostics(diagnostics);
            using var transaction = connection.BeginTransaction();
            var before = ReadState(connection, transaction, materializationScope);
            var existing = ReadRevisionSet(connection, transaction, revisionSetId);
            if (existing is not null &&
                existing.State == state &&
                string.Equals(
                    SerializeDiagnostics(existing.Diagnostics),
                    diagnosticsJson,
                    StringComparison.Ordinal) &&
                before?.LatestAttempt.RevisionSetId == revisionSetId)
            {
                transaction.Commit();
                return new ConfigurationReconciliationResult(
                    state == ConfigurationRevisionSetState.Invalid
                        ? ConfigurationReconciliationOutcome.Invalid
                        : ConfigurationReconciliationOutcome.Blocked,
                    existing,
                    before!);
            }

            UpsertRevisionSet(
                connection,
                transaction,
                revisionSetId,
                materializationScope,
                before?.Active?.RevisionSetId,
                state,
                operationId,
                now,
                now,
                appliedAt: null,
                context,
                diagnosticsJson);
            UpsertControlMember(
                connection,
                transaction,
                revisionSetId,
                validation.Revision,
                treeHash,
                contentHash,
                schemaVersion);
            UpsertScope(
                connection,
                transaction,
                materializationScope,
                before?.Active?.RevisionSetId,
                before?.LastKnownGood?.RevisionSetId,
                revisionSetId,
                now);

            var diagnostic = diagnostics[0];
            AuditEventStore.Append(
                connection,
                transaction,
                AuditEventDraft.Create(
                    context,
                    now,
                    state == ConfigurationRevisionSetState.Invalid
                        ? "configuration.revision.invalid"
                        : "configuration.revision.blocked",
                    "configuration-scope",
                    materializationScope,
                    AuditOutcome.Failed,
                    diagnostic.Code,
                    new Dictionary<string, string>
                    {
                        ["operation_id"] = operationId,
                        ["repository_id"] = validation.Revision.RepositoryId,
                        ["revision_set_id"] = revisionSetId,
                    }) with
                {
                    BaseRevision = before?.Active is null ? null : ControlRevision(before.Active).Canonical,
                    ResultRevision = validation.Revision.Canonical,
                });
            transaction.Commit();
            var after = ReadState(connection, transaction: null, materializationScope)
                ?? throw new InvalidOperationException("reconciliation attempt state is missing");
            return new ConfigurationReconciliationResult(
                state == ConfigurationRevisionSetState.Invalid
                    ? ConfigurationReconciliationOutcome.Invalid
                    : ConfigurationReconciliationOutcome.Blocked,
                after.LatestAttempt,
                after);
        });

    private static void UpsertRevisionSet(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string revisionSetId,
        string materializationScope,
        string? baseRevisionSetId,
        ConfigurationRevisionSetState state,
        string operationId,
        DateTimeOffset requestedAt,
        DateTimeOffset validatedAt,
        DateTimeOffset? appliedAt,
        ManagementRequestContext context,
        string diagnosticsJson)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO configuration_revision_sets(
                revision_set_id, materialization_scope, base_revision_set_id, state, operation_id,
                requested_unix_ms, validated_unix_ms, applied_unix_ms,
                actor_type, actor_id, correlation_id, request_id, diagnostics_json)
            VALUES (
                $revisionSetId, $scope, $baseRevisionSetId, $state, $operationId,
                $requestedAt, $validatedAt, $appliedAt,
                $actorType, $actorId, $correlationId, $requestId, $diagnosticsJson)
            ON CONFLICT(revision_set_id) DO UPDATE SET
                base_revision_set_id = excluded.base_revision_set_id,
                state = excluded.state,
                operation_id = excluded.operation_id,
                requested_unix_ms = excluded.requested_unix_ms,
                validated_unix_ms = excluded.validated_unix_ms,
                applied_unix_ms = excluded.applied_unix_ms,
                actor_type = excluded.actor_type,
                actor_id = excluded.actor_id,
                correlation_id = excluded.correlation_id,
                request_id = excluded.request_id,
                diagnostics_json = excluded.diagnostics_json
            WHERE configuration_revision_sets.materialization_scope = excluded.materialization_scope;
            """;
        command.Parameters.AddWithValue("$revisionSetId", revisionSetId);
        command.Parameters.AddWithValue("$scope", materializationScope);
        command.Parameters.AddWithValue("$baseRevisionSetId", (object?)baseRevisionSetId ?? DBNull.Value);
        command.Parameters.AddWithValue("$state", Serialize(state));
        command.Parameters.AddWithValue("$operationId", operationId);
        command.Parameters.AddWithValue("$requestedAt", requestedAt.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$validatedAt", validatedAt.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue(
            "$appliedAt",
            appliedAt is null ? DBNull.Value : appliedAt.Value.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$actorType", context.Principal.ActorType);
        command.Parameters.AddWithValue("$actorId", context.Principal.ActorId);
        command.Parameters.AddWithValue("$correlationId", context.CorrelationId);
        command.Parameters.AddWithValue("$requestId", (object?)context.RequestId ?? DBNull.Value);
        command.Parameters.AddWithValue("$diagnosticsJson", diagnosticsJson);
        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException("revision-set identity belongs to another materialization scope");
        }
    }

    private static void UpsertControlMember(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string revisionSetId,
        ConfigurationRevision revision,
        string treeHash,
        string? contentHash,
        string? schemaVersion)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO configuration_revision_members(
                revision_set_id, repository_id, repository_role, commit_sha,
                tree_hash, content_hash, schema_version, project_binding)
            VALUES (
                $revisionSetId, $repositoryId, 'CONTROL', $commit,
                $treeHash, $contentHash, $schemaVersion, NULL)
            ON CONFLICT(revision_set_id, repository_id) DO UPDATE SET
                repository_role = excluded.repository_role,
                commit_sha = excluded.commit_sha,
                tree_hash = excluded.tree_hash,
                content_hash = excluded.content_hash,
                schema_version = excluded.schema_version,
                project_binding = NULL;
            """;
        command.Parameters.AddWithValue("$revisionSetId", revisionSetId);
        command.Parameters.AddWithValue("$repositoryId", revision.RepositoryId);
        command.Parameters.AddWithValue("$commit", revision.Commit);
        command.Parameters.AddWithValue("$treeHash", treeHash);
        command.Parameters.AddWithValue("$contentHash", (object?)contentHash ?? DBNull.Value);
        command.Parameters.AddWithValue("$schemaVersion", (object?)schemaVersion ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static void UpsertScope(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string scope,
        string? activeRevisionSetId,
        string? lastKnownGoodRevisionSetId,
        string latestAttemptRevisionSetId,
        DateTimeOffset updatedAt)
    {
        EnsureScopePointer(
            connection,
            transaction,
            scope,
            latestAttemptRevisionSetId,
            requiredState: null,
            "latest attempt");
        if (activeRevisionSetId is not null)
        {
            EnsureScopePointer(
                connection,
                transaction,
                scope,
                activeRevisionSetId,
                ConfigurationRevisionSetState.Active,
                "active revision");
        }

        if (!string.Equals(activeRevisionSetId, lastKnownGoodRevisionSetId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "active and last-known-good revision pointers must advance together");
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO configuration_materialization_scopes(
                materialization_scope, active_revision_set_id,
                last_known_good_revision_set_id, latest_attempt_revision_set_id, updated_unix_ms)
            VALUES ($scope, $active, $lastKnownGood, $latestAttempt, $updatedAt)
            ON CONFLICT(materialization_scope) DO UPDATE SET
                active_revision_set_id = excluded.active_revision_set_id,
                last_known_good_revision_set_id = excluded.last_known_good_revision_set_id,
                latest_attempt_revision_set_id = excluded.latest_attempt_revision_set_id,
                updated_unix_ms = excluded.updated_unix_ms;
            """;
        command.Parameters.AddWithValue("$scope", scope);
        command.Parameters.AddWithValue("$active", (object?)activeRevisionSetId ?? DBNull.Value);
        command.Parameters.AddWithValue("$lastKnownGood", (object?)lastKnownGoodRevisionSetId ?? DBNull.Value);
        command.Parameters.AddWithValue("$latestAttempt", latestAttemptRevisionSetId);
        command.Parameters.AddWithValue("$updatedAt", updatedAt.ToUnixTimeMilliseconds());
        command.ExecuteNonQuery();
    }

    private static void EnsureScopePointer(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string scope,
        string revisionSetId,
        ConfigurationRevisionSetState? requiredState,
        string pointerName)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT state FROM configuration_revision_sets
            WHERE revision_set_id = $revisionSetId AND materialization_scope = $scope;
            """;
        command.Parameters.AddWithValue("$revisionSetId", revisionSetId);
        command.Parameters.AddWithValue("$scope", scope);
        var state = command.ExecuteScalar() as string;
        if (state is null || requiredState is not null && state != Serialize(requiredState.Value))
        {
            throw new InvalidOperationException(
                $"configuration scope {pointerName} does not reference a same-scope " +
                $"{(requiredState is null ? string.Empty : Serialize(requiredState.Value) + " ")}revision set");
        }
    }

    private static void MarkOperationAppliedIfNeeded(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ConfigurationMutationOperation? operation,
        string revisionSetId,
        ConfigurationRevisionDescriptor descriptor,
        DateTimeOffset updatedAt)
    {
        if (operation is null)
        {
            return;
        }

        if (operation.State == ConfigurationMutationState.Applied)
        {
            if (operation.RevisionSetId != revisionSetId)
            {
                throw new InvalidOperationException(
                    "configuration operation is already linked to another revision set");
            }

            return;
        }

        if (operation.State != ConfigurationMutationState.Committed ||
            operation.ResultRevision != descriptor.Revision ||
            !string.Equals(
                operation.CandidateAggregateContentHash,
                descriptor.AggregateContentHash,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "only the exact committed configuration operation may become applied");
        }

        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE configuration_mutation_operations SET
                state = 'APPLIED', revision_set_id = $revisionSetId, updated_unix_ms = $updatedAt
            WHERE operation_id = $operationId AND state = 'COMMITTED'
                AND repository_id = $repositoryId AND result_commit = $resultCommit
                AND candidate_content_hash = $contentHash;
            """;
        update.Parameters.AddWithValue("$revisionSetId", revisionSetId);
        update.Parameters.AddWithValue("$updatedAt", updatedAt.ToUnixTimeMilliseconds());
        update.Parameters.AddWithValue("$operationId", operation.OperationId);
        update.Parameters.AddWithValue("$repositoryId", descriptor.Revision.RepositoryId);
        update.Parameters.AddWithValue("$resultCommit", descriptor.Revision.Commit);
        update.Parameters.AddWithValue("$contentHash", descriptor.AggregateContentHash);
        if (update.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException("configuration operation changed before apply");
        }
    }

    private static ConfigurationMaterializationState? ReadState(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string scope)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT active_revision_set_id, last_known_good_revision_set_id,
                latest_attempt_revision_set_id, updated_unix_ms
            FROM configuration_materialization_scopes
            WHERE materialization_scope = $scope;
            """;
        command.Parameters.AddWithValue("$scope", scope);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var activeId = reader.IsDBNull(0) ? null : reader.GetString(0);
        var lastKnownGoodId = reader.IsDBNull(1) ? null : reader.GetString(1);
        var latestId = reader.GetString(2);
        var updatedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3));
        reader.Close();
        var active = activeId is null ? null : ReadRevisionSet(connection, transaction, activeId);
        var lastKnownGood = lastKnownGoodId is null
            ? null
            : activeId == lastKnownGoodId
                ? active
                : ReadRevisionSet(connection, transaction, lastKnownGoodId);
        var latest = latestId == activeId
            ? active
            : ReadRevisionSet(connection, transaction, latestId);
        if (latest is null ||
            !string.Equals(latest.MaterializationScope, scope, StringComparison.Ordinal) ||
            active is not null && !string.Equals(active.MaterializationScope, scope, StringComparison.Ordinal) ||
            activeId is not null && active?.State != ConfigurationRevisionSetState.Active ||
            lastKnownGoodId != activeId)
        {
            throw new InvalidDataException("configuration materialization scope pointers are inconsistent");
        }

        return new ConfigurationMaterializationState(scope, active, lastKnownGood, latest, updatedAt);
    }

    private static StoredConfigurationRevisionSet? ReadRevisionSet(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string revisionSetId)
    {
        string materializationScope;
        string? baseRevisionSetId;
        ConfigurationRevisionSetState state;
        string operationId;
        DateTimeOffset requestedAt;
        DateTimeOffset validatedAt;
        DateTimeOffset? appliedAt;
        ManagementRequestContext context;
        IReadOnlyList<ConfigurationValidationDiagnostic> diagnostics;
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT materialization_scope, base_revision_set_id, state, operation_id,
                    requested_unix_ms, validated_unix_ms, applied_unix_ms,
                    actor_type, actor_id, correlation_id, request_id, diagnostics_json
                FROM configuration_revision_sets WHERE revision_set_id = $revisionSetId;
                """;
            command.Parameters.AddWithValue("$revisionSetId", revisionSetId);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            materializationScope = reader.GetString(0);
            baseRevisionSetId = reader.IsDBNull(1) ? null : reader.GetString(1);
            state = ParseRevisionSetState(reader.GetString(2));
            operationId = reader.GetString(3);
            requestedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4));
            validatedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5));
            appliedAt = reader.IsDBNull(6)
                ? null
                : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(6));
            context = new ManagementRequestContext(
                new ManagementPrincipal(reader.GetString(7), reader.GetString(8), "reconciliation", null),
                reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                "reconciliation");
            diagnostics = DeserializeDiagnostics(reader.GetString(11));
        }

        var members = new List<StoredConfigurationRevisionMember>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT repository_id, repository_role, commit_sha, tree_hash,
                    content_hash, schema_version, project_binding
                FROM configuration_revision_members
                WHERE revision_set_id = $revisionSetId
                ORDER BY repository_role, repository_id COLLATE BINARY;
                """;
            command.Parameters.AddWithValue("$revisionSetId", revisionSetId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                members.Add(new StoredConfigurationRevisionMember(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6)));
            }
        }

        if (members.Count(member => member.RepositoryRole == "CONTROL") != 1)
        {
            throw new InvalidDataException("configuration revision set has no unique control member");
        }

        return new StoredConfigurationRevisionSet(
            revisionSetId,
            materializationScope,
            baseRevisionSetId,
            state,
            operationId,
            requestedAt,
            validatedAt,
            appliedAt,
            context,
            diagnostics,
            members);
    }

    private static ConfigurationRevision ControlRevision(StoredConfigurationRevisionSet revisionSet)
    {
        var member = revisionSet.Members.Single(member => member.RepositoryRole == "CONTROL");
        return new ConfigurationRevision(member.RepositoryId, member.Commit);
    }

    private static string ComputeRevisionSetId(
        string materializationScope,
        ConfigurationRevision revision,
        string treeHash,
        string validationIdentity)
    {
        var canonical = string.Join('\n',
            "vivarium-configuration-revision-set-v1",
            materializationScope,
            "CONTROL",
            revision.RepositoryId,
            revision.Commit,
            treeHash,
            validationIdentity);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }

    private static IReadOnlyList<ConfigurationValidationDiagnostic> NormalizeDiagnostics(
        IReadOnlyList<ConfigurationValidationDiagnostic> diagnostics,
        string fallbackCode)
    {
        var normalized = diagnostics.Count == 0
            ? [new ConfigurationValidationDiagnostic(fallbackCode, null, null, "configuration validation failed")]
            : diagnostics.Take(32).ToArray();
        foreach (var diagnostic in normalized)
        {
            RequireBounded(diagnostic.Code, 128, "diagnostic code");
            RequireOptionalBounded(diagnostic.Path, 256, "diagnostic path");
            RequireOptionalBounded(diagnostic.Field, 128, "diagnostic field");
            RequireBounded(diagnostic.Summary, 512, "diagnostic summary");
        }

        _ = SerializeDiagnostics(normalized);
        return normalized;
    }

    private static string SerializeDiagnostics(
        IReadOnlyList<ConfigurationValidationDiagnostic> diagnostics)
    {
        var json = JsonSerializer.Serialize(diagnostics);
        if (json.Length > 8192)
        {
            throw new ArgumentException("configuration diagnostics exceed the 8192-character bound");
        }

        return json;
    }

    private static IReadOnlyList<ConfigurationValidationDiagnostic> DeserializeDiagnostics(string json) =>
        JsonSerializer.Deserialize<List<ConfigurationValidationDiagnostic>>(json)
        ?? throw new InvalidDataException("configuration diagnostics are invalid");

    private static void ValidateValidation(ConfigurationRevisionValidation validation)
    {
        if (validation.TreeHash is { } treeHash)
        {
            ValidateObjectId(treeHash, "tree hash");
        }

        if (validation.IsValid)
        {
            var descriptor = validation.Validated!.Descriptor;
            if (descriptor.Revision != validation.Revision ||
                !string.Equals(descriptor.TreeHash, validation.TreeHash, StringComparison.Ordinal))
            {
                throw new ArgumentException("validated configuration descriptor does not match its revision");
            }

            ValidateObjectId(descriptor.TreeHash, "tree hash");
            ValidateContentHash(descriptor.AggregateContentHash, "aggregate content hash");
            RequireBounded(descriptor.SchemaVersion, 64, "schema version");
            if (descriptor.Parents.Any(parent =>
                    parent.RepositoryId != descriptor.Revision.RepositoryId))
            {
                throw new ArgumentException("configuration parent revisions cross repository identities");
            }

            if (validation.Validated.Documents.Count > 1024)
            {
                throw new ArgumentException("validated configuration has too many documents");
            }

            foreach (var document in validation.Validated.Documents)
            {
                ValidateContentHash(document.ContentHash, "document content hash");
            }
        }
        else if (validation.TreeHash is null)
        {
            throw new ArgumentException(
                "invalid authoritative revisions must retain their exact tree hash");
        }
    }

    private static void ValidateObjectId(string value, string name)
    {
        if (value.Length is not 40 and not 64 ||
            value.Any(character => !char.IsAsciiHexDigit(character) || char.IsUpper(character)))
        {
            throw new ArgumentException($"{name} must be a lowercase complete Git object ID");
        }
    }

    private static void ValidateContentHash(string value, string name)
    {
        if (value.Length != 64 ||
            value.Any(character => !char.IsAsciiHexDigit(character) || char.IsUpper(character)))
        {
            throw new ArgumentException($"{name} must be a lowercase SHA-256 value");
        }
    }

    private static void RequireBounded(string value, int maximum, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum)
        {
            throw new ArgumentException($"{name} must be between 1 and {maximum} characters");
        }
    }

    private static void RequireOptionalBounded(string? value, int maximum, string name)
    {
        if (value is not null && (string.IsNullOrWhiteSpace(value) || value.Length > maximum))
        {
            throw new ArgumentException($"{name} must be null or between 1 and {maximum} characters");
        }
    }

    private static string Serialize(ConfigurationRevisionSetState state) => state switch
    {
        ConfigurationRevisionSetState.Invalid => "INVALID",
        ConfigurationRevisionSetState.Blocked => "BLOCKED",
        ConfigurationRevisionSetState.Active => "ACTIVE",
        ConfigurationRevisionSetState.Superseded => "SUPERSEDED",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    private static ConfigurationRevisionSetState ParseRevisionSetState(string state) => state switch
    {
        "INVALID" => ConfigurationRevisionSetState.Invalid,
        "BLOCKED" => ConfigurationRevisionSetState.Blocked,
        "ACTIVE" => ConfigurationRevisionSetState.Active,
        "SUPERSEDED" => ConfigurationRevisionSetState.Superseded,
        _ => throw new InvalidDataException($"unknown configuration revision-set state '{state}'"),
    };
}
