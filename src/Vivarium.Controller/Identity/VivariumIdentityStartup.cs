using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using OpenIddict.Client;
using OpenIddict.Server;
using Vivarium.Controller.Persistence;
using Vivarium.Controller.Auditing;

namespace Vivarium.Controller.Identity;

internal static class VivariumIdentityStartup
{
    public static async Task InitializeVivariumIdentityAsync(this IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var configuration = services.GetRequiredService<IConfiguration>();
        _ = VivariumIdentityRegistration.PublicOrigin(configuration);
        _ = services.GetRequiredService<IOptions<KeyManagementOptions>>().Value;
        _ = services.GetRequiredService<IDataProtectionProvider>().CreateProtector("Vivarium.Startup")
            .Protect(Array.Empty<byte>());
        _ = services.GetRequiredService<IOptions<OpenIddictServerOptions>>().Value;
        _ = services.GetRequiredService<IOptions<OpenIddictClientOptions>>().Value;

        var database = services.GetRequiredService<VivariumDbContext>();
        var users = services.GetRequiredService<UserManager<VivariumUser>>();
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var enabled = configuration.GetValue<bool>("Vivarium:Identity:SuperuserEnabled");
        var password = enabled ? "aA1!" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) : null;
        var user = await users.FindByIdAsync(VivariumIdentityConstants.SuperuserId.ToString());
        if (user is null)
        {
            user = new VivariumUser
            {
                Id = VivariumIdentityConstants.SuperuserId,
                UserName = VivariumIdentityConstants.SuperuserName,
                DisplayName = "Installation superuser",
                IsEnabled = enabled,
            };
            Require(password is null ? await users.CreateAsync(user) : await users.CreateAsync(user, password));
        }
        else
        {
            if (user.UserName != VivariumIdentityConstants.SuperuserName)
                throw new InvalidDataException("The reserved installation identity is inconsistent.");
            user.IsEnabled = enabled;
            Require(await users.UpdateAsync(user));
            if (password is not null)
                Require(await users.ResetPasswordAsync(user, await users.GeneratePasswordResetTokenAsync(user), password));
            else Require(await users.UpdateSecurityStampAsync(user));
            Require(await users.SetLockoutEndDateAsync(user, null));
            Require(await users.ResetAccessFailedCountAsync(user));
        }
        services.GetRequiredService<PostgresAuditStore>().Stage(AuditEventDraft.Create(
            services.GetRequiredService<RuntimeRequestContext>().ForSystem("identity-startup"),
            services.GetRequiredService<TimeProvider>().GetUtcNow(), "account.installation.configure", "user",
            user.Id.ToString(), details: new Dictionary<string, string> { ["enabled"] = enabled ? "true" : "false" }));
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        // D34's explicit private startup disclosure, after the framework hash is durably committed.
        if (password is not null)
            services.GetRequiredService<ILoggerFactory>().CreateLogger("Vivarium.PrivateStartup")
                .LogWarning("Installation superuser login: superuser; password: {SuperuserPassword}", password);
    }

    private static void Require(IdentityResult result)
    {
        if (!result.Succeeded) throw new InvalidOperationException("Identity installation initialization failed.");
    }
}
