using System.Net;
using System.Security.Cryptography;
using Vivarium.Agent;

namespace Vivarium.Tests;

[TestFixture]
public sealed class AgentBlobClientTests
{
    private string rootDir = null!;

    [SetUp]
    public void SetUp()
    {
        rootDir = Path.Combine(
            Path.GetTempPath(),
            "vivarium-agent-blob-client-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootDir);
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            Directory.Delete(rootDir, recursive: true);
        }
        catch
        {
            // Preserve the original failure if a platform delays releasing the test file.
        }
    }

    [Test]
    public async Task Transfers_send_build_session_and_declared_artifact_size_headers()
    {
        var payload = "payload bytes"u8.ToArray();
        var artifact = "artifact bytes"u8.ToArray();
        var payloadHash = Hash(payload);
        var handler = new RecordingHandler(payload);
        using var client = new BlobClient(new Uri("https://controller.invalid"), handler)
        {
            BearerToken = "agent-secret-token",
        };
        var downloadPath = Path.Combine(rootDir, "download", "payload.bin");
        var artifactPath = Path.Combine(rootDir, "artifact.bin");
        await File.WriteAllBytesAsync(artifactPath, artifact);

        await client.DownloadAsync(
            payloadHash,
            downloadPath,
            "build-one",
            "session-one",
            CancellationToken.None);
        var artifactHash = await client.UploadAsync(
            artifactPath,
            "build-one",
            "session-one",
            CancellationToken.None);
        var downloaded = await File.ReadAllBytesAsync(downloadPath);

        Assert.Multiple(() =>
        {
            Assert.That(downloaded, Is.EqualTo(payload));
            Assert.That(artifactHash, Is.EqualTo(Hash(artifact)));
            Assert.That(handler.Requests, Has.Count.EqualTo(2));
            Assert.That(handler.Requests.Select(request => request.BuildId),
                Is.All.EqualTo("build-one"));
            Assert.That(handler.Requests.Select(request => request.SessionId),
                Is.All.EqualTo("session-one"));
            Assert.That(handler.Requests.Select(request => request.Authorization),
                Is.All.EqualTo("Bearer agent-secret-token"));
            Assert.That(handler.Requests[0].Method, Is.EqualTo(HttpMethod.Get));
            Assert.That(handler.Requests[0].DeclaredSize, Is.Null);
            Assert.That(handler.Requests[1].Method, Is.EqualTo(HttpMethod.Put));
            Assert.That(handler.Requests[1].DeclaredSize, Is.EqualTo(artifact.Length));
            Assert.That(handler.Requests[1].ContentLength, Is.EqualTo(artifact.Length));
            Assert.That(handler.Requests[1].Body, Is.EqualTo(artifact));
        });
    }

    private static string Hash(byte[] bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    private sealed record RecordedRequest(
        HttpMethod Method,
        string BuildId,
        string SessionId,
        string Authorization,
        long? DeclaredSize,
        long? ContentLength,
        byte[] Body);

    private sealed class RecordingHandler(byte[] payload) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? []
                : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            Requests.Add(new RecordedRequest(
                request.Method,
                request.Headers.GetValues(BlobClient.BuildIdHeader).Single(),
                request.Headers.GetValues(BlobClient.SessionIdHeader).Single(),
                request.Headers.Authorization!.ToString(),
                request.Headers.TryGetValues(BlobClient.DeclaredSizeHeader, out var sizes)
                    ? long.Parse(sizes.Single(), System.Globalization.CultureInfo.InvariantCulture)
                    : null,
                request.Content?.Headers.ContentLength,
                body));
            return request.Method == HttpMethod.Get
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(payload),
                }
                : new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }
}
