namespace Vivarium.Agent;

public sealed class AgentOptions
{
    public required string ControllerUrl { get; init; }

    /// <summary>SHA-256 of the controller's certificate, hex — the only trust anchor (D4).</summary>
    public required string CertFingerprintSha256 { get; init; }

    public string? EnrollToken { get; init; }

    /// <summary>Everything the agent owns lives here: identity, token, build workdirs.</summary>
    public required string DataDir { get; init; }

    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan ReconnectDelay { get; init; } = TimeSpan.FromSeconds(3);
}
