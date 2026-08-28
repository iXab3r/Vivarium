using System.Diagnostics;
using System.Security.Cryptography;
using Vivarium.Agent;
using Vivarium.Contracts.V1;
using Vivarium.Controller;
using Vivarium.Controller.Agents;

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

            // The Authorize click issues a token over the live session (D7).
            await controller.AuthorizeAgentAsync(connected.AgentId);
            await agent.WaitAuthorizedAsync(TimeSpan.FromSeconds(20));

            // Payload in, step run, artifacts out — the whole D3 contract.
            var payload = "hello vivarium"u8.ToArray();
            var sha = await UploadBlobAsync(controller, payload);

            var assignment = new BuildAssignment { BuildId = "b-0001" };
            assignment.Payload.Add(new Blob { Sha256 = sha, FileName = "payload/hello.txt" });
            assignment.Steps.Add(EchoStep("step-says-hello"));
            assignment.Collect.Add("payload/**");
            assignment.Parameters["scenario"] = "loopback";

            using var buildCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var result = await controller.Builds.RunBuildAsync(connected.AgentId, assignment, buildCts.Token);

            Assert.That(result.BuildId, Is.EqualTo("b-0001"));
            Assert.That(result.Steps, Has.Count.EqualTo(1));
            Assert.That(result.Steps[0].ExitCode, Is.Zero);
            Assert.That(result.Steps[0].TimedOut, Is.False);
            Assert.That(result.Outcome, Is.EqualTo(BuildOutcome.Succeeded));

            var artifact = result.Artifacts.Single(a => a.Path == "payload/hello.txt");
            Assert.That(artifact.Sha256, Is.EqualTo(sha).IgnoreCase);
            Assert.That(artifact.Size, Is.EqualTo(payload.Length));
            Assert.That(controller.Blobs.Contains(sha), Is.True);

            Assert.That(controller.Builds.GetLog("b-0001"), Is.Empty,
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
    public async Task Blob_store_requires_bearer_and_verifies_hashes()
    {
        await using var controller = await StartControllerAsync();
        using var http = PinnedClient(controller);
        var body = new byte[] { 1, 2, 3, 4 };
        var sha = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();

        // No bearer -> 401.
        using (var anonymous = new HttpRequestMessage(HttpMethod.Put, $"/blobs/{sha}")
               { Content = new ByteArrayContent(body) })
        {
            var response = await http.SendAsync(anonymous);
            Assert.That((int)response.StatusCode, Is.EqualTo(401));
        }

        // Body that does not hash to its name -> rejected, never stored (D4).
        using (var lying = Authorized(controller, HttpMethod.Put, $"/blobs/{new string('a', 64)}"))
        {
            lying.Content = new ByteArrayContent(body);
            var response = await http.SendAsync(lying);
            Assert.That((int)response.StatusCode, Is.EqualTo(400));
            Assert.That(controller.Blobs.Contains(new string('a', 64)), Is.False);
        }

        // The route value itself is treated as a digest, never as a filesystem path.
        using (var malformed = Authorized(controller, HttpMethod.Put, "/blobs/not-a-sha256"))
        {
            malformed.Content = new ByteArrayContent(body);
            var response = await http.SendAsync(malformed);
            Assert.That((int)response.StatusCode, Is.EqualTo(400));
        }

        // Honest upload -> stored, readable back with a bearer.
        using (var honest = Authorized(controller, HttpMethod.Put, $"/blobs/{sha}"))
        {
            honest.Content = new ByteArrayContent(body);
            var response = await http.SendAsync(honest);
            Assert.That(response.IsSuccessStatusCode, Is.True);
        }

        using (var read = Authorized(controller, HttpMethod.Get, $"/blobs/{sha}"))
        {
            var response = await http.SendAsync(read);
            Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.EqualTo(body));
        }
    }

    [Test]
    public async Task Blob_store_rejects_a_mismatched_idempotent_put_and_preserves_the_blob()
    {
        await using var controller = await StartControllerAsync();
        using var http = PinnedClient(controller);
        var original = "original blob"u8.ToArray();
        var hash = await UploadBlobAsync(controller, original);

        using (var lying = Authorized(controller, HttpMethod.Put, $"/blobs/{hash}"))
        {
            lying.Content = new ByteArrayContent("different body"u8.ToArray());
            var response = await http.SendAsync(lying);
            Assert.That((int)response.StatusCode, Is.EqualTo(400));
        }

        using var read = Authorized(controller, HttpMethod.Get, $"/blobs/{hash}");
        var readResponse = await http.SendAsync(read);
        Assert.That(readResponse.IsSuccessStatusCode, Is.True);
        Assert.That(await readResponse.Content.ReadAsByteArrayAsync(), Is.EqualTo(original));
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

    private static HttpRequestMessage Authorized(VivariumControllerHost controller, HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new("Bearer", controller.Tokens.AdminToken);
        return request;
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

    private static async Task<string> UploadBlobAsync(VivariumControllerHost controller, byte[] content)
    {
        var sha = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        using var http = PinnedClient(controller);
        using var request = Authorized(controller, HttpMethod.Put, $"/blobs/{sha}");
        request.Content = new ByteArrayContent(content);
        var response = await http.SendAsync(request);
        Assert.That(response.IsSuccessStatusCode, Is.True, await response.Content.ReadAsStringAsync());
        return sha;
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
}
