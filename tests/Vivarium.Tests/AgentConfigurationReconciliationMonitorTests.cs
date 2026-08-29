using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Vivarium.Contracts.V1;
using Vivarium.Controller.Agents;
using Vivarium.Controller.Configuration.Agents;
using Vivarium.Controller.Configuration.Git;
using Vivarium.Controller.Configuration.Reconciliation;
using Vivarium.Controller.Persistence;
using Vivarium.Controller.Security;

namespace Vivarium.Tests;

[TestFixture]
[NonParallelizable]
public sealed class AgentConfigurationReconciliationMonitorTests
{
    private const string AgentId = "agent-one";
    private string rootDir = null!;

    [SetUp]
    public void SetUp()
    {
        rootDir = Path.Combine(
            Path.GetTempPath(),
            "vivarium-agent-configuration-monitor-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootDir);
        Directory.CreateDirectory(Path.Combine(rootDir, "data"));
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
            // Preserve the original failure if Git or SQLite releases a handle late.
        }
    }

    [Test]
    public async Task External_valid_enablement_commit_converges_durable_and_live_state()
    {
        await using var database = new VivariumDatabase(Path.Combine(rootDir, "data"));
        await InsertAgentAsync(database, enabled: true);
        var repositoryPath = Path.Combine(rootDir, "configuration");
        var repository = await ManagedGitRepository.OpenOrCreateAsync(repositoryPath, "controller");
        var reconciler = new ConfigurationReconciler(database, TimeProvider.System);
        _ = await ReconcileAsync(reconciler, repository);
        var registry = ConnectRegistry(new AgentStore(database), enabled: true);
        var monitor = CreateMonitor(
            repository,
            reconciler,
            new AgentStore(database),
            registry,
            new CapturingLogger<AgentConfigurationReconciliationMonitor>());

        await CommitExternalAgentAsync(repositoryPath, enabled: false, "Disable Agent externally");
        var externalHead = await repository.GetAuthoritativeHeadAsync();
        var result = await monitor.ReconcileOnceAsync();
        var stored = await new AgentStore(database).GetAsync(AgentId);

        Assert.Multiple(() =>
        {
            Assert.That(result!.Outcome, Is.EqualTo(ConfigurationReconciliationOutcome.Applied));
            Assert.That(ControlRevision(result.State.Active!), Is.EqualTo(externalHead));
            Assert.That(stored!.Enabled, Is.False);
            Assert.That(registry.Get(AgentId)!.Enabled, Is.False);
        });
    }

    [Test]
    public async Task Invalid_external_head_retains_last_known_good_durable_and_live_state()
    {
        await using var database = new VivariumDatabase(Path.Combine(rootDir, "data"));
        await InsertAgentAsync(database, enabled: true);
        var repositoryPath = Path.Combine(rootDir, "configuration");
        var repository = await ManagedGitRepository.OpenOrCreateAsync(repositoryPath, "controller");
        var reconciler = new ConfigurationReconciler(database, TimeProvider.System);
        var store = new AgentStore(database);
        var registry = ConnectRegistry(store, enabled: true);
        var monitor = CreateMonitor(
            repository,
            reconciler,
            store,
            registry,
            new CapturingLogger<AgentConfigurationReconciliationMonitor>());

        await CommitExternalAgentAsync(repositoryPath, enabled: false, "Establish disabled LKG");
        var applied = await monitor.ReconcileOnceAsync();
        var lastKnownGood = ControlRevision(applied!.State.Active!);
        registry.SetEnabled(AgentId, enabled: true);
        await File.WriteAllTextAsync(
            Path.Combine(repositoryPath, ".env"),
            "AUTH_TOKEN=must-not-appear-in-logs\n",
            new UTF8Encoding(false));
        await RunGitAsync(repositoryPath, "add", ".env");
        await CommitExternalAsync(repositoryPath, "Invalid external configuration");
        var invalidHead = await repository.GetAuthoritativeHeadAsync();

        var rejected = await monitor.ReconcileOnceAsync();
        var stored = await store.GetAsync(AgentId);

        Assert.Multiple(() =>
        {
            Assert.That(rejected!.Outcome, Is.EqualTo(ConfigurationReconciliationOutcome.Invalid));
            Assert.That(invalidHead, Is.Not.EqualTo(lastKnownGood));
            Assert.That(ControlRevision(rejected.State.Active!), Is.EqualTo(lastKnownGood));
            Assert.That(ControlRevision(rejected.State.LastKnownGood!), Is.EqualTo(lastKnownGood));
            Assert.That(stored!.Enabled, Is.False);
            Assert.That(registry.Get(AgentId)!.Enabled, Is.False,
                "the rejected head must refresh live state from the durable LKG projection");
        });
    }

