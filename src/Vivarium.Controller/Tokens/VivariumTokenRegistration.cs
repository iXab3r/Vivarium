using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Client;
using OpenIddict.Server;
using static OpenIddict.Abstractions.OpenIddictConstants;
using Microsoft.EntityFrameworkCore;
using Vivarium.Controller.Auditing;
using Vivarium.Controller.Identity;
using Vivarium.Controller.Persistence;

namespace Vivarium.Controller.Tokens;

/// <summary>Extends the shared framework stack with the fixed first-party native token client.</summary>
internal static class VivariumTokenRegistration
{
    public static IServiceCollection AddVivariumTokens(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<TokenIssuanceTransaction>();
        services.AddSingleton<IAuthorizationHandler, TokenUserRequirement>();
        services.AddAuthorization(options => options.AddPolicy(VivariumTokenProfile.OrdinaryUserPolicy,
            policy => policy.AddAuthenticationSchemes(IdentityConstants.ApplicationScheme)
                .RequireAuthenticatedUser().AddRequirements(new TokenUserRequirement())));
        services.AddRateLimiter(_ => { });
        services.AddOptions<RateLimiterOptions>().Configure<IConfiguration>((options, settings) =>
        {
            var node = settings.GetSection("Vivarium:Identity");
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                var endpoint = context.GetEndpoint()?.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName;
                if (endpoint == VivariumTokenProfile.AuthorizationEndpoint)
                    return RateLimitPartition.GetConcurrencyLimiter("authorize:" + VivariumTokenProfile.UserId(context.User),
                        _ => new ConcurrencyLimiterOptions
                        {
                            PermitLimit = node.GetValue<int>("TokenCreationConcurrencyLimit"),
                            QueueLimit = node.GetValue<int>("TokenCreationQueueLimit"),
                            QueueProcessingOrder = QueueProcessingOrder.OldestFirst
                        });
                if (endpoint == VivariumTokenProfile.RedemptionEndpoint)
                    return RateLimitPartition.GetConcurrencyLimiter("redemption", _ => new ConcurrencyLimiterOptions
                    {
                        PermitLimit = node.GetValue<int>("TokenRedemptionConcurrencyLimit"), QueueLimit = 0
                    });
                return RateLimitPartition.GetNoLimiter("other");
            });
            options.AddPolicy(VivariumTokenProfile.CreationPolicy, context =>
                RateLimitPartition.GetFixedWindowLimiter(VivariumTokenProfile.UserId(context.User), _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = node.GetValue<int>("TokenCreationPermitLimit"),
                    Window = TimeSpan.FromSeconds(node.GetValue<int>("TokenCreationWindowSeconds")),
                    QueueLimit = node.GetValue<int>("TokenCreationQueueLimit"),
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    AutoReplenishment = true
                }));
        });
        services.AddOpenIddict().AddValidation(options => options.UseAspNetCore()
            .DisableAccessTokenExtractionFromBodyForm().DisableAccessTokenExtractionFromQueryString())
        .AddServer(options =>
        {
            options.AllowAuthorizationCodeFlow().RequireProofKeyForCodeExchange()
                .DisableAccessTokenEncryption()
                .SetConfigurationEndpointUris("/.well-known/openid-configuration")
                .SetJsonWebKeySetEndpointUris("/.well-known/jwks")
                .SetAuthorizationEndpointUris("/connect/authorize").SetTokenEndpointUris("/connect/token")
                .RegisterScopes(VivariumTokenProfile.Scope);
            options.UseAspNetCore().EnableAuthorizationEndpointPassthrough().EnableTokenEndpointPassthrough();
            // Preserve stock code consumption on a later failure; only new entry creation is
            // transactional with application audit. Orders follow the pinned 7.5.0 pipeline.
            options.AddEventHandler<OpenIddictServerEvents.ProcessSignInContext>(handler => handler
                .UseScopedHandler<BeginTokenIssuance>()
                .SetOrder(OpenIddictServerHandlers.RedeemTokenEntry.Descriptor.Order + 500));
            options.AddEventHandler<OpenIddictServerEvents.ProcessSignInContext>(handler => handler
                .UseScopedHandler<PersistTokenLabel>()
                .SetOrder(OpenIddictServerHandlers.GenerateAccessToken.Descriptor.Order + 500));
            options.AddEventHandler<OpenIddictServerEvents.ProcessSignInContext>(handler => handler
                .UseScopedHandler<CommitTokenIssuance>()
                .SetOrder(OpenIddictServerHandlers.AttachSignInParameters.Descriptor.Order - 500));
        }).AddClient(options =>
        {
            options.AddEventHandler<OpenIddictClientEvents.ProcessAuthenticationContext>(handler => handler
                .UseInlineHandler(context =>
                {
                    if (context.Registration?.RegistrationId == VivariumTokenProfile.Client)
                        context.SendUserInfoRequest = false;
                    return default;
                }).SetOrder(OpenIddictClientHandlers.EvaluateUserInfoRequest.Descriptor.Order + 500));
            options.AddEventHandler<OpenIddictClientEvents.ProcessChallengeContext>(handler => handler
                .UseInlineHandler(context =>
                {
                    if (context.Registration?.RegistrationId == VivariumTokenProfile.Client)
                    {
                        context.Parameters["vivarium_label"] = context.Properties.GetValueOrDefault(VivariumTokenProfile.Label);
                        context.Parameters["vivarium_lifetime_days"] = context.Properties.GetValueOrDefault(VivariumTokenProfile.Lifetime);
                    }
                    return default;
                }).SetOrder(OpenIddictClientHandlers.AttachCustomChallengeParameters.Descriptor.Order - 500));
        });
        services.AddOptions<OpenIddictClientOptions>().Configure<IConfiguration>((options, settings) =>
        {
            var origin = new Uri(settings["Vivarium:PublicOrigin"]!);
            var registration = new OpenIddictClientRegistration
            {
                RegistrationId = VivariumTokenProfile.Client, ClientId = VivariumTokenProfile.Client,
                ClientType = ClientTypes.Confidential,
                Issuer = origin, RedirectUri = new Uri(origin, "/signin-vivarium-token")
            };
            registration.ClientAuthenticationMethods.Add(ClientAuthenticationMethods.PrivateKeyJwt);
            registration.SigningCredentials.AddRange(options.SigningCredentials);
            registration.CodeChallengeMethods.Add(CodeChallengeMethods.Sha256);
            registration.Scopes.Add(VivariumTokenProfile.Scope);
            options.Registrations.Add(registration);
        });
        return services;
    }

    public static async Task InitializeVivariumTokensAsync(this IServiceProvider services, CancellationToken cancellationToken)
    {
        var database = services.GetRequiredService<VivariumDbContext>();
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var configuration = services.GetRequiredService<IConfiguration>();
        var applications = services.GetRequiredService<IOpenIddictApplicationManager>();
        var client = services.GetRequiredService<IOptionsMonitor<OpenIddictClientOptions>>().CurrentValue;
        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = VivariumTokenProfile.Client, ClientType = ClientTypes.Confidential,
            ConsentType = ConsentTypes.Implicit, DisplayName = "Vivarium token UI", JsonWebKeySet = new JsonWebKeySet()
        };
        foreach (var credential in client.SigningCredentials)
        {
            // Certificate conversion exports public x5c/key material only.
            var key = JsonWebKeyConverter.ConvertFromX509SecurityKey((X509SecurityKey)credential.Key);
            descriptor.JsonWebKeySet.Keys.Add(key);
        }
        if (descriptor.JsonWebKeySet.Keys.Count == 0)
            throw new InvalidOperationException("Persistent client signing credentials are required.");
        descriptor.RedirectUris.Add(new Uri(new Uri(configuration["Vivarium:PublicOrigin"]!), "/signin-vivarium-token"));
        descriptor.Permissions.UnionWith([
            Permissions.Endpoints.Authorization, Permissions.Endpoints.Token, Permissions.GrantTypes.AuthorizationCode,
            Permissions.ResponseTypes.Code, Permissions.Prefixes.Scope + VivariumTokenProfile.Scope
        ]);
        descriptor.Requirements.Add(Requirements.Features.ProofKeyForCodeExchange);
        var application = await applications.FindByClientIdAsync(VivariumTokenProfile.Client, cancellationToken);
        if (application is null) await applications.CreateAsync(descriptor, cancellationToken);
        else await applications.UpdateAsync(application, descriptor, cancellationToken);
        services.GetRequiredService<PostgresAuditStore>().Stage(AuditEventDraft.Create(
            services.GetRequiredService<RuntimeRequestContext>().ForSystem("identity-startup"),
            services.GetRequiredService<TimeProvider>().GetUtcNow(), "oauth-client.configure", "oauth-client",
            VivariumTokenProfile.Client));
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
