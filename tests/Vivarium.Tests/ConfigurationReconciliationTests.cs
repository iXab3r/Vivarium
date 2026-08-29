using System.Text;
using Microsoft.Data.Sqlite;
using Vivarium.Controller.Auditing;
using Vivarium.Controller.Configuration.Git;
using Vivarium.Controller.Configuration.Reconciliation;
using Vivarium.Controller.Persistence;
using Vivarium.Controller.Security;

namespace Vivarium.Tests;

[TestFixture]
public sealed class ConfigurationReconciliationTests
{
    private static readonly DateTimeOffset TestNow =
        DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
    private const string RepositoryId = "controller";
    private const string Scope = "control";
    private const string AgentId = "agent-00000001";
    private string rootDir = null!;

    [SetUp]
    public void SetUp()
    {
        rootDir = Path.Combine(
            Path.GetTempPath(),
            "vivarium-configuration-reconciliation-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootDir);
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            Directory.Delete(rootDir, recursive: true);
        }
        catch
        {
            // Best effort: preserve the original failure when a platform delays handle release.
        }
    }

    [Test]
    public async Task Valid_revision_applies_once_and_last_known_good_survives_restart()
    {
        var dataDir = Path.Combine(rootDir, "controller");
        Directory.CreateDirectory(dataDir);
        var validation = ValidAgentRevision(
            Commit('b'),
            Tree('c'),
            Aggregate('d'),
            AgentDocument(AgentId, enabled: false, Content('e')),
            parents: [Revision(Commit('a'))]);
        string revisionSetId;

        await using (var database = new VivariumDatabase(dataDir))
        {
            await InsertAgentAsync(database, AgentId, enabled: true);
            var reconciler = CreateReconciler(database);
            var applied = await reconciler.ReconcileAsync(
                RequestContext("apply-1"),
                Scope,
                validation,
                operationId: "external-apply-1");
            revisionSetId = applied.Attempt.RevisionSetId;
            var retried = await reconciler.ReconcileAsync(
                RequestContext("apply-1-retry"),
                Scope,
                validation,
                operationId: "external-apply-1-retry");
            var persisted = await ReadAgentProjectionAsync(database, AgentId);
            var audits = await new AuditEventStore(database).ListAsync();
            var rowCounts = await database.ReadAsync(connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT
                        (SELECT COUNT(*) FROM configuration_revision_sets),
                        (SELECT COUNT(*) FROM configuration_revision_members),
                        (SELECT COUNT(*) FROM configuration_materialization_scopes);
                    """;
                using var reader = command.ExecuteReader();
                Assert.That(reader.Read(), Is.True);
                return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
            });

            Assert.Multiple(() =>
            {
                Assert.That(applied.Outcome, Is.EqualTo(ConfigurationReconciliationOutcome.Applied));
                Assert.That(retried.Outcome, Is.EqualTo(ConfigurationReconciliationOutcome.NoChange));
                Assert.That(applied.State.Active!.RevisionSetId, Is.EqualTo(revisionSetId));
                Assert.That(applied.State.LastKnownGood!.RevisionSetId, Is.EqualTo(revisionSetId));
                Assert.That(persisted.RuntimeEnabled, Is.False);
                Assert.That(persisted.DesiredEnabled, Is.False);
                Assert.That(persisted.SourceCommit, Is.EqualTo(Commit('b')));
                Assert.That(persisted.SourceRevisionSetId, Is.EqualTo(revisionSetId));
                Assert.That(rowCounts, Is.EqualTo((1L, 1L, 1L)));
                Assert.That(audits.Count(audit => audit.Action == "configuration.revision.applied"), Is.EqualTo(1));
                Assert.That(audits.Single().BaseRevision, Is.Null);
                Assert.That(audits.Single().ResultRevision, Is.EqualTo(Revision(Commit('b')).Canonical));
            });
        }

        await using var restarted = new VivariumDatabase(dataDir);
        var restored = await CreateReconciler(restarted).GetStateAsync(Scope);
        var restoredProjection = await ReadAgentProjectionAsync(restarted, AgentId);
        Assert.Multiple(() =>
        {
            Assert.That(restored, Is.Not.Null);
            Assert.That(restored!.Active!.RevisionSetId, Is.EqualTo(revisionSetId));
            Assert.That(restored.LastKnownGood!.RevisionSetId, Is.EqualTo(revisionSetId));
            Assert.That(restored.LatestAttempt.RevisionSetId, Is.EqualTo(revisionSetId));
            Assert.That(restoredProjection.RuntimeEnabled, Is.False);
            Assert.That(restoredProjection.SourceRevisionSetId, Is.EqualTo(revisionSetId));
        });
    }

    [Test]
    public async Task Invalid_head_never_activates_and_keeps_bounded_secret_free_diagnostics()
    {
        const string sentinelSecret = "plain-secret-never-persist";
        await using var database = CreateDatabase();
        await InsertAgentAsync(database, AgentId, enabled: true);
        var reconciler = CreateReconciler(database);
        var valid = ValidAgentRevision(
            Commit('b'),
            Tree('c'),
            Aggregate('d'),
            AgentDocument(AgentId, enabled: false, Content('e')),
            parents: [Revision(Commit('a'))]);
        var active = await reconciler.ReconcileAsync(
            RequestContext("valid"),
            Scope,
            valid,
            operationId: "valid-operation");
        var invalidRevision = Revision(Commit('f'));
        var invalid = new ConfigurationRevisionValidation(
            invalidRevision,
            Tree('1'),
            Validated: null,
            Diagnostics:
            [
                new ConfigurationValidationDiagnostic(
                    "secret_value_forbidden",
                    ".vivarium/agents/agent-00000001.yaml",
                    "spec.enabled",
                    "a resolved secret value is forbidden"),
            ]);

        var rejected = await reconciler.ReconcileAsync(
            RequestContext("invalid"),
            Scope,
            invalid,
            operationId: "invalid-operation");
        var projection = await ReadAgentProjectionAsync(database, AgentId);
        var audits = await new AuditEventStore(database).ListAsync();
        var serializedEvidence = string.Join('|',
            rejected.Attempt.Diagnostics.Select(diagnostic =>
                $"{diagnostic.Code}:{diagnostic.Path}:{diagnostic.Field}:{diagnostic.Summary}")) +
            string.Join('|', audits.SelectMany(audit => audit.Details.Select(pair => $"{pair.Key}:{pair.Value}")));

        Assert.Multiple(() =>
        {
            Assert.That(rejected.Outcome, Is.EqualTo(ConfigurationReconciliationOutcome.Invalid));
            Assert.That(rejected.Attempt.State, Is.EqualTo(ConfigurationRevisionSetState.Invalid));
            Assert.That(rejected.Attempt.AppliedAt, Is.Null);
            Assert.That(rejected.Attempt.Members.Single().ContentHash, Is.Null);
            Assert.That(rejected.State.Active!.RevisionSetId, Is.EqualTo(active.Attempt.RevisionSetId));
            Assert.That(rejected.State.LastKnownGood!.RevisionSetId, Is.EqualTo(active.Attempt.RevisionSetId));
            Assert.That(rejected.State.LatestAttempt.RevisionSetId, Is.EqualTo(rejected.Attempt.RevisionSetId));
            Assert.That(projection.RuntimeEnabled, Is.False);
            Assert.That(serializedEvidence, Does.Not.Contain(sentinelSecret));
            Assert.That(audits.Single(audit => audit.Action == "configuration.revision.invalid").BaseRevision,
                Is.EqualTo(Revision(Commit('b')).Canonical));
            Assert.That(audits.Single(audit => audit.Action == "configuration.revision.invalid").ResultRevision,
                Is.EqualTo(invalidRevision.Canonical));
        });
    }

    [Test]
    public async Task Unknown_agent_blocks_the_complete_projection_without_partial_changes()
    {
        await using var database = CreateDatabase();
        await InsertAgentAsync(database, AgentId, enabled: true);
        var reconciler = CreateReconciler(database);
        var first = await reconciler.ReconcileAsync(
            RequestContext("initial"),
            Scope,
            ValidAgentRevision(
                Commit('b'),
                Tree('c'),
                Aggregate('d'),
                AgentDocument(AgentId, enabled: false, Content('e')),
                parents: [Revision(Commit('a'))]),
            operationId: "initial-operation");
        var blockedValidation = ValidAgentRevision(
            Commit('2'),
            Tree('3'),
            Aggregate('4'),
            AgentDocument(AgentId, enabled: true, Content('5')),
            AgentDocument("unknown-agent", enabled: false, Content('6')),
            parents: [Revision(Commit('b'))]);

        var blocked = await reconciler.ReconcileAsync(
            RequestContext("blocked"),
            Scope,
            blockedValidation,
            operationId: "blocked-operation");
        var projection = await ReadAgentProjectionAsync(database, AgentId);
        var audits = await new AuditEventStore(database).ListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(blocked.Outcome, Is.EqualTo(ConfigurationReconciliationOutcome.Blocked));
            Assert.That(blocked.Attempt.State, Is.EqualTo(ConfigurationRevisionSetState.Blocked));
            Assert.That(blocked.Attempt.AppliedAt, Is.Null);
            Assert.That(blocked.Attempt.Members.Single().ContentHash, Is.EqualTo(Aggregate('4')));
            Assert.That(blocked.Attempt.Members.Single().SchemaVersion, Is.EqualTo("vivarium.io/v1alpha1"));
            Assert.That(blocked.Attempt.Diagnostics.Single().Code, Is.EqualTo("agent_registration_not_found"));
            Assert.That(blocked.State.Active!.RevisionSetId, Is.EqualTo(first.Attempt.RevisionSetId));
            Assert.That(blocked.State.LastKnownGood!.RevisionSetId, Is.EqualTo(first.Attempt.RevisionSetId));
            Assert.That(projection.RuntimeEnabled, Is.False,
                "the known Agent update must roll back when a later document blocks the projection");
            Assert.That(projection.SourceCommit, Is.EqualTo(Commit('b')));
            Assert.That(audits.Single(audit => audit.Action == "configuration.revision.blocked").ReasonCode,
                Is.EqualTo("agent_registration_not_found"));
        });
    }

    [Test]
    public async Task Principal_idempotent_mutation_reaches_exact_committed_and_applied_revision()
    {
        await using var database = CreateDatabase();
        await InsertAgentAsync(database, AgentId, enabled: true);
        var reconciler = CreateReconciler(database);
        var context = RequestContext("mutation", requestId: "request-1", actorId: "user-1");
        var intent = new ConfigurationMutationIntent(
            "operation-1",
            "agent.set-enabled",
            Scope,
            Revision(Commit('a')),
            Content('1'));
        var created = await reconciler.Operations.BeginAsync(context, intent);
        var retry = await reconciler.Operations.BeginAsync(
            context with { CorrelationId = "retry-correlation" },
            intent with { OperationId = "ignored-retry-operation" });
        var changed = intent with { OperationId = "changed-operation", RequestHash = Content('2') };
        var idempotencyError = Assert.ThrowsAsync<ConfigurationIdempotencyConflictException>(async () =>
            await reconciler.Operations.BeginAsync(context, changed));
        var resultRevision = Revision(Commit('b'));
        var gitResult = new ConfigurationCommitResult(
            ConfigurationCommitOutcome.Committed,
            intent.ExpectedBase,
            intent.ExpectedBase,
            resultRevision,
            Aggregate('3'),
            Diff: [],
            Diagnostics: []);
        var committed = await reconciler.Operations.RecordGitResultAsync(intent.OperationId, gitResult);
        var validation = ValidAgentRevision(
            resultRevision.Commit,
            Tree('4'),
            Aggregate('3'),
            AgentDocument(AgentId, enabled: false, Content('5')),
            parents: [intent.ExpectedBase],
            provenance: new ConfigurationCommitProvenance(
                intent.OperationId,
                context.RequestId!,
                context.CorrelationId,
                context.Principal.ActorType,
                context.Principal.ActorId));
        var applied = await reconciler.ReconcileAsync(
            ManagementRequestContext.System("restart-reconcile"),
            Scope,
            validation);
        var operation = await reconciler.Operations.GetAsync(intent.OperationId);
        var audits = await new AuditEventStore(database).ListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(created.Outcome, Is.EqualTo(ConfigurationMutationBeginOutcome.Created));
            Assert.That(retry.Outcome, Is.EqualTo(ConfigurationMutationBeginOutcome.Existing));
            Assert.That(retry.Operation.OperationId, Is.EqualTo(intent.OperationId));
            Assert.That(idempotencyError!.OperationId, Is.EqualTo(intent.OperationId));
            Assert.That(committed.State, Is.EqualTo(ConfigurationMutationState.Committed));
            Assert.That(committed.CandidateAggregateContentHash, Is.EqualTo(Aggregate('3')));
            Assert.That(applied.Outcome, Is.EqualTo(ConfigurationReconciliationOutcome.Applied));
            Assert.That(operation!.State, Is.EqualTo(ConfigurationMutationState.Applied));
            Assert.That(operation.ResultRevision, Is.EqualTo(resultRevision));
            Assert.That(operation.RevisionSetId, Is.EqualTo(applied.Attempt.RevisionSetId));
            Assert.That(audits.Select(audit => audit.Action), Does.Contain("configuration.mutation.requested"));
            Assert.That(audits.Select(audit => audit.Action), Does.Contain("configuration.mutation.committed"));
            Assert.That(audits.Select(audit => audit.Action), Does.Contain("configuration.revision.applied"));
            Assert.That(audits.Single(audit => audit.Action == "configuration.revision.applied").ActorId,
                Is.EqualTo("user-1"));
            Assert.That(audits.Single(audit => audit.Action == "configuration.revision.applied").BaseRevision,
                Is.Null);
            Assert.That(audits.Single(audit => audit.Action == "configuration.revision.applied").ResultRevision,
                Is.EqualTo(resultRevision.Canonical));
        });
    }

    [Test]
    public async Task Unchanged_git_result_links_the_existing_active_set_without_new_apply()
    {
        await using var database = CreateDatabase();
        await InsertAgentAsync(database, AgentId, enabled: true);
        var reconciler = CreateReconciler(database);
        var revision = Revision(Commit('b'));
        var validation = ValidAgentRevision(
            revision.Commit,
            Tree('c'),
            Aggregate('d'),
            AgentDocument(AgentId, enabled: false, Content('e')),
            parents: [Revision(Commit('a'))]);
        var active = await reconciler.ReconcileAsync(
            RequestContext("initial-apply"),
            Scope,
            validation,
            operationId: "initial-apply-operation");
        var context = RequestContext("no-change", requestId: "no-change-request");
        var intent = new ConfigurationMutationIntent(
            "no-change-operation",
            "agent.set-enabled",
            Scope,
            revision,
            Content('1'));
        await reconciler.Operations.BeginAsync(context, intent);
        var noChange = await reconciler.Operations.RecordGitResultAsync(
            intent.OperationId,
            new ConfigurationCommitResult(
                ConfigurationCommitOutcome.Unchanged,
                revision,
                revision,
                revision,
                Aggregate('d'),
                Diff: [],
                Diagnostics: []));
        var setCount = await ReadScalarAsync<long>(
            database,
            "SELECT COUNT(*) FROM configuration_revision_sets;");
        var audits = await new AuditEventStore(database).ListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(noChange.State, Is.EqualTo(ConfigurationMutationState.Applied));
            Assert.That(noChange.RevisionSetId, Is.EqualTo(active.Attempt.RevisionSetId));
            Assert.That(setCount, Is.EqualTo(1));
            Assert.That(audits.Count(audit => audit.Action == "configuration.revision.applied"), Is.EqualTo(1));
            Assert.That(audits.Count(audit => audit.Action == "configuration.mutation.no_change"), Is.EqualTo(1));
            Assert.That(audits.Single(audit => audit.Action == "configuration.mutation.no_change").Outcome,
                Is.EqualTo(AuditOutcome.NoChange));
            Assert.That(audits.Single(audit => audit.Action == "configuration.mutation.no_change").BaseRevision,
                Is.EqualTo(revision.Canonical));
            Assert.That(audits.Single(audit => audit.Action == "configuration.mutation.no_change").ResultRevision,
                Is.EqualTo(revision.Canonical));
        });
    }

    [Test]
    public async Task Rejected_mutation_has_no_candidate_aggregate_and_no_configuration_set()
    {
        await using var database = CreateDatabase();
        var reconciler = CreateReconciler(database);
        var context = RequestContext("reject", requestId: "reject-request");
        var intent = new ConfigurationMutationIntent(
            "reject-operation",
            "agent.set-enabled",
            Scope,
            Revision(Commit('a')),
            Content('1'));
        await reconciler.Operations.BeginAsync(context, intent);
        var result = new ConfigurationCommitResult(
            ConfigurationCommitOutcome.Rejected,
            intent.ExpectedBase,
            intent.ExpectedBase,
            ResultRevision: null,
            CandidateAggregateContentHash: null,
            Diff: [],
            Diagnostics:
            [
                new ConfigurationValidationDiagnostic(
                    "secret_value_forbidden",
                    ".vivarium/agents/agent-00000001.yaml",
                    "spec.enabled",
                    "resolved values are forbidden"),
            ]);

        var rejected = await reconciler.Operations.RecordGitResultAsync(intent.OperationId, result);
        var setCount = await ReadScalarAsync<long>(database, "SELECT COUNT(*) FROM configuration_revision_sets;");

        Assert.Multiple(() =>
        {
            Assert.That(rejected.State, Is.EqualTo(ConfigurationMutationState.Rejected));
            Assert.That(rejected.CandidateAggregateContentHash, Is.Null);
            Assert.That(rejected.FailureCode, Is.EqualTo("secret_value_forbidden"));
            Assert.That(setCount, Is.Zero);
        });
    }

    [Test]
    public async Task Restart_recovers_committed_head_from_matching_trailers_and_applies_it()
    {
        var dataDir = Path.Combine(rootDir, "restart-controller");
        Directory.CreateDirectory(dataDir);
        var context = RequestContext("before-crash", requestId: "restart-request", actorId: "user-2");
        var intent = new ConfigurationMutationIntent(
            "restart-operation",
            "agent.set-enabled",
            Scope,
            Revision(Commit('a')),
            Content('1'));
        await using (var beforeCrash = new VivariumDatabase(dataDir))
        {
            await InsertAgentAsync(beforeCrash, AgentId, enabled: true);
            await CreateReconciler(beforeCrash).Operations.BeginAsync(context, intent);
        }

        var validation = ValidAgentRevision(
            Commit('b'),
            Tree('c'),
            Aggregate('d'),
            AgentDocument(AgentId, enabled: false, Content('e')),
            parents: [intent.ExpectedBase],
            provenance: new ConfigurationCommitProvenance(
                intent.OperationId,
                context.RequestId!,
                context.CorrelationId,
                context.Principal.ActorType,
                context.Principal.ActorId));
        await using var restarted = new VivariumDatabase(dataDir);
        var reconciler = CreateReconciler(restarted);
        var applied = await reconciler.ReconcileAsync(
            ManagementRequestContext.System("startup-reconciliation"),
            Scope,
            validation);
        var operation = await reconciler.Operations.GetAsync(intent.OperationId);
        var audits = await new AuditEventStore(restarted).ListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(applied.Outcome, Is.EqualTo(ConfigurationReconciliationOutcome.Applied));
            Assert.That(operation!.State, Is.EqualTo(ConfigurationMutationState.Applied));
            Assert.That(operation.ResultRevision, Is.EqualTo(validation.Revision));
            Assert.That(audits.Select(audit => audit.Action),
                Does.Contain("configuration.mutation.commit_recovered"));
            Assert.That(audits.Single(audit => audit.Action == "configuration.mutation.commit_recovered").ActorId,
                Is.EqualTo("user-2"));
            Assert.That(audits.Single(audit => audit.Action == "configuration.mutation.commit_recovered").BaseRevision,
                Is.EqualTo(intent.ExpectedBase.Canonical));
            Assert.That(audits.Single(audit => audit.Action == "configuration.mutation.commit_recovered").ResultRevision,
                Is.EqualTo(validation.Revision.Canonical));
        });
    }

    [Test]
    public async Task Invalid_head_and_its_last_known_good_pointer_survive_restart()
    {
        var dataDir = Path.Combine(rootDir, "invalid-restart-controller");
        Directory.CreateDirectory(dataDir);
        string activeRevisionSetId;
        string invalidRevisionSetId;
        await using (var beforeRestart = new VivariumDatabase(dataDir))
        {
            await InsertAgentAsync(beforeRestart, AgentId, enabled: true);
            var reconciler = CreateReconciler(beforeRestart);
            var active = await reconciler.ReconcileAsync(
                RequestContext("restart-active"),
                Scope,
                ValidAgentRevision(
                    Commit('b'),
                    Tree('c'),
                    Aggregate('d'),
                    AgentDocument(AgentId, enabled: false, Content('e')),
                    parents: [Revision(Commit('a'))]),
                operationId: "restart-active-operation");
            var invalid = await reconciler.ReconcileAsync(
                RequestContext("restart-invalid"),
                Scope,
                new ConfigurationRevisionValidation(
                    Revision(Commit('f')),
                    Tree('1'),
                    Validated: null,
                    Diagnostics:
                    [
                        new ConfigurationValidationDiagnostic(
                            "agent_document_invalid",
                            ".vivarium/agents/agent-00000001.yaml",
                            "spec.enabled",
                            "enabled must be an explicit boolean"),
                    ]),
                operationId: "restart-invalid-operation");
            activeRevisionSetId = active.Attempt.RevisionSetId;
            invalidRevisionSetId = invalid.Attempt.RevisionSetId;
        }

        await using var restarted = new VivariumDatabase(dataDir);
        var restored = await CreateReconciler(restarted).GetStateAsync(Scope);
        var projection = await ReadAgentProjectionAsync(restarted, AgentId);

        Assert.Multiple(() =>
        {
            Assert.That(restored, Is.Not.Null);
            Assert.That(restored!.Active!.RevisionSetId, Is.EqualTo(activeRevisionSetId));
            Assert.That(restored.LastKnownGood!.RevisionSetId, Is.EqualTo(activeRevisionSetId));
            Assert.That(restored.LatestAttempt.RevisionSetId, Is.EqualTo(invalidRevisionSetId));
            Assert.That(restored.LatestAttempt.State, Is.EqualTo(ConfigurationRevisionSetState.Invalid));
            Assert.That(projection.RuntimeEnabled, Is.False);
            Assert.That(projection.SourceRevisionSetId, Is.EqualTo(activeRevisionSetId));
        });
    }

    [Test]
    public async Task Rejected_apply_audit_rolls_back_revision_scope_and_agent_projection()
    {
        await using var database = CreateDatabase();
        await InsertAgentAsync(database, AgentId, enabled: true);
        await database.WriteAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TRIGGER reject_configuration_audit
                BEFORE INSERT ON audit_events
                WHEN NEW.action = 'configuration.revision.applied'
                BEGIN
                    SELECT RAISE(ABORT, 'test configuration audit rejection');
                END;
                """;
            command.ExecuteNonQuery();
            return true;
        });
        var validation = ValidAgentRevision(
            Commit('b'),
            Tree('c'),
            Aggregate('d'),
            AgentDocument(AgentId, enabled: false, Content('e')),
            parents: [Revision(Commit('a'))]);

        var error = Assert.ThrowsAsync<SqliteException>(async () =>
            await CreateReconciler(database).ReconcileAsync(
                RequestContext("audit-rejection"),
                Scope,
                validation,
                operationId: "audit-rejection-operation"));
        var state = await CreateReconciler(database).GetStateAsync(Scope);
        var enabled = await ReadScalarAsync<long>(
            database,
            $"SELECT enabled FROM agents WHERE agent_id = '{AgentId}';");
        var revisionCount = await ReadScalarAsync<long>(
            database,
            "SELECT COUNT(*) FROM configuration_revision_sets;");

        Assert.Multiple(() =>
        {
            Assert.That(error!.Message, Does.Contain("test configuration audit rejection"));
            Assert.That(state, Is.Null);
            Assert.That(enabled, Is.EqualTo(1));
            Assert.That(revisionCount, Is.Zero);
        });
    }

