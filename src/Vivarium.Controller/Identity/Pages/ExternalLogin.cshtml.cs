using Vivarium.Controller.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using OpenIddict.Client;
using OpenIddict.Client.AspNetCore;

namespace Vivarium.Controller.Identity.Pages;

/// <summary>Start a configured framework challenge or finish its temporary Identity external-login cookie.</summary>
[AllowAnonymous]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class ExternalLoginModel(SignInManager<VivariumUser> signIn, VivariumExternalUsers externalUsers,
    IOptions<OpenIddictClientOptions> options) : PageModel
{
    public IActionResult OnPost(string provider, bool rememberMe = false, string returnUrl = "/")
    {
        if (!Url.IsLocalUrl(returnUrl) || !options.Value.Registrations.Any(registration =>
                registration.RegistrationId == "external:" + provider)) return BadRequest();
        var properties = new AuthenticationProperties
        {
            RedirectUri = "/Identity/Account/ExternalLogin?handler=Callback"
        };
        properties.Items[OpenIddictClientAspNetCoreConstants.Properties.RegistrationId] = "external:" + provider;
        properties.Items[VivariumExternalCallback.ReturnUrl] = returnUrl;
        properties.Items[VivariumExternalCallback.RememberMe] = rememberMe ? "true" : "false";
        return Challenge(properties, OpenIddictClientAspNetCoreDefaults.AuthenticationScheme);
    }

    public IActionResult OnGet() => BadRequest();

    public async Task<IActionResult> OnGetCallbackAsync(CancellationToken cancellationToken)
    {
        var external = await HttpContext.AuthenticateAsync(IdentityConstants.ExternalScheme);
        var info = await signIn.GetExternalLoginInfoAsync();
        if (info is null || !external.Succeeded
            || !external.Properties!.Items.TryGetValue(VivariumExternalCallback.ReturnUrl, out var returnUrl)
            || !Url.IsLocalUrl(returnUrl)
            || !options.Value.Registrations.Any(registration =>
                registration.RegistrationId?.StartsWith("external:", StringComparison.Ordinal) == true
                && registration.Issuer!.AbsoluteUri == info.LoginProvider)) return BadRequest();
        var user = await externalUsers.FindOrCreateAsync(info.LoginProvider, info.ProviderKey,
            info.Principal.Identity?.Name, cancellationToken);
        if (user.Id == VivariumIdentityConstants.SuperuserId) return Forbid();
        external.Properties.Items.TryGetValue(VivariumExternalCallback.RememberMe, out var rememberMe);
        var remember = rememberMe == "true";
        var result = await signIn.ExternalLoginSignInAsync(info.LoginProvider, info.ProviderKey, remember, bypassTwoFactor: true);
        if (!result.Succeeded)
        {
            await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);
            return LocalRedirect("/Identity/Account/Login");
        }
        return LocalRedirect(returnUrl!);
    }
}
