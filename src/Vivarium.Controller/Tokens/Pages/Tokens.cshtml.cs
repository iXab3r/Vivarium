using System.Globalization;
using Vivarium.Controller.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using OpenIddict.Abstractions;
using OpenIddict.Client.AspNetCore;
using OpenIddict.EntityFrameworkCore.Models;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Vivarium.Controller.Tokens.Pages;

/// <summary>Own token metadata and framework issuance/revocation; no recoverable credential store.</summary>
[Authorize(Policy = VivariumTokenProfile.OrdinaryUserPolicy)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class TokensModel(UserManager<VivariumUser> users, IOpenIddictTokenManager tokens,
    IOpenIddictApplicationManager applications, IConfiguration configuration) : PageModel
{
    public List<TokenRow> Entries { get; } = [];
    public int LifetimeCap => configuration.GetValue<int>("Vivarium:Identity:TokenLifetimeDays");
    [BindProperty] public string? Label { get; set; }
    [BindProperty] public int? LifetimeDays { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        VivariumTokenEndpoints.NoStore(HttpContext);
        var user = await VivariumTokenProfile.CurrentUserAsync(HttpContext, users);
        if (user is null) return Forbid();
        var subject = user.Id.ToString();
        await foreach (var token in tokens.ListAsync(query => query.OfType<OpenIddictEntityFrameworkCoreToken>()
            .Where(token => token.Subject == subject && token.Type == TokenTypeIdentifiers.AccessToken
                && token.Application != null && token.Application.ClientId == VivariumTokenProfile.Client)
            .OrderByDescending(token => token.CreationDate).ThenBy(token => token.Id).Take(100), HttpContext.RequestAborted))
        {
            var properties = await tokens.GetPropertiesAsync(token, HttpContext.RequestAborted);
            Entries.Add(new TokenRow((await tokens.GetIdAsync(token, HttpContext.RequestAborted))!,
                properties.TryGetValue(VivariumTokenProfile.Label, out var label) ? label.GetString() ?? "" : "",
                await tokens.GetCreationDateAsync(token, HttpContext.RequestAborted),
                await tokens.GetExpirationDateAsync(token, HttpContext.RequestAborted),
                (await tokens.GetStatusAsync(token, HttpContext.RequestAborted))!));
        }
        return Page();
    }

    public async Task<IActionResult> OnPostCreateAsync()
    {
        VivariumTokenEndpoints.NoStore(HttpContext);
        var user = await VivariumTokenProfile.CurrentUserAsync(HttpContext, users);
        if (user is null) return Forbid();
        if (!ModelState.IsValid || !VivariumTokenProfile.TryInput(Label,
            LifetimeDays?.ToString(CultureInfo.InvariantCulture), configuration, out var label, out var days))
            return BadRequest();
        var properties = new AuthenticationProperties
        {
            RedirectUri = "/signin-vivarium-token",
            Items =
            {
                [OpenIddictClientAspNetCoreConstants.Properties.RegistrationId] = VivariumTokenProfile.Client,
                [OpenIddictClientAspNetCoreConstants.Properties.Scope] = VivariumTokenProfile.Scope,
                [OpenIddictClientAspNetCoreConstants.Properties.CodeChallengeMethod] = CodeChallengeMethods.Sha256,
                [VivariumTokenProfile.InitiatingUser] = user.Id.ToString(),
                [VivariumTokenProfile.Label] = label,
                [VivariumTokenProfile.Lifetime] = days.ToString(CultureInfo.InvariantCulture)
            }
        };
        return Challenge(properties, OpenIddictClientAspNetCoreDefaults.AuthenticationScheme);
    }

    public async Task<IActionResult> OnPostRevokeAsync(string id)
    {
        VivariumTokenEndpoints.NoStore(HttpContext);
        var user = await VivariumTokenProfile.CurrentUserAsync(HttpContext, users);
        if (user is null) return Forbid();
        var token = await VivariumTokenEndpoints.OwnEntryAsync(id, user.Id.ToString(), tokens, applications,
            HttpContext.RequestAborted);
        if (token is null) return NotFound();
        if (!await tokens.TryRevokeAsync(token, HttpContext.RequestAborted)) return StatusCode(503);
        return LocalRedirect("/Identity/Account/Manage/Tokens");
    }

    public sealed record TokenRow(string Id, string Label, DateTimeOffset? CreatedAt, DateTimeOffset? ExpiresAt, string Status);
}
