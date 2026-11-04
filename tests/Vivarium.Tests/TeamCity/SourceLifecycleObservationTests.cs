using System.Security.Cryptography;
using System.Text.Json;

namespace Vivarium.Tests.TeamCity;

[Category("TeamCityReplayControl")]
public sealed class SourceLifecycleObservationTests
{
    [TestCase("source-checkout-observations.json", "Error while applying patch", false)]
    [TestCase("source-collection-failure-observations.json", "Unable to collect changes", true)]
    public void Pinned_source_failures_retain_the_counter_revision_and_literal_exchange_boundary(string file, string text, bool unavailable)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory, "TeamCity/Reference", file)));
        var root = document.RootElement;
        foreach (var field in new[] { "complete", "ownedProjectRemoved", "ownedResourcesRemoved", "permissionsRestored" })
            Assert.That(root.GetProperty(field).GetBoolean(), Is.True);
        Assert.That(root.GetProperty("counterBefore").GetString(), Is.EqualTo("1"));
        Assert.That(root.GetProperty("counterAfter").GetString(), Is.EqualTo("2"));
        var scenario = root.GetProperty("cases")[0];
        Assert.That(scenario.GetProperty("terminalProven").GetBoolean(), Is.True);
        var terminal = scenario.GetProperty("terminalObservation");
        Assert.That(terminal.GetProperty("statusText").GetString(), Is.EqualTo(text));
        Assert.That(terminal.GetProperty("status").GetString(), Is.EqualTo("FAILURE"));
        Assert.That(terminal.GetProperty("failedToStart").GetBoolean(), Is.True);
        var version = terminal.GetProperty("revisions").GetProperty("revision")[0].GetProperty("version").GetString();
        Assert.That(version, Is.EqualTo(unavailable ? "N/A" : root.GetProperty("sourceFixture").GetProperty("sourceCommit").GetString()));
        foreach (var exchange in root.GetProperty("setup").EnumerateArray().Concat(root.GetProperty("cleanup").EnumerateArray())
            .Concat(scenario.GetProperty("exchanges").EnumerateArray()))
        foreach (var side in new[] { "request", "response" })
        {
            var entity = exchange.GetProperty(side);
            Assert.That(entity.GetProperty("headers").TryGetProperty("authorization", out _), Is.False);
            var bytes = Convert.FromBase64String(entity.GetProperty("bodyBase64").GetString()!);
            Assert.That(bytes.Length, Is.EqualTo(entity.GetProperty("bodyBytes").GetInt32()));
            Assert.That(Convert.ToHexStringLower(SHA256.HashData(bytes)), Is.EqualTo(entity.GetProperty("bodySha256").GetString()));
        }
    }
}