    [Test]
    public async Task Authoritative_head_recheck_converges_to_the_newest_observed_revision()
    {
        await using var database = CreateDatabase();
        await InsertAgentAsync(database, AgentId, enabled: true);
        var first = ValidAgentRevision(
            Commit('b'),
            Tree('c'),
            Aggregate('d'),
            AgentDocument(AgentId, enabled: false, Content('e')),
            parents: [Revision(Commit('a'))]);
        var newest = ValidAgentRevision(
            Commit('f'),
            Tree('1'),
            Aggregate('2'),
            AgentDocument(AgentId, enabled: true, Content('3')),
            parents: [first.Revision]);
        var repository = new MovingHeadRepository(
            [first.Revision, newest.Revision, newest.Revision, newest.Revision],
            [first, newest]);
        var reconciler = CreateReconciler(database);

        var result = await reconciler.ReconcileAuthoritativeHeadAsync(
            RequestContext("moving-head"),
            Scope,
            repository);
        var projection = await ReadAgentProjectionAsync(database, AgentId);

        Assert.Multiple(() =>
        {
            Assert.That(result.HeadConvergence!.State,
                Is.EqualTo(ConfigurationHeadConvergenceState.Converged));
            Assert.That(result.HeadConvergence.Attempts, Is.EqualTo(2));
            Assert.That(result.HeadConvergence.ObservedAuthoritativeHead, Is.EqualTo(newest.Revision));
            Assert.That(ControlRevision(result.State.Active!), Is.EqualTo(newest.Revision));
            Assert.That(projection.RuntimeEnabled, Is.True);
            Assert.That(projection.SourceCommit, Is.EqualTo(newest.Revision.Commit));
        });
    }

