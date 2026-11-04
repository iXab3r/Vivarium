using Vivarium.Controller.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using Vivarium.Controller.Auditing;
using Vivarium.Controller.Persistence;

namespace Vivarium.Controller.Identity;

/// <summary>Use Identity's ordinary sign-in eligibility hook for current account enablement.</summary>
internal sealed class VivariumSignInManager(UserManager<VivariumUser> userManager, IHttpContextAccessor accessor,
    IUserClaimsPrincipalFactory<VivariumUser> claimsFactory, IOptions<IdentityOptions> options,
    ILogger<SignInManager<VivariumUser>> logger, IAuthenticationSchemeProvider schemes,
    IUserConfirmation<VivariumUser> confirmation, IConfiguration configuration,
    VivariumDbContext database, PostgresAuditStore audits, RuntimeRequestContext requestContext, TimeProvider time)
    : SignInManager<VivariumUser>(userManager, accessor, claimsFactory, options, logger, schemes, confirmation)
{
    public override async Task<bool> CanSignInAsync(VivariumUser user) => user.IsEnabled
        && (user.Id != VivariumIdentityConstants.SuperuserId || configuration.GetValue<bool>("Vivarium:Identity:SuperuserEnabled"))
        && await base.CanSignInAsync(user);

    public override async Task SignInWithClaimsAsync(VivariumUser user,
        AuthenticationProperties? authenticationProperties, IEnumerable<Claim> additionalClaims)
    {
        // The stock password/external/2FA pipeline reaches this hook only for the application
        // sign-in. Commit its authorization audit before the framework discloses the cookie.
        await using (var transaction = await database.Database.BeginTransactionAsync())
        {
            audits.Stage(AuditEventDraft.Create(requestContext.ForUser(user.Id, "identity-sign-in", "identity"),
                time.GetUtcNow(), "authentication.authorize_cookie", "user", user.Id.ToString()));
            await database.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        await base.SignInWithClaimsAsync(user, authenticationProperties, additionalClaims);
    }
}
