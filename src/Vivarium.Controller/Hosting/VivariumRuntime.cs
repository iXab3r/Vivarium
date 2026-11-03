using Chiral;
using EyeAuras.Storage;
using Microsoft.EntityFrameworkCore;
using Vivarium.Controller.Identity;
using Vivarium.Controller.Persistence;
using Vivarium.Controller.Tokens;
using Vivarium.Controller.Catalog;
using Vivarium.Controller.Agents;
using Vivarium.Controller.Builds.Runtime;

namespace Vivarium.Controller.Hosting;

/// <summary>The fresh-install Host composition; legacy services retire only with verified replacement.</summary>
public static class VivariumRuntime
{
    public static WebApplication Build(string settingsDirectory, Action<ILoggingBuilder>? configureLogging = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(VivariumRuntime).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
            WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
        });
        builder.Services.AddVivariumNodes(builder.Configuration, settingsDirectory);
        builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
        builder.Logging.AddFilter("OpenIddict", LogLevel.Warning);
        configureLogging?.Invoke(builder.Logging);
        builder.Services.AddDbContextFactory<VivariumDbContext>(options => options.UseNpgsql(
            builder.Configuration.GetConnectionString("Vivarium")
                ?? throw new InvalidOperationException("Initialize the PostgreSQL node first.")));
        builder.Services.AddVivariumIdentity(builder.Configuration);
        builder.Services.AddVivariumTokens(builder.Configuration);
        builder.Services.AddHostedService<RuntimeStartup>();
        builder.Services.AddSingleton(provider => CatalogWorker.Sources(provider.GetRequiredService<IConfiguration>()));
        builder.Services.AddSingleton<CatalogService>();
        builder.Services.AddScoped<CatalogQueries>();
        builder.Services.AddHostedService<CatalogWorker>();
        builder.Services.AddGrpc();
        builder.Services.AddSingleton<AgentCredentials>();
        builder.Services.AddSingleton<AgentLifecycleCoordinator>();
        builder.Services.AddSingleton(provider => new AgentRegistry(timeProvider: provider.GetRequiredService<TimeProvider>()));
        builder.Services.AddSingleton<RuntimeAgentSessions>();
        builder.Services.AddSingleton<RuntimeBuildService>();
        builder.Services.AddSingleton<RuntimeBuildBytes>();
        builder.Services.AddSingleton<RuntimeStepCompiler>();
        builder.Services.AddSingleton<RuntimeBuildCoordinator>();
        builder.Services.AddSingleton<RuntimeBuildOutputStore>();
        builder.Services.AddHostedService<RuntimeBuildWorker>();
        builder.Services.AddSingleton<Vivarium.Controller.Rest.Common.RestCursorCodec>();
        builder.WebHost.ConfigureKestrel(options =>
            options.Configure(builder.Configuration.GetSection("Kestrel"), reloadOnChange: false));
        builder.Services.DeferVivariumIdentityPreload();
        var app = builder.Build();
        app.UseExceptionHandler(errors => errors.Run(context =>
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            context.Response.Headers.CacheControl = "no-store";
            return Task.CompletedTask;
        }));
        var storageEndpoint = new object();
        app.MapLocalStorage().WithMetadata(storageEndpoint);
        app.UseRouting();
        app.Use(async (context, next) =>
        {
            var storagePort = new Uri(builder.Configuration["Kestrel:Endpoints:Storage:Url"]!).Port;
            var isStorage = context.GetEndpoint()?.Metadata.Contains(storageEndpoint) == true;
            if ((context.Connection.LocalPort == storagePort) != isStorage)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            if (context.Request.Path.StartsWithSegments("/Identity")
                || context.Request.Path.StartsWithSegments("/connect")
                || context.Request.Path.StartsWithSegments("/.well-known")
                || context.Request.Path.StartsWithSegments("/signin-vivarium-token")
                || context.Request.Path.StartsWithSegments("/api")
                || context.Request.Path.StartsWithSegments("/app/rest"))
                context.Response.Headers.CacheControl = "no-store";
            await next(context);
        });
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();
        app.MapVivariumIdentity();
        RuntimeIdentity.Map(app);
        app.MapAccess();
        app.MapCatalog();
        app.MapRuntimeAgents();
        app.MapRuntimeBuilds();
        app.MapRuntimeBuildOutput();
        app.MapRuntimeBuildArtifacts();
        app.MapRuntimeAgentBytes();
        app.MapGrpcService<RuntimeAgentHub>();
        app.MapWorkbench();
        app.MapVivariumTokens();
        app.MapGet("/health/live", () => Results.Text("Healthy"));
        app.MapGet("/health/ready", async (VivariumDbContext database, CancellationToken cancellationToken) =>
        {
            try { await PostgresSchema.VerifyReadyAsync(database, cancellationToken); }
            catch (Exception exception) when (exception is InvalidDataException or Npgsql.NpgsqlException)
            { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
            return Results.Text("Healthy");
        });
        app.UseStaticFiles();
        app.MapFallback("/api/{**path}", () => Results.NotFound());
        app.MapFallback("/app/rest/{**path}", () => Results.NotFound());
        return app;
    }

    private sealed class RuntimeStartup(NodeConfigurationService settings, IEnumerable<NodeDescriptor> nodes,
        IConfiguration configuration, IServiceScopeFactory scopes) : IHostedService
    {
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            foreach (var node in nodes)
                await WaitForNodeAsync(settings, node.NodeId, deadline.Token);
            var publicListener = new Uri(configuration["Kestrel:Endpoints:Public:Url"]!);
            var storageListener = new Uri(configuration["Kestrel:Endpoints:Storage:Url"]!);
            if (!storageListener.IsLoopback || storageListener.Scheme != "http"
                || storageListener.Port == publicListener.Port)
                throw new InvalidDataException("Storage needs a distinct HTTP loopback listener.");
            if (configuration["Storage:Backend"] == "Local"
                && configuration["Storage:Local:PublicBaseUrl"]?.TrimEnd('/') != storageListener.AbsoluteUri.TrimEnd('/'))
                throw new InvalidDataException("Local Storage origin must match its private listener.");
            await using var scope = scopes.CreateAsyncScope();
            await PostgresSchema.VerifyReadyAsync(scope.ServiceProvider.GetRequiredService<VivariumDbContext>(), deadline.Token);
            await scope.ServiceProvider.InitializeVivariumIdentityAsync(deadline.Token);
            await scope.ServiceProvider.InitializeVivariumTokensAsync(deadline.Token);
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    internal static async Task WaitForNodeAsync(NodeConfigurationService settings, string nodeId,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await settings.GetAsync(nodeId);
            if (result.Value?.SavedRevision is null)
                throw new InvalidDataException($"Initialize {nodeId} with the local init command first.");
            if (result.Value.RuntimeStatus == "error")
                throw new InvalidDataException($"Configuration failed for {nodeId}.");
            if (settings.IsReady(nodeId) == true) return;
            await Task.Delay(20, cancellationToken);
        }
    }
}
