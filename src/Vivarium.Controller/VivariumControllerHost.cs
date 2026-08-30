using System.Net;
using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Vivarium.Contracts.V1;
using Vivarium.Controller.Administration;
using Vivarium.Controller.Agents;
using Vivarium.Controller.Auditing;
using Vivarium.Controller.Blobs;
using Vivarium.Controller.Blobs.Access;
using Vivarium.Controller.Builds;
using Vivarium.Controller.Components;
using Vivarium.Controller.Configuration.Agents;
using Vivarium.Controller.Configuration.Git;
using Vivarium.Controller.Configuration.Reconciliation;
using Vivarium.Controller.Deployment;
using Vivarium.Controller.Management;
using Vivarium.Controller.Persistence;
using Vivarium.Controller.Rest.Agents;
using Vivarium.Controller.Rest.Agents.Configuration;
using Vivarium.Controller.Rest.Administration;
using Vivarium.Controller.Rest.Audit;
using Vivarium.Controller.Rest.Builds;
using Vivarium.Controller.Rest.Builds.Mutations;
using Vivarium.Controller.Rest.Blobs;
using Vivarium.Controller.Rest.Common;
using Vivarium.Controller.Rest.Events;
using Vivarium.Controller.Rest.Deployment;
using Vivarium.Controller.Rest.OpenApi;
using Vivarium.Controller.Rest.System;
using Vivarium.Controller.ResultAdapters.Trx;
using Vivarium.Controller.Scheduling;
using Vivarium.Controller.Security;

namespace Vivarium.Controller;

/// <summary>
/// The controller as a startable component: Program.cs runs it, the tier-2 test suite hosts it
/// in-process on a loopback port (D20).
/// </summary>
public sealed class VivariumControllerHost : IAsyncDisposable
{
    private readonly WebApplication app;

    public ControllerCertificate Certificate { get; }
    public TokenStore Tokens { get; }
    public AdministrationBootstrapService AdministrationBootstrap { get; }
    public ManagementAuthorizer ManagementAuthorizer { get; }
    public ManagementRequestContextFactory ManagementContexts { get; }
    public UserCredentialService UserCredentials { get; }
    public AuditEventStore Audits { get; }
    public VivariumDatabase Database { get; }
    public ManagedGitRepository ConfigurationRepository { get; }
    public ConfigurationReconciler ConfigurationReconciler { get; }
    public IAgentDesiredConfigurationService AgentDesiredConfiguration { get; }
    public AgentStore AgentStore { get; }
    public AgentLifecycleCoordinator AgentLifecycle { get; }
    public AgentRegistry Registry { get; }
    public AgentAdministration AgentAdministration { get; }
    public BuildStore BuildStore { get; }
    public BuildQueueStore BuildQueueStore { get; }
    public BuildQueueService BuildQueue { get; }
    public BuildTracker Builds { get; }
    public MatrixBuildStore MatrixBuildStore { get; }
    public MatrixBuildSubmissionService MatrixBuildSubmissions { get; }
    public MatrixBuildCancellationService MatrixBuildCancellations { get; }
    public BlobStore Blobs { get; }
    public TrxProjectionStore TrxProjections { get; }
    public TrxProjectionService TrxProjectionService { get; }
    public AgentPackageStore AgentPackages { get; }
    public AgentUpgradeStore AgentUpgradeStore { get; }
    public AgentUpgradeService AgentUpgrades { get; }
    public string Url { get; }

