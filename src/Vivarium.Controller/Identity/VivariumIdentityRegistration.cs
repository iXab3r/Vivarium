using Vivarium.Controller.Persistence;
using Vivarium.Controller.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Client;
using OpenIddict.Server;
using static OpenIddict.Abstractions.OpenIddictConstants;
using Vivarium.Controller.Auditing;

namespace Vivarium.Controller.Identity;

/// <summary>Stock Identity and shared OpenIddict stacks; configuration is resolved only after Chiral readiness.</summary>
public static class VivariumIdentityRegistration
{
    /// <summary>Register the single framework identity stack without reading unrestored node credentials.</summary>
    public static IServiceCollection AddVivariumIdentity(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<RuntimeRequestContext>();
        services.AddScoped<PostgresAuditStore>();
        services.AddScoped<CurrentAccess>();
        services.AddScoped<RuntimeIdentity>();
        services.AddScoped<TeamCityAccessService>();
        services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = IdentityConstants.ApplicationScheme;
            options.DefaultChallengeScheme = IdentityConstants.ApplicationScheme;
            options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
        }).AddIdentityCookies();
        services.AddIdentityCore<VivariumUser>()
            .AddEntityFrameworkStores<VivariumDbContext>().AddDefaultTokenProviders()
            .AddSignInManager<VivariumSignInManager>();
        services.ConfigureApplicationCookie(options =>
        {
            options.LoginPath = "/Identity/Account/Login";
            options.ExpireTimeSpan = TimeSpan.FromDays(14);
            options.SlidingExpiration = true;
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Events.OnRedirectToLogin = context =>
            {
                if (context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/app/rest"))
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                else context.Response.Redirect(context.RedirectUri);
                return Task.CompletedTask;
            };
            options.Events.OnRedirectToAccessDenied = context =>
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            };
        });
        services.AddAntiforgery(options => options.HeaderName = "RequestVerificationToken");
        services.AddRazorPages(options => options.RootDirectory = "/");
        services.AddTransient<IStartupFilter, VivariumIdentityPageStartupFilter>();
        services.AddDataProtection().SetApplicationName("Vivarium");
        services.AddOptions<KeyManagementOptions>().Configure<IConfiguration, ILoggerFactory>((options, settings, logger) =>
        {
            var directory = settings["Vivarium:Identity:DataProtectionDirectory"];
            if (directory is null || !Path.IsPathFullyQualified(directory))
                throw new InvalidOperationException("An absolute persistent Data Protection directory is required.");
            options.XmlRepository = new FileSystemXmlRepository(new DirectoryInfo(directory), logger);
        });
        services.AddSingleton<VivariumIdentityKeys>();
        services.AddOpenIddict()
            // Framework writes share application audit transactions. A rolled-back transaction
            // must never leave a positive/revoked entry in an out-of-transaction entity cache.
            .AddCore(options => options.DisableEntityCaching().UseEntityFrameworkCore().UseDbContext<VivariumDbContext>())
            .AddServer(options => options.UseAspNetCore())
            .AddClient(options =>
            {
                options.AllowAuthorizationCodeFlow();
                options.UseSystemNetHttp();
                options.UseAspNetCore().EnableRedirectionEndpointPassthrough();
            })
            .AddValidation(options =>
            {
                options.UseLocalServer();
                options.AddAudiences("vivarium-api");
                options.EnableTokenEntryValidation();
                options.UseAspNetCore();
            });
        services.AddOptions<OpenIddictServerOptions>().Configure<VivariumIdentityKeys, IConfiguration>((options, keys, settings) =>
        {
            options.Issuer = PublicOrigin(settings);
            options.SigningCredentials.Add(new SigningCredentials(new X509SecurityKey(keys.ServerSigning), SecurityAlgorithms.RsaSha256));
            options.EncryptionCredentials.Add(new EncryptingCredentials(new X509SecurityKey(keys.ServerEncryption), SecurityAlgorithms.RsaOAEP, SecurityAlgorithms.Aes256CbcHmacSha512));
        });
        services.AddOptions<OpenIddictClientOptions>().Configure<VivariumIdentityKeys, IConfiguration>((options, keys, settings) =>
        {
            options.ClientUri = PublicOrigin(settings);
            options.DisableWebServicesFederationClaimMapping = true;
            options.SigningCredentials.Add(new SigningCredentials(new X509SecurityKey(keys.ClientSigning), SecurityAlgorithms.RsaSha256));
            options.EncryptionCredentials.Add(new EncryptingCredentials(new X509SecurityKey(keys.ClientEncryption), SecurityAlgorithms.RsaOAEP, SecurityAlgorithms.Aes256CbcHmacSha512));
            foreach (var provider in settings.GetSection("Vivarium:Identity:Providers").GetChildren())
            {
                var key = provider["Key"]!;
                var registration = new OpenIddictClientRegistration
                {
                    RegistrationId = "external:" + key,
                    ProviderName = key,
                    Issuer = new Uri(provider["Issuer"]!, UriKind.Absolute),
                    ClientId = provider["ClientId"],
                    ClientSecret = provider["ClientSecret"],
                    RedirectUri = new Uri(PublicOrigin(settings), "signin-oidc/" + key)
                };
                registration.ClientAuthenticationMethods.UnionWith([ClientAuthenticationMethods.ClientSecretBasic, ClientAuthenticationMethods.ClientSecretPost]);
                registration.CodeChallengeMethods.Add(CodeChallengeMethods.Sha256);
                registration.Scopes.Add(Scopes.OpenId);
                foreach (var scope in provider.GetSection("Scopes").GetChildren()) registration.Scopes.Add(scope.Value!);
                options.Registrations.Add(registration);
            }
        });
        services.AddScoped<VivariumExternalUsers>();
        return services;
    }

    // Call after all framework registrations: the optional DP preload service resolves its key ring
    // in its constructor, before any hosted service can restore Chiral. Keep the stock provider/ring;
    // our existing post-Chiral startup performs that same preload before admission instead.
    internal static void DeferVivariumIdentityPreload(this IServiceCollection services)
    {
        var preload = services.Single(descriptor => descriptor.ServiceType == typeof(IHostedService)
            && descriptor.ImplementationType?.FullName ==
                "Microsoft.AspNetCore.DataProtection.Internal.DataProtectionHostedService");
        services.Remove(preload);
    }

    internal static Uri PublicOrigin(IConfiguration configuration)
    {
        if (!Uri.TryCreate(configuration["Vivarium:PublicOrigin"], UriKind.Absolute, out var origin)
            || origin.Scheme != Uri.UriSchemeHttps || origin.UserInfo.Length != 0 || origin.AbsolutePath != "/"
            || origin.Query.Length != 0 || origin.Fragment.Length != 0)
            throw new InvalidOperationException("Identity requires a canonical HTTPS public origin.");
        return origin;
    }

    /// <summary>Map external callbacks; common account pages join shared routing after Chiral startup.</summary>
    public static void MapVivariumIdentity(this WebApplication app)
    {
        app.MapMethods("/signin-oidc/{providerKey}", ["GET", "POST"], VivariumExternalCallback.CompleteAsync);
    }
}
