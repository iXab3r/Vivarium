using System.Text.Json;

namespace Vivarium.Tests.TeamCity;

[Category("TeamCityReplayControl")]
public sealed class BuildLifecycleObservationTests
{
    [Test]
    public void Reference_allocates_number_and_running_state_before_a_paused_owned_Agent_receives_work()
    {
        using var source = JsonDocument.Parse(File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory,
            "TeamCity/Reference/claimed-counter-observations.json")));
        var receipt = source.RootElement;
        Assert.That(receipt.GetProperty("complete").GetBoolean(), Is.True);
        Assert.That(receipt.GetProperty("counterBefore").GetString(), Is.EqualTo("1"));
        Assert.That(receipt.GetProperty("counterWhilePaused").GetString(), Is.EqualTo("2"));
        var assigned = receipt.GetProperty("stateBeforeUnpause");
        Assert.That(assigned.GetProperty("state").GetString(), Is.EqualTo("running"));
        Assert.That(assigned.GetProperty("status").GetString(), Is.EqualTo("SUCCESS"));
        Assert.That(assigned.GetProperty("number").GetString(), Is.EqualTo("1"));
        Assert.That(assigned.GetProperty("startDate").GetString(), Is.Not.Empty);
        Assert.That(receipt.GetProperty("firstTerminal").GetProperty("number").GetString(), Is.EqualTo("1"));
        Assert.That(receipt.GetProperty("counterAfterCancellation").GetString(), Is.EqualTo("2"));
        Assert.That(receipt.GetProperty("finalBuild").GetProperty("number").GetString(), Is.EqualTo("2"));
        foreach (var name in new[] { "failure", "cleanupFailure", "userCleanupFailure", "resourceCleanupFailure",
            "unpauseCleanupFailure", "authenticationRestoreFailure", "allUsersRestoreFailure", "gateRestoreFailure" })
            Assert.That(receipt.TryGetProperty(name, out _), Is.False, name);
        Assert.That(receipt.GetProperty("authenticationSettingsAfter").GetRawText(), Is.EqualTo(receipt.GetProperty("authenticationSettings").GetRawText()));
        Assert.That(receipt.GetProperty("finalPermissions").GetRawText(), Is.EqualTo(receipt.GetProperty("initialPermissions").GetRawText()));
    }

    [Test]
    public void Reference_distinguishes_queue_numbering_running_outcome_and_completed_cancellation()
    {
        using var source = JsonDocument.Parse(File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory,
            "TeamCity/Reference/r0-access-observations.json")));
        JsonElement[] States(string id)
        {
            var result = new List<JsonElement>();
            var item = source.RootElement.GetProperty("cases").EnumerateArray().Single(c => c.GetProperty("id").GetString() == id);
            foreach (var exchange in item.GetProperty("exchanges").EnumerateArray())
            {
                var response = exchange.GetProperty("response");
                if (response.GetProperty("status").GetInt32() != 200) continue;
                JsonDocument body;
                try { body = JsonDocument.Parse(Convert.FromBase64String(response.GetProperty("bodyBase64").GetString()!)); }
                catch (JsonException) { continue; }
                using (body)
                    if (body.RootElement.ValueKind == JsonValueKind.Object && body.RootElement.TryGetProperty("state", out _))
                        result.Add(body.RootElement.Clone());
            }
            return result.ToArray();
        }
        var success = States("TCREST-R0-SCOPED-RUN-ARTIFACT-001");
        var failed = States("TCREST-R0-SCOPED-NONZERO-EXIT-001");
        var queuedCancel = States("TCREST-R0-SCOPED-QUEUED-CANCEL-001");
        var runningCancel = States("TCREST-R0-SCOPED-RUNNING-CANCEL-001");
        foreach (var states in new[] { success, failed, queuedCancel, runningCancel })
        {
            var queued = states.Where(b => b.GetProperty("state").GetString() == "queued").ToArray();
            Assert.That(queued, Is.Not.Empty);
            Assert.That(queued.All(b => !b.TryGetProperty("number", out _)), Is.True);
        }
        var cancelled = queuedCancel.Last();
        Assert.That(cancelled.GetProperty("number").GetString(), Is.EqualTo("N/A"));
        Assert.That(cancelled.GetProperty("status").GetString(), Is.EqualTo("UNKNOWN"));
        Assert.That(failed.Last().GetProperty("number").GetString(), Is.EqualTo("2"));
        Assert.That(runningCancel.Last().GetProperty("number").GetString(), Is.EqualTo("3"));
        Assert.That(success.Where(b => b.GetProperty("state").GetString() == "running")
            .All(b => b.GetProperty("status").GetString() == "SUCCESS"), Is.True);
        Assert.That(failed.Any(b => b.GetProperty("state").GetString() == "running" && b.GetProperty("status").GetString() == "FAILURE"), Is.True);
        Assert.That(runningCancel.Any(b => b.GetProperty("state").GetString() == "running"
            && b.GetProperty("status").GetString() == "UNKNOWN" && b.TryGetProperty("canceledInfo", out _)), Is.True);
    }
}
