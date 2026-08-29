namespace Vivarium.Controller;

public sealed class ControllerOptions
{
    public static readonly TimeSpan DefaultBuildQueueWaitTimeout = TimeSpan.FromMinutes(30);

    public required string DataDir { get; init; }

    /// <summary>Listen address; loopback in tests, Any for a real deployment.</summary>
    public string Host { get; init; } = "0.0.0.0";

    /// <summary>0 = dynamic port (tests).</summary>
    public int Port { get; init; } = 8443;

    /// <summary>How long an agent may be silent before its session is replaced.</summary>
    public TimeSpan AgentHeartbeatTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>How long an assigned build waits for its owning agent to reconnect.</summary>
    public TimeSpan AgentReconnectGrace { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>How long a build may wait in the queue before any payload starts.</summary>
    public TimeSpan BuildQueueWaitTimeout { get; init; } = DefaultBuildQueueWaitTimeout;

    /// <summary>
    /// Optional release-bundled schema-v1 Agent package catalog imported before the server listens.
    /// </summary>
    public string? AgentPackageCatalogPath { get; init; }

    /// <summary>
    /// Enables the hidden package publication surface used by deployment tests and local Agent
    /// development. Production upgrades always use packages bundled with this Server release.
    /// </summary>
    public bool EnableDevelopmentAgentPackageApi { get; init; }

    /// <summary>Clock used by heartbeat and reconnect-lease logic; replaceable for tier-1 tests.</summary>
    public TimeProvider TimeProvider { get; init; } = System.TimeProvider.System;
}
