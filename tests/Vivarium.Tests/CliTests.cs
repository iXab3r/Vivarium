using System.Text;
using Google.Protobuf;
using Vivarium.Cli;
using Vivarium.Cli.Configuration;
using Vivarium.Contracts.V1;
using Vivarium.Controller;

namespace Vivarium.Tests;

[TestFixture]
[NonParallelizable]
public class CliTests
{
    private string root = null!;

    [SetUp]
    public void SetUp()
    {
        root = Path.Combine(Path.GetTempPath(), "vivarium-cli-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch
        {
            // Best effort on Windows, where an async file handle may release a beat later.
        }
    }

    [Test]
    public void Run_arguments_preserve_repeated_only_and_overrides()
    {
        var parsed = (RunCommand)CliArguments.Parse(
        [
            "run", "tier-2", "--file", "farm.yaml", "--only", "win", "--only", "linux",
            "--no-wait", "--url", "https://ctrl:8443", "--token", "secret",
            "--fingerprint", Fingerprint('a'),
        ]);

        Assert.Multiple(() =>
        {
            Assert.That(parsed.Configuration, Is.EqualTo("tier-2"));
            Assert.That(parsed.FilePath, Is.EqualTo("farm.yaml"));
            Assert.That(parsed.OnlyCells, Is.EqualTo(new[] { "win", "linux" }));
            Assert.That(parsed.NoWait, Is.True);
            Assert.That(parsed.Url, Is.EqualTo("https://ctrl:8443"));
            Assert.That(parsed.Token, Is.EqualTo("secret"));
        });
    }

    [Test]
    public void Cancel_arguments_have_an_explicit_reason_and_the_same_connection_overrides()
    {
        var defaults = (CancelCommand)CliArguments.Parse(["cancel", "matrix-1"]);
        var overridden = (CancelCommand)CliArguments.Parse(
        [
            "cancel", "matrix-2", "--reason", "operator stop",
            "--url", "https://ctrl:8443", "--token", "secret",
            "--fingerprint", Fingerprint('a'),
        ]);

        Assert.Multiple(() =>
        {
            Assert.That(defaults.Reason, Is.EqualTo("Cancelled by viv CLI"));
            Assert.That(overridden.BuildId, Is.EqualTo("matrix-2"));
            Assert.That(overridden.Reason, Is.EqualTo("operator stop"));
            Assert.That(overridden.Url, Is.EqualTo("https://ctrl:8443"));
            Assert.That(overridden.Token, Is.EqualTo("secret"));
        });
    }

    [Test]
    public void Agent_deployment_arguments_are_explicit_and_bounded()
    {
        var upgrade = (AgentUpgradeCommand)CliArguments.Parse(
        [
            "agent", "upgrade", "agent-1",
            "--reason", "canary", "--timeout-seconds", "120", "--no-wait",
        ]);
        var cancellation = (AgentUpgradeCancellationCommand)CliArguments.Parse(
            ["agent", "upgrade-cancel", "operation-1", "--reason", "operator stop", "--no-wait"]);
        var rollback = (AgentUpgradeCancellationCommand)CliArguments.Parse(
            ["agent", "upgrade-rollback", "operation-2"]);

        Assert.Multiple(() =>
        {
            Assert.That(upgrade.AgentId, Is.EqualTo("agent-1"));
            Assert.That(upgrade.Reason, Is.EqualTo("canary"));
            Assert.That(upgrade.TimeoutSeconds, Is.EqualTo(120));
            Assert.That(upgrade.NoWait, Is.True);
            Assert.That(cancellation.OperationId, Is.EqualTo("operation-1"));
            Assert.That(cancellation.Reason, Is.EqualTo("operator stop"));
            Assert.That(cancellation.NoWait, Is.True);
            Assert.That(rollback.Reason, Is.EqualTo("Rollback requested by viv CLI"));
            Assert.Throws<CliUsageException>(() => CliArguments.Parse(
                ["agent", "upgrade", "agent-1", "--timeout-seconds", "10"]));
            Assert.Throws<CliUsageException>(() => CliArguments.Parse(
                ["agent", "package", "publish", "agent.zip"]));
        });
    }

    [Test]
    public void Endpoint_settings_use_flags_then_environment_then_saved_config()
    {
        var environment = new Dictionary<string, string?>
        {
            ["VIVARIUM_URL"] = "https://environment:9443/",
            ["VIVARIUM_TOKEN"] = "environment-token",
            ["VIVARIUM_CERT_FINGERPRINT"] = Fingerprint('b'),
        };
        var saved = new ClientConfiguration(
            "https://saved:7443", Fingerprint('c'), "saved-token");

        var resolved = EndpointSettingsResolver.Resolve(
            "https://flag:8443/", null, Fingerprint('a'),
            name => environment.GetValueOrDefault(name), saved);

        Assert.Multiple(() =>
        {
            Assert.That(resolved.Url, Is.EqualTo("https://flag:8443"));
            Assert.That(resolved.Token, Is.EqualTo("environment-token"));
            Assert.That(resolved.Fingerprint, Is.EqualTo("SHA256:" + new string('A', 64)));
        });
    }

    [TestCase("sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [TestCase("AA:AA:AA:AA:AA:AA:AA:AA:AA:AA:AA:AA:AA:AA:AA:AA:AA:AA:AA:AA:AA:AA:AA:AA:AA:AA:AA:AA:AA:AA:AA:AA")]
    public void Fingerprint_normalization_accepts_common_sha256_forms(string value)
    {
        Assert.That(PinnedTls.NormalizeFingerprint(value),
            Is.EqualTo("SHA256:" + new string('A', 64)));
    }

    [TestCase("")]
    [TestCase("SHA256:abc")]
    [TestCase("SHA1:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [TestCase("SHA256:gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    public void Fingerprint_validation_rejects_malformed_values(string value)
    {
        Assert.Catch(() => PinnedTls.NormalizeFingerprint(value));
    }

    [Test]
    public void Assignment_mapping_preserves_files_process_files_out_and_cell_identity()
    {
        var cell = new ResolvedVivariumCell(
            "windows",
            "os.family == windows",
            "win-x64",
            new ResolvedPayload(Path.Combine(root, "payload"), "payload"),
            [new ResolvedVivariumStep(
                "tests.exe", ["--report", "results/out.trx"],
                new Dictionary<string, string> { ["MODE"] = "full" },
                ".", TimeSpan.FromMinutes(2), VivariumStepPolicy.Always)],
            ["results/**"],
            TimeSpan.FromMinutes(15),
            VivariumOnFail.Keep);
        var run = new ResolvedVivariumRun("Vivarium", "tier-2", [cell]);
        var archive = new PayloadArchiveInfo(Path.Combine(root, "payload.zip"), new string('a', 64), 42);

        var request = BuildRequestMapper.Create(
            run,
            "exact yaml bytes"u8,
            new Dictionary<string, PayloadArchiveInfo> { [cell.Payload.SourceDirectory] = archive },
            "request-id");

        var mapped = request.Cells.Single();
        Assert.Multiple(() =>
        {
            Assert.That(request.DefinitionSnapshot.ToByteArray(), Is.EqualTo("exact yaml bytes"u8.ToArray()));
            Assert.That(mapped.Name, Is.EqualTo("windows"));
            Assert.That(mapped.AgentExpression, Is.EqualTo("os.family == windows"));
            Assert.That(mapped.Rid, Is.EqualTo("win-x64"));
            Assert.That(mapped.QueueTimeoutSec, Is.EqualTo(900));
            Assert.That(mapped.Assignment.Payload.Single().Archive, Is.True);
            Assert.That(mapped.Assignment.Payload.Single().Sha256, Is.EqualTo(new string('a', 64)));
            Assert.That(mapped.Assignment.Steps.Single().TimeoutSec, Is.EqualTo(120));
            Assert.That(mapped.Assignment.Steps.Single().Policy, Is.EqualTo(StepPolicy.Always));
            Assert.That(mapped.Assignment.Collect, Is.EqualTo(new[] { "results/**" }));
            Assert.That(mapped.Assignment.OnFail, Is.EqualTo(OnFail.KeepMachine));
            Assert.That(mapped.Assignment.Parameters["cell"], Is.EqualTo("windows"));
            Assert.That(mapped.Assignment.Parameters["rid"], Is.EqualTo("win-x64"));
        });
    }

    [Test]
    public void Aggregate_exit_is_zero_only_when_every_terminal_cell_succeeded()
    {
        var green = Snapshot(BuildOutcome.Succeeded, BuildOutcome.Succeeded);
        var red = Snapshot(BuildOutcome.Failed, BuildOutcome.Succeeded);
        var cancelled = Snapshot(BuildOutcome.Cancelled);

        Assert.Multiple(() =>
        {
            Assert.That(VivariumCliApplication.AggregateExitCode(green), Is.Zero);
            Assert.That(VivariumCliApplication.AggregateExitCode(red), Is.EqualTo(1));
            Assert.That(VivariumCliApplication.AggregateExitCode(cancelled), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Login_reconnects_with_observed_exact_pin_before_saving_credentials()
    {
        var observed = Fingerprint('a').ToUpperInvariant();
        var endpoint = new RecordingEndpoint();
        var endpointFactory = new StaticEndpointFactory(endpoint);
        var configurationStore = new RecordingConfigurationStore();
        var console = new RecordingConsole();
        var application = new VivariumCliApplication(
            console,
            configurationStore,
            new StaticCertificateProbe(observed),
            endpointFactory,
            new TemporaryPayloadArchiveFactory(),
            _ => null);

        var exitCode = await application.ExecuteAsync(
            ["login", "https://controller:8443/", "--token", "secret", "--fingerprint", Fingerprint('a')],
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(exitCode, Is.Zero);
            Assert.That(endpoint.ValidateCalls, Is.EqualTo(1));
            Assert.That(endpointFactory.LastSettings,
                Is.EqualTo(new EndpointSettings("https://controller:8443", observed, "secret")));
            Assert.That(configurationStore.Saved,
                Is.EqualTo(new ClientConfiguration("https://controller:8443", observed, "secret")));
            Assert.That(
                console.Output.Any(line => line.Contains("secret", StringComparison.Ordinal)),
                Is.False);
        });
    }

    [Test]
    public async Task No_wait_stages_payloads_then_returns_after_durable_submit()
    {
        var payload = Path.Combine(root, "payload");
        Directory.CreateDirectory(payload);
        await File.WriteAllTextAsync(Path.Combine(payload, "test.txt"), "payload");
        var yamlPath = Path.Combine(root, "vivarium.yaml");
        var yaml = """
            project: Vivarium
            configurations:
              tier-2:
                matrix:
                  windows:
                    agent: os.family == windows
                    rid: win-x64
                payload: payload
                steps:
                  - program: tests.exe
                collect:
                  - results/**
                clean: none
            """;
        await File.WriteAllTextAsync(yamlPath, yaml, new UTF8Encoding(false));

        var endpoint = new RecordingEndpoint();
        var console = new RecordingConsole();
        var application = new VivariumCliApplication(
            console,
            new StaticConfigurationStore(new ClientConfiguration(
                "https://controller:8443", Fingerprint('a'), "token")),
            new UnusedCertificateProbe(),
            new StaticEndpointFactory(endpoint),
            new TemporaryPayloadArchiveFactory(),
            _ => null);

        var exitCode = await application.ExecuteAsync(
            ["run", "tier-2", "--file", yamlPath, "--no-wait"], CancellationToken.None);
        var exactDefinition = await File.ReadAllBytesAsync(yamlPath);

        Assert.Multiple(() =>
        {
            Assert.That(exitCode, Is.Zero);
            Assert.That(endpoint.StageCalls, Is.EqualTo(1));
            Assert.That(endpoint.StagedArchives, Has.Count.EqualTo(1));
            Assert.That(endpoint.SubmitCalls, Is.EqualTo(1));
            Assert.That(endpoint.WatchCalls, Is.Zero);
            Assert.That(endpoint.Submitted!.DefinitionSnapshot.ToByteArray(),
                Is.EqualTo(exactDefinition));
            Assert.That(console.Output, Has.Some.Contains("matrix-build-1"));
            Assert.That(console.Output,
                Does.Contain("Results: https://controller:8443/builds/matrix-build-1"));
        });
    }

    [Test]
    public async Task Cancel_uses_pinned_settings_and_returns_the_durable_aggregate()
    {
        var endpoint = new RecordingEndpoint();
        var console = new RecordingConsole();
        var application = new VivariumCliApplication(
            console,
            new StaticConfigurationStore(new ClientConfiguration(
                "https://controller:8443", Fingerprint('a'), "token")),
            new UnusedCertificateProbe(),
            new StaticEndpointFactory(endpoint),
            new TemporaryPayloadArchiveFactory(),
            _ => null);

        var exitCode = await application.ExecuteAsync(
            ["cancel", "matrix-build-1", "--reason", "CI timeout"],
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(exitCode, Is.Zero);
            Assert.That(endpoint.CancelCalls, Is.EqualTo(1));
            Assert.That(endpoint.CancelledBuildId, Is.EqualTo("matrix-build-1"));
            Assert.That(endpoint.CancellationReason, Is.EqualTo("CI timeout"));
            Assert.That(console.Output,
                Does.Contain("Cancellation requested for matrix build matrix-build-1"));
            Assert.That(console.Output,
                Does.Contain("Results: https://controller:8443/builds/matrix-build-1"));
        });
    }

    [Test]
    public async Task Agent_deployment_commands_use_saved_pinned_endpoint_and_print_operation_identity()
    {
        var endpoint = new RecordingEndpoint();
        var console = new RecordingConsole();
        var application = new VivariumCliApplication(
            console,
            new StaticConfigurationStore(new ClientConfiguration(
                "https://controller:8443", Fingerprint('a'), "token")),
            new UnusedCertificateProbe(),
            new StaticEndpointFactory(endpoint),
            new TemporaryPayloadArchiveFactory(),
            _ => null);

        var upgradeExit = await application.ExecuteAsync(
        [
            "agent", "upgrade", "agent-1",
            "--reason", "canary", "--no-wait",
        ], CancellationToken.None);
        var cancellationExit = await application.ExecuteAsync(
        [
            "agent", "upgrade-rollback", "operation-1", "--reason", "bad canary", "--no-wait",
        ], CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(upgradeExit, Is.Zero);
            Assert.That(cancellationExit, Is.Zero);
            Assert.That(endpoint.CreateUpgradeCalls, Is.EqualTo(1));
            Assert.That(endpoint.CancelUpgradeCalls, Is.EqualTo(1));
            Assert.That(endpoint.UpgradeReason, Is.EqualTo("canary"));
            Assert.That(endpoint.UpgradeCancellationReason, Is.EqualTo("bad canary"));
            Assert.That(console.Output, Has.Some.Contains("Server release: 2.0.0"));
            Assert.That(console.Output, Has.Some.Contains("operation-1"));
            Assert.That(console.Output, Has.Some.Contains("AWAITING-HEALTH"));
            Assert.That(console.Output, Has.Some.Contains("ROLLBACK-REQUESTED"));
            Assert.That(console.Output, Has.Some.Contains("Maintenance drain: HELD"));
        });
    }

    [Test]
    public async Task No_wait_with_real_controller_pins_tls_uploads_and_commits_matrix()
    {
        var payload = Path.Combine(root, "real-payload");
        Directory.CreateDirectory(payload);
        await File.WriteAllTextAsync(Path.Combine(payload, "test.txt"), "real payload");
        var yamlPath = Path.Combine(root, "real-vivarium.yaml");
        await File.WriteAllTextAsync(yamlPath, """
            project: Vivarium
            configurations:
              tier-2:
                matrix:
                  windows:
                    agent: os.family == windows
                    rid: win-x64
                payload: real-payload
                steps:
                  - program: tests.exe
                clean: none
            """, new UTF8Encoding(false));

        await using var controller = await VivariumControllerHost.StartAsync(new ControllerOptions
        {
            DataDir = Path.Combine(root, "controller"),
            Host = "127.0.0.1",
            Port = 0,
        });
        var enrollToken = await controller.Tokens.CreateEnrollTokenAsync();
        var hello = new Hello
        {
            AgentId = "known-windows",
            EnrollToken = enrollToken,
            SessionId = "offline-session",
            Os = new OsInfo { Family = "windows", Arch = "x64", Version = "test" },
        };
        hello.Parameters["hostname"] = "known-windows";
        hello.Parameters["os.family"] = "windows";
        Assert.That(await controller.Tokens.AdmitAgentAsync(hello), Is.Not.Null);
        await controller.AgentStore.ObserveHelloAsync(hello);

        var console = new RecordingConsole();
        var application = new VivariumCliApplication(
            console,
            new StaticConfigurationStore(new ClientConfiguration(
                controller.Url,
                "SHA256:" + controller.Certificate.FingerprintSha256,
                controller.Tokens.SubmitToken)),
            new UnusedCertificateProbe(),
            new ControlPlaneEndpointFactory(),
            new TemporaryPayloadArchiveFactory(),
            _ => null);

        var exitCode = await application.ExecuteAsync(
            ["run", "tier-2", "--file", yamlPath, "--no-wait"], CancellationToken.None);
        Assert.That(exitCode, Is.Zero, string.Join(Environment.NewLine, console.Output));
        var buildId = console.Output.Single(line => line.StartsWith("Submitted matrix build ", StringComparison.Ordinal))
            ["Submitted matrix build ".Length..];
        var snapshot = await controller.MatrixBuildStore.GetSnapshotAsync(buildId);
        var queueItem = (await controller.BuildQueueStore.ListPendingAsync()).Single();

        Assert.Multiple(() =>
        {
            Assert.That(exitCode, Is.Zero);
            Assert.That(snapshot, Is.Not.Null);
            Assert.That(snapshot!.Cells.Single().Name, Is.EqualTo("windows"));
            Assert.That(queueItem.Assignment.Parameters["cell"], Is.EqualTo("windows"));
            Assert.That(queueItem.Assignment.Parameters["rid"], Is.EqualTo("win-x64"));
            Assert.That(controller.Blobs.Contains(queueItem.Assignment.Payload.Single().Sha256), Is.True);
        });
    }

    private static BuildSnapshot Snapshot(params BuildOutcome[] outcomes)
    {
        var snapshot = new BuildSnapshot { State = DurableBuildState.Finished };
        for (var index = 0; index < outcomes.Length; index++)
        {
            snapshot.Cells.Add(new BuildCellSnapshot
            {
                Name = $"cell-{index}",
                BuildId = $"build-{index}",
                State = DurableBuildState.Finished,
                Outcome = outcomes[index],
            });
        }

        return snapshot;
    }

    private static string Fingerprint(char value) => "SHA256:" + new string(value, 64);

    private sealed class StaticConfigurationStore(ClientConfiguration value) : IClientConfigurationStore
    {
        public Task<ClientConfiguration?> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult<ClientConfiguration?>(value);

        public Task SaveAsync(ClientConfiguration configuration, CancellationToken cancellationToken) =>
            throw new AssertionException("configuration should not be saved during run");
    }

    private sealed class RecordingConfigurationStore : IClientConfigurationStore
    {
        public ClientConfiguration? Saved { get; private set; }

        public Task<ClientConfiguration?> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult<ClientConfiguration?>(null);

        public Task SaveAsync(ClientConfiguration configuration, CancellationToken cancellationToken)
        {
            Saved = configuration;
            return Task.CompletedTask;
        }
    }

    private sealed class UnusedCertificateProbe : IServerCertificateProbe
    {
        public Task<string> GetFingerprintAsync(string controllerUrl, CancellationToken cancellationToken) =>
            throw new AssertionException("certificate discovery should not run during run");
    }

    private sealed class StaticCertificateProbe(string fingerprint) : IServerCertificateProbe
    {
        public Task<string> GetFingerprintAsync(string controllerUrl, CancellationToken cancellationToken) =>
            Task.FromResult(fingerprint);
    }

    private sealed class StaticEndpointFactory(IControlPlaneEndpoint endpoint) : IControlPlaneEndpointFactory
    {
        public EndpointSettings? LastSettings { get; private set; }

        public IControlPlaneEndpoint Create(EndpointSettings settings)
        {
            LastSettings = settings;
            return endpoint;
        }
    }

    private sealed class RecordingEndpoint : IControlPlaneEndpoint
    {
        public int StageCalls { get; private set; }
        public int ValidateCalls { get; private set; }
        public int SubmitCalls { get; private set; }
        public int WatchCalls { get; private set; }
        public int CancelCalls { get; private set; }
        public int CreateUpgradeCalls { get; private set; }
        public int CancelUpgradeCalls { get; private set; }
        public SubmitBuildRequest? Submitted { get; private set; }
        public IReadOnlyCollection<PayloadArchiveInfo>? StagedArchives { get; private set; }
        public string? CancelledBuildId { get; private set; }
        public string? CancellationReason { get; private set; }
        public string? UpgradeReason { get; private set; }
        public string? UpgradeCancellationReason { get; private set; }

        public Task ValidateAsync(CancellationToken cancellationToken)
        {
            ValidateCalls++;
            return Task.CompletedTask;
        }

        public Task<string> StageBlobsAsync(
            string projectId,
            IReadOnlyCollection<PayloadArchiveInfo> archives,
            string idempotencyKey,
            CancellationToken cancellationToken)
        {
            StageCalls++;
            StagedArchives = archives;
            Assert.That(projectId, Is.EqualTo("Vivarium"));
            Assert.That(archives.All(archive => File.Exists(archive.Path)), Is.True);
            Assert.That(idempotencyKey, Is.Not.Empty);
            return Task.FromResult("stage-1");
        }

        public Task<BuildRef> SubmitBuildAsync(
            SubmitBuildRequest request,
            string blobStagingId,
            CancellationToken cancellationToken)
        {
            SubmitCalls++;
            Submitted = request;
            Assert.That(blobStagingId, Is.EqualTo("stage-1"));
            return Task.FromResult(new BuildRef { BuildId = "matrix-build-1" });
        }

        public Task<BuildSnapshot> CancelBuildAsync(
            string buildId,
            string reason,
            CancellationToken cancellationToken)
        {
            CancelCalls++;
            CancelledBuildId = buildId;
            CancellationReason = reason;
            return Task.FromResult(new BuildSnapshot
            {
                Build = new BuildRef { BuildId = buildId },
                State = DurableBuildState.CancelRequested,
            });
        }

        public Task<AgentUpgradeSnapshot> CreateAgentUpgradeAsync(
            string agentId,
            string reason,
            int? timeoutSeconds,
            string idempotencyKey,
            CancellationToken cancellationToken)
        {
            CreateUpgradeCalls++;
            UpgradeReason = reason;
            Assert.That(idempotencyKey, Is.Not.Empty);
            return Task.FromResult(new AgentUpgradeSnapshot(
                "operation-1", agentId, "2.0.0", "awaiting-health", 1,
                1, DateTimeOffset.UtcNow.AddSeconds(5), null, null, null,
                true, DateTimeOffset.UtcNow.AddMinutes(10),
                [new AgentUpgradeEventSnapshot(
                    1, "awaiting-health", "candidate_observed", 2, new string('a', 64),
                    DateTimeOffset.UtcNow)]));
        }

        public Task<AgentUpgradeSnapshot> CancelAgentUpgradeAsync(
            string operationId,
            string reason,
            CancellationToken cancellationToken)
        {
            CancelUpgradeCalls++;
            UpgradeCancellationReason = reason;
            return Task.FromResult(new AgentUpgradeSnapshot(
                operationId, "agent-1", "2.0.0", "rollback-requested", 1,
                1, DateTimeOffset.UtcNow.AddSeconds(5), reason, null, null,
                true, DateTimeOffset.UtcNow.AddMinutes(10),
                [new AgentUpgradeEventSnapshot(
                    2, "rollback-requested", "operator_cancelled", 2, new string('a', 64),
                    DateTimeOffset.UtcNow)]));
        }

        public async IAsyncEnumerable<BuildSnapshot> WatchBuildAsync(
            string buildId,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            WatchCalls++;
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingConsole : ICliConsole
    {
        public List<string> Output { get; } = [];
        public bool IsInteractive => false;
        public void WriteLine(string value) => Output.Add(value);
        public void WriteError(string value) => Output.Add(value);
        public Task<string?> ReadLineAsync(CancellationToken cancellationToken) =>
            Task.FromResult<string?>(null);
        public Task<string?> ReadSecretAsync(CancellationToken cancellationToken) =>
            Task.FromResult<string?>(null);
    }
}
