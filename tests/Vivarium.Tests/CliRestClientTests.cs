using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Vivarium.Cli;
using Vivarium.Cli.Rest;
using Vivarium.Contracts.V1;

namespace Vivarium.Tests;

[TestFixture]
public sealed class CliRestClientTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Test]
    public void Submission_mapper_preserves_the_frozen_build_contract_and_normalizes_enums()
    {
        var source = Submission();

        var mapped = RestBuildRequestMapper.Create(source, "stage-1");

        var cell = mapped.Cells.Single();
        var assignment = cell.Assignment;
        var step = assignment.Steps.Single();
        Assert.Multiple(() =>
        {
            Assert.That(mapped.Project, Is.EqualTo("project-1"));
            Assert.That(mapped.Configuration, Is.EqualTo("configuration-1"));
            Assert.That(Encoding.UTF8.GetString(mapped.DefinitionSnapshot),
                Is.EqualTo("version: 1\n"));
            Assert.That(mapped.BlobStagingId, Is.EqualTo("stage-1"));
            Assert.That(cell.Name, Is.EqualTo("linux"));
            Assert.That(cell.AgentExpression, Is.EqualTo("os=linux"));
            Assert.That(cell.Rid, Is.EqualTo("linux-x64"));
            Assert.That(cell.QueueTimeoutSeconds, Is.EqualTo(90));
            Assert.That(assignment.Payload.Single(), Is.EqualTo(new RestBuildPayloadRequest(
                new string('a', 64), "payload.zip", Archive: true, UnpackTo: string.Empty)));
            Assert.That(step.Program, Is.EqualTo("dotnet"));
            Assert.That(step.Args, Is.EqualTo(new[] { "test", "suite.csproj" }));
            Assert.That(step.Env.Keys, Is.EqualTo(new[] { "A", "Z" }),
                "maps are serialized in deterministic ordinal order");
            Assert.That(step.TimeoutSeconds, Is.EqualTo(120));
            Assert.That(step.Policy, Is.EqualTo("even-if-failed"));
            Assert.That(step.ExpectedReboot, Is.True);
            Assert.That(assignment.OnFail, Is.EqualTo("keep-machine"));
            Assert.That(assignment.Collect, Is.EqualTo(new[] { "TestResults/*.trx" }));
        });
    }

    [Test]
    public async Task Mutation_boundary_uses_exact_routes_headers_and_camel_case_json()
    {
        var hash = new string('a', 64);
        var build = Build("running", outcome: null, cancellationRequested: false);
        var cancelled = Build("cancel-requested", outcome: null, cancellationRequested: true);
        var observed = new List<ObservedRequest>();
        var responses = new Queue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>>(
        [
            async (request, cancellationToken) =>
            {
                observed.Add(await ObserveAsync(request, cancellationToken));
                return JsonResponse(new RestBlobUploadPlan(
                    "stage-1",
                    DateTimeOffset.Parse("2026-08-29T12:30:00Z"),
                    [new RestBlobUploadPlanItem(hash, 4, true, $"/blobs/{hash}")]));
            },
            async (request, cancellationToken) =>
            {
                observed.Add(await ObserveAsync(request, cancellationToken));
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            },
            async (request, cancellationToken) =>
            {
                observed.Add(await ObserveAsync(request, cancellationToken));
                var response = JsonResponse(build);
                response.Headers.Location = new Uri("/api/v1/builds/matrix-1", UriKind.Relative);
                response.Headers.ETag = new EntityTagHeaderValue("\"runtime-1\"");
                return response;
            },
            async (request, cancellationToken) =>
            {
                observed.Add(await ObserveAsync(request, cancellationToken));
                var response = JsonResponse(cancelled);
                response.Headers.ETag = new EntityTagHeaderValue("\"runtime-2\"");
                return response;
            },
        ]);
        using var handler = new CallbackHandler((request, cancellationToken) =>
            responses.Dequeue()(request, cancellationToken));
        await using var client = Client(handler, disposeHandler: false);
        var tempFile = Path.Combine(Path.GetTempPath(), $"vivarium-cli-rest-{Guid.NewGuid():N}.zip");
        await File.WriteAllBytesAsync(tempFile, [1, 2, 3, 4]);
        try
        {
            var plan = await client.CreateBlobUploadPlanAsync(
                new RestBlobUploadPlanRequest(
                    "project-1",
                    [new RestBlobDescriptor(hash, 4)]),
                "plan-key",
                CancellationToken.None);
            await client.UploadBlobAsync(
                plan.Items.Single(),
                plan.Id,
                tempFile,
                CancellationToken.None);
            var submitted = await client.SubmitBuildAsync(
                RestBuildRequestMapper.Create(Submission(), plan.Id),
                "build-key",
                CancellationToken.None);
            var cancellation = await client.CancelBuildAsync(
                "matrix-1",
                "operator requested",
                CancellationToken.None);

            using var planJson = JsonDocument.Parse(observed[0].Body!);
            using var submissionJson = JsonDocument.Parse(observed[2].Body!);
            using var cancellationJson = JsonDocument.Parse(observed[3].Body!);
            Assert.Multiple(() =>
            {
                Assert.That(observed.Select(request => request.Method), Is.EqualTo(new[]
                {
                    HttpMethod.Post,
                    HttpMethod.Put,
                    HttpMethod.Post,
                    HttpMethod.Put,
                }));
                Assert.That(observed.Select(request => request.Path), Is.EqualTo(new[]
                {
                    "/api/v1/blob-upload-plans",
                    $"/blobs/{hash}",
                    "/api/v1/builds",
                    "/api/v1/builds/matrix-1/cancellation",
                }));
                Assert.That(observed.All(request => request.Authorization == "Bearer token-1"), Is.True);
                Assert.That(observed[0].IdempotencyKey, Is.EqualTo("plan-key"));
                Assert.That(observed[1].BlobStagingId, Is.EqualTo("stage-1"));
                Assert.That(observed[1].BodyBytes, Is.EqualTo(new byte[] { 1, 2, 3, 4 }));
                Assert.That(observed[2].IdempotencyKey, Is.EqualTo("build-key"));
                Assert.That(observed[3].IfMatch, Is.Null,
                    "convergent first-reason-wins cancellation has no runtime precondition in this slice");
                Assert.That(planJson.RootElement.GetProperty("projectId").GetString(),
                    Is.EqualTo("project-1"));
                Assert.That(submissionJson.RootElement.GetProperty("definitionSnapshot").GetString(),
                    Is.EqualTo(Convert.ToBase64String(Encoding.UTF8.GetBytes("version: 1\n"))));
                Assert.That(submissionJson.RootElement.GetProperty("cells")[0]
                    .GetProperty("assignment").GetProperty("steps")[0]
                    .GetProperty("policy").GetString(), Is.EqualTo("even-if-failed"));
                Assert.That(cancellationJson.RootElement.GetProperty("reason").GetString(),
                    Is.EqualTo("operator requested"));
                Assert.That(submitted.Resource.Id, Is.EqualTo("matrix-1"));
                Assert.That(submitted.ETag, Is.EqualTo("\"runtime-1\""));
                Assert.That(cancellation.Resource.State, Is.EqualTo("cancel-requested"));
                Assert.That(cancellation.Resource.CancellationRequested, Is.True);
            });
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Test]
    public async Task Agent_deployment_client_uses_current_release_operation_resources()
    {
        var package = new RestAgentPackageResource(
            "package-1", "2.0.0", "linux-x64", new string('a', 64), 123,
            DateTimeOffset.Parse("2026-08-29T12:00:00Z"), "bundled");
        var active = Upgrade("awaiting-health", package, completedAt: null);
        var completed = Upgrade(
            "succeeded", package, DateTimeOffset.Parse("2026-08-29T12:01:00Z"));
        var cancellation = active with
        {
            State = "rollback-requested",
            CancellationReason = "operator rollback",
        };
        var observed = new List<ObservedRequest>();
        var responses = new Queue<HttpResponseMessage>(
        [
            JsonResponse(active, HttpStatusCode.Accepted),
            JsonResponse(completed),
            JsonResponse(cancellation),
        ]);
        using var handler = new CallbackHandler(async (request, cancellationToken) =>
        {
            observed.Add(await ObserveAsync(request, cancellationToken));
            return responses.Dequeue();
        });
        await using var client = Client(handler, disposeHandler: false);
        var created = await client.CreateAgentUpgradeAsync(
            "agent-1",
            new RestAgentUpgradeRequest("canary", 120),
            "upgrade-key",
            CancellationToken.None);
        var read = await client.GetAgentUpgradeAsync(
            "operation-1", CancellationToken.None);
        var cancelled = await client.CancelAgentUpgradeAsync(
            "operation-1", "operator rollback", CancellationToken.None);

        using var createBody = JsonDocument.Parse(observed[0].Body!);
        using var cancellationBody = JsonDocument.Parse(observed[2].Body!);
        Assert.Multiple(() =>
        {
            Assert.That(observed.Select(item => item.Path), Is.EqualTo(new[]
            {
                "/api/v1/agents/agent-1/upgrade-operations",
                "/api/v1/agent-upgrade-operations/operation-1",
                "/api/v1/agent-upgrade-operations/operation-1/cancellation",
            }));
            Assert.That(observed[0].IdempotencyKey, Is.EqualTo("upgrade-key"));
            Assert.That(createBody.RootElement.TryGetProperty("packageId", out _), Is.False);
            Assert.That(createBody.RootElement.GetProperty("reason").GetString(),
                Is.EqualTo("canary"));
            Assert.That(created.State, Is.EqualTo("awaiting-health"));
            Assert.That(read.State, Is.EqualTo("succeeded"));
            Assert.That(cancelled.State, Is.EqualTo("rollback-requested"));
            Assert.That(cancellationBody.RootElement.GetProperty("reason").GetString(),
                Is.EqualTo("operator rollback"));
        });
    }

    [Test]
    public async Task Sse_watch_resumes_by_event_id_and_reloads_authoritative_build_resource()
    {
        var build = Build("finished", "succeeded", cancellationRequested: false);
        var envelope = new RestEventEnvelope(
            "event-2",
            2,
            DateTimeOffset.Parse("2026-08-29T12:00:00Z"),
            "build.updated",
            new RestResourceReference("build", "matrix-1", "/api/v1/builds/matrix-1"),
            "correlation-1",
            JsonSerializer.SerializeToElement(new { staleState = "running" }, JsonOptions),
            ConfigurationRevision: null,
            ObservationRevision: null,
            RuntimeRevision: "runtime:2");
        var sse = $"""
            : keepalive

            id: event-2
            event: build.updated
            data: {JsonSerializer.Serialize(envelope, JsonOptions)}


            """;
        var observed = new List<ObservedRequest>();
        var call = 0;
        using var handler = new CallbackHandler(async (request, cancellationToken) =>
        {
            observed.Add(await ObserveAsync(request, cancellationToken));
            if (call++ == 0)
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
                };
                return response;
            }

            var current = JsonResponse(build);
            current.Headers.ETag = new EntityTagHeaderValue("\"runtime-2\"");
            return current;
        });
        await using var client = Client(handler, disposeHandler: false);
        var updates = new List<RestBuildWatchUpdate>();

        await foreach (var update in client.WatchBuildAsync(
            "matrix-1",
            "event-1",
            CancellationToken.None))
        {
            updates.Add(update);
        }

        Assert.Multiple(() =>
        {
            Assert.That(observed, Has.Count.EqualTo(2));
            Assert.That(observed[0].Path,
                Is.EqualTo("/api/v1/events?topic=build&resourceId=matrix-1"));
            Assert.That(observed[0].LastEventId, Is.EqualTo("event-1"));
            Assert.That(observed[0].Accept, Does.Contain("text/event-stream"));
            Assert.That(observed[1].Path, Is.EqualTo("/api/v1/builds/matrix-1"));
            Assert.That(updates.Single().EventId, Is.EqualTo("event-2"));
            Assert.That(updates.Single().Build.State, Is.EqualTo("finished"));
            Assert.That(updates.Single().Build.Outcome, Is.EqualTo("succeeded"));
            Assert.That(updates.Single().ETag, Is.EqualTo("\"runtime-2\""));
        });
    }

    [Test]
    public void Problem_details_are_bounded_to_a_typed_client_failure()
    {
        using var handler = new CallbackHandler((_, _) => Task.FromResult(JsonResponse(
            new RestProblemResponse(
                "https://vivarium.dev/problems/build-cancellation-conflict",
                "The build cannot be cancelled",
                409,
                "The first terminal result already won.",
                "build_cancellation_conflict",
                "correlation-1"),
            HttpStatusCode.Conflict,
            "application/problem+json")));
        var exception = Assert.ThrowsAsync<VivariumRestApiException>(async () =>
        {
            await using var client = Client(handler, disposeHandler: false);
            await client.CancelBuildAsync(
                "matrix-1",
                "too late",
                CancellationToken.None);
        });

        Assert.Multiple(() =>
        {
            Assert.That(exception!.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
            Assert.That(exception.Code, Is.EqualTo("build_cancellation_conflict"));
            Assert.That(exception.CorrelationId, Is.EqualTo("correlation-1"));
            Assert.That(exception.Message, Is.EqualTo("The first terminal result already won."));
        });
    }

    [Test]
    public void Blob_upload_rejects_an_off_origin_url_before_sending_credentials()
    {
        var called = false;
        using var handler = new CallbackHandler((_, _) =>
        {
            called = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        });
        var path = Path.Combine(Path.GetTempPath(), $"vivarium-cli-rest-{Guid.NewGuid():N}.zip");
        File.WriteAllBytes(path, [1]);
        try
        {
            Assert.ThrowsAsync<InvalidDataException>(async () =>
            {
                await using var client = Client(handler, disposeHandler: false);
                await client.UploadBlobAsync(
                    new RestBlobUploadPlanItem(
                        new string('a', 64),
                        1,
                        true,
                        "https://attacker.invalid/blobs/secret"),
                    "stage-1",
                    path,
                    CancellationToken.None);
            });
            Assert.That(called, Is.False);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static VivariumRestApiClient Client(
        HttpMessageHandler handler,
        bool disposeHandler) => new(
        new EndpointSettings(
            "https://controller.example:8443",
            "SHA256:" + new string('A', 64),
            "token-1"),
        handler,
        disposeHandler);

    private static SubmitBuildRequest Submission()
    {
        var step = new Step
        {
            Program = "dotnet",
            Cwd = "src",
            TimeoutSec = 120,
            Policy = StepPolicy.EvenIfFailed,
            ExpectedReboot = true,
        };
        step.Args.Add(["test", "suite.csproj"]);
        step.Env["Z"] = "last";
        step.Env["A"] = "first";
        var assignment = new BuildAssignment { OnFail = OnFail.KeepMachine };
        assignment.Payload.Add(new Blob
        {
            Sha256 = new string('a', 64),
            FileName = "payload.zip",
            Archive = true,
            UnpackTo = string.Empty,
        });
        assignment.Steps.Add(step);
        assignment.Collect.Add("TestResults/*.trx");
        assignment.Parameters["rid"] = "linux-x64";
        var request = new SubmitBuildRequest
        {
            RequestId = "build-key",
            Project = "project-1",
            Configuration = "configuration-1",
            DefinitionSnapshot = ByteString.CopyFromUtf8("version: 1\n"),
        };
        request.Cells.Add(new MatrixBuildCell
        {
            Name = "linux",
            AgentExpression = "os=linux",
            Rid = "linux-x64",
            QueueTimeoutSec = 90,
            Assignment = assignment,
        });
        return request;
    }

    private static RestBuildResource Build(
        string state,
        string? outcome,
        bool cancellationRequested) => new(
        "matrix-1",
        "/api/v1/builds/matrix-1",
        "project-1",
        "configuration-1",
        state,
        outcome,
        cancellationRequested,
        [],
        DateTimeOffset.Parse("2026-08-29T11:00:00Z"),
        DateTimeOffset.Parse("2026-08-29T12:00:00Z"),
        cancellationRequested ? "runtime:2" : "runtime:1");

    private static RestAgentUpgradeOperationResource Upgrade(
        string state,
        RestAgentPackageResource package,
        DateTimeOffset? completedAt) => new(
        OperationId: "operation-1",
        AgentId: "agent-1",
        Package: package,
        State: state,
        Reason: "canary",
        MaintenanceFence: 1,
        PriorPackageSha256: new string('a', 64),
        StartingConnectionGeneration: 1,
        ObservedConnectionGeneration: completedAt is null ? null : 2,
        RestartAttempts: 1,
        LastDispatchConnectionGeneration: 1,
        NextRestartAt: completedAt is null
            ? DateTimeOffset.Parse("2026-08-29T12:00:10Z")
            : null,
        CancellationReason: null,
        FailureCode: null,
        ResultPackageSha256: completedAt is null ? null : package.Sha256,
        DrainHeld: completedAt is null,
        CreatedAt: DateTimeOffset.Parse("2026-08-29T12:00:00Z"),
        UpdatedAt: completedAt ?? DateTimeOffset.Parse("2026-08-29T12:00:30Z"),
        Deadline: DateTimeOffset.Parse("2026-08-29T12:10:00Z"),
        CompletedAt: completedAt,
        Events:
        [
            new RestAgentUpgradeEventResource(
                1, state, "test", completedAt is null ? 1 : 2,
                completedAt is null ? null : package.Sha256,
                DateTimeOffset.Parse("2026-08-29T12:00:00Z")),
        ]);

    private static HttpResponseMessage JsonResponse<T>(
        T value,
        HttpStatusCode status = HttpStatusCode.OK,
        string mediaType = "application/json") => new(status)
    {
        Content = new StringContent(
            JsonSerializer.Serialize(value, JsonOptions),
            Encoding.UTF8,
            mediaType),
    };

    private static async Task<ObservedRequest> ObserveAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var bytes = request.Content is null
            ? null
            : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        return new ObservedRequest(
            request.Method,
            request.RequestUri!.PathAndQuery,
            request.Headers.Authorization?.ToString(),
            Header(request, VivariumRestApiClient.IdempotencyHeader),
            Header(request, VivariumRestApiClient.BlobStagingHeader),
            Header(request, "If-Match"),
            Header(request, "Last-Event-ID"),
            string.Join(',', request.Headers.Accept.Select(value => value.MediaType)),
            request.Content?.Headers.ContentType?.MediaType,
            bytes is null ? null : Encoding.UTF8.GetString(bytes),
            bytes);
    }

    private static string? Header(HttpRequestMessage request, string name) =>
        request.Headers.TryGetValues(name, out var values) ? values.Single() : null;

    private sealed record ObservedRequest(
        HttpMethod Method,
        string Path,
        string? Authorization,
        string? IdempotencyKey,
        string? BlobStagingId,
        string? IfMatch,
        string? LastEventId,
        string Accept,
        string? ContentType,
        string? Body,
        byte[]? BodyBytes);

    private sealed class CallbackHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => callback(request, cancellationToken);
    }
}
