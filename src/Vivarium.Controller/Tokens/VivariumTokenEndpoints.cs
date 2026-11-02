using System.Globalization;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Vivarium.Controller.Identity;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using OpenIddict.Abstractions;
using OpenIddict.Client.AspNetCore;
using OpenIddict.Server.AspNetCore;
using OpenIddict.Validation;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Vivarium.Controller.Tokens;

/// <summary>Application decisions inside the stock authorization-code, redemption and callback pipeline.</summary>
internal static class VivariumTokenEndpoints
{
    public static void MapVivariumTokens(this WebApplication app)
    {
        app.MapMethods("/connect/authorize", ["GET", "POST"], AuthorizeAsync)
            .WithName(VivariumTokenProfile.AuthorizationEndpoint)
            .RequireAuthorization(VivariumTokenProfile.OrdinaryUserPolicy)
            .RequireRateLimiting(VivariumTokenProfile.CreationPolicy);
        app.MapPost("/connect/token", RedeemAsync).WithName(VivariumTokenProfile.RedemptionEndpoint);
        app.MapGet("/signin-vivarium-token", CompleteAsync);
    }

    private static async Task<IResult> AuthorizeAsync(HttpContext http, UserManager<VivariumUser> users,
        IConfiguration configuration)
    {
        var request = http.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("A validated authorization request is required.");
        var user = await VivariumTokenProfile.CurrentUserAsync(http, users);
        if (user is null) return Reject(Errors.AccessDenied);
        if (request.ClientId != VivariumTokenProfile.Client || !request.IsAuthorizationCodeFlow()
            || request.GetScopes().Length != 1 || !request.HasScope(VivariumTokenProfile.Scope)
            || request.CodeChallengeMethod != CodeChallengeMethods.Sha256
            || !VivariumTokenProfile.TryInput((string?)request["vivarium_label"],
                (string?)request["vivarium_lifetime_days"], configuration, out var label, out var days))
            return Reject(Errors.InvalidRequest);

        var identity = new ClaimsIdentity(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
            Claims.Name, Claims.Role);
        identity.SetClaim(Claims.Subject, user.Id.ToString());
        identity.SetClaim(VivariumTokenProfile.CodeLabel, label);
        var principal = new ClaimsPrincipal(identity);
        principal.SetScopes(VivariumTokenProfile.Scope);
        principal.SetResources(VivariumTokenProfile.Audience);
        principal.SetAccessTokenLifetime(TimeSpan.FromDays(days));
        return Results.SignIn(principal, new AuthenticationProperties(),
            OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    private static async Task<IResult> RedeemAsync(HttpContext http, UserManager<VivariumUser> users)
    {
        var request = http.GetOpenIddictServerRequest();
        if (request is null || !request.IsAuthorizationCodeGrantType()
            || request.ClientId != VivariumTokenProfile.Client) return Reject(Errors.InvalidGrant);
        var ticket = await http.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        var principal = ticket.Principal;
        if (!ticket.Succeeded || principal?.GetClaim(Claims.Subject) is not { } subject)
            return Reject(Errors.InvalidGrant);
        var user = await users.FindByIdAsync(subject);
        if (user is not { IsEnabled: true } || user.Id == VivariumIdentityConstants.SuperuserId)
            return Reject(Errors.InvalidGrant);
        // The authorization-code principal is framework-protected. Its finite lifetime and bounded
        // private label survive; none of the caller's redemption fields can replace them.
        return Results.SignIn(principal, new AuthenticationProperties(),
            OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    private static async Task<IResult> CompleteAsync(HttpContext http, UserManager<VivariumUser> users,
        OpenIddictValidationService validation, IOpenIddictTokenManager tokens,
        IOpenIddictApplicationManager applications)
    {
        NoStore(http);
        var ticket = await http.AuthenticateAsync(OpenIddictClientAspNetCoreDefaults.AuthenticationScheme);
        if (!ticket.Succeeded || ticket.Properties is null
            || !ticket.Properties.Items.TryGetValue(OpenIddictClientAspNetCoreConstants.Properties.RegistrationId,
                out var registration) || registration != VivariumTokenProfile.Client)
            return Results.BadRequest();
        var raw = ticket.Properties.GetTokenValue(OpenIddictClientAspNetCoreConstants.Tokens.BackchannelAccessToken);
        if (string.IsNullOrEmpty(raw)) return Results.BadRequest();
        ClaimsPrincipal validated;
        try { validated = await validation.ValidateAccessTokenAsync(raw, http.RequestAborted); }
        catch (OpenIddictExceptions.ProtocolException) { return Results.BadRequest(); }
        var subject = validated.GetClaim(Claims.Subject);
        var user = await VivariumTokenProfile.CurrentUserAsync(http, users);
        if (user is null || !validated.HasScope(VivariumTokenProfile.Scope)
            || subject != user.Id.ToString()
            || !ticket.Properties.Items.TryGetValue(VivariumTokenProfile.InitiatingUser, out var initiator)
            || subject != initiator)
        {
            if (await OwnEntryAsync(validated.GetTokenId(), subject, tokens, applications, http.RequestAborted)
                is { } entry && !await tokens.TryRevokeAsync(entry, http.RequestAborted))
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            return Results.BadRequest();
        }
        // A response-only disclosure: no redirect, TempData, application cookie or recoverable list.
        var encoded = HtmlEncoder.Default.Encode(raw);
        return Results.Content("""
            <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>New token · Vivarium</title><link rel="stylesheet" href="/identity.css"></head>
            <body class="account-page token-reveal"><header class="app-header"><a class="brand" href="/">Vivarium</a><a href="/">Back to Vivarium</a></header><main>
            <h1>New token</h1><section class="account-panel" aria-label="Token disclosure">
            <p class="token-warning" id="token-warning">Copy this token now. It will not be shown again.</p>
            <label class="field" for="token">Token</label><textarea id="token" readonly rows="8" aria-describedby="token-warning" spellcheck="false">
            """ + encoded + """
            </textarea><div class="token-actions"><button id="copy" class="primary" type="button">Copy token</button><a class="account-button" href="/Identity/Account/Manage/Tokens">Done</a></div>
            </section></main><script>document.getElementById('copy').onclick=async()=>{await navigator.clipboard.writeText(document.getElementById('token').value.trim());document.getElementById('copy').textContent='Copied';};</script></body></html>
            """, "text/html; charset=utf-8");
    }

    internal static async Task<object?> OwnEntryAsync(string? id, string? subject,
        IOpenIddictTokenManager tokens, IOpenIddictApplicationManager applications, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(subject)) return null;
        var entry = await tokens.FindByIdAsync(id, cancellationToken);
        if (entry is null || await tokens.GetSubjectAsync(entry, cancellationToken) != subject
            || await tokens.GetTypeAsync(entry, cancellationToken) != TokenTypeIdentifiers.AccessToken) return null;
        var appId = await tokens.GetApplicationIdAsync(entry, cancellationToken);
        var app = appId is null ? null : await applications.FindByIdAsync(appId, cancellationToken);
        return app is not null && await applications.GetClientIdAsync(app, cancellationToken) == VivariumTokenProfile.Client
            ? entry : null;
    }

    internal static void NoStore(HttpContext http)
    {
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers["Referrer-Policy"] = "no-referrer";
        http.Response.Headers.XContentTypeOptions = "nosniff";
    }

    private static IResult Reject(string error) => Results.Forbid(new AuthenticationProperties(
        new Dictionary<string, string?>
        { [OpenIddictServerAspNetCoreConstants.Properties.Error] = error }),
        [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
}
