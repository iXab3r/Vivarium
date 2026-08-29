// Vivarium bootstrap — the frozen launcher (ARCHITECTURE §7, D2). This is the only code baked into
// images and installed on physical machines; it must stay boring forever. Entire behavior:
// read bootstrap.json → keep the agent current from the controller's manifest → run it → repeat.
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

var baseDir = AppContext.BaseDirectory;
var configPath = Path.Combine(baseDir, "bootstrap.json");
if (!File.Exists(configPath))
{
    Console.Error.WriteLine($"viv-agent-update: missing {configPath}");
    return 2;
}

using var configDoc = JsonDocument.Parse(File.ReadAllText(configPath));
var controllerUrl = configDoc.RootElement.GetProperty("controllerUrl").GetString()!;
var fingerprint = configDoc.RootElement.GetProperty("certFingerprint").GetString()!
    .Replace("SHA256:", "", StringComparison.OrdinalIgnoreCase);

var agentDir = Path.Combine(baseDir, "agent");
var currentDir = Path.Combine(agentDir, "current");
var versionFile = Path.Combine(agentDir, "version");
var exeName = OperatingSystem.IsWindows() ? "viv-agent.exe" : "viv-agent";

var handler = new SocketsHttpHandler();
handler.SslOptions.RemoteCertificateValidationCallback = (_, cert, _, _) =>
    cert != null &&
    Convert.ToHexString(SHA256.HashData(cert.GetRawCertData()))
        .Equals(fingerprint, StringComparison.OrdinalIgnoreCase);
using var http = new HttpClient(handler) { BaseAddress = new Uri(controllerUrl) };

Console.WriteLine($"viv-agent-update: controller {controllerUrl}");
var random = new Random();
while (true)
{
    try
    {
        await UpdateAgentIfNeededAsync();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"viv-agent-update: manifest/update failed ({ex.Message}); using current agent if present");
    }

    var exe = Path.Combine(currentDir, exeName);
    if (File.Exists(exe))
    {
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                WorkingDirectory = currentDir,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("--config");
            psi.ArgumentList.Add(configPath);
            psi.ArgumentList.Add("--data");
            psi.ArgumentList.Add(Path.Combine(baseDir, "data"));
            using var process = Process.Start(psi)!;
            await process.WaitForExitAsync();
            Console.WriteLine($"viv-agent-update: agent exited with {process.ExitCode}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"viv-agent-update: failed to run agent ({ex.Message})");
        }
    }
    else
    {
        Console.Error.WriteLine("viv-agent-update: no agent available yet");
    }

    await Task.Delay(TimeSpan.FromSeconds(5 + random.Next(0, 10)));
}

async Task UpdateAgentIfNeededAsync()
{
    var os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";
    var arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
    using var response = await http.GetAsync($"/bootstrap/manifest?os={os}&arch={arch}");
    if (!response.IsSuccessStatusCode)
    {
        return; // no manifest published — run whatever we have (offline tolerance)
    }

    using var manifest = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    var version = manifest.RootElement.GetProperty("version").GetString()!;
    var sha256 = manifest.RootElement.GetProperty("sha256").GetString()!;
    var url = manifest.RootElement.GetProperty("url").GetString()!;

    var currentVersion = File.Exists(versionFile) ? File.ReadAllText(versionFile).Trim() : "";
    if (currentVersion == version && Directory.Exists(currentDir))
    {
        return;
    }

    Console.WriteLine($"viv-agent-update: updating agent {currentVersion} -> {version}");
    var zipPath = Path.Combine(agentDir, "download.zip.tmp");
    Directory.CreateDirectory(agentDir);
    await using (var file = File.Create(zipPath))
    await using (var body = await http.GetStreamAsync(url))
    {
        await body.CopyToAsync(file);
    }

    await using (var check = File.OpenRead(zipPath))
    {
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(check));
        if (!actual.Equals(sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(zipPath);
            throw new InvalidOperationException("agent download failed sha256 verification");
        }
    }

    var staging = Path.Combine(agentDir, "staging");
    if (Directory.Exists(staging))
    {
        Directory.Delete(staging, recursive: true);
    }

    ZipFile.ExtractToDirectory(zipPath, staging);
    File.Delete(zipPath);

    var old = Path.Combine(agentDir, "old");
    if (Directory.Exists(old))
    {
        Directory.Delete(old, recursive: true);
    }

    if (Directory.Exists(currentDir))
    {
        Directory.Move(currentDir, old);
    }

    Directory.Move(staging, currentDir);
    File.WriteAllText(versionFile, version);
    if (Directory.Exists(old))
    {
        Directory.Delete(old, recursive: true);
    }
}
