using System.Text;
using Vivarium.Controller.Agents;
using Vivarium.Controller.Configuration.Git;
using Vivarium.Controller.Configuration.Reconciliation;
using Vivarium.Controller.Security;

namespace Vivarium.Controller.Configuration.Agents;

/// <summary>
/// Converges externally-authored managed-local Git commits into the durable and live Agent
/// projections without crossing an Agent lifecycle mutation boundary.
/// </summary>
public sealed class AgentConfigurationReconciliationMonitor : BackgroundService
{
    private const int MaxAgentSetAcquireAttempts = 8;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly IConfigurationRepository repository;
    private readonly ConfigurationReconciler reconciler;
    private readonly AgentStore agents;
    private readonly AgentRegistry registry;
    private readonly AgentLifecycleCoordinator lifecycle;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<AgentConfigurationReconciliationMonitor> log;
    private readonly object warningGate = new();
    private string? lastWarningCode;

    public AgentConfigurationReconciliationMonitor(
        IConfigurationRepository repository,
        ConfigurationReconciler reconciler,
        AgentStore agents,
        AgentRegistry registry,
        AgentLifecycleCoordinator lifecycle,
        TimeProvider timeProvider,
        ILogger<AgentConfigurationReconciliationMonitor> log)
    {
        this.repository = repository;
        this.reconciler = reconciler;
        this.agents = agents;
        this.registry = registry;
        this.lifecycle = lifecycle;
        this.timeProvider = timeProvider;
        this.log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await ReconcileOnceAsync(stoppingToken);
        using var timer = new PeriodicTimer(PollInterval, timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await ReconcileOnceAsync(stoppingToken);
        }
    }

    internal async Task<ConfigurationReconciliationResult?> ReconcileOnceAsync(
        CancellationToken cancellationToken = default)
    {
        StableAgentLeaseSet? leaseSet;
        try
        {
            leaseSet = await TryAcquireStableAgentSetAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            LogWarningOnce("configuration_agent_set_read_failed");
            return null;
        }

        if (leaseSet is null)
        {
            LogWarningOnce("configuration_agent_set_unstable");
            return null;
        }

        await using (leaseSet)
        {
            ConfigurationReconciliationResult? result = null;
            try
            {
                result = await reconciler.ReconcileAuthoritativeHeadAsync(
                    ManagementRequestContext.System(
                        "agent-configuration-reconciliation-monitor"),
                    AgentDesiredConfigurationService.MaterializationScope,
                    repository,
                    cancellationToken: cancellationToken);

                if (result.HeadConvergence?.State == ConfigurationHeadConvergenceState.Degraded)
                {
                    LogWarningOnce(SafeCode(
                        result.HeadConvergence.Diagnostic?.Code,
                        "configuration_head_unstable"));
                }
                else if (result.Outcome is ConfigurationReconciliationOutcome.Invalid or
                    ConfigurationReconciliationOutcome.Blocked)
                {
                    LogWarningOnce(SafeCode(
                        result.Attempt.Diagnostics.FirstOrDefault()?.Code,
                        result.Outcome == ConfigurationReconciliationOutcome.Invalid
                            ? "configuration_invalid"
                            : "configuration_projection_blocked"));
                }
                else
                {
                    ClearWarning();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (ConfigurationRepositoryException exception)
            {
                LogWarningOnce(SafeCode(
                    exception.Code,
                    "configuration_repository_failed"));
            }
            catch
            {
                LogWarningOnce("configuration_reconciliation_failed");
            }

            try
            {
                await RefreshLiveProjectionAsync(leaseSet.AgentIds, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                LogWarningOnce("configuration_live_projection_refresh_failed");
            }

            return result;
        }
    }

    private async Task<StableAgentLeaseSet?> TryAcquireStableAgentSetAsync(
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxAgentSetAcquireAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var expectedAgentIds = await ReadAgentIdsAsync();
            var leases = new List<IAsyncDisposable>(expectedAgentIds.Length);
            try
            {
                foreach (var agentId in expectedAgentIds)
                {
                    leases.Add(await lifecycle.AcquireAsync(agentId, cancellationToken));
                }

                var observedAgentIds = await ReadAgentIdsAsync();
                if (expectedAgentIds.SequenceEqual(observedAgentIds, StringComparer.Ordinal))
                {
                    return new StableAgentLeaseSet(expectedAgentIds, leases);
                }
            }
            catch
            {
                await ReleaseAsync(leases);
                throw;
            }

            await ReleaseAsync(leases);
        }

        return null;
    }

    private async Task<string[]> ReadAgentIdsAsync() =>
        (await agents.ListAsync())
        .Select(agent => agent.AgentId)
        .Order(StringComparer.Ordinal)
        .ToArray();

    private async Task RefreshLiveProjectionAsync(
        IReadOnlyList<string> agentIds,
        CancellationToken cancellationToken)
    {
        foreach (var agentId in agentIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (registry.Get(agentId) is null)
            {
                continue;
            }

            var stored = await agents.GetAsync(agentId);
            if (stored is not null)
            {
                registry.SetEnabled(agentId, stored.Enabled);
            }
        }
    }

    private void LogWarningOnce(string code)
    {
        lock (warningGate)
        {
            if (string.Equals(lastWarningCode, code, StringComparison.Ordinal))
            {
                return;
            }

            lastWarningCode = code;
        }

        log.LogWarning(
            "Agent configuration reconciliation retained the last-known-good projection; code {ErrorCode}",
            code);
    }

    private void ClearWarning()
    {
        lock (warningGate)
        {
            lastWarningCode = null;
        }
    }

    private static string SafeCode(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var result = new StringBuilder(Math.Min(value.Length, 128));
        foreach (var character in value.Take(128))
        {
            if (character is >= 'a' and <= 'z' || char.IsAsciiDigit(character))
            {
                result.Append(character);
            }
            else if (character is >= 'A' and <= 'Z')
            {
                result.Append(char.ToLowerInvariant(character));
            }
            else
            {
                result.Append('_');
            }
        }

        return result.Length == 0 ? fallback : result.ToString();
    }

    private static async ValueTask ReleaseAsync(List<IAsyncDisposable> leases)
    {
        for (var index = leases.Count - 1; index >= 0; index--)
        {
            await leases[index].DisposeAsync();
        }
    }

    private sealed class StableAgentLeaseSet(
        string[] agentIds,
        List<IAsyncDisposable> leases) : IAsyncDisposable
    {
        public IReadOnlyList<string> AgentIds { get; } = agentIds;

        public ValueTask DisposeAsync() => ReleaseAsync(leases);
    }
}
