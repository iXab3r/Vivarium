using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;

namespace Vivarium.Controller.Rest.Common;

public sealed class VivariumProblemDetails : ProblemDetails
{
    [JsonPropertyName("code")]
    public required string Code { get; init; }

    [JsonPropertyName("correlationId")]
    public required string CorrelationId { get; init; }

    [JsonPropertyName("retryable")]
    public bool Retryable { get; init; }

    [JsonPropertyName("target")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RestProblemTarget? Target { get; init; }

    [JsonPropertyName("errors")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<RestProblemError>? Errors { get; init; }
}

public sealed record RestProblemTarget(string Type, string Id);

public sealed record RestProblemError(string Path, string Code, string Message);
