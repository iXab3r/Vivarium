using Cake.Frosting;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

[TaskName("Release")]
[TaskDescription("Packages platform Compile outputs into the deterministic D19 release asset set.")]
public sealed class ReleaseTask : AsyncFrostingTask<BuildContext>
{
    public override async Task RunAsync(BuildContext context)
    {
        var version = context.ProductVersion;
        context.SetTeamCityBuildNumber();
        var releaseRoot = ReleaseLayout.ReleaseRoot(context);
        var stagingRoot = Path.Combine(context.OutRoot, "release-staging");
        BuildDirectory.Recreate(releaseRoot);
        BuildDirectory.Recreate(stagingRoot);
        EnsureFreeSpace(context.OutRoot);

        var agentPackageNames = new List<string>();
        foreach (var rid in ReleaseLayout.SupportedRids)
        {
            var compileRoot = ReleaseLayout.CompileRoot(context, rid);
            var name = $"viv-agent-{rid}.zip";
            DeterministicZip.Create(
                Path.Combine(compileRoot, "agent"),
                Path.Combine(releaseRoot, name),
                rid);
            agentPackageNames.Add(name);
        }

        var bundledCatalogRoot = Path.Combine(stagingRoot, "bundled-agent-packages");
        CreateBundledAgentCatalog(context, bundledCatalogRoot, version);

        foreach (var rid in ReleaseLayout.SupportedRids)
        {
            var compileRoot = ReleaseLayout.CompileRoot(context, rid);
            var name = $"viv-cli-{rid}.zip";
            DeterministicZip.Create(
                Path.Combine(compileRoot, "cli"),
                Path.Combine(releaseRoot, name),
                rid);
        }

        foreach (var rid in ReleaseLayout.SupportedRids)
        {
            var compileRoot = ReleaseLayout.CompileRoot(context, rid);
            var stage = Path.Combine(stagingRoot, "server", rid);
            CopyDirectory(Path.Combine(compileRoot, "server"), stage);
            var packageDirectory = Path.Combine(stage, "packages", "agents");
            Directory.CreateDirectory(packageDirectory);
            foreach (var packageName in agentPackageNames.OrderBy(name => name, StringComparer.Ordinal))
            {
                File.Copy(
                    Path.Combine(releaseRoot, packageName),
                    Path.Combine(packageDirectory, packageName),
                    overwrite: false);
            }
            CopyDirectory(
                bundledCatalogRoot,
                Path.Combine(stage, "agent-packages"));

            var name = $"viv-server-{rid}.zip";
            DeterministicZip.Create(stage, Path.Combine(releaseRoot, name), rid);
            Directory.Delete(stage, recursive: true);
        }

        Directory.Delete(stagingRoot, recursive: true);
        await ReleaseSmokeTask.SmokeVerifiedReleaseAsync(context, context.HostRid, version);
    }

