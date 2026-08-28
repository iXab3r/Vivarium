using System.Net;
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
using Vivarium.Controller.Components;
using Vivarium.Controller.Agents;
using Vivarium.Controller.Blobs;
using Vivarium.Controller.Builds;
using Vivarium.Controller.Management;
using Vivarium.Controller.Persistence;
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
    public VivariumDatabase Database { get; }
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
    public string Url { get; }

    private VivariumControllerHost(
        WebApplication app,
        ControllerCertificate certificate,
        VivariumDatabase database,
        TokenStore tokens,
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
        string url)
    {
        this.app = app;
        Certificate = certificate;
        Database = database;
        Tokens = tokens;
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
        var agentStore = new AgentStore(database);
        var agentLifecycle = new AgentLifecycleCoordinator();
        var registry = new AgentRegistry(agentStore, options.TimeProvider);
        var buildStore = new BuildStore(database);
        var buildQueueStore = new BuildQueueStore(database);
        await buildQueueStore.InitializeQueueDeadlinesAsync(options.BuildQueueWaitTimeout);
        await buildQueueStore.ExpireDueAsync(options.TimeProvider.GetUtcNow());
        var buildQueue = new BuildQueueService(
            buildQueueStore,
            registry,
            options.TimeProvider,
            options.BuildQueueWaitTimeout);
        var builds = new BuildTracker(
            registry,
            buildStore,
            buildQueueStore,
            options.TimeProvider,
            options.AgentReconnectGrace);
        await builds.InitializeAsync();
        var agentAdministration = new AgentAdministration(
            registry, agentStore, buildStore, tokens, agentLifecycle);
        var blobs = new BlobStore(Path.Combine(options.DataDir, "blobs"));
        var matrixBuildStore = new MatrixBuildStore(database);
        var databaseChanges = new DatabaseChangeNotifier(database);
        var matrixBuildSubmissions = new MatrixBuildSubmissionService(
            matrixBuildStore,
            agentStore,
            blobs,
            buildQueue,
            options.TimeProvider,
            options.BuildQueueWaitTimeout);
        var matrixBuildCancellations = new MatrixBuildCancellationService(
            matrixBuildStore, builds, buildQueue, options.TimeProvider);

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
        builder.Services.AddSingleton(agentStore);
        builder.Services.AddSingleton(agentLifecycle);
        builder.Services.AddSingleton(registry);
        builder.Services.AddSingleton(agentAdministration);
        builder.Services.AddSingleton(buildStore);
        builder.Services.AddSingleton(buildQueueStore);
        builder.Services.AddSingleton(buildQueue);
        builder.Services.AddSingleton(builds);
        builder.Services.AddSingleton(blobs);
        builder.Services.AddSingleton(matrixBuildStore);
        builder.Services.AddSingleton(databaseChanges);
        builder.Services.AddSingleton(matrixBuildSubmissions);
        builder.Services.AddSingleton(matrixBuildCancellations);
        builder.Services.AddGrpc();
        builder.Services.AddHostedService<AgentHeartbeatMonitor>();
        builder.Services.AddSingleton<BuildQueueTimeoutMonitor>();
        builder.Services.AddHostedService(
            services => services.GetRequiredService<BuildQueueTimeoutMonitor>());
        builder.Services.AddSingleton<BuildScheduler>();
        builder.Services.AddHostedService(
            services => services.GetRequiredService<BuildScheduler>());
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
        app.UseAntiforgery();
        app.MapGrpcService<AgentHubService>();
        app.MapGrpcService<ControlPlaneService>();
        MapBlobEndpoints(app);
        MapPanelLogin(app);
        MapPanelArtifactEndpoints(app);
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

        return new VivariumControllerHost(
            app, certificate, database, tokens, agentStore, agentLifecycle, registry, agentAdministration,
            buildStore, buildQueueStore, buildQueue, builds, matrixBuildStore,
            matrixBuildSubmissions, matrixBuildCancellations, blobs, url);
    }

    /// <summary>The Authorize click (D7): issue a token and deliver it over the live session.</summary>
    public Task AuthorizeAgentAsync(string agentId) => AgentAdministration.AuthorizeAsync(agentId);

    /// <summary>Force-drop an agent's live session; it reconnects and re-hellos (D4).</summary>
    public void KickAgent(string agentId) => Registry.Get(agentId)?.SessionAbort?.Cancel();

    public Task WaitForShutdownAsync(CancellationToken ct = default) => app.WaitForShutdownAsync(ct);

    public async ValueTask DisposeAsync()
    {
        await app.StopAsync(TimeSpan.FromSeconds(5));
        await app.DisposeAsync();
        await Database.DisposeAsync();
    }

    private static void MapBlobEndpoints(WebApplication app)
    {
        app.MapPut("/blobs/{sha256}", async (string sha256, HttpRequest request, BlobStore blobs, TokenStore tokens) =>
        {
            if (!await IsAuthorizedAsync(request, tokens))
            {
                return Results.Unauthorized();
            }

            var ok = await blobs.PutAsync(sha256, request.Body, request.HttpContext.RequestAborted);
            return ok ? Results.Ok() : Results.BadRequest("body does not hash to the requested name");
        });

        app.MapGet("/blobs/{sha256}", async (string sha256, HttpRequest request, BlobStore blobs, TokenStore tokens) =>
        {
            if (!await IsAuthorizedAsync(request, tokens))
            {
                return Results.Unauthorized();
            }

            var path = blobs.GetPath(sha256);
            return path == null ? Results.NotFound() : Results.File(path, "application/octet-stream");
        });
    }

    private static void MapPanelLogin(WebApplication app)
    {
        app.MapGet("/login", (HttpContext context) =>
            context.User.Identity?.IsAuthenticated == true
                ? Results.Redirect("/agents")
                : Results.Content(PanelLogin.Render(invalid: false), "text/html"));

        app.MapPost("/login", async (HttpContext context, TokenStore tokens) =>
        {
            var form = await context.Request.ReadFormAsync();
            var supplied = form["token"].ToString();
            if (!FixedTokenEquals(tokens.AdminToken, supplied))
            {
                return Results.Content(PanelLogin.Render(invalid: true), "text/html", statusCode: 401);
            }

            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, "administrator")],
                CookieAuthenticationDefaults.AuthenticationScheme);
            await context.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(identity),
                new AuthenticationProperties { IsPersistent = true });
            return Results.Redirect("/agents");
        }).DisableAntiforgery();

        app.MapPost("/logout", async (HttpContext context) =>
        {
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Redirect("/login");
        }).RequireAuthorization().DisableAntiforgery();
    }

    private static void MapPanelArtifactEndpoints(WebApplication app)
    {
        app.MapGet(
            "/builds/{matrixId}/cells/{cellBuildId}/artifacts/{ordinal:int}",
            DownloadArtifactAsync)
            .RequireAuthorization();
    }

    private static async Task<IResult> DownloadArtifactAsync(
        string matrixId,
        string cellBuildId,
        int ordinal,
        MatrixBuildStore matrixBuilds,
        BlobStore blobs)
    {
        var artifact = await matrixBuilds.FindArtifactAsync(matrixId, cellBuildId, ordinal);
        if (artifact is null)
        {
            return Results.NotFound();
        }

        var path = blobs.GetPath(artifact.Sha256);
        if (path is null)
        {
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

    private static bool FixedTokenEquals(string expected, string supplied)
    {
        var left = Encoding.UTF8.GetBytes(expected);
        var right = Encoding.UTF8.GetBytes(supplied);
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
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

    private static Task<bool> IsAuthorizedAsync(HttpRequest request, TokenStore tokens)
    {
        var header = request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        return header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? tokens.IsValidBearerAsync(header[prefix.Length..])
            : Task.FromResult(false);
    }
}
