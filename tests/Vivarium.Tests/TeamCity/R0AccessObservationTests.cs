using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Vivarium.Tests.TeamCity;

[Category("TeamCityReplayControl")]
public sealed class R0AccessObservationTests
{
    private static JsonDocument Corpus() => JsonDocument.Parse(File.ReadAllText(Path.Combine(
        TestContext.CurrentContext.TestDirectory, "TeamCity/Reference/r0-access-observations.json")));

    [TestCaseSource(typeof(RecordedMutationCases), nameof(RecordedMutationCases.R0AccessObservations))]
    public async Task Selected_R0_reference_replays_exact_actors_requests_and_effects(RecordedMutationCase observation)
    {
        using var server = RecordedMutationReplayTests.StartReplay(observation);
        using var http = new HttpClient { BaseAddress = new Uri(server.Url!) };
        await observation.ExecuteAsync(http, replay: true);
        Assert.That(server.LogEntries, Has.Count.EqualTo(observation.Exchanges.Count));
        Assert.That(server.LogEntries.All(entry => entry.MappingGuid is not null), Is.True);
    }

    [Test]
    public void Successful_scoped_run_and_protected_sibling_have_real_equal_bytes_before_denial()
    {
        var cases = RecordedMutationCases.R0AccessObservations.ToArray();
        var expected = "owned-r0-smoke\n"u8.ToArray();
        var positives = cases.SelectMany(c => c.Exchanges)
            .Where(e => e.Method == "GET" && e.PathAndQuery.Contains("/artifacts/files/smoke.txt", StringComparison.Ordinal)
                && e.Expected.Status == HttpStatusCode.OK).ToArray();
        Assert.That(positives.Any(e => e.Actor == "owned-scoped-pat"), Is.True);
        Assert.That(positives.Any(e => e.Actor == "admin"), Is.True);
        foreach (var positive in positives) Assert.That(positive.Expected.Body, Is.EqualTo(expected));
        var protectedCase = cases.Single(c => c.Id == "TCREST-R0-PROTECTED-SIBLING-BYTES-DENIED-001");
        Assert.That(protectedCase.Exchanges.Count(e => e.Actor == "owned-scoped-pat" &&
            e.Expected.Status == HttpStatusCode.Forbidden), Is.EqualTo(4));
        Assert.That(protectedCase.Exchanges.Any(e => e.Actor == "owned-scoped-pat" &&
            e.Expected.Status == HttpStatusCode.OK && e.PathAndQuery.Contains("/artifacts/files/", StringComparison.Ordinal)), Is.True);
        Assert.That(protectedCase.Exchanges.Where(e => e.Expected.Status == HttpStatusCode.Forbidden)
            .All(e => !Encoding.UTF8.GetString(e.Expected.Body).Contains("owned-r0-smoke", StringComparison.Ordinal)), Is.True);
    }

    [Test]
    public void Project_agent_denial_preserves_enabled_state_and_revocation_is_distinct_from_forbidden()
    {
        var cases = RecordedMutationCases.R0AccessObservations.ToArray();
        var agent = cases.Single(c => c.Id == "TCREST-R0-PROJECT-ACTOR-AGENT-CONTROL-DENIED-001");
        Assert.That(agent.Exchanges.Select(e => e.Expected.Status), Is.EqualTo(new[]
            { HttpStatusCode.OK, HttpStatusCode.Forbidden, HttpStatusCode.OK }));
        foreach (var read in new[] { agent.Exchanges[0], agent.Exchanges[^1] })
        {
            using var value = JsonDocument.Parse(read.Expected.Body);
            Assert.That(value.RootElement.GetProperty("status").GetBoolean(), Is.True);
        }
        var revoke = cases.Single(c => c.Id == "TCREST-R0-REVOKED-CREDENTIAL-BUILD-BYTES-DENIED-001");
        Assert.That(revoke.Exchanges.Select(e => e.Expected.Status), Is.EqualTo(new[]
        {
            HttpStatusCode.Forbidden, HttpStatusCode.NoContent, HttpStatusCode.Unauthorized,
            HttpStatusCode.Unauthorized, HttpStatusCode.OK,
        }));
        Assert.That(revoke.Exchanges[1].Actor, Is.EqualTo("admin"));
        Assert.That(revoke.Exchanges[2].Actor, Is.EqualTo("owned-scoped-pat"));
        Assert.That(revoke.Exchanges[3].Actor, Is.EqualTo("owned-scoped-pat"));
        Assert.That(revoke.Exchanges[2].Expected.MediaType, Is.EqualTo("text/plain"));
        Assert.That(revoke.Exchanges[3].Expected.MediaType, Is.EqualTo("text/plain"));
    }

