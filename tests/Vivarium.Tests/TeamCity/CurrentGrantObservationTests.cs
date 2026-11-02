using System.Security.Cryptography;
using System.Text.Json;

namespace Vivarium.Tests.TeamCity;

[Category("TeamCityReplayControl")]
public sealed class CurrentGrantObservationTests
{
    private static JsonDocument Corpus() => JsonDocument.Parse(File.ReadAllText(Path.Combine(
        TestContext.CurrentContext.TestDirectory, "TeamCity/Reference/current-grant-observations.json")));

    [Test]
    public async Task Strict_replay_keeps_encoded_role_scope_distinct_from_another_raw_target()
    {
        var observation = RecordedMutationCases.CurrentGrantObservations.Single(c => c.Id == "TCREST-PERMISSION-PROJECT-CURRENT-GRANT-001");
        var exchange = observation.Exchanges[0];
        Assert.That(exchange.PathAndQuery, Does.Contain("p%3A"));
        using var server = RecordedMutationReplayTests.StartReplay(observation);
        using var http = new HttpClient { BaseAddress = new Uri(server.Url!) };
        using var altered = (exchange with { PathAndQuery = exchange.PathAndQuery.Replace("p%3A", "p:", StringComparison.Ordinal) }).Request("fixture-" + exchange.Actor);
        altered.Headers.Add("X-Vivarium-Replay-Raw-Target", exchange.PathAndQuery);
        using var rejected = await http.SendAsync(altered);
        Assert.That(rejected.StatusCode, Is.EqualTo(System.Net.HttpStatusCode.NotFound));
        using var original = exchange.Request("fixture-" + exchange.Actor);
        using var accepted = await http.SendAsync(original);
        foreach (var entry in server.LogEntries)
        {
            var message = entry.RequestMessage ?? throw new InvalidDataException("Replay log lacks a request.");
            TestContext.Out.WriteLine($"Replay URI: {message.Url}; raw target: {message.Headers!["X-Vivarium-Replay-Raw-Target"]}; expected: {exchange.PathAndQuery}");
        }
        await RestResponseAssert.MatchesAsync(accepted, exchange.ExpectedAt(http.BaseAddress!));
        Assert.That(server.LogEntries.First().MappingGuid, Is.Null);
        Assert.That(server.LogEntries.Last().MappingGuid, Is.Not.Null);
    }

    [TestCaseSource(typeof(RecordedMutationCases), nameof(RecordedMutationCases.CurrentGrantObservations))]
    public async Task Current_grant_observation_replays_literal_requests_and_effect_readbacks(RecordedMutationCase observation)
    {
        using var server = RecordedMutationReplayTests.StartReplay(observation);
        using var http = new HttpClient { BaseAddress = new Uri(server.Url!) };
        await observation.ExecuteAsync(http, replay: true);
        Assert.That(server.LogEntries, Has.Count.EqualTo(observation.Exchanges.Count));
        Assert.That(server.LogEntries.All(entry => entry.MappingGuid is not null), Is.True);
    }

    [Test]
    public void One_scoped_credential_observes_current_grant_and_revoke_with_protected_project_controls()
    {
        using var document = Corpus(); var root = document.RootElement; var metadata = root.GetProperty("metadata");
        Assert.That(metadata.GetProperty("beforeGrant").GetProperty("status").GetInt32(), Is.EqualTo(403));
        Assert.That(metadata.GetProperty("afterGrant").GetProperty("returned").GetBoolean(), Is.True);
        Assert.That(metadata.GetProperty("afterRevoke").GetProperty("status").GetInt32(), Is.EqualTo(403));
        Assert.That(metadata.GetProperty("protectedBefore").GetProperty("status").GetInt32(), Is.EqualTo(403));
        Assert.That(metadata.GetProperty("protectedAfter").GetProperty("status").GetInt32(), Is.EqualTo(403));
        var cases = root.GetProperty("cases").EnumerateArray().ToArray();
        var exchanges = cases.SelectMany(c => c.GetProperty("exchanges").EnumerateArray()).ToArray();
        var project = metadata.GetProperty("project").GetString();
        var protectedProject = metadata.GetProperty("protectedProject").GetString();
        var writes = exchanges.Where(e => e.GetProperty("actor").GetString() == "owned-scoped-pat" &&
            e.GetProperty("request").GetProperty("method").GetString() == "PUT").ToArray();
        Assert.That(writes.Select(e => e.GetProperty("response").GetProperty("status").GetInt32()), Is.EqualTo(new[] { 403, 200, 403 }));
        Assert.That(writes.All(e => e.GetProperty("request").GetProperty("path").GetString() == "/app/rest/projects/id:" + project + "/description"), Is.True);
        var protectedReads = exchanges.Where(e => e.GetProperty("request").GetProperty("path").GetString() == "/app/rest/projects/id:" + protectedProject).ToArray();
        Assert.That(protectedReads.Where(e => e.GetProperty("actor").GetString() == "owned-scoped-pat")
            .Select(e => e.GetProperty("response").GetProperty("status").GetInt32()), Is.EqualTo(new[] { 403, 403 }));
        Assert.That(protectedReads.Where(e => e.GetProperty("actor").GetString() == "admin")
            .All(e => e.GetProperty("response").GetProperty("status").GetInt32() == 200), Is.True);
        Assert.That(protectedReads.Count(e => e.GetProperty("actor").GetString() == "admin"), Is.EqualTo(2));
        var finalRead = cases[^1].GetProperty("exchanges").EnumerateArray().Last();
        using var final = JsonDocument.Parse(Convert.FromBase64String(finalRead.GetProperty("response").GetProperty("bodyBase64").GetString()!));
        Assert.That(final.RootElement.GetProperty("description").GetString(), Is.EqualTo("allowed-after-grant"), "Denied writes must not replace the accepted value.");
    }

    [Test]
    public void Credential_setup_is_excluded_while_literal_entities_and_cleanup_evidence_remain_integral()
    {
        using var document = Corpus(); var root = document.RootElement; var metadata = root.GetProperty("metadata");
        Assert.That(metadata.GetProperty("complete").GetBoolean(), Is.True);
        Assert.That(metadata.GetProperty("localAliasRemoved").GetBoolean(), Is.True);
        foreach (var setup in metadata.GetProperty("privateSetup").EnumerateArray())
            Assert.That(setup.GetProperty("entityExport").GetString(), Is.EqualTo("OMITTED_PRIVATE_CREDENTIAL_SETUP"));
        var count = 0;
        foreach (var observation in root.GetProperty("cases").EnumerateArray())
        foreach (var exchange in observation.GetProperty("exchanges").EnumerateArray())
        {
            count++;
            var request = exchange.GetProperty("request");
            Assert.That(request.GetProperty("headers").TryGetProperty("authorization", out _), Is.False);
            Assert.That(request.GetProperty("path").GetString(), Does.Not.Contain("/tokens"));
            foreach (var part in new[] { request, exchange.GetProperty("response") })
            {
                var bytes = Convert.FromBase64String(part.GetProperty("bodyBase64").GetString()!);
                Assert.That(bytes.Length, Is.EqualTo(part.GetProperty("bodyBytes").GetInt32()));
                Assert.That(Convert.ToHexStringLower(SHA256.HashData(bytes)), Is.EqualTo(part.GetProperty("bodySha256").GetString()));
            }
        }
        Assert.That(count, Is.EqualTo(root.GetProperty("case_exchange_count").GetInt32()));
    }
}
