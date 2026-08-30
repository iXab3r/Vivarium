using Cake.Frosting;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

[TaskName("Publish")]
[TaskDescription("Publishes the ready Release assets to GitHub.")]
public sealed class PublishTask : AsyncFrostingTask<BuildContext>
{
    public override async Task RunAsync(BuildContext context)
    {
        var version = context.ProductVersion;
        var sourceSha = context.RequireSourceSha();
        var repository = context.RequireGitHubRepository();
        context.SetTeamCityBuildNumber();

        var token = Environment.GetEnvironmentVariable("GH_TOKEN");
        if (string.IsNullOrWhiteSpace(token) || token.Contains('%', StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Publish requires a resolved GH_TOKEN environment secret.");
        }

        using var http = GitHubReleasePublisher.CreateHttpClient(token);
        var publisher = new GitHubReleasePublisher(context, http, repository, version, sourceSha);
        await publisher.PublishAsync();
    }
}

internal sealed class GitHubReleasePublisher
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly BuildContext context;
    private readonly HttpClient http;
    private readonly string repository;
    private readonly string version;
    private readonly string sourceSha;
    private readonly string tag;

    public GitHubReleasePublisher(
        BuildContext context,
        HttpClient http,
        string repository,
        string version,
        string sourceSha)
    {
        this.context = context;
        this.http = http;
        this.repository = repository;
        this.version = version;
        this.sourceSha = sourceSha;
        tag = "v" + version;
    }

    public static HttpClient CreateHttpClient(string token)
    {
        var client = new HttpClient
        {
            BaseAddress = new Uri("https://api.github.com/"),
            Timeout = TimeSpan.FromMinutes(30),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Vivarium-Publisher/1.0");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }

    public async Task PublishAsync()
    {
        var tagExists = await VerifyTagIfPresentAsync();
        var expected = ExpectedAssets();
        var release = await GetReleaseAsync();
        if (release is null)
        {
            release = await CreateDraftAsync();
            await VerifyTagIfPresentAsync(requireTag: true);
        }
        else if (!tagExists)
        {
            throw new InvalidOperationException($"GitHub release {tag} exists, but its tag does not.");
        }

        if (!release.Draft)
        {
            VerifyRemoteAssets(release, expected);
            Console.WriteLine($"GitHub release {tag} is already published with the expected assets.");
            return;
        }

        VerifyCompatibleDraftAssets(release, expected);
        var remoteNames = release.Assets.Select(asset => asset.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var asset in expected.Where(asset => !remoteNames.Contains(asset.Name)))
        {
            await UploadAsync(release.Id, asset);
        }

        release = await GetReleaseAsync()
            ?? throw new InvalidOperationException("GitHub draft disappeared after asset upload.");
        VerifyRemoteAssets(release, expected);
        release = await PublishDraftAsync(release.Id);
        VerifyRemoteAssets(release, expected);
        if (release.Draft)
        {
            throw new InvalidOperationException("GitHub did not publish the release.");
        }
    }

    private async Task<bool> VerifyTagIfPresentAsync(bool requireTag = false)
    {
        using var response = await http.GetAsync($"repos/{repository}/commits/{tag}");
        if (response.StatusCode == HttpStatusCode.NotFound && !requireTag)
        {
            return false;
        }

        await EnsureSuccessAsync(response, $"Could not resolve tag {tag}");
        var commit = await ReadAsync<RemoteCommit>(response);
        if (!string.Equals(commit.Sha, sourceSha, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Tag {tag} resolves to {commit.Sha}, but the release source is {sourceSha}.");
        }

        return true;
    }

    private async Task<RemoteRelease?> GetReleaseAsync()
    {
        using var response = await http.GetAsync($"repos/{repository}/releases/tags/{tag}");
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, "Could not inspect GitHub release");
        var release = await ReadAsync<RemoteRelease>(response);
        if (!string.Equals(release.TagName, tag, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"GitHub returned release tag '{release.TagName}', expected '{tag}'.");
        }

        return release;
    }

    private async Task<RemoteRelease> CreateDraftAsync()
    {
        using var content = JsonContent.Create(new
        {
            tag_name = tag,
            target_commitish = sourceSha,
            name = $"Vivarium {version}",
            draft = true,
            prerelease = version.Contains('-', StringComparison.Ordinal),
            generate_release_notes = true,
        });
        using var response = await http.PostAsync($"repos/{repository}/releases", content);
        await EnsureSuccessAsync(response, $"Could not create GitHub release {tag}");
        return await ReadAsync<RemoteRelease>(response);
    }

    private async Task UploadAsync(long releaseId, LocalAsset asset)
    {
        var url = $"https://uploads.github.com/repos/{repository}/releases/{releaseId}/assets" +
            $"?name={Uri.EscapeDataString(asset.Name)}";
        await using var stream = File.OpenRead(asset.Path);
        using var content = new StreamContent(stream);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        using var response = await http.PostAsync(url, content);
        await EnsureSuccessAsync(response, $"Could not upload {asset.Name}");
    }

    private async Task<RemoteRelease> PublishDraftAsync(long releaseId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"repos/{repository}/releases/{releaseId}")
        {
            Content = JsonContent.Create(new { draft = false }),
        };
        using var response = await http.SendAsync(request);
        await EnsureSuccessAsync(response, $"Could not publish GitHub release {tag}");
        return await ReadAsync<RemoteRelease>(response);
    }

    private LocalAsset[] ExpectedAssets()
    {
        var root = ReleaseLayout.ReleaseRoot(context);
        return ReleaseLayout.ReleaseAssetNames()
            .Select(name =>
            {
                var path = Path.Combine(root, name);
                return new LocalAsset(name, path, new FileInfo(path).Length);
            })
            .ToArray();
    }

    private static void VerifyCompatibleDraftAssets(
        RemoteRelease release,
        IReadOnlyCollection<LocalAsset> expected)
    {
        var expectedByName = expected.ToDictionary(asset => asset.Name, StringComparer.Ordinal);
        foreach (var remote in release.Assets)
        {
            if (!expectedByName.TryGetValue(remote.Name, out var local) || remote.Size != local.Size)
            {
                throw new InvalidOperationException(
                    $"Draft release contains an unexpected or mismatched asset '{remote.Name}'.");
            }
        }
    }

    private static void VerifyRemoteAssets(
        RemoteRelease release,
        IReadOnlyCollection<LocalAsset> expected)
    {
        VerifyCompatibleDraftAssets(release, expected);
        var expectedNames = expected.Select(asset => asset.Name).OrderBy(name => name, StringComparer.Ordinal);
        var remoteNames = release.Assets.Select(asset => asset.Name).OrderBy(name => name, StringComparer.Ordinal);
        if (!expectedNames.SequenceEqual(remoteNames, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("GitHub release does not contain the expected asset set.");
        }
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        await using var stream = await response.Content.ReadAsStreamAsync();
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions)
            ?? throw new InvalidDataException("GitHub returned an empty response.");
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string message)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync();
        throw new InvalidOperationException($"{message}: HTTP {(int)response.StatusCode}. {body}");
    }
}

internal sealed record LocalAsset(string Name, string Path, long Size);

internal sealed record RemoteCommit(string Sha);

internal sealed record RemoteRelease(
    long Id,
    [property: JsonPropertyName("tag_name")] string TagName,
    bool Draft,
    RemoteAsset[] Assets);

internal sealed record RemoteAsset(string Name, long Size);
