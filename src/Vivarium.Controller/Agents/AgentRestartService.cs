using System.Security.Cryptography;
using System.Text;
using Vivarium.Contracts.V1;
using Vivarium.Controller.Auditing;
using Vivarium.Controller.Agents.Compatibility;
using Vivarium.Controller.Security;

namespace Vivarium.Controller.Agents;

public sealed class AgentRestartUnavailableException(string reason) : Exception(reason)
{
    public string Reason { get; } = reason;
}

/// <summary>
/// Durable remote restart coordinator. A dispatch or ACK is not success; completion requires both a
/// newer authenticated connection generation and a different Bootstrap child process (D31).
/// </summary>
public sealed class AgentRestartService(
    AgentRestartStore store,
    AgentRegistry registry,
    ManagementCommandAuthorizer authorization,
    TimeProvider timeProvider,
    ILogger<AgentRestartService> log) : BackgroundService
{
    public async Task<AgentRestartOperation> CreateAsync(
        ManagementRequestContext context,
        string agentId,
        AgentRestartMode mode,
        string reason,
        TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(context.RequestId))
        {
            throw new ArgumentException("Agent restart requires an idempotency key", nameof(context));
        }
        if (mode is not (AgentRestartMode.AfterCurrentWork or
            AgentRestartMode.CancelThenRestart or AgentRestartMode.Force))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 512)
        {
            throw new ArgumentException("restart reason must contain 1-512 characters", nameof(reason));
        }
        if (timeout < TimeSpan.FromSeconds(5) || timeout > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        await authorization.DemandAsync(
            context,
            ManagementPermission.AgentManage,
            "agent.restart",
            "agent",
            agentId);

        var canonical = $"{agentId}\n{AgentRestartStore.ModeValue(mode)}\n{reason.Trim()}\n{timeout.Ticks}";
        var requestHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        var replay = await store.FindByRequestAsync(context);
        if (replay is not null)
        {
            if (!string.Equals(replay.RequestHash, requestHash, StringComparison.Ordinal))
            {
                throw new AgentRestartRequestConflictException();
            }
            return replay.Operation;
        }

        if (!registry.TryGetControlConnection(agentId, out var connection, out var unavailable) ||
            connection is null)
        {
            throw new AgentRestartUnavailableException(unavailable ?? "agent_unavailable");
        }
        if (!registry.SupportsCapability(
                connection,
                AgentProtocolCompatibility.BootstrapSupervisorCapabilityId,
                contractMajor: 1))
        {
            throw new AgentRestartUnavailableException("agent_restart_supervision_unavailable");
        }
        if (!registry.TryGetProcessInstanceId(connection, out var processInstanceId) ||
            processInstanceId is null)
        {
            throw new AgentRestartUnavailableException("agent_restart_process_identity_unavailable");
        }

        // Drain under the same runtime gate used by Build admission before persisting the restart.
        // This prevents a terminal result from exposing an idle scheduling window before Agent exit.
        registry.SetMaintenanceDrain(agentId, drained: true);
        AgentRestartOperation operation;
        try
        {
            var now = timeProvider.GetUtcNow();
            operation = await store.CreateAsync(
                agentId,
                mode,
                reason.Trim(),
                connection.ConnectionGeneration,
                processInstanceId,
                requestHash,
                context,
                now + timeout,
                now,
                AuditEventDraft.Create(
                    context,
                    now,
                    "agent.restart",
                    "agent",
                    agentId,
                    details: new Dictionary<string, string>
                    {
                        ["mode"] = AgentRestartStore.ModeValue(mode),
                    }));
        }
        catch
        {
            if (!await store.HasActiveAsync(agentId))
            {
                registry.SetMaintenanceDrain(agentId, drained: false);
            }
            throw;
        }
        Dispatch(operation);
        return operation;
    }

    public Task<AgentRestartOperation?> FindAsync(string operationId) => store.FindAsync(operationId);

    public Task<bool> HasActiveAsync(string agentId) => store.HasActiveAsync(agentId);

    public async Task OnAcknowledgedAsync(
        AgentRestartAcknowledged acknowledged,
        AgentConnectionHandle connection)
    {
        if (!registry.IsCurrent(connection) ||
            !string.Equals(acknowledged.SessionId, connection.SessionId, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(acknowledged.OperationId))
        {
            return;
        }

        var operation = await store.FindAsync(acknowledged.OperationId);
        if (operation is null || operation.AgentId != connection.AgentId ||
            operation.Mode != acknowledged.Mode ||
            operation.RequestedConnectionGeneration != connection.ConnectionGeneration)
        {
            return;
        }
        await store.TryAcknowledgeAsync(
            operation.OperationId,
            connection.AgentId,
            connection.ConnectionGeneration,
            timeProvider.GetUtcNow());
    }

    public Task<bool> OnAgentConnectedAsync(AgentConnectionHandle connection)
    {
        return registry.TryGetProcessInstanceId(connection, out var processInstanceId) &&
            processInstanceId is not null
                ? store.TryCompleteAsync(
                    connection.AgentId,
                    connection.ConnectionGeneration,
                    processInstanceId,
                    timeProvider.GetUtcNow())
                : Task.FromResult(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var now = timeProvider.GetUtcNow();
            foreach (var agentId in await store.FailDueAsync(now))
            {
                registry.Quarantine(agentId, "agent_restart_deadline_expired");
                await registry.PersistOperationalStateAsync(agentId);
                log.LogWarning("Agent {AgentId} did not confirm restart before its deadline", agentId);
            }

            foreach (var operation in await store.ListActiveAsync())
            {
                Dispatch(operation);
            }
        }
    }

    private void Dispatch(AgentRestartOperation operation)
    {
        if (operation.State != AgentRestartState.Requested ||
            !registry.TryGetControlConnection(operation.AgentId, out var connection, out _) ||
            connection is null ||
            connection.ConnectionGeneration != operation.RequestedConnectionGeneration)
        {
            return;
        }

        registry.TrySend(connection, new ControllerMsg
        {
            Restart = new RestartAgent
            {
                Reason = operation.Reason,
                OperationId = operation.OperationId,
                Mode = operation.Mode,
                DeadlineUnixMs = operation.Deadline.ToUnixTimeMilliseconds(),
            },
        });
    }
}
