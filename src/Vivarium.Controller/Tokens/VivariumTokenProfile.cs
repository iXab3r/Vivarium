using System.Globalization;
using System.Security.Claims;
using Vivarium.Controller.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;

namespace Vivarium.Controller.Tokens;

/// <summary>The single native-client token profile; business rights are never token claims.</summary>
internal static class VivariumTokenProfile
{
    public const string Client = "vivarium-token-ui";
    public const string Audience = "vivarium-api";
    public const string Scope = "vivarium_api";
    public const string OrdinaryUserPolicy = "VivariumTokenUser";
    public const string CreationPolicy = "VivariumTokenCreation";
    public const string AuthorizationEndpoint = "VivariumTokenAuthorization";
    public const string RedemptionEndpoint = "VivariumTokenRedemption";
    public const string InitiatingUser = "vivarium.initiatingUserId";
    public const string Label = "vivarium.label";
    public const string Lifetime = "vivarium.lifetimeDays";
    public const string CodeLabel = "oi_vivarium_label";

    public static bool TryInput(string? label, string? days, IConfiguration configuration,
        out string normalizedLabel, out int lifetime)
    {
        normalizedLabel = label?.Trim() ?? "";
        var cap = configuration.GetValue<int>("Vivarium:Identity:TokenLifetimeDays");
        lifetime = cap;
        return cap is >= 1 and <= 365 && normalizedLabel.Length <= 128
            && !normalizedLabel.Any(char.IsControl)
            && (string.IsNullOrEmpty(days) || int.TryParse(days, NumberStyles.None,
                CultureInfo.InvariantCulture, out lifetime)) && lifetime >= 1 && lifetime <= cap;
    }

    public static async Task<VivariumUser?> CurrentUserAsync(HttpContext context, UserManager<VivariumUser> users)
    {
        var ticket = await context.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        if (!ticket.Succeeded || ticket.Principal is null) return null;
        var user = await users.GetUserAsync(ticket.Principal);
        return user is { IsEnabled: true } && user.Id != VivariumIdentityConstants.SuperuserId ? user : null;
    }

    public static string UserId(ClaimsPrincipal principal) => principal.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("A current local user is required for token admission.");
}
