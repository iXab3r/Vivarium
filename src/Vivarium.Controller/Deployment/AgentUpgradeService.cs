using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Vivarium.Contracts.V1;
using Vivarium.Controller.Agents;
using Vivarium.Controller.Agents.Compatibility;
using Vivarium.Controller.Security;

namespace Vivarium.Controller.Deployment;

public sealed class AgentUpgradeService : BackgroundService
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan CandidateProbation = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RestartRetry = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan HealthRetry = TimeSpan.FromSeconds(5);
    private const int MaximumRestartDispatches = 3;

    private readonly AgentUpgradeStore operations;
    private readonly AgentPackageStore packages;
    private readonly AgentStore agents;
    private readonly AgentRegistry registry;
    private readonly AgentLifecycleCoordinator lifecycle;
    private readonly ManagementCommandAuthorizer authorization;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<AgentUpgradeService> log;
    private readonly SemaphoreSlim pass = new(1, 1);
    private readonly Dictionary<(string OperationId, long Generation), DateTimeOffset> candidateFirstSeen = [];
    private readonly Dictionary<(string OperationId, long Generation), DateTimeOffset> lastHealthSignal = [];
    private readonly Dictionary<(string OperationId, long Generation), DateTimeOffset> commitSignals = [];
    private readonly Dictionary<(string OperationId, long Generation), DateTimeOffset> finalizationSignals = [];
    private readonly Dictionary<(string OperationId, long Generation), DateTimeOffset> rollbackSignals = [];

    public AgentUpgradeService(
        AgentUpgradeStore operations,
        AgentPackageStore packages,
        AgentStore agents,
        AgentRegistry registry,
        AgentLifecycleCoordinator lifecycle,
        ManagementCommandAuthorizer authorization,
        TimeProvider timeProvider,
        ILogger<AgentUpgradeService> log)
    {
        this.operations = operations;
        this.packages = packages;
        this.agents = agents;
        this.registry = registry;
        this.lifecycle = lifecycle;
        this.authorization = authorization;
        this.timeProvider = timeProvider;
        this.log = log;
    }

    public async Task<AgentUpgradeCreation> CreateAsync(
        ManagementRequestContext context,
        string agentId,
        string requestId,
        string reason,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ValidateId(agentId, nameof(agentId));
        requestId = ValidateRequestId(requestId);
        reason = ValidateReason(reason);
        var effectiveTimeout = timeout ?? DefaultTimeout;
        if (effectiveTimeout < TimeSpan.FromMinutes(2) || effectiveTimeout > TimeSpan.FromHours(24))
        {
            throw new AgentUpgradeException(
                "agent_upgrade_timeout_invalid",
                "Upgrade timeout must be between 2 minutes and 24 hours.",
                StatusCodes.Status422UnprocessableEntity);
        }

        await authorization.DemandAsync(
            context,
            ManagementPermission.AgentManage,
            "agent.upgrade.request",
            "agent",
            agentId);
        AgentUpgradeCreation created;
        await pass.WaitAsync(cancellationToken);
        try
        {
            await using (await lifecycle.AcquireAsync(agentId, cancellationToken))
            {
                var projection = await agents.GetProjectionAsync(agentId)
                    ?? throw new AgentUpgradeException(
                        "agent_not_found", $"Agent '{agentId}' does not exist.",
                        StatusCodes.Status404NotFound);
                if (!projection.Agent.Authorized)
                {
                    throw new AgentUpgradeException(
                        "agent_not_authorized",
                        "Only an authorized Agent can receive a central package update.");
                }
                var actualRid = ReadRid(
                    projection.Observation?.Facts.OsFamily ?? projection.Agent.OsFamily,
                    projection.Observation?.Facts.OsArchitecture ?? projection.Agent.Architecture);
                var package = packages.FindCurrentRelease(actualRid)
                    ?? throw new AgentUpgradeException(
                        "server_agent_release_unavailable",
                        $"Server version '{AgentHubService.ServerVersion}' has no bundled " +
                        $"Agent package for RID '{actualRid}'.",
                        StatusCodes.Status503ServiceUnavailable);
                var requestHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                    $"agent-upgrade\n{agentId}\n{package.PackageId}\n{reason}\n{effectiveTimeout.Ticks}")))
                    .ToLowerInvariant();
                var replay = await operations.FindReplayAsync(context, requestId, requestHash);
                if (replay is not null)
                {
                    return replay;
                }

                var priorDigest = projection.Observation?.PackageDigestSha256;
                if (string.IsNullOrEmpty(priorDigest))
                {
                    throw new AgentUpgradeException(
                        "agent_prior_package_unknown",
                        "The Agent must report an exact active package digest before it can be safely upgraded.",
                        StatusCodes.Status422UnprocessableEntity);
                }
                if (!projection.Capabilities
                    .Concat(projection.Observation?.Capabilities ?? [])
                    .Any(capability =>
                        capability.CapabilityId ==
                            AgentProtocolCompatibility.BootstrapSupervisorCapabilityId &&
                        capability.ContractMajor == 1))
                {
                    throw new AgentUpgradeException(
                        "agent_bootstrap_supervisor_required",
                        "The Agent must report the compatible bootstrap supervisor before it can be safely upgraded.",
                        StatusCodes.Status422UnprocessableEntity);
                }
                if (string.Equals(priorDigest, package.Sha256, StringComparison.Ordinal))
                {
                    throw new AgentUpgradeException(
                        "agent_package_already_active",
                        "The Agent already reports the requested package digest.");
                }

                created = await operations.CreateAsync(
                    context,
                    package,
                    agentId,
                    requestId,
                    requestHash,
                    reason,
                    priorDigest,
                    projection.Agent.ConnectionGeneration,
                    effectiveTimeout);
                if (!created.Operation.IsTerminal)
                {
                    registry.SetMaintenanceDrain(agentId, drained: true);
                }
            }
        }
        finally
        {
            pass.Release();
        }

        await TryAdvanceAsync(created.Operation.OperationId, cancellationToken);
        return created with
        {
            Operation = await operations.FindAsync(created.Operation.OperationId) ?? created.Operation,
        };
    }

    public Task<AgentUpgradeOperation?> FindAsync(string operationId) => operations.FindAsync(operationId);

    public DateTimeOffset UtcNow => timeProvider.GetUtcNow();

    public Task<IReadOnlyList<AgentUpgradeOperation>> ListAsync(string? agentId = null) =>
        operations.ListAsync(agentId);

    public Task<IReadOnlyList<AgentUpgradeEvent>> ListEventsAsync(string operationId) =>
        operations.ListEventsAsync(operationId);

    public Task<AgentUpgradeOperation?> FindActiveForAgentAsync(string agentId) =>
        operations.FindActiveAsync(agentId);

    public async Task<bool> ReportBootstrapFailureAsync(
        string agentId,
        string operationId,
        string failureCode,
        CancellationToken cancellationToken = default)
    {
        ValidateId(agentId, nameof(agentId));
        ValidateId(operationId, nameof(operationId));
        if (failureCode != "child_termination_failed")
        {
            return false;
        }

        await pass.WaitAsync(cancellationToken);
        try
        {
            await using (await lifecycle.AcquireAsync(agentId, cancellationToken))
            {
                var operation = await operations.FindAsync(operationId);
                if (operation is null || operation.AgentId != agentId ||
                    operation.State == AgentUpgradeState.Draining || !operation.DrainHeld)
                {
                    return false;
                }
                if (operation.State == AgentUpgradeState.Failed)
                {
                    return string.Equals(operation.FailureCode, failureCode, StringComparison.Ordinal);
                }

                var changed = await operations.FailAsync(
                    operation.OperationId,
                    operation.MaintenanceFence,
                    failureCode);
                registry.SetMaintenanceDrain(agentId, drained: true);
                return changed;
            }
        }
        finally
        {
            pass.Release();
        }
    }

    public async Task<bool> CancelAndReleaseAsync(
        ManagementRequestContext context,
        string operationId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var operation = await operations.FindAsync(operationId)
            ?? throw new AgentUpgradeException(
                "agent_upgrade_not_found", $"Upgrade operation '{operationId}' does not exist.",
                StatusCodes.Status404NotFound);
        await authorization.DemandAsync(
            context,
            ManagementPermission.AgentManage,
            "agent.upgrade.cancel",
            "agent-upgrade",
            operationId);

        await pass.WaitAsync(cancellationToken);
        try
        {
            await using (await lifecycle.AcquireAsync(operation.AgentId, cancellationToken))
            {
                var changed = await operations.CancelOrRequestRollbackAsync(context, operationId, reason);
                var current = await operations.FindAsync(operationId);
                registry.SetMaintenanceDrain(operation.AgentId, current?.DrainHeld == true);
                return changed;
            }
        }
        finally
        {
            pass.Release();
        }
    }

    public async Task<UpgradeHealthAccepted?> OnAgentReconciledAsync(
        AgentConnectionHandle reconciledConnection,
        CancellationToken cancellationToken = default)
    {
        var operation = await operations.FindActiveAsync(reconciledConnection.AgentId) ??
            await operations.FindDrainOwnerAsync(reconciledConnection.AgentId);
        if (operation is not null)
        {
            await TryAdvanceAsync(operation.OperationId, cancellationToken);
        }

        // Health/commit messages are emitted by the durable coordinator after probation. Returning
        // null keeps the AgentHub connection path free of a second, racy acceptance path.
        return null;
    }

    public async Task ConfirmHealthAsync(
        AgentConnectionHandle connection,
        UpgradeHealthConfirmed confirmation,
        CancellationToken cancellationToken = default)
    {
        await pass.WaitAsync(cancellationToken);
        try
        {
            await using (await lifecycle.AcquireAsync(connection.AgentId, cancellationToken))
            {
                var operation = await operations.FindActiveAsync(connection.AgentId);
                if (operation is null || operation.State != AgentUpgradeState.AwaitingHealth ||
                    timeProvider.GetUtcNow() >= operation.Deadline ||
                    !TryGetExactCandidate(operation, connection, out _, out _) ||
                    confirmation.ConnectionGeneration != checked((ulong)connection.ConnectionGeneration) ||
                    !string.Equals(confirmation.SessionId, connection.SessionId, StringComparison.Ordinal) ||
                    !string.Equals(confirmation.OperationId, operation.OperationId, StringComparison.Ordinal) ||
                    !string.Equals(confirmation.PackageSha256, operation.Package.Sha256, StringComparison.Ordinal))
                {
                    return;
                }

                if (await operations.BeginCommitAsync(
                        operation.OperationId,
                        operation.MaintenanceFence,
                        connection.ConnectionGeneration,
                        confirmation.PackageSha256))
                {
                    SendCommitAcceptance(operation, connection);
                }
            }
        }
        finally
        {
            pass.Release();
        }
    }

    public async Task ConfirmCommitAsync(
        AgentConnectionHandle connection,
        UpgradeCommitConfirmed confirmation,
        CancellationToken cancellationToken = default)
    {
        await pass.WaitAsync(cancellationToken);
        try
        {
            await using (await lifecycle.AcquireAsync(connection.AgentId, cancellationToken))
            {
                var operation = await operations.FindActiveAsync(connection.AgentId);
                if (operation is null || operation.State != AgentUpgradeState.CommitPending)
                {
                    return;
                }
                if (timeProvider.GetUtcNow() >= operation.Deadline)
                {
                    await operations.RequestAutomaticRollbackAsync(
                        operation.OperationId,
                        operation.MaintenanceFence,
                        "upgrade_deadline_exceeded");
                    return;
                }
                if (
                    operation.ObservedConnectionGeneration != connection.ConnectionGeneration ||
                    !TryGetExactCandidate(operation, connection, out _, out _) ||
                    confirmation.ConnectionGeneration != checked((ulong)connection.ConnectionGeneration) ||
                    !string.Equals(confirmation.SessionId, connection.SessionId, StringComparison.Ordinal) ||
                    !string.Equals(confirmation.OperationId, operation.OperationId, StringComparison.Ordinal) ||
                    !string.Equals(confirmation.PackageSha256, operation.Package.Sha256, StringComparison.Ordinal))
                {
                    return;
                }

                if (await operations.BeginFinalizationAsync(
                        operation.OperationId,
                        operation.MaintenanceFence,
                        connection.ConnectionGeneration,
                        confirmation.PackageSha256))
                {
                    SendCommitRecorded(operation, connection);
                }
            }
        }
        finally
        {
            pass.Release();
        }
    }

    public async Task ConfirmFinalizationAsync(
        AgentConnectionHandle connection,
        UpgradeFinalizationConfirmed confirmation,
        CancellationToken cancellationToken = default)
    {
        await pass.WaitAsync(cancellationToken);
        try
        {
            await using (await lifecycle.AcquireAsync(connection.AgentId, cancellationToken))
            {
                var operation = await operations.FindActiveAsync(connection.AgentId);
                if (operation is null || operation.State != AgentUpgradeState.Finalizing)
                {
                    return;
                }
                if (timeProvider.GetUtcNow() >= operation.Deadline)
                {
                    await operations.RequestAutomaticRollbackAsync(
                        operation.OperationId,
                        operation.MaintenanceFence,
                        "upgrade_deadline_exceeded");
                    return;
                }
                if (
                    operation.ObservedConnectionGeneration != connection.ConnectionGeneration ||
                    !TryGetExactCandidate(operation, connection, out _, out _) ||
                    confirmation.ConnectionGeneration != checked((ulong)connection.ConnectionGeneration) ||
                    !string.Equals(confirmation.SessionId, connection.SessionId, StringComparison.Ordinal) ||
                    !string.Equals(confirmation.OperationId, operation.OperationId, StringComparison.Ordinal) ||
                    !string.Equals(confirmation.PackageSha256, operation.Package.Sha256, StringComparison.Ordinal))
                {
                    return;
                }

                if (await operations.CompleteAsync(
                        operation.OperationId,
                        operation.MaintenanceFence,
                        connection.ConnectionGeneration,
                        confirmation.PackageSha256))
                {
                    registry.SetMaintenanceDrain(operation.AgentId, drained: false);
                    ClearTransient(operation.OperationId);
                }
            }
        }
        finally
        {
            pass.Release();
        }
    }

    public async Task TryAdvanceAsync(
        string operationId,
        CancellationToken cancellationToken = default)
    {
        await pass.WaitAsync(cancellationToken);
        try
        {
            var operation = await operations.FindAsync(operationId);
            if (operation is null || operation.State is
                AgentUpgradeState.Succeeded or AgentUpgradeState.RolledBack or AgentUpgradeState.Cancelled)
            {
                return;
            }

            await using (await lifecycle.AcquireAsync(operation.AgentId, cancellationToken))
            {
                operation = await operations.FindAsync(operationId);
                if (operation is null || operation.State is
                    AgentUpgradeState.Succeeded or AgentUpgradeState.RolledBack or AgentUpgradeState.Cancelled)
                {
                    return;
                }

                registry.SetMaintenanceDrain(operation.AgentId, drained: operation.DrainHeld);
                if (!registry.TryGetMaintenanceConnection(
                        operation.AgentId,
                        out var connection,
                        out var hello,
                        out _))
                {
                    if (timeProvider.GetUtcNow() >= operation.Deadline &&
                        operation.State == AgentUpgradeState.Draining)
                    {
                        await operations.CancelOrRequestRollbackAsync(
                            ManagementRequestContext.System("agent-upgrade-deadline"),
                            operation.OperationId,
                            "upgrade_deadline_before_handoff");
                        registry.SetMaintenanceDrain(operation.AgentId, drained: false);
                    }
                    else if (timeProvider.GetUtcNow() >= operation.Deadline &&
                             operation.State is not (
                                 AgentUpgradeState.RollbackRequested or AgentUpgradeState.Failed))
                    {
                        await operations.RequestAutomaticRollbackAsync(
                            operation.OperationId,
                            operation.MaintenanceFence,
                            "upgrade_deadline_exceeded");
                    }

                    return;
                }

                var activeConnection = connection!;
                var activeHello = hello!;
                if (IsExactPrior(operation, activeConnection, activeHello))
                {
                    if (await operations.RollbackObservedAsync(
                            operation.OperationId,
                            operation.MaintenanceFence,
                            activeConnection.ConnectionGeneration,
                            activeHello.AgentPackageSha256,
                            string.IsNullOrWhiteSpace(activeHello.UpgradeFailureCode)
                                ? null
                                : activeHello.UpgradeFailureCode))
                    {
                        registry.SetMaintenanceDrain(operation.AgentId, drained: false);
                        ClearTransient(operation.OperationId);
                    }

                    return;
                }

                if (string.Equals(
                        activeHello.UpgradeOperationId,
                        operation.OperationId,
                        StringComparison.Ordinal) &&
                    string.Equals(
                        activeHello.UpgradeFailureCode,
                        "child_termination_failed",
                        StringComparison.Ordinal))
                {
                    if (operation.State == AgentUpgradeState.Draining)
                    {
                        await operations.FailBeforeHandoffAsync(
                            operation.OperationId,
                            operation.MaintenanceFence,
                            "child_termination_failed");
                        registry.SetMaintenanceDrain(operation.AgentId, drained: false);
                    }
                    else if (operation.State != AgentUpgradeState.Failed)
                    {
                        await operations.FailAsync(
                            operation.OperationId,
                            operation.MaintenanceFence,
                            "child_termination_failed");
                        registry.SetMaintenanceDrain(operation.AgentId, drained: true);
                    }
                    return;
                }

                if (operation.State == AgentUpgradeState.Failed)
                {
                    return;
                }

                if (operation.State == AgentUpgradeState.RollbackRequested)
                {
                    SignalRollbackOnce(operation, activeConnection);
                    return;
                }

                if (timeProvider.GetUtcNow() >= operation.Deadline)
                {
                    if (operation.State == AgentUpgradeState.Draining)
                    {
                        await operations.CancelOrRequestRollbackAsync(
                            ManagementRequestContext.System("agent-upgrade-deadline"),
                            operation.OperationId,
                            "upgrade_deadline_before_handoff");
                        registry.SetMaintenanceDrain(operation.AgentId, drained: false);
                    }
                    else
                    {
                        await operations.RequestAutomaticRollbackAsync(
                            operation.OperationId,
                            operation.MaintenanceFence,
                            "upgrade_deadline_exceeded");
                    }

                    return;
                }

                if (TryGetExactCandidate(operation, activeConnection, out _, out var candidateError))
                {
                    await AdvanceCandidateAsync(operation, activeConnection);
                    return;
                }

                if (candidateError is not null &&
                    activeConnection.ConnectionGeneration > operation.StartingConnectionGeneration &&
                    string.Equals(activeHello.UpgradeOperationId, operation.OperationId, StringComparison.Ordinal))
                {
                    await operations.RequestAutomaticRollbackAsync(
                        operation.OperationId,
                        operation.MaintenanceFence,
                        candidateError);
                    return;
                }

                string actualRid;
                try
                {
                    actualRid = ReadRid(
                        activeHello.Os?.Family ?? string.Empty,
                        activeHello.Os?.Arch ?? string.Empty);
                }
                catch (AgentUpgradeException exception)
                {
                    if (operation.State == AgentUpgradeState.Draining)
                    {
                        await operations.FailBeforeHandoffAsync(
                            operation.OperationId, operation.MaintenanceFence, exception.Code);
                        registry.SetMaintenanceDrain(operation.AgentId, drained: false);
                    }
                    else
                    {
                        await operations.FailAsync(
                            operation.OperationId, operation.MaintenanceFence, exception.Code);
                    }
                    return;
                }
                if (!string.Equals(actualRid, operation.Package.Rid, StringComparison.Ordinal))
                {
                    if (operation.State == AgentUpgradeState.Draining)
                    {
                        await operations.FailBeforeHandoffAsync(
                            operation.OperationId,
                            operation.MaintenanceFence,
                            "connected_agent_rid_changed");
                        registry.SetMaintenanceDrain(operation.AgentId, drained: false);
                    }
                    else
                    {
                        await operations.FailAsync(
                            operation.OperationId,
                            operation.MaintenanceFence,
                            "connected_agent_rid_changed");
                    }
                    return;
                }

                if (operation.State == AgentUpgradeState.Draining)
                {
                    var livePriorDigest = activeHello.AgentPackageSha256;
                    if (!IsSha256(livePriorDigest) ||
                        string.Equals(livePriorDigest, operation.Package.Sha256, StringComparison.Ordinal))
                    {
                        await operations.FailBeforeHandoffAsync(
                            operation.OperationId,
                            operation.MaintenanceFence,
                            IsSha256(livePriorDigest)
                                ? "target_became_active_while_draining"
                                : "live_prior_package_unknown");
                        registry.SetMaintenanceDrain(operation.AgentId, drained: false);
                        return;
                    }
                    if (!await operations.PrepareHandoffAsync(
                            operation.OperationId,
                            operation.MaintenanceFence,
                            livePriorDigest,
                            activeConnection.ConnectionGeneration))
                    {
                        return;
                    }

                    operation = await operations.FindAsync(operation.OperationId) ?? operation;
                }

                if (operation.State is AgentUpgradeState.HandoffReady or AgentUpgradeState.AwaitingHealth &&
                    operation.RestartAttempts >= MaximumRestartDispatches &&
                    (operation.NextRestartAt is null || operation.NextRestartAt <= timeProvider.GetUtcNow()))
                {
                    await operations.RequestAutomaticRollbackAsync(
                        operation.OperationId,
                        operation.MaintenanceFence,
                        "restart_delivery_exhausted");
                    return;
                }

                if (operation.State is AgentUpgradeState.HandoffReady or AgentUpgradeState.AwaitingHealth &&
                    await operations.RecordRestartDispatchAsync(
                        operation.OperationId,
                        operation.MaintenanceFence,
                        activeConnection.ConnectionGeneration,
                        RestartRetry))
                {
                    registry.TrySend(activeConnection, new ControllerMsg
                    {
                        Restart = new RestartAgent
                        {
                            Reason = $"agent-upgrade:{operation.OperationId}",
                        },
                    });
                }
            }
        }
        catch (AgentPackageException exception)
        {
            var operation = await operations.FindAsync(operationId);
            if (operation is not null && !operation.IsTerminal)
            {
                await operations.FailAsync(
                    operation.OperationId,
                    operation.MaintenanceFence,
                    exception.Code);
            }
        }
        finally
        {
            pass.Release();
        }
    }

    private async Task AdvanceCandidateAsync(
        AgentUpgradeOperation operation,
        AgentConnectionHandle connection)
    {
        RetainTransientGeneration(operation.OperationId, connection.ConnectionGeneration);
        if (operation.State == AgentUpgradeState.Finalizing)
        {
            if (operation.ObservedConnectionGeneration != connection.ConnectionGeneration)
            {
                if (!await operations.ObserveFinalizingSessionAsync(
                        operation.OperationId,
                        operation.MaintenanceFence,
                        connection.ConnectionGeneration))
                {
                    return;
                }
            }

            SendCommitRecorded(operation, connection);
            return;
        }

        if (operation.State == AgentUpgradeState.CommitPending)
        {
            if (operation.ObservedConnectionGeneration != connection.ConnectionGeneration)
            {
                await operations.ResetCommitForNewSessionAsync(
                    operation.OperationId,
                    operation.MaintenanceFence,
                    connection.ConnectionGeneration);
                candidateFirstSeen[(operation.OperationId, connection.ConnectionGeneration)] =
                    timeProvider.GetUtcNow();
                return;
            }

            SendCommitAcceptance(operation, connection);
            return;
        }

        if (operation.State != AgentUpgradeState.AwaitingHealth)
        {
            return;
        }

        var key = (operation.OperationId, connection.ConnectionGeneration);
        var now = timeProvider.GetUtcNow();
        if (!candidateFirstSeen.TryGetValue(key, out var firstSeen))
        {
            candidateFirstSeen[key] = now;
            await operations.ObserveCandidateAsync(
                operation.OperationId,
                operation.MaintenanceFence,
                connection.ConnectionGeneration);
            return;
        }

        if (now - firstSeen < CandidateProbation)
        {
            return;
        }

        if (lastHealthSignal.TryGetValue(key, out var last) && now - last < HealthRetry)
        {
            return;
        }

        if (registry.TrySend(connection, new ControllerMsg
        {
            UpgradeHealthAccepted = new UpgradeHealthAccepted
            {
                OperationId = operation.OperationId,
                PackageSha256 = operation.Package.Sha256,
                SessionId = connection.SessionId,
                ConnectionGeneration = checked((ulong)connection.ConnectionGeneration),
            },
        }))
        {
            lastHealthSignal[key] = now;
        }
    }

    private void SendCommitAcceptance(
        AgentUpgradeOperation operation,
        AgentConnectionHandle connection)
    {
        var key = (operation.OperationId, connection.ConnectionGeneration);
        var now = timeProvider.GetUtcNow();
        if (commitSignals.TryGetValue(key, out var last) && now - last < HealthRetry)
        {
            return;
        }

        if (registry.TrySend(connection, new ControllerMsg
        {
            UpgradeCommitAccepted = new UpgradeCommitAccepted
            {
                OperationId = operation.OperationId,
                PackageSha256 = operation.Package.Sha256,
                SessionId = connection.SessionId,
                ConnectionGeneration = checked((ulong)connection.ConnectionGeneration),
            },
        }))
        {
            commitSignals[key] = now;
        }
    }

    private void SendCommitRecorded(
        AgentUpgradeOperation operation,
        AgentConnectionHandle connection)
    {
        var key = (operation.OperationId, connection.ConnectionGeneration);
        var now = timeProvider.GetUtcNow();
        if (finalizationSignals.TryGetValue(key, out var last) && now - last < HealthRetry)
        {
            return;
        }

        if (registry.TrySend(connection, new ControllerMsg
        {
            UpgradeCommitRecorded = new UpgradeCommitRecorded
            {
                OperationId = operation.OperationId,
                PackageSha256 = operation.Package.Sha256,
                SessionId = connection.SessionId,
                ConnectionGeneration = checked((ulong)connection.ConnectionGeneration),
            },
        }))
        {
            finalizationSignals[key] = now;
        }
    }

    private void SignalRollbackOnce(
        AgentUpgradeOperation operation,
        AgentConnectionHandle connection)
    {
        RetainTransientGeneration(operation.OperationId, connection.ConnectionGeneration);
        var key = (operation.OperationId, connection.ConnectionGeneration);
        var now = timeProvider.GetUtcNow();
        if (rollbackSignals.TryGetValue(key, out var last) && now - last < HealthRetry)
        {
            return;
        }

        if (registry.TrySend(connection, new ControllerMsg
        {
            Restart = new RestartAgent
            {
                Reason = $"agent-upgrade-rollback:{operation.OperationId}",
            },
        }))
        {
            rollbackSignals[key] = now;
        }
    }

    private bool TryGetExactCandidate(
        AgentUpgradeOperation operation,
        AgentConnectionHandle expectedConnection,
        out Hello? hello,
        out string? error)
    {
        hello = null;
        error = null;
        if (!registry.TryGetMaintenanceConnection(
                operation.AgentId,
                out var current,
                out var observed,
                out _) ||
            !registry.IsCurrent(expectedConnection) ||
            current!.ConnectionGeneration != expectedConnection.ConnectionGeneration ||
            !string.Equals(current.SessionId, expectedConnection.SessionId, StringComparison.Ordinal) ||
            current.ConnectionGeneration <= operation.StartingConnectionGeneration ||
            !string.Equals(observed!.UpgradeOperationId, operation.OperationId, StringComparison.Ordinal))
        {
            return false;
        }

        hello = observed;
        if (!string.Equals(observed.AgentPackageSha256, operation.Package.Sha256, StringComparison.Ordinal))
        {
            error = "unexpected_post_upgrade_digest";
            return false;
        }

        string actualRid;
        try
        {
            actualRid = AgentPackageRids.FromPlatform(
                observed.Os?.Family ?? string.Empty,
                observed.Os?.Arch ?? string.Empty);
        }
        catch (AgentPackageException)
        {
            error = "candidate_rid_invalid";
            return false;
        }

        if (!string.Equals(actualRid, operation.Package.Rid, StringComparison.Ordinal))
        {
            error = "candidate_rid_mismatch";
            return false;
        }

        return true;
    }

    private static bool IsExactPrior(
        AgentUpgradeOperation operation,
        AgentConnectionHandle connection,
        Hello hello) =>
        connection.ConnectionGeneration > operation.StartingConnectionGeneration &&
        !string.IsNullOrEmpty(operation.PriorPackageSha256) &&
        string.Equals(hello.UpgradeOperationId, operation.OperationId, StringComparison.Ordinal) &&
        string.Equals(hello.AgentPackageSha256, operation.PriorPackageSha256, StringComparison.Ordinal);

    private void ClearTransient(string operationId)
    {
        foreach (var key in candidateFirstSeen.Keys.Where(key => key.OperationId == operationId).ToArray())
        {
            candidateFirstSeen.Remove(key);
            lastHealthSignal.Remove(key);
            commitSignals.Remove(key);
            finalizationSignals.Remove(key);
            rollbackSignals.Remove(key);
        }
        foreach (var key in commitSignals.Keys.Where(key => key.OperationId == operationId).ToArray())
        {
            commitSignals.Remove(key);
        }
        foreach (var key in rollbackSignals.Keys.Where(key => key.OperationId == operationId).ToArray())
        {
            rollbackSignals.Remove(key);
        }
        foreach (var key in finalizationSignals.Keys.Where(key => key.OperationId == operationId).ToArray())
        {
            finalizationSignals.Remove(key);
        }
    }

    private void RetainTransientGeneration(string operationId, long generation)
    {
        RemoveOtherGenerations(candidateFirstSeen);
        RemoveOtherGenerations(lastHealthSignal);
        RemoveOtherGenerations(commitSignals);
        RemoveOtherGenerations(finalizationSignals);
        RemoveOtherGenerations(rollbackSignals);

        void RemoveOtherGenerations(Dictionary<(string OperationId, long Generation), DateTimeOffset> values)
        {
            foreach (var key in values.Keys.Where(key =>
                         key.OperationId == operationId && key.Generation != generation).ToArray())
            {
                values.Remove(key);
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), timeProvider);
        try
        {
            do
            {
                var pending = (await operations.ListCoordinatedAsync())
                    .OrderBy(operation => operation.CreatedAt)
                    .ThenBy(operation => operation.OperationId, StringComparer.Ordinal)
                    .ToArray();
                foreach (var operation in pending)
                {
                    try
                    {
                        await TryAdvanceAsync(operation.OperationId, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception exception)
                    {
                        log.LogError(
                            exception,
                            "Agent upgrade coordinator pass failed for operation {OperationId}",
                            operation.OperationId);
                    }
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private static string ReadRid(string os, string arch)
    {
        try
        {
            return AgentPackageRids.FromPlatform(os, arch);
        }
        catch (AgentPackageException exception)
        {
            throw new AgentUpgradeException(exception.Code, exception.Message);
        }
    }

    private static bool IsSha256(string? value) => value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string ValidateRequestId(string requestId)
    {
        requestId = requestId?.Trim() ?? string.Empty;
        if (requestId.Length is < 1 or > 256 || requestId.Any(character =>
                character is '\r' or '\n' or '\0'))
        {
            throw new AgentUpgradeException(
                "idempotency_key_invalid",
                "Idempotency-Key must contain 1-256 safe characters.",
                StatusCodes.Status400BadRequest);
        }

        return requestId;
    }

    private static string ValidateReason(string reason)
    {
        reason = reason?.Trim() ?? string.Empty;
        if (reason.Length is < 1 or > 512 || reason.Any(character =>
                character is '\r' or '\n' or '\0'))
        {
            throw new AgentUpgradeException(
                "agent_upgrade_reason_invalid",
                "Upgrade reason must contain 1-512 safe characters.",
                StatusCodes.Status422UnprocessableEntity);
        }

        return reason;
    }

    private static void ValidateId(string value, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 ||
            value.Any(character => character is '\r' or '\n' or '\0'))
        {
            throw new AgentUpgradeException(
                "identifier_invalid",
                $"{parameter} must contain 1-256 safe characters.",
                StatusCodes.Status400BadRequest);
        }
    }
}
