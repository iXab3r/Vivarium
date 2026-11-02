using Vivarium.Controller.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Vivarium.Controller.Identity;

/// <summary>Use Identity's ordinary sign-in eligibility hook for current account enablement.</summary>
internal sealed class VivariumSignInManager(UserManager<VivariumUser> userManager, IHttpContextAccessor accessor,
    IUserClaimsPrincipalFactory<VivariumUser> claimsFactory, IOptions<IdentityOptions> options,
    ILogger<SignInManager<VivariumUser>> logger, IAuthenticationSchemeProvider schemes,
    IUserConfirmation<VivariumUser> confirmation, IConfiguration configuration)
    : SignInManager<VivariumUser>(userManager, accessor, claimsFactory, options, logger, schemes, confirmation)
{
    public override async Task<bool> CanSignInAsync(VivariumUser user) => user.IsEnabled
        && (user.Id != VivariumIdentityConstants.SuperuserId || configuration.GetValue<bool>("Vivarium:Identity:SuperuserEnabled"))
        && await base.CanSignInAsync(user);
}
