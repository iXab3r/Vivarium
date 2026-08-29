using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vivarium.Contracts.V1;
using Vivarium.Controller;
using Vivarium.Controller.Rest.Builds;
using Vivarium.Controller.Rest.Builds.Mutations;

namespace Vivarium.Tests;

[TestFixture]
public sealed class BuildMutationRestApiTests
{
    private string rootDir = null!;

    [SetUp]
    public void SetUp()
    {
        rootDir = Path.Combine(
            Path.GetTempPath(), "vivarium-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootDir);
    }

    [TearDown]
    public void TearDown() => BuildMutationRestHarness.DeleteDirectory(rootDir);

    [Test]
    public async Task Submission_replays_exact_receipt_across_restart_and_rejects_key_reuse()
    {
        const string idempotencyKey = "rest-build-restart-1";
        string adminToken;
        string firstJson;
        string firstEtag;
        string firstLocation;
        BuildSubmissionRequest request;

        await using (var first = await BuildMutationRestHarness.StartAsync(rootDir))
        {
            adminToken = first.Tokens.AdminToken;
            await BuildMutationRestHarness.RegisterAuthorizedAgentAsync(first, "agent-rest-build");
            using var http = BuildMutationRestHarness.CreateClient(first, adminToken);
            var stagingId = await BuildMutationRestHarness.CreateUploadedPlanAsync(
                http, "project-a", "plan-restart-1");
            request = BuildMutationRestHarness.CreateRequest(
                "project-a", "debug", stagingId);

            using var firstResponse = await BuildMutationRestHarness.SubmitAsync(
                http, request, idempotencyKey);
            firstJson = await firstResponse.Content.ReadAsStringAsync();
            firstEtag = firstResponse.Headers.ETag?.Tag
                ?? throw new AssertionException("submission did not return an ETag");
            firstLocation = firstResponse.Headers.Location?.ToString()
                ?? throw new AssertionException("submission did not return Location");
            Assert.That(firstResponse.StatusCode, Is.EqualTo(HttpStatusCode.Created));

            using var retry = await BuildMutationRestHarness.SubmitAsync(
                http, request, idempotencyKey);
            var retryJson = await retry.Content.ReadAsStringAsync();
            Assert.Multiple(() =>
            {
                Assert.That(retry.StatusCode, Is.EqualTo(HttpStatusCode.Created));
                Assert.That(retry.Headers.ETag?.Tag, Is.EqualTo(firstEtag));
                Assert.That(retry.Headers.Location?.ToString(), Is.EqualTo(firstLocation));
                Assert.That(retryJson, Is.EqualTo(firstJson));
            });

            using var conflict = await BuildMutationRestHarness.SubmitAsync(
                http,
                request with { Configuration = "release" },
                idempotencyKey);
            Assert.That(conflict.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
            var problem = await conflict.Content.ReadFromJsonAsync<JsonElement>();
            Assert.That(problem.GetProperty("code").GetString(), Is.EqualTo("idempotency_key_reused"));
        }

        await using (var restarted = await BuildMutationRestHarness.StartAsync(rootDir))
        {
            using var http = BuildMutationRestHarness.CreateClient(restarted, adminToken);
            using var replay = await BuildMutationRestHarness.SubmitAsync(
                http, request!, idempotencyKey);
            var replayJson = await replay.Content.ReadAsStringAsync();
            Assert.Multiple(() =>
            {
                Assert.That(replay.StatusCode, Is.EqualTo(HttpStatusCode.Created));
                Assert.That(replay.Headers.ETag?.Tag, Is.EqualTo(firstEtag));
                Assert.That(replay.Headers.Location?.ToString(), Is.EqualTo(firstLocation));
                Assert.That(replayJson, Is.EqualTo(firstJson));
            });
        }
    }

    [Test]
    public async Task Cancellation_is_authenticated_convergent_and_preserves_first_reason()
    {
        await using var controller = await BuildMutationRestHarness.StartAsync(rootDir);
        await BuildMutationRestHarness.RegisterAuthorizedAgentAsync(controller, "agent-cancel");
        using var http = BuildMutationRestHarness.CreateClient(
            controller, controller.Tokens.AdminToken);
        var stagingId = await BuildMutationRestHarness.CreateUploadedPlanAsync(
            http, "project-cancel", "plan-cancel-1");
        using var submitted = await BuildMutationRestHarness.SubmitAsync(
            http,
            BuildMutationRestHarness.CreateRequest(
                "project-cancel", "configuration-cancel", stagingId),
            "build-cancel-1");
        var created = await submitted.Content.ReadFromJsonAsync<BuildResource>()
            ?? throw new AssertionException("Build response was empty");
        await Task.Delay(5);

        using var first = await http.PutAsJsonAsync(
            $"/api/v1/builds/{created.Id}/cancellation",
            new BuildCancellationRequest("operator requested stop"));
        var cancelled = await first.Content.ReadFromJsonAsync<BuildResource>()
            ?? throw new AssertionException("cancellation response was empty");
        using var duplicate = await http.PutAsJsonAsync(
            $"/api/v1/builds/{created.Id}/cancellation",
            new BuildCancellationRequest("different later reason"));
        var replay = await duplicate.Content.ReadFromJsonAsync<BuildResource>()
            ?? throw new AssertionException("cancellation replay was empty");

        Assert.Multiple(() =>
        {
            Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(duplicate.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(cancelled.State, Is.EqualTo("finished"));
            Assert.That(cancelled.Outcome, Is.EqualTo("cancelled"));
            Assert.That(cancelled.Children.Single().CancellationReason,
                Is.EqualTo("operator requested stop"));
            Assert.That(replay.RuntimeRevision, Is.EqualTo(cancelled.RuntimeRevision));
            Assert.That(replay.State, Is.EqualTo(cancelled.State));
            Assert.That(replay.Outcome, Is.EqualTo(cancelled.Outcome));
            Assert.That(replay.Children.Single().CancellationReason,
                Is.EqualTo(cancelled.Children.Single().CancellationReason));
            Assert.That(duplicate.Headers.ETag?.Tag, Is.EqualTo(first.Headers.ETag?.Tag));
        });

        using var anonymous = BuildMutationRestHarness.CreateClient(controller, bearerToken: null);
        using var denied = await anonymous.PutAsJsonAsync(
            $"/api/v1/builds/{created.Id}/cancellation",
            new BuildCancellationRequest("unauthenticated"));
        Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task Submission_requires_bounded_identity_and_explicit_assignment_fields()
    {
        await using var controller = await BuildMutationRestHarness.StartAsync(rootDir);
        using var http = BuildMutationRestHarness.CreateClient(
            controller, controller.Tokens.AdminToken);
        using var missingKey = await http.PostAsJsonAsync(
            "/api/v1/builds",
            new BuildSubmissionRequest("p", "c", [1], "staging", []));
        Assert.That(missingKey.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        using var invalid = new HttpRequestMessage(HttpMethod.Post, "/api/v1/builds")
        {
            Content = JsonContent.Create(new BuildSubmissionRequest(
                "p", "c", [1], "staging", [])),
        };
        invalid.Headers.TryAddWithoutValidation("Idempotency-Key", "contains space");
        using var invalidResponse = await http.SendAsync(invalid);
        Assert.That(invalidResponse.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        using var malformed = new HttpRequestMessage(HttpMethod.Post, "/api/v1/builds")
        {
            Content = JsonContent.Create(new BuildSubmissionRequest(
                "p", "c", [1], "staging", [])),
        };
        malformed.Headers.Add("Idempotency-Key", "valid-key");
        using var malformedResponse = await http.SendAsync(malformed);
        Assert.That(malformedResponse.StatusCode, Is.EqualTo(HttpStatusCode.UnprocessableEntity));
    }
}

internal static class BuildMutationRestHarness
{
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("vivarium-rest-payload");
    internal static readonly string PayloadSha256 =
        Convert.ToHexStringLower(SHA256.HashData(Payload));

    public static Task<VivariumControllerHost> StartAsync(string rootDir) =>
        VivariumControllerHost.StartAsync(new ControllerOptions
        {
            DataDir = Path.Combine(rootDir, "controller"),
            Host = "127.0.0.1",
            Port = 0,
        });

    public static HttpClient CreateClient(
        VivariumControllerHost controller,
        string? bearerToken)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                certificate is not null &&
                Convert.ToHexString(SHA256.HashData(certificate.RawData)).Equals(
                    controller.Certificate.FingerprintSha256,
                    StringComparison.OrdinalIgnoreCase),
        };
        var client = new HttpClient(handler) { BaseAddress = new Uri(controller.Url) };
        if (bearerToken is not null)
        {
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", bearerToken);
        }

        return client;
    }

    public static async Task RegisterAuthorizedAgentAsync(
        VivariumControllerHost controller,
        string agentId)
    {
        var enrollmentToken = await controller.Tokens.CreateEnrollTokenAsync();
        var hello = new Hello
        {
            AgentId = agentId,
            SessionId = $"session-{agentId}",
            EnrollToken = enrollmentToken,
            Os = new OsInfo { Family = "linux", Arch = "x64", Version = "test" },
        };
        hello.Parameters["hostname"] = agentId;
        Assert.That(await controller.Tokens.AdmitAgentAsync(hello), Is.Not.Null);
        await controller.AgentStore.ObserveHelloAsync(hello);
        Assert.That(await controller.Tokens.AuthorizeAgentAsync(agentId), Is.Not.Null);
    }

    public static async Task<string> CreateUploadedPlanAsync(
        HttpClient http,
        string projectId,
        string idempotencyKey)
    {
        using var planRequest = new HttpRequestMessage(
            HttpMethod.Post, "/api/v1/blob-upload-plans")
        {
            Content = JsonContent.Create(new
            {
                projectId,
                blobs = new[] { new { sha256 = PayloadSha256, size = Payload.LongLength } },
            }),
        };
        planRequest.Headers.Add("Idempotency-Key", idempotencyKey);
        using var planResponse = await http.SendAsync(planRequest);
        Assert.That(planResponse.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        var plan = await planResponse.Content.ReadFromJsonAsync<JsonElement>();
        var stagingId = plan.GetProperty("id").GetString()
            ?? throw new AssertionException("blob plan ID was missing");

        using var upload = new HttpRequestMessage(
            HttpMethod.Put, $"/blobs/{PayloadSha256}")
        {
            Content = new ByteArrayContent(Payload),
        };
        upload.Headers.Add("X-Vivarium-Blob-Staging-Id", stagingId);
        using var uploaded = await http.SendAsync(upload);
        Assert.That(uploaded.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        return stagingId;
    }

    public static BuildSubmissionRequest CreateRequest(
        string project,
        string configuration,
        string stagingId) => new(
        project,
        configuration,
        Encoding.UTF8.GetBytes($"project: {project}\nconfiguration: {configuration}\n"),
        stagingId,
        [
            new BuildSubmissionCellRequest(
                "linux-x64",
                string.Empty,
                "linux-x64",
                1800,
                new BuildSubmissionAssignmentRequest(
                    [new BuildSubmissionPayloadRequest(
                        PayloadSha256, "payload.bin", Archive: false, UnpackTo: string.Empty)],
                    [],
                    [],
                    "none",
                    new Dictionary<string, string>())),
        ]);

    public static Task<HttpResponseMessage> SubmitAsync(
        HttpClient http,
        BuildSubmissionRequest request,
        string idempotencyKey)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, "/api/v1/builds")
        {
            Content = JsonContent.Create(request),
        };
        message.Headers.Add("Idempotency-Key", idempotencyKey);
        return http.SendAsync(message);
    }

    public static void DeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best effort on platforms where Kestrel or SQLite releases handles a beat later.
        }
    }
}
