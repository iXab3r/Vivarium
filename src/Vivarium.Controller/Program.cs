using System.Reflection;
using Vivarium.Controller;

if (args is ["--version"])
{
    var assembly = typeof(ControllerOptions).Assembly;
    var informationalVersion = assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
        .InformationalVersion;
    var productVersion = informationalVersion?.Split('+', 2)[0];
    var version = string.IsNullOrWhiteSpace(productVersion)
        ? assembly.GetName().Version?.ToString(3) ?? "0.0.0"
        : productVersion;
    Console.WriteLine($"viv-server {version}");
    return;
}

var dataDir = ArgValue(args, "--data")
    ?? Environment.GetEnvironmentVariable("VIVARIUM_DATA")
    ?? Path.Combine(Environment.CurrentDirectory, "vivarium-data");
var port = int.TryParse(ArgValue(args, "--port"), out var p) ? p : 8443;
var agentPackageCatalog = ArgValue(args, "--agent-package-catalog")
    ?? Environment.GetEnvironmentVariable("VIVARIUM_AGENT_PACKAGE_CATALOG");
var defaultAgentPackageCatalog = Path.Combine(
    AppContext.BaseDirectory, "agent-packages", "catalog.json");

var host = await VivariumControllerHost.StartAsync(new ControllerOptions
{
    DataDir = Path.GetFullPath(dataDir),
    Port = port,
    AgentPackageCatalogPath = string.IsNullOrWhiteSpace(agentPackageCatalog)
        ? File.Exists(defaultAgentPackageCatalog) ? defaultAgentPackageCatalog : null
        : Path.GetFullPath(agentPackageCatalog),
});

Console.WriteLine($"Vivarium controller listening on {host.Url}");
Console.WriteLine($"Panel: {host.Url}");
Console.WriteLine($"TLS:   self-signed, fingerprint SHA256:{host.Certificate.FingerprintSha256}");
if (host.AdministrationBootstrap.Startup.BootstrapToken is { } bootstrapToken)
{
    Console.WriteLine(
        $"VIVARIUM FIRST-RUN TOKEN [generation " +
        $"{host.AdministrationBootstrap.Startup.BootstrapGenerationId}; expires " +
        $"{host.AdministrationBootstrap.Startup.BootstrapExpiresAt:O}]: {bootstrapToken}");
    Console.WriteLine($"Open {host.Url}/setup and paste this token. Do not send it in a URL.");
}
else if (host.AdministrationBootstrap.Startup.State is
         Vivarium.Controller.Administration.AdministrationState.SetupInProgress or
         Vivarium.Controller.Administration.AdministrationState.SetupWaitingForGit or
         Vivarium.Controller.Administration.AdministrationState.SetupActivating)
{
    Console.WriteLine("First-run setup is pending; use the local setup status/reissue operation to resume it.");
}
else if (host.AdministrationBootstrap.Startup.State is
         Vivarium.Controller.Administration.AdministrationState.RecoveryAvailable or
         Vivarium.Controller.Administration.AdministrationState.RecoveryInProgress)
{
    Console.WriteLine("Break-glass recovery is explicitly enabled; revoke it locally when the recovery action is complete.");
}
Console.WriteLine("Legacy admin/submit token files remain enabled only as migration adapters.");
Console.WriteLine($"Data:  {dataDir}");

await host.WaitForShutdownAsync();

static string? ArgValue(string[] args, string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
