namespace Vivarium.Controller.Agents;

/// <summary>Fresh-install PG Agent identity/runtime authority; desired fields are Git projections.</summary>
public sealed class AgentRegistration
{
    public required string AgentId { get; set; }
    public int TeamCityId { get; set; }
    public required string ReportedName { get; set; }
    public bool Authorized { get; set; }
    public bool MaintenanceDrain { get; set; }
    public bool DesiredEnabled { get; set; }
    public string? DesiredRevision { get; set; }
    public string? DesiredName { get; set; }
    public string CustomParametersJson { get; set; } = "{}";
    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public long CredentialGeneration { get; set; }
    public long ConnectionGeneration { get; set; }
    public string? CurrentSessionId { get; set; }
    public long SessionCredentialGeneration { get; set; }
    public bool SessionAuthenticated { get; set; }
    public string? ProcessInstanceId { get; set; }
    public string? CurrentWorkloadId { get; set; }
    public long WorkloadFence { get; set; }
    public decimal LastHeartbeatSequence { get; set; }
    public DateTimeOffset? LastHeartbeatAt { get; set; }
    public bool Reconciled { get; set; }
    public bool Quarantined { get; set; }
    public string Health { get; set; } = "UNKNOWN";
    public string HealthReason { get; set; } = "session_not_reconciled";
    public string ReportedParametersJson { get; set; } = "{}";
    public string AdvertisedCapabilitiesJson { get; set; } = "[]";
    public string NegotiatedCapabilitiesJson { get; set; } = "[]";
    public string? StaticObservationJson { get; set; }
    // Strong random Agent secrets are distinct from ordinary Identity/OpenIddict credentials.
    public string? EnrollmentTokenHash { get; set; }
    public string? AuthTokenHash { get; set; }
    public string? ProtectedPendingAuthToken { get; set; }
}

public sealed class AgentEnrollment
{
    public Guid Id { get; set; }
    public required string TokenHash { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public string? ClaimedAgentId { get; set; }
}
