using Vivarium.Controller.Administration;
using Vivarium.Controller.Security;

namespace Vivarium.Controller.Rest.Common;

public sealed record RestAuthorizationResult(
    ManagementRequestContext? Context,
    IResult? Failure)
{
    public bool IsAuthorized => Context is not null && Failure is null;
}

public static class RestAuthentication
{
    public static async Task<RestAuthorizationResult> AuthorizeAsync(
        HttpContext httpContext,
        ManagementPermission permission,
        string source,
        RestProblemTarget? target = null)
    {
        var authentication = await AuthenticateAsync(httpContext, source);
        if (!authentication.IsAuthorized)
        {
            return authentication;
        }

        var authorizer = httpContext.RequestServices.GetRequiredService<ManagementAuthorizer>();
        if (!authorizer.Allows(authentication.Context!.Principal, permission))
        {
            return new RestAuthorizationResult(
                Context: null,
                RestProblems.PermissionDenied(httpContext, permission.ToString(), target));
        }

        return authentication;
    }

    public static async Task<RestAuthorizationResult> AuthenticateManagementAsync(
        HttpContext httpContext,
        string source)
    {
        var authentication = await AuthenticateAsync(httpContext, source);
        if (!authentication.IsAuthorized)
        {
            return authentication;
        }

        var authorizer = httpContext.RequestServices.GetRequiredService<ManagementAuthorizer>();
        var principal = authentication.Context!.Principal;
        var isManagementPrincipal =
            authorizer.Allows(principal, ManagementPermission.PanelAccess) ||
            authorizer.Allows(principal, ManagementPermission.BuildWatch);
        return isManagementPrincipal
            ? authentication
            : new RestAuthorizationResult(
                Context: null,
                RestProblems.PermissionDenied(httpContext, "management-api.access"));
    }

    private static async Task<RestAuthorizationResult> AuthenticateAsync(
        HttpContext httpContext,
        string source)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        var contexts = httpContext.RequestServices
            .GetRequiredService<ManagementRequestContextFactory>();
        var authorizationHeader = httpContext.Request.Headers.Authorization.ToString();
        ManagementRequestContext? requestContext;
        try
        {
            if (!string.IsNullOrWhiteSpace(authorizationHeader))
            {
                const string recoveryPrefix = "Vivarium-Recovery ";
                if (authorizationHeader.StartsWith(
                        recoveryPrefix,
                        StringComparison.OrdinalIgnoreCase))
                {
                    var recovery = httpContext.RequestServices
                        .GetRequiredService<AdministrationBootstrapService>();
                    var authentication = await recovery.AuthenticateRecoverySessionAsync(
                        authorizationHeader[recoveryPrefix.Length..].Trim(),
                        httpContext.RequestAborted);
                    requestContext = authentication is null
                        ? null
                        : new ManagementRequestContext(
                            ManagementPrincipal.Superuser,
                            RestCorrelation.Get(httpContext),
                            RequestId: null,
                            source);
                }
                else
                {
                    requestContext = await contexts.FromBearerAsync(
                        authorizationHeader,
                        RestCorrelation.Get(httpContext),
                        requestId: null,
                        source);
                }
            }
            else if (httpContext.User.Identity?.IsAuthenticated == true)
            {
                requestContext = contexts.FromClaims(
                    httpContext.User,
                    RestCorrelation.Get(httpContext),
                    requestId: null,
                    source);
            }
            else
            {
                requestContext = null;
            }
        }
        catch (Exception exception) when (exception is
            ManagementAuthorizationException or
            AdministrationBootstrapException or
            ArgumentException)
        {
            requestContext = null;
        }

        return requestContext is null
            ? new RestAuthorizationResult(
                Context: null,
                RestProblems.AuthenticationRequired(httpContext))
            : new RestAuthorizationResult(requestContext, Failure: null);
    }
}
