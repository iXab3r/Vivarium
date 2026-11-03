using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Vivarium.Controller.Rest.Common;
using Vivarium.Controller.Security;

namespace Vivarium.Controller.Identity;

/// <summary>One framework credential and the stock browser request-verification boundary.</summary>
internal static class RuntimeManagement
{
    internal static async Task<IResult> ExecuteAsync(HttpContext http, Func<ManagementRequestContext, Task<IResult>> action,
        bool mutation = false, string source = "access")
    {
        var user = await http.RequestServices.GetRequiredService<RuntimeIdentity>().CurrentAsync(http);
        if (user is null)
        {
            http.Response.Headers.WWWAuthenticate = "Bearer";
            return RestProblems.AuthenticationRequired(http);
        }
        try
        {
            var bearer = http.Request.Headers.ContainsKey("Authorization");
            if (mutation && !bearer) await http.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(http);
            return await action(http.RequestServices.GetRequiredService<RuntimeRequestContext>()
                .ForUser(user.Id, bearer ? "openid-bearer" : "identity-cookie", source));
        }
        catch (AccessPermissionException)
        {
            return RestProblems.Create(http, 403, "permission_denied", "Permission is denied",
                "Current permissions do not allow this action.");
        }
        catch (AntiforgeryValidationException)
        {
            return RestProblems.Create(http, 400, "invalid_antiforgery_token", "Request verification failed",
                "Use the current browser request-verification token.");
        }
        catch (JsonException) { return InvalidInput(http); }
        catch (BadHttpRequestException) { return InvalidInput(http); }
    }

    internal static IResult InvalidInput(HttpContext http) => RestProblems.Create(http, 400,
        "invalid_request", "Invalid request", "Supply the documented JSON request shape.");
}
