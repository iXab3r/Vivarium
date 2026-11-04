using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vivarium.Controller.Agents;
using Vivarium.Controller.Catalog;
using Vivarium.Controller.Persistence;
using Vivarium.Controller.Builds.Runtime;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Extensions.Logging;

namespace Vivarium.DefinitionProof;

public sealed partial class R0AcceptanceCases
{
    [TestCase(TestName = "TCREST-R0-PROTECTED-SIBLING-BYTES-DENIED-001")]
    public async Task Protected_sibling_matches_every_frozen_exchange_with_existing_bytes_and_unchanged_ordinary_actor()
    {
        using var corpus = await ReferenceCorpusAsync(); var metadata = corpus.RootElement.GetProperty("metadata");
        var clock = new FakeTimeProvider(ReferenceDate("20261102T124114+0000"));
        var wire = new R0AgentMessageGate { Enabled = false };
        await using var host = await installation.CreateCaseAsync(configureServices: services =>
        {
            ConfigureReferenceTime(services, clock); services.AddSingleton(wire);
            services.AddGrpc(options => options.Interceptors.Add<R0AgentMessageGate>());
        });
        try
        {
            using var administrator = await host.AdministratorAsync(); var csrf = await host.AntiforgeryAsync(administrator);
            await PrepareReferenceNamesAsync(host, administrator, csrf); await host.StartWindowsAgentAsync(administrator, csrf);
            using var actor = new ScopedKeyScenario(host.Origin, host.Thumbprint, host.Root, host.Certificate);
            await actor.InitializeUserAsync(host.Services, metadata.GetProperty("username").GetString()!, "Owned R0 scoped run actor");
            await R0AcceptanceInstallation.Case.MutateAsync(administrator, csrf, HttpMethod.Put,
                $"/api/v1/users/{actor.UserId}/roles/PROJECT_DEVELOPER/projects/P", null, HttpStatusCode.NoContent);
            await actor.CreateKeyAsync();
            using var user = JsonDocument.Parse(await actor.BuildClient.GetStringAsync("/api/v1/users/current"));
            var agent = await AgentAsync(administrator, host);
            var bindings = new R0ReferenceBindings(metadata, host.Origin, user.RootElement.GetProperty("teamCityId").GetInt64(),
                host.TeamCityAgentId!.Value, agent.GetProperty("name").GetString()!);
            using var sourceAdministrator = new ScopedKeyScenario(host.Origin, host.Thumbprint, host.Root, host.Certificate);
            await sourceAdministrator.InitializeUserAsync(host.Services, "vivarium-lab-admin", displayName: null);
            await R0AcceptanceInstallation.Case.MutateAsync(administrator, csrf, HttpMethod.Put,
                $"/api/v1/users/{sourceAdministrator.UserId}/roles/SYSTEM_ADMIN/g", null, HttpStatusCode.NoContent);
            await sourceAdministrator.CreateKeyAsync();
            using var adminUser = JsonDocument.Parse(await sourceAdministrator.BuildClient.GetStringAsync("/api/v1/users/current"));
            bindings.Users.Add(1, adminUser.RootElement.GetProperty("teamCityId").GetInt64());
            var coordinator = host.Services.GetRequiredService<RuntimeBuildCoordinator>();
            var positive = await SubmitAsync(actor.BuildClient, "P_LocalSmoke");
            await WaitAsync(async () =>
            {
                await coordinator.TickAsync(CancellationToken.None);
                return (await ReadBuildAsync(actor.BuildClient, positive)).GetProperty("state").GetString() == "finished";
            });
            Assert.That(await actor.BuildClient.GetByteArrayAsync($"/app/rest/builds/id:{positive}/artifacts/files/smoke.txt"), Is.EqualTo("owned-r0-smoke\n"u8.ToArray()));
            await WaitAsync(async () => (await AgentAsync(administrator, host)).GetProperty("ready").GetBoolean());
            bindings.Builds.Add(metadata.GetProperty("allowedBuildId").GetInt64(), positive);
            await using (var scope = host.Services.CreateAsyncScope())
            {
                var database = scope.ServiceProvider.GetRequiredService<VivariumDbContext>();
                bindings.InternalProjects.Add("64", (await database.CatalogProjects.SingleAsync(p => p.ExternalId == "Q")).Id);
            }
            wire.Enabled = true;
            var source = corpus.RootElement.GetProperty("cases").EnumerateArray().Single(item => item.GetProperty("id").GetString()
                == "TCREST-R0-PROTECTED-SIBLING-BYTES-DENIED-001");
            var exchanges = source.GetProperty("exchanges"); Assert.That(exchanges.GetArrayLength(), Is.EqualTo(33));
            var index = 0; var countBeforeDeniedAdmission = 0;
            foreach (var exchange in exchanges.EnumerateArray())
            {
                if (index == 2)
                {
                    clock.SetUtcNow(ReferenceDate("20261102T124115+0000"));
                    await coordinator.TickAsync(CancellationToken.None);
                    await wire.Acceptance.Arrived.WaitAsync(TimeSpan.FromSeconds(20)); wire.Acceptance.Release();
                    await wire.Step.Arrived.WaitAsync(TimeSpan.FromSeconds(20));
                }
                if (index == 24)
                {
                    clock.SetUtcNow(ReferenceDate("20261102T124125+0000")); wire.Step.Release(); wire.Collection.Release(); wire.Result.Release();
                    await WaitAsync(async () =>
                    {
                        await coordinator.TickAsync(CancellationToken.None);
                        return (await ReadBuildAsync(administrator, bindings.Builds[metadata.GetProperty("protectedBuildId").GetInt64()])).GetProperty("state").GetString() == "finished";
                    });
                }
                if (index == 27)
                {
                    await using var scope = host.Services.CreateAsyncScope();
                    countBeforeDeniedAdmission = await scope.ServiceProvider.GetRequiredService<VivariumDbContext>().RuntimeBuilds.CountAsync();
                }
                await AssertReferenceExchangeAsync(exchange, bindings, actor.BuildClient, sourceAdministrator.BuildClient, csrf);
                if (index == 27)
                {
                    await using var scope = host.Services.CreateAsyncScope();
                    Assert.That(await scope.ServiceProvider.GetRequiredService<VivariumDbContext>().RuntimeBuilds.CountAsync(), Is.EqualTo(countBeforeDeniedAdmission));
                }
                index++;
            }
        }
        finally { wire.Acceptance.Release(); wire.Step.Release(); wire.Collection.Release(); wire.Result.Release(); }
    }

