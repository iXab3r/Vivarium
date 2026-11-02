using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Vivarium.Tests.TeamCity;

[Category("TeamCityReplayControl")]
public sealed class VersionedSettingsObservationTests
{
    private static JsonDocument Corpus() => JsonDocument.Parse(File.ReadAllText(Path.Combine(
        TestContext.CurrentContext.TestDirectory, "TeamCity/Reference/versioned-mode-observations.json")));

    [TestCaseSource(typeof(RecordedMutationCases), nameof(RecordedMutationCases.ModeObservations))]
    public async Task Mode_observation_replays_literal_requests_responses_and_bytes(RecordedMutationCase observation)
    {
        using var server = RecordedMutationReplayTests.StartReplay(observation);
        using var http = new HttpClient { BaseAddress = new Uri(server.Url!) };
        await observation.ExecuteAsync(http, replay: true);
        Assert.That(server.LogEntries, Has.Count.EqualTo(observation.Exchanges.Count));
        Assert.That(server.LogEntries.All(entry => entry.MappingGuid is not null), Is.True);
    }

    [Test]
    public void Source_snapshots_and_selected_exchange_accounting_preserve_independent_bytes()
    {
        using var document = Corpus(); var root = document.RootElement;
        Assert.That(root.GetProperty("reference").GetProperty("build").GetString(), Is.EqualTo("222742"));
        var cases = root.GetProperty("cases").EnumerateArray().ToArray();
        Assert.That(cases.Select(c => c.GetProperty("id").GetString()).Distinct().Count(), Is.EqualTo(30));
        Assert.That(cases.Sum(c => c.GetProperty("exchanges").GetArrayLength()), Is.EqualTo(root.GetProperty("selectedExchangeCount").GetInt32()));
        Assert.That(root.GetProperty("selectedExchangeCount").GetInt32() + root.GetProperty("omittedConsecutiveIdenticalGets").GetInt32(),
            Is.EqualTo(root.GetProperty("originalExchangeCount").GetInt32()));
        foreach (var capture in root.GetProperty("captures").EnumerateArray())
        foreach (var snapshot in capture.GetProperty("sourceSnapshots").EnumerateArray())
        foreach (var file in snapshot.GetProperty("files").EnumerateObject())
        {
            var bytes = Convert.FromBase64String(file.Value.GetProperty("bodyBase64").GetString()!);
            Assert.That(bytes.Length, Is.EqualTo(file.Value.GetProperty("bytes").GetInt32()));
            Assert.That(Convert.ToHexStringLower(SHA256.HashData(bytes)), Is.EqualTo(file.Value.GetProperty("sha256").GetString()));
            var blob = Encoding.UTF8.GetBytes("blob " + bytes.Length + "\0").Concat(bytes).ToArray();
            Assert.That(Convert.ToHexStringLower(SHA1.HashData(blob)), Is.EqualTo(file.Value.GetProperty("gitBlob").GetString()));
        }
    }

    [Test]
    public void Six_observed_mode_cells_distinguish_editability_from_build_settings_selection()
    {
        using var document = Corpus();
        var capture = document.RootElement.GetProperty("captures")[0];
        var cells = capture.GetProperty("observations").EnumerateArray().Where(o => o.GetProperty("label").GetString() == "mode").ToArray();
        Assert.That(cells, Has.Length.EqualTo(6));
        Assert.That(cells.Select(c => (c.GetProperty("mode").GetString(), c.GetProperty("editing").GetBoolean())).Distinct().Count(), Is.EqualTo(6));
        foreach (var cell in cells)
        {
            var editing = cell.GetProperty("editing").GetBoolean();
            var outcome = cell.GetProperty("outcome");
            Assert.That(outcome.GetProperty("returned").GetBoolean(), Is.EqualTo(editing));
            var value = cell.GetProperty("readback").EnumerateArray().Single(p => p.GetProperty("name").GetString() == "env.MODE").GetProperty("value").GetString();
            var requested = "value-" + cell.GetProperty("mode").GetString() + "-" + editing.ToString().ToLowerInvariant();
            if (editing) Assert.That(value, Is.EqualTo(requested));
            else
            {
                Assert.That(outcome.GetProperty("status").GetInt32(), Is.EqualTo(500));
                Assert.That(outcome.GetProperty("readOnly").GetBoolean(), Is.True);
                Assert.That(value, Is.Not.EqualTo(requested));
                Assert.That(cell.GetProperty("beforeRevision").GetString(), Is.EqualTo(cell.GetProperty("afterRevision").GetString()));
            }
        }
    }

