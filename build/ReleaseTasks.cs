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
        var version = context.RequireReleaseVersion();
        var sourceSha = context.RequireSourceSha();
        foreach (var rid in ReleaseLayout.SupportedRids)
        {
            CompileManifestVerifier.Verify(context, rid, version, sourceSha);
        }

        var releaseRoot = ReleaseLayout.ReleaseRoot(context);
        var stagingRoot = Path.Combine(context.OutRoot, "release-staging");
        PayloadSmokeTask.RecreateDirectory(releaseRoot);
        PayloadSmokeTask.RecreateDirectory(stagingRoot);
        EnsureFreeSpace(context.OutRoot);

        var assets = new List<ReleaseAsset>();
        foreach (var rid in ReleaseLayout.SupportedRids)
        {
            var compileRoot = ReleaseLayout.CompileRoot(context, rid);
            var stage = Path.Combine(stagingRoot, "agent", rid);
            CopyDirectory(Path.Combine(compileRoot, "agent"), stage);
            var name = $"viv-agent-{rid}.zip";
            DeterministicZip.Create(stage, Path.Combine(releaseRoot, name), rid);
            assets.Add(ReleaseAsset.FromFile(releaseRoot, name, "agent-template", rid));
            Directory.Delete(stage, recursive: true);
        }

        foreach (var rid in ReleaseLayout.SupportedRids)
        {
            var compileRoot = ReleaseLayout.CompileRoot(context, rid);
            var stage = Path.Combine(stagingRoot, "cli", rid);
            CopyDirectory(Path.Combine(compileRoot, "cli"), stage);
            var name = $"viv-cli-{rid}.zip";
            DeterministicZip.Create(stage, Path.Combine(releaseRoot, name), rid);
            assets.Add(ReleaseAsset.FromFile(releaseRoot, name, "cli", rid));
            Directory.Delete(stage, recursive: true);
        }

        var agentAssets = assets.Where(asset => asset.Component == "agent-template").ToArray();
        foreach (var rid in ReleaseLayout.SupportedRids)
        {
            var compileRoot = ReleaseLayout.CompileRoot(context, rid);
            var stage = Path.Combine(stagingRoot, "server", rid);
            CopyDirectory(Path.Combine(compileRoot, "server"), stage);
            var packageDirectory = Path.Combine(stage, "packages", "agents");
            Directory.CreateDirectory(packageDirectory);
            foreach (var asset in agentAssets.OrderBy(asset => asset.Name, StringComparer.Ordinal))
            {
                File.Copy(
                    Path.Combine(releaseRoot, asset.Name),
                    Path.Combine(packageDirectory, asset.Name),
                    overwrite: false);
            }

            WriteJson(
                Path.Combine(stage, "packages", "manifest.json"),
                new EmbeddedPackageManifest(1, version, agentAssets));
            var name = $"viv-server-{rid}.zip";
            DeterministicZip.Create(stage, Path.Combine(releaseRoot, name), rid);
            assets.Add(ReleaseAsset.FromFile(releaseRoot, name, "server", rid));
            Directory.Delete(stage, recursive: true);
        }

        var orderedAssets = assets.OrderBy(asset => asset.Name, StringComparer.Ordinal).ToArray();
        WriteJson(
            Path.Combine(releaseRoot, ReleaseLayout.ManifestName),
            new ReleaseManifest(1, version, sourceSha, orderedAssets));
        WriteChecksums(releaseRoot, orderedAssets.Select(asset => asset.Name).Append(ReleaseLayout.ManifestName));
        Directory.Delete(stagingRoot, recursive: true);
        ReleaseVerifier.Verify(context, version, sourceSha);
        await ReleaseSmokeTask.SmokeVerifiedReleaseAsync(context, context.HostRid, version);
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

    internal static void WriteJson<T>(string path, T value)
    {
        var json = JsonSerializer.Serialize(value, ReleaseLayout.JsonOptions) + "\n";
        File.WriteAllText(path, json, new UTF8Encoding(false));
    }

    private static void WriteChecksums(string releaseRoot, IEnumerable<string> names)
    {
        var lines = names.OrderBy(name => name, StringComparer.Ordinal)
            .Select(name => $"{ReleaseAsset.HashFile(Path.Combine(releaseRoot, name))}  {name}");
        File.WriteAllText(
            Path.Combine(releaseRoot, ReleaseLayout.ChecksumsName),
            string.Join("\n", lines) + "\n",
            new UTF8Encoding(false));
    }
}

