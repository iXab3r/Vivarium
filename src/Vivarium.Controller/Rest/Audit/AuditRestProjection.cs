using Vivarium.Controller.Auditing;

namespace Vivarium.Controller.Rest.Audit;

internal sealed record AuditReadPageProjection(
    IReadOnlyList<AuditEventResource> Items,
    AuditEventCursor? NextCursor);

internal sealed class AuditRestProjection(AuditEventStore audits)
{
    public async Task<AuditReadPageProjection> ListAuditEventsAsync(
        AuditEventQuery query,
        AuditEventCursor? after,
        int limit)
    {
        var page = await audits.QueryPageAsync(query, after, limit);
        return new AuditReadPageProjection(
            page.Items.Select(candidate => ToResource(candidate.AuditEvent)).ToArray(),
            page.NextCursor);
    }

    private static AuditEventResource ToResource(StoredAuditEvent auditEvent) => new(
        auditEvent.AuditEventId,
        "/api/v1/audit-events",
        auditEvent.ReceivedAt,
        new AuditActorResource(
            auditEvent.ActorType,
            auditEvent.ActorId,
            auditEvent.CredentialKind),
        auditEvent.CorrelationId,
        auditEvent.RequestId,
        auditEvent.Source,
        auditEvent.Action,
        new AuditTargetResource(auditEvent.TargetType, auditEvent.TargetId),
        OutcomeValue(auditEvent.Outcome),
        string.IsNullOrWhiteSpace(auditEvent.ReasonCode) ? null : auditEvent.ReasonCode,
        AuditRestDictionaries.Ordered(auditEvent.Details),
        auditEvent.BaseRevision,
        auditEvent.ResultRevision);

    internal static string OutcomeValue(AuditOutcome outcome) => outcome switch
    {
        AuditOutcome.Succeeded => "succeeded",
        AuditOutcome.Denied => "denied",
        AuditOutcome.Failed => "failed",
        AuditOutcome.NoChange => "no-change",
        _ => "unknown",
    };
}
