using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Vivarium.Controller.Auditing;
using Vivarium.Controller.Configuration.Git;
using Vivarium.Controller.Configuration.Reconciliation;
using Vivarium.Controller.Persistence;
using Vivarium.Controller.Security;

namespace Vivarium.Controller.Administration;

public sealed class AdministrationBootstrapService(
    VivariumDatabase database,
    IConfigurationRepository repository,
    ConfigurationReconciler reconciler,
    AuditEventStore audits,
    TimeProvider timeProvider)
{
    private const int VerifierIterations = 210_000;
    private static readonly TimeSpan BootstrapLifetime = TimeSpan.FromHours(24);
    private static readonly TimeSpan SetupSessionLifetime = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan ResumeTokenLifetime = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan RecoveryTokenLifetime = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan RecoverySessionLifetime = TimeSpan.FromMinutes(30);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public AdministrationStartup Startup { get; private set; } =
        new(string.Empty, AdministrationState.Unclaimed, null, null, null);

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var token = NewToken();
        var credential = Derive(token);
        var now = timeProvider.GetUtcNow();
        var startup = await database.WriteAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            var instance = ReadInstance(connection, transaction);
            if (instance is null)
            {
                instance = new StoredInstance(
                    ManagementIdentifiers.NewId(),
                    AdministrationState.Unclaimed,
                    1,
                    null,
                    now);
                using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO administration_instances(
                        instance_key, instance_id, state, state_version,
                        created_unix_ms, updated_unix_ms)
                    VALUES (1, $instanceId, 'UNCLAIMED', 1, $now, $now);
                    """;
                insert.Parameters.AddWithValue("$instanceId", instance.InstanceId);
                insert.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
                insert.ExecuteNonQuery();
            }

            if (instance.State != AdministrationState.Unclaimed)
            {
                transaction.Commit();
                return new AdministrationStartup(
                    instance.InstanceId,
                    instance.State,
                    null,
                    null,
                    null);
            }

            RevokeCurrentGeneration(
                connection,
                transaction,
                "BOOTSTRAP",
                now,
                "controller_restart");
            var generationId = ManagementIdentifiers.NewId();
            var expiresAt = now.Add(BootstrapLifetime);
            InsertGeneration(
                connection,
                transaction,
                generationId,
                "BOOTSTRAP",
                operationId: null,
                credential,
                now,
                expiresAt);
            AuditEventStore.Append(
                connection,
                transaction,
                AuditEventDraft.Create(
                    LocalOperatorContext("administration-bootstrap-startup"),
                    now,
                    "administration.bootstrap-issued",
                    "administration-instance",
                    instance.InstanceId,
                    details: new Dictionary<string, string>
                    {
                        ["generation_id"] = generationId,
                        ["expires_at"] = expiresAt.ToString("O"),
                        ["delivery"] = "private-console",
                    }));
            transaction.Commit();
            return new AdministrationStartup(
                instance.InstanceId,
                instance.State,
                generationId,
                token,
                expiresAt);
        });
        Startup = startup;
    }

    public Task<AdministrationStatus> GetStatusAsync() => database.ReadAsync(connection =>
    {
        var instance = ReadInstance(connection, transaction: null)
            ?? throw new InvalidOperationException("administration instance is not initialized");
        return new AdministrationStatus(
            instance.InstanceId,
            instance.State,
            instance.StateVersion,
            instance.SetupOperationId,
            instance.State == AdministrationState.Unclaimed ? "local-private-console" : null,
            instance.UpdatedAt);
    });

    public Task<SetupOperationSnapshot?> GetOperationAsync(string operationId) =>
        database.ReadAsync(connection => ReadOperation(connection, transaction: null, operationId));

    public async Task<SetupClaimResult> ClaimAsync(
        string token,
        string? suppliedCorrelationId,
        string source,
        CancellationToken cancellationToken = default)
    {
        ValidateToken(token);
        var now = timeProvider.GetUtcNow();
        var candidates = await ListCurrentClaimGenerationsAsync(now);
        var matched = candidates.FirstOrDefault(candidate => Verify(token, candidate.Credential));
        if (matched is null)
        {
            var denied = ManagementRequestContext.Anonymous(
                source,
                SafeCorrelation(suppliedCorrelationId));
            await audits.AppendAsync(AuditEventDraft.Create(
                denied,
                now,
                "administration.bootstrap-claim",
                "administration-instance",
                "first-run",
                AuditOutcome.Denied,
                "invalid_claim"));
            throw new AdministrationBootstrapException(
                "setup_claim_invalid",
                "The setup claim is invalid or expired.");
        }

        var sessionToken = NewToken();
        var sessionCredential = Derive(sessionToken);
        var correlationId = ManagementIdentifiers.NormalizeCorrelationId(suppliedCorrelationId);
        return await database.WriteAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            var generation = ReadGeneration(connection, transaction, matched.GenerationId);
            if (generation is null || generation.ConsumedAt is not null || generation.RevokedAt is not null ||
                generation.ExpiresAt < now || !Verify(token, generation.Credential))
            {
                throw new AdministrationBootstrapException(
                    "setup_claim_invalid",
                    "The setup claim is invalid or expired.");
            }

            var instance = ReadInstance(connection, transaction)
                ?? throw new InvalidOperationException("administration instance is not initialized");
            string operationId;
            var resumed = generation.Purpose == "SETUP_RESUME";
            if (generation.Purpose == "BOOTSTRAP")
            {
                if (instance.State != AdministrationState.Unclaimed)
                {
                    throw new AdministrationBootstrapException(
                        "setup_claim_unavailable",
                        "The controller already has a setup operation.");
                }

                operationId = ManagementIdentifiers.NewId();
                using (var operation = connection.CreateCommand())
                {
                    operation.Transaction = transaction;
                    operation.CommandText = """
                        INSERT INTO administration_setup_operations(
                            operation_id, state, state_version, correlation_id,
                            created_unix_ms, updated_unix_ms)
                        VALUES ($operationId, 'IN_PROGRESS', 1, $correlationId, $now, $now);
                        """;
                    operation.Parameters.AddWithValue("$operationId", operationId);
                    operation.Parameters.AddWithValue("$correlationId", correlationId);
                    operation.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
                    operation.ExecuteNonQuery();
                }

                using var updateInstance = connection.CreateCommand();
                updateInstance.Transaction = transaction;
                updateInstance.CommandText = """
                    UPDATE administration_instances SET
                        state = 'SETUP_IN_PROGRESS',
                        state_version = state_version + 1,
                        setup_operation_id = $operationId,
                        updated_unix_ms = $now
                    WHERE instance_key = 1 AND state = 'UNCLAIMED';
                    """;
                updateInstance.Parameters.AddWithValue("$operationId", operationId);
                updateInstance.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
                if (updateInstance.ExecuteNonQuery() != 1)
                {
                    throw new AdministrationBootstrapException(
                        "setup_claim_unavailable",
                        "The controller already has a setup operation.");
                }
            }
            else
            {
                operationId = generation.OperationId
                    ?? throw new InvalidOperationException("setup resume generation has no operation");
                if (instance.SetupOperationId != operationId ||
                    instance.State is AdministrationState.Active or AdministrationState.Unclaimed)
                {
                    throw new AdministrationBootstrapException(
                        "setup_claim_unavailable",
                        "The setup operation is no longer resumable.");
                }
            }

            ConsumeGeneration(connection, transaction, generation.GenerationId, now);
            RevokeSetupSessions(connection, transaction, operationId, now, "session_replaced");
            var sessionId = ManagementIdentifiers.NewId();
            var sessionExpiresAt = now.Add(SetupSessionLifetime);
            InsertSession(
                connection,
                transaction,
                sessionId,
                operationId,
                generation.GenerationId,
                sessionCredential,
                now,
                sessionExpiresAt);
            var context = new ManagementRequestContext(
                new ManagementPrincipal("setup", operationId, "setup-session", LegacyScope: null),
                correlationId,
                RequestId: null,
                source);
            AuditEventStore.Append(
                connection,
                transaction,
                AuditEventDraft.Create(
                    context,
                    now,
                    resumed
                        ? "administration.setup-resumed"
                        : "administration.bootstrap-claim",
                    "setup-operation",
                    operationId,
                    details: new Dictionary<string, string>
                    {
                        ["generation_id"] = generation.GenerationId,
                        ["session_id"] = sessionId,
                    }));
            var operationSnapshot = ReadOperation(connection, transaction, operationId)
                ?? throw new InvalidOperationException("setup operation was not persisted");
            transaction.Commit();
            return new SetupClaimResult(
                operationId,
                sessionToken,
                sessionExpiresAt,
                operationSnapshot.StateVersion,
                resumed);
        });
    }

    public async Task<SetupSessionAuthentication?> AuthenticateSetupSessionAsync(
        string token,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateToken(token);
        var now = timeProvider.GetUtcNow();
        var sessions = await database.ReadAsync<IReadOnlyList<StoredSession>>(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT session_id, operation_id, generation_id, token_salt, token_verifier,
                       issued_unix_ms, expires_unix_ms, revoked_unix_ms
                FROM administration_setup_sessions
                WHERE revoked_unix_ms IS NULL AND expires_unix_ms >= $now
                ORDER BY issued_unix_ms DESC, session_id COLLATE BINARY;
                """;
            command.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
            using var reader = command.ExecuteReader();
            var result = new List<StoredSession>();
            while (reader.Read())
            {
                result.Add(ReadSession(reader));
            }

            return result;
        });
        var session = sessions.FirstOrDefault(candidate => Verify(token, candidate.Credential));
        if (session is null)
        {
            return null;
        }

        var operation = await GetOperationAsync(session.OperationId);
        return operation is null || operation.State is SetupOperationState.Completed or SetupOperationState.Abandoned
            ? null
            : new SetupSessionAuthentication(session.SessionId, session.OperationId, operation);
    }

    public async Task<SetupAdministratorReservation> ReserveAdministratorAsync(
        SetupSessionAuthentication session,
        string requestId,
        long expectedStateVersion,
        string login,
        string displayName,
        string password,
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        ValidateRequestId(requestId);
        login = NormalizeLogin(login);
        displayName = NormalizeDisplayName(displayName);
        ValidatePassword(password);
        var requestHash = HashRequest("administrator", login, displayName);
        var passwordCredential = Derive(password);
        var now = timeProvider.GetUtcNow();
        return await database.WriteAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            EnsureSessionCurrent(connection, transaction, session, now);
            var existing = ReadStoredResponse<SetupAdministratorReservation>(
                connection,
                transaction,
                session.OperationId,
                "administrator",
                requestId,
                requestHash);
            if (existing is not null)
            {
                if (!ReservedPasswordMatches(
                        connection,
                        transaction,
                        session.OperationId,
                        password))
                {
                    throw new AdministrationBootstrapException(
                        "idempotency_key_reused",
                        "The Idempotency-Key was already used for different setup content.");
                }

                transaction.Commit();
                return existing with { Replayed = true };
            }

            var operation = ReadOperation(connection, transaction, session.OperationId)
                ?? throw new AdministrationBootstrapException("setup_operation_not_found", "The setup operation was not found.");
            DemandEditable(operation, expectedStateVersion);
            if (operation.PendingUserId is not null &&
                (!string.Equals(operation.PendingLogin, login, StringComparison.OrdinalIgnoreCase) ||
                 !string.Equals(operation.PendingDisplayName, displayName, StringComparison.Ordinal)))
            {
                throw new AdministrationBootstrapException(
                    "setup_administrator_conflict",
                    "The setup operation already reserved a different administrator.");
            }

            var userId = operation.PendingUserId ?? "user-" + ManagementIdentifiers.NewId();
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE administration_setup_operations SET
                    pending_user_id = $userId,
                    pending_login = $login,
                    pending_display_name = $displayName,
                    password_algorithm = 'PBKDF2-SHA256',
                    password_iterations = $iterations,
                    password_salt = $salt,
                    password_verifier = $verifier,
                    state_version = state_version + 1,
                    updated_unix_ms = $now
                WHERE operation_id = $operationId AND state = 'IN_PROGRESS'
                    AND state_version = $expectedVersion;
                """;
            update.Parameters.AddWithValue("$userId", userId);
            update.Parameters.AddWithValue("$login", login);
            update.Parameters.AddWithValue("$displayName", displayName);
            update.Parameters.AddWithValue("$iterations", VerifierIterations);
            update.Parameters.Add("$salt", SqliteType.Blob).Value = passwordCredential.Salt;
            update.Parameters.Add("$verifier", SqliteType.Blob).Value = passwordCredential.Verifier;
            update.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
            update.Parameters.AddWithValue("$operationId", session.OperationId);
            update.Parameters.AddWithValue("$expectedVersion", expectedStateVersion);
            if (update.ExecuteNonQuery() != 1)
            {
                throw new AdministrationBootstrapException(
                    "setup_state_conflict",
                    "The setup operation changed; refresh it before retrying.");
            }

            var response = new SetupAdministratorReservation(
                session.OperationId,
                userId,
                login,
                displayName,
                expectedStateVersion + 1,
                Replayed: false);
            StoreResponse(
                connection,
                transaction,
                session.OperationId,
                "administrator",
                requestId,
                requestHash,
                StatusCodes.Status200OK,
                response,
                now);
            AuditEventStore.Append(
                connection,
                transaction,
                AuditEventDraft.Create(
                    SetupContext(session.OperationId, correlationId, requestId, "rest-setup-administrator"),
                    now,
                    "administration.identity-reserved",
                    "user",
                    userId));
            transaction.Commit();
            return response;
        });
    }

    public async Task<SetupRepositoryReservation> ConfigureManagedLocalRepositoryAsync(
        SetupSessionAuthentication session,
        string requestId,
        long expectedStateVersion,
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        ValidateRequestId(requestId);
        var head = await repository.GetAuthoritativeHeadAsync(cancellationToken);
        var validation = await repository.ValidateRevisionAsync(head, cancellationToken);
        if (!validation.IsValid)
        {
            throw new AdministrationBootstrapException(
                "setup_repository_invalid",
                "The managed-local configuration repository is not valid.");
        }

        var requestHash = HashRequest("repository", "managed-local", head.Canonical);
        var now = timeProvider.GetUtcNow();
        return await database.WriteAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            EnsureSessionCurrent(connection, transaction, session, now);
            var existing = ReadStoredResponse<SetupRepositoryReservation>(
                connection,
                transaction,
                session.OperationId,
                "repository",
                requestId,
                requestHash);
            if (existing is not null)
            {
                transaction.Commit();
                return existing with { Replayed = true };
            }

            var operation = ReadOperation(connection, transaction, session.OperationId)
                ?? throw new AdministrationBootstrapException("setup_operation_not_found", "The setup operation was not found.");
            DemandEditable(operation, expectedStateVersion);
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE administration_setup_operations SET
                    repository_mode = 'managed-local',
                    repository_id = $repositoryId,
                    expected_base_commit = $commit,
                    state_version = state_version + 1,
                    updated_unix_ms = $now
                WHERE operation_id = $operationId AND state = 'IN_PROGRESS'
                    AND state_version = $expectedVersion;
                """;
            update.Parameters.AddWithValue("$repositoryId", head.RepositoryId);
            update.Parameters.AddWithValue("$commit", head.Commit);
            update.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
            update.Parameters.AddWithValue("$operationId", session.OperationId);
            update.Parameters.AddWithValue("$expectedVersion", expectedStateVersion);
            if (update.ExecuteNonQuery() != 1)
            {
                throw new AdministrationBootstrapException(
                    "setup_state_conflict",
                    "The setup operation changed; refresh it before retrying.");
            }

            var response = new SetupRepositoryReservation(
                session.OperationId,
                "managed-local",
                head.RepositoryId,
                head.Commit,
                expectedStateVersion + 1,
                Replayed: false);
            StoreResponse(
                connection,
                transaction,
                session.OperationId,
                "repository",
                requestId,
                requestHash,
                StatusCodes.Status200OK,
                response,
                now);
            AuditEventStore.Append(
                connection,
                transaction,
                AuditEventDraft.Create(
                    SetupContext(session.OperationId, correlationId, requestId, "rest-setup-repository"),
                    now,
                    "administration.git-validated",
                    "configuration-repository",
                    head.RepositoryId,
                    details: new Dictionary<string, string>
                    {
                        ["commit"] = head.Commit,
                        ["mode"] = "managed-local",
                    }) with { ResultRevision = head.Canonical });
            transaction.Commit();
            return response;
        });
    }

    public async Task<SetupCompletionResult> CompleteSetupAsync(
        SetupSessionAuthentication session,
        string requestId,
        long expectedStateVersion,
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        ValidateRequestId(requestId);
        var operation = await GetOperationAsync(session.OperationId)
            ?? throw new AdministrationBootstrapException(
                "setup_operation_not_found",
                "The setup operation was not found.");
        if (operation.State == SetupOperationState.InProgress)
        {
            DemandEditable(operation, expectedStateVersion);
        }
        else if (operation.State != SetupOperationState.Activating)
        {
            throw new AdministrationBootstrapException(
                "setup_operation_not_editable",
                "The setup operation cannot be completed in its current phase.");
        }

        if (operation.PendingUserId is null || operation.PendingLogin is null ||
            operation.PendingDisplayName is null || operation.RepositoryId is null ||
            operation.ExpectedBaseCommit is null || operation.RepositoryMode != "managed-local")
        {
            throw new AdministrationBootstrapException(
                "setup_completion_incomplete",
                "Reserve the administrator and managed-local repository before completion.");
        }

        var bindingId = "setup-" + operation.OperationId;
        var userPath = $".vivarium/rbac/users/{operation.PendingUserId}.yaml";
        var bindingPath = $".vivarium/rbac/bindings/{bindingId}.yaml";
        var userDocument = ConfigurationTreeValidator.RenderUser(
            operation.PendingUserId,
            operation.PendingLogin,
            operation.PendingDisplayName,
            active: true);
        var bindingDocument = ConfigurationTreeValidator.RenderRoleBinding(
            bindingId,
            "user",
            operation.PendingUserId,
            AuthorizationRoleIds.SystemAdministrator,
            "global",
            "global");
        ConfigurationRevision candidate;
        if (operation.CandidateCommit is not null)
        {
            candidate = new ConfigurationRevision(operation.RepositoryId, operation.CandidateCommit);
        }
        else
        {
            var expectedBase = new ConfigurationRevision(
                operation.RepositoryId,
                operation.ExpectedBaseCommit);
            var head = await repository.GetAuthoritativeHeadAsync(cancellationToken);
            if (head != expectedBase)
            {
                var validation = await repository.ValidateRevisionAsync(head, cancellationToken);
                if (!IsRecoverableSetupCandidate(
                        validation,
                        operation.OperationId,
                        operation.PendingUserId,
                        bindingId))
                {
                    throw new AdministrationBootstrapException(
                        "setup_repository_conflict",
                        "The authoritative configuration changed after setup reviewed its baseline.");
                }

                candidate = head;
            }
            else
            {
                var commit = await repository.UpsertDocumentsAsync(
                    new ConfigurationTreeMutation(
                        expectedBase,
                        [
                            new ConfigurationDocumentUpsert(userPath, userDocument),
                            new ConfigurationDocumentUpsert(bindingPath, bindingDocument),
                        ],
                        new ConfigurationCommitMetadata(
                            "Establish the first Vivarium administrator",
                            operation.OperationId,
                            requestId,
                            ManagementIdentifiers.NormalizeCorrelationId(correlationId),
                            new ConfigurationCommitActor(
                                operation.PendingUserId,
                                "setup",
                                operation.PendingDisplayName))),
                    cancellationToken);
                if (commit.Outcome is ConfigurationCommitOutcome.Conflict)
                {
                    throw new AdministrationBootstrapException(
                        "setup_repository_conflict",
                        "The authoritative configuration changed while setup was committing.");
                }

                if (commit.Outcome is ConfigurationCommitOutcome.Rejected ||
                    commit.ResultRevision is null)
                {
                    throw new AdministrationBootstrapException(
                        "setup_repository_invalid",
                        commit.Diagnostics.FirstOrDefault()?.Summary ??
                        "The first administrator configuration was rejected.");
                }

                candidate = commit.ResultRevision;
            }

            operation = await MarkActivatingAsync(
                session,
                operation,
                candidate,
                cancellationToken);
        }

        var setupContext = SetupContext(
            operation.OperationId,
            correlationId,
            requestId,
            "rest-setup-completion");
        var reconciliation = await reconciler.ReconcileAuthoritativeHeadAsync(
            setupContext,
            "controller",
            repository,
            cancellationToken: cancellationToken);
        var activeRevision = reconciliation.State.Active?.Members.FirstOrDefault(member =>
            member.RepositoryRole == "CONTROL")?.Commit;
        if (reconciliation.Outcome is not (
                ConfigurationReconciliationOutcome.Applied or
                ConfigurationReconciliationOutcome.NoChange) ||
            !string.Equals(activeRevision, candidate.Commit, StringComparison.Ordinal))
        {
            throw new AdministrationBootstrapException(
                "setup_activation_blocked",
                "The first administrator commit is not the active configuration revision.");
        }

        var now = timeProvider.GetUtcNow();
        return await database.WriteAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            EnsureSessionCurrent(connection, transaction, session, now);
            var current = ReadOperation(connection, transaction, operation.OperationId)
                ?? throw new AdministrationBootstrapException(
                    "setup_operation_not_found",
                    "The setup operation was not found.");
            if (current.State != SetupOperationState.Activating ||
                !string.Equals(current.CandidateCommit, candidate.Commit, StringComparison.Ordinal))
            {
                throw new AdministrationBootstrapException(
                    "setup_state_conflict",
                    "The setup operation changed; refresh it before retrying.");
            }

            EnsureActivatedAuthorizationProjection(
                connection,
                transaction,
                current.PendingUserId!,
                bindingId,
                candidate);
            using (var credential = connection.CreateCommand())
            {
                credential.Transaction = transaction;
                credential.CommandText = """
                    INSERT INTO authorization_user_credentials(
                        user_id, credential_state, password_algorithm, password_iterations,
                        password_salt, password_verifier, credential_generation,
                        created_unix_ms, updated_unix_ms)
                    SELECT pending_user_id, 'ACTIVE', password_algorithm, password_iterations,
                           password_salt, password_verifier, 1, $now, $now
                    FROM administration_setup_operations
                    WHERE operation_id = $operationId
                        AND pending_user_id IS NOT NULL
                        AND password_verifier IS NOT NULL;
                    """;
                credential.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
                credential.Parameters.AddWithValue("$operationId", current.OperationId);
                if (credential.ExecuteNonQuery() != 1)
                {
                    throw new AdministrationBootstrapException(
                        "setup_credential_conflict",
                        "The administrator credential could not be activated.");
                }
            }

            using (var finish = connection.CreateCommand())
            {
                finish.Transaction = transaction;
                finish.CommandText = """
                    UPDATE administration_setup_operations SET
                        state = 'COMPLETED',
                        state_version = state_version + 1,
                        updated_unix_ms = $now
                    WHERE operation_id = $operationId AND state = 'ACTIVATING'
                        AND candidate_commit = $commit;

                    UPDATE administration_instances SET
                        state = 'ACTIVE',
                        state_version = state_version + 1,
                        active_user_id = $userId,
                        active_repository_id = $repositoryId,
                        active_commit = $commit,
                        updated_unix_ms = $now
                    WHERE instance_key = 1 AND state = 'SETUP_ACTIVATING'
                        AND setup_operation_id = $operationId;
                    """;
                finish.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
                finish.Parameters.AddWithValue("$operationId", current.OperationId);
                finish.Parameters.AddWithValue("$commit", candidate.Commit);
                finish.Parameters.AddWithValue("$userId", current.PendingUserId!);
                finish.Parameters.AddWithValue("$repositoryId", candidate.RepositoryId);
                if (finish.ExecuteNonQuery() != 2)
                {
                    throw new AdministrationBootstrapException(
                        "setup_state_conflict",
                        "The administration state changed while setup was activating.");
                }
            }

            RevokeCurrentGeneration(
                connection, transaction, "SETUP_RESUME", now, "setup_completed");
            RevokeSetupSessions(
                connection, transaction, current.OperationId, now, "setup_completed");
            var response = new SetupCompletionResult(
                current.OperationId,
                current.PendingUserId!,
                candidate.RepositoryId,
                candidate.Commit,
                current.StateVersion + 1,
                Active: true);
            StoreResponse(
                connection,
                transaction,
                current.OperationId,
                "completion",
                requestId,
                HashRequest("completion", current.PendingUserId!, candidate.Canonical),
                StatusCodes.Status200OK,
                response,
                now);
            AuditEventStore.Append(
                connection,
                transaction,
                AuditEventDraft.Create(
                    setupContext,
                    now,
                    "administration.setup-completed",
                    "administration-instance",
                    Startup.InstanceId,
                    details: new Dictionary<string, string>
                    {
                        ["operation_id"] = current.OperationId,
                        ["user_id"] = current.PendingUserId!,
                    }) with { ResultRevision = candidate.Canonical });
            transaction.Commit();
            return response;
        });
    }

    public Task<LocalSetupToken> ReissueSetupAccessAsync(string operationId) =>
        IssueOperationTokenAsync(
            operationId,
            "SETUP_RESUME",
            ResumeTokenLifetime,
            "administration.setup-access-reissued");

    public Task<LocalSetupToken> RotateBootstrapAsync() =>
        IssueBootstrapTokenAsync("local_rotation");

    public async Task<LocalSetupToken> IssueRecoveryAccessAsync(string reason)
    {
        reason = NormalizeLocalReason(reason);
        var token = NewToken();
        var credential = Derive(token);
        var now = timeProvider.GetUtcNow();
        return await database.WriteAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            var instance = ReadInstance(connection, transaction)
                ?? throw new InvalidOperationException("administration instance is not initialized");
            if (instance.State is not (
                    AdministrationState.Active or
                    AdministrationState.RecoveryAvailable or
                    AdministrationState.RecoveryInProgress) ||
                instance.SetupOperationId is null)
            {
                throw new AdministrationBootstrapException(
                    "recovery_unavailable",
                    "Recovery can be issued only for an active controller.");
            }

            var operation = ReadOperation(connection, transaction, instance.SetupOperationId);
            if (operation?.State != SetupOperationState.Completed)
            {
                throw new AdministrationBootstrapException(
                    "recovery_unavailable",
                    "The active administration operation is not recoverable.");
            }

            RevokeCurrentGeneration(connection, transaction, "RECOVERY", now, "recovery_reissued");
            RevokeSetupSessions(
                connection, transaction, operation.OperationId, now, "recovery_reissued");
            var generationId = ManagementIdentifiers.NewId();
            var expiresAt = now.Add(RecoveryTokenLifetime);
            InsertGeneration(
                connection,
                transaction,
                generationId,
                "RECOVERY",
                operation.OperationId,
                credential,
                now,
                expiresAt);
            using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE administration_instances SET
                        state = 'RECOVERY_AVAILABLE',
                        state_version = state_version + 1,
                        updated_unix_ms = $now
                    WHERE instance_key = 1 AND state IN (
                        'ACTIVE', 'RECOVERY_AVAILABLE', 'RECOVERY_IN_PROGRESS');
                    """;
                update.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
                if (update.ExecuteNonQuery() != 1)
                {
                    throw new AdministrationBootstrapException(
                        "recovery_state_conflict",
                        "The administration recovery state changed.");
                }
            }

            AuditEventStore.Append(
                connection,
                transaction,
                AuditEventDraft.Create(
                    LocalOperatorContext("administration-recovery-issue"),
                    now,
                    "administration.recovery-issued",
                    "administration-instance",
                    instance.InstanceId,
                    details: new Dictionary<string, string>
                    {
                        ["generation_id"] = generationId,
                        ["reason"] = reason,
                    }));
            transaction.Commit();
            return new LocalSetupToken(generationId, token, expiresAt, operation.OperationId);
        });
    }

    public async Task<RecoveryClaimResult> ExchangeRecoveryAsync(
        string token,
        string? suppliedCorrelationId,
        string source,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateToken(token);
        var now = timeProvider.GetUtcNow();
        var candidates = await ListCurrentRecoveryGenerationsAsync(now);
        var matched = candidates.FirstOrDefault(candidate => Verify(token, candidate.Credential));
        if (matched is null)
        {
            await audits.AppendAsync(AuditEventDraft.Create(
                ManagementRequestContext.Anonymous(source, SafeCorrelation(suppliedCorrelationId)),
                now,
                "administration.recovery-claim-failed",
                "administration-instance",
                "active",
                AuditOutcome.Denied,
                "invalid_claim"));
            throw new AdministrationBootstrapException(
                "recovery_claim_invalid",
                "The recovery claim is invalid or expired.");
        }

        var sessionToken = NewToken();
        var sessionCredential = Derive(sessionToken);
        var correlationId = ManagementIdentifiers.NormalizeCorrelationId(suppliedCorrelationId);
        return await database.WriteAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            var generation = ReadGeneration(connection, transaction, matched.GenerationId);
            if (generation is null || generation.Purpose != "RECOVERY" ||
                generation.OperationId is null || generation.ConsumedAt is not null ||
                generation.RevokedAt is not null || generation.ExpiresAt < now ||
                !Verify(token, generation.Credential))
            {
                throw new AdministrationBootstrapException(
                    "recovery_claim_invalid",
                    "The recovery claim is invalid or expired.");
            }

            var instance = ReadInstance(connection, transaction)
                ?? throw new InvalidOperationException("administration instance is not initialized");
            if (instance.State != AdministrationState.RecoveryAvailable ||
                instance.SetupOperationId != generation.OperationId)
            {
                throw new AdministrationBootstrapException(
                    "recovery_claim_unavailable",
                    "Recovery is no longer available for this controller.");
            }

            ConsumeGeneration(connection, transaction, generation.GenerationId, now);
            RevokeSetupSessions(
                connection, transaction, generation.OperationId, now, "recovery_session_replaced");
            var sessionId = ManagementIdentifiers.NewId();
            var expiresAt = now.Add(RecoverySessionLifetime);
            InsertSession(
                connection,
                transaction,
                sessionId,
                generation.OperationId,
                generation.GenerationId,
                sessionCredential,
                now,
                expiresAt);
            using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE administration_instances SET
                        state = 'RECOVERY_IN_PROGRESS',
                        state_version = state_version + 1,
                        updated_unix_ms = $now
                    WHERE instance_key = 1 AND state = 'RECOVERY_AVAILABLE';
                    """;
                update.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
                if (update.ExecuteNonQuery() != 1)
                {
                    throw new AdministrationBootstrapException(
                        "recovery_state_conflict",
                        "The administration recovery state changed.");
                }
            }

            var context = new ManagementRequestContext(
                ManagementPrincipal.Superuser,
                correlationId,
                RequestId: null,
                source);
            AuditEventStore.Append(
                connection,
                transaction,
                AuditEventDraft.Create(
                    context,
                    now,
                    "administration.recovery-claim-succeeded",
                    "administration-instance",
                    instance.InstanceId,
                    details: new Dictionary<string, string>
                    {
                        ["generation_id"] = generation.GenerationId,
                        ["session_id"] = sessionId,
                    }));
            transaction.Commit();
            return new RecoveryClaimResult(
                generation.OperationId,
                sessionToken,
                expiresAt);
        });
    }

    public async Task<RecoverySessionAuthentication?> AuthenticateRecoverySessionAsync(
        string token,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateToken(token);
        var now = timeProvider.GetUtcNow();
        var sessions = await database.ReadAsync<IReadOnlyList<StoredSession>>(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT s.session_id, s.operation_id, s.generation_id,
                       s.token_salt, s.token_verifier,
                       s.issued_unix_ms, s.expires_unix_ms, s.revoked_unix_ms
                FROM administration_setup_sessions s
                JOIN administration_token_generations g
                    ON g.generation_id = s.generation_id
                JOIN administration_instances i ON i.instance_key = 1
                WHERE g.purpose = 'RECOVERY'
                    AND i.state = 'RECOVERY_IN_PROGRESS'
                    AND i.setup_operation_id = s.operation_id
                    AND s.revoked_unix_ms IS NULL AND s.expires_unix_ms >= $now
                ORDER BY s.issued_unix_ms DESC, s.session_id COLLATE BINARY;
                """;
            command.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
            using var reader = command.ExecuteReader();
            var result = new List<StoredSession>();
            while (reader.Read())
            {
                result.Add(ReadSession(reader));
            }

            return result;
        });
        var session = sessions.FirstOrDefault(candidate => Verify(token, candidate.Credential));
        return session is null
            ? null
            : new RecoverySessionAuthentication(session.SessionId, session.OperationId);
    }

    public Task RevokeRecoveryAsync(string reason)
    {
        reason = NormalizeLocalReason(reason);
        var now = timeProvider.GetUtcNow();
        return database.WriteAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            var instance = ReadInstance(connection, transaction)
                ?? throw new InvalidOperationException("administration instance is not initialized");
            if (instance.State is not (
                    AdministrationState.RecoveryAvailable or
                    AdministrationState.RecoveryInProgress) ||
                instance.SetupOperationId is null)
            {
                throw new AdministrationBootstrapException(
                    "recovery_unavailable",
                    "No recovery access is currently enabled.");
            }

            RevokeCurrentGeneration(connection, transaction, "RECOVERY", now, "recovery_revoked");
            RevokeSetupSessions(
                connection, transaction, instance.SetupOperationId, now, "recovery_revoked");
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE administration_instances SET
                    state = 'ACTIVE',
                    state_version = state_version + 1,
                    updated_unix_ms = $now
                WHERE instance_key = 1 AND state IN (
                    'RECOVERY_AVAILABLE', 'RECOVERY_IN_PROGRESS');
                """;
            update.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
            if (update.ExecuteNonQuery() != 1)
            {
                throw new AdministrationBootstrapException(
                    "recovery_state_conflict",
                    "The administration recovery state changed.");
            }

            AuditEventStore.Append(
                connection,
                transaction,
                AuditEventDraft.Create(
                    LocalOperatorContext("administration-recovery-revoke"),
                    now,
                    "administration.recovery-revoked",
                    "administration-instance",
                    instance.InstanceId,
                    details: new Dictionary<string, string> { ["reason"] = reason }));
            transaction.Commit();
            return true;
        });
    }

    public async Task<LocalSetupToken> AbandonSetupAsync(
        string operationId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        RequireBounded(operationId, 64, "operation ID");
        reason = NormalizeLocalReason(reason);
        cancellationToken.ThrowIfCancellationRequested();
        var token = NewToken();
        var credential = Derive(token);
        var now = timeProvider.GetUtcNow();
        return await database.WriteAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            var operation = ReadOperation(connection, transaction, operationId)
                ?? throw new AdministrationBootstrapException(
                    "setup_operation_not_found",
                    "The setup operation was not found.");
            if (operation.State != SetupOperationState.InProgress || operation.CandidateCommit is not null)
            {
                throw new AdministrationBootstrapException(
                    "setup_abandon_unavailable",
                    "Setup can be abandoned only before a candidate commit exists.");
            }

            var instance = ReadInstance(connection, transaction)
                ?? throw new InvalidOperationException("administration instance is not initialized");
            if (instance.State != AdministrationState.SetupInProgress ||
                !string.Equals(instance.SetupOperationId, operationId, StringComparison.Ordinal))
            {
                throw new AdministrationBootstrapException(
                    "setup_abandon_unavailable",
                    "The setup operation is not the controller's active first-run operation.");
            }

            RevokeCurrentGeneration(connection, transaction, "SETUP_RESUME", now, "setup_abandoned");
            RevokeSetupSessions(connection, transaction, operationId, now, "setup_abandoned");
            using (var abandon = connection.CreateCommand())
            {
                abandon.Transaction = transaction;
                abandon.CommandText = """
                    UPDATE administration_setup_operations SET
                        state = 'ABANDONED',
                        state_version = state_version + 1,
                        pending_user_id = NULL,
                        pending_login = NULL,
                        pending_display_name = NULL,
                        password_algorithm = NULL,
                        password_iterations = NULL,
                        password_salt = NULL,
                        password_verifier = NULL,
                        last_failure_code = 'abandoned',
                        updated_unix_ms = $now
                    WHERE operation_id = $operationId AND state = 'IN_PROGRESS'
                        AND state_version = $expectedVersion AND candidate_commit IS NULL;
                    """;
                abandon.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
                abandon.Parameters.AddWithValue("$operationId", operationId);
                abandon.Parameters.AddWithValue("$expectedVersion", operation.StateVersion);
                if (abandon.ExecuteNonQuery() != 1)
                {
                    throw new AdministrationBootstrapException(
                        "setup_state_conflict",
                        "The setup operation changed; refresh it before retrying.");
                }
            }

            using (var reset = connection.CreateCommand())
            {
                reset.Transaction = transaction;
                reset.CommandText = """
                    UPDATE administration_instances SET
                        state = 'UNCLAIMED',
                        state_version = state_version + 1,
                        setup_operation_id = NULL,
                        updated_unix_ms = $now
                    WHERE instance_key = 1 AND state = 'SETUP_IN_PROGRESS'
                        AND setup_operation_id = $operationId;
                    """;
                reset.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
                reset.Parameters.AddWithValue("$operationId", operationId);
                if (reset.ExecuteNonQuery() != 1)
                {
                    throw new AdministrationBootstrapException(
                        "setup_state_conflict",
                        "The administration state changed; refresh it before retrying.");
                }
            }

            RevokeCurrentGeneration(connection, transaction, "BOOTSTRAP", now, "setup_abandoned");
            var generationId = ManagementIdentifiers.NewId();
            var expiresAt = now.Add(BootstrapLifetime);
            InsertGeneration(
                connection,
                transaction,
                generationId,
                "BOOTSTRAP",
                operationId: null,
                credential,
                now,
                expiresAt);
            AuditEventStore.Append(
                connection,
                transaction,
                AuditEventDraft.Create(
                    LocalOperatorContext("administration-setup-abandon"),
                    now,
                    "administration.setup-abandoned",
                    "setup-operation",
                    operationId,
                    details: new Dictionary<string, string>
                    {
                        ["reason"] = reason,
                        ["replacement_generation_id"] = generationId,
                    }));
            transaction.Commit();
            return new LocalSetupToken(generationId, token, expiresAt, null);
        });
    }

    private async Task<LocalSetupToken> IssueBootstrapTokenAsync(string reason)
    {
        var token = NewToken();
        var credential = Derive(token);
        var now = timeProvider.GetUtcNow();
        return await database.WriteAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            var instance = ReadInstance(connection, transaction)
                ?? throw new InvalidOperationException("administration instance is not initialized");
            if (instance.State != AdministrationState.Unclaimed)
            {
                throw new AdministrationBootstrapException(
                    "setup_rotation_unavailable",
                    "Bootstrap token rotation is available only before a setup claim.");
            }

            RevokeCurrentGeneration(connection, transaction, "BOOTSTRAP", now, reason);
            var generationId = ManagementIdentifiers.NewId();
            var expiresAt = now.Add(BootstrapLifetime);
            InsertGeneration(connection, transaction, generationId, "BOOTSTRAP", null, credential, now, expiresAt);
            AuditEventStore.Append(
                connection,
                transaction,
                AuditEventDraft.Create(
                    LocalOperatorContext("administration-bootstrap-rotate"),
                    now,
                    "administration.bootstrap-rotated",
                    "administration-instance",
                    instance.InstanceId,
                    details: new Dictionary<string, string>
                    {
                        ["generation_id"] = generationId,
                    }));
            transaction.Commit();
            return new LocalSetupToken(generationId, token, expiresAt, null);
        });
    }

    private async Task<LocalSetupToken> IssueOperationTokenAsync(
        string operationId,
        string purpose,
        TimeSpan lifetime,
        string auditAction)
    {
        var token = NewToken();
        var credential = Derive(token);
        var now = timeProvider.GetUtcNow();
        return await database.WriteAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            var operation = ReadOperation(connection, transaction, operationId)
                ?? throw new AdministrationBootstrapException("setup_operation_not_found", "The setup operation was not found.");
            if (operation.State is SetupOperationState.Completed or SetupOperationState.Abandoned)
            {
                throw new AdministrationBootstrapException(
                    "setup_operation_terminal",
                    "The setup operation is no longer resumable.");
            }

            RevokeCurrentGeneration(connection, transaction, purpose, now, "access_reissued");
            RevokeSetupSessions(connection, transaction, operationId, now, "access_reissued");
            var generationId = ManagementIdentifiers.NewId();
            var expiresAt = now.Add(lifetime);
            InsertGeneration(connection, transaction, generationId, purpose, operationId, credential, now, expiresAt);
            AuditEventStore.Append(
                connection,
                transaction,
                AuditEventDraft.Create(
                    LocalOperatorContext("administration-setup-access"),
                    now,
                    auditAction,
                    "setup-operation",
                    operationId,
                    details: new Dictionary<string, string>
                    {
                        ["generation_id"] = generationId,
                    }));
            transaction.Commit();
            return new LocalSetupToken(generationId, token, expiresAt, operationId);
        });
    }

    private Task<IReadOnlyList<StoredGeneration>> ListCurrentClaimGenerationsAsync(DateTimeOffset now) =>
        database.ReadAsync<IReadOnlyList<StoredGeneration>>(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT generation_id, purpose, operation_id, token_salt, token_verifier,
                       issued_unix_ms, expires_unix_ms, consumed_unix_ms, revoked_unix_ms
                FROM administration_token_generations
                WHERE purpose IN ('BOOTSTRAP', 'SETUP_RESUME')
                  AND consumed_unix_ms IS NULL AND revoked_unix_ms IS NULL
                  AND expires_unix_ms >= $now
                ORDER BY issued_unix_ms DESC, generation_id COLLATE BINARY;
                """;
            command.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
            using var reader = command.ExecuteReader();
            var result = new List<StoredGeneration>();
            while (reader.Read())
            {
                result.Add(ReadGeneration(reader));
            }

            return result;
        });

    private Task<IReadOnlyList<StoredGeneration>> ListCurrentRecoveryGenerationsAsync(DateTimeOffset now) =>
        database.ReadAsync<IReadOnlyList<StoredGeneration>>(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT generation_id, purpose, operation_id, token_salt, token_verifier,
                       issued_unix_ms, expires_unix_ms, consumed_unix_ms, revoked_unix_ms
                FROM administration_token_generations
                WHERE purpose = 'RECOVERY'
                  AND consumed_unix_ms IS NULL AND revoked_unix_ms IS NULL
                  AND expires_unix_ms >= $now
                ORDER BY issued_unix_ms DESC, generation_id COLLATE BINARY;
                """;
            command.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
            using var reader = command.ExecuteReader();
            var result = new List<StoredGeneration>();
            while (reader.Read())
            {
                result.Add(ReadGeneration(reader));
            }

            return result;
        });

    private Task<SetupOperationSnapshot> MarkActivatingAsync(
        SetupSessionAuthentication session,
        SetupOperationSnapshot operation,
        ConfigurationRevision candidate,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = timeProvider.GetUtcNow();
        return database.WriteAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            EnsureSessionCurrent(connection, transaction, session, now);
            using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE administration_setup_operations SET
                        state = 'ACTIVATING',
                        state_version = state_version + 1,
                        candidate_commit = $commit,
                        updated_unix_ms = $now
                    WHERE operation_id = $operationId AND state = 'IN_PROGRESS'
                        AND state_version = $expectedVersion
                        AND repository_id = $repositoryId
                        AND expected_base_commit = $expectedBase;

                    UPDATE administration_instances SET
                        state = 'SETUP_ACTIVATING',
                        state_version = state_version + 1,
                        updated_unix_ms = $now
                    WHERE instance_key = 1 AND state = 'SETUP_IN_PROGRESS'
                        AND setup_operation_id = $operationId;
                    """;
                update.Parameters.AddWithValue("$commit", candidate.Commit);
                update.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
                update.Parameters.AddWithValue("$operationId", operation.OperationId);
                update.Parameters.AddWithValue("$expectedVersion", operation.StateVersion);
                update.Parameters.AddWithValue("$repositoryId", candidate.RepositoryId);
                update.Parameters.AddWithValue("$expectedBase", operation.ExpectedBaseCommit!);
                if (update.ExecuteNonQuery() != 2)
                {
                    throw new AdministrationBootstrapException(
                        "setup_state_conflict",
                        "The setup operation changed while its Git commit was being recorded.");
                }
            }

            var updated = ReadOperation(connection, transaction, operation.OperationId)
                ?? throw new InvalidOperationException("setup operation was not persisted");
            AuditEventStore.Append(
                connection,
                transaction,
                AuditEventDraft.Create(
                    SetupContext(
                        operation.OperationId,
                        operation.OperationId,
                        operation.OperationId,
                        "setup-activation"),
                    now,
                    "administration.git-committed",
                    "setup-operation",
                    operation.OperationId,
                    details: new Dictionary<string, string>
                    {
                        ["commit"] = candidate.Commit,
                    }) with { ResultRevision = candidate.Canonical });
            transaction.Commit();
            return updated;
        });
    }

    private static bool IsRecoverableSetupCandidate(
        ConfigurationRevisionValidation validation,
        string operationId,
        string userId,
        string bindingId)
    {
        if (!validation.IsValid ||
            !string.Equals(
                validation.Validated!.Descriptor.ControllerProvenance?.OperationId,
                operationId,
                StringComparison.Ordinal))
        {
            return false;
        }

        var documents = validation.Validated.Documents;
        return documents.Any(document => document.Kind == "User" && document.Id == userId) &&
            documents.Any(document =>
                document.Kind == "RoleBinding" && document.Id == bindingId &&
                document.ScalarFields["spec.principalId"] == userId &&
                document.ScalarFields["spec.roleId"] == AuthorizationRoleIds.SystemAdministrator &&
                document.ScalarFields["spec.scopeType"] == "global" &&
                document.ScalarFields["spec.scopeId"] == "global");
    }

    private static void EnsureActivatedAuthorizationProjection(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string userId,
        string bindingId,
        ConfigurationRevision revision)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*)
            FROM authorization_desired_users u
            JOIN authorization_role_bindings b
                ON b.principal_type = 'user' AND b.principal_id = u.user_id
            WHERE u.user_id = $userId AND u.desired_active = 1
                AND u.source_repository_id = $repositoryId
                AND u.source_commit = $commit
                AND b.binding_id = $bindingId
                AND b.role_id = 'SYSTEM_ADMIN'
                AND b.scope_kind = 'global' AND b.scope_id = 'global'
                AND b.source_repository_id = $repositoryId
                AND b.source_commit = $commit;
            """;
        command.Parameters.AddWithValue("$userId", userId);
        command.Parameters.AddWithValue("$repositoryId", revision.RepositoryId);
        command.Parameters.AddWithValue("$commit", revision.Commit);
        command.Parameters.AddWithValue("$bindingId", bindingId);
        if (Convert.ToInt32(command.ExecuteScalar()) != 1)
        {
            throw new AdministrationBootstrapException(
                "setup_activation_blocked",
                "The active configuration does not contain the exact first administrator binding.");
        }
    }

    private static void DemandEditable(SetupOperationSnapshot operation, long expectedStateVersion)
    {
        if (operation.State != SetupOperationState.InProgress)
        {
            throw new AdministrationBootstrapException(
                "setup_operation_not_editable",
                "The setup operation is not editable in its current phase.");
        }

        if (operation.StateVersion != expectedStateVersion)
        {
            throw new AdministrationBootstrapException(
                "setup_state_conflict",
                "The setup operation changed; refresh it before retrying.");
        }
    }

    private static void EnsureSessionCurrent(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SetupSessionAuthentication session,
        DateTimeOffset now)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT 1 FROM administration_setup_sessions
            WHERE session_id = $sessionId AND operation_id = $operationId
              AND revoked_unix_ms IS NULL AND expires_unix_ms >= $now;
            """;
        command.Parameters.AddWithValue("$sessionId", session.SessionId);
        command.Parameters.AddWithValue("$operationId", session.OperationId);
        command.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        if (command.ExecuteScalar() is null)
        {
            throw new AdministrationBootstrapException(
                "setup_session_invalid",
                "The setup session is invalid or expired.");
        }
    }

    private static T? ReadStoredResponse<T>(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string operationId,
        string requestKind,
        string requestId,
        string requestHash)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT request_hash, response_json
            FROM administration_setup_requests
            WHERE operation_id = $operationId AND request_kind = $requestKind
              AND request_id = $requestId;
            """;
        command.Parameters.AddWithValue("$operationId", operationId);
        command.Parameters.AddWithValue("$requestKind", requestKind);
        command.Parameters.AddWithValue("$requestId", requestId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return default;
        }

        if (!string.Equals(reader.GetString(0), requestHash, StringComparison.Ordinal))
        {
            throw new AdministrationBootstrapException(
                "idempotency_key_reused",
                "The Idempotency-Key was already used for different setup content.");
        }

        return JsonSerializer.Deserialize<T>(reader.GetString(1), JsonOptions)
            ?? throw new InvalidDataException("stored setup response is invalid");
    }

    private static bool ReservedPasswordMatches(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string operationId,
        string password)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT password_salt, password_verifier
            FROM administration_setup_operations
            WHERE operation_id = $operationId;
            """;
        command.Parameters.AddWithValue("$operationId", operationId);
        using var reader = command.ExecuteReader();
        return reader.Read() && !reader.IsDBNull(0) && !reader.IsDBNull(1) &&
            Verify(
                password,
                new StoredCredential((byte[])reader[0], (byte[])reader[1]));
    }

    private static void StoreResponse<T>(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string operationId,
        string requestKind,
        string requestId,
        string requestHash,
        int status,
        T response,
        DateTimeOffset now)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO administration_setup_requests(
                operation_id, request_kind, request_id, request_hash,
                response_status, response_json, created_unix_ms)
            VALUES (
                $operationId, $requestKind, $requestId, $requestHash,
                $status, $responseJson, $now);
            """;
        command.Parameters.AddWithValue("$operationId", operationId);
        command.Parameters.AddWithValue("$requestKind", requestKind);
        command.Parameters.AddWithValue("$requestId", requestId);
        command.Parameters.AddWithValue("$requestHash", requestHash);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$responseJson", JsonSerializer.Serialize(response, JsonOptions));
        command.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        command.ExecuteNonQuery();
    }

    private static StoredInstance? ReadInstance(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT instance_id, state, state_version, setup_operation_id, updated_unix_ms
            FROM administration_instances WHERE instance_key = 1;
            """;
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new StoredInstance(
                reader.GetString(0),
                ParseAdministrationState(reader.GetString(1)),
                reader.GetInt64(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)))
            : null;
    }

    private static SetupOperationSnapshot? ReadOperation(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string operationId)
    {
        RequireBounded(operationId, 64, "operation ID");
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT operation_id, state, state_version,
                   pending_user_id, pending_login, pending_display_name,
                   repository_mode, repository_id, expected_base_commit, candidate_commit,
                   last_failure_code, created_unix_ms, updated_unix_ms
            FROM administration_setup_operations WHERE operation_id = $operationId;
            """;
        command.Parameters.AddWithValue("$operationId", operationId);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new SetupOperationSnapshot(
                reader.GetString(0),
                ParseSetupState(reader.GetString(1)),
                reader.GetInt64(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.GetString(10),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(11)),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(12)))
            : null;
    }

    private static StoredGeneration? ReadGeneration(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string generationId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT generation_id, purpose, operation_id, token_salt, token_verifier,
                   issued_unix_ms, expires_unix_ms, consumed_unix_ms, revoked_unix_ms
            FROM administration_token_generations WHERE generation_id = $generationId;
            """;
        command.Parameters.AddWithValue("$generationId", generationId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadGeneration(reader) : null;
    }

    private static StoredGeneration ReadGeneration(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.IsDBNull(2) ? null : reader.GetString(2),
        new StoredCredential((byte[])reader[3], (byte[])reader[4]),
        DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5)),
        DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(6)),
        reader.IsDBNull(7) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(7)),
        reader.IsDBNull(8) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(8)));

    private static StoredSession ReadSession(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        new StoredCredential((byte[])reader[3], (byte[])reader[4]),
        DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5)),
        DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(6)),
        reader.IsDBNull(7) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(7)));

    private static void InsertGeneration(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string generationId,
        string purpose,
        string? operationId,
        StoredCredential credential,
        DateTimeOffset issuedAt,
        DateTimeOffset expiresAt)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO administration_token_generations(
                generation_id, purpose, operation_id, token_salt, token_verifier,
                issued_unix_ms, expires_unix_ms)
            VALUES (
                $generationId, $purpose, $operationId, $salt, $verifier,
                $issuedAt, $expiresAt);
            """;
        command.Parameters.AddWithValue("$generationId", generationId);
        command.Parameters.AddWithValue("$purpose", purpose);
        command.Parameters.AddWithValue("$operationId", (object?)operationId ?? DBNull.Value);
        command.Parameters.Add("$salt", SqliteType.Blob).Value = credential.Salt;
        command.Parameters.Add("$verifier", SqliteType.Blob).Value = credential.Verifier;
        command.Parameters.AddWithValue("$issuedAt", issuedAt.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$expiresAt", expiresAt.ToUnixTimeMilliseconds());
        command.ExecuteNonQuery();
    }

    private static void InsertSession(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId,
        string operationId,
        string generationId,
        StoredCredential credential,
        DateTimeOffset issuedAt,
        DateTimeOffset expiresAt)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO administration_setup_sessions(
                session_id, operation_id, generation_id, token_salt, token_verifier,
                issued_unix_ms, expires_unix_ms)
            VALUES (
                $sessionId, $operationId, $generationId, $salt, $verifier,
                $issuedAt, $expiresAt);
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId);
        command.Parameters.AddWithValue("$operationId", operationId);
        command.Parameters.AddWithValue("$generationId", generationId);
        command.Parameters.Add("$salt", SqliteType.Blob).Value = credential.Salt;
        command.Parameters.Add("$verifier", SqliteType.Blob).Value = credential.Verifier;
        command.Parameters.AddWithValue("$issuedAt", issuedAt.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$expiresAt", expiresAt.ToUnixTimeMilliseconds());
        command.ExecuteNonQuery();
    }

    private static void ConsumeGeneration(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string generationId,
        DateTimeOffset now)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE administration_token_generations SET consumed_unix_ms = $now
            WHERE generation_id = $generationId
              AND consumed_unix_ms IS NULL AND revoked_unix_ms IS NULL;
            """;
        command.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$generationId", generationId);
        if (command.ExecuteNonQuery() != 1)
        {
            throw new AdministrationBootstrapException(
                "setup_claim_invalid",
                "The setup claim is invalid or expired.");
        }
    }

    private static void RevokeCurrentGeneration(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string purpose,
        DateTimeOffset now,
        string reason)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE administration_token_generations SET
                revoked_unix_ms = $now, revoke_reason = $reason
            WHERE purpose = $purpose AND consumed_unix_ms IS NULL
              AND revoked_unix_ms IS NULL;
            """;
        command.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$purpose", purpose);
        command.ExecuteNonQuery();
    }

    private static void RevokeSetupSessions(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string operationId,
        DateTimeOffset now,
        string reason)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE administration_setup_sessions SET
                revoked_unix_ms = $now, revoke_reason = $reason
            WHERE operation_id = $operationId AND revoked_unix_ms IS NULL;
            """;
        command.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$operationId", operationId);
        command.ExecuteNonQuery();
    }

    private static StoredCredential Derive(string token)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var verifier = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(token),
            salt,
            VerifierIterations,
            HashAlgorithmName.SHA256,
            32);
        return new StoredCredential(salt, verifier);
    }

    private static bool Verify(string token, StoredCredential credential)
    {
        var actual = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(token),
            credential.Salt,
            VerifierIterations,
            HashAlgorithmName.SHA256,
            credential.Verifier.Length);
        return CryptographicOperations.FixedTimeEquals(actual, credential.Verifier);
    }

    private static string NewToken() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    private static string HashRequest(params string[] parts) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join('\n', parts) + "\n")));

    private static string NormalizeLogin(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        value = value.Trim();
        if (value.Length > 128 || !char.IsAsciiLetterOrDigit(value[0]) ||
            value.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-')))
        {
            throw new AdministrationBootstrapException(
                "setup_login_invalid",
                "Login must use 1-128 ASCII letters, digits, '.', '_' or '-'.");
        }

        return value;
    }

    private static string NormalizeDisplayName(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        value = value.Trim();
        if (value.Length > 256 || value.Any(char.IsControl))
        {
            throw new AdministrationBootstrapException(
                "setup_display_name_invalid",
                "Display name must contain 1-256 printable characters.");
        }

        return value;
    }

    private static void ValidatePassword(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length is < 12 or > 256 || value.Any(character => character is '\0' or '\r' or '\n'))
        {
            throw new AdministrationBootstrapException(
                "setup_password_invalid",
                "Password must contain 12-256 characters without line breaks.");
        }
    }

    private static void ValidateToken(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        if (token.Length != 64 || token.Any(character =>
                !(char.IsAsciiDigit(character) || character is >= 'a' and <= 'f' or >= 'A' and <= 'F')))
        {
            throw new AdministrationBootstrapException(
                "setup_claim_invalid",
                "The setup claim is invalid or expired.");
        }
    }

    private static void ValidateRequestId(string requestId)
    {
        if (string.IsNullOrWhiteSpace(requestId) || requestId.Length > 256 ||
            requestId.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or ':' or '@' or '/' or '-')))
        {
            throw new AdministrationBootstrapException(
                "idempotency_key_invalid",
                "Idempotency-Key must contain 1-256 safe ASCII characters.");
        }
    }

    private static void RequireBounded(string value, int maximum, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum)
        {
            throw new AdministrationBootstrapException(
                "setup_request_invalid",
                $"{field} is invalid.");
        }
    }

    private static string NormalizeLocalReason(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new AdministrationBootstrapException(
                "setup_abandon_reason_required",
                "A local abandonment reason is required.");
        }

        value = value.Trim();
        if (value.Length > 256 || value.Any(char.IsControl))
        {
            throw new AdministrationBootstrapException(
                "setup_abandon_reason_invalid",
                "The abandonment reason must contain 1-256 printable characters.");
        }

        return value;
    }

    private static string? SafeCorrelation(string? value)
    {
        try
        {
            return ManagementIdentifiers.NormalizeCorrelationId(value);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static ManagementRequestContext LocalOperatorContext(string source) => new(
        new ManagementPrincipal("local-operator", "controller-host", "local-host", LegacyScope: null),
        ManagementIdentifiers.NewId(),
        RequestId: null,
        source);

    private static ManagementRequestContext SetupContext(
        string operationId,
        string correlationId,
        string requestId,
        string source) => new(
            new ManagementPrincipal("setup", operationId, "setup-session", LegacyScope: null),
            ManagementIdentifiers.NormalizeCorrelationId(correlationId),
            requestId,
            source);

    private static AdministrationState ParseAdministrationState(string value) => value switch
    {
        "UNCLAIMED" => AdministrationState.Unclaimed,
        "SETUP_IN_PROGRESS" => AdministrationState.SetupInProgress,
        "SETUP_WAITING_FOR_GIT" => AdministrationState.SetupWaitingForGit,
        "SETUP_ACTIVATING" => AdministrationState.SetupActivating,
        "ACTIVE" => AdministrationState.Active,
        "RECOVERY_AVAILABLE" => AdministrationState.RecoveryAvailable,
        "RECOVERY_IN_PROGRESS" => AdministrationState.RecoveryInProgress,
        _ => throw new InvalidDataException("administration instance has an unknown state"),
    };

    private static SetupOperationState ParseSetupState(string value) => value switch
    {
        "IN_PROGRESS" => SetupOperationState.InProgress,
        "WAITING_FOR_GIT" => SetupOperationState.WaitingForGit,
        "ACTIVATING" => SetupOperationState.Activating,
        "COMPLETED" => SetupOperationState.Completed,
        "ABANDONED" => SetupOperationState.Abandoned,
        "BLOCKED" => SetupOperationState.Blocked,
        _ => throw new InvalidDataException("setup operation has an unknown state"),
    };

    private sealed record StoredInstance(
        string InstanceId,
        AdministrationState State,
        long StateVersion,
        string? SetupOperationId,
        DateTimeOffset UpdatedAt);

    private sealed record StoredCredential(byte[] Salt, byte[] Verifier);

    private sealed record StoredGeneration(
        string GenerationId,
        string Purpose,
        string? OperationId,
        StoredCredential Credential,
        DateTimeOffset IssuedAt,
        DateTimeOffset ExpiresAt,
        DateTimeOffset? ConsumedAt,
        DateTimeOffset? RevokedAt);

    private sealed record StoredSession(
        string SessionId,
        string OperationId,
        string GenerationId,
        StoredCredential Credential,
        DateTimeOffset IssuedAt,
        DateTimeOffset ExpiresAt,
        DateTimeOffset? RevokedAt);
}
