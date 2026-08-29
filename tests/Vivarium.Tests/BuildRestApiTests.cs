using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using Vivarium.Contracts.V1;
using Vivarium.Controller;
using Vivarium.Controller.Builds;
using Vivarium.Controller.Management;
using Vivarium.Controller.Persistence;
using Vivarium.Controller.Rest.Builds;
using Vivarium.Controller.Security;

namespace Vivarium.Tests;

[TestFixture]
public class BuildRestApiTests
{
    private static readonly DateTimeOffset TestNow =
        new(2026, 8, 29, 10, 0, 0, TimeSpan.Zero);

    private string rootDir = null!;

    [SetUp]
    public void SetUp()
    {
        rootDir = Path.Combine(Path.GetTempPath(), "vivarium-tests", Guid.NewGuid().ToString("N"));
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
            // best effort
        }
    }

    [Test]
    public async Task Build_projection_pages_filters_and_retains_assigned_agent_provenance()
    {
        await using var database = new VivariumDatabase(rootDir);
        var matrices = new MatrixBuildStore(database);
        var queue = new BuildQueueStore(database);
        var builds = new BuildStore(database);
        var first = await SubmitAsync(
            matrices, "request-first", "project-a", "debug", TestNow, "windows-first");
        var second = await SubmitAsync(
            matrices, "request-second", "project-a", "release", TestNow.AddMinutes(1), "linux-second");
        var third = await SubmitAsync(
            matrices, "request-third", "project-b", "release", TestNow.AddMinutes(2), "mac-third");

        var firstPage = await matrices.ListPageAsync(new MatrixBuildQuery(2));
        var secondPage = await matrices.ListPageAsync(new MatrixBuildQuery(
            2,
            BeforeCreatedAt: firstPage.Items[^1].CreatedAt,
            BeforeMatrixBuildId: firstPage.Items[^1].MatrixBuildId));
        Assert.Multiple(() =>
        {
            Assert.That(
                firstPage.Items.Select(item => item.MatrixBuildId),
                Is.EqualTo(new[] { third.BuildId, second.BuildId }));
            Assert.That(firstPage.HasMore, Is.True);
            Assert.That(secondPage.Items.Select(item => item.MatrixBuildId),
                Is.EqualTo(new[] { first.BuildId }));
            Assert.That(secondPage.HasMore, Is.False);
        });

        var projectPage = await matrices.ListPageAsync(new MatrixBuildQuery(10, Project: "project-a"));
        Assert.That(
            projectPage.Items.Select(item => item.MatrixBuildId),
            Is.EqualTo(new[] { second.BuildId, first.BuildId }));

        var secondChild = (await matrices.GetSnapshotAsync(second.BuildId))!.Cells.Single().BuildId;
        var claimedAt = TestNow.AddMinutes(3);
        Assert.That(await queue.TryClaimAsync(secondChild, "agent-linux", claimedAt), Is.True);
        Assert.That(await queue.TryPrepareDispatchAsync(
            secondChild,
            "agent-linux",
            "session-linux",
            claimedAt,
            "Linux builder",
            new Dictionary<string, string>
            {
                ["os.family"] = "linux",
                ["pool"] = "reported-pool",
            },
            new Dictionary<string, string>
            {
                ["pool"] = "custom-pool",
            }), Is.True);
        Assert.That(
            await queue.CompleteDispatchAsync(secondChild, "agent-linux", "session-linux"),
            Is.True);
        var result = new BuildResult
        {
            BuildId = secondChild,
            SessionId = "session-linux",
            Outcome = BuildOutcome.Failed,
            StatusText = "test failure",
        };
        result.Steps.Add(new StepResult { StepIndex = 0, ExitCode = 1 });
        result.Artifacts.Add(new Artifact
        {
            Path = "results/test.xml",
            Sha256 = new string('a', 64),
            Size = 42,
        });
        Assert.That(
            await builds.TryFinishAsync(
                result, "agent-linux", "session-linux", claimedAt.AddMinutes(1)),
            Is.True);

        var failed = await matrices.ListPageAsync(new MatrixBuildQuery(
            10,
            State: DurableBuildState.Finished,
            Outcome: BuildOutcome.Failed));
        Assert.That(failed.Items.Select(item => item.MatrixBuildId), Is.EqualTo(new[] { second.BuildId }));

        var projection = new BuildRestProjection(matrices, queue);
        var detail = await projection.GetBuildAsync(second.BuildId);
        Assert.That(detail, Is.Not.Null);
        var child = detail!.Children.Single();
        Assert.Multiple(() =>
        {
            Assert.That(detail.State, Is.EqualTo("finished"));
            Assert.That(detail.Outcome, Is.EqualTo("failed"));
            Assert.That(detail.RuntimeRevision, Does.StartWith("runtime:"));
            Assert.That(child.AssignedAgent?.Id, Is.EqualTo("agent-linux"));
            Assert.That(child.AssignedAgent?.ReportedParameters["pool"], Is.EqualTo("reported-pool"));
            Assert.That(child.AssignedAgent?.CustomParameters["pool"], Is.EqualTo("custom-pool"));
            Assert.That(child.AssignedAgent?.EffectiveParameters["pool"], Is.EqualTo("custom-pool"));
            Assert.That(child.QueueWaitMilliseconds, Is.EqualTo((long)TimeSpan.FromMinutes(2).TotalMilliseconds));
            Assert.That(child.Steps.Single().ExitCode, Is.EqualTo(1));
            Assert.That(child.Artifacts.Single().DownloadUrl,
                Is.EqualTo($"/builds/{second.BuildId}/cells/{secondChild}/artifacts/0"));
        });
    }

    [Test]
    public async Task Queue_projection_is_fifo_cursor_paged_and_filterable()
    {
        await using var database = new VivariumDatabase(rootDir);
        var matrices = new MatrixBuildStore(database);
        var queue = new BuildQueueStore(database);
        await SubmitAsync(matrices, "queue-1", "project-a", "debug", TestNow, "first");
        await SubmitAsync(matrices, "queue-2", "project-a", "release", TestNow.AddMinutes(1), "second");
        await SubmitAsync(matrices, "queue-3", "project-b", "release", TestNow.AddMinutes(2), "third");

        var firstPage = await queue.ListPendingPageAsync(new BuildQueueQuery(2));
        var secondPage = await queue.ListPendingPageAsync(new BuildQueueQuery(
            2, AfterQueueId: firstPage.Items[^1].QueueId));
        Assert.Multiple(() =>
        {
            Assert.That(firstPage.Items.Select(item => item.CellName), Is.EqualTo(new[] { "first", "second" }));
            Assert.That(firstPage.HasMore, Is.True);
            Assert.That(secondPage.Items.Select(item => item.CellName), Is.EqualTo(new[] { "third" }));
            Assert.That(secondPage.HasMore, Is.False);
        });

        var filtered = await queue.ListPendingPageAsync(new BuildQueueQuery(
            10, Project: "project-a", Configuration: "release"));
        Assert.That(filtered.Items.Select(item => item.CellName), Is.EqualTo(new[] { "second" }));

        var projection = new BuildRestProjection(matrices, queue);
        var resourcePage = await projection.ListQueueAsync(new BuildQueueQuery(10));
        Assert.Multiple(() =>
        {
            Assert.That(resourcePage.Items.Select(item => item.Id), Is.EqualTo(new[] { "1", "2", "3" }));
            Assert.That(resourcePage.Items.All(item => item.State == "queued"), Is.True);
            Assert.That(resourcePage.Items.All(item => item.BuildUrl is not null), Is.True);
            Assert.That(resourcePage.Items.All(item => item.QueueWaitMilliseconds is null), Is.True);
        });
    }

    [Test]
    public async Task Http_build_and_queue_reads_enforce_auth_cursor_problem_and_etag_contracts()
    {
        await using var controller = await VivariumControllerHost.StartAsync(new ControllerOptions
        {
            DataDir = Path.Combine(rootDir, "controller"),
            Host = "127.0.0.1",
            Port = 0,
        });
        var older = await SubmitAsync(
            controller.MatrixBuildStore,
            "http-older",
            "project-a",
            "debug",
            TestNow,
            "older");
        var newer = await SubmitAsync(
            controller.MatrixBuildStore,
            "http-newer",
            "project-a",
            "release",
            TestNow.AddMinutes(1),
            "newer");
        await SubmitAsync(
            controller.MatrixBuildStore,
            "http-other",
            "project-b",
            "release",
            TestNow.AddMinutes(2),
            "other");

        using var anonymous = PinnedClient(controller);
        using var anonymousRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/builds");
        anonymousRequest.Headers.Add(
            ManagementRequestContextFactory.CorrelationHeader,
            "rest-build-anonymous");
        var anonymousResponse = await anonymous.SendAsync(anonymousRequest);
        var anonymousProblem = JsonDocument.Parse(await anonymousResponse.Content.ReadAsStringAsync());
        Assert.Multiple(() =>
        {
            Assert.That(anonymousResponse.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(anonymousResponse.Content.Headers.ContentType?.MediaType,
                Is.EqualTo("application/problem+json"));
            Assert.That(anonymousProblem.RootElement.GetProperty("code").GetString(),
                Is.EqualTo("authentication_required"));
            Assert.That(anonymousProblem.RootElement.GetProperty("correlationId").GetString(),
                Is.EqualTo("rest-build-anonymous"));
        });

        using var submit = PinnedClient(controller);
        submit.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", controller.Tokens.SubmitToken);
        var firstResponse = await submit.GetAsync("/api/v1/builds?limit=1&project=project-a");
        var firstJson = JsonDocument.Parse(await firstResponse.Content.ReadAsStringAsync());
        var firstItem = firstJson.RootElement.GetProperty("items")[0];
        var cursor = firstJson.RootElement.GetProperty("page").GetProperty("nextCursor").GetString();
        Assert.Multiple(() =>
        {
            Assert.That(firstResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(firstItem.GetProperty("id").GetString(), Is.EqualTo(newer.BuildId));
            Assert.That(cursor, Is.Not.Null.And.Not.Empty);
            Assert.That(firstResponse.Headers.ETag, Is.Not.Null);
        });

        var nextResponse = await submit.GetAsync(
            $"/api/v1/builds?limit=1&project=project-a&cursor={Uri.EscapeDataString(cursor!)}");
        var nextJson = JsonDocument.Parse(await nextResponse.Content.ReadAsStringAsync());
        Assert.Multiple(() =>
        {
            Assert.That(nextResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(nextJson.RootElement.GetProperty("items")[0].GetProperty("id").GetString(),
                Is.EqualTo(older.BuildId));
            Assert.That(
                nextJson.RootElement.GetProperty("page").TryGetProperty("nextCursor", out _),
                Is.False);
        });

        using var conditional = new HttpRequestMessage(
            HttpMethod.Get,
            "/api/v1/builds?limit=1&project=project-a");
        conditional.Headers.IfNoneMatch.Add(firstResponse.Headers.ETag!);
        var notModified = await submit.SendAsync(conditional);
        var detail = await submit.GetAsync($"/api/v1/builds/{newer.BuildId}");
        var missing = await submit.GetAsync("/api/v1/builds/not-a-build");
        var filteredQueue = await submit.GetAsync(
            "/api/v1/queue?project=project-a&configuration=release");
        var queueJson = JsonDocument.Parse(await filteredQueue.Content.ReadAsStringAsync());
        Assert.Multiple(() =>
        {
            Assert.That(notModified.StatusCode, Is.EqualTo(HttpStatusCode.NotModified));
            Assert.That(detail.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(detail.Headers.ETag, Is.Not.Null);
            Assert.That(missing.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(queueJson.RootElement.GetProperty("items").GetArrayLength(), Is.EqualTo(1));
            Assert.That(
                queueJson.RootElement.GetProperty("items")[0].GetProperty("configuration").GetString(),
                Is.EqualTo("release"));
        });

        var queuedChildId = (await controller.MatrixBuildStore.GetSnapshotAsync(newer.BuildId))!
            .Cells.Single().BuildId;
        Assert.That(
            await controller.BuildQueueStore.TryClaimAsync(
                queuedChildId, "claim-only-agent", TestNow.AddMinutes(2)),
            Is.True);
        using var changedDetailRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/v1/builds/{newer.BuildId}");
        changedDetailRequest.Headers.IfNoneMatch.Add(detail.Headers.ETag!);
        var changedDetail = await submit.SendAsync(changedDetailRequest);
        using var changedQueueRequest = new HttpRequestMessage(
            HttpMethod.Get,
            "/api/v1/queue?project=project-a&configuration=release");
        changedQueueRequest.Headers.IfNoneMatch.Add(filteredQueue.Headers.ETag!);
        var changedQueue = await submit.SendAsync(changedQueueRequest);
        var changedQueueJson = JsonDocument.Parse(await changedQueue.Content.ReadAsStringAsync());
        Assert.Multiple(() =>
        {
            Assert.That(changedDetail.StatusCode, Is.EqualTo(HttpStatusCode.OK),
                "a queue-only claim must advance the enclosing Build validator");
            Assert.That(changedQueue.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(
                changedQueueJson.RootElement.GetProperty("items")[0].GetProperty("state").GetString(),
                Is.EqualTo("claimed"));
        });

        var agentToken = await RegisterAuthorizedAgentAsync(controller, "rest-scope-agent");
        using var denied = PinnedClient(controller);
        denied.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", agentToken);
        var forbidden = await denied.GetAsync("/api/v1/builds");
        var forbiddenProblem = JsonDocument.Parse(await forbidden.Content.ReadAsStringAsync());
        Assert.Multiple(() =>
        {
            Assert.That(forbidden.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(forbiddenProblem.RootElement.GetProperty("code").GetString(),
                Is.EqualTo("permission_denied"));
        });
    }

    [Test]
    public async Task Http_build_reads_and_runtime_etag_survive_controller_restart()
    {
        var dataDir = Path.Combine(rootDir, "restart-controller");
        var time = new FixedTimeProvider(TestNow);
        string matrixBuildId;
        string etag;
        string adminToken;
        await using (var first = await VivariumControllerHost.StartAsync(new ControllerOptions
        {
            DataDir = dataDir,
            Host = "127.0.0.1",
            Port = 0,
            TimeProvider = time,
        }))
        {
            matrixBuildId = (await SubmitAsync(
                first.MatrixBuildStore,
                "restart-build",
                "project-a",
                "restart",
                TestNow,
                "restart-cell")).BuildId;
            adminToken = first.Tokens.AdminToken;
            using var http = PinnedClient(first);
            http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", adminToken);
            var response = await http.GetAsync($"/api/v1/builds/{matrixBuildId}");
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            etag = response.Headers.ETag?.Tag
                ?? throw new AssertionException("build response did not include an ETag");
        }

        await using var restarted = await VivariumControllerHost.StartAsync(new ControllerOptions
        {
            DataDir = dataDir,
            Host = "127.0.0.1",
            Port = 0,
            TimeProvider = time,
        });
        using var restartedHttp = PinnedClient(restarted);
        restartedHttp.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", adminToken);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/builds/{matrixBuildId}");
        request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(etag));
        var responseAfterRestart = await restartedHttp.SendAsync(request);
        Assert.That(responseAfterRestart.StatusCode, Is.EqualTo(HttpStatusCode.NotModified));
    }

    private static Task<BuildRef> SubmitAsync(
        MatrixBuildStore store,
        string requestId,
        string project,
        string configuration,
        DateTimeOffset now,
        string cellName)
    {
        var request = new SubmitBuildRequest
        {
            RequestId = requestId,
            Project = project,
            Configuration = configuration,
            DefinitionSnapshot = ByteString.CopyFromUtf8($"project: {project}"),
        };
        request.Cells.Add(new MatrixBuildCell
        {
            Name = cellName,
            Rid = "test-rid",
            AgentExpression = string.Empty,
            Assignment = new BuildAssignment(),
        });
        return store.SubmitAsync(
            ManagementPrincipal.LegacyAdmin,
            request,
            requestHash: $"hash-{requestId}",
            definitionHash: $"definition-{requestId}",
            now,
            TimeSpan.FromMinutes(30),
            auditEventFactory: null);
    }

    private static async Task<string> RegisterAuthorizedAgentAsync(
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
        return await controller.Tokens.AuthorizeAgentAsync(agentId)
            ?? throw new AssertionException("authorized agent token was not created");
    }

    private static HttpClient PinnedClient(VivariumControllerHost controller)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                certificate is not null &&
                Convert.ToHexString(SHA256.HashData(certificate.RawData))
                    .Equals(
                        controller.Certificate.FingerprintSha256,
                        StringComparison.OrdinalIgnoreCase),
        };
        return new HttpClient(handler) { BaseAddress = new Uri(controller.Url) };
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