    [TestCase(TestName = "TCREST-R0-SCOPED-RUN-ARTIFACT-001")]
    public async Task Scoped_success_matches_every_frozen_exchange_and_downloads_the_real_process_artifact()
    {
        using var corpus = await ReferenceCorpusAsync(); var metadata = corpus.RootElement.GetProperty("metadata");
        var clock = new FakeTimeProvider(ReferenceDate("20261102T124101+0000")); var wire = new R0AgentMessageGate();
        await using var host = await installation.CreateCaseAsync(configureServices: services =>
        {
            ConfigureReferenceTime(services, clock); services.AddSingleton(wire);
            services.AddGrpc(options => options.Interceptors.Add<R0AgentMessageGate>());
        });
        try
        {
            using var administrator = await host.AdministratorAsync(); var csrf = await host.AntiforgeryAsync(administrator);
            // Independently prepare the recorded file's bytes and source modification time in
            // the actual producing process. Storage/public metadata do not supply this oracle.
            var settings = Path.Combine(host.Definitions, ".vivarium/unbound/P/Settings.g.cs");
            var text = await File.ReadAllTextAsync(settings);
            const string marker = "[IO.File]::WriteAllText('smoke.txt',\"owned-r0-smoke`n\",[Text.UTF8Encoding]::new($false))";
            text = RewriteEncodedPowerShell(text, script => script.Contains(marker, StringComparison.Ordinal)
                ? script.Replace(marker, marker + "\n[IO.File]::SetLastWriteTimeUtc('smoke.txt',[DateTime]::new(2026,11,2,12,41,4,[DateTimeKind]::Utc))", StringComparison.Ordinal) : script);
            await File.WriteAllTextAsync(settings, text);
            await PrepareReferenceNamesAsync(host, administrator, csrf);
            await host.StartWindowsAgentAsync(administrator, csrf);
            using var actor = new ScopedKeyScenario(host.Origin, host.Thumbprint, host.Root, host.Certificate);
            await actor.InitializeUserAsync(host.Services, metadata.GetProperty("username").GetString()!, "Owned R0 scoped run actor");
            await R0AcceptanceInstallation.Case.MutateAsync(administrator, csrf, HttpMethod.Put,
                $"/api/v1/users/{actor.UserId}/roles/PROJECT_DEVELOPER/projects/P", null, HttpStatusCode.NoContent);
            await actor.CreateKeyAsync();
            using var user = JsonDocument.Parse(await actor.BuildClient.GetStringAsync("/api/v1/users/current"));
            var agent = await AgentAsync(administrator, host);
            var bindings = new R0ReferenceBindings(metadata, host.Origin, user.RootElement.GetProperty("teamCityId").GetInt64(),
                host.TeamCityAgentId!.Value, agent.GetProperty("name").GetString()!);
            var source = corpus.RootElement.GetProperty("cases").EnumerateArray().Single(item => item.GetProperty("id").GetString()
                == "TCREST-R0-SCOPED-RUN-ARTIFACT-001");
            var exchanges = source.GetProperty("exchanges"); Assert.That(exchanges.GetArrayLength(), Is.EqualTo(33));
            var coordinator = host.Services.GetRequiredService<RuntimeBuildCoordinator>();
            var index = 0;
            foreach (var exchange in exchanges.EnumerateArray())
            {
                if (index == 4)
                {
                    await coordinator.TickAsync(CancellationToken.None);
                    await wire.Acceptance.Arrived.WaitAsync(TimeSpan.FromSeconds(20));
                }
                if (index == 5)
                {
                    clock.SetUtcNow(ReferenceDate("20261102T124102+0000")); wire.Acceptance.Release();
                    await wire.Step.Arrived.WaitAsync(TimeSpan.FromSeconds(20));
                }
                if (index == 9)
                {
                    wire.Step.Release();
                    await wire.Collection.Arrived.WaitAsync(TimeSpan.FromSeconds(20));
                }
                if (index == 13)
                {
                    wire.Collection.Release();
                    await wire.Result.Arrived.WaitAsync(TimeSpan.FromSeconds(20));
                }
                if (index == 28)
                {
                    clock.SetUtcNow(ReferenceDate("20261102T124114+0000")); wire.Result.Release();
                    await WaitAsync(async () =>
                    {
                        await coordinator.TickAsync(CancellationToken.None);
                        return (await ReadBuildAsync(actor.BuildClient, bindings.Builds.Values.Single())).GetProperty("state").GetString() == "finished";
                    });
                }
                await AssertReferenceExchangeAsync(exchange, bindings, actor.BuildClient, administrator, csrf);
                index++;
            }
        }
        finally { wire.Acceptance.Release(); wire.Step.Release(); wire.Collection.Release(); wire.Result.Release(); }
    }

