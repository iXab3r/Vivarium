using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

internal static class DependencyArtifacts
{
    private sealed record Artifact(string Name, string Sha256);
    private sealed record Release(string Repository, string Tag, string RepositoryCommit, Artifact[] Files);
    private sealed record Manifest(Release[] Releases);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private const long PackageLimit = 64 * 1024 * 1024;

    internal static HttpClient CreateHttpClient()
    {
        var http = new HttpClient { BaseAddress = new("https://api.github.com/"), Timeout = TimeSpan.FromMinutes(2),
            MaxResponseContentBufferSize = PackageLimit };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Vivarium-Dependencies/1.0");
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN") ?? Environment.GetEnvironmentVariable("GH_TOKEN");
        if (!string.IsNullOrWhiteSpace(token) && !token.Contains('%'))
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return http;
    }

    internal static async Task PrepareAsync(string root, HttpClient http)
    {
        var manifest = JsonSerializer.Deserialize<Manifest>(await File.ReadAllTextAsync(Path.Combine(root, "dependency-artifacts.json")), JsonOptions)
            ?? throw new InvalidDataException("Dependency manifest is missing.");
        var feed = Path.Combine(root, "artifacts", "dependencies", "nuget");
        Directory.CreateDirectory(feed);
        foreach (var release in manifest.Releases)
        {
            if (!Regex.IsMatch(release.Repository, "^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")
                || string.IsNullOrWhiteSpace(release.Tag) || !Regex.IsMatch(release.RepositoryCommit, "^[a-f0-9]{40}$"))
                throw new InvalidDataException("Invalid dependency release identity.");
            var missing = new List<Artifact>();
            foreach (var file in release.Files)
            {
                if (!Regex.IsMatch(file.Name, "^[A-Za-z0-9.-]+\\.nupkg$") || !Regex.IsMatch(file.Sha256, "^[a-f0-9]{64}$"))
                    throw new InvalidDataException("Invalid dependency artifact identity.");
                var target = Path.Combine(feed, file.Name);
                if (File.Exists(target)) await VerifyAsync(target, file.Sha256);
                else missing.Add(file);
            }
            if (missing.Count > 0)
            {
                var prefix = $"repos/{release.Repository}/";
                using var commit = await ReadJsonAsync(http, prefix + "commits/" + Uri.EscapeDataString(release.Tag));
                if (commit.RootElement.GetProperty("sha").GetString() != release.RepositoryCommit)
                    throw new InvalidDataException("Dependency release tag no longer resolves to its pinned commit.");
                using var metadata = await ReadJsonAsync(http, prefix + "releases/tags/" + Uri.EscapeDataString(release.Tag));
                foreach (var file in missing)
                {
                    var asset = metadata.RootElement.GetProperty("assets").EnumerateArray()
                        .SingleOrDefault(item => item.GetProperty("name").GetString() == file.Name);
                    if (asset.ValueKind == JsonValueKind.Undefined)
                        throw new InvalidDataException($"Pinned dependency asset is missing: {file.Name}");
                    var id = asset.GetProperty("id").GetInt64();
                    if (id <= 0) throw new InvalidDataException("Invalid dependency asset identity.");
                    var staging = Path.Combine(feed, ".download-" + Guid.NewGuid().ToString("N"));
                    try
                    {
                        using var request = new HttpRequestMessage(HttpMethod.Get, prefix + "releases/assets/" + id);
                        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
                        using var response = await SendAsync(http, request);
                        await using (var destination = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        await using (var source = await response.Content.ReadAsStreamAsync())
                        {
                            var buffer = new byte[81920]; long total = 0; int count;
                            while ((count = await source.ReadAsync(buffer)) > 0)
                            {
                                total += count;
                                if (total > PackageLimit) throw new InvalidDataException("Dependency package exceeds 64 MiB.");
                                await destination.WriteAsync(buffer.AsMemory(0, count));
                            }
                        }
                        await VerifyAsync(staging, file.Sha256);
                        File.Move(staging, Path.Combine(feed, file.Name));
                    }
                    finally { File.Delete(staging); }
                }
            }
            foreach (var file in release.Files) Console.WriteLine($"Verified dependency {file.Name}");
        }
    }

    private static async Task VerifyAsync(string path, string expected)
    {
        await using var stream = File.OpenRead(path);
        if (stream.Length > PackageLimit || Convert.ToHexStringLower(await SHA256.HashDataAsync(stream)) != expected)
            throw new InvalidDataException($"Dependency hash differs: {Path.GetFileName(path)}");
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpClient http, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await SendAsync(http, request);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient http, HttpRequestMessage request)
    {
        try
        {
            // Buffering keeps the HttpClient byte limit and deadline over the complete transfer.
            var response = await http.SendAsync(request);
            if (response.IsSuccessStatusCode) return response;
            var status = (int)response.StatusCode;
            response.Dispose();
            throw new IOException($"Pinned dependency request failed (HTTP {status}); check read access to the manifest repositories.");
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
        { throw new IOException("Pinned dependency request failed; check GitHub availability and read credentials."); }
    }
}
