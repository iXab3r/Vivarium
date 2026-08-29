namespace Vivarium.Agent;

using Vivarium.Agent.Facts;

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

    /// <summary>Exact running package identity when launched from a verified controller package.</summary>
    public string? AgentPackageVersion { get; init; }

    /// <summary>Lowercase SHA-256 of the running package; empty until packaging supplies it.</summary>
    public string? AgentPackageSha256 { get; init; }

    /// <summary>Durable controller operation that activated this package, when any.</summary>
    public string? UpgradeOperationId { get; init; }

    /// <summary>Non-secret launcher health marker written after an authenticated Welcome.</summary>
    public string? UpgradeHealthMarkerPath { get; init; }

    /// <summary>Bounded launcher failure evidence reported with the retained operation identity.</summary>
    public string? UpgradeFailureCode { get; init; }

    /// <summary>Launcher-owned liveness lease; both fields are required when supervised by bootstrap.</summary>
    public string? BootstrapLeasePath { get; init; }
    public string? BootstrapLeaseId { get; init; }

    /// <summary>Replaceable only for deterministic platform-collector tests.</summary>
    public IPlatformFactsCollector? PlatformFactsCollector { get; init; }
}
