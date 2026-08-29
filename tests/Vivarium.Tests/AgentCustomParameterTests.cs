using Vivarium.Contracts.V1;
using Vivarium.Controller.Agents;
using Vivarium.Controller.Auditing;
using Vivarium.Controller.Builds;
using Vivarium.Controller.Persistence;
using Vivarium.Controller.Scheduling;
using Vivarium.Controller.Security;

namespace Vivarium.Tests;

[TestFixture]
public class AgentCustomParameterTests
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
    public async Task Custom_parameters_are_merged_for_matching_and_survive_reconnect_and_restart()
    {
        var dataDir = Path.Combine(rootDir, "controller");
        const string agentId = "custom-agent";
        await using (var controller = await CoreHarness.StartAsync(dataDir))
        {
            await controller.RegisterAsync(HelloFor(
                agentId,
                "session-1",
                ("hostname", "custom-agent"),
                ("os.family", "windows")));
            var beforeEdit = (await controller.Administration.ListAsync()).Single();

            await controller.Administration.SetCustomParameterAsync(
                ManagementRequestContext.System("test"), agentId, "pool", "lab");
            await controller.Administration.SetCustomParameterAsync(
                ManagementRequestContext.System("test"), agentId, "software.browser", "chrome");
            await controller.Administration.SetCustomParameterAsync(
                ManagementRequestContext.System("test"), agentId, "pool", "secure-lab");

            Assert.That(controller.Registry.TryBeginBuild(
                agentId,
                "stale-match",
                beforeEdit.ParameterGeneration,
                out _,
                out var staleReason), Is.False);
            Assert.That(staleReason, Does.Contain("parameters changed"));

            var first = (await controller.Administration.ListAsync()).Single();
            Assert.Multiple(() =>
            {
                Assert.That(first.ReportedParameters.Keys,
                    Is.EqualTo(new[] { "hostname", "os.family" }));
                Assert.That(first.CustomParameters.Keys,
                    Is.EqualTo(new[] { "pool", "software.browser" }));
                Assert.That(first.Parameters.Keys,
                    Is.EqualTo(new[] { "hostname", "os.family", "pool", "software.browser" }));
                Assert.That(first.Parameters["pool"], Is.EqualTo("secure-lab"));
                Assert.That(AgentCompatibilityMatcher.Match(
                    "os.family == windows && pool == secure-lab",
                    first.Name,
                    first.Parameters).Compatible, Is.True);
            });

            await controller.Store.ObserveHelloAsync(HelloFor(
                agentId,
                "session-2",
                ("hostname", "custom-agent"),
                ("os.family", "linux"),
                ("kernel", "6.8")));
            await controller.Administration.DeleteCustomParameterAsync(
                ManagementRequestContext.System("test"), agentId, "software.browser");

            var reconnected = (await controller.Administration.ListAsync()).Single();
            Assert.Multiple(() =>
            {
                Assert.That(reconnected.ReportedParameters["os.family"], Is.EqualTo("linux"));
                Assert.That(reconnected.ReportedParameters["kernel"], Is.EqualTo("6.8"));
                Assert.That(reconnected.CustomParameters,
                    Is.EqualTo(new Dictionary<string, string> { ["pool"] = "secure-lab" }));
                Assert.That(reconnected.Authorization, Is.EqualTo(AgentAuth.Unauthorized));
                Assert.That(reconnected.Enabled, Is.True);
            });
        }

        await using var restarted = await CoreHarness.StartAsync(dataDir);
        var restored = (await restarted.Administration.ListAsync()).Single();
        Assert.Multiple(() =>
        {
            Assert.That(restored.Connected, Is.False);
            Assert.That(restored.ReportedParameters["os.family"], Is.EqualTo("linux"));
            Assert.That(restored.CustomParameters["pool"], Is.EqualTo("secure-lab"));
            Assert.That(restored.Parameters.Keys,
                Is.EqualTo(new[] { "hostname", "kernel", "os.family", "pool" }));
        });
    }

    [Test]
    public async Task Reported_and_custom_keys_cannot_collide()
    {
        await using var controller = await CoreHarness.StartAsync(Path.Combine(rootDir, "controller"));
        const string agentId = "conflict-agent";
        await controller.RegisterAsync(HelloFor(
            agentId,
            "session-1",
            ("hostname", "conflict-agent"),
            ("os.family", "windows")));

        var reportedConflict = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await controller.Administration.SetCustomParameterAsync(
                ManagementRequestContext.System("test"), agentId, "os.family", "linux"));
        Assert.That(reportedConflict!.Message, Does.Contain("conflicts with a reported parameter"));

        await controller.Administration.SetCustomParameterAsync(
            ManagementRequestContext.System("test"), agentId, "software.channel", "beta");
        var helloConflict = Assert.ThrowsAsync<InvalidDataException>(async () =>
            await controller.Store.ObserveHelloAsync(HelloFor(
                agentId,
                "session-2",
                ("hostname", "conflict-agent"),
                ("os.family", "linux"),
                ("software.channel", "stable"))));
        Assert.That(helloConflict!.Message, Does.Contain("software.channel"));

        var unchanged = await controller.Store.GetAsync(agentId);
        Assert.Multiple(() =>
        {
            Assert.That(unchanged!.ReportedParameters["os.family"], Is.EqualTo("windows"));
            Assert.That(unchanged.CustomParameters["software.channel"], Is.EqualTo("beta"));
            Assert.That(unchanged.Parameters["software.channel"], Is.EqualTo("beta"));
        });

        Assert.ThrowsAsync<ArgumentException>(async () =>
            await controller.Administration.SetCustomParameterAsync(
                ManagementRequestContext.System("test"), agentId, "name", "spoof"));
        Assert.ThrowsAsync<ArgumentException>(async () =>
            await controller.Administration.SetCustomParameterAsync(
                ManagementRequestContext.System("test"), agentId, "bad key", "value"));
    }

    [Test]
    public async Task Editing_custom_parameters_is_rejected_while_the_agent_owns_a_build()
    {
        await using var controller = await CoreHarness.StartAsync(Path.Combine(rootDir, "controller"));
        const string agentId = "busy-agent";
        await controller.RegisterAsync(HelloFor(
            agentId,
            "session-1",
            ("os.family", "windows")));
        var assignment = new BuildAssignment { BuildId = "busy-build" };
        await controller.Builds.CreateAsync(
            agentId,
            "session-1",
            assignment,
            DateTimeOffset.UtcNow);

        var error = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await controller.Administration.SetCustomParameterAsync(
                ManagementRequestContext.System("test"), agentId, "pool", "lab"));
        var snapshot = (await controller.Administration.ListAsync()).Single();
        Assert.Multiple(() =>
        {
            Assert.That(error!.Message, Does.Contain("stop the build"));
            Assert.That(snapshot.CustomParameters, Is.Empty);
            Assert.That(snapshot.Enabled, Is.True, "the safety fence must preserve operator enablement");
            Assert.That(snapshot.Authorization, Is.EqualTo(AgentAuth.Unauthorized));
        });
    }

    private static Hello HelloFor(
        string agentId,
        string sessionId,
        params (string Key, string Value)[] parameters)
    {
        var hello = new Hello { AgentId = agentId, SessionId = sessionId };
        foreach (var (key, value) in parameters)
        {
            hello.Parameters.Add(key, value);
        }

        return hello;
    }

    private sealed class CoreHarness : IAsyncDisposable
    {
        private readonly List<CancellationTokenSource> sessions = [];

        private CoreHarness(
            VivariumDatabase database,
            TokenStore tokens,
            AgentStore store,
            AgentRegistry registry,
            BuildStore builds,
            AgentAdministration administration)
        {
            Database = database;
            Tokens = tokens;
            Store = store;
            Registry = registry;
            Builds = builds;
            Administration = administration;
        }

        public VivariumDatabase Database { get; }
        public TokenStore Tokens { get; }
        public AgentStore Store { get; }
        public AgentRegistry Registry { get; }
        public BuildStore Builds { get; }
        public AgentAdministration Administration { get; }

        public static Task<CoreHarness> StartAsync(string dataDir)
        {
            Directory.CreateDirectory(dataDir);
            var database = new VivariumDatabase(dataDir);
            var tokens = new TokenStore(dataDir, database);
            var store = new AgentStore(database);
            var registry = new AgentRegistry(store);
            var builds = new BuildStore(database);
            var authorization = new ManagementCommandAuthorizer(
                new ManagementAuthorizer(), new AuditEventStore(database), TimeProvider.System);
            var administration = new AgentAdministration(
                registry,
                store,
                builds,
                tokens,
                new AgentLifecycleCoordinator(),
                authorization: authorization);
            return Task.FromResult(new CoreHarness(
                database, tokens, store, registry, builds, administration));
        }

        public async Task RegisterAsync(Hello hello)
        {
            var enrollToken = await Tokens.CreateEnrollTokenAsync();
            hello.EnrollToken = enrollToken;
            var admission = await Tokens.AdmitAgentAsync(hello)
                ?? throw new AssertionException("agent admission failed");
            await Store.ObserveHelloAsync(hello);
            var session = new CancellationTokenSource();
            sessions.Add(session);
            var connection = Registry.Register(
                hello, admission.Authorization, admission.Enabled, session);
            Registry.Reconcile(connection, currentBuildId: null);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var session in sessions)
            {
                session.Cancel();
                session.Dispose();
            }

            await Database.DisposeAsync();
        }
    }
}
