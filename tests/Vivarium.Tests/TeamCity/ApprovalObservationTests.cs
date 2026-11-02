using System.Security.Cryptography;
using System.Text.Json;

namespace Vivarium.Tests.TeamCity;

[Category("TeamCityReplayControl")]
public sealed class ApprovalObservationTests
{
    private static JsonDocument Corpus() => JsonDocument.Parse(File.ReadAllText(Path.Combine(
        TestContext.CurrentContext.TestDirectory, "TeamCity/Reference/approval-observations.json")));

    [TestCaseSource(typeof(RecordedMutationCases), nameof(RecordedMutationCases.ApprovalObservations))]
    public async Task Approval_observation_replays_literal_requests_and_effects(RecordedMutationCase observation)
    {
        using var server = RecordedMutationReplayTests.StartReplay(observation);
        using var http = new HttpClient { BaseAddress = new Uri(server.Url!) };
        await observation.ExecuteAsync(http, replay: true);
        Assert.That(server.LogEntries, Has.Count.EqualTo(observation.Exchanges.Count));
        Assert.That(server.LogEntries.All(entry => entry.MappingGuid is not null), Is.True);
    }

    [Test]
    public void Approval_capacity_repeat_unknown_user_and_timeout_keep_distinct_reference_facts()
    {
        using var document = Corpus(); var metadata = document.RootElement.GetProperty("metadata");
        var observations = metadata.GetProperty("observations").EnumerateArray().ToArray();
        JsonElement Observation(string label) => observations.Single(o => o.GetProperty("label").GetString() == label);
        var waiting = Observation("Waiting for explicit approval").GetProperty("approval");
        Assert.That(waiting.GetProperty("status").GetString(), Is.EqualTo("waitingForApproval"));
        Assert.That(waiting.GetProperty("canBeApprovedByCurrentUser").GetBoolean(), Is.True);
        Assert.That(metadata.GetProperty("approved").GetProperty("status").GetString(), Is.EqualTo("approved"));
        var noAgent = Observation("Approved but still queued without an Agent").GetProperty("build");
        Assert.That(noAgent.GetProperty("state").GetString(), Is.EqualTo("queued"));
        Assert.That(noAgent.GetProperty("waitReason").GetString(), Is.EqualTo("There are no idle compatible agents which can run this build"));
        Assert.That(metadata.GetProperty("repeated").GetProperty("returned").GetBoolean(), Is.False);
        Assert.That(metadata.GetProperty("repeated").GetProperty("status").GetInt32(), Is.EqualTo(400));
        var unknown = Observation("Unknown approver literal state").GetProperty("approval");
        Assert.That(unknown.GetProperty("configurationValid").GetBoolean(), Is.True, "Unknown user is not the malformed-rule variant.");
        Assert.That(unknown.GetProperty("canBeApprovedByCurrentUser").GetBoolean(), Is.False);
        Assert.That(metadata.GetProperty("invalidApprove").GetProperty("result").GetProperty("status").GetString(), Is.EqualTo("waitingForApproval"));
        var timedOut = metadata.GetProperty("timeoutBuild");
        Assert.That(timedOut.GetProperty("state").GetString(), Is.EqualTo("finished"));
        Assert.That(timedOut.GetProperty("status").GetString(), Is.EqualTo("FAILURE"));
        Assert.That(timedOut.GetProperty("failedToStart").GetBoolean(), Is.True);
        Assert.That(timedOut.TryGetProperty("canceledInfo", out _), Is.False);
        Assert.That(metadata.GetProperty("timeoutApproval").GetProperty("result").GetProperty("status").GetString(), Is.EqualTo("timedOut"));
        Assert.That(metadata.GetProperty("timeoutProblems")[0].GetProperty("type").GetString(), Is.EqualTo("PREPARATION_FAILURE_TYPE"));
    }

    [Test]
    public void Approval_capture_preserves_literal_bytes_and_restores_private_gates()
    {
        using var document = Corpus(); var root = document.RootElement; var metadata = root.GetProperty("metadata");
        Assert.That(metadata.GetProperty("complete").GetBoolean(), Is.True);
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
