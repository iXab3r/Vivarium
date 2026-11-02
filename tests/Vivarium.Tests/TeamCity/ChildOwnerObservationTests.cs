using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Vivarium.Tests.TeamCity;

[Category("TeamCityReplayControl")]
public sealed class ChildOwnerObservationTests
{
    private static JsonDocument Corpus() => JsonDocument.Parse(File.ReadAllText(Path.Combine(
        TestContext.CurrentContext.TestDirectory, "TeamCity/Reference/child-owner-observations.json")));

    [TestCaseSource(typeof(RecordedMutationCases), nameof(RecordedMutationCases.ChildOwnerObservations))]
    public async Task Child_owner_observation_replays_literal_requests_and_effects(RecordedMutationCase observation)
    {
        using var server = RecordedMutationReplayTests.StartReplay(observation);
        using var http = new HttpClient { BaseAddress = new Uri(server.Url!) };
        await observation.ExecuteAsync(http, replay: true);
        Assert.That(server.LogEntries, Has.Count.EqualTo(observation.Exchanges.Count));
        Assert.That(server.LogEntries.All(entry => entry.MappingGuid is not null), Is.True);
    }

    [Test]
    public void Disabled_child_ownership_survives_fenced_parent_export_and_explicit_source_reintroduction()
    {
        using var document = Corpus(); var metadata = document.RootElement.GetProperty("metadata");
        var observations = metadata.GetProperty("observations").EnumerateArray().ToArray();
        JsonElement Observation(string label) => observations.Single(o => o.GetProperty("label").GetString() == label);
        var scope = Observation("child-scope");
        Assert.That(scope.GetProperty("inherited").GetProperty("status").GetInt32(), Is.EqualTo(500));
        Assert.That(scope.GetProperty("inherited").GetProperty("readOnly").GetBoolean(), Is.True);
        Assert.That(scope.GetProperty("local").GetProperty("returned").GetBoolean(), Is.True);
        var exported = Observation("child-after-fenced-parent-export");
        Assert.That(exported.GetProperty("sourceIncludesLocalChildWrite").GetBoolean(), Is.False);
        Assert.That(exported.GetProperty("sourceIncludesOriginalChildSeed").GetBoolean(), Is.False);
        var reintroduced = Observation("child-after-explicit-source-reintroduction");
        Assert.That(reintroduced.GetProperty("observed").GetProperty("message").GetString(), Does.StartWith("Changes from VCS are applied to project settings,"));
        Assert.That(reintroduced.GetProperty("childSettings").GetProperty("result").GetProperty("synchronizationMode").GetString(), Is.EqualTo("disabled"));
        var child = reintroduced.GetProperty("childReadback").GetProperty("result").EnumerateArray().ToArray();
        Assert.That(child.Single(p => p.GetProperty("name").GetString() == "env.CHILD").GetProperty("value").GetString(), Is.EqualTo("disabled-child-write"));
        Assert.That(child.Single(p => p.GetProperty("name").GetString() == "env.MODE").GetProperty("inherited").GetBoolean(), Is.True);
        var snapshots = metadata.GetProperty("sourceSnapshots").EnumerateArray().ToArray();
        string Source(string label) => Encoding.UTF8.GetString(Convert.FromBase64String(snapshots.Single(s => s.GetProperty("label").GetString() == label)
            .GetProperty("files").GetProperty(".teamcity/settings.kts").GetProperty("bodyBase64").GetString()!));
        Assert.That(Source("initial-applied"), Does.Contain("child-seed"));
        Assert.That(Source("parent-write-after-child-disabled"), Does.Not.Contain("object Child : Project"));
        Assert.That(Source("after-child-reintroduction"), Does.Contain("ancestor-reintroduced-child-value"));
    }

    [Test]
    public void Child_owner_capture_keeps_entities_source_revisions_and_private_cleanup()
    {
        using var document = Corpus(); var root = document.RootElement; var metadata = root.GetProperty("metadata");
        Assert.That(metadata.GetProperty("complete").GetBoolean(), Is.True);
        Assert.That(metadata.GetProperty("initialPermissions").GetRawText(), Is.EqualTo(metadata.GetProperty("finalPermissions").GetRawText()));
        Assert.That(metadata.GetProperty("resourceCleanup").GetString(), Does.Contain("removed"));
        var count = 0;
        foreach (var observation in root.GetProperty("cases").EnumerateArray())
        foreach (var exchange in observation.GetProperty("exchanges").EnumerateArray())
        {
            count++;
            Assert.That(exchange.GetProperty("request").GetProperty("headers").TryGetProperty("authorization", out _), Is.False);
            foreach (var entity in new[] { exchange.GetProperty("request"), exchange.GetProperty("response") })
            {
                var bytes = Convert.FromBase64String(entity.GetProperty("bodyBase64").GetString()!);
                Assert.That(bytes.Length, Is.EqualTo(entity.GetProperty("bodyBytes").GetInt32()));
                Assert.That(Convert.ToHexStringLower(SHA256.HashData(bytes)), Is.EqualTo(entity.GetProperty("bodySha256").GetString()));
            }
        }
        foreach (var snapshot in metadata.GetProperty("sourceSnapshots").EnumerateArray())
        foreach (var file in snapshot.GetProperty("files").EnumerateObject())
        {
            var bytes = Convert.FromBase64String(file.Value.GetProperty("bodyBase64").GetString()!);
            Assert.That(bytes.Length, Is.EqualTo(file.Value.GetProperty("bytes").GetInt32()));
            Assert.That(Convert.ToHexStringLower(SHA256.HashData(bytes)), Is.EqualTo(file.Value.GetProperty("sha256").GetString()));
        }
        Assert.That(count, Is.EqualTo(root.GetProperty("exchange_count").GetInt32()));
    }
}
