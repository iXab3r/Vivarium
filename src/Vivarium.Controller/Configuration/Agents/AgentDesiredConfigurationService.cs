using System.Security.Cryptography;
using System.Text;
using Vivarium.Controller.Agents;
using Vivarium.Controller.Configuration.Git;
using Vivarium.Controller.Configuration.Reconciliation;
using Vivarium.Controller.Security;

namespace Vivarium.Controller.Configuration.Agents;

public sealed class AgentDesiredConfigurationService(
    IConfigurationRepository repository,
    ConfigurationReconciler reconciler,
    AgentStore agents,
    AgentLifecycleCoordinator agentLifecycle,
    ManagementCommandAuthorizer authorization,
    IAgentDesiredConfigurationActivationSink activationSink)
    : IAgentDesiredConfigurationService
{
    public const string MaterializationScope = "controller";
    public const string OperationKindPrefix = "agent.set-enabled:";
    private readonly SemaphoreSlim mutationGate = new(1, 1);

    public async Task<AgentDesiredConfigurationSnapshot?> GetAsync(
        string agentId,
        CancellationToken cancellationToken = default)
    {
        agentId = NormalizeAgentId(agentId);
        if (await agents.GetAsync(agentId) is null)
        {
            return null;
        }

        var head = await repository.GetAuthoritativeHeadAsync(cancellationToken);
        var headValidation = await repository.ValidateRevisionAsync(head, cancellationToken);
        var materialization = await reconciler.GetStateAsync(MaterializationScope);
        var appliedRevision = materialization?.Active is null
            ? null
            : ControlRevision(materialization.Active);
        var appliedValidation = appliedRevision is null
            ? null
            : appliedRevision == head
                ? headValidation
                : await repository.ValidateRevisionAsync(appliedRevision, cancellationToken);
        var (state, diagnostics) = Classify(head, materialization);
        return new AgentDesiredConfigurationSnapshot(
            agentId,
            ReadEnabled(headValidation, agentId),
            ReadEnabled(appliedValidation, agentId),
            head,
            appliedRevision,
            state,
            diagnostics);
    }

    public async Task<AgentDesiredConfigurationMutationResult> SetEnabledAsync(
        ManagementRequestContext context,
        string agentId,
        bool enabled,
        ConfigurationRevision expectedBase,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(expectedBase);
        await authorization.DemandAsync(
            context,
            ManagementPermission.AgentManage,
            enabled ? "agent.configuration.enable" : "agent.configuration.disable",
            "agent",
            agentId);
        agentId = NormalizeAgentId(agentId);
        ValidateRequestIdentity(context);

        await mutationGate.WaitAsync(cancellationToken);
        try
        {
            await using var lifecycleLease = await agentLifecycle.AcquireAsync(
                agentId,
                cancellationToken);
            if (await agents.GetAsync(agentId) is null)
            {
                throw new AgentDesiredConfigurationNotFoundException(agentId);
            }

            if (!string.Equals(expectedBase.RepositoryId, repository.RepositoryId, StringComparison.Ordinal))
            {
                throw new AgentDesiredConfigurationConflictException(
                    "configuration_repository_mismatch",
                    "the supplied configuration revision belongs to another repository",
                    await repository.GetAuthoritativeHeadAsync(cancellationToken),
                    appliedRevision: null,
                    diagnostics: []);
            }

            var operationId = ManagementIdentifiers.NewId();
            var intent = new ConfigurationMutationIntent(
                operationId,
                OperationKindFor(agentId),
                MaterializationScope,
                expectedBase,
                HashRequest(agentId, enabled),
                Targets:
                [
                    new ConfigurationMutationTarget("agent", agentId, AgentPath(agentId)),
                ]);
            ConfigurationMutationBeginResult begun;
            try
            {
                begun = await reconciler.Operations.BeginAsync(context, intent);
            }
            catch (ConfigurationIdempotencyConflictException exception)
            {
                var snapshot = await RequiredSnapshotAsync(agentId, cancellationToken);
                throw new AgentDesiredConfigurationConflictException(
                    "idempotency_key_reused",
                    "the Idempotency-Key was already used for different Agent settings",
                    snapshot.AuthoritativeRevision,
                    snapshot.AppliedRevision,
                    diagnostics: [],
                    exception);
            }

            if (begun.Outcome == ConfigurationMutationBeginOutcome.Existing)
            {
                return await ResumeAsync(
                    context,
                    agentId,
                    enabled,
                    begun.Operation,
                    cancellationToken);
            }

            return await CommitAsync(
                context,
                agentId,
                enabled,
                begun.Operation,
                cancellationToken);
        }
        finally
        {
            mutationGate.Release();
        }
    }

    private async Task<AgentDesiredConfigurationMutationResult> ResumeAsync(
        ManagementRequestContext context,
        string agentId,
        bool enabled,
        ConfigurationMutationOperation operation,
        CancellationToken cancellationToken)
    {
        if (operation.State == ConfigurationMutationState.Pending)
        {
            var head = await repository.GetAuthoritativeHeadAsync(cancellationToken);
            var validation = await repository.ValidateRevisionAsync(head, cancellationToken);
            if (validation.Validated?.Descriptor.ControllerProvenance is { } provenance &&
                string.Equals(provenance.OperationId, operation.OperationId, StringComparison.Ordinal))
            {
                var recovered = await ReconcileExactHeadAsync(
                    context,
                    operation.OperationId,
                    head,
                    agentId,
                    cancellationToken);
                return await CompleteReconciliationAsync(
                    agentId,
                    enabled,
                    operation.OperationId,
                    operation.ExpectedBase,
                    head,
                    recovered,
                    replayed: true,
                    cancellationToken);
            }

            return await CommitAsync(context, agentId, enabled, operation, cancellationToken);
        }

        if (operation.State == ConfigurationMutationState.Committed)
        {
            var resultRevision = operation.ResultRevision
                ?? throw new InvalidOperationException("committed configuration operation has no revision");
            var reconciled = await ReconcileExactHeadAsync(
                context,
                operation.OperationId,
                resultRevision,
                agentId,
                cancellationToken);
            return await CompleteReconciliationAsync(
                agentId,
                enabled,
                operation.OperationId,
                operation.ExpectedBase,
                resultRevision,
                reconciled,
                replayed: true,
                cancellationToken);
        }

        if (operation.State == ConfigurationMutationState.Applied)
        {
            var resultRevision = operation.ResultRevision
                ?? throw new InvalidOperationException("applied configuration operation has no revision");
            var currentApplied = await RequiredSnapshotAsync(agentId, cancellationToken);
            var snapshot = currentApplied.AppliedRevision == resultRevision
                ? currentApplied
                : await SnapshotAtRevisionAsync(agentId, resultRevision, cancellationToken);
            var diff = await ReconstructDiffAsync(
                agentId,
                operation.ExpectedBase,
                resultRevision,
                cancellationToken);
            if (currentApplied.AppliedRevision == resultRevision)
            {
                NotifyApplied(
                    agentId,
                    enabled,
                    resultRevision,
                    operation.OperationId,
                    currentApplied);
            }

            return new AgentDesiredConfigurationMutationResult(
                operation.OperationId,
                resultRevision,
                snapshot,
                diff,
                Replayed: true);
        }

        var current = await RequiredSnapshotAsync(agentId, cancellationToken);
        if (operation.State == ConfigurationMutationState.Conflict)
        {
            throw new AgentDesiredConfigurationPreconditionException(
                operation.ExpectedBase,
                operation.ConflictRevision ?? current.AuthoritativeRevision,
                operation.Diff);
        }

        throw new AgentDesiredConfigurationConflictException(
            string.IsNullOrWhiteSpace(operation.FailureCode)
                ? "configuration_invalid"
                : operation.FailureCode,
            string.IsNullOrWhiteSpace(operation.FailureSummary)
                ? "the Agent settings mutation was rejected"
                : operation.FailureSummary,
            current.AuthoritativeRevision,
            current.AppliedRevision,
            current.Diagnostics);
    }

    private async Task<AgentDesiredConfigurationMutationResult> CommitAsync(
        ManagementRequestContext context,
        string agentId,
        bool enabled,
        ConfigurationMutationOperation operation,
        CancellationToken cancellationToken)
    {
        var mutation = new ConfigurationDocumentMutation(
            operation.ExpectedBase,
            AgentPath(agentId),
            RenderAgent(agentId, enabled),
            new ConfigurationCommitMetadata(
                enabled ? $"Enable Agent {agentId}" : $"Disable Agent {agentId}",
                operation.OperationId,
                context.RequestId!,
                context.CorrelationId,
                new ConfigurationCommitActor(
                    context.Principal.ActorId,
                    context.Principal.ActorType,
                    context.Principal.ActorId)));
        ConfigurationCommitResult gitResult;
        try
        {
            gitResult = await repository.UpsertDocumentAsync(mutation, cancellationToken);
        }
        catch (ConfigurationRepositoryException exception)
        {
            await reconciler.Operations.RecordRepositoryAttemptFailureAsync(
                operation.OperationId,
                ManagementIdentifiers.NewId(),
                SafeRepositoryFailureCode(exception.Code));
            throw;
        }
        var recorded = await reconciler.Operations.RecordGitResultAsync(
            operation.OperationId,
            gitResult);
        if (gitResult.Outcome == ConfigurationCommitOutcome.Conflict)
        {
            throw new AgentDesiredConfigurationPreconditionException(
                operation.ExpectedBase,
                gitResult.CurrentRevision,
                gitResult.Diff);
        }

        if (gitResult.Outcome == ConfigurationCommitOutcome.Rejected)
        {
            var diagnostic = gitResult.Diagnostics.FirstOrDefault()
                ?? new ConfigurationValidationDiagnostic(
                    "configuration_invalid",
                    AgentPath(agentId),
                    null,
                    "the Agent settings mutation was rejected");
            var snapshot = await RequiredSnapshotAsync(agentId, cancellationToken);
            throw new AgentDesiredConfigurationConflictException(
                diagnostic.Code.ToLowerInvariant(),
                diagnostic.Summary,
                snapshot.AuthoritativeRevision,
                snapshot.AppliedRevision,
                gitResult.Diagnostics);
        }

        var resultRevision = recorded.ResultRevision
            ?? throw new InvalidOperationException("accepted Git mutation has no result revision");
        var reconciled = await ReconcileExactHeadAsync(
            context,
            operation.OperationId,
            resultRevision,
            agentId,
            cancellationToken);
        return await CompleteReconciliationAsync(
            agentId,
            enabled,
            operation.OperationId,
            operation.ExpectedBase,
            resultRevision,
            reconciled,
            replayed: false,
            cancellationToken,
            gitResult.Diff);
    }

    private async Task<ConfigurationReconciliationResult> ReconcileExactHeadAsync(
        ManagementRequestContext context,
        string operationId,
        ConfigurationRevision expectedHead,
        string agentId,
        CancellationToken cancellationToken)
    {
        var currentHead = await repository.GetAuthoritativeHeadAsync(cancellationToken);
        if (currentHead != expectedHead)
        {
            await ThrowHeadAdvancedAsync(agentId, cancellationToken);
        }

        var validation = await repository.ValidateRevisionAsync(currentHead, cancellationToken);
        if (await repository.GetAuthoritativeHeadAsync(cancellationToken) != expectedHead)
        {
            await ThrowHeadAdvancedAsync(agentId, cancellationToken);
        }

        var reconciled = await reconciler.ReconcileAsync(
            context,
            MaterializationScope,
            validation,
            operationId,
            cancellationToken);
        if (await repository.GetAuthoritativeHeadAsync(cancellationToken) != expectedHead)
        {
            await ThrowHeadAdvancedAsync(agentId, cancellationToken);
        }

        return reconciled;
    }

    private async Task ThrowHeadAdvancedAsync(
        string agentId,
        CancellationToken cancellationToken)
    {
        var current = await RequiredSnapshotAsync(agentId, cancellationToken);
        throw new AgentDesiredConfigurationConflictException(
            "configuration_head_advanced_before_apply",
            "the authoritative configuration advanced before the Agent settings could be applied",
            current.AuthoritativeRevision,
            current.AppliedRevision,
            current.Diagnostics);
    }

    private async Task<AgentDesiredConfigurationMutationResult> CompleteReconciliationAsync(
        string agentId,
        bool enabled,
        string operationId,
        ConfigurationRevision expectedBase,
        ConfigurationRevision resultRevision,
        ConfigurationReconciliationResult reconciled,
        bool replayed,
        CancellationToken cancellationToken,
        IReadOnlyList<ConfigurationPathDiff>? knownDiff = null)
    {
        if (reconciled.Outcome is ConfigurationReconciliationOutcome.Invalid or
            ConfigurationReconciliationOutcome.Blocked)
        {
            var diagnostic = reconciled.Attempt.Diagnostics.FirstOrDefault()
                ?? new ConfigurationValidationDiagnostic(
                    "configuration_reconciliation_conflict",
                    AgentPath(agentId),
                    null,
                    "the committed Agent settings could not become active");
            throw new AgentDesiredConfigurationConflictException(
                diagnostic.Code.ToLowerInvariant(),
                diagnostic.Summary,
                await repository.GetAuthoritativeHeadAsync(cancellationToken),
                reconciled.State.Active is null
                    ? null
                    : ControlRevision(reconciled.State.Active),
                reconciled.Attempt.Diagnostics);
        }

        var snapshot = await RequiredSnapshotAsync(agentId, cancellationToken);
        var diff = knownDiff ?? await ReconstructDiffAsync(
            agentId,
            expectedBase,
            resultRevision,
            cancellationToken);
        NotifyApplied(agentId, enabled, resultRevision, operationId, snapshot);
        return new AgentDesiredConfigurationMutationResult(
            operationId,
            resultRevision,
            snapshot,
            diff,
            replayed);
    }

    private void NotifyApplied(
        string agentId,
        bool enabled,
        ConfigurationRevision resultRevision,
        string operationId,
        AgentDesiredConfigurationSnapshot snapshot)
    {
        if (snapshot.AppliedRevision != resultRevision || snapshot.AppliedEnabled != enabled)
        {
            throw new InvalidOperationException(
                "the reconciled Agent projection does not match the requested desired setting");
        }

        activationSink.OnApplied(new AgentDesiredConfigurationActivation(
            agentId,
            enabled,
            resultRevision,
            operationId));
    }

    private async Task<IReadOnlyList<ConfigurationPathDiff>> ReconstructDiffAsync(
        string agentId,
        ConfigurationRevision expectedBase,
        ConfigurationRevision resultRevision,
        CancellationToken cancellationToken)
    {
        var path = AgentPath(agentId);
        var before = await repository.ValidateRevisionAsync(expectedBase, cancellationToken);
        var after = await repository.ValidateRevisionAsync(resultRevision, cancellationToken);
        var beforeDocument = FindAgent(before, agentId);
        var afterDocument = FindAgent(after, agentId)
            ?? throw new InvalidOperationException("result revision has no requested Agent document");
        return
        [
            new ConfigurationPathDiff(
                path,
                expectedBase == resultRevision
                    ? ConfigurationPathChangeKind.Unchanged
                    : beforeDocument is null
                        ? ConfigurationPathChangeKind.Added
                        : ConfigurationPathChangeKind.Modified,
                beforeDocument?.ContentHash,
                afterDocument.ContentHash),
        ];
    }

    private async Task<AgentDesiredConfigurationSnapshot> RequiredSnapshotAsync(
        string agentId,
        CancellationToken cancellationToken) =>
        await GetAsync(agentId, cancellationToken)
        ?? throw new AgentDesiredConfigurationNotFoundException(agentId);

    private async Task<AgentDesiredConfigurationSnapshot> SnapshotAtRevisionAsync(
        string agentId,
        ConfigurationRevision revision,
        CancellationToken cancellationToken)
    {
        var validation = await repository.ValidateRevisionAsync(revision, cancellationToken);
        var enabled = ReadEnabled(validation, agentId);
        if (!validation.IsValid || enabled is null)
        {
            throw new InvalidOperationException(
                "the applied configuration operation no longer resolves to its Agent document");
        }

        return new AgentDesiredConfigurationSnapshot(
            agentId,
            enabled,
            enabled,
            revision,
            revision,
            AgentDesiredConfigurationState.Active,
            Diagnostics: []);
    }

    private static (AgentDesiredConfigurationState State,
        IReadOnlyList<ConfigurationValidationDiagnostic> Diagnostics) Classify(
        ConfigurationRevision head,
        ConfigurationMaterializationState? materialization)
    {
        if (materialization is null)
        {
            return (AgentDesiredConfigurationState.Pending, []);
        }

        var latestRevision = ControlRevision(materialization.LatestAttempt);
        if (latestRevision != head)
        {
            return (AgentDesiredConfigurationState.Pending, []);
        }

        return materialization.LatestAttempt.State switch
        {
            ConfigurationRevisionSetState.Active => (
                AgentDesiredConfigurationState.Active,
                materialization.LatestAttempt.Diagnostics),
            ConfigurationRevisionSetState.Invalid => (
                AgentDesiredConfigurationState.Invalid,
                materialization.LatestAttempt.Diagnostics),
            ConfigurationRevisionSetState.Blocked => (
                AgentDesiredConfigurationState.Blocked,
                materialization.LatestAttempt.Diagnostics),
            ConfigurationRevisionSetState.Superseded => (
                AgentDesiredConfigurationState.Pending,
                materialization.LatestAttempt.Diagnostics),
            _ => throw new ArgumentOutOfRangeException(),
        };
    }

    private static ConfigurationRevision ControlRevision(StoredConfigurationRevisionSet revisionSet)
    {
        var member = revisionSet.Members.Single(member =>
            string.Equals(member.RepositoryRole, "CONTROL", StringComparison.Ordinal));
        return new ConfigurationRevision(member.RepositoryId, member.Commit);
    }

    private static bool? ReadEnabled(
        ConfigurationRevisionValidation? validation,
        string agentId)
    {
        var document = validation is null ? null : FindAgent(validation, agentId);
        return document?.ScalarFields.TryGetValue("spec.enabled", out var text) == true &&
            bool.TryParse(text, out var enabled)
                ? enabled
                : null;
    }

    private static ValidatedConfigurationDocument? FindAgent(
        ConfigurationRevisionValidation validation,
        string agentId) => validation.Validated?.Documents.SingleOrDefault(document =>
        string.Equals(document.Kind, "Agent", StringComparison.Ordinal) &&
        string.Equals(document.Id, agentId, StringComparison.Ordinal));

    private static string AgentPath(string agentId) => $".vivarium/agents/{agentId}.yaml";

    private static ReadOnlyMemory<byte> RenderAgent(string agentId, bool enabled) =>
        Encoding.UTF8.GetBytes($"""
            apiVersion: vivarium.io/v1alpha1
            kind: Agent
            id: {agentId}
            spec:
              enabled: {enabled.ToString().ToLowerInvariant()}

            """);

    private static string HashRequest(string agentId, bool enabled) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"agent.set-enabled\n{agentId}\n{enabled.ToString().ToLowerInvariant()}\n")));

    private static string SafeRepositoryFailureCode(string value)
    {
        var normalized = new string(value.Take(128).Select(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-'
                ? char.ToLowerInvariant(character)
                : '_').ToArray());
        return string.IsNullOrWhiteSpace(normalized)
            ? "configuration_repository_failed"
            : normalized;
    }

    private static string OperationKindFor(string agentId) =>
        OperationKindPrefix + Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(AgentPath(agentId))));

    private static void ValidateRequestIdentity(ManagementRequestContext context)
    {
        if (string.IsNullOrWhiteSpace(context.RequestId) || context.RequestId.Length > 256)
        {
            throw new ArgumentException(
                "Agent configuration mutations require a 1-256 character request ID",
                nameof(context));
        }
    }

    private static string NormalizeAgentId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 128 ||
            !(char.IsAsciiLetterLower(value[0]) || char.IsAsciiDigit(value[0])) ||
            !(char.IsAsciiLetterLower(value[^1]) || char.IsAsciiDigit(value[^1])) ||
            value.Any(character =>
                !(char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) ||
                  character is '.' or '-')))
        {
            throw new AgentDesiredConfigurationValidationException(
                "agent_id_invalid",
                "Agent IDs in desired configuration must be lowercase stable ASCII identifiers");
        }

        return value;
    }
}
