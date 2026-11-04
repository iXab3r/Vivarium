using Vivarium.Controller.Persistence;

namespace Vivarium.Controller.Auditing;

/// <summary>Stages the shared bounded audit contract in the caller's application transaction.</summary>
public sealed class PostgresAuditStore(VivariumDbContext database)
{
    public void Stage(AuditEventDraft draft)
    {
        AuditEventStore.Validate(draft);
        var context = draft.RequestContext;
        database.AuditEvents.Add(new PersistentAuditEvent
        {
            Id = draft.AuditEventId, ReceivedAt = draft.ReceivedAt.ToUniversalTime(),
            ActorType = context.Principal.ActorType, ActorId = context.Principal.ActorId,
            CredentialKind = context.Principal.CredentialKind, CorrelationId = context.CorrelationId,
            RequestId = context.RequestId, Action = draft.Action, TargetType = draft.TargetType,
            TargetId = draft.TargetId, Outcome = AuditEventStore.SerializeOutcome(draft.Outcome),
            ReasonCode = draft.ReasonCode,
            DetailsJson = AuditEventStore.SerializeStoredDetails(draft.Details, context.Source),
            BaseRevision = draft.BaseRevision, ResultRevision = draft.ResultRevision,
        });
    }
}

public sealed class PersistentAuditEvent
{
    public required string Id { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public required string ActorType { get; set; }
    public required string ActorId { get; set; }
    public required string CredentialKind { get; set; }
    public required string CorrelationId { get; set; }
    public string? RequestId { get; set; }
    public required string Action { get; set; }
    public required string TargetType { get; set; }
    public required string TargetId { get; set; }
    public required string Outcome { get; set; }
    public required string ReasonCode { get; set; }
    public required string DetailsJson { get; set; }
    public string? BaseRevision { get; set; }
    public string? ResultRevision { get; set; }
}