    private VivariumControllerHost(
        WebApplication app,
        ControllerCertificate certificate,
        VivariumDatabase database,
        TokenStore tokens,
        AdministrationBootstrapService administrationBootstrap,
        ManagementAuthorizer managementAuthorizer,
        ManagementRequestContextFactory managementContexts,
        UserCredentialService userCredentials,
        AuditEventStore audits,
        ManagedGitRepository configurationRepository,
        ConfigurationReconciler configurationReconciler,
        IAgentDesiredConfigurationService agentDesiredConfiguration,
        AgentStore agentStore,
        AgentLifecycleCoordinator agentLifecycle,
        AgentRegistry registry,
        AgentAdministration agentAdministration,
        BuildStore buildStore,
        BuildQueueStore buildQueueStore,
        BuildQueueService buildQueue,
        BuildTracker builds,
        MatrixBuildStore matrixBuildStore,
        MatrixBuildSubmissionService matrixBuildSubmissions,
        MatrixBuildCancellationService matrixBuildCancellations,
        BlobStore blobs,
        TrxProjectionStore trxProjections,
        TrxProjectionService trxProjectionService,
        AgentPackageStore agentPackages,
        AgentUpgradeStore agentUpgradeStore,
        AgentUpgradeService agentUpgrades,
        string url)
    {
        this.app = app;
        Certificate = certificate;
        Database = database;
        Tokens = tokens;
        AdministrationBootstrap = administrationBootstrap;
        ManagementAuthorizer = managementAuthorizer;
        ManagementContexts = managementContexts;
        UserCredentials = userCredentials;
        Audits = audits;
        ConfigurationRepository = configurationRepository;
        ConfigurationReconciler = configurationReconciler;
        AgentDesiredConfiguration = agentDesiredConfiguration;
        AgentStore = agentStore;
        AgentLifecycle = agentLifecycle;
        Registry = registry;
        AgentAdministration = agentAdministration;
        BuildStore = buildStore;
        BuildQueueStore = buildQueueStore;
        BuildQueue = buildQueue;
        Builds = builds;
        MatrixBuildStore = matrixBuildStore;
        MatrixBuildSubmissions = matrixBuildSubmissions;
        MatrixBuildCancellations = matrixBuildCancellations;
        Blobs = blobs;
        TrxProjections = trxProjections;
        TrxProjectionService = trxProjectionService;
        AgentPackages = agentPackages;
        AgentUpgradeStore = agentUpgradeStore;
        AgentUpgrades = agentUpgrades;
        Url = url;
    }

