using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vivarium.Agent;
using Vivarium.Contracts.V1;
using Vivarium.Controller;
using Vivarium.Controller.Agents;
using Vivarium.Controller.Rest.Builds.Mutations;

namespace Vivarium.Tests;

/// <summary>
/// Tier-2 in-process protocol tests (D20): a real Kestrel controller with pinned self-signed TLS on
/// a loopback port, and the real agent session loop talking to it. No hypervisors anywhere.
/// </summary>
[TestFixture]
public class SessionLoopTests
{
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
            // best effort — Windows can hold file locks a beat longer than the processes
        }
    }

    [Test]
    public async Task Agent_enrolls_gets_authorized_and_runs_a_build()
    {
        await using var controller = await StartControllerAsync();
        var enrollToken = await controller.Tokens.CreateEnrollTokenAsync();

        var agent = new AgentRunner(new AgentOptions
        {
            ControllerUrl = controller.Url,
            CertFingerprintSha256 = controller.Certificate.FingerprintSha256,
            EnrollToken = enrollToken,
            DataDir = Path.Combine(rootDir, "agent"),
            HeartbeatInterval = TimeSpan.FromSeconds(1),
            ReconnectDelay = TimeSpan.FromMilliseconds(250),
        });

        using var cts = new CancellationTokenSource();
        var agentTask = agent.RunAsync(cts.Token);
        try
        {
            // TeamCity flow: the agent appears unauthorized, visible but never scheduled (D8).
            var connected = await WaitForAsync(
                () => controller.Registry.All.FirstOrDefault(a => a.Connected),
                TimeSpan.FromSeconds(20));
            Assert.That(connected.Auth, Is.EqualTo(AgentAuth.Unauthorized));
            Assert.That(connected.Hello.Parameters["os.family"], Is.Not.Empty);
            Assert.That(connected.Hello.SessionId, Is.Not.Empty);
            var enrollmentSessionId = connected.Hello.SessionId;

            // The Authorize click issues a token over the live session (D7).
            await controller.AuthorizeAgentAsync(connected.AgentId);
            await agent.WaitAuthorizedAsync(TimeSpan.FromSeconds(20));
            var bearerSession = controller.Registry.Get(connected.AgentId);
            Assert.Multiple(() =>
            {
                Assert.That(bearerSession?.Auth, Is.EqualTo(AgentAuth.Authorized));
                Assert.That(bearerSession?.Hello.SessionId, Is.Not.EqualTo(enrollmentSessionId));
            });
            var enrollmentReplay = connected.Hello.Clone();
            enrollmentReplay.SessionId = "replayed-enrollment-session";
            enrollmentReplay.EnrollToken = enrollToken;
            enrollmentReplay.AuthToken = "";
            Assert.That(await controller.Tokens.AdmitAgentAsync(enrollmentReplay), Is.Null);

            // Payload in, step run, artifacts out — the whole D3 contract.
            var payload = "hello vivarium"u8.ToArray();
            using var http = PinnedClient(controller);
            http.DefaultRequestHeaders.Authorization =
                new("Bearer", controller.Tokens.AdminToken);
            var staged = await CreateUploadedPlanAsync(
                http,
                "session-loop",
                "session-loop-payload",
                payload);
            var submission = BuildRequest(staged);
            using var submitted = new HttpRequestMessage(HttpMethod.Post, "/api/v1/builds")
            {
                Content = JsonContent.Create(submission),
            };
            submitted.Headers.Add("Idempotency-Key", "session-loop-build");
            using var submittedResponse = await http.SendAsync(submitted);
            Assert.That(
                submittedResponse.StatusCode,
                Is.EqualTo(HttpStatusCode.Created),
                await submittedResponse.Content.ReadAsStringAsync());
            var submittedResource = await submittedResponse.Content.ReadFromJsonAsync<JsonElement>();
            var matrixBuildId = submittedResource.GetProperty("id").GetString()
                ?? throw new AssertionException("submitted Build ID was missing");

            var completed = await WaitForAsync(
                async () =>
                {
                    var snapshot = await controller.MatrixBuildStore.GetSnapshotAsync(matrixBuildId);
                    return snapshot?.State == DurableBuildState.Finished ? snapshot : null;
                },
                TimeSpan.FromSeconds(60));
            var result = completed.Cells.Single();

            Assert.That(result.AgentId, Is.EqualTo(connected.AgentId));
            Assert.That(result.Steps, Has.Count.EqualTo(1));
            Assert.That(result.Steps[0].ExitCode, Is.Zero);
            Assert.That(result.Steps[0].TimedOut, Is.False);
            Assert.That(result.Outcome, Is.EqualTo(BuildOutcome.Succeeded));

            var artifact = result.Artifacts.Single(a => a.Path == "payload/hello.txt");
            var sha = staged.Sha256;
            Assert.That(artifact.Sha256, Is.EqualTo(sha).IgnoreCase);
            Assert.That(artifact.Size, Is.EqualTo(payload.Length));
            Assert.That(controller.Blobs.Contains(sha), Is.True);

            Assert.That(controller.Builds.GetLog(result.BuildId), Is.Empty,
                "terminal build log buffers are evicted after the durable result is acknowledged");
        }
        finally
        {
            cts.Cancel();
            try
            {
                await agentTask;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    [Test]
    public async Task Two_agents_run_two_matrix_cells_concurrently_on_one_controller()
    {
        await using var controller = await StartControllerAsync();
        var first = new AgentRunner(new AgentOptions
        {
            ControllerUrl = controller.Url,
            CertFingerprintSha256 = controller.Certificate.FingerprintSha256,
            EnrollToken = await controller.Tokens.CreateEnrollTokenAsync(),
            DataDir = Path.Combine(rootDir, "agent-one"),
            HeartbeatInterval = TimeSpan.FromMilliseconds(250),
            ReconnectDelay = TimeSpan.FromMilliseconds(250),
        });
        var second = new AgentRunner(new AgentOptions
        {
            ControllerUrl = controller.Url,
            CertFingerprintSha256 = controller.Certificate.FingerprintSha256,
            EnrollToken = await controller.Tokens.CreateEnrollTokenAsync(),
            DataDir = Path.Combine(rootDir, "agent-two"),
            HeartbeatInterval = TimeSpan.FromMilliseconds(250),
            ReconnectDelay = TimeSpan.FromMilliseconds(250),
        });
        using var cts = new CancellationTokenSource();
        var firstTask = first.RunAsync(cts.Token);
        var secondTask = second.RunAsync(cts.Token);
        try
        {
            var connected = await WaitForAsync(
                () => controller.Registry.All.Count(agent => agent.Connected) == 2
                    ? controller.Registry.All.Where(agent => agent.Connected).ToArray()
                    : null,
                TimeSpan.FromSeconds(20));
            Assert.That(
                connected.Select(agent => agent.AgentId).Distinct().ToArray(),
                Has.Length.EqualTo(2));
            foreach (var agent in connected)
            {
                await controller.AuthorizeAgentAsync(agent.AgentId);
            }

            await Task.WhenAll(
                first.WaitAuthorizedAsync(TimeSpan.FromSeconds(20)),
                second.WaitAuthorizedAsync(TimeSpan.FromSeconds(20)));

            using var http = PinnedClient(controller);
            http.DefaultRequestHeaders.Authorization =
                new("Bearer", controller.Tokens.AdminToken);
            var staged = await CreateUploadedPlanAsync(
                http,
                "two-agent-proof",
                "two-agent-proof-payload",
                "two agents"u8.ToArray());
            using var submit = new HttpRequestMessage(HttpMethod.Post, "/api/v1/builds")
            {
                Content = JsonContent.Create(TwoAgentBuildRequest(staged)),
            };
            submit.Headers.Add("Idempotency-Key", "two-agent-proof-build");
            using var submitted = await http.SendAsync(submit);
            Assert.That(
                submitted.StatusCode,
                Is.EqualTo(HttpStatusCode.Created),
                await submitted.Content.ReadAsStringAsync());
            var resource = await submitted.Content.ReadFromJsonAsync<JsonElement>();
            var matrixBuildId = resource.GetProperty("id").GetString()
                ?? throw new AssertionException("submitted Build ID was missing");

            var concurrent = await WaitForAsync(
                async () =>
                {
                    var snapshot = await controller.MatrixBuildStore.GetSnapshotAsync(matrixBuildId);
                    return snapshot is not null &&
                           snapshot.Cells.Count(cell => cell.State == DurableBuildState.Running) == 2 &&
                           snapshot.Cells.Select(cell => cell.AgentId)
                               .Where(agentId => !string.IsNullOrEmpty(agentId))
                               .Distinct(StringComparer.Ordinal)
                               .Count() == 2
                        ? snapshot
                        : null;
                },
                TimeSpan.FromSeconds(30));
            var completed = await WaitForAsync(
                async () =>
                {
                    var snapshot = await controller.MatrixBuildStore.GetSnapshotAsync(matrixBuildId);
                    return snapshot?.State == DurableBuildState.Finished ? snapshot : null;
                },
                TimeSpan.FromSeconds(60));

            Assert.Multiple(() =>
            {
                Assert.That(concurrent.Cells, Has.Count.EqualTo(2));
                Assert.That(
                    completed.Cells.Select(cell => cell.AgentId).Distinct().ToArray(),
                    Has.Length.EqualTo(2));
                Assert.That(completed.Cells.Select(cell => cell.Outcome),
                    Is.All.EqualTo(BuildOutcome.Succeeded));
                Assert.That(completed.Cells.Select(cell => cell.AgentId),
                    Is.EquivalentTo(connected.Select(agent => agent.AgentId)));
            });
        }
        finally
        {
            cts.Cancel();
            try
            {
                await Task.WhenAll(firstTask, secondTask);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    [Test]
    public async Task Object_scoped_blob_staging_requires_bearer_and_verifies_hashes()
    {
        await using var controller = await StartControllerAsync();
        using var http = PinnedClient(controller);
        http.DefaultRequestHeaders.Authorization =
            new("Bearer", controller.Tokens.AdminToken);
        var body = new byte[] { 1, 2, 3, 4 };
        var sha = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
        var plan = await CreatePlanAsync(
            http,
            "blob-boundary",
            "blob-boundary-plan",
            sha,
            body.LongLength);

        // No bearer -> 401.
        using (var anonymousHttp = PinnedClient(controller))
        using (var anonymous = new HttpRequestMessage(HttpMethod.Put, $"/blobs/{sha}")
               { Content = new ByteArrayContent(body) })
        {
            anonymous.Headers.Add("X-Vivarium-Blob-Staging-Id", plan.StagingId);
            var response = await anonymousHttp.SendAsync(anonymous);
            Assert.That((int)response.StatusCode, Is.EqualTo(401));
        }

        // Body that does not hash to its name -> rejected, never stored (D4).
        using (var lying = new HttpRequestMessage(HttpMethod.Put, $"/blobs/{sha}"))
        {
            lying.Headers.Add("X-Vivarium-Blob-Staging-Id", plan.StagingId);
            lying.Content = new ByteArrayContent(new byte[] { 4, 3, 2, 1 });
            var response = await http.SendAsync(lying);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.UnprocessableEntity));
            Assert.That(controller.Blobs.Contains(sha), Is.False);
        }

        // The route value itself is treated as a digest, never as a filesystem path.
        using (var malformed = new HttpRequestMessage(HttpMethod.Put, "/blobs/not-a-sha256"))
        {
            malformed.Headers.Add("X-Vivarium-Blob-Staging-Id", plan.StagingId);
            malformed.Content = new ByteArrayContent(body);
            var response = await http.SendAsync(malformed);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.UnprocessableEntity));
        }

        // Honest upload -> stored. Management credentials still cannot read by digest.
        using (var honest = new HttpRequestMessage(HttpMethod.Put, $"/blobs/{sha}"))
        {
            honest.Headers.Add("X-Vivarium-Blob-Staging-Id", plan.StagingId);
            honest.Content = new ByteArrayContent(body);
            var response = await http.SendAsync(honest);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        }

        using (var read = new HttpRequestMessage(HttpMethod.Get, $"/blobs/{sha}"))
        {
            var response = await http.SendAsync(read);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        }
    }

    [Test]
    public async Task Blob_store_rejects_a_mismatched_idempotent_put_and_preserves_the_blob()
    {
        await using var controller = await StartControllerAsync();
        using var http = PinnedClient(controller);
        http.DefaultRequestHeaders.Authorization =
            new("Bearer", controller.Tokens.AdminToken);
        var original = "original blob"u8.ToArray();
        var staged = await CreateUploadedPlanAsync(
            http,
            "blob-replay",
            "blob-replay-plan",
            original);

        using (var lying = new HttpRequestMessage(HttpMethod.Put, $"/blobs/{staged.Sha256}"))
        {
            lying.Headers.Add("X-Vivarium-Blob-Staging-Id", staged.StagingId);
            lying.Content = new ByteArrayContent("different body"u8.ToArray());
            var response = await http.SendAsync(lying);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        }

        var path = controller.Blobs.GetPath(staged.Sha256)
            ?? throw new AssertionException("completed staged blob disappeared");
        Assert.That(await File.ReadAllBytesAsync(path), Is.EqualTo(original));
    }

    private Task<VivariumControllerHost> StartControllerAsync() =>
        VivariumControllerHost.StartAsync(new ControllerOptions
        {
            DataDir = Path.Combine(rootDir, "controller"),
            Host = "127.0.0.1",
            Port = 0,
        });

    private static Step EchoStep(string text)
    {
        var step = new Step();
        if (OperatingSystem.IsWindows())
        {
            step.Program = "cmd";
            step.Args.Add("/c");
            step.Args.Add("echo");
            step.Args.Add(text);
        }
        else
        {
            step.Program = "/bin/sh";
            step.Args.Add("-c");
            step.Args.Add($"echo {text}");
        }

        return step;
    }

    private static BuildSubmissionRequest BuildRequest(StagedBlob staged)
    {
        var echo = EchoStep("step-says-hello");
        return new BuildSubmissionRequest(
            "session-loop",
            "real-agent",
            Encoding.UTF8.GetBytes("project: session-loop\nconfiguration: real-agent\n"),
            staged.StagingId,
            [
                new BuildSubmissionCellRequest(
                    "local-agent",
                    string.Empty,
                    "local",
                    60,
                    new BuildSubmissionAssignmentRequest(
                        [new BuildSubmissionPayloadRequest(
                            staged.Sha256,
                            "payload/hello.txt",
                            Archive: false,
                            UnpackTo: string.Empty)],
                        [new BuildSubmissionStepRequest(
                            echo.Program,
                            echo.Args.ToArray(),
                            new Dictionary<string, string>(),
                            string.Empty,
                            TimeoutSeconds: 30,
                            Policy: "default",
                            ExpectedReboot: false)],
                        ["payload/**"],
                        "none",
                        new Dictionary<string, string>
                        {
                            ["scenario"] = "loopback",
                        }))
            ]);
    }

    private static BuildSubmissionRequest TwoAgentBuildRequest(StagedBlob staged)
    {
        var sleep = SleepStep(3);
        BuildSubmissionCellRequest Cell(string name) => new(
            name,
            string.Empty,
            "local",
            60,
            new BuildSubmissionAssignmentRequest(
                [new BuildSubmissionPayloadRequest(
                    staged.Sha256,
                    "payload/input.txt",
                    Archive: false,
                    UnpackTo: string.Empty)],
                [new BuildSubmissionStepRequest(
                    sleep.Program,
                    sleep.Args.ToArray(),
                    new Dictionary<string, string>(),
                    string.Empty,
                    TimeoutSeconds: 30,
                    Policy: "default",
                    ExpectedReboot: false)],
                [],
                "none",
                new Dictionary<string, string> { ["scenario"] = name }));

        return new BuildSubmissionRequest(
            "two-agent-proof",
            "parallel-matrix",
            Encoding.UTF8.GetBytes("project: two-agent-proof\nconfiguration: parallel-matrix\n"),
            staged.StagingId,
            [Cell("cell-one"), Cell("cell-two")]);
    }

    private static Step SleepStep(int seconds)
    {
        var step = new Step();
        if (OperatingSystem.IsWindows())
        {
            step.Program = "cmd";
            step.Args.Add(["/c", "ping", "-n", (seconds + 1).ToString(), "127.0.0.1"]);
        }
        else
        {
            step.Program = "/bin/sh";
            step.Args.Add(["-c", $"sleep {seconds}"]);
        }

        return step;
    }

    private static HttpClient PinnedClient(VivariumControllerHost controller)
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, cert, _, _) =>
                cert != null &&
                Convert.ToHexString(SHA256.HashData(cert.RawData))
                    .Equals(controller.Certificate.FingerprintSha256, StringComparison.OrdinalIgnoreCase),
        };
        return new HttpClient(handler) { BaseAddress = new Uri(controller.Url) };
    }

    private static async Task<BlobPlan> CreatePlanAsync(
        HttpClient http,
        string projectId,
        string idempotencyKey,
        string sha256,
        long size)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/blob-upload-plans")
        {
            Content = JsonContent.Create(new
            {
                projectId,
                blobs = new[] { new { sha256, size } },
            }),
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        using var response = await http.SendAsync(request);
        Assert.That(
            response.StatusCode,
            Is.EqualTo(HttpStatusCode.Created),
            await response.Content.ReadAsStringAsync());
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var item = json.GetProperty("items")[0];
        return new BlobPlan(
            json.GetProperty("id").GetString()
                ?? throw new AssertionException("blob staging ID was missing"),
            item.GetProperty("uploadUrl").GetString()
                ?? throw new AssertionException("blob upload URL was missing"),
            item.GetProperty("uploadRequired").GetBoolean());
    }

    private static async Task<StagedBlob> CreateUploadedPlanAsync(
        HttpClient http,
        string projectId,
        string idempotencyKey,
        byte[] content)
    {
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(content));
        var plan = await CreatePlanAsync(
            http,
            projectId,
            idempotencyKey,
            sha256,
            content.LongLength);
        Assert.That(plan.UploadRequired, Is.True);
        using var upload = new HttpRequestMessage(HttpMethod.Put, plan.UploadUrl)
        {
            Content = new ByteArrayContent(content),
        };
        upload.Headers.Add("X-Vivarium-Blob-Staging-Id", plan.StagingId);
        using var response = await http.SendAsync(upload);
        Assert.That(
            response.StatusCode,
            Is.EqualTo(HttpStatusCode.NoContent),
            await response.Content.ReadAsStringAsync());
        return new StagedBlob(plan.StagingId, sha256);
    }

    private static async Task<T> WaitForAsync<T>(Func<T?> probe, TimeSpan timeout) where T : class
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            var value = probe();
            if (value != null)
            {
                return value;
            }

            await Task.Delay(100);
        }

        throw new AssertionException("condition not reached within timeout");
    }

    private static async Task<T> WaitForAsync<T>(
        Func<Task<T?>> probe,
        TimeSpan timeout) where T : class
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            var value = await probe();
            if (value != null)
            {
                return value;
            }

            await Task.Delay(100);
        }

        throw new AssertionException("condition not reached within timeout");
    }

    private sealed record BlobPlan(string StagingId, string UploadUrl, bool UploadRequired);

    private sealed record StagedBlob(string StagingId, string Sha256);
}
