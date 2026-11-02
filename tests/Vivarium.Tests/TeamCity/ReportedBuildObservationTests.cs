using System.Security.Cryptography;
using System.Text.Json;

namespace Vivarium.Tests.TeamCity;

[Category("TeamCityReplayControl")]
public sealed class ReportedBuildObservationTests
{
    private static JsonDocument Corpus() => JsonDocument.Parse(File.ReadAllText(Path.Combine(
        TestContext.CurrentContext.TestDirectory, "TeamCity/Reference/reported-build-observations.json")));

    [TestCaseSource(typeof(RecordedMutationCases), nameof(RecordedMutationCases.ReportedBuildObservations))]
    public async Task Reported_build_observation_replays_literal_requests_and_effects(RecordedMutationCase observation)
    {
        using var server = RecordedMutationReplayTests.StartReplay(observation);
        using var http = new HttpClient { BaseAddress = new Uri(server.Url!) };
        await observation.ExecuteAsync(http, replay: true);
        Assert.That(server.LogEntries, Has.Count.EqualTo(observation.Exchanges.Count));
        Assert.That(server.LogEntries.All(entry => entry.MappingGuid is not null), Is.True);
    }

    [Test]
    public void Display_override_and_finish_facts_do_not_claim_immutable_current_problems_or_natural_failure()
    {
        using var document = Corpus(); var root = document.RootElement; var metadata = root.GetProperty("metadata");
        var observations = metadata.GetProperty("observations").EnumerateArray().ToArray();
        JsonElement Build(string label) => observations.Single(o => o.GetProperty("label").GetString() == label).GetProperty("build");
        var initial = Build("Changed display number");
        Assert.That(initial.GetProperty("id").GetInt64(), Is.EqualTo(metadata.GetProperty("buildId").GetInt64()));
        Assert.That(initial.GetProperty("number").GetString(), Is.EqualTo("owned-display-label"));
        var finishing = observations.Where(o => o.GetProperty("label").GetString() == "After finish").Select(o => o.GetProperty("build")).ToArray();
        Assert.That(finishing.Select(b => b.GetProperty("state").GetString()), Is.EqualTo(new[] { "running", "finished" }));
        Assert.That(finishing.All(b => b.GetProperty("status").GetString() == "FAILURE"), Is.True);
        Assert.That(Build("After manual success").GetProperty("status").GetString(), Is.EqualTo("SUCCESS"));
        Assert.That(metadata.GetProperty("problemBeforeFinish").GetArrayLength(), Is.EqualTo(1));
        Assert.That(metadata.GetProperty("problemAfterOverride").GetArrayLength(), Is.Zero);
        Assert.That(metadata.GetProperty("explicitProblemAfterOverride").GetProperty("status").GetInt32(), Is.EqualTo(404));
        var cancelled = Build("Natural failure after manual success");
        Assert.That(cancelled.GetProperty("canceledInfo").GetProperty("timestamp").GetString(), Is.Not.Empty);
        Assert.That(cancelled.GetProperty("status").GetString(), Is.EqualTo("UNKNOWN"));
        Assert.That(metadata.GetProperty("naturalManualStatus").GetProperty("errorCount").GetInt32(), Is.Zero);
        Assert.That(metadata.GetProperty("natural_failure_proven").GetBoolean(), Is.False);
        Assert.That(metadata.GetProperty("agentlessDenial").GetInt32(), Is.EqualTo(400));
    }

    [Test]
    public void Reported_capture_keeps_literal_entities_and_separate_cleanup_evidence()
    {
        using var document = Corpus(); var root = document.RootElement; var metadata = root.GetProperty("metadata");
        Assert.That(metadata.GetProperty("complete").GetBoolean(), Is.True);
        Assert.That(metadata.GetProperty("resourcesRemoved").GetBoolean(), Is.True);
        Assert.That(metadata.GetProperty("initialPermissions").GetRawText(), Is.EqualTo(metadata.GetProperty("finalPermissions").GetRawText()));
        var count = 0;
        foreach (var observation in root.GetProperty("cases").EnumerateArray())
        foreach (var exchange in observation.GetProperty("exchanges").EnumerateArray())
        {
            count++;
            Assert.That(exchange.GetProperty("actor").GetString(), Is.EqualTo("admin"));
            Assert.That(exchange.GetProperty("request").GetProperty("headers").TryGetProperty("authorization", out _), Is.False);
            foreach (var entity in new[] { exchange.GetProperty("request"), exchange.GetProperty("response") })
            {
                var bytes = Convert.FromBase64String(entity.GetProperty("bodyBase64").GetString()!);
                Assert.That(bytes.Length, Is.EqualTo(entity.GetProperty("bodyBytes").GetInt32()));
                Assert.That(Convert.ToHexStringLower(SHA256.HashData(bytes)), Is.EqualTo(entity.GetProperty("bodySha256").GetString()));
            }
        }
        Assert.That(count, Is.EqualTo(root.GetProperty("exchange_count").GetInt32()));
    }
}
