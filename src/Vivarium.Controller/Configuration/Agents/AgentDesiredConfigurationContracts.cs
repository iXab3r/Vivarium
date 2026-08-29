using Vivarium.Controller.Configuration.Git;

namespace Vivarium.Controller.Configuration.Agents;

public enum AgentDesiredConfigurationState
{
    Pending,
    Active,
    Invalid,
    Blocked,
}

public sealed record AgentDesiredConfigurationSnapshot(
    string AgentId,
    bool? DesiredEnabled,
    bool? AppliedEnabled,
    ConfigurationRevision AuthoritativeRevision,
    ConfigurationRevision? AppliedRevision,
    AgentDesiredConfigurationState State,
    IReadOnlyList<ConfigurationValidationDiagnostic> Diagnostics);

public sealed record AgentDesiredConfigurationMutationResult(
    string OperationId,
    ConfigurationRevision ResultRevision,
    AgentDesiredConfigurationSnapshot Settings,
    IReadOnlyList<ConfigurationPathDiff> Diff,
    bool Replayed);

public sealed record AgentDesiredConfigurationActivation(
    string AgentId,
    bool Enabled,
    ConfigurationRevision AppliedRevision,
    string OperationId);

/// <summary>
/// Bridges a committed and reconciled desired setting to the live Agent scheduling projection.
/// Root composition owns the registry adapter so this domain service never creates a second desired
/// authority or reaches into a live session directly.
/// </summary>
public interface IAgentDesiredConfigurationActivationSink
{
    void OnApplied(AgentDesiredConfigurationActivation activation);
}

public interface IAgentDesiredConfigurationService
{
    Task<AgentDesiredConfigurationSnapshot?> GetAsync(
        string agentId,
        CancellationToken cancellationToken = default);

    Task<AgentDesiredConfigurationMutationResult> SetEnabledAsync(
        Security.ManagementRequestContext context,
        string agentId,
        bool enabled,
        ConfigurationRevision expectedBase,
        CancellationToken cancellationToken = default);
}

public sealed class AgentDesiredConfigurationNotFoundException(string agentId)
    : Exception($"agent '{agentId}' does not exist")
{
    public string AgentId { get; } = agentId;
}

public sealed class AgentDesiredConfigurationValidationException(
    string code,
    string summary,
    IReadOnlyList<ConfigurationValidationDiagnostic>? diagnostics = null)
    : Exception(summary)
{
    public string Code { get; } = code;

    public IReadOnlyList<ConfigurationValidationDiagnostic> Diagnostics { get; } = diagnostics ?? [];
}

public sealed class AgentDesiredConfigurationPreconditionException(
    ConfigurationRevision expectedRevision,
    ConfigurationRevision currentRevision,
    IReadOnlyList<ConfigurationPathDiff> diff)
    : Exception("the authoritative Agent configuration revision changed")
{
    public ConfigurationRevision ExpectedRevision { get; } = expectedRevision;

    public ConfigurationRevision CurrentRevision { get; } = currentRevision;

    public IReadOnlyList<ConfigurationPathDiff> Diff { get; } = diff;
}

public sealed class AgentDesiredConfigurationConflictException(
    string code,
    string summary,
    ConfigurationRevision currentRevision,
    ConfigurationRevision? appliedRevision,
    IReadOnlyList<ConfigurationValidationDiagnostic> diagnostics,
    Exception? innerException = null)
    : Exception(summary, innerException)
{
    public string Code { get; } = code;

    public ConfigurationRevision CurrentRevision { get; } = currentRevision;

    public ConfigurationRevision? AppliedRevision { get; } = appliedRevision;

    public IReadOnlyList<ConfigurationValidationDiagnostic> Diagnostics { get; } = diagnostics;
}
