using Vivarium.Contracts.V1;
using Vivarium.Controller.Builds;
using Vivarium.Controller.Security;

namespace Vivarium.Controller.Agents;

/// <summary>TeamCity-shaped administration used by the panel and later by ControlPlane.</summary>
public sealed class AgentAdministration
{
    private readonly AgentRegistry registry;
    private readonly AgentStore store;
    private readonly BuildStore builds;
    private readonly TokenStore tokens;
    private readonly AgentLifecycleCoordinator lifecycle;

    public AgentAdministration(
        AgentRegistry registry,
        AgentStore store,
        BuildStore builds,
        TokenStore tokens,
        AgentLifecycleCoordinator lifecycle)
    {
        this.registry = registry;
        this.store = store;
        this.builds = builds;
        this.tokens = tokens;
        this.lifecycle = lifecycle;
    }

    public async Task<IReadOnlyList<AgentSnapshot>> ListAsync()
    {
        var snapshots = await registry.GetSnapshotsAsync();
        var activeByAgent = (await builds.ListAssignedActiveAsync())
            .ToDictionary(build => build.AgentId!);
        return snapshots.Select(agent => activeByAgent.TryGetValue(agent.AgentId, out var build)
            ? agent with
            {
                Activity = AgentActivity.Building,
                CurrentBuildId = build.BuildId,
            }
            : agent).ToArray();
    }

    public async Task AuthorizeAsync(string agentId)
    {
        var token = await tokens.AuthorizeAgentAsync(agentId);
        registry.SetAuthorized(agentId, true);
        if (token != null)
        {
            registry.TrySend(agentId, new ControllerMsg
            {
                Authorized = new AuthorizationGranted { AuthToken = token },
            });
        }
    }

    public async Task UnauthorizeAsync(string agentId)
    {
        await store.SetAuthorizedAsync(agentId, false);
        registry.SetAuthorized(agentId, false);
    }

    public async Task SetEnabledAsync(string agentId, bool enabled)
    {
        await store.SetEnabledAsync(agentId, enabled);
        registry.SetEnabled(agentId, enabled);
    }

    public async Task RenameAsync(string agentId, string name)
    {
        await store.RenameAsync(agentId, name);
        registry.NotifyChanged();
    }

    public Task SetCustomParameterAsync(string agentId, string key, string value)
    {
        var normalized = AgentParameterMaps.ValidateCustom(key, value);
        return MutateCustomParametersAsync(
            agentId,
            () => store.SetCustomParameterAsync(agentId, normalized.Key, normalized.Value));
    }

    public Task DeleteCustomParameterAsync(string agentId, string key)
    {
        var normalizedKey = AgentParameterMaps.ValidateCustomKey(key);
        return MutateCustomParametersAsync(
            agentId,
            () => store.DeleteCustomParameterAsync(agentId, normalizedKey));
    }

    public async Task DeleteAsync(string agentId)
    {
        await using var lease = await lifecycle.AcquireAsync(agentId);
        var durableBuild = (await builds.ListAssignedActiveAsync())
            .FirstOrDefault(build => build.AgentId == agentId);
        if (durableBuild != null)
        {
            throw new InvalidOperationException(
                $"agent '{agentId}' is building '{durableBuild.BuildId}'; stop the build before deleting it");
        }

        var live = registry.Get(agentId);
        if (live != null)
        {
            lock (live.Gate)
            {
                if (live.CurrentBuildId != null)
                {
                    throw new InvalidOperationException(
                        $"agent '{agentId}' is building '{live.CurrentBuildId}'; stop the build before deleting it");
                }

                // Fence scheduling between the idle check and removal from persistent storage.
                live.Enabled = false;
            }
        }

        await store.DeleteAsync(agentId);
        registry.Remove(agentId);
    }

    public Task<string> CreateEnrollTokenAsync() => tokens.CreateEnrollTokenAsync();

    private async Task MutateCustomParametersAsync(string agentId, Func<Task> mutation)
    {
        await using var lease = await lifecycle.AcquireAsync(agentId);
        var live = registry.Get(agentId);
        if (live != null)
        {
            lock (live.Gate)
            {
                if (live.CurrentBuildId != null)
                {
                    throw new InvalidOperationException(
                        $"agent '{agentId}' is building '{live.CurrentBuildId}'; " +
                        "stop the build before editing its custom parameters");
                }

                // Fence scheduler snapshots without changing the operator-owned enabled axis.
                live.ParametersChanging = true;
            }
        }

        var changed = false;
        try
        {
            var durableBuild = (await builds.ListAssignedActiveAsync())
                .FirstOrDefault(build => build.AgentId == agentId);
            if (durableBuild != null)
            {
                throw new InvalidOperationException(
                    $"agent '{agentId}' is building '{durableBuild.BuildId}'; " +
                    "stop the build before editing its custom parameters");
            }

            await mutation();
            changed = true;
        }
        finally
        {
            if (live != null)
            {
                lock (live.Gate)
                {
                    if (changed)
                    {
                        live.ParameterGeneration++;
                    }

                    live.ParametersChanging = false;
                }
            }

            registry.NotifyChanged();
        }
    }
}
