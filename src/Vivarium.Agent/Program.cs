using System.Text.Json;
using Vivarium.Agent;

// Config precedence: --config bootstrap.json (what the stamped zip ships, D19) < explicit flags.
string? url = null;
string? fingerprint = null;
string? enrollToken = ArgValue(args, "--token");
var dataDir = ArgValue(args, "--data");

var configPath = ArgValue(args, "--config");
if (configPath != null)
{
    using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
    url = doc.RootElement.GetProperty("controllerUrl").GetString();
    fingerprint = doc.RootElement.GetProperty("certFingerprint").GetString();
    if (enrollToken == null &&
        doc.RootElement.TryGetProperty("enrollToken", out var t) &&
        t.ValueKind == JsonValueKind.String)
    {
        enrollToken = t.GetString();
    }
}

url = ArgValue(args, "--url") ?? url;
fingerprint = ArgValue(args, "--fp") ?? fingerprint;

if (url == null || fingerprint == null)
{
    Console.Error.WriteLine("usage: vivarium-agent --url <https://ctrl:8443> --fp <sha256hex> [--token <enroll>] [--data <dir>]");
    Console.Error.WriteLine("   or: vivarium-agent --config <bootstrap.json> [--data <dir>]");
    return 2;
}

var runner = new AgentRunner(new AgentOptions
{
    ControllerUrl = url,
    CertFingerprintSha256 = fingerprint.Replace("SHA256:", "", StringComparison.OrdinalIgnoreCase),
    EnrollToken = enrollToken,
    DataDir = Path.GetFullPath(dataDir ?? Path.Combine(AppContext.BaseDirectory, "data")),
    AgentPackageVersion = ArgValue(args, "--package-version"),
    AgentPackageSha256 = ArgValue(args, "--package-sha256"),
    UpgradeOperationId = ArgValue(args, "--upgrade-operation"),
    UpgradeHealthMarkerPath = ArgValue(args, "--upgrade-health-marker"),
    UpgradeFailureCode = ArgValue(args, "--upgrade-failure-code"),
    BootstrapLeasePath = ArgValue(args, "--bootstrap-lease"),
    BootstrapLeaseId = ArgValue(args, "--bootstrap-lease-id"),
});

Console.WriteLine($"vivarium-agent {AgentRunner.Version} (agent id {runner.AgentId})");
Console.WriteLine($"controller: {url}");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

await runner.RunAsync(cts.Token);
return 0;

static string? ArgValue(string[] args, string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
