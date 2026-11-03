using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Chiral;
using EyeAuras.Storage;
using EyeAuras.Storage.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Vivarium.Controller.Hosting;
using Vivarium.Controller.Persistence;
using Vivarium.Controller.Catalog;
using Vivarium.Controller.Builds.Runtime;

namespace Vivarium.DefinitionProof;

[NonParallelizable]
public sealed class RuntimeHostPhysicalTests
{
    private string? ownedRoot;

    [TearDown]
    public void RemoveOwnedPrivateFiles()
    {
        if (ownedRoot is null) return;
        var parent = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.WorkDirectory, "artifacts/r0-host"))
            + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(ownedRoot);
        if (!target.StartsWith(parent, StringComparison.OrdinalIgnoreCase)
            || !Regex.IsMatch(Path.GetFileName(target), "^runtime-[a-f0-9]{32}$"))
            throw new InvalidDataException("Owned private fixture cleanup target differs.");
        if (Directory.Exists(target))
        {
            foreach (var file in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
            Directory.Delete(target, recursive: true);
        }
        ownedRoot = null;
    }

    [Test]
    public async Task Ordinary_published_CLI_runs_fails_cancels_downloads_and_retains_builds_across_Host_restart()
    {
        await using var postgres = new OwnedPostgres();
        await postgres.StartAsync();
        var root = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.WorkDirectory,
            "artifacts/r0-host/runtime-" + Guid.NewGuid().ToString("N")));
        ownedRoot = root;
        Directory.CreateDirectory(root);
        var secretScenario = new RuntimeSecretScenario(root);
        var definitions = Path.Combine(root, "definitions");
        Directory.CreateDirectory(definitions);
        await SourceCompiler.GitAsync(definitions, "init", "-b", "main");
        await SourceCompiler.GitAsync(definitions, "config", "core.autocrlf", "false");
        await SourceCompiler.GitAsync(definitions, "config", "user.name", "Owned Host fixture");
        await SourceCompiler.GitAsync(definitions, "config", "user.email", "fixture@example.invalid");
        var source = ProductionEvaluatorPhysicalTests.Sources();
        // The first line is a cmd label and a complete sh program. Both actual Agents run the same
        // committed configuration; MODE controls the source fixture's success/failure/long variants.
        var cliScript = """
            :; if [ "$MODE" = fail ]; then exit 7; fi; if [ "$MODE" = fail8 ]; then exit 8; fi; if [ "$MODE" = long ]; then trap 'i=0; while [ ! -f /data/allow-cancel-exit ] && [ $i -lt 100 ]; do sleep 0.1; i=$((i+1)); done; exit 0' TERM; echo owned-running-start; while :; do sleep 0.1; done; fi; echo from CSharp Git; printf 'owned-smoke-artifact\n' > proof.txt; echo "##teamcity[publishArtifacts 'proof.txt']"; exit 0
            if "%MODE%"=="fail" exit /b 7
            if "%MODE%"=="fail8" exit /b 8
            if "%MODE%"=="long" (echo owned-running-start&ping -n 60 127.0.0.1 >nul)
            echo from CSharp Git
            echo owned-smoke-artifact>proof.txt
            echo ##teamcity[publishArtifacts 'proof.txt']
            """;
        var smokeSettings = source["Settings.g.cs"].Replace("\"echo from CSharp Git\"", JsonSerializer.Serialize(cliScript), StringComparison.Ordinal);
        var waitingScript = OperatingSystem.IsWindows()
            ? "echo owned-running-start\nping -n 60 127.0.0.1 >nul" : "trap '' TERM\necho owned-running-start\nsleep 60";
        smokeSettings = smokeSettings.Replace("new(\"P_LocalSmoke\",", $$"""
            new("P_Secret", "Secret", [], [
                new("secret", "Secret", "simpleRunner", [new("script.content", {{JsonSerializer.Serialize(secretScenario.Script)}}, null)])
            ], [], [], [], 2, []),
            new("P_Nonzero", "Nonzero", [], [
                new("fail", "Fail", "simpleRunner", [new("script.content", "echo owned-nonzero\nexit 7", null)])
            ], [], [], [], 1, []),
            new("P_Wait", "Wait", [], [
                new("wait", "Wait", "simpleRunner", [new("script.content", {{JsonSerializer.Serialize(waitingScript)}}, null)])
            ], [], [], [], 2, []),
            new("P_LocalSmoke",
            """, StringComparison.Ordinal);
        var files = new Dictionary<string, string>
        {
            [".vivarium/repository.yaml"] = """
                apiVersion: vivarium.io/v1alpha1
                kind: Repository
                id: host-fixture
                spec:
                  schemaVersion: "2"
                  documentKinds:
                    - Project
                """,
            [".vivarium/projects/P/Control.g.cs"] = source["Control.g.cs"],
            [".vivarium/unbound/P/Settings.g.cs"] = smokeSettings,
            [".vivarium/unbound/P/UserCode.cs"] = source["UserCode.cs"],
            [".vivarium/projects/Q/Control.g.cs"] = source["Control.g.cs"].Replace("\"P\"", "\"Q\"").Replace("Project P", "Project Q"),
            [".vivarium/unbound/Q/Settings.g.cs"] = smokeSettings.Replace("\"P\"", "\"Q\"").Replace("P_", "Q_"),
            [".vivarium/unbound/Q/UserCode.cs"] = source["UserCode.cs"],
        };
        foreach (var (path, text) in files)
        {
            var target = Path.Combine(definitions, path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllTextAsync(target, text);
        }
        await SourceCompiler.GitAsync(definitions, "add", "--all");
        await SourceCompiler.GitAsync(definitions, "commit", "-m", "fixture: Host Git catalogue");
        var directory = Path.Combine(root, "nodes");
        var publicPort = Port();
        var storagePort = Port();
        Assert.That(publicPort, Is.Not.EqualTo(storagePort));
        var origin = $"https://127.0.0.1:{publicPort}";
        var storageOrigin = $"http://127.0.0.1:{storagePort}";
        var certificate = Certificate(root, "https", tls: true);
        var identity = new JsonObject
        {
            ["SuperuserEnabled"] = true, ["TokenLifetimeDays"] = 3,
            ["DataProtectionDirectory"] = Path.Combine(root, "keys"),
            ["Providers"] = new JsonArray(), ["TokenCreationPermitLimit"] = 10,
            ["TokenCreationWindowSeconds"] = 60, ["TokenCreationConcurrencyLimit"] = 2,
            ["TokenCreationQueueLimit"] = 0, ["TokenRedemptionConcurrencyLimit"] = 2,
        };
        foreach (var name in new[] { "ServerSigningCertificate", "ServerEncryptionCertificate",
            "ClientSigningCertificate", "ClientEncryptionCertificate" })
            identity[name] = new JsonObject { ["Path"] = Certificate(root, name, tls: false).Path };
        var storage = new LocalStorageOptions
        {
            Root = Path.Combine(root, "objects"), PublicBaseUrl = storageOrigin,
            SigningKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            MaxObjectBytes = 1048576, DefaultLifetimeSeconds = 60, MaxLifetimeSeconds = 300,
        };
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddVivariumNodes(builder.Configuration, directory);
        using (var setup = builder.Build())
        {
            await setup.StartAsync();
            try
            {
                var settings = setup.Services.GetRequiredService<NodeConfigurationService>();
                var documents = new Dictionary<string, JsonObject>
                {
                    ["vivarium-host"] = new()
                    {
                        ["PublicOrigin"] = origin, ["ListenUrl"] = $"https://0.0.0.0:{publicPort}",
                        ["StorageListenUrl"] = storageOrigin, ["CertificatePath"] = certificate.Path,
                        ["OutputRetentionDays"] = 1,
                        ["DefinitionSources"] = JsonSerializer.SerializeToNode(new
                        { RepositoryId = "host-fixture", Path = definitions, SettingsRepositories = Array.Empty<object>() }),
                    },
                    ["vivarium-database"] = new() { ["ConnectionString"] = postgres.ConnectionString },
                    ["vivarium-identity"] = identity,
                    ["vivarium-storage"] = JsonSerializer.SerializeToNode(new
                    {
                        Backend = "Local", storage.Root, storage.PublicBaseUrl, storage.SigningKey,
                        storage.MaxObjectBytes, storage.DefaultLifetimeSeconds, storage.MaxLifetimeSeconds,
                    })!.AsObject(),
                };
                foreach (var (id, config) in documents)
                {
                    var saved = await settings.SaveAsync(id, Guid.NewGuid(), true, config, NodeConfigActor.System, null);
                    Assert.That(saved.StatusCode, Is.EqualTo(200), id + " initialization failed.");
                }
            }
            finally { await setup.StopAsync(); }
        }
        await using (var database = new VivariumDbContext(new DbContextOptionsBuilder<VivariumDbContext>()
            .UseNpgsql(postgres.ConnectionString).Options))
            await PostgresSchema.MigrateAsync(database);

        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false, CookieContainer = new CookieContainer(),
            ServerCertificateCustomValidationCallback = (_, remote, _, _) => remote is not null
                && remote.Thumbprint == certificate.Thumbprint,
        };
        using var http = new HttpClient(handler) { BaseAddress = new Uri(origin), Timeout = TimeSpan.FromSeconds(15) };
        using var scopedActor = new ScopedKeyScenario(new Uri(origin), certificate.Thumbprint, root, certificate.Path);
        string oldPassword;
        long? executedBuild = null;
        string? outputResumeCursor = null;
        var capture = new PrivateStartupLog();
        using var payloadPause = new BoundedPayloadPause();
        RuntimeBuildBytes? compilerBytes = null;
        await using (var app = VivariumRuntime.Build(directory, logs => logs.ClearProviders().AddProvider(capture), services =>
        {
            scopedActor.ConfigureServices(services);
            services.Replace(ServiceDescriptor.Singleton<RuntimeStepCompiler>(provider => new RuntimeStepCompiler(
                compilerBytes = new RuntimeBuildBytes(provider.GetRequiredService<IObjectStorage>(), payloadPause),
                provider.GetRequiredService<RuntimeBuildSecrets>())));
        }))
        {
            // This owned logger receives only the product's intended private disclosure. It never
            // writes the password, form, token or cookie into console/TRX/diagnostic artifacts.
            await app.StartAsync();
            try
            {
                Assert.That((await http.GetAsync("/health/ready")).StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That((await http.GetAsync("/identity.css")).StatusCode, Is.EqualTo(HttpStatusCode.OK),
                    "The explicit Host build must include the stylesheet linked by its account pages.");
                using (var anonymous = await http.GetAsync("/api/v1/users/current"))
                {
                    Assert.That(anonymous.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
                    Assert.That(anonymous.Headers.Location, Is.Null);
                    Assert.That(anonymous.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/problem+json"));
                    using var problem = JsonDocument.Parse(await anonymous.Content.ReadAsStringAsync());
                    Assert.That(problem.RootElement.GetProperty("code").GetString(), Is.EqualTo("authentication_required"));
                }
                foreach (var path in new[] { "/objects", "/objects/", "/OBJECTS" })
                    Assert.That((await http.GetAsync(path)).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
                Assert.That((await http.GetAsync(storageOrigin + "/health/live")).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
                oldPassword = capture.Password ?? throw new InvalidOperationException("Private startup disclosure absent.");
                var login = await LoginAsync(http, oldPassword);
                Assert.That(login.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
                using (var current = await http.GetAsync("/api/v1/users/current"))
                {
                    Assert.That(current.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                    using var account = JsonDocument.Parse(await current.Content.ReadAsStringAsync());
                    Assert.That(account.RootElement.GetProperty("teamCityId").GetInt64(), Is.Positive);
                }
                using (var roles = await http.GetAsync("/api/v1/roles"))
                {
                    Assert.That(roles.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                    using var roleDefinitions = JsonDocument.Parse(await roles.Content.ReadAsStringAsync());
                    Assert.That(roleDefinitions.RootElement.GetProperty("items").EnumerateArray()
                        .Select(role => role.GetProperty("id").GetString()), Is.EquivalentTo(new[]
                            { "SYSTEM_ADMIN", "PROJECT_ADMIN", "PROJECT_DEVELOPER", "PROJECT_VIEWER", "AGENT_MANAGER" }));
                }
                Assert.That((await http.GetAsync("/api/v1/projects")).StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
                var shell = await http.GetStringAsync("/access");
                var bootstrap = Regex.Match(shell, "<script id=\"vivarium-session\" type=\"application/json\">(.*?)</script>", RegexOptions.Singleline);
                using var session = JsonDocument.Parse(bootstrap.Groups[1].Value);
                using var reconcile = new HttpRequestMessage(HttpMethod.Post, "/api/v1/configuration-changes/reconcile");
                reconcile.Headers.Add("RequestVerificationToken", session.RootElement.GetProperty("requestVerificationToken").GetString());
                using var accepted = await http.SendAsync(reconcile);
                Assert.That(accepted.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
                var location = accepted.Headers.Location!;
                var until = DateTimeOffset.UtcNow.AddSeconds(60);
                var applied = false;
                while (DateTimeOffset.UtcNow < until)
                {
                    using var progress = await http.GetAsync(location);
                    Assert.That(progress.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                    using var change = JsonDocument.Parse(await progress.Content.ReadAsStringAsync());
                    var state = change.RootElement.GetProperty("state").GetString();
                    if (state == "applied") { applied = true; break; }
                    Assert.That(state, Is.EqualTo("pending").Or.EqualTo("evaluated"));
                    await Task.Delay(100);
                }
                Assert.That(applied, Is.True);
                using var catalog = JsonDocument.Parse(await http.GetStringAsync("/api/v1/projects/P/build-configurations"));
                Assert.That(catalog.RootElement.GetProperty("items")[0].GetProperty("id").GetString(), Is.EqualTo("P_LocalSmoke"));
                using (var noEnrollmentCsrf = await http.PostAsync("/api/v1/enrollment-tokens", content: null))
                    Assert.That(noEnrollmentCsrf.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
                using var enrollmentRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/enrollment-tokens");
                enrollmentRequest.Headers.Add("RequestVerificationToken", session.RootElement.GetProperty("requestVerificationToken").GetString());
                using var enrollmentResponse = await http.SendAsync(enrollmentRequest);
                Assert.That(enrollmentResponse.StatusCode, Is.EqualTo(HttpStatusCode.Created));
                using var enrollment = JsonDocument.Parse(await enrollmentResponse.Content.ReadAsStringAsync());
                using var publicCertificate = X509CertificateLoader.LoadPkcs12FromFile(certificate.Path, null,
                    X509KeyStorageFlags.EphemeralKeySet);
                var agentBootstrap = Path.Combine(root, "agent-bootstrap.json");
                await File.WriteAllTextAsync(agentBootstrap, JsonSerializer.Serialize(new
                {
                    controllerUrl = origin, certFingerprint = publicCertificate.GetCertHashString(HashAlgorithmName.SHA256),
                    enrollToken = enrollment.RootElement.GetProperty("token").GetString(),
                }));
                var agentData = Path.Combine(root, "agent-data");
                var agent = StartAgent(agentBootstrap, agentData);
                var agentOutput = agent.StandardOutput.ReadToEndAsync();
                var agentError = agent.StandardError.ReadToEndAsync();
                try
                {
                    var registration = await WaitAgentAsync(http, value => value.GetProperty("connected").GetBoolean());
                    var agentId = registration.GetProperty("id").GetString()!;
                    using var authorize = new HttpRequestMessage(HttpMethod.Put, "/api/v1/agents/" + agentId + "/authorization");
                    authorize.Headers.Add("RequestVerificationToken", session.RootElement.GetProperty("requestVerificationToken").GetString());
                    authorize.Content = new StringContent("{\"authorized\":true}", System.Text.Encoding.UTF8, "application/json");
                    using var authorization = await http.SendAsync(authorize);
                    Assert.That(authorization.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
                    var proven = await WaitAgentAsync(http, value => value.GetProperty("authorized").GetBoolean()
                        && value.GetProperty("reconciled").GetBoolean() && value.GetProperty("connected").GetBoolean());
                    Assert.That(proven.GetProperty("connectionGeneration").GetInt64(), Is.GreaterThan(1),
                        "Credential delivery must close the pending stream and prove the persisted bearer in a new Hello.");
                    Assert.That(proven.GetProperty("teamCityId").GetInt32(), Is.Positive);
                    Assert.That(proven.GetProperty("ready").GetBoolean(), Is.False,
                        "A real connection cannot invent the missing Git enablement projection.");
                    await File.WriteAllTextAsync(Path.Combine(definitions, ".vivarium/repository.yaml"),
                        DefinitionGitReader.Manifest("host-fixture", control: true, agents: true));
                    var agentDocument = Path.Combine(definitions, ".vivarium/agents", agentId + ".yaml");
                    Directory.CreateDirectory(Path.GetDirectoryName(agentDocument)!);
                    await File.WriteAllTextAsync(agentDocument, $"""
                        apiVersion: vivarium.io/v1alpha1
                        kind: Agent
                        id: {agentId}
                        spec:
                          enabled: true
                        """);
                    await SourceCompiler.GitAsync(definitions, "add", "--all");
                    await SourceCompiler.GitAsync(definitions, "commit", "-m", "fixture: enable the enrolled Agent through Git");
                    await ReconcileAsync(http, session.RootElement.GetProperty("requestVerificationToken").GetString()!);
                    var ready = await WaitAgentAsync(http, value => value.GetProperty("id").GetString() == agentId
                        && value.GetProperty("ready").GetBoolean());
                    Assert.That(ready.GetProperty("teamCityId").GetInt32(), Is.EqualTo(proven.GetProperty("teamCityId").GetInt32()));
                    Assert.That(ready.GetProperty("appliedConfigurationRevision").GetString(), Does.StartWith("host-fixture@"));
                    using var submit = new HttpRequestMessage(HttpMethod.Post, "/api/v1/builds");
                    submit.Headers.Add("RequestVerificationToken", session.RootElement.GetProperty("requestVerificationToken").GetString());
                    submit.Content = new StringContent("{\"configurationId\":\"P_LocalSmoke\"}", System.Text.Encoding.UTF8, "application/json");
                    using var queued = await http.SendAsync(submit);
                    Assert.That(queued.StatusCode, Is.EqualTo(HttpStatusCode.Accepted),
                        "The fresh entry must run an actual Git-owned BuildType rather than a submitted payload.");
                    using var admitted = JsonDocument.Parse(await queued.Content.ReadAsStringAsync());
                    executedBuild = admitted.RootElement.GetProperty("id").GetInt64();
                    Assert.That(executedBuild, Is.Positive);
                    var finished = await WaitBuildAsync(http, executedBuild.Value);
                    Assert.That(finished.GetProperty("status").GetString(), Is.EqualTo("SUCCESS"));
                    Assert.That(finished.GetProperty("configurationId").GetString(), Is.EqualTo("P_LocalSmoke"));
                    using var output = JsonDocument.Parse(await http.GetStringAsync($"/api/v1/builds/{executedBuild}/output"));
                    outputResumeCursor = output.RootElement.GetProperty("output").GetProperty("resumeCursor").GetString();
                    Assert.That(output.RootElement.GetProperty("output").GetProperty("state").GetString(), Is.EqualTo("complete"),
                        output.RootElement.GetRawText());
                    var stdout = new List<byte>();
                    foreach (var entry in output.RootElement.GetProperty("items").EnumerateArray())
                    {
                        Assert.That(entry.GetProperty("kind").GetString(), Is.EqualTo("data"));
                        if (entry.GetProperty("stream").GetString() != "stdout") continue;
                        var content = entry.GetProperty("content");
                        var persisted = await http.GetByteArrayAsync(content.GetProperty("href").GetString());
                        Assert.That(persisted.Length, Is.EqualTo(content.GetProperty("length").GetInt32()));
                        Assert.That(Convert.ToHexStringLower(SHA256.HashData(persisted)), Is.EqualTo(content.GetProperty("sha256").GetString()));
                        stdout.AddRange(persisted);
                    }
                    Assert.That(System.Text.Encoding.UTF8.GetString(stdout.ToArray()), Does.Contain("from CSharp Git"));
                    using var artifacts = JsonDocument.Parse(await http.GetStringAsync($"/api/v1/builds/{executedBuild}/artifacts"));
                    var artifact = artifacts.RootElement.GetProperty("items").EnumerateArray().Single();
                    Assert.That(artifact.GetProperty("path").GetString(), Is.EqualTo("proof.txt"));
                    var artifactBytes = await http.GetByteArrayAsync(artifact.GetProperty("href").GetString());
                    Assert.That(System.Text.Encoding.UTF8.GetString(artifactBytes).ReplaceLineEndings("\n"), Is.EqualTo("owned-smoke-artifact\n"));
                    Assert.That(Convert.ToHexStringLower(SHA256.HashData(artifactBytes)), Is.EqualTo(artifact.GetProperty("sha256").GetString()));
                    await WaitAgentAsync(http, value => value.GetProperty("id").GetString() == agentId && value.GetProperty("ready").GetBoolean());
                    await RuntimeResultReplayScenario.VerifyAsync(app.Services, executedBuild.Value);
                    using var resumed = await http.GetAsync($"/api/v1/builds/{executedBuild}/output?cursor="
                        + Uri.EscapeDataString(output.RootElement.GetProperty("output").GetProperty("resumeCursor").GetString()!));
                    Assert.That(resumed.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                    using var end = JsonDocument.Parse(await resumed.Content.ReadAsStringAsync());
                    Assert.That(end.RootElement.GetProperty("items").GetArrayLength(), Is.Zero);
                    using var repeatRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/builds");
                    repeatRequest.Headers.Add("RequestVerificationToken", session.RootElement.GetProperty("requestVerificationToken").GetString());
                    repeatRequest.Content = new StringContent("{\"configurationId\":\"P_LocalSmoke\"}", System.Text.Encoding.UTF8, "application/json");
                    using var repeatedResponse = await http.SendAsync(repeatRequest);
                    Assert.That(repeatedResponse.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
                    using var repeated = JsonDocument.Parse(await repeatedResponse.Content.ReadAsStringAsync());
                    var cancelledId = repeated.RootElement.GetProperty("id").GetInt64();
                    await payloadPause.Entered.WaitAsync(TimeSpan.FromSeconds(10));
                    using var cancellation = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/builds/{cancelledId}/cancellation");
                    cancellation.Headers.Add("RequestVerificationToken", session.RootElement.GetProperty("requestVerificationToken").GetString());
                    cancellation.Content = new StringContent("{\"reason\":\"Owned preparation cancellation\",\"mode\":\"force\"}",
                        System.Text.Encoding.UTF8, "application/json");
                    using var cancelledResponse = await http.SendAsync(cancellation);
                    Assert.That(cancelledResponse.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
                    using (var bound = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
                    {
                        var cancelled = await WaitBuildAsync(http, cancelledId, bound.Token);
                        Assert.That(cancelled.GetProperty("cancelReason").GetString(), Is.EqualTo("Owned preparation cancellation"));
                        Assert.That(cancelled.GetProperty("failedToStart").GetBoolean(), Is.False);
                    }
                    payloadPause.Release();
                    await payloadPause.Completed.WaitAsync(TimeSpan.FromSeconds(10));
                    await WaitAgentAsync(http, value => value.GetProperty("id").GetString() == agentId && value.GetProperty("ready").GetBoolean());
                    using var afterCancel = new HttpRequestMessage(HttpMethod.Post, "/api/v1/builds");
                    afterCancel.Headers.Add("RequestVerificationToken", session.RootElement.GetProperty("requestVerificationToken").GetString());
                    afterCancel.Content = new StringContent("{\"configurationId\":\"P_LocalSmoke\"}", System.Text.Encoding.UTF8, "application/json");
                    using var nextResponse = await http.SendAsync(afterCancel);
                    Assert.That(nextResponse.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
                    using var nextAdmitted = JsonDocument.Parse(await nextResponse.Content.ReadAsStringAsync());
                    var next = await WaitBuildAsync(http, nextAdmitted.RootElement.GetProperty("id").GetInt64());
                    Assert.That(next.GetProperty("status").GetString(), Is.EqualTo("SUCCESS"));
                    Assert.That(next.GetProperty("number").GetString(), Is.EqualTo("3"));
                    Assert.That(next.GetProperty("agentId").GetString(), Is.EqualTo(agentId));
                    using var wrongBuildCursor = await http.GetAsync($"/api/v1/builds/{next.GetProperty("id").GetInt64()}/output?cursor="
                        + Uri.EscapeDataString(outputResumeCursor!));
                    Assert.That(wrongBuildCursor.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
                    await WaitAgentAsync(http, value => value.GetProperty("id").GetString() == agentId && value.GetProperty("ready").GetBoolean());
                    await using var check = await app.Services.GetRequiredService<IDbContextFactory<VivariumDbContext>>().CreateDbContextAsync();
                    var cancelledExecution = await check.RuntimeBuildExecutions.AsNoTracking().SingleAsync(e => e.BuildId == cancelledId);
                    Assert.That(cancelledExecution.SentAt, Is.Null, "A late payload response cannot revive a cancelled preparation.");
                    Assert.That(await check.RuntimeBuildResults.AnyAsync(r => r.BuildId == cancelledId), Is.False);
                    var journal = await check.AuditEvents.AsNoTracking().Where(a => a.TargetType == "build" && a.TargetId == executedBuild.Value.ToString())
                        .ToArrayAsync();
                    Assert.That(journal.Select(a => a.Action), Is.EquivalentTo(new[] { "build.submit", "build.assignment.prepared",
                        "build.assignment.dispatching", "build.assignment.accepted", "build.result.accepted", "build.output.sealed", "build.lease.released" }));
                    Assert.That(journal.Select(a => a.CorrelationId).Distinct().ToArray(), Has.Length.EqualTo(1));
                    Assert.That(journal.All(a => a.ResultRevision is { Length: 40 }), Is.True);
                    await scopedActor.RunAsync(app.Services, http,
                        session.RootElement.GetProperty("requestVerificationToken").GetString()!, agentId);
                }
                catch
                {
                    await using var diagnostic = await app.Services.GetRequiredService<IDbContextFactory<VivariumDbContext>>().CreateDbContextAsync();
                    var lifecycle = await (from build in diagnostic.RuntimeBuilds.AsNoTracking()
                        join owner in diagnostic.RuntimeBuildExecutions.AsNoTracking() on build.Id equals owner.BuildId into owners
                        from owner in owners.DefaultIfEmpty()
                        select new { build.Id, build.State, build.Status, build.FailedToStart,
                            Phase = owner == null ? null : owner.Phase, AcceptedAt = owner == null ? null : owner.AcceptedAt,
                            TerminalAt = owner == null ? null : owner.TerminalReceivedAt }).ToArrayAsync();
                    var capacity = await diagnostic.AgentRegistrations.AsNoTracking().Select(a => new
                        { a.CurrentSessionId, a.Reconciled, a.Authorized, a.Health, a.HealthReason, a.Quarantined, a.CurrentWorkloadId }).ToArrayAsync();
                    TestContext.Progress.WriteLine(JsonSerializer.Serialize(new { lifecycle, capacity }));
                    throw;
                }
                finally
                {
                    if (!agent.HasExited) agent.Kill(entireProcessTree: true);
                    await agent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    await Task.WhenAll(agentOutput, agentError).WaitAsync(TimeSpan.FromSeconds(10));
                    agent.Dispose();
                }
                var installationRoles = "/api/v1/users/53a74e90-6a7c-4965-a530-8d498c4e523e/roles/SYSTEM_ADMIN/g";
                using (var noCsrf = await http.PutAsync(installationRoles, content: null))
                    Assert.That(noCsrf.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest),
                        "A cookie cannot mutate access without normal request verification.");
                using (var explicitCredential = new HttpRequestMessage(HttpMethod.Get, "/api/v1/users/current"))
                {
                    explicitCredential.Headers.Authorization = new("Bearer", "owned-invalid");
                    using var mixed = await http.SendAsync(explicitCredential);
                    Assert.That(mixed.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized),
                        "An invalid explicit credential cannot inherit the installation cookie.");
                }
                using (var explicitCredential = new HttpRequestMessage(HttpMethod.Put, installationRoles))
                {
                    explicitCredential.Headers.Authorization = new("Bearer", "owned-invalid");
                    using var denied = await http.SendAsync(explicitCredential);
                    Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
                }
                await using (var state = new VivariumDbContext(new DbContextOptionsBuilder<VivariumDbContext>()
                    .UseNpgsql(postgres.ConnectionString).Options))
                {
                    Assert.That(await state.AuditEvents.CountAsync(entry => entry.Action == "authentication.authorize_cookie"
                        && entry.ActorId == "53a74e90-6a7c-4965-a530-8d498c4e523e"),
                        Is.EqualTo(1));
                    await state.Database.ExecuteSqlRawAsync("""
                        CREATE FUNCTION public.owned_reject_signin_audit() RETURNS trigger LANGUAGE plpgsql AS $$
                        BEGIN RAISE EXCEPTION 'Owned audit sink fault' USING ERRCODE = '23514'; END $$;
                        CREATE TRIGGER "Owned_AuditSinkFault" BEFORE INSERT ON "AuditEvents"
                            FOR EACH ROW EXECUTE FUNCTION public.owned_reject_signin_audit();
                        """);
                    try
                    {
                        using var freshHandler = new HttpClientHandler
                        {
                            AllowAutoRedirect = false, CookieContainer = new CookieContainer(),
                            ServerCertificateCustomValidationCallback = (_, remote, _, _) => remote is not null
                                && remote.Thumbprint == certificate.Thumbprint,
                        };
                        using var fresh = new HttpClient(freshHandler) { BaseAddress = new Uri(origin), Timeout = TimeSpan.FromSeconds(15) };
                        using var rejected = await LoginAsync(fresh, oldPassword);
                        Assert.That(rejected.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
                        Assert.That(freshHandler.CookieContainer.GetCookies(new Uri(origin)).Cast<Cookie>()
                            .Any(cookie => cookie.Name.Contains("Identity.Application", StringComparison.Ordinal)), Is.False);
                        Assert.That(rejected.Headers.TryGetValues("Set-Cookie", out var cookies)
                            && cookies.Any(value => value.Contains("Identity.Application", StringComparison.Ordinal)), Is.False);
                        Assert.That(await state.AuditEvents.CountAsync(entry => entry.Action == "authentication.authorize_cookie"
                            && entry.ActorId == "53a74e90-6a7c-4965-a530-8d498c4e523e"),
                            Is.EqualTo(1));
                    }
                    finally
                    {
                        await state.Database.ExecuteSqlRawAsync("""
                            DROP TRIGGER "Owned_AuditSinkFault" ON "AuditEvents";
                            DROP FUNCTION public.owned_reject_signin_audit();
                            """);
                    }
                }
                Assert.That((await http.GetAsync("/Identity/Account/Manage/Tokens")).StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
                Assert.That((await http.GetAsync("/Identity/Account/Login?ReturnUrl=https://example.invalid/")).StatusCode,
                    Is.EqualTo(HttpStatusCode.BadRequest));
                var unsigned = await http.PostAsync("/Identity/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
                    { ["login"] = "superuser", ["token"] = oldPassword, ["returnUrl"] = "/" }));
                Assert.That(unsigned.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
                var objects = app.Services.GetRequiredService<IObjectStorage>();
                var upload = await objects.UploadAsync(new GetUploadUrl("owned-r0/object", "application/octet-stream"));
                using var bytes = new ByteArrayContent("owned-r0-object\n"u8.ToArray());
                bytes.Headers.ContentType = new("application/octet-stream");
                using var uploaded = await http.PutAsync(upload.Url, bytes);
                Assert.That(uploaded.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
            }
            finally { payloadPause.Release(); await app.StopAsync(); compilerBytes?.Dispose(); }
        }
        var restartedCapture = new PrivateStartupLog();
        System.Diagnostics.Process? reconnected = null;
        Task<string>? reconnectOutput = null;
        Task<string>? reconnectError = null;
        try
        {
            await using (var app = VivariumRuntime.Build(directory, logs => logs.ClearProviders().AddProvider(restartedCapture), scopedActor.ConfigureServices))
            {
                await app.StartAsync();
                try
                {
                    Assert.That(restartedCapture.Password is not null && restartedCapture.Password != oldPassword, Is.True);
                    Assert.That((await LoginAsync(http, oldPassword)).StatusCode, Is.EqualTo(HttpStatusCode.OK), "Old password must fail normal login.");
                    Assert.That((await LoginAsync(http, restartedCapture.Password!)).StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
                    Assert.That((await http.GetAsync("/api/v1/build-configurations/P_LocalSmoke")).StatusCode, Is.EqualTo(HttpStatusCode.OK));
                    Assert.That(executedBuild, Is.Not.Null);
                    using var retainedBuild = JsonDocument.Parse(await http.GetStringAsync($"/api/v1/builds/{executedBuild}"));
                    using var retainedOutput = JsonDocument.Parse(await http.GetStringAsync($"/api/v1/builds/{executedBuild}/output"));
                    using var coldResume = await http.GetAsync($"/api/v1/builds/{executedBuild}/output?cursor=" + Uri.EscapeDataString(outputResumeCursor!));
                    Assert.That(coldResume.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                    using var coldEnd = JsonDocument.Parse(await coldResume.Content.ReadAsStringAsync());
                    Assert.That(coldEnd.RootElement.GetProperty("items").GetArrayLength(), Is.Zero);
                    var retainedStdout = new List<byte>();
                    foreach (var entry in retainedOutput.RootElement.GetProperty("items").EnumerateArray())
                        if (entry.GetProperty("kind").GetString() == "data" && entry.GetProperty("stream").GetString() == "stdout")
                            retainedStdout.AddRange(await http.GetByteArrayAsync(entry.GetProperty("content").GetProperty("href").GetString()));
                    Assert.That(System.Text.Encoding.UTF8.GetString(retainedStdout.ToArray()), Does.Contain("from CSharp Git"));
                    using var retainedArtifacts = JsonDocument.Parse(await http.GetStringAsync($"/api/v1/builds/{executedBuild}/artifacts"));
                    var retainedArtifact = retainedArtifacts.RootElement.GetProperty("items").EnumerateArray().Single();
                    var retainedArtifactBytes = await http.GetByteArrayAsync(retainedArtifact.GetProperty("href").GetString());
                    Assert.That(System.Text.Encoding.UTF8.GetString(retainedArtifactBytes).ReplaceLineEndings("\n"), Is.EqualTo("owned-smoke-artifact\n"));
                    await scopedActor.AfterRestartAndRevokeAsync(app.Services, http);
                    Assert.That(retainedBuild.RootElement.GetProperty("state").GetString(), Is.EqualTo("finished"));
                    Assert.That(retainedBuild.RootElement.GetProperty("status").GetString(), Is.EqualTo("SUCCESS"));
                    using var priorAgents = JsonDocument.Parse(await http.GetStringAsync("/api/v1/agents"));
                    var oldAgent = priorAgents.RootElement.GetProperty("items")[0];
                    var oldAgentId = oldAgent.GetProperty("id").GetString();
                    var oldGeneration = oldAgent.GetProperty("connectionGeneration").GetInt64();
                    reconnected = StartAgent(Path.Combine(root, "agent-bootstrap.json"), Path.Combine(root, "agent-data"));
                    reconnectOutput = reconnected.StandardOutput.ReadToEndAsync();
                    reconnectError = reconnected.StandardError.ReadToEndAsync();
                    var currentAgent = await WaitAgentAsync(http, value => value.GetProperty("connected").GetBoolean()
                        && value.GetProperty("reconciled").GetBoolean());
                    Assert.That(currentAgent.GetProperty("id").GetString(), Is.EqualTo(oldAgentId));
                    Assert.That(currentAgent.GetProperty("connectionGeneration").GetInt64(), Is.GreaterThan(oldGeneration));
                    Assert.That(currentAgent.GetProperty("authorized").GetBoolean(), Is.True);
                    var objects = app.Services.GetRequiredService<IObjectStorage>();
                    var download = await objects.DownloadAsync(new GetDownloadUrl("owned-r0/object", "owned.bin"));
                    using var retained = await http.GetAsync(download.Url);
                    Assert.That(retained.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                    Assert.That(await retained.Content.ReadAsByteArrayAsync(), Is.EqualTo("owned-r0-object\n"u8.ToArray()));
                    await objects.DeleteAsync("owned-r0/object", CancellationToken.None);
                    Assert.That((await http.GetAsync(download.Url)).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
                    scopedActor.ResetRetentionTime();
                    await scopedActor.CheckRunSecretAdmissionAsync(http);
                    await secretScenario.BeforeRestartAsync(scopedActor.BuildClient, app.Services);
                    await RuntimeResultReplayScenario.VerifyAsync(app.Services, executedBuild!.Value);
                }
                finally { await app.StopAsync(); }
            }
            var recoveryErrors = new FailureTypesLog();
            await using (var app = VivariumRuntime.Build(directory, logs => logs.ClearProviders().AddProvider(recoveryErrors), scopedActor.ConfigureServices))
            {
                await app.StartAsync();
                try
                {
                    await scopedActor.AfterRetentionRestartAsync(() => recoveryErrors.Failures);
                    await secretScenario.AfterRestartAsync(scopedActor.BuildClient, app.Services);
                    await RuntimeResultReplayScenario.RejectConflictAsync(app.Services, executedBuild!.Value);
                    if (!reconnected!.HasExited) reconnected.Kill(entireProcessTree: true);
                    await reconnected.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                }
                finally { await app.StopAsync(); }
            }
        }
        finally
        {
            if (reconnected is not null)
            {
                if (!reconnected.HasExited) reconnected.Kill(entireProcessTree: true);
                await reconnected.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                await Task.WhenAll(reconnectOutput!, reconnectError!).WaitAsync(TimeSpan.FromSeconds(10));
                secretScenario.AssertPrivate(reconnectOutput!.Result); secretScenario.AssertPrivate(reconnectError!.Result);
                reconnected.Dispose();
            }
            // Scan only after Host/Agent release their ownership locks and mutable spool handles.
            secretScenario.AssertOwnedFilesPrivate();
        }
    }

    private sealed class FailureTypesLog : ILoggerProvider, ILogger
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> failures = new();
        internal string Failures => string.Join("; ", failures);
        public ILogger CreateLogger(string categoryName) => categoryName == "Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware" ? this : NullLogger.Instance;
        public bool IsEnabled(LogLevel level) => level >= LogLevel.Error;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Dispose() { }
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            // Never format arbitrary framework messages, URLs or credential-bearing requests.
            for (var current = exception; current is not null; current = current.InnerException)
                failures.Enqueue(current.GetType().Name + ":" + string.Join(",", Regex.Matches(current.Message, @"\b(?:ID|IDX)[0-9]{4,8}\b").Select(m => m.Value))
                    + ":" + new System.Diagnostics.StackTrace(current).GetFrame(0)?.GetMethod()?.Name);
        }
    }

    private static async Task<HttpResponseMessage> LoginAsync(HttpClient http, string password)
    {
        using var page = await http.GetAsync("/Identity/Account/Login");
        Assert.That(page.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var html = await page.Content.ReadAsStringAsync();
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        if (!match.Success) throw new InvalidDataException("Normal login antiforgery input missing.");
        return await http.PostAsync("/Identity/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["login"] = "superuser", ["token"] = password, ["returnUrl"] = "/",
            ["__RequestVerificationToken"] = WebUtility.HtmlDecode(match.Groups[1].Value),
        }));
    }

    private static int Port()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static (string Path, string Thumbprint) Certificate(string root, string name, bool tls)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=owned-r0-" + name, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        if (tls)
        {
            var names = new SubjectAlternativeNameBuilder();
            names.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(names.Build());
        }
        // Credential-expiry scenarios advance validation by two days while transport/signing keys remain valid.
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(30));
        var path = Path.Combine(root, name + ".pfx");
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx));
        return (path, certificate.Thumbprint);
    }

    private static System.Diagnostics.Process StartAgent(string bootstrap, string data)
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Vivarium.slnx"))) directory = directory.Parent;
        var root = directory?.FullName ?? throw new InvalidOperationException("Repository root is absent.");
        var dotnet = Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(),
            "../../..", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
        return ProofProcess.Start(dotnet, [Path.Combine(root, "src/Vivarium.Agent/bin/Debug/net10.0/Vivarium.Agent.dll"),
            "--config", bootstrap, "--data", data], root);
    }

    private static async Task<JsonElement> WaitAgentAsync(HttpClient http, Func<JsonElement, bool> condition)
    {
        var until = DateTimeOffset.UtcNow.AddSeconds(45);
        while (DateTimeOffset.UtcNow < until)
        {
            using var response = await http.GetAsync("/api/v1/agents");
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            foreach (var value in body.RootElement.GetProperty("items").EnumerateArray())
                if (condition(value)) return value.Clone();
            await Task.Delay(100);
        }
        throw new AssertionException("The owned Agent did not reach the required public state.");
    }

    private static async Task ReconcileAsync(HttpClient http, string csrf)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/configuration-changes/reconcile");
        request.Headers.Add("RequestVerificationToken", csrf);
        using var response = await http.SendAsync(request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        var until = DateTimeOffset.UtcNow.AddSeconds(60);
        while (DateTimeOffset.UtcNow < until)
        {
            using var status = await http.GetAsync(response.Headers.Location);
            Assert.That(status.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            using var change = JsonDocument.Parse(await status.Content.ReadAsStringAsync());
            var state = change.RootElement.GetProperty("state").GetString();
            if (state == "applied") return;
            Assert.That(state, Is.EqualTo("pending").Or.EqualTo("evaluated"));
            await Task.Delay(100);
        }
        throw new AssertionException("The owned Git policy did not activate.");
    }

    private static async Task<JsonElement> WaitBuildAsync(HttpClient http, long id, CancellationToken token = default)
    {
        var until = DateTimeOffset.UtcNow.AddSeconds(45);
        while (DateTimeOffset.UtcNow < until)
        {
            using var response = await http.GetAsync($"/api/v1/builds/{id}", token);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            using var build = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (build.RootElement.GetProperty("state").GetString() == "finished") return build.RootElement.Clone();
            await Task.Delay(100, token);
        }
        throw new AssertionException("The owned catalogue Build did not finish.");
    }

    private sealed class PrivateStartupLog : ILoggerProvider, ILogger
    {
        public string? Password { get; private set; }
        public ILogger CreateLogger(string categoryName) => categoryName == "Vivarium.PrivateStartup" ? this : NullLogger.Instance;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (state is IEnumerable<KeyValuePair<string, object?>> fields)
                Password = fields.FirstOrDefault(field => field.Key == "SuperuserPassword").Value as string;
        }
        public void Dispose() => Password = null;
    }
}
