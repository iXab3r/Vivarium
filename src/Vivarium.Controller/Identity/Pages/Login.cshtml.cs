using Vivarium.Controller.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using OpenIddict.Client;

namespace Vivarium.Controller.Identity.Pages;

/// <summary>One common login page; only the reserved installation account has a local password.</summary>
[AllowAnonymous]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class LoginModel(SignInManager<VivariumUser> signIn, IOptions<OpenIddictClientOptions> options) : PageModel
{
    [BindProperty] public string? Login { get; set; }
    [BindProperty] public string? Token { get; set; }
    [BindProperty] public bool RememberMe { get; set; }
    [BindProperty(SupportsGet = true)] public string ReturnUrl { get; set; } = "/";
    public IEnumerable<string> Providers => options.Value.Registrations
        .Where(registration => registration.RegistrationId?.StartsWith("external:", StringComparison.Ordinal) == true)
        .Select(registration => registration.ProviderName!);

    public IActionResult OnGet() => Url.IsLocalUrl(ReturnUrl) ? Page() : BadRequest();

    public async Task<IActionResult> OnPostAsync()
    {
        if (!Url.IsLocalUrl(ReturnUrl)) return BadRequest();
        if (Login == VivariumIdentityConstants.SuperuserName && Token is { Length: > 0 and <= 1024 }
            && (await signIn.PasswordSignInAsync(VivariumIdentityConstants.SuperuserName, Token, RememberMe,
                lockoutOnFailure: true)).Succeeded)
            return LocalRedirect(ReturnUrl);
        ModelState.Clear();
        Token = null;
        ModelState.AddModelError(string.Empty, "Sign-in failed.");
        return Page();
    }
}