    [Test]
    public async Task Continuously_moving_head_returns_bounded_degraded_evidence()
    {
        await using var database = CreateDatabase();
        await InsertAgentAsync(database, AgentId, enabled: true);
        var validations = new[]
        {
            ValidAgentRevision(Commit('b'), Tree('1'), Aggregate('2'),
                AgentDocument(AgentId, false, Content('3')), [Revision(Commit('a'))]),
            ValidAgentRevision(Commit('d'), Tree('4'), Aggregate('5'),
                AgentDocument(AgentId, true, Content('6')), [Revision(Commit('c'))]),
            ValidAgentRevision(Commit('f'), Tree('7'), Aggregate('8'),
                AgentDocument(AgentId, false, Content('9')), [Revision(Commit('e'))]),
            ValidAgentRevision(Commit('1'), Tree('a'), Aggregate('b'),
                AgentDocument(AgentId, true, Content('c')), [Revision(Commit('0'))]),
        };
        var observedBetweenAttempts = new[]
        {
            Revision(Commit('c')),
            Revision(Commit('e')),
            Revision(Commit('0')),
            Revision(Commit('2')),
        };
        var heads = validations.SelectMany((validation, index) =>
                new[] { validation.Revision, observedBetweenAttempts[index] })
            .ToArray();
        var repository = new MovingHeadRepository(heads, validations);
        var reconciler = CreateReconciler(database);

        var result = await reconciler.ReconcileAuthoritativeHeadAsync(
            RequestContext("unstable-head"),
            Scope,
            repository);

        Assert.Multiple(() =>
        {
            Assert.That(result.HeadConvergence!.State,
                Is.EqualTo(ConfigurationHeadConvergenceState.Degraded));
            Assert.That(result.HeadConvergence.Attempts, Is.EqualTo(4));
            Assert.That(result.HeadConvergence.ObservedAuthoritativeHead,
                Is.EqualTo(observedBetweenAttempts[^1]));
            Assert.That(result.HeadConvergence.Diagnostic!.Code,
                Is.EqualTo("configuration_head_unstable"));
            Assert.That(ControlRevision(result.State.Active!), Is.EqualTo(validations[^1].Revision));
            Assert.That(repository.HeadReadCount, Is.EqualTo(8));
        });
    }

