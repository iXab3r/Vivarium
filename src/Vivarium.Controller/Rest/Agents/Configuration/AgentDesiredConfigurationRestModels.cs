using Microsoft.AspNetCore.Mvc;

namespace Vivarium.Controller.Rest.Agents.Configuration;

public sealed record AgentDesiredConfigurationUpdateRequest(bool? Enabled);

public sealed record AgentDesiredConfigurationResource(
    string AgentId,
    string Url,
    bool? DesiredEnabled,
    bool? AppliedEnabled,
    string ConfigurationRevision,
    string? AppliedConfigurationRevision,
    string State,
    IReadOnlyList<AgentConfigurationDiagnosticResource> Diagnostics);

public sealed record AgentDesiredConfigurationChangeResource(
    string OperationId,
    string State,
    string BaseConfigurationRevision,
    string HeadConfigurationRevision,
    string? AppliedConfigurationRevision,
    IReadOnlyList<AgentConfigurationDiffResource> Diff,
    bool Replayed,
    AgentDesiredConfigurationResource Settings);

public sealed record AgentConfigurationDiffResource(
    string Path,
    string Change,
    string? PreviousContentHash,
    string? ResultContentHash);

public sealed record AgentConfigurationDiagnosticResource(
    string Code,
    string? Path,
    string? Field,
    string Summary);

public sealed class AgentConfigurationProblemDetails : ProblemDetails
{
    public required string Code { get; init; }

    public required string CorrelationId { get; init; }

    public bool Retryable { get; init; }

    public string? CurrentConfigurationRevision { get; init; }

    public string? AppliedConfigurationRevision { get; init; }

    public IReadOnlyList<AgentConfigurationDiagnosticResource>? Errors { get; init; }
}
