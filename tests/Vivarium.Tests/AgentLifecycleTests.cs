using System.Diagnostics;
using System.Runtime.Versioning;
using Vivarium.Agent;
using Vivarium.Contracts.V1;
using Vivarium.Controller;
using Vivarium.Controller.Agents;

namespace Vivarium.Tests;

[TestFixture]
public class AgentLifecycleTests
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
    public async Task Stop_build_cancels_the_process_and_returns_agent_to_idle()
    {
        await using var controller = await StartControllerAsync();
        var enrollToken = await controller.Tokens.CreateEnrollTokenAsync();
        var agent = NewAgent(controller, enrollToken);

        using var agentCts = new CancellationTokenSource();
        var agentTask = agent.RunAsync(agentCts.Token);
        try
        {
            var connected = await WaitForAsync(
                () => controller.Registry.All.FirstOrDefault(a => a.Connected),
                TimeSpan.FromSeconds(20));
            await controller.AuthorizeAgentAsync(connected.AgentId);
            await agent.WaitAuthorizedAsync(TimeSpan.FromSeconds(20));

            var assignment = new BuildAssignment { BuildId = "b-cancel" };
            assignment.Steps.Add(SleepStep(30));
            var buildTask = controller.Builds.RunBuildAsync(
                connected.AgentId, assignment, CancellationToken.None);

            await WaitForAsync(
                () => controller.Registry.Get(connected.AgentId)?.CurrentBuildId == "b-cancel"
                    ? controller.Registry.Get(connected.AgentId)
                    : null,
                TimeSpan.FromSeconds(10));

            var second = new BuildAssignment { BuildId = "b-overlap" };
            var overlap = Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await controller.Builds.RunBuildAsync(connected.AgentId, second, CancellationToken.None));
            Assert.That(overlap!.Message, Does.Contain("already building"));

            // TeamCity semantics: disabling prevents future assignment; it does not stop current work.
            await controller.AgentAdministration.SetEnabledAsync(connected.AgentId, false);
            await Task.Delay(250);
            Assert.Multiple(() =>
            {
                Assert.That(buildTask.IsCompleted, Is.False);
                Assert.That(controller.Registry.Get(connected.AgentId)?.CurrentBuildId, Is.EqualTo("b-cancel"));
            });
            var busyDelete = Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await controller.AgentAdministration.DeleteAsync(connected.AgentId));
            Assert.That(busyDelete!.Message, Does.Contain("stop the build"));

            Assert.That(
                await controller.Builds.CancelBuildAsync("b-cancel", "operator requested stop"),
                Is.True);
            var result = await buildTask.WaitAsync(TimeSpan.FromSeconds(20));

            Assert.That(result.Outcome, Is.EqualTo(BuildOutcome.Cancelled));
            Assert.That(result.StatusText, Is.EqualTo("operator requested stop"));
            Assert.That(controller.Registry.Get(connected.AgentId)!.Activity, Is.EqualTo(AgentActivity.Idle));

            var disabled = Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await controller.Builds.RunBuildAsync(connected.AgentId, second, CancellationToken.None));
            Assert.That(disabled!.Message, Does.Contain("disabled"));
        }
        finally
        {
            agentCts.Cancel();
            await IgnoreCancellationAsync(agentTask);
        }
    }

    [Test]
    public async Task Authorization_and_enabled_state_survive_controller_restart()
    {
        var controllerDir = Path.Combine(rootDir, "controller");
        var agentDir = Path.Combine(rootDir, "agent");
        string agentId;

        await using (var controller = await VivariumControllerHost.StartAsync(new ControllerOptions
                     {
                         DataDir = controllerDir,
                         Host = "127.0.0.1",
                         Port = 0,
                     }))
        {
            var enrollToken = await controller.Tokens.CreateEnrollTokenAsync();
            var agent = new AgentRunner(new AgentOptions
            {
                ControllerUrl = controller.Url,
                CertFingerprintSha256 = controller.Certificate.FingerprintSha256,
                EnrollToken = enrollToken,
                DataDir = agentDir,
                HeartbeatInterval = TimeSpan.FromMilliseconds(250),
                ReconnectDelay = TimeSpan.FromMilliseconds(100),
            });
            agentId = agent.AgentId;

            using var cts = new CancellationTokenSource();
            var run = agent.RunAsync(cts.Token);
            await WaitForAsync(
                () => controller.Registry.Get(agentId) is { Connected: true } value ? value : null,
                TimeSpan.FromSeconds(20));
            await controller.AuthorizeAgentAsync(agentId);
            await agent.WaitAuthorizedAsync(TimeSpan.FromSeconds(20));
            await controller.AgentAdministration.SetEnabledAsync(agentId, false);
            cts.Cancel();
            await IgnoreCancellationAsync(run);
        }

        await using var restarted = await VivariumControllerHost.StartAsync(new ControllerOptions
        {
            DataDir = controllerDir,
            Host = "127.0.0.1",
            Port = 0,
        });
        var record = (await restarted.AgentAdministration.ListAsync()).Single(a => a.AgentId == agentId);
        Assert.Multiple(() =>
        {
            Assert.That(record.Connected, Is.False);
            Assert.That(record.Authorization, Is.EqualTo(AgentAuth.Authorized));
            Assert.That(record.Enabled, Is.False);
        });
    }

    [Test]
    public async Task Enrollment_token_is_consumed_after_the_agent_confirms_its_credential()
    {
        await using var controller = await StartControllerAsync();
        var enrollmentToken = await controller.Tokens.CreateEnrollTokenAsync();
        const string agentId = "enrollment-token-agent";

        var first = await controller.Tokens.AdmitAgentAsync(new Hello
        {
            AgentId = agentId,
            SessionId = "session-1",
            EnrollToken = enrollmentToken,
        });
        Assert.That(first?.Authorization, Is.EqualTo(AgentAuth.Unauthorized));

        var credential = await controller.Tokens.AuthorizeAgentAsync(agentId);
        Assert.That(credential, Is.Not.Null.And.Not.Empty);
        var confirmed = await controller.Tokens.AdmitAgentAsync(new Hello
        {
            AgentId = agentId,
            SessionId = "session-2",
            AuthToken = credential,
        });
        Assert.That(confirmed?.Authorization, Is.EqualTo(AgentAuth.Authorized));

        var replay = await controller.Tokens.AdmitAgentAsync(new Hello
        {
            AgentId = agentId,
            SessionId = "session-3",
            EnrollToken = enrollmentToken,
        });
        Assert.That(replay, Is.Null);

        await controller.AgentAdministration.UnauthorizeAsync(agentId);
        Assert.That(await controller.Tokens.IsValidBearerAsync(credential!), Is.True,
            "authorization controls scheduling; the credential still identifies the agent");

        await controller.AgentAdministration.DeleteAsync(agentId);
        Assert.That(await controller.Tokens.IsValidBearerAsync(credential!), Is.False,
            "deleting the registration revokes its credential");
    }

    [Test]
    [UnsupportedOSPlatform("windows")]
    public async Task Controller_and_agent_secrets_are_owner_only_on_unix()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Ignore("Unix file modes are not available on Windows");
            return;
        }

        var controllerDir = Path.Combine(rootDir, "controller");
        Directory.CreateDirectory(controllerDir);
        File.SetUnixFileMode(controllerDir, PermissiveDirectoryMode);

        await using var controller = await StartControllerAsync();
        Assert.Multiple(() =>
        {
            Assert.That(File.GetUnixFileMode(controllerDir), Is.EqualTo(PrivateDirectoryMode));
            Assert.That(
                File.GetUnixFileMode(Path.Combine(controllerDir, "controller.pfx")),
                Is.EqualTo(PrivateSecretMode));
            Assert.That(
                File.GetUnixFileMode(Path.Combine(controllerDir, "admin.token")),
                Is.EqualTo(PrivateSecretMode));
            Assert.That(
                File.GetUnixFileMode(Path.Combine(controllerDir, "submit.token")),
                Is.EqualTo(PrivateSecretMode));
            Assert.That(controller.Tokens.AdminToken, Does.Match("^[0-9A-F]{48}$"));
            Assert.That(controller.Tokens.SubmitToken, Does.Match("^[0-9A-F]{48}$"));
        });

        var agentDir = Path.Combine(rootDir, "agent");
        Directory.CreateDirectory(agentDir);
        File.SetUnixFileMode(agentDir, PermissiveDirectoryMode);
        var enrollToken = await controller.Tokens.CreateEnrollTokenAsync();
        var agent = NewAgent(controller, enrollToken);

        using var agentCts = new CancellationTokenSource();
        var agentTask = agent.RunAsync(agentCts.Token);
        try
        {
            await WaitForAsync(
                () => controller.Registry.Get(agent.AgentId) is { Connected: true } value ? value : null,
                TimeSpan.FromSeconds(20));
            await controller.AuthorizeAgentAsync(agent.AgentId);
            await agent.WaitAuthorizedAsync(TimeSpan.FromSeconds(20));

            Assert.Multiple(() =>
            {
                Assert.That(File.GetUnixFileMode(agentDir), Is.EqualTo(PrivateDirectoryMode));
                Assert.That(
                    File.GetUnixFileMode(Path.Combine(agentDir, "auth.token")),
                    Is.EqualTo(PrivateSecretMode));
            });
        }
        finally
        {
            agentCts.Cancel();
            await IgnoreCancellationAsync(agentTask);
        }
    }

    [Test]
    public async Task Delete_serializes_with_admitted_reconnect_and_removes_its_live_session()
    {
        await using var controller = await StartControllerAsync();
        const string agentId = "delete-admission-race";
        var enrollmentToken = await controller.Tokens.CreateEnrollTokenAsync();
        var initialHello = new Hello
        {
            AgentId = agentId,
            SessionId = "initial-session",
            EnrollToken = enrollmentToken,
        };
        Assert.That(await controller.Tokens.AdmitAgentAsync(initialHello), Is.Not.Null);
        await controller.AgentStore.ObserveHelloAsync(initialHello);
        var credential = await controller.Tokens.AuthorizeAgentAsync(agentId);
        Assert.That(credential, Is.Not.Null.And.Not.Empty);

        var reconnect = new Hello
        {
            AgentId = agentId,
            SessionId = "racing-session",
            AuthToken = credential,
        };
        using var session = new CancellationTokenSource();
        var admitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueRegistration = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var register = Task.Run(async () =>
        {
            await using (await controller.AgentLifecycle.AcquireAsync(agentId))
            {
                var admission = await controller.Tokens.AdmitAgentAsync(reconnect);
                Assert.That(admission?.Authorization, Is.EqualTo(AgentAuth.Authorized));
                admitted.TrySetResult();
                await continueRegistration.Task;
                await controller.AgentStore.ObserveHelloAsync(reconnect);
                controller.Registry.Register(
                    reconnect, admission!.Authorization, admission.Enabled, session);
            }
        });

        await admitted.Task;
        var delete = controller.AgentAdministration.DeleteAsync(agentId);
        Assert.That(delete.IsCompleted, Is.False,
            "delete must wait until the admitted connection is registered under the same guard");
        continueRegistration.TrySetResult();
        await register;
        await delete;
        var stored = await controller.AgentStore.GetAsync(agentId);
        var credentialIsValid = await controller.Tokens.IsValidBearerAsync(credential!);

        Assert.Multiple(() =>
        {
            Assert.That(controller.Registry.Get(agentId), Is.Null);
            Assert.That(session.IsCancellationRequested, Is.True);
            Assert.That(stored, Is.Null);
            Assert.That(credentialIsValid, Is.False);
        });
        Assert.That(await controller.Tokens.AdmitAgentAsync(reconnect), Is.Null,
            "the credential deleted after admission must not reconnect");
        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await controller.AgentStore.ObserveHelloAsync(reconnect));
    }

    [Test]
    public async Task Provider_waits_for_a_new_idle_session_after_restore()
    {
        var registry = new AgentRegistry();
        using var first = new CancellationTokenSource();
        var original = registry.Register(
            new Hello { AgentId = "pool-1", SessionId = "s-1" },
            AgentAuth.Authorized, true, first);
        var originalGeneration = original.ConnectionGeneration;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var wait = registry.WaitForFreshIdleSessionAsync(
            "pool-1", originalGeneration, timeout.Token);

        using var reconnectingBuild = new CancellationTokenSource();
        var reconnecting = registry.Register(
            new Hello { AgentId = "pool-1", SessionId = "s-2", RunningBuildId = "stale-build" },
            AgentAuth.Authorized, true, reconnectingBuild);
        registry.Reconcile(reconnecting, "stale-build");
        Assert.That(wait.IsCompleted, Is.False, "a session that still reports work is not pristine");

        using var restored = new CancellationTokenSource();
        var restoredConnection = registry.Register(
            new Hello { AgentId = "pool-1", SessionId = "s-3" },
            AgentAuth.Authorized, true, restored);
        Assert.That(wait.IsCompleted, Is.False,
            "a newly connected session is not ready before durable-build reconciliation");
        registry.Reconcile(restoredConnection, currentBuildId: null);

        var fresh = await wait;
        Assert.Multiple(() =>
        {
            Assert.That(fresh.SessionId, Is.EqualTo("s-3"));
            Assert.That(fresh.ConnectionGeneration, Is.GreaterThan(originalGeneration));
        });
    }

    [Test]
    public void Missed_heartbeat_disconnects_and_aborts_the_session()
    {
        var registry = new AgentRegistry();
        using var session = new CancellationTokenSource();
        registry.Register(
            new Hello { AgentId = "a-1", SessionId = "s-1" },
            AgentAuth.Authorized, true, session);
        var agent = registry.Get("a-1")!;

        var expired = registry.ExpireStaleConnections(
            agent.LastHeartbeat.AddMinutes(1), TimeSpan.FromSeconds(20));

        Assert.Multiple(() =>
        {
            Assert.That(expired.Select(loss => loss.AgentId), Is.EqualTo(new[] { "a-1" }));
            Assert.That(registry.Get("a-1")!.Connected, Is.False);
            Assert.That(session.IsCancellationRequested, Is.True);
        });
    }

    private Task<VivariumControllerHost> StartControllerAsync() =>
        VivariumControllerHost.StartAsync(new ControllerOptions
        {
            DataDir = Path.Combine(rootDir, "controller"),
            Host = "127.0.0.1",
            Port = 0,
        });

    private AgentRunner NewAgent(VivariumControllerHost controller, string enrollToken) => new(new AgentOptions
    {
        ControllerUrl = controller.Url,
        CertFingerprintSha256 = controller.Certificate.FingerprintSha256,
        EnrollToken = enrollToken,
        DataDir = Path.Combine(rootDir, "agent"),
        HeartbeatInterval = TimeSpan.FromMilliseconds(250),
        ReconnectDelay = TimeSpan.FromMilliseconds(100),
    });

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

    private static async Task IgnoreCancellationAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
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

            await Task.Delay(50);
        }

        throw new AssertionException("condition not reached within timeout");
    }

    private const UnixFileMode PrivateDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode PrivateSecretMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode PermissiveDirectoryMode =
        PrivateDirectoryMode |
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
}
