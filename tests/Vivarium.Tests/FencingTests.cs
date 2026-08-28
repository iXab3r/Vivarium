using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Google.Protobuf;
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
        var enrollToken = await controller.Tokens.CreateEnrollTokenAsync();

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
            await controller.AuthorizeAgentAsync(connected.AgentId);
            await agent.WaitAuthorizedAsync(TimeSpan.FromSeconds(20));
            var firstSessionId = connected.SessionId;

            var assignment = new BuildAssignment { BuildId = "b-kick" };
            assignment.Steps.Add(SleepStep(seconds: 4));
            assignment.Steps.Add(EchoStep("after-reconnect"));
            assignment.Steps.Add(SleepStep(seconds: 2));

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

            await WaitUntilAsync(
                () => controller.Builds.GetLog("b-kick").Contains("after-reconnect", StringComparison.Ordinal),
                TimeSpan.FromSeconds(20));
            var result = await buildTask;
            Assert.That(result.Steps, Has.Count.EqualTo(3));
            Assert.That(result.Steps.Select(s => s.ExitCode), Is.All.Zero);
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
        using var session = new CancellationTokenSource();
        var connection = registry.Register(
            new Hello { AgentId = "a-1", SessionId = "s-1" },
            AgentAuth.Authorized,
            enabled: true,
            session);
        registry.Reconcile(connection, currentBuildId: null);
        var tracker = new BuildTracker(registry);

        var task = tracker.RunBuildAsync("a-1", new BuildAssignment { BuildId = "b-dup" }, CancellationToken.None);

        await tracker.OnResultAsync(
            new BuildResult { BuildId = "b-dup", SessionId = "s-1", StatusText = "first" },
            connection);
        await tracker.OnResultAsync(
            new BuildResult { BuildId = "b-dup", SessionId = "s-1", StatusText = "second" },
            connection); // late duplicate

        var result = await task;
        Assert.That(result.StatusText, Is.EqualTo("first"), "the first submission wins; duplicates are discarded");
    }

    [Test]
    public async Task Concurrent_cancellation_is_idempotent_and_preserves_the_first_reason()
    {
        await using var controller = await VivariumControllerHost.StartAsync(new ControllerOptions
        {
            DataDir = Path.Combine(rootDir, "cancel-race-controller"),
            Host = "127.0.0.1",
            Port = 0,
        });
        const string agentId = "cancel-race-agent";
        const string buildId = "cancel-race-build";
        using var session = new CancellationTokenSource();
        var connection = controller.Registry.Register(
            new Hello { AgentId = agentId, SessionId = "cancel-race-session" },
            AgentAuth.Authorized,
            enabled: true,
            session);
        controller.Registry.Reconcile(connection, currentBuildId: null);
        await controller.BuildStore.CreateAsync(
            agentId,
            connection.SessionId,
            new BuildAssignment { BuildId = buildId },
            DateTimeOffset.UtcNow);
        await controller.Builds.InitializeAsync();
        await controller.Builds.OnAgentReconnectedAsync(connection, buildId);

        var first = controller.Builds.CancelBuildAsync(buildId, "first reason");
        var second = controller.Builds.CancelBuildAsync(buildId, "second reason");
        var results = await Task.WhenAll(first, second);
        var persisted = await controller.BuildStore.GetAsync(buildId);

        Assert.Multiple(() =>
        {
            Assert.That(results, Is.All.True);
            Assert.That(persisted?.State, Is.EqualTo(TrackedBuildState.CancelRequested));
            Assert.That(persisted?.CancellationReason, Is.EqualTo("first reason"));
            Assert.That(controller.Builds.GetSnapshots().Single(build => build.BuildId == buildId)
                .CancellationReason, Is.EqualTo("first reason"));
        });

        await controller.Builds.OnResultAsync(new BuildResult
        {
            BuildId = buildId,
            SessionId = connection.SessionId,
            Outcome = BuildOutcome.Cancelled,
            StatusText = "first reason",
        }, connection);
        Assert.That(await controller.Builds.CancelBuildAsync(buildId, "too late"), Is.False);
    }

    [Test]
    public async Task Persisted_result_is_retried_after_agent_and_controller_restart_until_acknowledged()
    {
        var controllerDir = Path.Combine(rootDir, "controller");
        var agentDir = Path.Combine(rootDir, "agent");
        var pendingResultPath = Path.Combine(agentDir, "pending-build-result.pb");
        var options = new ControllerOptions
        {
            DataDir = controllerDir,
            Host = "127.0.0.1",
            Port = ReserveTcpPort(),
        };

        BuildResult firstResult;
        string agentId;
        await using (var first = await VivariumControllerHost.StartAsync(options))
        {
            var enrollToken = await first.Tokens.CreateEnrollTokenAsync();
            var firstAgent = new AgentRunner(new AgentOptions
            {
                ControllerUrl = first.Url,
                CertFingerprintSha256 = first.Certificate.FingerprintSha256,
                EnrollToken = enrollToken,
                DataDir = agentDir,
                HeartbeatInterval = TimeSpan.FromMilliseconds(250),
                ReconnectDelay = TimeSpan.FromMilliseconds(250),
            });
            agentId = firstAgent.AgentId;

            using var firstAgentCts = new CancellationTokenSource();
            var firstAgentTask = firstAgent.RunAsync(firstAgentCts.Token);
            try
            {
                var connected = await WaitForAsync(
                    () => first.Registry.Get(agentId) is { Connected: true, Reconciled: true } value
                        ? value
                        : null,
                    TimeSpan.FromSeconds(20));
                await first.AuthorizeAgentAsync(connected.AgentId);
                await firstAgent.WaitAuthorizedAsync(TimeSpan.FromSeconds(20));

                var assignment = new BuildAssignment { BuildId = "b-result-ack" };
                assignment.Steps.Add(EchoStep("persist-before-ack"));
                using var resultCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                firstResult = await first.Builds.RunBuildAsync(agentId, assignment, resultCts.Token);
                await WaitUntilAsync(() => !File.Exists(pendingResultPath), TimeSpan.FromSeconds(10));
            }
            finally
            {
                firstAgentCts.Cancel();
                try
                {
                    await firstAgentTask;
                }
                catch (OperationCanceledException)
                {
                }
            }
        }

        // Recreate the exact crash window: SQLite has committed the first result, but the agent
        // restarted before applying its ACK and therefore still has the terminal result on disk.
        File.WriteAllBytes(pendingResultPath, firstResult.ToByteArray());

        await using var restarted = await VivariumControllerHost.StartAsync(options);
        var retryingAgent = new AgentRunner(new AgentOptions
        {
            ControllerUrl = restarted.Url,
            CertFingerprintSha256 = restarted.Certificate.FingerprintSha256,
            DataDir = agentDir,
            HeartbeatInterval = TimeSpan.FromMilliseconds(250),
            ReconnectDelay = TimeSpan.FromMilliseconds(250),
        });
        using var retryCts = new CancellationTokenSource();
        var retryTask = retryingAgent.RunAsync(retryCts.Token);
        try
        {
            await WaitUntilAsync(() => !File.Exists(pendingResultPath), TimeSpan.FromSeconds(20));
            var persisted = await restarted.BuildStore.GetAsync(firstResult.BuildId);
            var agent = restarted.Registry.Get(agentId);
            Assert.Multiple(() =>
            {
                Assert.That(persisted?.Result?.StatusText, Is.EqualTo(firstResult.StatusText));
                Assert.That(agent?.Activity, Is.EqualTo(AgentActivity.Idle));
                Assert.That(agent?.CurrentBuildId, Is.Null);
            });
        }
        finally
        {
            retryCts.Cancel();
            try
            {
                await retryTask;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    [Test]
    public async Task Cancellation_survives_controller_restart_and_is_replayed_after_reconnect()
    {
        var controllerDir = Path.Combine(rootDir, "controller");
        var agentDir = Path.Combine(rootDir, "agent");
        var port = ReserveTcpPort();
        var options = new ControllerOptions
        {
            DataDir = controllerDir,
            Host = "127.0.0.1",
            Port = port,
        };

        using var agentCts = new CancellationTokenSource();
        using var firstWaitCts = new CancellationTokenSource();
        Task? agentTask = null;
        Task<BuildResult>? firstWait = null;
        string agentId;

        var first = await VivariumControllerHost.StartAsync(options);
        try
        {
            var enrollToken = await first.Tokens.CreateEnrollTokenAsync();
            var agent = new AgentRunner(new AgentOptions
            {
                ControllerUrl = first.Url,
                CertFingerprintSha256 = first.Certificate.FingerprintSha256,
                EnrollToken = enrollToken,
                DataDir = agentDir,
                HeartbeatInterval = TimeSpan.FromMilliseconds(250),
                ReconnectDelay = TimeSpan.FromSeconds(5),
            });
            agentId = agent.AgentId;
            agentTask = agent.RunAsync(agentCts.Token);

            var connected = await WaitForAsync(
                () => first.Registry.Get(agentId) is { Connected: true } value ? value : null,
                TimeSpan.FromSeconds(20));
            await first.AuthorizeAgentAsync(connected.AgentId);
            await agent.WaitAuthorizedAsync(TimeSpan.FromSeconds(20));

            var assignment = new BuildAssignment { BuildId = "b-restart-cancel" };
            assignment.Steps.Add(SleepStep(seconds: 30));
            firstWait = first.Builds.RunBuildAsync(agentId, assignment, firstWaitCts.Token);
            await WaitUntilAsync(
                () => first.Builds.GetLog(assignment.BuildId).Contains("RUNNING", StringComparison.Ordinal),
                TimeSpan.FromSeconds(10));

            first.KickAgent(agentId);
            await WaitUntilAsync(
                () => first.Registry.Get(agentId)?.Connected == false,
                TimeSpan.FromSeconds(10));
            Assert.That(
                await first.Builds.CancelBuildAsync(assignment.BuildId, "survive controller restart"),
                Is.True);
            Assert.That(
                (await first.BuildStore.GetAsync(assignment.BuildId))?.State,
                Is.EqualTo(TrackedBuildState.CancelRequested));
        }
        finally
        {
            await first.DisposeAsync();
            firstWaitCts.Cancel();
            if (firstWait != null)
            {
                try
                {
                    await firstWait;
                }
                catch (OperationCanceledException)
                {
                }
            }
        }

        try
        {
            await using var restarted = await VivariumControllerHost.StartAsync(options);
            var restoredAgent = (await restarted.AgentAdministration.ListAsync())
                .Single(agent => agent.AgentId == agentId);
            Assert.Multiple(() =>
            {
                Assert.That(restoredAgent.Connected, Is.False);
                Assert.That(restoredAgent.Activity, Is.EqualTo(AgentActivity.Building));
                Assert.That(restoredAgent.CurrentBuildId, Is.EqualTo("b-restart-cancel"));
            });
            var deleteActive = Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await restarted.AgentAdministration.DeleteAsync(agentId));
            Assert.That(deleteActive!.Message, Does.Contain("stop the build"));

            await WaitForAsync(
                () => restarted.Registry.Get(agentId) is { Connected: true } value ? value : null,
                TimeSpan.FromSeconds(30));

            using var resultCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var result = await restarted.Builds.WaitForResultAsync("b-restart-cancel", resultCts.Token);
            var persisted = await restarted.BuildStore.GetAsync(result.BuildId);
            Assert.Multiple(() =>
            {
                Assert.That(result.Outcome, Is.EqualTo(BuildOutcome.Cancelled));
                Assert.That(result.StatusText, Is.EqualTo("survive controller restart"));
                Assert.That(persisted?.State, Is.EqualTo(TrackedBuildState.Finished));
                Assert.That(persisted?.Result?.Outcome, Is.EqualTo(BuildOutcome.Cancelled));
            });
        }
        finally
        {
            agentCts.Cancel();
            if (agentTask != null)
            {
                try
                {
                    await agentTask;
                }
                catch (OperationCanceledException)
                {
                }
            }
        }

        await using var verification = await VivariumControllerHost.StartAsync(options);
        using var verificationCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var reloadedResult = await verification.Builds.WaitForResultAsync(
            "b-restart-cancel", verificationCts.Token);
        Assert.That(reloadedResult.Outcome, Is.EqualTo(BuildOutcome.Cancelled),
            "terminal results must remain queryable after another controller restart");
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

    private static async Task WaitUntilAsync(Func<bool> probe, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (probe())
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new AssertionException("condition not reached within timeout");
    }

    private static int ReserveTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
