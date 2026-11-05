using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Vivarium.Tests;

public sealed class GitHubPublisherDraftTests
{
    [TestCase("fresh")]
    [TestCase("resume")]
    [TestCase("published")]
    [TestCase("older")]
    [TestCase("missing-fresh")]
    [TestCase("missing-resume")]
    [TestCase("missing-race")]
    public async Task Publication_preserves_exact_bytes_and_resumes_without_duplicate_creation(string mode)
    {
        await Run(mode, 200);
    }

    [TestCase("missing-conflict")]
    [TestCase("missing-denied")]
    [TestCase("missing-error")]
    public void Missing_tag_creation_never_bypasses_source_or_authority(string mode)
    {
        var failure = Assert.ThrowsAsync<InvalidOperationException>(() => Run(mode, 200));
        Assert.That(failure!.Message, Does.Contain(mode == "missing-conflict" ? "resolves to" : "Could not create tag"));
    }

    [TestCase(403)]
    [TestCase(500)]
    [TestCase(404)]
    public void Failed_listing_never_creates_a_release(int status)
    {
        Assert.ThrowsAsync<InvalidOperationException>(() => Run("resume", status));
    }

    private static async Task Run(string mode, int listStatus)
    {
        var root = Path.Combine(Path.GetTempPath(), "viv-publisher-" + Guid.NewGuid());
        Directory.CreateDirectory(Path.Combine(root, "out", "release"));
        try
        {
            var assembly = typeof(PublishTask).Assembly;
            var names = (string[])assembly.GetType("ReleaseLayout")!.GetMethod("ReleaseAssetNames")!.Invoke(null, null)!;
            var bytes = Encoding.UTF8.GetBytes("retained immutable archive bytes");
            foreach (var name in names) File.WriteAllBytes(Path.Combine(root, "out", "release", name), bytes);
            var context = (BuildContext)RuntimeHelpers.GetUninitializedObject(typeof(BuildContext));
            typeof(BuildContext).GetField("<Root>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(context, root);
            using var source = new Source(mode, listStatus, names, bytes);
            using var http = new HttpClient(source) { BaseAddress = new("https://api.github.com/") };
            var publisher = Activator.CreateInstance(assembly.GetType("GitHubReleasePublisher")!, context, http, "owner/repo", "0.1.30", new string('a', 40));
            await (Task)publisher!.GetType().GetMethod("PublishAsync")!.Invoke(publisher, null)!;
            Assert.Multiple(() => {
                Assert.That(source.Created, Is.EqualTo(mode is "fresh" or "missing-fresh" ? 1 : 0));
                Assert.That(source.Uploads, Is.EqualTo(mode == "published" ? 0 : names.Length - (mode is "fresh" or "missing-fresh" ? 0 : 1)));
                Assert.That(source.Patches, Is.EqualTo(mode == "published" ? 0 : 1));
                Assert.That(source.Refreshes, Is.EqualTo(mode == "published" ? 0 : 1));
                Assert.That(source.TagPosts, Is.EqualTo(mode.StartsWith("missing-", StringComparison.Ordinal) ? 1 : 0));
            });
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class Source(string mode, int listStatus, string[] names, byte[] bytes) : HttpMessageHandler
    {
        public int Created, Uploads, Patches, Refreshes, TagPosts;
        private bool tagExists = !mode.StartsWith("missing-", StringComparison.Ordinal);
        private bool published = mode == "published";
        private readonly HashSet<string> uploaded = mode is "fresh" or "missing-fresh" ? [] : mode == "published" ? [..names] : [names[0]];
        private object Release() => new { id = 42, tag_name = "v0.1.30", draft = !published, assets = uploaded.Select(n => new { name = n, size = bytes.Length, digest = "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() }) };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken token)
        {
            var path = r.RequestUri!.AbsolutePath;
            object body = new { };
            int status = 200;
            if (path.EndsWith("/git/ref/tags/v0.1.30")) { status = tagExists ? 200 : 404; }
            else if (path.EndsWith("/git/refs") && r.Method == HttpMethod.Post)
            {
                using var input = JsonDocument.Parse(await r.Content!.ReadAsStringAsync(token));
                Assert.That(input.RootElement.GetProperty("ref").GetString(), Is.EqualTo("refs/tags/v0.1.30"));
                Assert.That(input.RootElement.GetProperty("sha").GetString(), Is.EqualTo(new string('a', 40)));
                TagPosts++;
                status = mode == "missing-denied" ? 403 : mode == "missing-error" ? 500 : mode is "missing-race" or "missing-conflict" ? 422 : 201;
                tagExists = status is 201 or 422;
            }
            else if (path.EndsWith("/commits/v0.1.30")) body = new { sha = new string(mode == "missing-conflict" ? 'b' : 'a', 40) };
            else if (path.EndsWith("/releases/tags/v0.1.30")) { if (published) body = Release(); else status = 404; }
            else if (path.EndsWith("/releases") && r.Method == HttpMethod.Get)
            {
                status = listStatus;
                body = mode == "older" && !r.RequestUri.Query.Contains("page=2")
                    ? Enumerable.Range(0,100).Select(i => new { id = 100+i, tag_name = "other"+i, draft = true, assets = Array.Empty<object>() }).ToArray()
                    : mode is "fresh" or "missing-fresh" ? Array.Empty<object>() : new[] { Release() };
            }
            else if (path.EndsWith("/releases") && r.Method == HttpMethod.Post) { Created++; body = Release(); }
            else if (path.EndsWith("/assets") && r.Method == HttpMethod.Post)
            {
                Assert.That(await r.Content!.ReadAsByteArrayAsync(token), Is.EqualTo(bytes));
                uploaded.Add(Uri.UnescapeDataString(r.RequestUri.Query[6..])); Uploads++;
            }
            else if (path.EndsWith("/releases/42"))
            {
                if (r.Method == HttpMethod.Patch) { Patches++; published = true; } else Refreshes++;
                body = Release();
            }
            else throw new AssertionException("Unexpected request " + r.Method + " " + r.RequestUri);
            return new((HttpStatusCode)status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
        }
    }
}
