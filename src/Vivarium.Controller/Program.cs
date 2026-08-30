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

var host = await VivariumControllerHost.StartAsync(new ControllerOptions
{
    DataDir = Path.GetFullPath(dataDir),
    Port = port,
});

var enrollToken = await host.Tokens.CreateEnrollTokenAsync();
Console.WriteLine($"Vivarium controller listening on {host.Url}");
Console.WriteLine($"Panel: {host.Url}  (admin token: {host.Tokens.AdminToken})");
Console.WriteLine($"Submit token: {host.Tokens.SubmitToken}");
Console.WriteLine($"TLS:   self-signed, fingerprint SHA256:{host.Certificate.FingerprintSha256}");
Console.WriteLine($"Enroll token (single-use): {enrollToken}");
Console.WriteLine($"Data:  {dataDir}");

await host.WaitForShutdownAsync();

static string? ArgValue(string[] args, string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
