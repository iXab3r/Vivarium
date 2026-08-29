using System.Collections.ObjectModel;

namespace Vivarium.Controller.Rest.Audit;

public sealed record AuditRestCollection<T>(
    IReadOnlyList<T> Items,
    AuditRestPage Page);

public sealed record AuditRestPage(
    string? NextCursor,
    int Limit);

public sealed record AuditEventResource(
    string Id,
    string Url,
    DateTimeOffset ReceivedAt,
    AuditActorResource Actor,
    string CorrelationId,
    string? RequestId,
    string? Source,
    string Action,
    AuditTargetResource Target,
    string Outcome,
    string? ReasonCode,
    IReadOnlyDictionary<string, string> Details,
    string? BaseRevision,
    string? ResultRevision);

public sealed record AuditActorResource(
    string Type,
    string Id,
    string CredentialKind);

public sealed record AuditTargetResource(
    string Type,
    string Id);

internal static class AuditRestDictionaries
{
    public static IReadOnlyDictionary<string, string> Ordered(
        IEnumerable<KeyValuePair<string, string>> values) =>
        new ReadOnlyDictionary<string, string>(values
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
}