    private static void CreateBundledAgentCatalog(
        BuildContext context,
        string destination,
        string version)
    {
        Directory.CreateDirectory(destination);
        var packages = new List<object>();
        foreach (var rid in ReleaseLayout.SupportedRids.Order(StringComparer.Ordinal))
        {
            var fileName = $"agent-{rid}.zip";
            var packagePath = Path.Combine(destination, fileName);
            DeterministicZip.Create(
                Path.Combine(ReleaseLayout.CompileRoot(context, rid), "agent", "agent", "current"),
                packagePath,
                rid);
            using var content = File.OpenRead(packagePath);
            packages.Add(new
            {
                version,
                rid,
                file = fileName,
                sha256 = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(),
            });
        }

        var catalog = JsonSerializer.Serialize(
            new { schemaVersion = 1, packages },
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(
            Path.Combine(destination, "catalog.json"),
            catalog + "\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void EnsureFreeSpace(string path)
    {
        const long minimumBytes = 4L * 1024 * 1024 * 1024;
        var root = Path.GetPathRoot(Path.GetFullPath(path))
            ?? throw new InvalidOperationException($"Could not resolve the filesystem for {path}.");
        var available = new DriveInfo(root).AvailableFreeSpace;
        if (available < minimumBytes)
        {
            throw new InvalidOperationException(
                $"Release requires at least 4 GiB free on '{root}', but only {available / (1024 * 1024)} MiB is available.");
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException($"Compile output is missing: {source}");
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
        }
    }

}

[TaskName("ReleaseSmoke")]
[TaskDescription("Executes controller, CLI, agent, and bootstrap probes from final native release ZIPs.")]
public sealed class ReleaseSmokeTask : AsyncFrostingTask<BuildContext>
{
    public override async Task RunAsync(BuildContext context)
    {
        var version = context.ProductVersion;
        var rid = context.RequestedRid
            ?? throw new InvalidOperationException("ReleaseSmoke requires --rid <native-rid>.");
        if (!string.Equals(rid, context.HostRid, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"ReleaseSmoke requires the native host RID; requested {rid}, host {context.HostRid}.");
        }

        await SmokeVerifiedReleaseAsync(context, rid, version);
    }

    internal static async Task SmokeVerifiedReleaseAsync(
        BuildContext context,
        string rid,
        string version)
    {
        ReleaseLayout.RequireSupportedRid(rid);
        var smokeRoot = Path.Combine(context.OutRoot, "release-smoke", rid);
        BuildDirectory.Recreate(smokeRoot);
        var releaseRoot = ReleaseLayout.ReleaseRoot(context);
        var extension = OperatingSystem.IsWindows() ? ".exe" : string.Empty;

        var controllerRoot = Path.Combine(smokeRoot, "controller");
        DeterministicZip.Extract(Path.Combine(releaseRoot, $"viv-server-{rid}.zip"), controllerRoot);
        var catalogPath = Path.Combine(controllerRoot, "agent-packages", "catalog.json");
        if (!File.Exists(catalogPath))
        {
            throw new FileNotFoundException(
                "Server release is missing its D30 Agent package catalog.",
                catalogPath);
        }
        await SmokeControllerAsync(
            Path.Combine(controllerRoot, "viv-server" + extension),
            controllerRoot,
            Path.Combine(smokeRoot, "controller-data"));

        var cliRoot = Path.Combine(smokeRoot, "cli");
        DeterministicZip.Extract(Path.Combine(releaseRoot, $"viv-cli-{rid}.zip"), cliRoot);
        await SmokeCliAsync(Path.Combine(cliRoot, "viv-cli" + extension), cliRoot, version);

        var agentRoot = Path.Combine(smokeRoot, "agent");
        DeterministicZip.Extract(Path.Combine(releaseRoot, $"viv-agent-{rid}.zip"), agentRoot);
        await BuildProcess.RunExpectingExitCodeAsync(
            Path.Combine(agentRoot, "agent", "current", "viv-agent" + extension),
            [],
            agentRoot,
            expectedExitCode: 2,
            timeoutSeconds: 30);
        await BuildProcess.RunExpectingExitCodeAsync(
            Path.Combine(agentRoot, "viv-agent-update" + extension),
            [],
            agentRoot,
            expectedExitCode: 2,
            timeoutSeconds: 30);
    }

    internal static async Task SmokeCliAsync(string executable, string workingDirectory, string expectedVersion)
    {
        Console.WriteLine($"> {executable} --version");
        var output = await BuildProcess.CaptureAsync(
            executable,
            ["--version"],
            workingDirectory,
            timeoutSeconds: 30);
        Console.WriteLine(output);
        if (!string.Equals(output, $"viv-cli {expectedVersion}", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"CLI version mismatch: expected viv-cli {expectedVersion}, got {output}.");
        }
    }

    internal static async Task SmokeControllerAsync(
        string executable,
        string workingDirectory,
        string dataDirectory)
    {
        Console.WriteLine($"> {executable} --data {dataDirectory} --port 0");
        var startInfo = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        var bundleExtractionRoot = Path.Combine(dataDirectory, "bundle-cache");
        Directory.CreateDirectory(bundleExtractionRoot);
        startInfo.Environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = bundleExtractionRoot;
        startInfo.ArgumentList.Add("--data");
        startInfo.ArgumentList.Add(dataDirectory);
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add("0");

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start {executable}.");
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            const string prefix = "Vivarium controller listening on ";
            string? url = null;
            while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            {
                if (line.StartsWith(prefix, StringComparison.Ordinal))
                {
                    url = line[prefix.Length..];
                    break;
                }
            }

            if (url is null)
            {
                await process.WaitForExitAsync(timeout.Token);
                throw new InvalidOperationException(
                    $"Controller exited before startup with code {process.ExitCode}: {(await stderr).Trim()}");
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out var controllerUri) ||
                !string.Equals(controllerUri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) ||
                !controllerUri.IsLoopback)
            {
                throw new InvalidOperationException($"Controller reported an unsafe smoke URL: {url}");
            }

            using var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            using var response = await client.GetAsync(new Uri(controllerUri, "/app.css"), timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"Controller static asset probe returned HTTP {(int)response.StatusCode}.");
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new TimeoutException("Controller release smoke did not become ready within 30 seconds.");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            await process.WaitForExitAsync();
        }
    }
}

internal static class ReleaseLayout
{
    public static readonly string[] SupportedRids = ["win-x64", "linux-x64", "linux-arm64", "osx-arm64"];

