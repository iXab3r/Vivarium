using Microsoft.AspNetCore.Components.Authorization;
using Vivarium.Controller.Auditing;
using Vivarium.Controller.Security;

namespace Vivarium.Controller.Components;

public sealed class PanelManagementContext(
    AuthenticationStateProvider authenticationState,
    ManagementRequestContextFactory contexts,
    ManagementAuthorizer authorizer,
    AuditEventStore audits,
    TimeProvider timeProvider)
{
    public async Task<ManagementRequestContext> DemandAsync(ManagementPermission permission)
    {
        var state = await authenticationState.GetAuthenticationStateAsync();
        var context = contexts.FromClaims(
            state.User,
            suppliedCorrelationId: null,
            requestId: null,
            source: "panel");
        try
        {
            authorizer.Demand(context, permission);
            return context;
        }
        catch (ManagementAuthorizationException exception)
        {
            await audits.AppendAsync(AuditEventDraft.Create(
                context,
                timeProvider.GetUtcNow(),
                "security.authorization",
                "permission",
                permission.ToString(),
                AuditOutcome.Denied,
                exception.ReasonCode));
            throw;
        }
    }
}
