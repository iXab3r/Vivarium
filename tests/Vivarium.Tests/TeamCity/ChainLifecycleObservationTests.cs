using System.Security.Cryptography;
using System.Text.Json;

namespace Vivarium.Tests.TeamCity;

// Literal acquisition controls. Product effects are exercised separately by the real Host cases.
[Category("TeamCityReplayControl")]
public sealed class ChainLifecycleObservationTests
{
    [Test]
    public void Pinned_chain_failure_and_cancellation_source_keeps_complete_safe_literal_exchanges()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory,
            "TeamCity/Reference/chain-lifecycle-observations.json")));
        var root = document.RootElement;
        foreach (var field in new[] { "complete", "ownedProjectRemoved", "ownedResourcesRemoved", "permissionsRestored" })
            Assert.That(root.GetProperty(field).GetBoolean(), Is.True, field);
        Assert.That(root.GetProperty("image").GetString(), Is.EqualTo("sha256:e62c922b94edbef8536bb0d514a193a38bd480fd3855fdac7a649e0a5f8a550a"));
        var cases = root.GetProperty("cases").EnumerateArray().ToArray();
        Assert.That(cases.Select(c => c.GetProperty("id").GetString()), Is.EqualTo(new[] {
            "chain:failure-explicit-cancel-edges", "chain:cancel-upstream-explicit-cancel-edges", "chain:cancel-root-explicit-cancel-edges" }));
        Assert.That(cases[2].GetProperty("finalObservation").EnumerateArray().Select(b => b.GetProperty("state").GetString()),
            Is.EqualTo(new[] { "queued", "queued", "finished" }));
        var exchanges = root.GetProperty("setup").EnumerateArray().Concat(root.GetProperty("cleanup").EnumerateArray())
            .Concat(cases.SelectMany(c => c.GetProperty("exchanges").EnumerateArray()));
        foreach (var exchange in exchanges)
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