    public static async Task<VivariumControllerHost> StartAsync(ControllerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.BuildQueueWaitTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options.BuildQueueWaitTimeout), "build queue wait timeout must be positive");
        }

        PrivateStorage.EnsureDirectory(options.DataDir);
        var certificate = ControllerCertificate.LoadOrCreate(options.DataDir);
        var database = new VivariumDatabase(options.DataDir);
        var tokens = new TokenStore(options.DataDir, database);
        var managementAuthorizer = new ManagementAuthorizer(database);
        var managementContexts = new ManagementRequestContextFactory(tokens);
        var audits = new AuditEventStore(database);
        var userCredentials = new UserCredentialService(database, audits, options.TimeProvider);
        var managementCommands = new ManagementCommandAuthorizer(
            managementAuthorizer, audits, options.TimeProvider);
        var agentStore = new AgentStore(database);
        var agentOperationalStore = new AgentOperationalStore(database);
        var configurationRepository = await ManagedGitRepository.OpenOrCreateAsync(
            Path.Combine(options.DataDir, "configuration"),
            "controller");
        var configurationReconciler = new ConfigurationReconciler(
            database,
            options.TimeProvider);
        await configurationReconciler.ReconcileAuthoritativeHeadAsync(
            ManagementRequestContext.System("startup-configuration-reconciliation"),
            "controller",
            configurationRepository);
        var administrationBootstrap = new AdministrationBootstrapService(
            database,
            configurationRepository,
            configurationReconciler,
            audits,
            options.TimeProvider);
        await administrationBootstrap.InitializeAsync();
        var agentLifecycle = new AgentLifecycleCoordinator();
        var registry = new AgentRegistry(
            agentStore, options.TimeProvider, agentOperationalStore);
        var agentConfigurationActivation = new AgentDesiredConfigurationActivationSink(registry);
        var agentDesiredConfiguration = new AgentDesiredConfigurationService(
            configurationRepository,
            configurationReconciler,
            agentStore,
            agentLifecycle,
            managementCommands,
            agentConfigurationActivation);
        var blobs = new BlobStore(Path.Combine(options.DataDir, "blobs"));
        var blobAccessStore = new BlobAccessStore(database);
        var blobAccess = new BlobAccessService(
            blobAccessStore,
            blobs,
            managementCommands,
            audits,
            options.TimeProvider);
        var trxProjections = new TrxProjectionStore(database);
        var trxProjectionService = new TrxProjectionService(
            trxProjections,
            blobs,
            options.TimeProvider);
        await trxProjectionService.ReconcilePendingAsync();
        var agentPackages = new AgentPackageStore(
            options.DataDir,
            database,
            options.TimeProvider,
            options.EnableDevelopmentAgentPackageApi);
        if (!string.IsNullOrWhiteSpace(options.AgentPackageCatalogPath))
        {
            await agentPackages.ImportBundledCatalogAsync(
                options.AgentPackageCatalogPath,
                AgentHubService.ServerVersion);
        }
        var agentUpgradeStore = new AgentUpgradeStore(database, options.TimeProvider);
        var agentRestartStore = new AgentRestartStore(database);
        var buildStore = new BuildStore(database, blobAccessStore);
        var buildQueueStore = new BuildQueueStore(database);
        await buildQueueStore.InitializeQueueDeadlinesAsync(options.BuildQueueWaitTimeout);
        await buildQueueStore.ExpireDueAsync(options.TimeProvider.GetUtcNow());
        var buildQueue = new BuildQueueService(
            buildQueueStore,
            registry,
            options.TimeProvider,
            options.BuildQueueWaitTimeout,
            managementCommands);
        var builds = new BuildTracker(
            registry,
            buildStore,
            buildQueueStore,
            options.TimeProvider,
            options.AgentReconnectGrace,
            managementCommands,
            trxProjectionService,
            options.BuildGracefulStopTimeout,
            options.BuildForceStopTimeout,
            options.BuildAssignmentAckTimeout);
        await builds.InitializeAsync();
        var agentAdministration = new AgentAdministration(
            registry,
            agentStore,
            buildStore,
            tokens,
            agentLifecycle,
            options.TimeProvider,
            managementCommands,
            agentDesiredConfiguration);
        var matrixBuildStore = new MatrixBuildStore(database);
        var databaseChanges = new DatabaseChangeNotifier(database);
        var matrixBuildSubmissions = new MatrixBuildSubmissionService(
            matrixBuildStore,
            agentStore,
            blobs,
            buildQueue,
            options.TimeProvider,
            options.BuildQueueWaitTimeout,
            managementCommands);
        var matrixBuildCancellations = new MatrixBuildCancellationService(
            matrixBuildStore, builds, buildQueue, options.TimeProvider, managementCommands);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(VivariumControllerHost).Assembly.FullName,
        });
        var panelIdentity = CreatePanelIdentity(options.DataDir);
        builder.WebHost.UseStaticWebAssets();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddSingleton(certificate);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(options.TimeProvider);
        builder.Services.AddSingleton(database);
        builder.Services.AddSingleton(tokens);
        builder.Services.AddSingleton(administrationBootstrap);
        builder.Services.AddSingleton(managementAuthorizer);
        builder.Services.AddSingleton(managementContexts);
        builder.Services.AddSingleton(userCredentials);
        builder.Services.AddSingleton(audits);
        builder.Services.AddSingleton(managementCommands);
        builder.Services.AddSingleton(configurationRepository);
        builder.Services.AddSingleton<IConfigurationRepository>(configurationRepository);
        builder.Services.AddSingleton(configurationReconciler);
        builder.Services.AddSingleton(agentStore);
        builder.Services.AddSingleton(agentOperationalStore);
        builder.Services.AddSingleton(agentLifecycle);
        builder.Services.AddSingleton(registry);
        builder.Services.AddSingleton<IAgentDesiredConfigurationActivationSink>(
            agentConfigurationActivation);
        builder.Services.AddSingleton(agentDesiredConfiguration);
        builder.Services.AddSingleton<IAgentDesiredConfigurationService>(agentDesiredConfiguration);
        builder.Services.AddSingleton(agentAdministration);
        builder.Services.AddSingleton<AgentConfigurationReconciliationMonitor>();
        builder.Services.AddSingleton(buildStore);
        builder.Services.AddSingleton(buildQueueStore);
        builder.Services.AddSingleton(buildQueue);
        builder.Services.AddSingleton(builds);
        builder.Services.AddSingleton(blobs);
        builder.Services.AddSingleton(blobAccessStore);
        builder.Services.AddSingleton<IBlobBuildAttachmentParticipant>(blobAccessStore);
        builder.Services.AddSingleton<IBlobArtifactAttachmentParticipant>(blobAccessStore);
        builder.Services.AddSingleton(blobAccess);
        builder.Services.AddSingleton<IBlobObjectAccess>(blobAccess);
        builder.Services.AddSingleton(trxProjections);
        builder.Services.AddSingleton(trxProjectionService);
        builder.Services.AddSingleton(agentPackages);
        builder.Services.AddSingleton(agentUpgradeStore);
        builder.Services.AddSingleton(agentRestartStore);
        builder.Services.AddSingleton<AgentRestartService>();
        builder.Services.AddSingleton<AgentUpgradeService>();
        builder.Services.AddSingleton<IBuildResultProjectionParticipant>(trxProjectionService);
        builder.Services.AddSingleton(matrixBuildStore);
        builder.Services.AddSingleton(databaseChanges);
        builder.Services.AddSingleton(matrixBuildSubmissions);
        builder.Services.AddSingleton(matrixBuildCancellations);
        builder.Services.AddGrpc();
        builder.Services.AddHostedService(
            services => services.GetRequiredService<AgentConfigurationReconciliationMonitor>());
        builder.Services.AddHostedService<AgentHeartbeatMonitor>();
        builder.Services.AddSingleton<BuildQueueTimeoutMonitor>();
        builder.Services.AddHostedService(
            services => services.GetRequiredService<BuildQueueTimeoutMonitor>());
        builder.Services.AddSingleton<BuildScheduler>();
        builder.Services.AddHostedService(
            services => services.GetRequiredService<BuildScheduler>());
        builder.Services.AddHostedService(
            services => services.GetRequiredService<AgentUpgradeService>());
        builder.Services.AddHostedService(
            services => services.GetRequiredService<AgentRestartService>());
        builder.Services.AddDataProtection()
            .SetApplicationName($"Vivarium.Controller.Panel.{panelIdentity}");
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(cookie =>
            {
                cookie.LoginPath = "/login";
                cookie.Cookie.Name = $"vivarium.panel.{panelIdentity[..16]}";
                cookie.Cookie.HttpOnly = true;
                cookie.Cookie.SameSite = SameSiteMode.Strict;
                cookie.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                cookie.ExpireTimeSpan = TimeSpan.FromHours(12);
                cookie.SlidingExpiration = true;
            });
        builder.Services.AddAuthorization();
        builder.Services.AddCascadingAuthenticationState();
        builder.Services.AddScoped<PanelManagementContext>();
        builder.Services.AddVivariumRestApi();
        builder.Services.AddAgentRestApi();
        builder.Services.AddAgentRestartApi();
        builder.Services.AddAgentDesiredConfigurationRestApi();
        builder.Services.AddAdministrationSetupApi();
        builder.Services.AddAuditRestApi();
        builder.Services.AddBlobAccessApi();
        builder.Services.AddVivariumBuildApi();
        builder.Services.AddVivariumBuildMutationApi();
        builder.Services.AddVivariumBuildEventApi();
        builder.Services.AddAgentDeploymentApi();
        builder.Services.AddRazorComponents().AddInteractiveServerComponents();

        var address = options.Host == "0.0.0.0" ? IPAddress.Any : IPAddress.Parse(options.Host);
        builder.WebHost.ConfigureKestrel(kestrel =>
            kestrel.Listen(address, options.Port, listen =>
            {
                listen.Protocols = HttpProtocols.Http1AndHttp2;
                listen.UseHttps(certificate.Certificate);
            }));

        var app = builder.Build();
        app.UseStaticFiles();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseVivariumRestApi();
        app.UseAntiforgery();
        app.MapGrpcService<AgentHubService>();
        app.MapGrpcService<ControlPlaneService>();
        app.MapObjectScopedBlobDataPlane();
        MapPanelLogin(app);
        MapPanelArtifactEndpoints(app);
        app.MapVivariumSystemApi();
        app.MapVivariumOpenApi();
        app.MapAgentRestApi();
        app.MapAgentRestartApi();
        app.MapAgentDesiredConfigurationRestApi();
        app.MapAdministrationSetupApi();
        app.MapAuditRestApi();
        app.MapBlobAccessApi();
        app.MapVivariumBuildApi();
        app.MapVivariumBuildMutationApi();
        app.MapVivariumBuildEventApi();
        app.MapAgentDeploymentApi();
        app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode()
            .RequireAuthorization();

        await app.StartAsync();
        await builds.ArmStartupReconnectGraceAsync();

        var boundAddress = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses.First();
        // Kestrel reports e.g. https://0.0.0.0:8443 — keep it usable for local clients.
        var url = boundAddress.Replace("0.0.0.0", "127.0.0.1").Replace("[::]", "127.0.0.1");

        var agentUpgrades = app.Services.GetRequiredService<AgentUpgradeService>();

        return new VivariumControllerHost(
            app, certificate, database, tokens, administrationBootstrap,
            managementAuthorizer, managementContexts, userCredentials, audits,
            configurationRepository, configurationReconciler, agentDesiredConfiguration,
            agentStore, agentLifecycle, registry, agentAdministration,
            buildStore, buildQueueStore, buildQueue, builds, matrixBuildStore,
            matrixBuildSubmissions, matrixBuildCancellations, blobs,
            trxProjections, trxProjectionService,
            agentPackages, agentUpgradeStore, agentUpgrades, url);
    }

    /// <summary>The Authorize click (D7): issue a token and deliver it over the live session.</summary>
    public Task AuthorizeAgentAsync(string agentId) =>
        AgentAdministration.AuthorizeFromControllerAsync(agentId);

    /// <summary>Force-drop an agent's live session; it reconnects and re-hellos (D4).</summary>
    public void KickAgent(string agentId) => Registry.Get(agentId)?.SessionAbort?.Cancel();

    public Task WaitForShutdownAsync(CancellationToken ct = default) => app.WaitForShutdownAsync(ct);

    public async ValueTask DisposeAsync()
    {
        await app.StopAsync(TimeSpan.FromSeconds(5));
        await app.DisposeAsync();
        await Database.DisposeAsync();
    }

    private static void MapPanelLogin(WebApplication app)
    {
        app.MapGet("/login", (HttpContext context) =>
            context.User.Identity?.IsAuthenticated == true
                ? Results.Redirect("/agents")
                : Results.Content(PanelLogin.Render(invalid: false), "text/html"));

        app.MapPost("/login", async (
            HttpContext context,
            ManagementRequestContextFactory contexts,
            UserCredentialService userCredentials,
            ManagementAuthorizer authorizer,
            AuditEventStore audits,
            TimeProvider timeProvider) =>
        {
            var form = await context.Request.ReadFormAsync();
            var supplied = form["token"].ToString();
            var login = form["login"].ToString();
            var password = form["password"].ToString();
            var attemptedPassword = !string.IsNullOrWhiteSpace(login) || password.Length > 0;
            var suppliedCorrelationId =
                context.Request.Headers[ManagementRequestContextFactory.CorrelationHeader].ToString();
            ManagementRequestContext? requestContext;
            try
            {
                var correlationId = ManagementIdentifiers.NormalizeCorrelationId(suppliedCorrelationId);
                if (attemptedPassword)
                {
                    var principal = await userCredentials.AuthenticatePasswordAsync(
                        login,
                        password,
                        correlationId,
                        "panel-login",
                        context.RequestAborted);
                    requestContext = principal is null
                        ? null
                        : new ManagementRequestContext(
                            principal, correlationId, RequestId: null, "panel-login");
                }
                else
                {
                    requestContext = await contexts.FromBearerAsync(
                        $"Bearer {supplied}",
                        correlationId,
                        requestId: null,
                        source: "panel-login");
                }
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(exception.Message);
            }

            if (requestContext is null ||
                !authorizer.Allows(requestContext.Principal, ManagementPermission.PanelAccess))
            {
                var deniedContext = requestContext
                    ?? ManagementRequestContext.Anonymous("panel-login", suppliedCorrelationId);
                context.Response.Headers[ManagementRequestContextFactory.CorrelationHeader] =
                    deniedContext.CorrelationId;
                if (!attemptedPassword)
                {
                    await audits.AppendAsync(AuditEventDraft.Create(
                        deniedContext,
                        timeProvider.GetUtcNow(),
                        "security.authentication",
                        "panel-session",
                        "panel",
                        AuditOutcome.Denied,
                        "invalid_credentials"));
                }
                return Results.Content(PanelLogin.Render(invalid: true), "text/html", statusCode: 401);
            }

            context.Response.Headers[ManagementRequestContextFactory.CorrelationHeader] =
                requestContext.CorrelationId;
            if (!attemptedPassword)
            {
                await audits.AppendAsync(AuditEventDraft.Create(
                    requestContext,
                    timeProvider.GetUtcNow(),
                    "security.authentication",
                    "panel-session",
                    requestContext.Principal.ActorId));
            }
            await context.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                ManagementRequestContextFactory.CreateClaimsPrincipal(requestContext.Principal),
                new AuthenticationProperties { IsPersistent = true });
            return Results.Redirect("/agents");
        }).DisableAntiforgery();

        app.MapPost("/logout", async (
            HttpContext context,
            ManagementRequestContextFactory contexts,
            AuditEventStore audits,
            TimeProvider timeProvider) =>
        {
            var requestContext = contexts.FromClaims(
                context.User,
                context.Request.Headers[ManagementRequestContextFactory.CorrelationHeader].ToString(),
                requestId: null,
                source: "panel-logout");
            context.Response.Headers[ManagementRequestContextFactory.CorrelationHeader] =
                requestContext.CorrelationId;
            await audits.AppendAsync(AuditEventDraft.Create(
                requestContext,
                timeProvider.GetUtcNow(),
                "security.logout",
                "panel-session",
                requestContext.Principal.ActorId));
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Redirect("/login");
        }).RequireAuthorization().DisableAntiforgery();
    }

    private static void MapPanelArtifactEndpoints(WebApplication app)
    {
        app.MapGet(
            "/builds/{matrixId}/cells/{cellBuildId}/artifacts/{ordinal:int}",
            DownloadArtifactAsync);
    }

    private static async Task<IResult> DownloadArtifactAsync(
        HttpContext httpContext,
        string matrixId,
        string cellBuildId,
        int ordinal,
        MatrixBuildStore matrixBuilds,
        BlobStore blobs,
        IBlobObjectAccess blobAccess,
        ManagementRequestContextFactory contexts,
        ManagementAuthorizer authorizer,
        AuditEventStore audits,
        TimeProvider timeProvider)
    {
        var targetId = ArtifactAuditTarget(matrixId, cellBuildId, ordinal);
        ManagementRequestContext? requestContext = null;
        try
        {
            var correlationId = httpContext.Request
                .Headers[ManagementRequestContextFactory.CorrelationHeader]
                .ToString();
            requestContext = string.IsNullOrWhiteSpace(httpContext.Request.Headers.Authorization)
                ? contexts.FromClaims(
                    httpContext.User,
                    correlationId,
                    requestId: null,
                    source: "artifact-download")
                : await contexts.FromBearerAsync(
                    httpContext.Request.Headers.Authorization.ToString(),
                    correlationId,
                    requestId: null,
                    source: "artifact-download")
                    ?? throw new ManagementAuthorizationException(
                        ManagementPermission.ArtifactRead,
                        "authentication_required");
            authorizer.Demand(requestContext, ManagementPermission.ArtifactRead);
        }
        catch (ArgumentException exception)
        {
            requestContext = contexts.FromClaims(
                httpContext.User,
                suppliedCorrelationId: null,
                requestId: null,
                source: "artifact-download");
            httpContext.Response.Headers[ManagementRequestContextFactory.CorrelationHeader] =
                requestContext.CorrelationId;
            await audits.AppendAsync(AuditEventDraft.Create(
                requestContext,
                timeProvider.GetUtcNow(),
                "artifact.read",
                "artifact",
                targetId,
                AuditOutcome.Failed,
                "invalid_request"));
            return Results.BadRequest(exception.Message);
        }
        catch (ManagementAuthorizationException exception)
        {
            requestContext ??= AnonymousRequestContext(
                httpContext.Request, "artifact-download");
            httpContext.Response.Headers[ManagementRequestContextFactory.CorrelationHeader] =
                requestContext.CorrelationId;
            await audits.AppendAsync(AuditEventDraft.Create(
                requestContext,
                timeProvider.GetUtcNow(),
                "artifact.read",
                "artifact",
                targetId,
                AuditOutcome.Denied,
                exception.ReasonCode));
            if (exception.ReasonCode != "authentication_required")
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            return string.IsNullOrWhiteSpace(httpContext.Request.Headers.Authorization)
                ? Results.Challenge()
                : Results.Unauthorized();
        }

        httpContext.Response.Headers[ManagementRequestContextFactory.CorrelationHeader] =
            requestContext.CorrelationId;
        var artifact = await matrixBuilds.FindArtifactAsync(matrixId, cellBuildId, ordinal);
        var resolved = artifact is null
            ? null
            : await blobAccess.ResolveHumanArtifactAsync(
                new BlobHumanArtifactReadRequest(
                    requestContext,
                    cellBuildId,
                    ordinal.ToString(CultureInfo.InvariantCulture)),
                httpContext.RequestAborted);
        if (artifact is null || resolved is null ||
            !string.Equals(artifact.Sha256, resolved.Sha256, StringComparison.Ordinal) ||
            artifact.Size != resolved.Size)
        {
            await audits.AppendAsync(AuditEventDraft.Create(
                requestContext,
                timeProvider.GetUtcNow(),
                "artifact.read",
                "artifact",
                targetId,
                AuditOutcome.NoChange,
                "not_found"));
            return Results.NotFound();
        }

        var path = blobs.GetPath(resolved.Sha256);
        if (path is null)
        {
            await audits.AppendAsync(AuditEventDraft.Create(
                requestContext,
                timeProvider.GetUtcNow(),
                "artifact.read",
                "artifact",
                targetId,
                AuditOutcome.Failed,
                "blob_missing"));
            return Results.NotFound();
        }

        return Results.File(
            path,
            "application/octet-stream",
            fileDownloadName: SafeArtifactFileName(artifact.Path, ordinal));
    }

    private static string SafeArtifactFileName(string artifactPath, int ordinal)
    {
        var normalized = artifactPath.Replace('\\', '/');
        var segment = normalized[(normalized.LastIndexOf('/') + 1)..];
        var safe = new string(segment
            .Where(character => !char.IsControl(character) && character is not '"' and not '\\' and not '/')
            .ToArray());
        return string.IsNullOrWhiteSpace(safe) || safe is "." or ".."
            ? $"artifact-{ordinal}"
            : safe;
    }

    private static string CreatePanelIdentity(string dataDir)
    {
        var canonicalDataDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDir));
        if (OperatingSystem.IsWindows())
        {
            canonicalDataDir = canonicalDataDir.ToUpperInvariant();
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalDataDir)))
            .ToLowerInvariant();
    }

    private static string ArtifactAuditTarget(string matrixId, string cellBuildId, int ordinal)
    {
        if (ordinal >= 0 && IsGeneratedId(matrixId) && IsGeneratedId(cellBuildId))
        {
            return $"{matrixId}/{cellBuildId}/{ordinal}";
        }

        var rawTarget = $"{matrixId}\n{cellBuildId}\n{ordinal}";
        return $"invalid-artifact:{AuditInputDigest(rawTarget)}";
    }

    private static bool IsGeneratedId(string value) =>
        value.Length == 32 &&
        value.All(character =>
            char.IsAsciiDigit(character) || character is >= 'a' and <= 'f');

    private static string AuditInputDigest(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static ManagementRequestContext AnonymousRequestContext(
        HttpRequest request,
        string source)
    {
        try
        {
            return ManagementRequestContext.Anonymous(
                source,
                request.Headers[ManagementRequestContextFactory.CorrelationHeader].ToString());
        }
        catch (ArgumentException)
        {
            return ManagementRequestContext.Anonymous(source);
        }
    }
}
