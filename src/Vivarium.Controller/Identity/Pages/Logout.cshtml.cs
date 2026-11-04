using Vivarium.Controller.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Vivarium.Controller.Identity.Pages;

/// <summary>Antiforgery-protected common local logout; does not revoke JWTs or the upstream session.</summary>
[Authorize(AuthenticationSchemes = "Identity.Application")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class LogoutModel(SignInManager<VivariumUser> signIn) : PageModel
{
    public IActionResult OnGet() => StatusCode(StatusCodes.Status405MethodNotAllowed);

    public async Task<IActionResult> OnPostAsync()
    {
        await signIn.SignOutAsync();
        return LocalRedirect("/");
    }
}
