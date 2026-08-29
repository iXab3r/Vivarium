using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Vivarium.Controller.Agents;
using Vivarium.Controller.Auditing;
using Vivarium.Controller.Configuration.Agents;
using Vivarium.Controller.Configuration.Git;
using Vivarium.Controller.Configuration.Reconciliation;
using Vivarium.Controller.Persistence;
using Vivarium.Controller.Rest.Agents.Configuration;
using Vivarium.Controller.Rest.Common;
using Vivarium.Controller.Security;

namespace Vivarium.Tests;

[TestFixture]
[NonParallelizable]
public sealed class AgentDesiredConfigurationTests
{
    private const string RepositoryId = "controller";
    private const string AgentOne = "agent-one";
    private const string AgentTwo = "agent-two";
    private string rootDir = null!;
    private AgentLifecycleCoordinator agentLifecycle = null!;

    [SetUp]
    public void SetUp()
    {
        rootDir = Path.Combine(
            Path.GetTempPath(),
            "vivarium-agent-desired-configuration-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootDir);
        Directory.CreateDirectory(Path.Combine(rootDir, "data"));
        Directory.CreateDirectory(Path.Combine(rootDir, "controller-data"));
        Directory.CreateDirectory(Path.Combine(rootDir, "tokens"));
        agentLifecycle = new AgentLifecycleCoordinator();
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
    public async Task Enabled_mutation_commits_before_activation_and_survives_restart_with_exact_audit()
    {
        var dataDir = Path.Combine(rootDir, "controller-data");
        var repositoryPath = Path.Combine(rootDir, "configuration");
        ConfigurationRevision resultRevision;
        string operationId;

        await using (var database = new VivariumDatabase(dataDir))
        {
            await InsertAgentAsync(database, AgentOne, enabled: true);
            var repository = await ManagedGitRepository.OpenOrCreateAsync(repositoryPath, RepositoryId);
            var initial = await repository.GetAuthoritativeHeadAsync();
            var sink = new CapturingActivationSink();
            var service = await CreateServiceAsync(database, repository, sink);
            var before = await service.GetAsync(AgentOne);

            var changed = await service.SetEnabledAsync(
                AdminContext("disable-agent", "request-disable-agent"),
                AgentOne,
                enabled: false,
                initial);
            resultRevision = changed.ResultRevision;
            operationId = changed.OperationId;
            var stored = await new AgentStore(database).GetAsync(AgentOne);
            var committedBytes = await ReadGitFileAsync(
                repositoryPath,
                resultRevision,
                $".vivarium/agents/{AgentOne}.yaml");
            var audits = await new AuditEventStore(database).ListAsync();

            Assert.Multiple(() =>
            {
                Assert.That(before!.State, Is.EqualTo(AgentDesiredConfigurationState.Active));
                Assert.That(before.DesiredEnabled, Is.Null);
                Assert.That(before.AppliedEnabled, Is.Null);
                Assert.That(changed.Settings.State, Is.EqualTo(AgentDesiredConfigurationState.Active));
                Assert.That(changed.Settings.DesiredEnabled, Is.False);
                Assert.That(changed.Settings.AppliedEnabled, Is.False);
                Assert.That(changed.Settings.AuthoritativeRevision, Is.EqualTo(resultRevision));
                Assert.That(changed.Settings.AppliedRevision, Is.EqualTo(resultRevision));
                Assert.That(changed.Diff.Single().ChangeKind,
                    Is.EqualTo(ConfigurationPathChangeKind.Added));
                Assert.That(changed.Replayed, Is.False);
                Assert.That(stored!.Enabled, Is.False);
                Assert.That(sink.Activations.Single(), Is.EqualTo(
                    new AgentDesiredConfigurationActivation(
                        AgentOne,
                        Enabled: false,
                        resultRevision,
                        operationId)));
                Assert.That(Encoding.UTF8.GetString(committedBytes), Is.EqualTo(
                    AgentDocument(AgentOne, enabled: false)));
                Assert.That(audits.Single(audit =>
                        audit.Action == "configuration.mutation.requested" &&
                        audit.Details["operation_id"] == operationId).ActorId,
                    Is.EqualTo("admin-one"));
                Assert.That(audits.Single(audit =>
                        audit.Action == "configuration.revision.applied" &&
                        audit.Details["operation_id"] == operationId).ResultRevision,
                    Is.EqualTo(resultRevision.Canonical));
            });
        }

        await using (var restarted = new VivariumDatabase(dataDir))
        {
            var repository = await ManagedGitRepository.OpenOrCreateAsync(repositoryPath, RepositoryId);
            var service = await CreateServiceAsync(
                restarted,
                repository,
                new CapturingActivationSink());
            var restored = await service.GetAsync(AgentOne);
            var operation = await new ConfigurationReconciler(restarted, TimeProvider.System)
                .Operations.GetAsync(operationId);

            Assert.Multiple(() =>
            {
                Assert.That(restored!.State, Is.EqualTo(AgentDesiredConfigurationState.Active));
                Assert.That(restored.AuthoritativeRevision, Is.EqualTo(resultRevision));
                Assert.That(restored.AppliedRevision, Is.EqualTo(resultRevision));
                Assert.That(restored.AppliedEnabled, Is.False);
                Assert.That(operation!.State, Is.EqualTo(ConfigurationMutationState.Applied));
                Assert.That(operation.ResultRevision, Is.EqualTo(resultRevision));
            });
        }
    }

    [Test]
    public async Task Idempotency_is_path_scoped_and_exact_replay_wins_after_head_moves()
    {
        await using var database = new VivariumDatabase(Path.Combine(rootDir, "data"));
        await InsertAgentAsync(database, AgentOne, enabled: true);
        await InsertAgentAsync(database, AgentTwo, enabled: true);
        var repository = await ManagedGitRepository.OpenOrCreateAsync(
            Path.Combine(rootDir, "configuration"),
            RepositoryId);
        var sink = new CapturingActivationSink();
        var service = await CreateServiceAsync(database, repository, sink);
        var initial = await repository.GetAuthoritativeHeadAsync();
        var sharedRequest = AdminContext("shared", "same-idempotency-key");

        var first = await service.SetEnabledAsync(
            sharedRequest,
            AgentOne,
            enabled: false,
            initial);
        var second = await service.SetEnabledAsync(
            sharedRequest,
            AgentTwo,
            enabled: false,
            first.ResultRevision);
        var replay = await service.SetEnabledAsync(
            sharedRequest with { CorrelationId = "replay-correlation" },
            AgentOne,
            enabled: false,
            initial);
        var reused = Assert.ThrowsAsync<AgentDesiredConfigurationConflictException>(async () =>
            await service.SetEnabledAsync(
                sharedRequest,
                AgentOne,
                enabled: true,
                initial));
        var finalHead = await repository.GetAuthoritativeHeadAsync();

        Assert.Multiple(() =>
        {
            Assert.That(second.OperationId, Is.Not.EqualTo(first.OperationId),
                "the same principal/key is independent across canonical Agent paths");
            Assert.That(replay.OperationId, Is.EqualTo(first.OperationId));
            Assert.That(replay.ResultRevision, Is.EqualTo(first.ResultRevision));
            Assert.That(replay.Replayed, Is.True);
            Assert.That(replay.Settings.AuthoritativeRevision, Is.EqualTo(first.ResultRevision),
                "an idempotent replay returns the original semantic response");
            Assert.That(finalHead, Is.EqualTo(second.ResultRevision));
            Assert.That(reused!.Code, Is.EqualTo("idempotency_key_reused"));
            Assert.That(sink.Activations, Has.Count.EqualTo(2),
                "replaying a superseded result must not restore old live policy");
        });
    }

    [Test]
    public async Task Stale_base_is_distinct_from_validation_and_preserves_normalized_draft_diff()
    {
        await using var database = new VivariumDatabase(Path.Combine(rootDir, "data"));
        await InsertAgentAsync(database, AgentOne, enabled: true);
        await InsertAgentAsync(database, AgentTwo, enabled: true);
        var repository = await ManagedGitRepository.OpenOrCreateAsync(
            Path.Combine(rootDir, "configuration"),
            RepositoryId);
        var service = await CreateServiceAsync(
            database,
            repository,
            new CapturingActivationSink());
        var initial = await repository.GetAuthoritativeHeadAsync();
        _ = await service.SetEnabledAsync(
            AdminContext("first", "first-request"),
            AgentOne,
            enabled: false,
            initial);

        var staleContext = AdminContext("stale", "stale-request");
        var stale = Assert.ThrowsAsync<AgentDesiredConfigurationPreconditionException>(async () =>
            await service.SetEnabledAsync(
                staleContext,
                AgentOne,
                enabled: true,
                initial));
        var headAtConflict = stale!.CurrentRevision;
        var advanced = await service.SetEnabledAsync(
            AdminContext("advance-after-conflict", "advance-after-conflict-request"),
            AgentTwo,
            enabled: false,
            headAtConflict);
        var replay = Assert.ThrowsAsync<AgentDesiredConfigurationPreconditionException>(async () =>
            await service.SetEnabledAsync(
                staleContext with { CorrelationId = "stale-replay" },
                AgentOne,
                enabled: true,
                initial));
        var invalid = Assert.ThrowsAsync<AgentDesiredConfigurationValidationException>(async () =>
            await service.SetEnabledAsync(
                AdminContext("invalid", "invalid-request"),
                "Agent-UPPER",
                enabled: false,
                initial));

        Assert.Multiple(() =>
        {
            Assert.That(stale.CurrentRevision, Is.Not.EqualTo(initial));
            Assert.That(stale.Diff.Single().Path,
                Is.EqualTo($".vivarium/agents/{AgentOne}.yaml"));
            Assert.That(stale.Diff.Single().ResultContentHash, Has.Length.EqualTo(64));
            Assert.That(advanced.ResultRevision, Is.Not.EqualTo(headAtConflict));
            Assert.That(replay!.CurrentRevision, Is.EqualTo(headAtConflict));
            Assert.That(replay.Diff, Is.EqualTo(stale.Diff));
            Assert.That(invalid!.Code, Is.EqualTo("agent_id_invalid"));
        });
    }

    [Test]
    public async Task Application_boundary_denial_is_audited_before_validation_or_git_state_changes()
    {
        await using var database = new VivariumDatabase(Path.Combine(rootDir, "data"));
        await InsertAgentAsync(database, AgentOne, enabled: true);
        var repository = await ManagedGitRepository.OpenOrCreateAsync(
            Path.Combine(rootDir, "configuration"),
            RepositoryId);
        var service = await CreateServiceAsync(
            database,
            repository,
            new CapturingActivationSink());
        var initial = await repository.GetAuthoritativeHeadAsync();
        var deniedContext = new ManagementRequestContext(
            ManagementPrincipal.LegacySubmit,
            "denied-correlation",
            "denied-request",
            "agent-desired-configuration-test");

        var denied = Assert.ThrowsAsync<ManagementAuthorizationException>(async () =>
            await service.SetEnabledAsync(
                deniedContext,
                "INVALID-AGENT-ID",
                enabled: false,
                initial));
        var audits = await new AuditEventStore(database).ListAsync();
        var denialAudit = audits.Single(audit =>
            audit.Action == "agent.configuration.disable" &&
            audit.Outcome == AuditOutcome.Denied);
        var finalHead = await repository.GetAuthoritativeHeadAsync();

        Assert.Multiple(() =>
        {
            Assert.That(denied!.Permission, Is.EqualTo(ManagementPermission.AgentManage));
            Assert.That(finalHead, Is.EqualTo(initial));
            Assert.That(denialAudit.TargetId, Is.EqualTo("INVALID-AGENT-ID"));
            Assert.That(denialAudit.CorrelationId, Is.EqualTo("denied-correlation"));
        });
    }

    [Test]
    public async Task Concurrent_exact_retries_serialize_to_one_operation_and_one_commit()
    {
        await using var database = new VivariumDatabase(Path.Combine(rootDir, "data"));
        await InsertAgentAsync(database, AgentOne, enabled: true);
        var repository = await ManagedGitRepository.OpenOrCreateAsync(
            Path.Combine(rootDir, "configuration"),
            RepositoryId);
        var service = await CreateServiceAsync(
            database,
            repository,
            new CapturingActivationSink());
        var initial = await repository.GetAuthoritativeHeadAsync();
        var context = AdminContext("concurrent", "concurrent-request");

        var results = await Task.WhenAll(
            service.SetEnabledAsync(context, AgentOne, enabled: false, initial),
            service.SetEnabledAsync(
                context with { CorrelationId = "concurrent-retry" },
                AgentOne,
                enabled: false,
                initial));
        var validation = await repository.ValidateRevisionAsync(results[0].ResultRevision);

        Assert.Multiple(() =>
        {
            Assert.That(results.Select(result => result.OperationId).Distinct().Count(), Is.EqualTo(1));
            Assert.That(results.Select(result => result.ResultRevision).Distinct().Count(), Is.EqualTo(1));
            Assert.That(results.Count(result => result.Replayed), Is.EqualTo(1));
            Assert.That(validation.Validated!.Descriptor.Parents, Is.EqualTo(new[] { initial }));
        });
    }

    [Test]
    public async Task Head_advance_between_commit_and_apply_never_materializes_the_stale_commit()
    {
        await using var database = new VivariumDatabase(Path.Combine(rootDir, "data"));
        await InsertAgentAsync(database, AgentOne, enabled: true);
        await InsertAgentAsync(database, AgentTwo, enabled: true);
        var inner = await ManagedGitRepository.OpenOrCreateAsync(
            Path.Combine(rootDir, "configuration"),
            RepositoryId);
        var initial = await inner.GetAuthoritativeHeadAsync();
        var repository = new AdvancingRepository(inner, AgentTwo);
        var reconciler = new ConfigurationReconciler(database, TimeProvider.System);
        _ = await reconciler.ReconcileAuthoritativeHeadAsync(
            ManagementRequestContext.System("initial-reconcile"),
            AgentDesiredConfigurationService.MaterializationScope,
            repository);
        var service = CreateService(
            database,
            repository,
            reconciler,
            new CapturingActivationSink());

        var conflict = Assert.ThrowsAsync<AgentDesiredConfigurationConflictException>(async () =>
            await service.SetEnabledAsync(
                AdminContext("raced", "raced-request"),
                AgentOne,
                enabled: false,
                initial));
        var stored = await new AgentStore(database).GetAsync(AgentOne);
        var state = await reconciler.GetStateAsync(AgentDesiredConfigurationService.MaterializationScope);
        var audits = await new AuditEventStore(database).ListAsync();
        var operationId = audits.Single(audit =>
            audit.Action == "configuration.mutation.committed").Details["operation_id"];
        var operation = await reconciler.Operations.GetAsync(operationId);
        var finalHead = await inner.GetAuthoritativeHeadAsync();

        Assert.Multiple(() =>
        {
            Assert.That(conflict!.Code, Is.EqualTo("configuration_head_advanced_before_apply"));
            Assert.That(stored!.Enabled, Is.True);
            Assert.That(ControlRevision(state!.Active!), Is.EqualTo(initial));
            Assert.That(operation!.State, Is.EqualTo(ConfigurationMutationState.Committed));
            Assert.That(operation.ResultRevision, Is.Not.EqualTo(finalHead));
            Assert.That(audits.Any(audit => audit.Action == "configuration.revision.applied" &&
                audit.ResultRevision == operation.ResultRevision!.Canonical), Is.False);
        });
    }

    [Test]
    public async Task Rest_shape_requires_reversible_precondition_explicit_body_and_idempotency()
    {
        await using var database = new VivariumDatabase(Path.Combine(rootDir, "data"));
        await InsertAgentAsync(database, AgentOne, enabled: true);
        await InsertAgentAsync(database, AgentTwo, enabled: true);
        var tokens = new TokenStore(Path.Combine(rootDir, "tokens"), database);
        var repository = await ManagedGitRepository.OpenOrCreateAsync(
            Path.Combine(rootDir, "configuration"),
            RepositoryId);
        var service = await CreateServiceAsync(
            database,
            repository,
            new CapturingActivationSink());
        await using var app = await StartRestAppAsync(database, tokens, service);
        using var client = CreateClient(app);

        var get = await client.GetAsync($"/api/v1/agents/{AgentOne}/settings");
        var etag = get.Headers.ETag!.Tag;
        Assert.That(AgentConfigurationEtags.TryParse(etag, out var parsed), Is.True);
        Assert.That(parsed, Is.EqualTo(await repository.GetAuthoritativeHeadAsync()));

        var missingPrecondition = await PutAsync(
            client,
            AgentOne,
            new { enabled = false },
            etag: null,
            idempotencyKey: "missing-precondition");
        var missingIdempotency = await PutAsync(
            client,
            AgentOne,
            new { enabled = false },
            etag,
            idempotencyKey: null);
        var missingBodyField = await PutAsync(
            client,
            AgentOne,
            new { },
            etag,
            idempotencyKey: "missing-enabled");
        var accepted = await PutAsync(
            client,
            AgentOne,
            new { enabled = false },
            etag,
            idempotencyKey: "agent-one-change");
        var acceptedJson = await accepted.Content.ReadFromJsonAsync<JsonElement>();
        var headEtag = accepted.Headers.ETag!.Tag;
        var advanced = await PutAsync(
            client,
            AgentTwo,
            new { enabled = false },
            headEtag,
            idempotencyKey: "agent-two-change");
        var currentEtag = advanced.Headers.ETag!.Tag;
        var replay = await PutAsync(
            client,
            AgentOne,
            new { enabled = false },
            etag,
            idempotencyKey: "agent-one-change");
        var replayJson = await replay.Content.ReadFromJsonAsync<JsonElement>();
        var stale = await PutAsync(
            client,
            AgentOne,
            new { enabled = true },
            etag,
            idempotencyKey: "stale-change");
        var reused = await PutAsync(
            client,
            AgentOne,
            new { enabled = true },
            etag,
            idempotencyKey: "agent-one-change");

        Assert.Multiple(() =>
        {
            Assert.That(get.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(missingPrecondition.StatusCode, Is.EqualTo((HttpStatusCode)428));
            Assert.That(missingIdempotency.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(missingBodyField.StatusCode, Is.EqualTo(HttpStatusCode.UnprocessableEntity));
            Assert.That(accepted.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(acceptedJson.GetProperty("state").GetString(), Is.EqualTo("applied"));
            Assert.That(replay.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(replayJson.GetProperty("replayed").GetBoolean(), Is.True);
            Assert.That(replayJson.GetProperty("operationId").GetString(),
                Is.EqualTo(acceptedJson.GetProperty("operationId").GetString()));
            Assert.That(stale.StatusCode, Is.EqualTo(HttpStatusCode.PreconditionFailed));
            Assert.That(reused.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        });

        using var conditional = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/v1/agents/{AgentOne}/settings");
        conditional.Headers.TryAddWithoutValidation("If-None-Match", currentEtag);
        var notModified = await client.SendAsync(conditional);
        Assert.That(notModified.StatusCode, Is.EqualTo(HttpStatusCode.NotModified));

        using var staleConditional = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/v1/agents/{AgentOne}/settings");
        staleConditional.Headers.TryAddWithoutValidation(
            "If-None-Match",
            replay.Headers.ETag!.Tag);
        var currentRepresentation = await client.SendAsync(staleConditional);
        Assert.That(currentRepresentation.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task Get_maps_repository_failure_to_a_bounded_retryable_503_problem()
    {
        await using var database = new VivariumDatabase(Path.Combine(rootDir, "data"));
        await InsertAgentAsync(database, AgentOne, enabled: true);
        var tokens = new TokenStore(Path.Combine(rootDir, "tokens"), database);
        var service = CreateService(
            database,
            new UnavailableRepository(),
            new ConfigurationReconciler(database, TimeProvider.System),
            new CapturingActivationSink());
        await using var app = await StartRestAppAsync(database, tokens, service);
        using var client = CreateClient(app);

        var response = await client.GetAsync($"/api/v1/agents/{AgentOne}/settings");
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
            Assert.That(response.Content.Headers.ContentType?.MediaType,
                Is.EqualTo("application/problem+json"));
            Assert.That(problem.GetProperty("title").GetString(),
                Is.EqualTo("The configuration repository is unavailable"));
            Assert.That(problem.GetProperty("code").GetString(), Is.EqualTo("config_broken"));
            Assert.That(problem.GetProperty("retryable").GetBoolean(), Is.True);
            Assert.That(problem.GetProperty("detail").GetString(),
                Is.EqualTo("The configuration repository is temporarily unavailable."));
        });
    }

    private async Task<AgentDesiredConfigurationService> CreateServiceAsync(
        VivariumDatabase database,
        IConfigurationRepository repository,
        IAgentDesiredConfigurationActivationSink sink)
    {
        var reconciler = new ConfigurationReconciler(database, TimeProvider.System);
        _ = await reconciler.ReconcileAuthoritativeHeadAsync(
            ManagementRequestContext.System("test-initial-reconcile"),
            AgentDesiredConfigurationService.MaterializationScope,
            repository);
        return CreateService(database, repository, reconciler, sink);
    }

    private AgentDesiredConfigurationService CreateService(
        VivariumDatabase database,
        IConfigurationRepository repository,
        ConfigurationReconciler reconciler,
        IAgentDesiredConfigurationActivationSink sink)
    {
        var authorizer = new ManagementAuthorizer();
        return new AgentDesiredConfigurationService(
            repository,
            reconciler,
            new AgentStore(database),
            agentLifecycle,
            new ManagementCommandAuthorizer(
                authorizer,
                new AuditEventStore(database),
                TimeProvider.System),
            sink);
    }

    private static ManagementRequestContext AdminContext(string correlationId, string requestId) =>
        new(
            new ManagementPrincipal("user", "admin-one", "test", BearerScope.Admin),
            correlationId,
            requestId,
            "agent-desired-configuration-test");

    private static Task InsertAgentAsync(
        VivariumDatabase database,
        string agentId,
        bool enabled) => database.WriteAsync(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO agents(
                agent_id, name, enabled, first_seen_unix_ms, last_seen_unix_ms)
            VALUES ($agentId, $agentId, $enabled, 1, 1);
            """;
        command.Parameters.AddWithValue("$agentId", agentId);
        command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        command.ExecuteNonQuery();
        return true;
    });

    private static string AgentDocument(string agentId, bool enabled) => $"""
        apiVersion: vivarium.io/v1alpha1
        kind: Agent
        id: {agentId}
        spec:
          enabled: {enabled.ToString().ToLowerInvariant()}

        """;

    private static async Task<byte[]> ReadGitFileAsync(
        string repositoryPath,
        ConfigurationRevision revision,
        string path)
    {
        var start = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = repositoryPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("show");
        start.ArgumentList.Add($"{revision.Commit}:{path}");
        using var process = System.Diagnostics.Process.Start(start)
            ?? throw new InvalidOperationException("git did not start");
        await using var output = new MemoryStream();
        var copy = process.StandardOutput.BaseStream.CopyToAsync(output);
        var error = process.StandardError.ReadToEndAsync();
        await Task.WhenAll(copy, error, process.WaitForExitAsync());
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(await error);
        }

        return output.ToArray();
    }

    private static ConfigurationRevision ControlRevision(StoredConfigurationRevisionSet set)
    {
        var member = set.Members.Single(member => member.RepositoryRole == "CONTROL");
        return new ConfigurationRevision(member.RepositoryId, member.Commit);
    }

    private static async Task<WebApplication> StartRestAppAsync(
        VivariumDatabase database,
        TokenStore tokens,
        AgentDesiredConfigurationService service)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Services.AddSingleton(tokens);
        builder.Services.AddSingleton(new ManagementAuthorizer());
        builder.Services.AddSingleton<ManagementRequestContextFactory>();
        builder.Services.AddSingleton(service);
        builder.Services.AddVivariumRestApi();
        var app = builder.Build();
        app.UseVivariumRestApi();
        app.Use(async (context, next) =>
        {
            context.User = ManagementRequestContextFactory.CreateClaimsPrincipal(
                ManagementPrincipal.LegacyAdmin);
            await next(context);
        });
        app.MapAgentDesiredConfigurationRestApi();
        await app.StartAsync();
        return app;
    }

    private static HttpClient CreateClient(WebApplication app)
    {
        var addresses = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()?.Addresses;
        var address = addresses?.Single()
            ?? throw new InvalidOperationException("test Kestrel address is unavailable");
        return new HttpClient { BaseAddress = new Uri(address) };
    }

    private static async Task<HttpResponseMessage> PutAsync(
        HttpClient client,
        string agentId,
        object body,
        string? etag,
        string? idempotencyKey)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"/api/v1/agents/{agentId}/settings")
        {
            Content = JsonContent.Create(body),
        };
        if (etag is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", etag);
        }

        if (idempotencyKey is not null)
        {
            request.Headers.TryAddWithoutValidation(
                AgentDesiredConfigurationEndpoints.IdempotencyHeader,
                idempotencyKey);
        }

        return await client.SendAsync(request);
    }

    private sealed class CapturingActivationSink : IAgentDesiredConfigurationActivationSink
    {
        public List<AgentDesiredConfigurationActivation> Activations { get; } = [];

        public void OnApplied(AgentDesiredConfigurationActivation activation) =>
            Activations.Add(activation);
    }

    private sealed class AdvancingRepository(
        IConfigurationRepository inner,
        string otherAgentId) : IConfigurationRepository
    {
        private bool advanced;

        public string RepositoryId => inner.RepositoryId;

        public Task<ConfigurationRevision> GetAuthoritativeHeadAsync(
            CancellationToken cancellationToken = default) =>
            inner.GetAuthoritativeHeadAsync(cancellationToken);

        public Task<ConfigurationRevisionValidation> ValidateRevisionAsync(
            ConfigurationRevision revision,
            CancellationToken cancellationToken = default) =>
            inner.ValidateRevisionAsync(revision, cancellationToken);

        public async Task<ConfigurationCommitResult> UpsertDocumentAsync(
            ConfigurationDocumentMutation mutation,
            CancellationToken cancellationToken = default)
        {
            var result = await inner.UpsertDocumentAsync(mutation, cancellationToken);
            if (!advanced && result.Outcome == ConfigurationCommitOutcome.Committed)
            {
                advanced = true;
                var head = result.ResultRevision!;
                var second = await inner.UpsertDocumentAsync(
                    new ConfigurationDocumentMutation(
                        head,
                        $".vivarium/agents/{otherAgentId}.yaml",
                        Encoding.UTF8.GetBytes(AgentDocument(otherAgentId, enabled: true)),
                        new ConfigurationCommitMetadata(
                            "Advance configuration head",
                            "external-advance-operation",
                            "external-advance-request",
                            "external-advance-correlation",
                            new ConfigurationCommitActor(
                                "external-admin",
                                "user",
                                "External Admin"))),
                    cancellationToken);
                if (second.Outcome != ConfigurationCommitOutcome.Committed)
                {
                    throw new InvalidOperationException("test head advance did not commit");
                }
            }

            return result;
        }
    }

    private sealed class UnavailableRepository : IConfigurationRepository
    {
        public string RepositoryId => AgentDesiredConfigurationTests.RepositoryId;

        public Task<ConfigurationRevision> GetAuthoritativeHeadAsync(
            CancellationToken cancellationToken = default) =>
            throw new ConfigurationRepositoryException(
                "CONFIG/BROKEN",
                "sensitive repository failure");

        public Task<ConfigurationRevisionValidation> ValidateRevisionAsync(
            ConfigurationRevision revision,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("the unavailable repository has no revision");

        public Task<ConfigurationCommitResult> UpsertDocumentAsync(
            ConfigurationDocumentMutation mutation,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("the unavailable repository cannot commit");
    }
}
