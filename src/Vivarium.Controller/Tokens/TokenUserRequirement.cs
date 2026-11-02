using Vivarium.Controller.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;

namespace Vivarium.Controller.Tokens;

/// <summary>Admission uses the current ordinary account, independently of stored cookie roles.</summary>
internal sealed class TokenUserRequirement : AuthorizationHandler<TokenUserRequirement>, IAuthorizationRequirement
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context,
        TokenUserRequirement requirement)
    {
        if (context.Resource is not HttpContext http) return;
        var user = await VivariumTokenProfile.CurrentUserAsync(http,
            http.RequestServices.GetRequiredService<UserManager<VivariumUser>>());
        if (user is not null) context.Succeed(requirement);
    }
}