    public static readonly ReleaseComponent Controller =
        new("controller", Path.Combine("src", "Vivarium.Controller"), "Vivarium.Controller");
    public static readonly ReleaseComponent Agent =
        new("agent", Path.Combine("src", "Vivarium.Agent"), "Vivarium.Agent");
    public static readonly ReleaseComponent Bootstrap =
        new("bootstrap", Path.Combine("src", "Vivarium.Bootstrap"), "Vivarium.Bootstrap");
    public static readonly ReleaseComponent Cli =
        new("cli", Path.Combine("src", "Vivarium.Cli"), "Vivarium.Cli");
    public static readonly ReleaseComponent[] Components = [Controller, Agent, Bootstrap, Cli];

    public static string ReleaseRoot(BuildContext context) => Path.Combine(context.OutRoot, "release");

    public static string CompileRoot(BuildContext context, string rid) =>
        Path.Combine(context.OutRoot, "build", rid);

    public static string[] ReleaseAssetNames() => SupportedRids
        .SelectMany(rid => new[]
        {
            $"viv-server-{rid}.zip",
            $"viv-agent-{rid}.zip",
            $"viv-cli-{rid}.zip",
        })
        .OrderBy(name => name, StringComparer.Ordinal)
        .ToArray();

    public static void RequireSupportedRid(string rid)
    {
        if (!SupportedRids.Contains(rid, StringComparer.Ordinal))
        {
            throw new ArgumentOutOfRangeException(
                nameof(rid),
                rid,
                $"Expected one of: {string.Join(", ", SupportedRids)}.");
        }
    }
}

internal static class DeterministicZip
{
    private static readonly DateTimeOffset Timestamp = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static void Create(string sourceDirectory, string destination, string rid)
    {
        using var file = File.Create(destination);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false, Encoding.UTF8);
        foreach (var source in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories)
                     .OrderBy(path => Path.GetRelativePath(sourceDirectory, path), StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(sourceDirectory, source).Replace('\\', '/');
            var entry = archive.CreateEntry(relative, CompressionLevel.SmallestSize);
            entry.LastWriteTime = Timestamp;
            var executable = !rid.StartsWith("win-", StringComparison.Ordinal) &&
                             (relative is "viv-server" or "viv-cli" or "viv-agent-update" or "viv-agent" ||
                              relative.EndsWith("/viv-agent", StringComparison.Ordinal));
            entry.ExternalAttributes =
                (executable ? Convert.ToInt32("100755", 8) : Convert.ToInt32("100644", 8)) << 16;
            using var input = File.OpenRead(source);
            using var output = entry.Open();
            input.CopyTo(output);
        }
    }

    public static void Extract(string path, string destination)
    {
        Directory.CreateDirectory(destination);
        using var archive = ZipFile.OpenRead(path);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            if (!names.Add(entry.FullName) || entry.FullName.StartsWith("/", StringComparison.Ordinal) ||
                entry.FullName.Split('/').Any(segment => segment is "" or "." or ".."))
            {
                throw new InvalidDataException($"Unsafe or duplicate ZIP entry '{entry.FullName}' in {path}.");
            }

            var target = Path.GetFullPath(Path.Combine(destination, entry.FullName));
            var prefix = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
            if (!target.StartsWith(prefix, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"ZIP entry escapes extraction root: {entry.FullName}");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: false);
            if (!OperatingSystem.IsWindows() && ((entry.ExternalAttributes >> 16) & Convert.ToInt32("111", 8)) != 0)
            {
                File.SetUnixFileMode(target, (UnixFileMode)Convert.ToInt32("755", 8));
            }
        }
    }
}

internal sealed record ReleaseComponent(string Id, string Project, string AssemblyName);
