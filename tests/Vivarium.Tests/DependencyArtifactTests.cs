using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Vivarium.Tests;

public sealed class DependencyArtifactTests
{
    private string root = null!;
    private static readonly byte[] Package = Encoding.UTF8.GetBytes("independently pinned test package");
    private string Feed => Path.Combine(root, "artifacts", "dependencies", "nuget");
    private string Target => Path.Combine(Feed, "EyeAuras.Storage.1.0.0.nupkg");

    [SetUp]
    public void SetUp()
    {
        root = Path.Combine(Path.GetTempPath(), "vivarium-dependencies-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "dependency-artifacts.json"), JsonSerializer.Serialize(new
        {
            releases = new[] { new { repository = "owner/packages", tag = "v1.0.0", repositoryCommit = new string('a', 40),
                files = new[] { new { name = "EyeAuras.Storage.1.0.0.nupkg", sha256 = Convert.ToHexStringLower(SHA256.HashData(Package)) } } } }
        }));
    }

    [TearDown]
    public void TearDown() => Directory.Delete(root, recursive: true);

    [Test]
    public async Task Missing_feed_downloads_only_the_pinned_asset_then_publishes_verified_bytes()
    {
        using var handler = new Source(); using var http = new HttpClient(handler) { BaseAddress = new("https://api.github.com/") };
        await DependencyArtifacts.PrepareAsync(root, http);
        Assert.That(await File.ReadAllBytesAsync(Target), Is.EqualTo(Package));
        Assert.That(handler.Paths, Is.EqualTo(new[] { "/repos/owner/packages/commits/v1.0.0", "/repos/owner/packages/releases/tags/v1.0.0", "/repos/owner/packages/releases/assets/17" }));
        Assert.That(Directory.GetFiles(Feed), Has.Length.EqualTo(1));
    }

    [Test]
    public async Task Verified_cached_package_needs_no_network_or_credential()
    {
        Directory.CreateDirectory(Feed); await File.WriteAllBytesAsync(Target, Package);
        using var handler = new Source(); using var http = new HttpClient(handler) { BaseAddress = new("https://api.github.com/") };
        await DependencyArtifacts.PrepareAsync(root, http);
        Assert.That(handler.Paths, Is.Empty);
    }

    [Test]
    public void Corrupt_cached_bytes_fail_closed_without_network()
    {
        Directory.CreateDirectory(Feed); File.WriteAllText(Target, "corrupt");
        using var handler = new Source(); using var http = new HttpClient(handler) { BaseAddress = new("https://api.github.com/") };
        Assert.ThrowsAsync<InvalidDataException>(() => DependencyArtifacts.PrepareAsync(root, http));
        Assert.That(handler.Paths, Is.Empty);
    }

    [Test]
    public void Corrupt_download_never_becomes_a_feed_package_and_removes_staging()
    {
        using var handler = new Source { Corrupt = true }; using var http = new HttpClient(handler) { BaseAddress = new("https://api.github.com/") };
        Assert.ThrowsAsync<InvalidDataException>(() => DependencyArtifacts.PrepareAsync(root, http));
        Assert.That(Directory.GetFiles(Feed), Is.Empty);
    }

    [Test]
    public void Moved_release_tag_is_rejected_before_asset_download()
    {
        using var handler = new Source { MovedTag = true }; using var http = new HttpClient(handler) { BaseAddress = new("https://api.github.com/") };
        Assert.ThrowsAsync<InvalidDataException>(() => DependencyArtifacts.PrepareAsync(root, http));
        Assert.That(handler.Paths, Has.Count.EqualTo(1));
        Assert.That(Directory.GetFiles(Feed), Is.Empty);
    }

    private sealed class Source : HttpMessageHandler
    {
        internal List<string> Paths { get; } = [];
        internal bool Corrupt { get; init; }
        internal bool MovedTag { get; init; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath; Paths.Add(path);
            HttpContent body = path switch
            {
                "/repos/owner/packages/commits/v1.0.0" => new StringContent(JsonSerializer.Serialize(new { sha = new string(MovedTag ? 'b' : 'a', 40) })),
                "/repos/owner/packages/releases/tags/v1.0.0" => new StringContent("{\"assets\":[{\"name\":\"EyeAuras.Storage.1.0.0.nupkg\",\"id\":17}]}"),
                "/repos/owner/packages/releases/assets/17" => new ByteArrayContent(Corrupt ? [0] : Package),
                _ => throw new AssertionException("Unexpected dependency source request.")
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = body });
        }
    }
}
