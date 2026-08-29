using Vivarium.Contracts.V1;
using Vivarium.Controller.Auditing;
using Vivarium.Controller.Builds;
using Vivarium.Controller.Configuration.Agents;
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
    private readonly TimeProvider timeProvider;
    private readonly ManagementCommandAuthorizer? authorization;
    private readonly IAgentDesiredConfigurationService? desiredConfiguration;

    public AgentAdministration(
        AgentRegistry registry,
        AgentStore store,
        BuildStore builds,
        TokenStore tokens,
        AgentLifecycleCoordinator lifecycle,
        TimeProvider? timeProvider = null,
        ManagementCommandAuthorizer? authorization = null,
        IAgentDesiredConfigurationService? desiredConfiguration = null)
    {
        this.registry = registry;
        this.store = store;
        this.builds = builds;
        this.tokens = tokens;
        this.lifecycle = lifecycle;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.authorization = authorization;
        this.desiredConfiguration = desiredConfiguration;
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

    public async Task AuthorizeAsync(ManagementRequestContext context, string agentId)
    {
        await DemandAsync(
            context, ManagementPermission.AgentAuthorize, "agent.authorize", agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        var grant = await tokens.AuthorizeAgentWithGenerationAsync(agentId, Audit(
            context, "agent.authorize", agentId));
        if (grant.AuthToken != null)
        {
            // An enrollment-proof session is never promoted in place. It may receive the bearer,
            // then must reconnect and prove possession before the registry admits new work.
            registry.SetAuthorized(agentId, false);
            registry.TrySend(agentId, new ControllerMsg
            {
                Authorized = new AuthorizationGranted
                {
                    AuthToken = grant.AuthToken,
                    CredentialGeneration = checked((ulong)grant.CredentialGeneration),
                },
            });
        }
        else
        {
            registry.SetAuthorized(agentId, true);
        }
    }

    public async Task UnauthorizeAsync(ManagementRequestContext context, string agentId)
    {
        await DemandAsync(
            context, ManagementPermission.AgentManage, "agent.unauthorize", agentId);
        await store.SetAuthorizedAsync(agentId, false, Audit(
            context, "agent.unauthorize", agentId));
        registry.SetAuthorized(agentId, false);
    }

    public async Task SetEnabledAsync(
        ManagementRequestContext context,
        string agentId,
        bool enabled)
    {
        var action = enabled ? "agent.enable" : "agent.disable";
        await DemandAsync(context, ManagementPermission.AgentManage, action, agentId);
        var configuration = desiredConfiguration ?? throw new InvalidOperationException(
            "Agent desired configuration is not configured");
        var snapshot = await configuration.GetAsync(agentId)
            ?? throw new AgentDesiredConfigurationNotFoundException(agentId);
        var commandContext = context.RequestId is null
            ? context.WithRequestId(ManagementIdentifiers.NewId())
            : context;
        await configuration.SetEnabledAsync(
            commandContext,
            agentId,
            enabled,
            snapshot.AuthoritativeRevision);
    }

    public async Task RenameAsync(ManagementRequestContext context, string agentId, string name)
    {
        await DemandAsync(context, ManagementPermission.AgentManage, "agent.rename", agentId);
        await store.RenameAsync(agentId, name, Audit(context, "agent.rename", agentId));
        registry.NotifyChanged();
    }

    public async Task SetCustomParameterAsync(
        ManagementRequestContext context,
        string agentId,
        string key,
        string value)
    {
        await DemandAsync(
            context, ManagementPermission.AgentManage, "agent.custom-parameter.set", agentId);
        var normalized = AgentParameterMaps.ValidateCustom(key, value);
        await MutateCustomParametersAsync(
            agentId,
            () => store.SetCustomParameterAsync(
                agentId,
                normalized.Key,
                normalized.Value,
                Audit(context, "agent.custom-parameter.set", agentId, normalized.Key)));
    }

    public async Task DeleteCustomParameterAsync(
        ManagementRequestContext context,
        string agentId,
        string key)
    {
        await DemandAsync(
            context, ManagementPermission.AgentManage, "agent.custom-parameter.delete", agentId);
        var normalizedKey = AgentParameterMaps.ValidateCustomKey(key);
        await MutateCustomParametersAsync(
            agentId,
            () => store.DeleteCustomParameterAsync(
                agentId,
                normalizedKey,
                Audit(context, "agent.custom-parameter.delete", agentId, normalizedKey)));
    }

    public async Task DeleteAsync(ManagementRequestContext context, string agentId)
    {
        await DemandAsync(context, ManagementPermission.AgentManage, "agent.delete", agentId);
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

        await store.DeleteAsync(agentId, Audit(context, "agent.delete", agentId));
        registry.Remove(agentId);
    }

    public async Task<string> CreateEnrollTokenAsync(ManagementRequestContext context)
    {
        var targetId = ManagementIdentifiers.NewId();
        await DemandAsync(
            context,
            ManagementPermission.EnrollmentTokenCreate,
            "enrollment-token.create",
            targetId,
            "enrollment-token");
        return await tokens.CreateEnrollTokenAsync(Audit(
            context,
            "enrollment-token.create",
            targetId,
            targetType: "enrollment-token"));
    }

    internal Task AuthorizeFromControllerAsync(string agentId) => AuthorizeAsync(
        ManagementRequestContext.System("controller-agent-lifecycle"), agentId);

    internal Task<string> CreateEnrollTokenFromControllerAsync() => CreateEnrollTokenAsync(
        ManagementRequestContext.System("controller-agent-lifecycle"));

    private AuditEventDraft Audit(
        ManagementRequestContext context,
        string action,
        string targetId,
        string? detailKey = null,
        string targetType = "agent") =>
        AuditEventDraft.Create(
            context,
            timeProvider.GetUtcNow(),
            action,
            targetType,
            targetId,
            details: detailKey is null
                ? null
                : new Dictionary<string, string> { ["parameter_key"] = detailKey });

    private Task DemandAsync(
        ManagementRequestContext context,
        ManagementPermission permission,
        string action,
        string targetId,
        string targetType = "agent") =>
        (authorization ?? throw new InvalidOperationException(
            "application command authorization is not configured"))
        .DemandAsync(context, permission, action, targetType, targetId);

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
