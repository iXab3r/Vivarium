using System.Security.Claims;
using Vivarium.Controller.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using OpenIddict.Client;
using OpenIddict.Client.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Vivarium.Controller.Identity;

/// <summary>Bridge only validated external OIDC ID-token identity into Identity's temporary external cookie.</summary>
internal static class VivariumExternalCallback
{
    internal const string ReturnUrl = "vivarium.returnUrl";
    internal const string RememberMe = "vivarium.rememberMe";

    public static async Task<IResult> CompleteAsync(HttpContext context, string providerKey,
        SignInManager<VivariumUser> signIn, IOptions<OpenIddictClientOptions> options)
    {
        context.Response.Headers.CacheControl = "no-store";
        var result = await context.AuthenticateAsync(OpenIddictClientAspNetCoreDefaults.AuthenticationScheme);
        var registration = options.Value.Registrations.SingleOrDefault(item => item.RegistrationId == "external:" + providerKey);
        var id = result.Properties?.GetParameter<ClaimsPrincipal>(OpenIddictClientAspNetCoreConstants.Properties.BackchannelIdentityTokenPrincipal);
        var subject = id?.GetClaim(Claims.Subject);
        if (!result.Succeeded || registration is null || id is null || string.IsNullOrEmpty(subject)
            || subject.Length > 255 || subject.Any(character => character > 127)
            || id.GetClaim(Claims.Issuer) != registration.Issuer!.AbsoluteUri
            || result.Principal?.GetClaim(Claims.Private.RegistrationId) != registration.RegistrationId
            || !result.Properties!.Items.TryGetValue(ReturnUrl, out var returnUrl)
            || !RedirectHttpResult.IsLocalUrl(returnUrl))
            return Results.BadRequest();

        // Copy only identity and nonsecret protected UI state, never provider tokens or OAuth properties.
        var properties = signIn.ConfigureExternalAuthenticationProperties(registration.Issuer.AbsoluteUri,
            "/Identity/Account/ExternalLogin?handler=Callback");
        properties.Items[ReturnUrl] = returnUrl;
        result.Properties.Items.TryGetValue(RememberMe, out var rememberMe);
        properties.Items[RememberMe] = rememberMe;
        var identity = new ClaimsIdentity(IdentityConstants.ExternalScheme);
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, subject));
        if (id.GetClaim(Claims.Name) is { } name) identity.AddClaim(new Claim(ClaimTypes.Name, name));
        await context.SignInAsync(IdentityConstants.ExternalScheme, new ClaimsPrincipal(identity), properties);
        return Results.LocalRedirect(properties.RedirectUri!);
    }

}