    [Test]
    public async Task External_agent_document_removal_retains_last_known_good_state()
    {
        await using var database = new VivariumDatabase(Path.Combine(rootDir, "data"));
        await InsertAgentAsync(database, enabled: true);
        var repositoryPath = Path.Combine(rootDir, "configuration");
        var repository = await ManagedGitRepository.OpenOrCreateAsync(repositoryPath, "controller");
        var reconciler = new ConfigurationReconciler(database, TimeProvider.System);
        var store = new AgentStore(database);
        var registry = ConnectRegistry(store, enabled: true);
        var monitor = CreateMonitor(
            repository,
            reconciler,
            store,
            registry,
            new CapturingLogger<AgentConfigurationReconciliationMonitor>());

        await CommitExternalAgentAsync(repositoryPath, enabled: false, "Establish disabled LKG");
        var applied = await monitor.ReconcileOnceAsync();
        var lastKnownGood = ControlRevision(applied!.State.Active!);
        registry.SetEnabled(AgentId, enabled: true);
        await RunGitAsync(repositoryPath, "rm", $".vivarium/agents/{AgentId}.yaml");
        await CommitExternalAsync(repositoryPath, "Remove materialized Agent document");

        var blocked = await monitor.ReconcileOnceAsync();
        var stored = await store.GetAsync(AgentId);

        Assert.Multiple(() =>
        {
            Assert.That(blocked!.Outcome, Is.EqualTo(ConfigurationReconciliationOutcome.Blocked));
            Assert.That(ControlRevision(blocked.State.Active!), Is.EqualTo(lastKnownGood));
            Assert.That(ControlRevision(blocked.State.LastKnownGood!), Is.EqualTo(lastKnownGood));
            Assert.That(stored!.Enabled, Is.False);
            Assert.That(registry.Get(AgentId)!.Enabled, Is.False);
        });
    }

    [Test]
    public async Task Repository_failure_is_safe_and_a_later_cycle_still_converges()
    {
        await using var database = new VivariumDatabase(Path.Combine(rootDir, "data"));
        await InsertAgentAsync(database, enabled: true);
        var repositoryPath = Path.Combine(rootDir, "configuration");
        var repository = await ManagedGitRepository.OpenOrCreateAsync(repositoryPath, "controller");
        var reconciler = new ConfigurationReconciler(database, TimeProvider.System);
        _ = await ReconcileAsync(reconciler, repository);
        await CommitExternalAgentAsync(repositoryPath, enabled: false, "Establish disabled LKG");
        var applied = await ReconcileAsync(reconciler, repository);
        var lastKnownGood = ControlRevision(applied.State.Active!);
        var store = new AgentStore(database);
        var registry = ConnectRegistry(store, enabled: true);
        await CommitExternalAgentAsync(repositoryPath, enabled: true, "Enable Agent externally");
        var expectedHead = await repository.GetAuthoritativeHeadAsync();
        var failingRepository = new FailOnceRepository(repository);
        var logger = new CapturingLogger<AgentConfigurationReconciliationMonitor>();
        var monitor = CreateMonitor(
            failingRepository,
            reconciler,
            store,
            registry,
            logger);

        var failed = await monitor.ReconcileOnceAsync();
        var retained = await store.GetAsync(AgentId);
        var stateAfterFailure = await reconciler.GetStateAsync(
            AgentDesiredConfigurationService.MaterializationScope);
        var recovered = await monitor.ReconcileOnceAsync();
        var converged = await store.GetAsync(AgentId);

        Assert.Multiple(() =>
        {
            Assert.That(failed, Is.Null);
            Assert.That(retained!.Enabled, Is.False);
            Assert.That(ControlRevision(stateAfterFailure!.Active!), Is.EqualTo(lastKnownGood));
            Assert.That(recovered!.Outcome, Is.EqualTo(ConfigurationReconciliationOutcome.Applied));
            Assert.That(ControlRevision(recovered.State.Active!), Is.EqualTo(expectedHead));
            Assert.That(converged!.Enabled, Is.True);
            Assert.That(registry.Get(AgentId)!.Enabled, Is.True);
            Assert.That(failingRepository.HeadReadCount, Is.EqualTo(3));
            Assert.That(logger.Messages, Has.Some.Contains("configuration_git_unavailable"));
            Assert.That(logger.Messages, Has.None.Contains("private/repository"));
            Assert.That(logger.Messages, Has.None.Contains("secret-token"));
        });
    }