    [Test]
    public async Task Administrator_cannot_substitute_for_the_scoped_actor_in_replay_or_raw_execution()
    {
        var observation = RecordedMutationCases.R0AccessObservations.First();
        var first = observation.Exchanges[0];
        Assert.That(first.Actor, Is.EqualTo("owned-scoped-pat"));
        using var server = RecordedMutationReplayTests.StartReplay(observation);
        using var http = new HttpClient { BaseAddress = new Uri(server.Url!) };
        using var wrong = first.Request("fixture-admin");
        using var response = await http.SendAsync(wrong);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That(server.LogEntries.Single().MappingGuid, Is.Null);
        var adminPrefix = observation with { Exchanges = [first with { Actor = "admin" }, .. observation.Exchanges] };
        Assert.ThrowsAsync<InvalidOperationException>(() => adminPrefix.ExecuteAsync(http, replay: false));
        Assert.That(server.LogEntries, Has.Count.EqualTo(1), "Raw execution refuses actor substitution before HTTP.");
    }

    [Test]
    public void Nonzero_and_both_cancel_cases_retain_separate_lifecycle_and_outcome_evidence()
    {
        var cases = RecordedMutationCases.R0AccessObservations.ToArray();
        foreach (var (id, outcome) in new[]
        {
            ("TCREST-R0-SCOPED-NONZERO-EXIT-001", "FAILURE"),
            ("TCREST-R0-SCOPED-QUEUED-CANCEL-001", "UNKNOWN"),
            ("TCREST-R0-SCOPED-RUNNING-CANCEL-001", "UNKNOWN"),
        })
        {
            var observation = cases.Single(c => c.Id == id);
            var read = observation.Exchanges.Last(e => e.Actor == "owned-scoped-pat" &&
                e.Method == "GET" && e.PathAndQuery.StartsWith("/app/rest/builds/id:", StringComparison.Ordinal));
            using var body = JsonDocument.Parse(read.Expected.Body);
            Assert.That(body.RootElement.GetProperty("state").GetString(), Is.EqualTo("finished"));
            Assert.That(body.RootElement.GetProperty("status").GetString(), Is.EqualTo(outcome));
            if (id == "TCREST-R0-SCOPED-NONZERO-EXIT-001") continue;
            var cancel = observation.Exchanges.Single(e => e.Actor == "owned-scoped-pat" &&
                e.Method == "POST" && e.PathAndQuery.Contains("/id:", StringComparison.Ordinal));
            using var request = JsonDocument.Parse(cancel.Body!);
            Assert.That(request.RootElement.GetProperty("readdIntoQueue").GetBoolean(), Is.False);
        }
    }

    [Test]
    public void Corpus_preserves_literal_integrity_private_setup_boundaries_and_verified_cleanup()
    {
        using var document = Corpus();
        var root = document.RootElement;
        var metadata = root.GetProperty("metadata");
        Assert.That(metadata.GetProperty("complete").GetBoolean(), Is.True);
        Assert.That(metadata.GetProperty("localSecretsCleared").GetBoolean(), Is.True);
        Assert.That(metadata.GetProperty("cliVersion").GetString(), Is.EqualTo("0.5.6"));
        Assert.That(metadata.GetProperty("actorCredentialFingerprint").GetString(), Has.Length.EqualTo(64));
        foreach (var setup in metadata.GetProperty("privateSetup").EnumerateArray())
            Assert.That(setup.GetProperty("entityExport").GetString(), Is.EqualTo("OMITTED_PRIVATE_CREDENTIAL_SETUP"));
        var count = 0;
        foreach (var group in root.GetProperty("cases").EnumerateArray())
        foreach (var exchange in group.GetProperty("exchanges").EnumerateArray())
        {
            count++;
            var request = exchange.GetProperty("request");
            Assert.That(request.GetProperty("headers").TryGetProperty("authorization", out _), Is.False);
            foreach (var side in new[] { request, exchange.GetProperty("response") })
            {
                var bytes = Convert.FromBase64String(side.GetProperty("bodyBase64").GetString()!);
                Assert.That(bytes.Length, Is.EqualTo(side.GetProperty("bodyBytes").GetInt32()));
                Assert.That(Convert.ToHexStringLower(SHA256.HashData(bytes)), Is.EqualTo(side.GetProperty("bodySha256").GetString()));
            }
        }
        Assert.That(count, Is.EqualTo(root.GetProperty("case_exchange_count").GetInt32()));
        Assert.That(count, Is.EqualTo(134));
    }
}
