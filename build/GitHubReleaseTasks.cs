using Cake.Frosting;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

[TaskName("Publish")]
[TaskDescription("Publishes exact release assets through a verified draft-first GitHub release.")]
public sealed class PublishTask : AsyncFrostingTask<BuildContext>
{
    public override async Task RunAsync(BuildContext context)
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            throw new PlatformNotSupportedException("Publish runs only on the guarded Linux x64 publisher.");
        }

        var version = context.RequireReleaseVersion();
        var sourceSha = context.RequireSourceSha();
        var repository = context.RequireGitHubRepository();
        context.SetTeamCityBuildNumber();
        var token = Environment.GetEnvironmentVariable("GH_TOKEN");
        if (string.IsNullOrWhiteSpace(token) || token.Contains('%', StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Publish requires a resolved publish-only GH_TOKEN environment secret.");
        }

        var gh = await GitHubCli.ResolveAsync(context);
        var publisher = new GitHubReleasePublisher(context, gh, repository, version, sourceSha);
        await publisher.PublishAsync();
    }
}

internal sealed class GitHubReleasePublisher
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly BuildContext context;
    private readonly string gh;
    private readonly string repository;
    private readonly string version;
    private readonly string sourceSha;
    private readonly string tag;
    private readonly IReadOnlyDictionary<string, string?> environment;

    public GitHubReleasePublisher(
        BuildContext context,
        string gh,
        string repository,
        string version,
        string sourceSha)
    {
        this.context = context;
        this.gh = gh;
        this.repository = repository;
        this.version = version;
        this.sourceSha = sourceSha;
        tag = "v" + version;
        var configRoot = Path.Combine(context.OutRoot, "github-cli-config");
        Directory.CreateDirectory(configRoot);
        environment = new Dictionary<string, string?>
        {
            ["GH_CONFIG_DIR"] = configRoot,
            ["GH_PROMPT_DISABLED"] = "1",
            ["GH_NO_UPDATE_NOTIFIER"] = "1",
            ["NO_COLOR"] = "1",
        };
    }

    public async Task PublishAsync()
    {
        await VerifyImmutableReleasePolicyAsync();
        await VerifyTagCommitAsync();
        var expected = ExpectedAssets();
        var release = await GetReleaseAsync();
        if (release is null)
        {
            await CreateDraftAsync();
            release = await GetReleaseAsync()
                ?? throw new InvalidOperationException("GitHub did not return the draft immediately after creation.");
        }

        if (!release.Draft)
        {
            VerifyRemoteAssets(release, expected);
            if (!release.Immutable)
            {
                throw new InvalidOperationException("The existing published release is not immutable.");
            }
            Console.WriteLine($"GitHub release {tag} is already published with the exact expected assets.");
            return;
        }

        VerifyCompatibleDraftAssets(release, expected);
        var remoteNames = release.Assets.Select(asset => asset.Name).ToHashSet(StringComparer.Ordinal);
        var missing = expected.Where(asset => !remoteNames.Contains(asset.Name)).ToArray();
        if (missing.Length != 0)
        {
            var arguments = new List<string> { "release", "upload", tag };
            arguments.AddRange(missing.Select(asset => asset.Path));
            arguments.AddRange(["--repo", repository]);
            await RunAsync(arguments, timeoutSeconds: 1800);
        }

        release = await GetReleaseAsync()
            ?? throw new InvalidOperationException("GitHub draft disappeared after asset upload.");
        VerifyRemoteAssets(release, expected);
        await RunAsync(
            ["api", "--method", "PATCH", $"repos/{repository}/releases/{release.Id}", "-F", "draft=false"],
            timeoutSeconds: 120);

        release = await GetReleaseAsync()
            ?? throw new InvalidOperationException("GitHub release disappeared after publication.");
        VerifyRemoteAssets(release, expected);
        if (release.Draft || !release.Immutable)
        {
            throw new InvalidOperationException("GitHub did not publish the release as immutable.");
        }
    }

    private async Task VerifyImmutableReleasePolicyAsync()
    {
        var result = await RunResultAsync(["api", $"repos/{repository}/immutable-releases"]);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                "GitHub immutable releases must be enabled before publication. " + SafeError(result));
        }
        using var document = JsonDocument.Parse(result.StandardOutput);
        if (!document.RootElement.TryGetProperty("enabled", out var enabled) || !enabled.GetBoolean())
        {
            throw new InvalidOperationException("GitHub immutable releases are not enabled for the repository.");
        }
    }

    private async Task VerifyTagCommitAsync()
    {
        var result = await RunResultAsync(["api", $"repos/{repository}/commits/{tag}"]);
        EnsureSuccess(result, $"Could not resolve protected tag {tag}");
        using var document = JsonDocument.Parse(result.StandardOutput);
        var actual = document.RootElement.GetProperty("sha").GetString();
        if (!string.Equals(actual, sourceSha, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Tag {tag} resolves to {actual}, but the release artifact source is {sourceSha}.");
        }
    }

    private async Task<RemoteRelease?> GetReleaseAsync()
    {
        var result = await RunResultAsync(["api", $"repos/{repository}/releases/tags/{tag}"]);
        if (result.ExitCode != 0)
        {
            if (result.StandardError.Contains("HTTP 404", StringComparison.OrdinalIgnoreCase)) return null;
            throw new InvalidOperationException("Could not inspect GitHub release. " + SafeError(result));
        }
        var release = JsonSerializer.Deserialize<RemoteRelease>(result.StandardOutput, JsonOptions)
            ?? throw new InvalidDataException("GitHub returned an empty release document.");
        if (!string.Equals(release.TagName, tag, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"GitHub returned release tag '{release.TagName}', expected '{tag}'.");
        }
        return release;
    }

    private async Task CreateDraftAsync()
    {
        var arguments = new List<string>
        {
            "release", "create", tag,
            "--repo", repository,
            "--draft",
            "--verify-tag",
            "--title", $"Vivarium {version}",
            "--generate-notes",
        };
        if (version.Contains('-', StringComparison.Ordinal)) arguments.Add("--prerelease");
        await RunAsync(arguments, timeoutSeconds: 120);
    }

    private LocalAsset[] ExpectedAssets()
    {
        var root = ReleaseLayout.ReleaseRoot(context);
        return ReleaseLayout.ReleaseAssetNames().Select(name =>
        {
            var path = Path.Combine(root, name);
            using var stream = File.OpenRead(path);
            var digest = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            return new LocalAsset(name, path, new FileInfo(path).Length, "sha256:" + digest);
        }).ToArray();
    }

    private static void VerifyCompatibleDraftAssets(RemoteRelease release, IReadOnlyCollection<LocalAsset> expected)
    {
        var expectedByName = expected.ToDictionary(asset => asset.Name, StringComparer.Ordinal);
        foreach (var remote in release.Assets)
        {
            if (!expectedByName.TryGetValue(remote.Name, out var local) || !AssetMatches(remote, local))
            {
                throw new InvalidOperationException(
                    $"Draft release contains an unexpected or mismatched asset '{remote.Name}'; refusing to clobber it.");
            }
        }
    }

    private static void VerifyRemoteAssets(RemoteRelease release, IReadOnlyCollection<LocalAsset> expected)
    {
        VerifyCompatibleDraftAssets(release, expected);
        var expectedNames = expected.Select(asset => asset.Name).OrderBy(name => name, StringComparer.Ordinal);
        var remoteNames = release.Assets.Select(asset => asset.Name).OrderBy(name => name, StringComparer.Ordinal);
        if (!expectedNames.SequenceEqual(remoteNames, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("GitHub release does not contain the exact expected asset set.");
        }
    }

    private static bool AssetMatches(RemoteAsset remote, LocalAsset local) =>
        remote.Size == local.Size && string.Equals(remote.Digest, local.Digest, StringComparison.OrdinalIgnoreCase);

    private async Task RunAsync(IEnumerable<string> arguments, int timeoutSeconds)
    {
        var result = await RunResultAsync(arguments, timeoutSeconds);
        EnsureSuccess(result, "GitHub CLI command failed");
    }

    private Task<BuildProcessResult> RunResultAsync(IEnumerable<string> arguments, int timeoutSeconds = 120) =>
        BuildProcess.CaptureResultAsync(gh, arguments, context.Root, timeoutSeconds, environment);

    private static void EnsureSuccess(BuildProcessResult result, string message)
    {
        if (result.ExitCode != 0) throw new InvalidOperationException(message + ". " + SafeError(result));
    }

    private static string SafeError(BuildProcessResult result) =>
        string.IsNullOrWhiteSpace(result.StandardError)
            ? $"Exit code {result.ExitCode}."
            : result.StandardError;
}

internal static class GitHubCli
{
    public static async Task<string> ResolveAsync(BuildContext context)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("The locked GitHub CLI artifact is available for Linux only.");
        }

        var lockPath = Path.Combine(context.Root, "toolchains.lock.json");
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(lockPath));
        var tool = document.RootElement.GetProperty("tools").GetProperty("githubCli");
        var version = tool.GetProperty("version").GetString()
            ?? throw new InvalidDataException("githubCli version is missing from toolchains.lock.json.");
        var artifact = tool.GetProperty("artifacts").GetProperty("linux-x64");
        var url = artifact.GetProperty("url").GetString()
            ?? throw new InvalidDataException("githubCli URL is missing from toolchains.lock.json.");
        var expectedDigest = artifact.GetProperty("digest").GetString()
            ?? throw new InvalidDataException("githubCli digest is missing from toolchains.lock.json.");
        var toolRoot = Path.Combine(context.OutRoot, "tools", "github-cli", version, "linux-x64");
        var executable = Path.Combine(toolRoot, "bin", "gh");
        var marker = Path.Combine(toolRoot, ".verified-sha256");
        if (File.Exists(executable) && File.Exists(marker) &&
            string.Equals((await File.ReadAllTextAsync(marker)).Trim(), expectedDigest, StringComparison.OrdinalIgnoreCase))
        {
            return executable;
        }

        var download = Path.Combine(context.OutRoot, "downloads", $"github-cli-{version}-linux-x64.tar.gz");
        Directory.CreateDirectory(Path.GetDirectoryName(download)!);
        using (var http = new HttpClient())
        await using (var input = await http.GetStreamAsync(url))
        await using (var output = File.Create(download))
        {
            await input.CopyToAsync(output);
        }
        await using (var input = File.OpenRead(download))
        {
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(input)).ToLowerInvariant();
            if (!string.Equals(actual, expectedDigest, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"githubCli digest mismatch: expected {expectedDigest}, got {actual}.");
            }
        }

        var extractionRoot = Path.Combine(context.OutRoot, "tools", "github-cli", version, "extracting");
        BuildDirectory.Recreate(extractionRoot);
        await using (var input = File.OpenRead(download))
        using (var gzip = new GZipStream(input, CompressionMode.Decompress))
        {
            TarFile.ExtractToDirectory(gzip, extractionRoot, overwriteFiles: false);
        }
        var extracted = Path.Combine(extractionRoot, $"gh_{version}_linux_amd64");
        if (!Directory.Exists(extracted)) throw new InvalidDataException("Verified githubCli archive has an unexpected layout.");
        if (Directory.Exists(toolRoot)) Directory.Delete(toolRoot, recursive: true);
        Directory.Move(extracted, toolRoot);
        Directory.Delete(extractionRoot, recursive: true);
        File.SetUnixFileMode(
            executable,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        await File.WriteAllTextAsync(marker, expectedDigest + Environment.NewLine);
        var reported = await BuildProcess.CaptureAsync(executable, ["--version"], context.Root);
        if (!reported.Contains($"gh version {version}", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"githubCli reported '{reported}', expected version {version}.");
        }
        return executable;
    }
}

internal sealed record LocalAsset(string Name, string Path, long Size, string Digest);

internal sealed record RemoteRelease(
    long Id,
    [property: JsonPropertyName("tag_name")] string TagName,
    bool Draft,
    bool Immutable,
    RemoteAsset[] Assets);

internal sealed record RemoteAsset(string Name, long Size, string? Digest);
