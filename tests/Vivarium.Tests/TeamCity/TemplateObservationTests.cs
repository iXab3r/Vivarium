using System.Security.Cryptography;
using System.Text.Json;

namespace Vivarium.Tests.TeamCity;

[Category("TeamCityReplayControl")]
public sealed class TemplateObservationTests
{
    private static JsonDocument Corpus() => JsonDocument.Parse(File.ReadAllText(Path.Combine(
        TestContext.CurrentContext.TestDirectory, "TeamCity/Reference/template-observations.json")));

    [TestCaseSource(typeof(RecordedMutationCases), nameof(RecordedMutationCases.TemplateObservations))]
    public async Task Parameter_template_observation_replays_literal_mutations_and_readbacks(RecordedMutationCase observation)
    {
        using var server = RecordedMutationReplayTests.StartReplay(observation);
        using var http = new HttpClient { BaseAddress = new Uri(server.Url!) };
        await observation.ExecuteAsync(http, replay: true);
        Assert.That(server.LogEntries, Has.Count.EqualTo(observation.Exchanges.Count));
        Assert.That(server.LogEntries.All(entry => entry.MappingGuid is not null), Is.True);
    }

    [Test]
    public void Independent_reference_retains_project_priority_template_order_and_local_reset()
    {
        using var document = Corpus(); var root = document.RootElement;
        var observations = root.GetProperty("metadata").GetProperty("observations").EnumerateArray().ToArray();
        Assert.That(observations.Select(o => o.GetProperty("value").GetProperty("value").GetString()),
            Is.EqualTo(new[] { "A", "B", "LOCAL", "B", "B2", "PROJECT", "PROJECT", "LOCAL", "PROJECT" }));
        Assert.That(observations.Select(o => o.GetProperty("value").GetProperty("inherited").GetBoolean()),
            Is.EqualTo(new[] { true, true, false, true, true, true, true, false, true }));
        var templateIds = root.GetProperty("metadata").GetProperty("templates").EnumerateArray().Select(t => t.GetString()).ToArray();
        Assert.That(observations[0].GetProperty("templates").EnumerateArray().Select(t => t.GetProperty("id").GetString()), Is.EqualTo(templateIds));
        Assert.That(observations[1].GetProperty("templates").EnumerateArray().Select(t => t.GetProperty("id").GetString()), Is.EqualTo(templateIds.Reverse()));
        Assert.That(root.GetProperty("metadata").GetProperty("complete").GetBoolean(), Is.True);
        Assert.That(root.GetProperty("metadata").GetProperty("initialPermissions").GetRawText(),
            Is.EqualTo(root.GetProperty("metadata").GetProperty("finalPermissions").GetRawText()));
    }

    [Test]
    public void Every_literal_template_exchange_keeps_its_entity_hash_and_safe_actor()
    {
        using var document = Corpus(); var root = document.RootElement; var count = 0;
        foreach (var observation in root.GetProperty("cases").EnumerateArray())
        foreach (var exchange in observation.GetProperty("exchanges").EnumerateArray())
        {
            count++; Assert.That(exchange.GetProperty("actor").GetString(), Is.EqualTo("admin"));
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
