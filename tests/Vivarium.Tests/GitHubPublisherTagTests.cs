using System.Net;
using System.Reflection;
using System.Text;

namespace Vivarium.Tests;

public sealed class GitHubPublisherTagTests
{
    // GitHub documents GET git/ref/tags/<tag> as 404 for absence. Commit lookup
    // returns 422 for an unresolved name and must never be used to infer absence.
    [TestCase(404, false)]
    [TestCase(422, true)]
    [TestCase(403, true)]
    public async Task Reference_lookup_distinguishes_absence_from_errors(int status, bool fails)
    {
        using var handler = new Source(status);
        using var http = new HttpClient(handler) { BaseAddress = new("https://api.github.com/") };
        if (fails)
            Assert.ThrowsAsync<InvalidOperationException>(() => Verify(http));
        else
            Assert.That(await Verify(http), Is.False);
        Assert.That(handler.Paths, Is.EqualTo(new[] { "/repos/owner/repo/git/ref/tags/v0.1.30" }));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Existing_reference_requires_the_exact_resolved_commit(bool moved)
    {
        using var handler = new Source(200, moved);
        using var http = new HttpClient(handler) { BaseAddress = new("https://api.github.com/") };
        if (moved)
            Assert.ThrowsAsync<InvalidOperationException>(() => Verify(http));
        else
            Assert.That(await Verify(http), Is.True);
        Assert.That(handler.Paths, Is.EqualTo(new[] {
            "/repos/owner/repo/git/ref/tags/v0.1.30", "/repos/owner/repo/commits/v0.1.30" }));
    }

    [Test]
    public void Required_tag_cannot_be_absent()
    {
        using var handler = new Source(404);
        using var http = new HttpClient(handler) { BaseAddress = new("https://api.github.com/") };
        Assert.ThrowsAsync<InvalidOperationException>(() => Verify(http, true));
    }

    private static Task<bool> Verify(HttpClient http, bool required = false)
    {
        var type = typeof(PublishTask).Assembly.GetType("GitHubReleasePublisher")!;
        var publisher = Activator.CreateInstance(type, new object?[] { null, http, "owner/repo", "0.1.30", new string('a', 40) });
        return (Task<bool>)type.GetMethod("VerifyTagIfPresentAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(publisher, new object[] { required })!;
    }

    private sealed class Source(int status, bool moved = false) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);
            var reference = path.Contains("/git/ref/", StringComparison.Ordinal);
            return Task.FromResult(new HttpResponseMessage(reference ? (HttpStatusCode)status : HttpStatusCode.OK)
            {
                Content = new StringContent(reference ? "{}" : "{\"sha\":\"" + new string(moved ? 'b' : 'a', 40) + "\"}", Encoding.UTF8, "application/json")
            });
        }
    }
}