[TaskName("ReleaseVerify")]
[TaskDescription("Verifies release manifest, checksums, layouts, and nested package identity.")]
public sealed class ReleaseVerifyTask : FrostingTask<BuildContext>
{
    public override void Run(BuildContext context) =>
        ReleaseVerifier.Verify(context, context.RequireReleaseVersion(), context.RequireSourceSha());
}

[TaskName("ReleaseSmoke")]
[TaskDescription("Executes controller, CLI, agent, and bootstrap probes from final native release ZIPs.")]
public sealed class ReleaseSmokeTask : AsyncFrostingTask<BuildContext>
{
    public override async Task RunAsync(BuildContext context)
    {
        var version = context.RequireReleaseVersion();
        var sourceSha = context.RequireSourceSha();
        var rid = context.RequestedRid
            ?? throw new InvalidOperationException("ReleaseSmoke requires --rid <native-rid>.");
        if (!string.Equals(rid, context.HostRid, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"ReleaseSmoke requires the native host RID; requested {rid}, host {context.HostRid}.");
        }

        ReleaseVerifier.Verify(context, version, sourceSha);
        await SmokeVerifiedReleaseAsync(context, rid, version);
    }

    internal static async Task SmokeVerifiedReleaseAsync(
        BuildContext context,
        string rid,
        string version)
    {
        ReleaseLayout.RequireSupportedRid(rid);
        var smokeRoot = Path.Combine(context.OutRoot, "release-smoke", rid);
        PayloadSmokeTask.RecreateDirectory(smokeRoot);
        var releaseRoot = ReleaseLayout.ReleaseRoot(context);
        var extension = OperatingSystem.IsWindows() ? ".exe" : string.Empty;

        var controllerRoot = Path.Combine(smokeRoot, "controller");
        DeterministicZip.Extract(Path.Combine(releaseRoot, $"viv-server-{rid}.zip"), controllerRoot);
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
    public const string CompileManifestName = "compile-manifest.json";
    public const string ManifestName = "release-manifest.json";
    public const string ChecksumsName = "SHA256SUMS";

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
    public static readonly string[] ControllerContentFiles =
    [
        "appsettings.Development.json",
        "appsettings.json",
        "Vivarium.Controller.staticwebassets.endpoints.json",
        "wwwroot/_framework/blazor.server.js",
        "wwwroot/_framework/blazor.server.js.br",
        "wwwroot/_framework/blazor.server.js.gz",
        "wwwroot/_framework/blazor.web.js",
        "wwwroot/_framework/blazor.web.js.br",
        "wwwroot/_framework/blazor.web.js.gz",
        "wwwroot/app.css",
        "wwwroot/app.css.br",
        "wwwroot/app.css.gz",
        "wwwroot/Vivarium.Controller.styles.css",
        "wwwroot/Vivarium.Controller.styles.css.br",
        "wwwroot/Vivarium.Controller.styles.css.gz",
    ];

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static string ReleaseRoot(BuildContext context) => Path.Combine(context.OutRoot, "release");

    public static string CompileRoot(BuildContext context, string rid) =>
        Path.Combine(context.OutRoot, "build", rid);

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

internal static class ReleaseVerifier
{
    public static void Verify(BuildContext context, string expectedVersion, string expectedSourceSha)
    {
        var releaseRoot = ReleaseLayout.ReleaseRoot(context);
        var manifestPath = Path.Combine(releaseRoot, ReleaseLayout.ManifestName);
        var manifest = JsonSerializer.Deserialize<ReleaseManifest>(
            File.ReadAllText(manifestPath),
            ReleaseLayout.JsonOptions) ?? throw new InvalidDataException("Release manifest is empty.");
        if (manifest.SchemaVersion != 1 || manifest.Version != expectedVersion || manifest.SourceSha != expectedSourceSha)
        {
            throw new InvalidDataException("Release manifest identity does not match the requested release.");
        }

        var expectedNames = ReleaseLayout.SupportedRids
            .SelectMany(rid => new[]
            {
                $"viv-server-{rid}.zip",
                $"viv-agent-{rid}.zip",
                $"viv-cli-{rid}.zip",
            })
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        var actualNames = manifest.Assets.Select(asset => asset.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        if (!expectedNames.SequenceEqual(actualNames, StringComparer.Ordinal))
        {
            throw new InvalidDataException("Release manifest does not contain the exact D19 asset matrix.");
        }

        foreach (var asset in manifest.Assets)
        {
            var path = Path.Combine(releaseRoot, asset.Name);
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != asset.Size ||
                !string.Equals(ReleaseAsset.HashFile(path), asset.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Release asset identity mismatch: {asset.Name}");
            }
            DeterministicZip.Verify(path);
            VerifyAssetLayout(releaseRoot, asset, expectedVersion);
        }

        var checksumPath = Path.Combine(releaseRoot, ReleaseLayout.ChecksumsName);
        var expectedChecksums = manifest.Assets.Select(asset => asset.Name)
            .Append(ReleaseLayout.ManifestName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .Select(name => $"{ReleaseAsset.HashFile(Path.Combine(releaseRoot, name))}  {name}");
        var expectedText = string.Join("\n", expectedChecksums) + "\n";
        if (!string.Equals(File.ReadAllText(checksumPath), expectedText, StringComparison.Ordinal))
        {
            throw new InvalidDataException("SHA256SUMS does not exactly match the release assets.");
        }
    }

    private static void VerifyAssetLayout(string releaseRoot, ReleaseAsset asset, string version)
    {
        using var archive = ZipFile.OpenRead(Path.Combine(releaseRoot, asset.Name));
        var names = archive.Entries.Select(entry => entry.FullName).ToHashSet(StringComparer.Ordinal);
        var extension = asset.Rid.StartsWith("win-", StringComparison.Ordinal) ? ".exe" : string.Empty;
        switch (asset.Component)
        {
            case "cli":
                RequireExact(names, ["viv-cli" + extension], asset.Name);
                break;
            case "agent-template":
                RequireExact(
                    names,
                    [
                        "viv-agent-update" + extension,
                        "bootstrap.json.sample",
                        "agent/current/viv-agent" + extension,
                        "agent/version",
                    ],
                    asset.Name);
                var versionEntry = archive.GetEntry("agent/version")!;
                using (var reader = new StreamReader(versionEntry.Open(), Encoding.UTF8))
                {
                    if (!string.Equals(reader.ReadToEnd(), version + "\n", StringComparison.Ordinal))
                    {
                        throw new InvalidDataException($"Agent version marker mismatch in {asset.Name}.");
                    }
                }
                break;
            case "server":
                var expected = new List<string> { "viv-server" + extension, "packages/manifest.json" };
                expected.AddRange(ReleaseLayout.ControllerContentFiles);
                if (asset.Rid.StartsWith("win-", StringComparison.Ordinal))
                {
                    expected.Add("web.config");
                }
                expected.AddRange(ReleaseLayout.SupportedRids.Select(rid => $"packages/agents/viv-agent-{rid}.zip"));
                RequireExact(names, expected, asset.Name);
                VerifyEmbeddedPackages(releaseRoot, archive, version);
                break;
            default:
                throw new InvalidDataException($"Unknown release component: {asset.Component}");
        }
    }

    private static void VerifyEmbeddedPackages(string releaseRoot, ZipArchive archive, string version)
    {
        var manifestEntry = archive.GetEntry("packages/manifest.json")!;
        EmbeddedPackageManifest embedded;
        using (var stream = manifestEntry.Open())
        {
            embedded = JsonSerializer.Deserialize<EmbeddedPackageManifest>(stream, ReleaseLayout.JsonOptions)
                ?? throw new InvalidDataException("Embedded package manifest is empty.");
        }
        if (embedded.SchemaVersion != 1 || embedded.Version != version)
        {
            throw new InvalidDataException("Embedded package manifest identity mismatch.");
        }

        foreach (var package in embedded.Packages)
        {
            var entry = archive.GetEntry("packages/agents/" + package.Name)
                ?? throw new InvalidDataException($"Embedded package is missing: {package.Name}");
            using var nested = entry.Open();
            var nestedDigest = Convert.ToHexString(SHA256.HashData(nested)).ToLowerInvariant();
            if (entry.Length != package.Size ||
                !string.Equals(nestedDigest, package.Sha256, StringComparison.Ordinal) ||
                !string.Equals(
                    ReleaseAsset.HashFile(Path.Combine(releaseRoot, package.Name)),
                    package.Sha256,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Embedded package bytes differ from the release asset: {package.Name}");
            }
        }
    }

    private static void RequireExact(HashSet<string> actual, IEnumerable<string> expected, string asset)
    {
        var expectedSet = expected.ToHashSet(StringComparer.Ordinal);
        if (!actual.SetEquals(expectedSet))
        {
            throw new InvalidDataException($"Unexpected archive layout in {asset}.");
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
                             (relative is "viv-server" or "viv-cli" or "viv-agent-update" ||
                              relative.EndsWith("/viv-agent", StringComparison.Ordinal));
            entry.ExternalAttributes = ((executable ? Convert.ToInt32("100755", 8) : Convert.ToInt32("100644", 8)) << 16);
            using var input = File.OpenRead(source);
            using var output = entry.Open();
            input.CopyTo(output);
        }
    }

    public static void Verify(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        var names = archive.Entries.Select(entry => entry.FullName).ToArray();
        if (names.Length == 0 || names.Distinct(StringComparer.Ordinal).Count() != names.Length ||
            !names.SequenceEqual(names.OrderBy(name => name, StringComparer.Ordinal), StringComparer.Ordinal))
        {
            throw new InvalidDataException($"ZIP entries are empty, duplicated, or not sorted: {path}");
        }

        foreach (var entry in archive.Entries)
        {
            var timestamp = entry.LastWriteTime;
            var hasCanonicalTimestamp = timestamp.Year == 1980 && timestamp.Month == 1 && timestamp.Day == 1 &&
                                        timestamp.Hour == 0 && timestamp.Minute == 0 && timestamp.Second == 0;
            if (!hasCanonicalTimestamp || entry.FullName.StartsWith("/", StringComparison.Ordinal) ||
                entry.FullName.Split('/').Any(segment => segment is "" or "." or ".."))
            {
                throw new InvalidDataException($"Unsafe or non-deterministic ZIP entry '{entry.FullName}' in {path}.");
            }
        }
    }

    public static void Extract(string path, string destination)
    {
        Verify(path);
        Directory.CreateDirectory(destination);
        using var archive = ZipFile.OpenRead(path);
        foreach (var entry in archive.Entries)
        {
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

internal sealed record ReleaseManifest(int SchemaVersion, string Version, string SourceSha, ReleaseAsset[] Assets);

internal sealed record EmbeddedPackageManifest(int SchemaVersion, string Version, ReleaseAsset[] Packages);

internal sealed record ReleaseAsset(string Name, string Component, string Rid, long Size, string Sha256)
{
    public static ReleaseAsset FromFile(string root, string name, string component, string rid)
    {
        var path = Path.Combine(root, name);
        return new ReleaseAsset(name, component, rid, new FileInfo(path).Length, HashFile(path));
    }

    public static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
