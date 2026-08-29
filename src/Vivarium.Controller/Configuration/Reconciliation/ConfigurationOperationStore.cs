using Microsoft.Data.Sqlite;
using System.Text.Json;
using Vivarium.Controller.Auditing;
using Vivarium.Controller.Configuration.Git;
using Vivarium.Controller.Persistence;
using Vivarium.Controller.Security;

namespace Vivarium.Controller.Configuration.Reconciliation;

public sealed class ConfigurationOperationStore(
    VivariumDatabase database,
    TimeProvider timeProvider)
{
    public Task<ConfigurationMutationBeginResult> BeginAsync(
        ManagementRequestContext context,
        ConfigurationMutationIntent intent) => database.WriteAsync(connection =>
    {
        ValidateIntent(context, intent);
        using var transaction = connection.BeginTransaction();
        var existing = ReadByIdempotencyKey(
            connection,
            transaction,
            context.Principal.ActorType,
            context.Principal.ActorId,
            intent.OperationKind,
            context.RequestId!);
        if (existing is not null)
        {
            if (!MatchesIntent(existing, intent))
            {
                throw new ConfigurationIdempotencyConflictException(existing.OperationId);
            }

            transaction.Commit();
            return new ConfigurationMutationBeginResult(
                ConfigurationMutationBeginOutcome.Existing,
                existing);
        }

        var now = timeProvider.GetUtcNow();
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO configuration_mutation_operations(
                    operation_id, operation_kind, materialization_scope,
                    actor_type, actor_id, credential_kind, request_id, correlation_id, request_source,
                    repository_id, expected_base_commit, request_hash, state,
                    created_unix_ms, updated_unix_ms)
                VALUES (
                    $operationId, $operationKind, $scope,
                    $actorType, $actorId, $credentialKind, $requestId, $correlationId, $requestSource,
                    $repositoryId, $expectedBaseCommit, $requestHash, 'PENDING',
                    $createdAt, $updatedAt);
                """;
            insert.Parameters.AddWithValue("$operationId", intent.OperationId);
            insert.Parameters.AddWithValue("$operationKind", intent.OperationKind);
            insert.Parameters.AddWithValue("$scope", intent.MaterializationScope);
            insert.Parameters.AddWithValue("$actorType", context.Principal.ActorType);
            insert.Parameters.AddWithValue("$actorId", context.Principal.ActorId);
            insert.Parameters.AddWithValue("$credentialKind", context.Principal.CredentialKind);
            insert.Parameters.AddWithValue("$requestId", context.RequestId!);
            insert.Parameters.AddWithValue("$correlationId", context.CorrelationId);
            insert.Parameters.AddWithValue("$requestSource", context.Source);
            insert.Parameters.AddWithValue("$repositoryId", intent.ExpectedBase.RepositoryId);
            insert.Parameters.AddWithValue("$expectedBaseCommit", intent.ExpectedBase.Commit);
            insert.Parameters.AddWithValue("$requestHash", intent.RequestHash);
            insert.Parameters.AddWithValue("$createdAt", now.ToUnixTimeMilliseconds());
            insert.Parameters.AddWithValue("$updatedAt", now.ToUnixTimeMilliseconds());
            insert.ExecuteNonQuery();
        }

        InsertTargets(connection, transaction, intent.OperationId, intent.Targets ?? []);

        AuditEventStore.Append(
            connection,
            transaction,
            AuditEventDraft.Create(
                context,
                now,
                "configuration.mutation.requested",
                "configuration-scope",
                intent.MaterializationScope,
                details: AuditDetails(
                    intent.OperationId,
                    intent.ExpectedBase.RepositoryId,
                    intent.Targets ?? [])) with
            {
                BaseRevision = intent.ExpectedBase.Canonical,
            });
        transaction.Commit();
        return new ConfigurationMutationBeginResult(
            ConfigurationMutationBeginOutcome.Created,
            new ConfigurationMutationOperation(
                intent.OperationId,
                intent.OperationKind,
                intent.MaterializationScope,
                context,
                intent.ExpectedBase,
                intent.RequestHash,
                ConfigurationMutationState.Pending,
                ResultRevision: null,
                CandidateAggregateContentHash: null,
                FailureCode: string.Empty,
                FailureSummary: string.Empty,
                RevisionSetId: null,
                ConflictRevision: null,
                Diff: [],
                Targets: intent.Targets ?? [],
                now,
                now));
    });

    public Task<ConfigurationMutationOperation?> GetAsync(string operationId) =>
        database.ReadAsync(connection =>
        {
            RequireBounded(operationId, 128, "operation ID");
            return ReadByOperationId(connection, transaction: null, operationId);
        });

    public Task<ConfigurationMutationOperation> RecordGitResultAsync(
        string operationId,
        ConfigurationCommitResult result) => database.WriteAsync(connection =>
    {
        ArgumentNullException.ThrowIfNull(result);
        RequireBounded(operationId, 128, "operation ID");
        ValidateGitResultAggregateHash(result);
        using var transaction = connection.BeginTransaction();
        var existing = ReadByOperationId(connection, transaction, operationId)
            ?? throw new InvalidOperationException($"configuration operation '{operationId}' does not exist");
        if (result.ExpectedBase != existing.ExpectedBase)
        {
            throw new InvalidOperationException("Git result does not match the operation's expected base revision");
        }

        if (existing.State != ConfigurationMutationState.Pending)
        {
            EnsureRepeatedResultMatches(existing, result);
            transaction.Commit();
            return existing;
        }

        var now = timeProvider.GetUtcNow();
        var (state, resultRevision, failureCode, failureSummary, auditOutcome, action) =
            ClassifyResult(result);
        string? revisionSetId = null;
        if (result.Outcome == ConfigurationCommitOutcome.Unchanged)
        {
            revisionSetId = FindActiveRevisionSetId(
                connection,
                transaction,
                existing.MaterializationScope,
                resultRevision!,
                result.CandidateAggregateContentHash!);
            if (revisionSetId is not null)
            {
                state = ConfigurationMutationState.Applied;
            }
        }

        if (result.Outcome == ConfigurationCommitOutcome.Conflict)
        {
            InsertConflictEvidence(connection, transaction, operationId, result);
        }

        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE configuration_mutation_operations SET
                    state = $state,
                    result_commit = $resultCommit,
                    candidate_content_hash = $candidateContentHash,
                    failure_code = $failureCode,
                    failure_summary = $failureSummary,
                    revision_set_id = $revisionSetId,
                    updated_unix_ms = $updatedAt
                WHERE operation_id = $operationId AND state = 'PENDING';
                """;
            update.Parameters.AddWithValue("$state", Serialize(state));
            update.Parameters.AddWithValue(
                "$resultCommit",
                (object?)resultRevision?.Commit ?? DBNull.Value);
            update.Parameters.AddWithValue(
                "$candidateContentHash",
                (object?)result.CandidateAggregateContentHash ?? DBNull.Value);
            update.Parameters.AddWithValue("$failureCode", failureCode);
            update.Parameters.AddWithValue("$failureSummary", failureSummary);
            update.Parameters.AddWithValue("$revisionSetId", (object?)revisionSetId ?? DBNull.Value);
            update.Parameters.AddWithValue("$updatedAt", now.ToUnixTimeMilliseconds());
            update.Parameters.AddWithValue("$operationId", operationId);
            if (update.ExecuteNonQuery() != 1)
            {
                throw new InvalidOperationException("configuration operation changed unexpectedly");
            }
        }

        AuditEventStore.Append(
            connection,
            transaction,
            AuditEventDraft.Create(
                existing.RequestContext,
                now,
                action,
                "configuration-scope",
                existing.MaterializationScope,
                auditOutcome,
                failureCode,
                AuditDetails(
                    existing.OperationId,
                    existing.ExpectedBase.RepositoryId,
                    existing.Targets)) with
            {
                BaseRevision = existing.ExpectedBase.Canonical,
                ResultRevision = resultRevision?.Canonical,
            });
        transaction.Commit();
        return existing with
        {
            State = state,
            ResultRevision = resultRevision,
            CandidateAggregateContentHash = result.CandidateAggregateContentHash,
            FailureCode = failureCode,
            FailureSummary = failureSummary,
            RevisionSetId = revisionSetId,
            ConflictRevision = result.Outcome == ConfigurationCommitOutcome.Conflict
                ? result.CurrentRevision
                : null,
            Diff = result.Diff,
            UpdatedAt = now,
        };
    });

    public Task<ConfigurationRepositoryAttemptFailure> RecordRepositoryAttemptFailureAsync(
        string operationId,
        string attemptId,
        string failureCode) => database.WriteAsync(connection =>
    {
        RequireBounded(operationId, 128, "operation ID");
        RequireBounded(attemptId, 128, "repository attempt ID");
        RequireBounded(failureCode, 128, "repository failure code");
        using var transaction = connection.BeginTransaction();
        var operation = ReadByOperationId(connection, transaction, operationId)
            ?? throw new InvalidOperationException($"configuration operation '{operationId}' does not exist");
        if (operation.State != ConfigurationMutationState.Pending)
        {
            throw new InvalidOperationException(
                "repository failures may be recorded only while a configuration operation is retryable");
        }

        var now = timeProvider.GetUtcNow();
        const string summary = "the configuration repository attempt failed and remains retryable";
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO configuration_repository_attempt_failures(
                    attempt_id, operation_id, failure_code, failure_summary, attempted_unix_ms)
                VALUES ($attemptId, $operationId, $failureCode, $failureSummary, $attemptedAt)
                ON CONFLICT(attempt_id) DO NOTHING;
                """;
            insert.Parameters.AddWithValue("$attemptId", attemptId);
            insert.Parameters.AddWithValue("$operationId", operationId);
            insert.Parameters.AddWithValue("$failureCode", failureCode);
            insert.Parameters.AddWithValue("$failureSummary", summary);
            insert.Parameters.AddWithValue("$attemptedAt", now.ToUnixTimeMilliseconds());
            if (insert.ExecuteNonQuery() != 1)
            {
                var existing = ReadRepositoryFailure(connection, transaction, attemptId);
                if (existing is null ||
                    !string.Equals(existing.OperationId, operationId, StringComparison.Ordinal) ||
                    !string.Equals(existing.FailureCode, failureCode, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "repository attempt identity belongs to different failure evidence");
                }

                transaction.Commit();
                return existing;
            }
        }

        AuditEventStore.Append(
            connection,
            transaction,
            AuditEventDraft.Create(
                operation.RequestContext,
                now,
                "configuration.mutation.repository_attempt_failed",
                "configuration-scope",
                operation.MaterializationScope,
                AuditOutcome.Failed,
                failureCode,
                AuditDetails(
                    operation.OperationId,
                    operation.ExpectedBase.RepositoryId,
                    operation.Targets,
                    attemptId)) with
            {
                BaseRevision = operation.ExpectedBase.Canonical,
            });
        transaction.Commit();
        return new ConfigurationRepositoryAttemptFailure(
            attemptId,
            operationId,
            failureCode,
            summary,
            now);
    });

    public Task<IReadOnlyList<ConfigurationRepositoryAttemptFailure>>
        ListRepositoryAttemptFailuresAsync(string operationId) => database.ReadAsync(connection =>
        {
            RequireBounded(operationId, 128, "operation ID");
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT attempt_id, operation_id, failure_code, failure_summary, attempted_unix_ms
                FROM configuration_repository_attempt_failures
                WHERE operation_id = $operationId
                ORDER BY attempted_unix_ms, attempt_id COLLATE BINARY;
                """;
            command.Parameters.AddWithValue("$operationId", operationId);
            using var reader = command.ExecuteReader();
            var result = new List<ConfigurationRepositoryAttemptFailure>();
            while (reader.Read())
            {
                result.Add(ReadRepositoryFailure(reader));
            }

            return (IReadOnlyList<ConfigurationRepositoryAttemptFailure>)result;
        });

    public Task<ConfigurationMutationOperation?> RecoverCommittedHeadAsync(
        ValidatedConfigurationRevision revision) => database.WriteAsync(connection =>
    {
        ArgumentNullException.ThrowIfNull(revision);
        var provenance = revision.Descriptor.ControllerProvenance;
        if (provenance is null)
        {
            return null;
        }

        ValidateContentHash(revision.Descriptor.AggregateContentHash, "aggregate content hash");
        using var transaction = connection.BeginTransaction();
        var existing = ReadByOperationId(connection, transaction, provenance.OperationId);
        if (existing is null)
        {
            transaction.Commit();
            return null;
        }

        var descriptor = revision.Descriptor;
        if (!string.Equals(existing.RequestContext.Principal.ActorType, provenance.ActorType, StringComparison.Ordinal) ||
            !string.Equals(existing.RequestContext.Principal.ActorId, provenance.ActorId, StringComparison.Ordinal) ||
            !string.Equals(existing.RequestContext.RequestId, provenance.RequestId, StringComparison.Ordinal) ||
            !string.Equals(existing.RequestContext.CorrelationId, provenance.CorrelationId, StringComparison.Ordinal) ||
            !string.Equals(existing.ExpectedBase.RepositoryId, descriptor.Revision.RepositoryId, StringComparison.Ordinal) ||
            !descriptor.Parents.Contains(existing.ExpectedBase))
        {
            throw new InvalidOperationException(
                "controller commit provenance does not match its durable configuration intent");
        }

        if (existing.State != ConfigurationMutationState.Pending)
        {
            if (existing.State is not ConfigurationMutationState.Committed and not ConfigurationMutationState.Applied ||
                existing.ResultRevision != descriptor.Revision ||
                !string.Equals(
                    existing.CandidateAggregateContentHash,
                    descriptor.AggregateContentHash,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "controller commit provenance conflicts with the durable configuration operation");
            }

            transaction.Commit();
            return existing;
        }

        var now = timeProvider.GetUtcNow();
        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE configuration_mutation_operations SET
                    state = 'COMMITTED',
                    result_commit = $resultCommit,
                    candidate_content_hash = $candidateContentHash,
                    updated_unix_ms = $updatedAt
                WHERE operation_id = $operationId AND state = 'PENDING';
                """;
            update.Parameters.AddWithValue("$resultCommit", descriptor.Revision.Commit);
            update.Parameters.AddWithValue("$candidateContentHash", descriptor.AggregateContentHash);
            update.Parameters.AddWithValue("$updatedAt", now.ToUnixTimeMilliseconds());
            update.Parameters.AddWithValue("$operationId", existing.OperationId);
            if (update.ExecuteNonQuery() != 1)
            {
                throw new InvalidOperationException("configuration operation changed unexpectedly");
            }
        }

        AuditEventStore.Append(
            connection,
            transaction,
            AuditEventDraft.Create(
                existing.RequestContext,
                now,
                "configuration.mutation.commit_recovered",
                "configuration-scope",
                existing.MaterializationScope,
                details: AuditDetails(
                    existing.OperationId,
                    existing.ExpectedBase.RepositoryId,
                    existing.Targets)) with
            {
                BaseRevision = existing.ExpectedBase.Canonical,
                ResultRevision = descriptor.Revision.Canonical,
            });
        transaction.Commit();
        return existing with
        {
            State = ConfigurationMutationState.Committed,
            ResultRevision = descriptor.Revision,
            CandidateAggregateContentHash = descriptor.AggregateContentHash,
            UpdatedAt = now,
        };
    });

    private static ConfigurationMutationOperation? ReadByIdempotencyKey(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string actorType,
        string actorId,
        string operationKind,
        string requestId)
    {
        using var command = CreateReadCommand(connection, transaction);
        command.CommandText += """
             WHERE operations.actor_type = $actorType AND operations.actor_id = $actorId
                AND operations.operation_kind = $operationKind
                AND operations.request_id = $requestId;
            """;
        command.Parameters.AddWithValue("$actorType", actorType);
        command.Parameters.AddWithValue("$actorId", actorId);
        command.Parameters.AddWithValue("$operationKind", operationKind);
        command.Parameters.AddWithValue("$requestId", requestId);
        ConfigurationMutationOperation? operation;
        using (var reader = command.ExecuteReader())
        {
            operation = reader.Read() ? ReadOperation(reader) : null;
        }

        return operation is null
            ? null
            : operation with { Targets = ReadTargets(connection, transaction, operation.OperationId) };
    }

    private static ConfigurationMutationOperation? ReadByOperationId(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string operationId)
    {
        using var command = CreateReadCommand(connection, transaction);
        command.CommandText += " WHERE operations.operation_id = $operationId;";
        command.Parameters.AddWithValue("$operationId", operationId);
        ConfigurationMutationOperation? operation;
        using (var reader = command.ExecuteReader())
        {
            operation = reader.Read() ? ReadOperation(reader) : null;
        }

        return operation is null
            ? null
            : operation with { Targets = ReadTargets(connection, transaction, operation.OperationId) };
    }

    private static SqliteCommand CreateReadCommand(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                operations.operation_id, operations.operation_kind, operations.materialization_scope,
                operations.actor_type, operations.actor_id, operations.credential_kind,
                operations.request_id, operations.correlation_id, operations.request_source,
                operations.repository_id, operations.expected_base_commit,
                operations.request_hash, operations.state,
                operations.result_commit, operations.candidate_content_hash,
                operations.failure_code, operations.failure_summary,
                operations.revision_set_id, operations.created_unix_ms, operations.updated_unix_ms,
                conflicts.current_repository_id, conflicts.current_commit, conflicts.diff_json
            FROM configuration_mutation_operations operations
            LEFT JOIN configuration_mutation_conflicts conflicts
                ON conflicts.operation_id = operations.operation_id
            """;
        return command;
    }

    private static ConfigurationMutationOperation ReadOperation(SqliteDataReader reader)
    {
        var requestContext = new ManagementRequestContext(
            new ManagementPrincipal(
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                LegacyScope: null),
            reader.GetString(7),
            reader.GetString(6),
            reader.GetString(8));
        var expected = new ConfigurationRevision(reader.GetString(9), reader.GetString(10));
        return new ConfigurationMutationOperation(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            requestContext,
            expected,
            reader.GetString(11),
            ParseState(reader.GetString(12)),
            reader.IsDBNull(13)
                ? null
                : new ConfigurationRevision(expected.RepositoryId, reader.GetString(13)),
            reader.IsDBNull(14) ? null : reader.GetString(14),
            reader.GetString(15),
            reader.GetString(16),
            reader.IsDBNull(17) ? null : reader.GetString(17),
            reader.IsDBNull(20)
                ? null
                : new ConfigurationRevision(reader.GetString(20), reader.GetString(21)),
            reader.IsDBNull(22) ? [] : DeserializeDiff(reader.GetString(22)),
            Targets: [],
            CreatedAt: DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(18)),
            UpdatedAt: DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(19)));
    }

    private static string? FindActiveRevisionSetId(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string scope,
        ConfigurationRevision revision,
        string aggregateContentHash)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT s.revision_set_id
            FROM configuration_revision_sets s
            JOIN configuration_revision_members m ON m.revision_set_id = s.revision_set_id
            WHERE s.materialization_scope = $scope AND s.state = 'ACTIVE'
                AND m.repository_id = $repositoryId AND m.commit_sha = $commit
                AND m.content_hash = $contentHash;
            """;
        command.Parameters.AddWithValue("$scope", scope);
        command.Parameters.AddWithValue("$repositoryId", revision.RepositoryId);
        command.Parameters.AddWithValue("$commit", revision.Commit);
        command.Parameters.AddWithValue("$contentHash", aggregateContentHash);
        return command.ExecuteScalar() as string;
    }

    private static void InsertTargets(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string operationId,
        IReadOnlyList<ConfigurationMutationTarget> targets)
    {
        for (var index = 0; index < targets.Count; index++)
        {
            var target = targets[index];
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO configuration_mutation_targets(
                    operation_id, ordinal, target_type, target_id, path)
                VALUES ($operationId, $ordinal, $targetType, $targetId, $path);
                """;
            command.Parameters.AddWithValue("$operationId", operationId);
            command.Parameters.AddWithValue("$ordinal", index);
            command.Parameters.AddWithValue("$targetType", target.TargetType);
            command.Parameters.AddWithValue("$targetId", target.TargetId);
            command.Parameters.AddWithValue("$path", target.Path);
            command.ExecuteNonQuery();
        }
    }

    private static IReadOnlyList<ConfigurationMutationTarget> ReadTargets(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string operationId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT target_type, target_id, path
            FROM configuration_mutation_targets
            WHERE operation_id = $operationId
            ORDER BY ordinal;
            """;
        command.Parameters.AddWithValue("$operationId", operationId);
        using var reader = command.ExecuteReader();
        var targets = new List<ConfigurationMutationTarget>();
        while (reader.Read())
        {
            targets.Add(new ConfigurationMutationTarget(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2)));
        }

        return targets;
    }

    private static void InsertConflictEvidence(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string operationId,
        ConfigurationCommitResult result)
    {
        ValidateDiff(result.Diff);
        var diffJson = SerializeDiff(result.Diff);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO configuration_mutation_conflicts(
                operation_id, current_repository_id, current_commit, diff_json)
            VALUES ($operationId, $repositoryId, $commit, $diffJson);
            """;
        command.Parameters.AddWithValue("$operationId", operationId);
        command.Parameters.AddWithValue("$repositoryId", result.CurrentRevision.RepositoryId);
        command.Parameters.AddWithValue("$commit", result.CurrentRevision.Commit);
        command.Parameters.AddWithValue("$diffJson", diffJson);
        command.ExecuteNonQuery();
    }

    private static ConfigurationRepositoryAttemptFailure? ReadRepositoryFailure(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string attemptId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT attempt_id, operation_id, failure_code, failure_summary, attempted_unix_ms
            FROM configuration_repository_attempt_failures
            WHERE attempt_id = $attemptId;
            """;
        command.Parameters.AddWithValue("$attemptId", attemptId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadRepositoryFailure(reader) : null;
    }

    private static ConfigurationRepositoryAttemptFailure ReadRepositoryFailure(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)));

    private static IReadOnlyDictionary<string, string> AuditDetails(
        string operationId,
        string repositoryId,
        IReadOnlyList<ConfigurationMutationTarget> targets,
        string? repositoryAttemptId = null)
    {
        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["operation_id"] = operationId,
            ["repository_id"] = repositoryId,
            ["affected_target_count"] = targets.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        if (repositoryAttemptId is not null)
        {
            details["repository_attempt_id"] = repositoryAttemptId;
        }

        if (targets.FirstOrDefault() is { } target)
        {
            details["affected_target_type"] = target.TargetType;
            details["affected_target_id"] = target.TargetId;
            details["affected_path"] = target.Path;
        }

        return details;
    }

    private static string SerializeDiff(IReadOnlyList<ConfigurationPathDiff> diff)
    {
        var json = JsonSerializer.Serialize(diff);
        if (json.Length > 32768)
        {
            throw new ArgumentException("configuration conflict diff exceeds the 32768-character bound");
        }

        return json;
    }

    private static IReadOnlyList<ConfigurationPathDiff> DeserializeDiff(string json)
    {
        var diff = JsonSerializer.Deserialize<List<ConfigurationPathDiff>>(json)
            ?? throw new InvalidDataException("configuration conflict diff is invalid");
        try
        {
            ValidateDiff(diff);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("configuration conflict diff is invalid", exception);
        }

        return diff;
    }

    private static void ValidateDiff(IReadOnlyList<ConfigurationPathDiff> diff)
    {
        if (diff.Count > 32)
        {
            throw new ArgumentException("configuration conflict diff may contain at most 32 paths");
        }

        foreach (var item in diff)
        {
            ArgumentNullException.ThrowIfNull(item);
            RequireBounded(item.Path, 512, "configuration diff path");
            if (item.PreviousContentHash is { } previous)
            {
                ValidateContentHash(previous, "previous content hash");
            }

            if (item.ResultContentHash is { } result)
            {
                ValidateContentHash(result, "result content hash");
            }

            var invalidShape = item.ChangeKind switch
            {
                ConfigurationPathChangeKind.Added =>
                    item.PreviousContentHash is not null,
                ConfigurationPathChangeKind.Removed =>
                    item.ResultContentHash is not null,
                ConfigurationPathChangeKind.Unchanged =>
                    item.PreviousContentHash is null || item.ResultContentHash is null,
                ConfigurationPathChangeKind.Modified => false,
                _ => true,
            };
            if (invalidShape)
            {
                throw new ArgumentException("configuration diff content hashes do not match its change kind");
            }
        }
    }

    private static bool MatchesIntent(
        ConfigurationMutationOperation existing,
        ConfigurationMutationIntent intent) =>
        string.Equals(existing.RequestHash, intent.RequestHash, StringComparison.Ordinal) &&
        string.Equals(existing.MaterializationScope, intent.MaterializationScope, StringComparison.Ordinal) &&
        existing.ExpectedBase == intent.ExpectedBase &&
        (existing.Targets.Count == 0 || existing.Targets.SequenceEqual(intent.Targets ?? []));

    private static void EnsureRepeatedResultMatches(
        ConfigurationMutationOperation existing,
        ConfigurationCommitResult result)
    {
        var (_, resultRevision, failureCode, failureSummary, _, _) = ClassifyResult(result);
        if (existing.ResultRevision != resultRevision ||
            !string.Equals(
                existing.CandidateAggregateContentHash,
                result.CandidateAggregateContentHash,
                StringComparison.Ordinal) ||
            !string.Equals(existing.FailureCode, failureCode, StringComparison.Ordinal) ||
            !string.Equals(existing.FailureSummary, failureSummary, StringComparison.Ordinal) ||
            result.Outcome == ConfigurationCommitOutcome.Conflict &&
            (existing.ConflictRevision != result.CurrentRevision ||
             !existing.Diff.SequenceEqual(result.Diff)))
        {
            throw new InvalidOperationException("Git result conflicts with the completed configuration operation");
        }
    }

    private static (
        ConfigurationMutationState State,
        ConfigurationRevision? ResultRevision,
        string FailureCode,
        string FailureSummary,
        AuditOutcome AuditOutcome,
        string Action) ClassifyResult(ConfigurationCommitResult result)
    {
        if (result.CurrentRevision.RepositoryId != result.ExpectedBase.RepositoryId ||
            result.ResultRevision is { } revision && revision.RepositoryId != result.ExpectedBase.RepositoryId)
        {
            throw new InvalidOperationException("Git result crosses repository identities");
        }

        if (result.Outcome == ConfigurationCommitOutcome.Unchanged &&
            (result.CurrentRevision != result.ExpectedBase ||
             result.ResultRevision is not null && result.ResultRevision != result.ExpectedBase))
        {
            throw new InvalidOperationException(
                "unchanged Git result must retain the exact expected base revision");
        }

        return result.Outcome switch
        {
            ConfigurationCommitOutcome.Committed => (
                ConfigurationMutationState.Committed,
                result.ResultRevision ?? throw new InvalidOperationException("committed Git result has no revision"),
                string.Empty,
                string.Empty,
                AuditOutcome.Succeeded,
                "configuration.mutation.committed"),
            ConfigurationCommitOutcome.Unchanged => (
                ConfigurationMutationState.Committed,
                result.ResultRevision ?? result.CurrentRevision,
                string.Empty,
                string.Empty,
                AuditOutcome.NoChange,
                "configuration.mutation.no_change"),
            ConfigurationCommitOutcome.Conflict => (
                ConfigurationMutationState.Conflict,
                null,
                "configuration_base_conflict",
                "the authoritative configuration revision changed",
                AuditOutcome.Failed,
                "configuration.mutation.conflicted"),
            ConfigurationCommitOutcome.Rejected => (
                ConfigurationMutationState.Rejected,
                null,
                FirstDiagnostic(result.Diagnostics).Code,
                FirstDiagnostic(result.Diagnostics).Summary,
                AuditOutcome.Failed,
                "configuration.mutation.rejected"),
            _ => throw new ArgumentOutOfRangeException(nameof(result)),
        };
    }

    private static ConfigurationValidationDiagnostic FirstDiagnostic(
        IReadOnlyList<ConfigurationValidationDiagnostic> diagnostics)
    {
        var diagnostic = diagnostics.FirstOrDefault()
            ?? new ConfigurationValidationDiagnostic(
                "configuration_invalid",
                Path: null,
                Field: null,
                "configuration validation failed");
        RequireBounded(diagnostic.Code, 128, "diagnostic code");
        RequireBounded(diagnostic.Summary, 512, "diagnostic summary");
        return diagnostic;
    }

    private static void ValidateIntent(
        ManagementRequestContext context,
        ConfigurationMutationIntent intent)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(intent);
        RequireBounded(intent.OperationId, 128, "operation ID");
        RequireBounded(intent.OperationKind, 128, "operation kind");
        RequireBounded(intent.MaterializationScope, 128, "materialization scope");
        if (context.RequestId is null)
        {
            throw new ArgumentException("configuration mutations require a request ID", nameof(context));
        }

        ValidateContentHash(intent.RequestHash, "request hash");
        var targets = intent.Targets ?? [];
        if (targets.Count > 32)
        {
            throw new ArgumentException("configuration mutations may affect at most 32 targets");
        }

        foreach (var target in targets)
        {
            ArgumentNullException.ThrowIfNull(target);
            RequireBounded(target.TargetType, 64, "target type");
            RequireBounded(target.TargetId, 256, "target ID");
            RequireBounded(target.Path, 256, "target path");
        }

        if (targets.Distinct().Count() != targets.Count)
        {
            throw new ArgumentException("configuration mutation targets must be unique");
        }
    }

    private static void ValidateContentHash(string value, string name)
    {
        if (value.Length != 64 || value.Any(character =>
                !char.IsAsciiHexDigit(character) || char.IsUpper(character)))
        {
            throw new ArgumentException($"{name} must be a lowercase SHA-256 value");
        }
    }

    private static void ValidateGitResultAggregateHash(ConfigurationCommitResult result)
    {
        if (result.Outcome == ConfigurationCommitOutcome.Rejected)
        {
            if (result.CandidateAggregateContentHash is not null)
            {
                throw new ArgumentException("rejected Git results cannot claim a validated aggregate hash");
            }

            return;
        }

        if (result.CandidateAggregateContentHash is null)
        {
            throw new ArgumentException("validated Git results require a candidate aggregate hash");
        }

        ValidateContentHash(result.CandidateAggregateContentHash, "candidate aggregate content hash");
    }

    private static void RequireBounded(string value, int maximum, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum)
        {
            throw new ArgumentException($"{name} must be between 1 and {maximum} characters");
        }
    }

    private static string Serialize(ConfigurationMutationState state) => state switch
    {
        ConfigurationMutationState.Pending => "PENDING",
        ConfigurationMutationState.Committed => "COMMITTED",
        ConfigurationMutationState.Conflict => "CONFLICT",
        ConfigurationMutationState.Rejected => "REJECTED",
        ConfigurationMutationState.Applied => "APPLIED",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    private static ConfigurationMutationState ParseState(string state) => state switch
    {
        "PENDING" => ConfigurationMutationState.Pending,
        "COMMITTED" => ConfigurationMutationState.Committed,
        "CONFLICT" => ConfigurationMutationState.Conflict,
        "REJECTED" => ConfigurationMutationState.Rejected,
        "APPLIED" => ConfigurationMutationState.Applied,
        _ => throw new InvalidDataException($"unknown configuration operation state '{state}'"),
    };
}
