using System.Text.Json;
using System.Security.Cryptography;
using CliWrap;
using EyeAuras.Automation;

namespace Vivarium.Acceptance.Fixtures;

/// <summary>Real explicit local init/migrate/run commands, with private stdin and DML-only runtime credentials.</summary>
public sealed class HostFixture
{
    public required string Origin { get; init; }
    public required AutomationProcess Process { get; init; }

    public static async Task<HostFixture> StartAsync(AutomationScope scope, PostgresFixture postgres,
        RunTls tls, IssuerFixture issuer, string origin)
    {
        var run = await AutomationRun.ConnectAsync(scope.CancellationToken);
        var host = Environment.GetEnvironmentVariable("VIVARIUM_HOST_PATH")
            ?? throw new InvalidOperationException("Prepare the real Host artifact first.");
        if (!Path.IsPathFullyQualified(host) || !File.Exists(host))
            throw new InvalidOperationException("The prepared Host artifact is absent.");
        var nodes = Path.Combine(scope.WorkingDirectory, "nodes");
        var storageOrigin = $"http://127.0.0.1:{FixturePorts.Loopback()}";
        Command Command(string command) => Cli.Wrap("dotnet").WithArguments([host, command, "--settings-directory", nodes])
            .WithWorkingDirectory(scope.WorkingDirectory).WithEnvironmentVariables(tls.Environment);
        var identity = await IdentityFixtureSettings.CreateAsync(scope, issuer);
        // Installation access is unnecessary for this ordinary-login scenario.
        await PrivateCommand.ExecuteAsync(run, Command("init"), JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["vivarium-host"] = new { PublicOrigin = origin, ListenUrl = origin, StorageListenUrl = storageOrigin,
                CertificatePath = tls.CertificatePath, CertificatePassword = tls.CertificatePassword },
            ["vivarium-database"] = new { ConnectionString = postgres.RuntimeConnection },
            ["vivarium-identity"] = identity,
            ["vivarium-storage"] = new { Backend = "Local", Root = Path.Combine(scope.WorkingDirectory, "objects"),
                PublicBaseUrl = storageOrigin, SigningKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
                MaxObjectBytes = 1048576, DefaultLifetimeSeconds = 60, MaxLifetimeSeconds = 300 }
        }), scope.CancellationToken);
        await PrivateCommand.ExecuteAsync(run, Command("migrate"),
            JsonSerializer.Serialize(new { ConnectionString = postgres.MaintenanceConnection }), scope.CancellationToken);
        await postgres.GrantRuntimeAsync(scope.CancellationToken);
        var process = await scope.StartProcessAsync(Command("run"));
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
            { Timeout = TimeSpan.FromSeconds(5) };
        await AutomationWait.UntilAsync(async token =>
        {
            if ((await process.GetStatusAsync(token)).HasExited)
                throw new InvalidOperationException("Owned Host exited before readiness; inspect private diagnostics.");
            try { using var response = await http.GetAsync(origin + "/health/ready", token); return (int)response.StatusCode; }
            catch (HttpRequestException) { return 0; }
        }, status => status == 200, "owned Host readiness", TimeSpan.FromSeconds(45), scope.CancellationToken);
        return new() { Origin = origin, Process = process };
    }
}
