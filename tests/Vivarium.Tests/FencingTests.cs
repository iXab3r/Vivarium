using System.Diagnostics;
using Vivarium.Agent;
using Vivarium.Contracts.V1;
using Vivarium.Controller;
using Vivarium.Controller.Agents;
using Vivarium.Controller.Builds;

namespace Vivarium.Tests;

/// <summary>
/// Tier-2 fencing and re-adoption scenarios (D4): a build survives a dropped connection, its result
/// arrives through the next session, and duplicate results are idempotent.
/// </summary>
[TestFixture]
public class FencingTests
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
            // best effort
        }
    }

    [Test]
    public async Task Build_survives_a_kicked_connection_and_result_arrives_via_the_new_session()
    {
        await using var controller = await VivariumControllerHost.StartAsync(new ControllerOptions
        {
            DataDir = Path.Combine(rootDir, "controller"),
            Host = "127.0.0.1",
            Port = 0,
        });
        var enrollToken = controller.Tokens.CreateEnrollToken();

        var agent = new AgentRunner(new AgentOptions
        {
            ControllerUrl = controller.Url,
            CertFingerprintSha256 = controller.Certificate.FingerprintSha256,
            EnrollToken = enrollToken,
            DataDir = Path.Combine(rootDir, "agent"),
            HeartbeatInterval = TimeSpan.FromMilliseconds(500),
            ReconnectDelay = TimeSpan.FromMilliseconds(250),
        });

        using var cts = new CancellationTokenSource();
        var agentTask = agent.RunAsync(cts.Token);
        try
        {
            var connected = await WaitForAsync(
                () => controller.Registry.All.FirstOrDefault(a => a.Connected),
                TimeSpan.FromSeconds(20));
            controller.AuthorizeAgent(connected.AgentId);
            await agent.WaitAuthorizedAsync(TimeSpan.FromSeconds(20));
            var firstSessionId = connected.SessionId;

            var assignment = new BuildAssignment { BuildId = "b-kick" };
            assignment.Steps.Add(SleepStep(seconds: 4));
            assignment.Steps.Add(EchoStep("after-reconnect"));

            using var buildCts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var buildTask = controller.Builds.RunBuildAsync(connected.AgentId, assignment, buildCts.Token);

            // Let the sleep step start, then drop the connection mid-build.
            await Task.Delay(TimeSpan.FromSeconds(1));
            controller.KickAgent(connected.AgentId);

            // The agent reconnects with a fresh session and re-hellos its running build (D4).
            var readopted = await WaitForAsync(
                () =>
                {
                    var a = controller.Registry.Get(connected.AgentId);
                    return a is { Connected: true } && a.SessionId != firstSessionId ? a : null;
                },
                TimeSpan.FromSeconds(30));
            Assert.That(readopted.Hello.RunningBuildId, Is.EqualTo("b-kick"));
            Assert.That(readopted.Auth, Is.EqualTo(AgentAuth.Authorized), "token re-hello must skip re-enrollment");

            var result = await buildTask;
            Assert.That(result.Steps, Has.Count.EqualTo(2));
            Assert.That(result.Steps.Select(s => s.ExitCode), Is.All.Zero);
            Assert.That(controller.Builds.GetLog("b-kick"), Does.Contain("after-reconnect"),
                "the post-reconnect step's output must flow through the new session");
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
    public async Task Duplicate_build_results_are_idempotent()
    {
        var registry = new AgentRegistry();
        var agent = registry.Register(new Hello { AgentId = "a-1", SessionId = "s-1" }, AgentAuth.Authorized);
        agent.Connected = true;
        var tracker = new BuildTracker(registry);

        var task = tracker.RunBuildAsync("a-1", new BuildAssignment { BuildId = "b-dup" }, CancellationToken.None);

        tracker.OnResult(new BuildResult { BuildId = "b-dup", SessionId = "s-first" });
        tracker.OnResult(new BuildResult { BuildId = "b-dup", SessionId = "s-second" }); // late duplicate

        var result = await task;
        Assert.That(result.SessionId, Is.EqualTo("s-first"), "the first submission wins; duplicates are discarded");
    }

    private static Step SleepStep(int seconds)
    {
        var step = new Step();
        if (OperatingSystem.IsWindows())
        {
            step.Program = "cmd";
            step.Args.Add("/c");
            step.Args.Add("ping");
            step.Args.Add("-n");
            step.Args.Add((seconds + 1).ToString());
            step.Args.Add("127.0.0.1");
        }
        else
        {
            step.Program = "/bin/sh";
            step.Args.Add("-c");
            step.Args.Add($"sleep {seconds}");
        }

        return step;
    }

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