    [Test]
    public async Task Removing_materialized_agent_document_is_blocked_and_lkg_survives_restart()
    {
        var dataDir = Path.Combine(rootDir, "agent-removal-restart");
        Directory.CreateDirectory(dataDir);
        string activeRevisionSetId;
        await using (var database = new VivariumDatabase(dataDir))
        {
            await InsertAgentAsync(database, AgentId, enabled: true);
            var reconciler = CreateReconciler(database);
            var active = await reconciler.ReconcileAsync(
                RequestContext("initial-agent"),
                Scope,
                ValidAgentRevision(
                    Commit('b'),
                    Tree('c'),
                    Aggregate('d'),
                    AgentDocument(AgentId, enabled: false, Content('e')),
                    parents: [Revision(Commit('a'))]),
                operationId: "initial-agent-operation");
            activeRevisionSetId = active.Attempt.RevisionSetId;
            var withoutAgent = ValidAgentRevision(
                Commit('f'),
                Tree('1'),
                Aggregate('2'),
                documents: [],
                parents: [Revision(Commit('b'))],
                provenance: null);

            var blocked = await reconciler.ReconcileAsync(
                RequestContext("remove-agent"),
                Scope,
                withoutAgent,
                operationId: "remove-agent-operation");
            var projection = await ReadAgentProjectionAsync(database, AgentId);

            Assert.Multiple(() =>
            {
                Assert.That(blocked.Outcome, Is.EqualTo(ConfigurationReconciliationOutcome.Blocked));
                Assert.That(blocked.Attempt.Diagnostics.Single().Code,
                    Is.EqualTo("agent_document_removal_unsupported"));
                Assert.That(blocked.State.Active!.RevisionSetId, Is.EqualTo(activeRevisionSetId));
                Assert.That(blocked.State.LastKnownGood!.RevisionSetId, Is.EqualTo(activeRevisionSetId));
                Assert.That(projection.RuntimeEnabled, Is.False);
                Assert.That(projection.SourceCommit, Is.EqualTo(Commit('b')));
            });
        }

        await using var restarted = new VivariumDatabase(dataDir);
        var restored = await CreateReconciler(restarted).GetStateAsync(Scope);
        var restoredProjection = await ReadAgentProjectionAsync(restarted, AgentId);
        Assert.Multiple(() =>
        {
            Assert.That(restored!.Active!.RevisionSetId, Is.EqualTo(activeRevisionSetId));
            Assert.That(restored.LastKnownGood!.RevisionSetId, Is.EqualTo(activeRevisionSetId));
            Assert.That(restored.LatestAttempt.State, Is.EqualTo(ConfigurationRevisionSetState.Blocked));
            Assert.That(restored.LatestAttempt.Diagnostics.Single().Code,
                Is.EqualTo("agent_document_removal_unsupported"));
            Assert.That(restoredProjection.RuntimeEnabled, Is.False);
        });
    }