    [Test]
    public void Concurrent_dispatch_evidence_preserves_varying_outcomes_without_inventing_one_winner_rule()
    {
        using var document = Corpus(); var root = document.RootElement;
        foreach (var observation in root.GetProperty("cases").EnumerateArray().Where(c => c.GetProperty("id").GetString()!.Contains("concurrent", StringComparison.Ordinal)))
        {
            var writes = observation.GetProperty("exchanges").EnumerateArray().Where(e =>
                e.GetProperty("request").GetProperty("method").GetString() == "PUT" && e.GetProperty("client").GetString() is "A" or "B").ToArray();
            Assert.That(writes, Has.Length.EqualTo(2));
            Assert.That(writes.Max(e => e.GetProperty("sentSequence").GetInt32()), Is.LessThan(writes.Min(e => e.GetProperty("completedSequence").GetInt32())));
            foreach (var write in writes)
            {
                Assert.That(write.GetProperty("response").GetProperty("status").GetInt32(), Is.EqualTo(200));
                Assert.That(write.GetProperty("request").GetProperty("headers").TryGetProperty("if-match", out _), Is.False);
                Assert.That(write.GetProperty("request").GetProperty("headers").TryGetProperty("idempotency-key", out _), Is.False);
            }
            Assert.That(observation.GetProperty("omittedConsecutiveIdenticalGets").GetInt32(), Is.Zero);
        }
        var observations = root.GetProperty("captures")[1].GetProperty("observations").EnumerateArray().ToArray();
        var counts = observations.Where(o => o.GetProperty("label").GetString() == "disabled-steps-final")
            .Select(o => o.GetProperty("observed").GetArrayLength()).ToArray();
        Assert.That(counts, Has.Length.EqualTo(10));
        Assert.That(counts, Does.Contain(2).And.Contain(4), "One transcript cannot define a deterministic concurrent replacement result.");
        foreach (var (label, expected) in new[] { ("sequential-A", new[] { "sequential-A-one", "sequential-A-two" }), ("sequential-B", new[] { "sequential-B-two", "sequential-B-one" }) })
            Assert.That(observations.Single(o => o.GetProperty("label").GetString() == label).GetProperty("observed").EnumerateArray().Select(s => s.GetProperty("name").GetString()), Is.EqualTo(expected));
    }

    [Test]
    public void Used_context_changes_effective_settings_without_changing_the_product_source_revision()
    {
        using var document = Corpus(); var root = document.RootElement;
        var capture = root.GetProperty("captures")[0];
        var observations = capture.GetProperty("observations").EnumerateArray().ToArray();
        Assert.That(observations.Single(o => o.GetProperty("label").GetString() == "context-unused").GetProperty("outcome").GetProperty("returned").GetBoolean(), Is.True);
        foreach (var label in new[] { "context-used", "context-default" })
        {
            var outcome = observations.Single(o => o.GetProperty("label").GetString() == label).GetProperty("outcome");
            Assert.That(outcome.GetProperty("status").GetInt32(), Is.EqualTo(500));
            Assert.That(outcome.GetProperty("readOnly").GetBoolean(), Is.True);
        }
        var snapshots = capture.GetProperty("sourceSnapshots").EnumerateArray().Where(s =>
            s.GetProperty("label").GetString() is "use-context-in-computed-parameter" or "context-used-edit" or "context-value-changed" or "context-default-edit").ToArray();
        Assert.That(snapshots, Has.Length.EqualTo(4));
        Assert.That(snapshots.Select(s => s.GetProperty("revision").GetString()).Distinct().Count(), Is.EqualTo(1));
        var transcript = root.GetProperty("cases").EnumerateArray().Single(c => c.GetProperty("id").GetString() == "versioned-modes:dsl-context-usage-and-mutability");
        var values = new List<string>();
        foreach (var exchange in transcript.GetProperty("exchanges").EnumerateArray().Where(e =>
            e.GetProperty("request").GetProperty("method").GetString() == "GET" && e.GetProperty("request").GetProperty("path").GetString()!.EndsWith("/parameters", StringComparison.Ordinal)))
        {
            using var body = JsonDocument.Parse(Convert.FromBase64String(exchange.GetProperty("response").GetProperty("bodyBase64").GetString()!));
            foreach (var p in body.RootElement.GetProperty("property").EnumerateArray().Where(p => p.GetProperty("name").GetString() == "env.MODE"))
                if (p.GetProperty("value").GetString() is { } value && !values.Contains(value)) values.Add(value);
        }
        Assert.That(values.Where(v => v.StartsWith("ctx-", StringComparison.Ordinal)), Is.EqualTo(new[] { "ctx-a", "ctx-b", "ctx-default" }));
    }
}