    private static string RewriteEncodedPowerShell(string text, Func<string, string> rewrite)
        => System.Text.RegularExpressions.Regex.Replace(text, "(?<=-EncodedCommand )[A-Za-z0-9+/=]+", match =>
            Convert.ToBase64String(Encoding.Unicode.GetBytes(rewrite(Encoding.Unicode.GetString(Convert.FromBase64String(match.Value))))));

    private static async Task AssertReferenceExchangeAsync(JsonElement exchange, R0ReferenceBindings bindings,
        HttpClient actor, HttpClient administrator, string csrf)
    {
        var recorded = exchange.GetProperty("request"); var expected = exchange.GetProperty("response");
        var originalPath = recorded.GetProperty("path").GetString()!;
        var path = bindings.Path(originalPath) + "?" + string.Join('&', recorded.GetProperty("query").EnumerateObject()
            .Select(p => Uri.EscapeDataString(p.Name) + "=" + Uri.EscapeDataString(p.Value.GetString()!)));
        using var request = new HttpRequestMessage(new HttpMethod(recorded.GetProperty("method").GetString()!), path);
        request.Headers.Accept.ParseAdd(recorded.GetProperty("headers").GetProperty("accept").GetString()!);
        var body = Convert.FromBase64String(recorded.GetProperty("bodyBase64").GetString()!);
        if (body.Length > 0)
        {
            request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(bindings.Bind(JsonNode.Parse(body)!).ToJsonString()));
            request.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(recorded.GetProperty("headers").GetProperty("content-type").GetString()!);
        }
        var client = exchange.GetProperty("actor").GetString() switch
        { "owned-scoped-pat" => actor, "admin" => administrator, _ => throw new InvalidDataException("Unknown source actor.") };
        if (client == administrator && request.Method != HttpMethod.Get) request.Headers.Add("RequestVerificationToken", csrf);
        using var response = await client.SendAsync(request);
        Assert.That((int)response.StatusCode, Is.EqualTo(expected.GetProperty("status").GetInt32()), originalPath);
        var media = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(expected.GetProperty("headers").GetProperty("content-type").GetString()!).MediaType;
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo(media), originalPath);
        var actualBytes = await response.Content.ReadAsByteArrayAsync();
        var expectedBytes = Convert.FromBase64String(expected.GetProperty("bodyBase64").GetString()!);
        if (media != "application/json")
        {
            if (media == "application/xml") expectedBytes = Encoding.UTF8.GetBytes(bindings.Path(Encoding.UTF8.GetString(expectedBytes)));
            Assert.That(actualBytes, Is.EqualTo(expectedBytes), originalPath); return;
        }
        var actual = JsonNode.Parse(actualBytes)!; var source = JsonNode.Parse(expectedBytes)!;
        if (request.Method == HttpMethod.Post && originalPath == "/app/rest/buildQueue" && response.IsSuccessStatusCode)
            bindings.Builds.Add(source["id"]!.GetValue<long>(), actual["id"]!.GetValue<long>());
        var required = bindings.Bind(source, originalPath == "/app/rest/users/current" ? "user" : "build");
        Assert.That(JsonNode.DeepEquals(actual, required), Is.True,
            $"Complete {request.Method} {originalPath} JSON differs. Expected {required}; actual {actual}");
    }

    [TestCase(TestName = "TCREST-R0-SCOPED-QUEUED-CANCEL-001")]
    public async Task Queued_cancellation_matches_the_complete_frozen_workflow_without_assignment_or_counter_consumption()
    {
        using var corpus = await ReferenceCorpusAsync(); var metadata = corpus.RootElement.GetProperty("metadata");
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 2, 12, 41, 1, TimeSpan.Zero));
        await using var host = await installation.CreateCaseAsync(configureServices: services => ConfigureReferenceTime(services, clock));
        using var administrator = await host.AdministratorAsync(); var csrf = await host.AntiforgeryAsync(administrator);
        await PrepareReferenceNamesAsync(host, administrator, csrf);
        await host.StartWindowsAgentAsync(administrator, csrf);
        using var actor = new ScopedKeyScenario(host.Origin, host.Thumbprint, host.Root, host.Certificate);
        await actor.InitializeUserAsync(host.Services, metadata.GetProperty("username").GetString()!, "Owned R0 scoped run actor");
        await R0AcceptanceInstallation.Case.MutateAsync(administrator, csrf, HttpMethod.Put,
            $"/api/v1/users/{actor.UserId}/roles/PROJECT_DEVELOPER/projects/P", null, HttpStatusCode.NoContent);
        await actor.CreateKeyAsync();
        using var nativeUser = JsonDocument.Parse(await actor.BuildClient.GetStringAsync("/api/v1/users/current"));
        var agent = await AgentAsync(administrator, host);
        var bindings = new R0ReferenceBindings(metadata, host.Origin, nativeUser.RootElement.GetProperty("teamCityId").GetInt64(),
            host.TeamCityAgentId!.Value, agent.GetProperty("name").GetString()!);
        var source = corpus.RootElement.GetProperty("cases").EnumerateArray().Single(item => item.GetProperty("id").GetString()
            == "TCREST-R0-SCOPED-QUEUED-CANCEL-001");
        var exchanges = source.GetProperty("exchanges"); Assert.That(exchanges.GetArrayLength(), Is.EqualTo(7));
        foreach (var exchange in exchanges.EnumerateArray())
        {
            var recorded = exchange.GetProperty("request"); var expected = exchange.GetProperty("response");
            var expectedJson = JsonNode.Parse(Convert.FromBase64String(expected.GetProperty("bodyBase64").GetString()!))!;
            var method = recorded.GetProperty("method").GetString()!;
            if (method == "PUT") clock.SetUtcNow(ReferenceDate(expectedJson["comment"]!["timestamp"]!.GetValue<string>()));
            else if (method == "POST") clock.SetUtcNow(ReferenceDate(expectedJson["canceledInfo"]?["timestamp"]?.GetValue<string>()
                ?? expectedJson["queuedDate"]!.GetValue<string>()));
            var path = bindings.Path(recorded.GetProperty("path").GetString()!);
            path += "?" + string.Join('&', recorded.GetProperty("query").EnumerateObject()
                .Select(p => Uri.EscapeDataString(p.Name) + "=" + Uri.EscapeDataString(p.Value.GetString()!)));
            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            request.Headers.Accept.ParseAdd(recorded.GetProperty("headers").GetProperty("accept").GetString()!);
            var body = Convert.FromBase64String(recorded.GetProperty("bodyBase64").GetString()!);
            if (body.Length > 0)
            {
                request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(bindings.Bind(JsonNode.Parse(body)!).ToJsonString()));
                request.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(
                    recorded.GetProperty("headers").GetProperty("content-type").GetString()!);
            }
            var client = exchange.GetProperty("actor").GetString() switch
            { "owned-scoped-pat" => actor.BuildClient, "admin" => administrator, _ => throw new InvalidDataException("Unknown source actor.") };
            if (client == administrator && method != "GET") request.Headers.Add("RequestVerificationToken", csrf);
            using var response = await client.SendAsync(request);
            Assert.That((int)response.StatusCode, Is.EqualTo(expected.GetProperty("status").GetInt32()));
            Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo(expected.GetProperty("headers").GetProperty("content-type").GetString()));
            var actual = JsonNode.Parse(await response.Content.ReadAsByteArrayAsync())!;
            if (method == "POST" && recorded.GetProperty("path").GetString() == "/app/rest/buildQueue")
                bindings.Builds.Add(expectedJson["id"]!.GetValue<long>(), actual["id"]!.GetValue<long>());
            var required = bindings.Bind(expectedJson);
            Assert.That(JsonNode.DeepEquals(actual, required), Is.True,
                $"Complete {method} {recorded.GetProperty("path").GetString()} JSON differs. Expected {required}; actual {actual}");
        }
        var id = bindings.Builds.Values.Single();
        await using var scope = host.Services.CreateAsyncScope(); var database = scope.ServiceProvider.GetRequiredService<VivariumDbContext>();
        Assert.That(await database.RuntimeBuildExecutions.AnyAsync(e => e.BuildId == id), Is.False);
        Assert.That(await database.RuntimeBuildResults.AnyAsync(r => r.BuildId == id), Is.False);
        var build = await database.RuntimeBuilds.SingleAsync(b => b.Id == id);
        Assert.That(build.Counter, Is.Null); Assert.That(build.Number, Is.EqualTo("N/A"));
    }

    [TestCase(TestName = "TCREST-R0-PROJECT-ACTOR-AGENT-CONTROL-DENIED-001")]
    public async Task Project_actor_agent_control_matches_every_frozen_exchange_and_preserves_enabled_state()
    {
        // This source fixture sets the enabled comment at a literal server time. Control only
        // that owning service's clock; framework credential/heartbeat clocks remain ordinary.
        var clock = new ReferenceClock(new DateTimeOffset(2026, 11, 2, 12, 41, 1, TimeSpan.Zero));
        await using var host = await installation.CreateCaseAsync(catalogued: true, configureServices: services =>
            services.Replace(ServiceDescriptor.Singleton(provider => new CatalogService(
                provider.GetRequiredService<IDbContextFactory<VivariumDbContext>>(), provider.GetRequiredService<IConfiguration>(),
                provider.GetRequiredService<CatalogSources>(), clock, provider.GetRequiredService<AgentRegistry>()))));
        using var administrator = await host.AdministratorAsync(); var csrf = await host.AntiforgeryAsync(administrator);
        await host.StartWindowsAgentAsync(administrator, csrf);
        using var actor = new ScopedKeyScenario(host.Origin, host.Thumbprint, host.Root, host.Certificate);
        await actor.InitializeUserAsync(host.Services, "r0-reference-" + Guid.NewGuid().ToString("N")[..12]);
        await R0AcceptanceInstallation.Case.MutateAsync(administrator, csrf, HttpMethod.Put,
            $"/api/v1/users/{actor.UserId}/roles/PROJECT_DEVELOPER/projects/P", null, HttpStatusCode.NoContent);
        await actor.CreateKeyAsync();
        using var corpus = await ReferenceCorpusAsync();
        var observation = corpus.RootElement.GetProperty("cases").EnumerateArray().Single(item => item.GetProperty("id").GetString()
            == "TCREST-R0-PROJECT-ACTOR-AGENT-CONTROL-DENIED-001");
        var sourceAgent = corpus.RootElement.GetProperty("metadata").GetProperty("agentId").GetInt32();
        var exchanges = observation.GetProperty("exchanges"); Assert.That(exchanges.GetArrayLength(), Is.EqualTo(3));
        foreach (var exchange in exchanges.EnumerateArray())
        {
            var recorded = exchange.GetProperty("request");
            var path = recorded.GetProperty("path").GetString()!.Replace($"id:{sourceAgent}/", $"id:{host.TeamCityAgentId}/", StringComparison.Ordinal);
            path += "?" + string.Join('&', recorded.GetProperty("query").EnumerateObject()
                .Select(p => Uri.EscapeDataString(p.Name) + "=" + Uri.EscapeDataString(p.Value.GetString()!)));
            using var request = new HttpRequestMessage(new HttpMethod(recorded.GetProperty("method").GetString()!), path);
            request.Headers.Accept.ParseAdd(recorded.GetProperty("headers").GetProperty("accept").GetString()!);
            var body = Convert.FromBase64String(recorded.GetProperty("bodyBase64").GetString()!);
            if (body.Length > 0)
            {
                request.Content = new ByteArrayContent(body);
                request.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(
                    recorded.GetProperty("headers").GetProperty("content-type").GetString()!);
            }
            // Preserve the exact recorded actor for all three exchanges; the administrator
            // is only the final positive read, never a substitute for the denied mutation.
            var client = exchange.GetProperty("actor").GetString() switch
            { "owned-scoped-pat" => actor.BuildClient, "admin" => administrator, _ => throw new InvalidDataException("Unknown source actor.") };
            using var response = await client.SendAsync(request);
            var expected = exchange.GetProperty("response");
            Assert.That((int)response.StatusCode, Is.EqualTo(expected.GetProperty("status").GetInt32()));
            var media = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(expected.GetProperty("headers").GetProperty("content-type").GetString()!);
            Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo(media.MediaType));
            var actualBody = await response.Content.ReadAsByteArrayAsync();
            var expectedBody = Convert.FromBase64String(expected.GetProperty("bodyBase64").GetString()!);
            Assert.That(JsonNode.DeepEquals(JsonNode.Parse(actualBody), JsonNode.Parse(expectedBody)), Is.True,
                "Every recorded JSON field/type/value is required; only the request's allocated Agent locator is rebound.");
        }
        using var retained = JsonDocument.Parse(await administrator.GetStringAsync("/api/v1/agents/" + host.AgentId));
        Assert.That(retained.RootElement.GetProperty("enabled").GetBoolean(), Is.True);
    }

    private static async Task<JsonDocument> ReferenceCorpusAsync()
    {
        var repository = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "Vivarium.slnx"))) repository = repository.Parent;
        if (repository is null) throw new InvalidDataException("The reference repository is absent.");
        return JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(repository.FullName,
            "tests/Vivarium.Tests/TeamCity/Reference/r0-access-observations.json")));
    }

    private static void ConfigureReferenceTime(IServiceCollection services, TimeProvider clock)
    {
        services.Replace(ServiceDescriptor.Singleton(provider => new CatalogService(
            provider.GetRequiredService<IDbContextFactory<VivariumDbContext>>(), provider.GetRequiredService<IConfiguration>(),
            provider.GetRequiredService<CatalogSources>(), clock, provider.GetRequiredService<AgentRegistry>())));
        services.Replace(ServiceDescriptor.Singleton(provider => new RuntimeBuildService(
            provider.GetRequiredService<IDbContextFactory<VivariumDbContext>>(), provider.GetRequiredService<IConfiguration>(),
            provider.GetRequiredService<AgentRegistry>(), clock, provider.GetRequiredService<RuntimeBuildSecrets>())));
        services.Replace(ServiceDescriptor.Singleton(provider => new RuntimeBuildCoordinator(
            provider.GetRequiredService<IDbContextFactory<VivariumDbContext>>(), provider.GetRequiredService<RuntimeAgentSessions>(),
            provider.GetRequiredService<AgentRegistry>(), provider.GetRequiredService<RuntimeStepCompiler>(), provider.GetRequiredService<RuntimeBuildOutputStore>(),
            clock, provider.GetRequiredService<ILogger<RuntimeBuildCoordinator>>(), provider.GetRequiredService<RuntimeBuildSecrets>())));
        services.Replace(ServiceDescriptor.Singleton(provider => new RuntimeBuildOutputStore(
            provider.GetRequiredService<IDbContextFactory<VivariumDbContext>>(), provider.GetRequiredService<RuntimeBuildBytes>(),
            provider.GetRequiredService<AgentRegistry>(), clock, provider.GetRequiredService<ILogger<RuntimeBuildOutputStore>>())));
    }

    private static DateTimeOffset ReferenceDate(string text) => new(DateTime.ParseExact(text,
        "yyyyMMdd'T'HHmmss'+0000'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal));

    private static async Task PrepareReferenceNamesAsync(R0AcceptanceInstallation.Case host, HttpClient administrator, string csrf)
    {
        foreach (var project in new[] { "P", "Q" })
        {
            var control = Path.Combine(host.Definitions, $".vivarium/projects/{project}/Control.g.cs");
            await File.WriteAllTextAsync(control, (await File.ReadAllTextAsync(control)).Replace("Project " + project, "Owned R0 access " + project, StringComparison.Ordinal));
            var settings = Path.Combine(host.Definitions, $".vivarium/unbound/{project}/Settings.g.cs");
            await File.WriteAllTextAsync(settings, (await File.ReadAllTextAsync(settings)).Replace("\"Local smoke\"", "\"LocalSmoke\"", StringComparison.Ordinal));
        }
        await CommitCaseAsync(host, "fixture: prepare literal reference names through CSharp Git");
        Assert.That((await ReconcileCaseAsync(administrator, csrf)).GetProperty("state").GetString(), Is.EqualTo("applied"));
    }

    private sealed class ReferenceClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
