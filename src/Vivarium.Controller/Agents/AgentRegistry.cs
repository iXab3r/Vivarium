using System.Collections.Concurrent;
using System.Threading.Channels;
using Vivarium.Contracts.V1;

namespace Vivarium.Controller.Agents;

public enum AgentAuth
{
    Unauthorized,
    Authorized,
}

/// <summary>A known agent and, while connected, its outbound message channel.</summary>
public sealed class ConnectedAgent
{
    public required string AgentId { get; init; }
    public required Hello Hello { get; set; }
    public AgentAuth Auth { get; set; }
    public bool Connected { get; set; }
    public string SessionId { get; set; } = string.Empty;
    public DateTimeOffset LastHeartbeat { get; set; }

    public Channel<ControllerMsg> Outbox { get; private set; } = Channel.CreateUnbounded<ControllerMsg>();

    /// <summary>A reconnect replaces the outbox so messages never land on a dead stream's channel.</summary>
    public Channel<ControllerMsg> ResetOutbox() => Outbox = Channel.CreateUnbounded<ControllerMsg>();
}

public sealed class AgentRegistry
{
    private readonly ConcurrentDictionary<string, ConnectedAgent> agents = new();

    public IReadOnlyList<ConnectedAgent> All => agents.Values.ToArray();

    public ConnectedAgent? Get(string agentId) => agents.TryGetValue(agentId, out var agent) ? agent : null;

    public ConnectedAgent Register(Hello hello, AgentAuth auth)
    {
        var agent = agents.AddOrUpdate(
            hello.AgentId,
            _ => new ConnectedAgent { AgentId = hello.AgentId, Hello = hello, Auth = auth },
            (_, existing) =>
            {
                existing.Hello = hello;
                if (auth == AgentAuth.Authorized)
                {
                    existing.Auth = AgentAuth.Authorized;
                }

                return existing;
            });

        agent.SessionId = hello.SessionId;
        agent.ResetOutbox();
        return agent;
    }

    public bool TrySend(string agentId, ControllerMsg msg) =>
        Get(agentId)?.Outbox.Writer.TryWrite(msg) ?? false;
}
