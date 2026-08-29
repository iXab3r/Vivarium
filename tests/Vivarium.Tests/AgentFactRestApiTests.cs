using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Vivarium.Contracts.V1;
using Vivarium.Controller;
using Vivarium.Controller.Agents;
using Vivarium.Controller.Persistence;
using Vivarium.Controller.Security;

namespace Vivarium.Tests;

[TestFixture]
[NonParallelizable]
public sealed class AgentFactRestApiTests
{
    private const string HostFactsCapability = "agent-explorer.host-facts.v1";
    private const string BuildRunnerCapability = "teamcity.build-runner.v1";
    private const string PackageDigest =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private string rootDir = null!;

    [SetUp]
    public void SetUp()
    {
        rootDir = Path.Combine(
            Path.GetTempPath(), "vivarium-agent-fact-rest-tests", Guid.NewGuid().ToString("N"));
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
            // Best effort: preserve the original failure when a platform delays handle release.
        }
    }

    [Test]
    public async Task Store_persists_typed_facts_filters_and_monotonic_session_fences_across_restart()
    {
        var dataDir = Path.Combine(rootDir, "store");
        Directory.CreateDirectory(dataDir);
        await using (var database = new VivariumDatabase(dataDir))
        {
            var tokens = new TokenStore(dataDir, database);
            var store = new AgentStore(database);
            var hello = await EnrollAgentAsync(tokens, store, "typed-agent", "legacy-host", "linux");
            Assert.That(await tokens.AuthorizeAgentAsync(hello.AgentId), Is.Not.Null);
            var accepted = await store.AcceptSessionAsync(hello.AgentId, credentialGeneration: 1);
            await store.ObserveCapabilitiesAsync(
                hello.AgentId,
                accepted.CredentialGeneration,
                accepted.ConnectionGeneration,
                [new AgentCapabilitySupport(HostFactsCapability, 1)]);
            var written = await store.ObserveStaticFactsAsync(Observation(
                hello.AgentId,
                accepted,
                AgentFactCollectorOutcome.Partial,
                complete: false));
            var page = await store.QueryPageAsync(
                new AgentStoreQuery(
                    Hostnames: ["typed-host"],
                    OsFamilies: ["LINUX"],
                    OsVersions: ["24.04"],
                    OsBuilds: ["build-42"],
                    Architectures: ["ARM64"],
                    AgentVersions: ["2.0.0"],
                    Capabilities: [HostFactsCapability],
                    PackageDigests: [PackageDigest]),
                after: null,
                limit: 10);

            Assert.Multiple(() =>
            {
                Assert.That(accepted, Is.EqualTo(new AgentGenerationState(1, 1)));
                Assert.That(written.Revision, Is.EqualTo(1));
                Assert.That(written.Quality, Is.EqualTo("partial"));
                Assert.That(page.Items.Select(item => item.Agent.AgentId),
                    Is.EqualTo(new[] { "typed-agent" }));
                Assert.That(page.Items[0].Projection.Observation!.Capabilities,
                    Is.EqualTo(new[] { new AgentCapabilitySupport(HostFactsCapability, 1) }));
            });

            await store.ObserveCapabilitiesAsync(
                hello.AgentId,
                accepted.CredentialGeneration,
                accepted.ConnectionGeneration,
                [new AgentCapabilitySupport(BuildRunnerCapability, 1)]);
            _ = await store.ObserveStaticFactsAsync(Observation(
                hello.AgentId,
                accepted,
                AgentFactCollectorOutcome.Succeeded,
                complete: true));
            var capabilitiesOnlyUpdate = await store.GetProjectionAsync(hello.AgentId);
            var capabilityOnlyHello = await EnrollAgentAsync(
                tokens,
                store,
                "capability-only-agent",
                "capability-only-host",
                "linux");
            Assert.That(await tokens.AuthorizeAgentAsync(capabilityOnlyHello.AgentId), Is.Not.Null);
            var capabilityOnlyGeneration = await store.AcceptSessionAsync(
                capabilityOnlyHello.AgentId,
                credentialGeneration: 1);
            await store.ObserveCapabilitiesAsync(
                capabilityOnlyHello.AgentId,
                capabilityOnlyGeneration.CredentialGeneration,
                capabilityOnlyGeneration.ConnectionGeneration,
                [new AgentCapabilitySupport(BuildRunnerCapability, 1)]);
            var capabilityOnly = await store.GetProjectionAsync(capabilityOnlyHello.AgentId);

            Assert.Multiple(() =>
            {
                Assert.That(capabilitiesOnlyUpdate!.Observation!.Revision, Is.EqualTo(2));
                Assert.That(capabilitiesOnlyUpdate.Observation.Facts.Hostname, Is.EqualTo("typed-host"));
                Assert.That(capabilitiesOnlyUpdate.Observation.Capabilities,
                    Is.EqualTo(new[] { new AgentCapabilitySupport(BuildRunnerCapability, 1) }));
                Assert.That(capabilityOnly!.Observation, Is.Null,
                    "capability negotiation must not fabricate or replace a HostFacts observation");
                Assert.That(capabilityOnly.Capabilities,
                    Is.EqualTo(new[] { new AgentCapabilitySupport(BuildRunnerCapability, 1) }));
            });

            var uppercaseDigest = Observation(
                hello.AgentId,
                accepted,
                AgentFactCollectorOutcome.Succeeded,
                complete: true) with
            {
                PackageDigestSha256 = PackageDigest.ToUpperInvariant(),
            };
            Assert.That(
                async () => await store.ObserveStaticFactsAsync(uppercaseDigest),
                Throws.ArgumentException.With.Message.Contains("lowercase"));

            var schemaException = Assert.ThrowsAsync<SqliteException>(async () =>
                await database.WriteAsync(connection =>
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = """
                        UPDATE agent_fact_observations
                        SET package_digest_sha256 = upper(package_digest_sha256)
                        WHERE agent_id = 'typed-agent';
                        """;
                    command.ExecuteNonQuery();
                    return true;
                }));
            Assert.That(schemaException!.SqliteErrorCode, Is.EqualTo(19));
        }

        await using (var restarted = new VivariumDatabase(dataDir))
        {
            var store = new AgentStore(restarted);
            var projection = await store.GetProjectionAsync("typed-agent");
            var secondSession = await store.AcceptSessionAsync("typed-agent", credentialGeneration: 1);

            Assert.Multiple(() =>
            {
                Assert.That(projection, Is.Not.Null);
                Assert.That(projection!.Agent.CredentialGeneration, Is.EqualTo(1));
                Assert.That(projection.Agent.ConnectionGeneration, Is.EqualTo(1));
                Assert.That(projection.Observation!.Facts.Hostname, Is.EqualTo("typed-host"));
                Assert.That(projection.Observation.PackageDigestSha256, Is.EqualTo(PackageDigest));
                Assert.That(projection.Observation.Capabilities,
                    Is.EqualTo(new[] { new AgentCapabilitySupport(BuildRunnerCapability, 1) }));
                Assert.That(secondSession, Is.EqualTo(new AgentGenerationState(1, 2)));
            });

            Assert.That(
                async () => await store.ObserveStaticFactsAsync(Observation(
                    "typed-agent",
                    new AgentGenerationState(1, 1),
                    AgentFactCollectorOutcome.Succeeded,
                    complete: true)),
                Throws.InvalidOperationException.With.Message.Contains("superseded"));
        }
    }

    [Test]
    public async Task Replacement_reenrollment_revokes_old_credential_and_advances_generation()
    {
        var dataDir = Path.Combine(rootDir, "credentials");
        Directory.CreateDirectory(dataDir);
        await using var database = new VivariumDatabase(dataDir);
        var tokens = new TokenStore(dataDir, database);
        var store = new AgentStore(database);
        var hello = await EnrollAgentAsync(tokens, store, "replacement-agent", "replace-host", "linux");
        var oldToken = await tokens.AuthorizeAgentAsync(hello.AgentId);
        var initial = await store.GetGenerationStateAsync(hello.AgentId);

        hello.EnrollToken = await tokens.CreateEnrollTokenAsync();
        hello.AuthToken = string.Empty;
        var replacementAdmission = await tokens.AdmitAgentAsync(hello);
        var replaced = await store.GetGenerationStateAsync(hello.AgentId);
        var replacementToken = await tokens.AuthorizeAgentAsync(hello.AgentId);
        var authorized = await store.GetGenerationStateAsync(hello.AgentId);

        Assert.Multiple(() =>
        {
            Assert.That(oldToken, Is.Not.Null.And.Not.Empty);
            Assert.That(initial, Is.EqualTo(new AgentGenerationState(1, 0)));
            Assert.That(replacementAdmission, Is.Not.Null);
            Assert.That(replacementAdmission!.Authorization, Is.EqualTo(AgentAuth.Unauthorized));
            Assert.That(replacementAdmission.CredentialGeneration, Is.EqualTo(2));
            Assert.That(replaced, Is.EqualTo(new AgentGenerationState(2, 0)));
            Assert.That(replacementToken, Is.Not.Null.And.Not.Empty.And.Not.EqualTo(oldToken));
            Assert.That(authorized, Is.EqualTo(new AgentGenerationState(2, 0)),
                "issuing the replacement credential must use the generation already advanced by re-enrollment");
        });
        Assert.That(await tokens.ResolveBearerPrincipalAsync(oldToken!), Is.Null);
        Assert.That(await tokens.ResolveBearerPrincipalAsync(replacementToken!), Is.Not.Null);
    }

    [Test]
    public async Task Rest_projects_bounded_typed_facts_filters_etags_restart_and_legacy_unknowns()
    {
        var dataDir = Path.Combine(rootDir, "controller");
        Directory.CreateDirectory(dataDir);
        string firstEtag;
        await using (var controller = await StartControllerAsync(dataDir))
        {
            var hello = await EnrollAgentAsync(
                controller.Tokens,
                controller.AgentStore,
                "rest-typed-agent",
                "legacy-rest-host",
                "linux");
            var accepted = await controller.AgentStore.AcceptSessionAsync(hello.AgentId, 0);
            using var sessionAbort = new CancellationTokenSource();
            _ = controller.Registry.Register(
                hello,
                AgentAuth.Unauthorized,
                enabled: true,
                accepted.ConnectionGeneration,
                sessionAbort);
            await controller.AgentStore.ObserveCapabilitiesAsync(
                hello.AgentId,
                accepted.CredentialGeneration,
                accepted.ConnectionGeneration,
                [new AgentCapabilitySupport(HostFactsCapability, 1)]);
            await controller.AgentStore.ObserveStaticFactsAsync(Observation(
                hello.AgentId,
                accepted,
                AgentFactCollectorOutcome.Degraded,
                complete: false));
            Assert.That(await controller.Tokens.AuthorizeAgentAsync(hello.AgentId), Is.Not.Null);
            await EnrollAgentAsync(
                controller.Tokens,
                controller.AgentStore,
                "legacy-agent",
                "legacy-only-host",
                "windows");

            using var http = PinnedClient(controller);
            using var listRequest = AdminGet(
                controller,
                $"/api/v1/agents?hostname=typed-host&osVersion=24.04&osBuild=build-42" +
                $"&capability={HostFactsCapability}&packageDigest={PackageDigest}");
            var list = await http.SendAsync(listRequest);
            var listJson = await list.Content.ReadAsStringAsync();
            using var listBody = JsonDocument.Parse(listJson);
            var item = listBody.RootElement.GetProperty("items")[0];

            using var factsRequest = AdminGet(controller, "/api/v1/agents/rest-typed-agent/facts");
            var facts = await http.SendAsync(factsRequest);
            firstEtag = facts.Headers.ETag!.Tag;
            var factsJson = await facts.Content.ReadAsStringAsync();
            using var factsBody = JsonDocument.Parse(factsJson);

            using var legacyRequest = AdminGet(controller, "/api/v1/agents/legacy-agent/facts");
            var legacy = await http.SendAsync(legacyRequest);
            using var legacyBody = JsonDocument.Parse(await legacy.Content.ReadAsStreamAsync());

            using var conditionalRequest = AdminGet(controller, "/api/v1/agents/rest-typed-agent/facts");
            conditionalRequest.Headers.TryAddWithoutValidation("If-None-Match", firstEtag);
            var notModified = await http.SendAsync(conditionalRequest);

            Assert.Multiple(() =>
            {
                Assert.That(list.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(listBody.RootElement.GetProperty("items").GetArrayLength(), Is.EqualTo(1));
                Assert.That(item.GetProperty("id").GetString(), Is.EqualTo("rest-typed-agent"));
                Assert.That(item.GetProperty("factObservation").GetProperty("quality").GetString(),
                    Is.EqualTo("partial"));
                Assert.That(item.GetProperty("capabilities")[0].GetProperty("id").GetString(),
                    Is.EqualTo(HostFactsCapability));
                Assert.That(facts.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(factsBody.RootElement.GetProperty("quality").GetString(), Is.EqualTo("partial"));
                Assert.That(factsBody.RootElement.GetProperty("collectorOutcome").GetString(),
                    Is.EqualTo("degraded"));
                Assert.That(factsBody.RootElement.GetProperty("freshness").GetString(), Is.EqualTo("current"));
                Assert.That(factsBody.RootElement.GetProperty("currentGenerations")
                    .GetProperty("credential").GetInt64(), Is.EqualTo(1));
                Assert.That(factsBody.RootElement.GetProperty("observationGenerations")
                    .GetProperty("credential").GetInt64(), Is.Zero,
                    "first authorization must retain same-connection observation provenance without staling it");
                Assert.That(factsBody.RootElement.GetProperty("operatingSystem")
                    .GetProperty("build").GetString(), Is.EqualTo("build-42"));
                Assert.That(factsBody.RootElement.GetProperty("extensionFacts")
                    .GetProperty("hardware.vendor").GetString(), Is.EqualTo("Vivarium Test"));
                Assert.That(factsJson, Does.Not.Contain("diagnostic message must not be projected"));
                Assert.That(factsJson, Does.Not.Contain(hello.SessionId));
                Assert.That(legacy.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(legacyBody.RootElement.GetProperty("quality").GetString(), Is.EqualTo("unknown"));
                Assert.That(legacyBody.RootElement.GetProperty("observationRevision").GetInt64(), Is.Zero);
                Assert.That(legacyBody.RootElement.GetProperty("capabilities").GetArrayLength(), Is.Zero);
                Assert.That(notModified.StatusCode, Is.EqualTo(HttpStatusCode.NotModified));
            });

            _ = await controller.AgentStore.AcceptSessionAsync(hello.AgentId, credentialGeneration: 1);
            using var supersededRequest = AdminGet(controller, "/api/v1/agents/rest-typed-agent/facts");
            supersededRequest.Headers.TryAddWithoutValidation("If-None-Match", firstEtag);
            var superseded = await http.SendAsync(supersededRequest);
            using var supersededBody = JsonDocument.Parse(await superseded.Content.ReadAsStreamAsync());
            Assert.Multiple(() =>
            {
                Assert.That(superseded.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(supersededBody.RootElement.GetProperty("freshness").GetString(),
                    Is.EqualTo("superseded"));
                Assert.That(supersededBody.RootElement.GetProperty("currentGenerations")
                    .GetProperty("connection").GetInt64(), Is.EqualTo(2));
                Assert.That(supersededBody.RootElement.GetProperty("observationGenerations")
                    .GetProperty("connection").GetInt64(), Is.EqualTo(1));
            });
        }

        await using var restarted = await StartControllerAsync(dataDir);
        using var restartedHttp = PinnedClient(restarted);
        using var restartedRequest = AdminGet(restarted, "/api/v1/agents/rest-typed-agent/facts");
        var restartedFacts = await restartedHttp.SendAsync(restartedRequest);
        using var restartedBody = JsonDocument.Parse(await restartedFacts.Content.ReadAsStreamAsync());

        Assert.Multiple(() =>
        {
            Assert.That(restartedFacts.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(restartedBody.RootElement.GetProperty("freshness").GetString(),
                Is.EqualTo("superseded"));
            Assert.That(restartedBody.RootElement.GetProperty("hostname").GetString(), Is.EqualTo("typed-host"));
            Assert.That(restartedBody.RootElement.GetProperty("currentGenerations")
                .GetProperty("connection").GetInt64(), Is.EqualTo(2));
            Assert.That(restartedFacts.Headers.ETag!.Tag, Is.Not.EqualTo(firstEtag),
                "the fact representation ETag must change when freshness transitions to stale");
        });
    }

    private static AgentStaticObservation Observation(
        string agentId,
        AgentGenerationState generations,
        AgentFactCollectorOutcome outcome,
        bool complete) => new(
        agentId,
        DateTimeOffset.UtcNow.AddSeconds(-1),
        DateTimeOffset.UtcNow,
        outcome,
        complete,
        [new AgentObservationIssue(
            "collector.partial",
            "kernelVersion",
            "E_TEST",
            "diagnostic message must not be projected")],
        [new AgentCapabilitySupport(HostFactsCapability, 1)],
        new AgentStaticFacts(
            "typed-host",
            "linux",
            "Ubuntu",
            "24.04",
            "build-42",
            "6.8-test",
            "arm64",
            "x64",
            "2.0.0",
            "2.0.0-package",
            "collector-v1",
            Interactive: false,
            new Dictionary<string, string>
            {
                ["hardware.vendor"] = "Vivarium Test",
            }),
        generations.CredentialGeneration,
        generations.ConnectionGeneration,
        PackageDigest);

    private static async Task<Hello> EnrollAgentAsync(
        TokenStore tokens,
        AgentStore store,
        string agentId,
        string hostname,
        string osFamily)
    {
        var hello = new Hello
        {
            AgentId = agentId,
            EnrollToken = await tokens.CreateEnrollTokenAsync(),
            SessionId = $"session-{agentId}",
            AgentVersion = "legacy-agent-version",
            Os = new OsInfo
            {
                Family = osFamily,
                Version = "legacy-os-version",
                Arch = "x64",
            },
            Interactive = true,
        };
        hello.Parameters["hostname"] = hostname;
        Assert.That(await tokens.AdmitAgentAsync(hello), Is.Not.Null);
        await store.ObserveHelloAsync(hello);
        return hello;
    }

    private Task<VivariumControllerHost> StartControllerAsync(string dataDir) =>
        VivariumControllerHost.StartAsync(new ControllerOptions
        {
            DataDir = dataDir,
            Host = "127.0.0.1",
            Port = 0,
        });

    private static HttpRequestMessage AdminGet(VivariumControllerHost controller, string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", controller.Tokens.AdminToken);
        return request;
    }

    private static HttpClient PinnedClient(VivariumControllerHost controller)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                certificate is not null &&
                Convert.ToHexString(SHA256.HashData(certificate.RawData)).Equals(
                    controller.Certificate.FingerprintSha256,
                    StringComparison.OrdinalIgnoreCase),
        };
        return new HttpClient(handler) { BaseAddress = new Uri(controller.Url) };
    }
}
