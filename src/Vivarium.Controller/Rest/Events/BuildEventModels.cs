using System.Text.Json;

namespace Vivarium.Controller.Rest.Events;

public sealed record EventResourceReference(
    string Type,
    string Id,
    string Url);

public sealed record RestEventEnvelope(
    string Id,
    long Sequence,
    DateTimeOffset OccurredAt,
    string Type,
    EventResourceReference Resource,
    string CorrelationId,
    JsonElement Data,
    string? ConfigurationRevision,
    string? ObservationRevision,
    string? RuntimeRevision);

internal sealed record BuildEventData(
    string Recovery,
    bool AuthoritativeGetRequired);

public sealed class BuildEventStreamOptions
{
    public int BatchSize { get; init; } = 64;

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    public TimeSpan KeepaliveInterval { get; init; } = TimeSpan.FromSeconds(15);

    public TimeSpan WriteTimeout { get; init; } = TimeSpan.FromSeconds(15);
}
