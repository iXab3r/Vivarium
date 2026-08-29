using Grpc.Core;
using Vivarium.Controller.Auditing;
using Vivarium.Controller.Security;

namespace Vivarium.Controller.Management;

internal sealed class ControlPlaneAuthorizer(
    ManagementRequestContextFactory contexts,
    ManagementAuthorizer authorizer,
    AuditEventStore audits,
    TimeProvider timeProvider)
{
    public async Task<ManagementRequestContext> DemandAsync(
        ManagementPermission permission,
        ServerCallContext context)
    {
        var requestContext = await AuthenticateAsync(context);
        try
        {
            authorizer.Demand(requestContext, permission);
            return requestContext;
        }
        catch (ManagementAuthorizationException exception)
        {
            await audits.AppendAsync(AuditEventDraft.Create(
                requestContext,
                timeProvider.GetUtcNow(),
                "security.authorization",
                "permission",
                permission.ToString(),
                AuditOutcome.Denied,
                exception.ReasonCode));
            throw PermissionDenied(exception);
        }
    }

    public async Task<ManagementRequestContext> AuthenticateAsync(
        ServerCallContext context,
        string action = "security.authentication",
        string targetType = "rpc",
        string? targetId = null)
    {
        var header = context.RequestHeaders.FirstOrDefault(entry =>
            entry.Key.Equals("authorization", StringComparison.OrdinalIgnoreCase))?.Value;
        var suppliedCorrelationId = context.RequestHeaders.FirstOrDefault(entry =>
            entry.Key.Equals("x-correlation-id", StringComparison.OrdinalIgnoreCase))?.Value;
        ManagementRequestContext? requestContext;
        string normalizedCorrelationId;
        try
        {
            normalizedCorrelationId = ManagementIdentifiers.NormalizeCorrelationId(
                suppliedCorrelationId);
            requestContext = await contexts.FromBearerAsync(
                header ?? string.Empty,
                normalizedCorrelationId,
                requestId: null,
                source: "control-plane");
        }
        catch (ArgumentException exception)
        {
            var invalidContext = ManagementRequestContext.Anonymous("control-plane");
            AddCorrelationTrailer(context, invalidContext.CorrelationId);
            await audits.AppendAsync(AuditEventDraft.Create(
                invalidContext,
                timeProvider.GetUtcNow(),
                action,
                targetType,
                NormalizeTargetId(targetId ?? context.Method),
                AuditOutcome.Failed,
                "invalid_request"));
            throw new RpcException(new Status(StatusCode.InvalidArgument, exception.Message));
        }

        if (requestContext is null)
        {
            requestContext = ManagementRequestContext.Anonymous(
                "control-plane", normalizedCorrelationId);
            AddCorrelationTrailer(context, requestContext.CorrelationId);
            await audits.AppendAsync(AuditEventDraft.Create(
                requestContext,
                timeProvider.GetUtcNow(),
                action,
                targetType,
                NormalizeTargetId(targetId ?? context.Method),
                AuditOutcome.Denied,
                "authentication_required"));
            throw Unauthenticated();
        }

        AddCorrelationTrailer(context, requestContext.CorrelationId);
        return requestContext;
    }

    public static RpcException PermissionDenied(ManagementAuthorizationException exception) =>
        new(new Status(StatusCode.PermissionDenied, exception.Message));

    private static RpcException Unauthenticated() => new(new Status(
        StatusCode.Unauthenticated, "valid bearer token required"));

    private static void AddCorrelationTrailer(ServerCallContext context, string correlationId)
    {
        if (!context.ResponseTrailers.Any(entry => entry.Key == "x-correlation-id"))
        {
            context.ResponseTrailers.Add("x-correlation-id", correlationId);
        }
    }

    private static string NormalizeTargetId(string targetId)
    {
        var normalized = string.IsNullOrWhiteSpace(targetId) ? "(unspecified)" : targetId.Trim();
        return normalized.Length <= 256 ? normalized : normalized[..256];
    }
}
