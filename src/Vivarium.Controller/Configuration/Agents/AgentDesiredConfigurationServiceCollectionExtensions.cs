using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Vivarium.Controller.Configuration.Agents;

public static class AgentDesiredConfigurationServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Agent-specific desired-configuration application service. Root composition must
    /// also register the shared repository/reconciler and an activation sink for the live registry.
    /// </summary>
    public static IServiceCollection AddAgentDesiredConfiguration(
        this IServiceCollection services)
    {
        services.TryAddSingleton<AgentDesiredConfigurationService>();
        services.TryAddSingleton<IAgentDesiredConfigurationService>(services =>
            services.GetRequiredService<AgentDesiredConfigurationService>());
        return services;
    }
}
