using System.Net;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Vivarium.Contracts.V1;
using Vivarium.Controller.Agents;
using Vivarium.Controller.Blobs;
using Vivarium.Controller.Builds;
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
    public AgentRegistry Registry { get; }
    public BuildTracker Builds { get; }
    public BlobStore Blobs { get; }
    public string Url { get; }

    private VivariumControllerHost(
        WebApplication app,
        ControllerCertificate certificate,
        TokenStore tokens,
        AgentRegistry registry,
        BuildTracker builds,
        BlobStore blobs,
        string url)
    {
        this.app = app;
        Certificate = certificate;
        Tokens = tokens;
        Registry = registry;
        Builds = builds;
        Blobs = blobs;
        Url = url;
    }

    public static async Task<VivariumControllerHost> StartAsync(ControllerOptions options)
    {
        Directory.CreateDirectory(options.DataDir);
        var certificate = ControllerCertificate.LoadOrCreate(options.DataDir);
        var tokens = new TokenStore(options.DataDir);
        var registry = new AgentRegistry();
        var builds = new BuildTracker(registry);
        var blobs = new BlobStore(Path.Combine(options.DataDir, "blobs"));

        var builder = WebApplication.CreateBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddSingleton(certificate);
        builder.Services.AddSingleton(tokens);
        builder.Services.AddSingleton(registry);
        builder.Services.AddSingleton(builds);
        builder.Services.AddSingleton(blobs);
        builder.Services.AddGrpc();

        var address = options.Host == "0.0.0.0" ? IPAddress.Any : IPAddress.Parse(options.Host);
        builder.WebHost.ConfigureKestrel(kestrel =>
            kestrel.Listen(address, options.Port, listen =>
            {
                listen.Protocols = HttpProtocols.Http1AndHttp2;
                listen.UseHttps(certificate.Certificate);
            }));

        var app = builder.Build();
        app.MapGrpcService<AgentHubService>();
        MapBlobEndpoints(app);
        app.MapGet("/", () => $"Vivarium controller {AgentHubService.ServerVersion}");

        await app.StartAsync();

        var boundAddress = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses.First();
        // Kestrel reports e.g. https://0.0.0.0:8443 — keep it usable for local clients.
        var url = boundAddress.Replace("0.0.0.0", "127.0.0.1").Replace("[::]", "127.0.0.1");

        return new VivariumControllerHost(app, certificate, tokens, registry, builds, blobs, url);
    }

    /// <summary>The Authorize click (D7): issue a token and deliver it over the live session.</summary>
    public string AuthorizeAgent(string agentId)
    {
        var agent = Registry.Get(agentId)
            ?? throw new InvalidOperationException($"unknown agent '{agentId}'");
        var token = Tokens.IssueAgentToken(agentId);
        agent.Auth = AgentAuth.Authorized;
        Registry.TrySend(agentId, new ControllerMsg
        {
            Authorized = new AuthorizationGranted { AuthToken = token },
        });
        return token;
    }

    public Task WaitForShutdownAsync(CancellationToken ct = default) => app.WaitForShutdownAsync(ct);

    public async ValueTask DisposeAsync()
    {
        await app.StopAsync(TimeSpan.FromSeconds(5));
        await app.DisposeAsync();
    }

    private static void MapBlobEndpoints(WebApplication app)
    {
        app.MapPut("/blobs/{sha256}", async (string sha256, HttpRequest request, BlobStore blobs, TokenStore tokens) =>
        {
            if (!IsAuthorized(request, tokens))
            {
                return Results.Unauthorized();
            }

            var ok = await blobs.PutAsync(sha256, request.Body, request.HttpContext.RequestAborted);
            return ok ? Results.Ok() : Results.BadRequest("body does not hash to the requested name");
        });

        app.MapGet("/blobs/{sha256}", (string sha256, HttpRequest request, BlobStore blobs, TokenStore tokens) =>
        {
            if (!IsAuthorized(request, tokens))
            {
                return Results.Unauthorized();
            }

            var path = blobs.GetPath(sha256);
            return path == null ? Results.NotFound() : Results.File(path, "application/octet-stream");
        });
    }

    private static bool IsAuthorized(HttpRequest request, TokenStore tokens)
    {
        var header = request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        return header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
               tokens.IsValidBearer(header[prefix.Length..]);
    }
}