    [Test]
    public async Task Conflict_target_and_diff_replay_exactly_after_restart()
    {
        var dataDir = Path.Combine(rootDir, "conflict-replay");
        Directory.CreateDirectory(dataDir);
        var context = RequestContext("conflict", requestId: "conflict-request", actorId: "user-7");
        var target = new ConfigurationMutationTarget(
            "agent",
            AgentId,
            $".vivarium/agents/{AgentId}.yaml");
        var intent = new ConfigurationMutationIntent(
            "conflict-operation",
            "agent.set-enabled",
            Scope,
            Revision(Commit('a')),
            Content('1'),
            [target]);
        var conflictRevision = Revision(Commit('b'));
        var diff = new[]
        {
            new ConfigurationPathDiff(
                ".vivarium/agents/external-agent.yaml",
                ConfigurationPathChangeKind.Added,
                PreviousContentHash: null,
                ResultContentHash: Content('2')),
        };
        var result = new ConfigurationCommitResult(
            ConfigurationCommitOutcome.Conflict,
            intent.ExpectedBase,
            conflictRevision,
            ResultRevision: null,
            CandidateAggregateContentHash: Aggregate('3'),
            diff,
            Diagnostics: []);

        await using (var database = new VivariumDatabase(dataDir))
        {
            var operations = CreateReconciler(database).Operations;
            await operations.BeginAsync(context, intent);
            var recorded = await operations.RecordGitResultAsync(intent.OperationId, result);
            Assert.Multiple(() =>
            {
                Assert.That(recorded.ConflictRevision, Is.EqualTo(conflictRevision));
                Assert.That(recorded.Diff, Is.EqualTo(diff));
                Assert.That(recorded.Targets, Is.EqualTo(new[] { target }));
            });
        }

        await using var restarted = new VivariumDatabase(dataDir);
        var restartedOperations = CreateReconciler(restarted).Operations;
        var replay = await restartedOperations.BeginAsync(
            context with { CorrelationId = "conflict-retry" },
            intent with { OperationId = "ignored-retry-operation" });
        var repeated = await restartedOperations.RecordGitResultAsync(intent.OperationId, result);
        var audits = await new AuditEventStore(restarted).ListAsync();
        Assert.Multiple(() =>
        {
            Assert.That(replay.Outcome, Is.EqualTo(ConfigurationMutationBeginOutcome.Existing));
            Assert.That(replay.Operation.ConflictRevision, Is.EqualTo(conflictRevision));
            Assert.That(replay.Operation.Diff, Is.EqualTo(diff));
            Assert.That(repeated.Diff, Is.EqualTo(diff));
            Assert.That(audits.Count(audit => audit.Action == "configuration.mutation.conflicted"),
                Is.EqualTo(1));
            Assert.That(audits.Single(audit => audit.Action == "configuration.mutation.conflicted")
                .Details["affected_target_id"], Is.EqualTo(AgentId));
            Assert.That(audits.Single(audit => audit.Action == "configuration.mutation.conflicted")
                .Details["affected_path"], Is.EqualTo(target.Path));
        });
    }

