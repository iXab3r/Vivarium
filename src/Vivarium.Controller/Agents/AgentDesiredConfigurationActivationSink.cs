using Vivarium.Controller.Configuration.Agents;

namespace Vivarium.Controller.Agents;

internal sealed class AgentDesiredConfigurationActivationSink(AgentRegistry registry)
    : IAgentDesiredConfigurationActivationSink
{
    public void OnApplied(AgentDesiredConfigurationActivation activation)
    {
        ArgumentNullException.ThrowIfNull(activation);
        registry.SetEnabled(activation.AgentId, activation.Enabled);
    }
}