    private static AgentConfigurationReconciliationMonitor CreateMonitor(
        IConfigurationRepository repository,
        ConfigurationReconciler reconciler,
        AgentStore store,
        AgentRegistry registry,
        ILogger<AgentConfigurationReconciliationMonitor> logger) =>
        new(
            repository,
            reconciler,
            store,
            registry,
            new AgentLifecycleCoordinator(),
            TimeProvider.System,
            logger);

    private static AgentRegistry ConnectRegistry(AgentStore store, bool enabled)
    {
        var registry = new AgentRegistry(store, TimeProvider.System);
        var connection = registry.Register(
            new Hello { AgentId = AgentId, SessionId = "session-one" },
            AgentAuth.Authorized,
            enabled,
            new CancellationTokenSource());
        Assert.That(registry.Reconcile(connection, currentBuildId: null), Is.True);
        return registry;
    }

    private static Task<ConfigurationReconciliationResult> ReconcileAsync(
        ConfigurationReconciler reconciler,
        IConfigurationRepository repository) =>
        reconciler.ReconcileAuthoritativeHeadAsync(
            ManagementRequestContext.System("agent-configuration-monitor-test"),
            AgentDesiredConfigurationService.MaterializationScope,
            repository);

    private static Task InsertAgentAsync(
        VivariumDatabase database,
        bool enabled) => database.WriteAsync(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO agents(
                agent_id, name, enabled, first_seen_unix_ms, last_seen_unix_ms)
            VALUES ($agentId, $agentId, $enabled, 1, 1);
            """;
        command.Parameters.AddWithValue("$agentId", AgentId);
        command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        command.ExecuteNonQuery();
        return true;
    });

    private static ConfigurationRevision ControlRevision(StoredConfigurationRevisionSet set)
    {
        var member = set.Members.Single(member => member.RepositoryRole == "CONTROL");
        return new ConfigurationRevision(member.RepositoryId, member.Commit);
    }

    private static async Task CommitExternalAgentAsync(
        string repositoryPath,
        bool enabled,
        string message)
    {
        var relativePath = $".vivarium/agents/{AgentId}.yaml";
        var path = Path.Combine(repositoryPath, ".vivarium", "agents", $"{AgentId}.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, AgentDocument(enabled), new UTF8Encoding(false));
        await RunGitAsync(repositoryPath, "add", relativePath);
        await CommitExternalAsync(repositoryPath, message);
    }

    private static string AgentDocument(bool enabled) => $"""
        apiVersion: vivarium.io/v1alpha1
        kind: Agent
        id: {AgentId}
        spec:
          enabled: {enabled.ToString().ToLowerInvariant()}

        """;

    private static Task CommitExternalAsync(string repositoryPath, string message) =>
        RunGitAsync(
            repositoryPath,
            "-c", "user.name=External Administrator",
            "-c", "user.email=external@example.invalid",
            "-c", "commit.gpgsign=false",
            "commit", "-m", message);

    private static async Task<string> RunGitAsync(
        string repositoryPath,
        params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = repositoryPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("git did not start");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await outputTask;
        var error = await errorTask;
        Assert.That(process.ExitCode, Is.Zero, error);
        return output.TrimEnd('\r', '\n');
    }

    private sealed class FailOnceRepository(IConfigurationRepository inner)
        : IConfigurationRepository
    {
        public string RepositoryId => inner.RepositoryId;

        public int HeadReadCount { get; private set; }

        public Task<ConfigurationRevision> GetAuthoritativeHeadAsync(
            CancellationToken cancellationToken = default)
        {
            HeadReadCount++;
            if (HeadReadCount == 1)
            {
                throw new ConfigurationRepositoryException(
                    "CONFIGURATION_GIT_UNAVAILABLE",
                    "private/repository secret-token must never be logged");
            }

            return inner.GetAuthoritativeHeadAsync(cancellationToken);
        }

        public Task<ConfigurationRevisionValidation> ValidateRevisionAsync(
            ConfigurationRevision revision,
            CancellationToken cancellationToken = default) =>
            inner.ValidateRevisionAsync(revision, cancellationToken);

        public Task<ConfigurationCommitResult> UpsertDocumentAsync(
            ConfigurationDocumentMutation mutation,
            CancellationToken cancellationToken = default) =>
            inner.UpsertDocumentAsync(mutation, cancellationToken);
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
