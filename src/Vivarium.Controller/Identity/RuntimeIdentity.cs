using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using OpenIddict.Abstractions;
using OpenIddict.Validation.AspNetCore;
using Vivarium.Controller.Tokens;
using Vivarium.Controller.Rest.Common;

namespace Vivarium.Controller.Identity;

/// <summary>Resolve exactly one framework credential and the current local account for management requests.</summary>
public sealed class RuntimeIdentity(CurrentAccess access)
{
    public async Task<VivariumUser?> CurrentAsync(HttpContext http)
    {
        // An explicit credential never falls back to a more privileged browser session.
        var bearer = http.Request.Headers.ContainsKey("Authorization");
        var ticket = await http.AuthenticateAsync(bearer
            ? OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme : IdentityConstants.ApplicationScheme);
        if (!ticket.Succeeded || ticket.Principal is null) return null;
        var subject = bearer ? ticket.Principal.GetClaim(OpenIddictConstants.Claims.Subject)
            : ticket.Principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(subject, out var userId)
            || bearer && !ticket.Principal.HasScope(VivariumTokenProfile.Scope)) return null;
        if (bearer && userId == VivariumIdentityConstants.SuperuserId) return null;
        return await access.FindEligibleAsync(userId, http.RequestAborted);
    }

    internal static void Map(WebApplication app) =>
        app.MapGet("/api/v1/users/current", async (HttpContext http, RuntimeIdentity identity) =>
        {
            var user = await identity.CurrentAsync(http);
            if (user is null) http.Response.Headers.WWWAuthenticate = "Bearer";
            return user is null ? RestProblems.AuthenticationRequired(http)
                : Results.Json(new { id = user.Id, teamCityId = user.TeamCityId, userName = user.UserName, displayName = user.DisplayName });
        });
}