    [Test]
    public async Task Repository_attempt_failure_is_audited_once_and_operation_remains_retryable()
    {
        await using var database = CreateDatabase();
        var operations = CreateReconciler(database).Operations;
        var target = new ConfigurationMutationTarget(
            "agent",
            AgentId,
            $".vivarium/agents/{AgentId}.yaml");
        var intent = new ConfigurationMutationIntent(
            "retryable-operation",
            "agent.set-enabled",
            Scope,
            Revision(Commit('a')),
            Content('1'),
            [target]);
        await operations.BeginAsync(
            RequestContext("retryable", requestId: "retryable-request"),
            intent);

        var first = await operations.RecordRepositoryAttemptFailureAsync(
            intent.OperationId,
            "repository-attempt-1",
            "config_git_timeout");
        var replay = await operations.RecordRepositoryAttemptFailureAsync(
            intent.OperationId,
            "repository-attempt-1",
            "config_git_timeout");
        var failures = await operations.ListRepositoryAttemptFailuresAsync(intent.OperationId);
        var operation = await operations.GetAsync(intent.OperationId);
        var audits = await new AuditEventStore(database).ListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(replay, Is.EqualTo(first));
            Assert.That(failures, Is.EqualTo(new[] { first }));
            Assert.That(operation!.State, Is.EqualTo(ConfigurationMutationState.Pending));
            Assert.That(audits.Count(audit =>
                audit.Action == "configuration.mutation.repository_attempt_failed"), Is.EqualTo(1));
            Assert.That(audits.Single(audit =>
                    audit.Action == "configuration.mutation.repository_attempt_failed")
                .Details["affected_target_id"], Is.EqualTo(AgentId));
        });
    }

    private VivariumDatabase CreateDatabase()
    {
        var dataDir = Path.Combine(rootDir, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);
        return new VivariumDatabase(dataDir);
    }

    private static ConfigurationReconciler CreateReconciler(VivariumDatabase database) =>
        new(database, new FixedTimeProvider(TestNow));

    private static ManagementRequestContext RequestContext(
        string correlationId,
        string? requestId = null,
        string actorId = "admin") =>
        new(
            new ManagementPrincipal("user", actorId, "test", LegacyScope: null),
            correlationId,
            requestId,
            "test");

    private static ConfigurationRevisionValidation ValidAgentRevision(
        string commit,
        string treeHash,
        string aggregateHash,
        ValidatedConfigurationDocument document,
        IReadOnlyList<ConfigurationRevision> parents,
        ConfigurationCommitProvenance? provenance = null) =>
        ValidAgentRevision(commit, treeHash, aggregateHash, [document], parents, provenance);

    private static ConfigurationRevisionValidation ValidAgentRevision(
        string commit,
        string treeHash,
        string aggregateHash,
        ValidatedConfigurationDocument first,
        ValidatedConfigurationDocument second,
        IReadOnlyList<ConfigurationRevision> parents,
        ConfigurationCommitProvenance? provenance = null) =>
        ValidAgentRevision(commit, treeHash, aggregateHash, [first, second], parents, provenance);

    private static ConfigurationRevisionValidation ValidAgentRevision(
        string commit,
        string treeHash,
        string aggregateHash,
        IReadOnlyList<ValidatedConfigurationDocument> documents,
        IReadOnlyList<ConfigurationRevision> parents,
        ConfigurationCommitProvenance? provenance)
    {
        var revision = Revision(commit);
        var descriptor = new ConfigurationRevisionDescriptor(
            revision,
            treeHash,
            aggregateHash,
            "vivarium.io/v1alpha1",
            parents,
            provenance);
        var validated = new ValidatedConfigurationRevision(
            descriptor,
            documents.OrderBy(document => document.Path, StringComparer.Ordinal).ToArray());
        return new ConfigurationRevisionValidation(revision, treeHash, validated, Diagnostics: []);
    }

    private static ValidatedConfigurationDocument AgentDocument(
        string agentId,
        bool enabled,
        string contentHash)
    {
        var bytes = Encoding.UTF8.GetBytes($$"""
            apiVersion: vivarium.io/v1alpha1
            kind: Agent
            id: {{agentId}}
            spec:
              enabled: {{enabled.ToString().ToLowerInvariant()}}
            """);
        return new ValidatedConfigurationDocument(
            $".vivarium/agents/{agentId}.yaml",
            "vivarium.io/v1alpha1",
            "Agent",
            agentId,
            contentHash,
            bytes,
            new Dictionary<string, string>
            {
                ["spec.enabled"] = enabled ? "true" : "false",
            });
    }

    private static ConfigurationRevision Revision(string commit) => new(RepositoryId, commit);

    private static ConfigurationRevision ControlRevision(StoredConfigurationRevisionSet revisionSet)
    {
        var member = revisionSet.Members.Single(item => item.RepositoryRole == "CONTROL");
        return new ConfigurationRevision(member.RepositoryId, member.Commit);
    }

    private static string Commit(char value) => new(value, 40);

    private static string Tree(char value) => new(value, 40);

    private static string Aggregate(char value) => new(value, 64);

    private static string Content(char value) => new(value, 64);

    private static Task InsertAgentAsync(
        VivariumDatabase database,
        string agentId,
        bool enabled) => database.WriteAsync(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO agents(
                agent_id, name, enabled, first_seen_unix_ms, last_seen_unix_ms)
            VALUES ($agentId, $name, $enabled, 1, 1);
            """;
        command.Parameters.AddWithValue("$agentId", agentId);
        command.Parameters.AddWithValue("$name", agentId);
        command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        command.ExecuteNonQuery();
        return true;
    });

    private static Task<(
        bool RuntimeEnabled,
        bool DesiredEnabled,
        string SourceCommit,
        string SourceRevisionSetId)> ReadAgentProjectionAsync(
        VivariumDatabase database,
        string agentId) => database.ReadAsync(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.enabled, d.enabled, d.source_commit, d.source_revision_set_id
            FROM agents a
            JOIN agent_desired_configuration d ON d.agent_id = a.agent_id
            WHERE a.agent_id = $agentId;
            """;
        command.Parameters.AddWithValue("$agentId", agentId);
        using var reader = command.ExecuteReader();
        Assert.That(reader.Read(), Is.True);
        return (
            reader.GetInt64(0) != 0,
            reader.GetInt64(1) != 0,
            reader.GetString(2),
            reader.GetString(3));
    });

    private static Task<T> ReadScalarAsync<T>(VivariumDatabase database, string sql) =>
        database.ReadAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T));
        });

    private sealed class MovingHeadRepository(
        IReadOnlyList<ConfigurationRevision> heads,
        IReadOnlyList<ConfigurationRevisionValidation> validations) : IConfigurationRepository
    {
        private readonly IReadOnlyDictionary<ConfigurationRevision, ConfigurationRevisionValidation>
            validationsByRevision = validations.ToDictionary(validation => validation.Revision);
        private int headReadCount;

        public string RepositoryId => ConfigurationReconciliationTests.RepositoryId;

        public int HeadReadCount => headReadCount;

        public Task<ConfigurationRevision> GetAuthoritativeHeadAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var index = Math.Min(Interlocked.Increment(ref headReadCount) - 1, heads.Count - 1);
            return Task.FromResult(heads[index]);
        }

        public Task<ConfigurationRevisionValidation> ValidateRevisionAsync(
            ConfigurationRevision revision,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(validationsByRevision[revision]);
        }

        public Task<ConfigurationCommitResult> UpsertDocumentAsync(
            ConfigurationDocumentMutation mutation,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("the moving-head reconciliation fake is read-only");
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
