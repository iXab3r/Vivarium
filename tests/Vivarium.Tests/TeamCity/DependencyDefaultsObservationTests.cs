using System.Security.Cryptography;
using System.Text.Json;

namespace Vivarium.Tests.TeamCity;

// Pinned independent source facts, not a product conformance verdict.
[Category("TeamCityReplayControl")]
public sealed class DependencyDefaultsObservationTests
{
    [Test]
    public void Legacy_CSharp_snapshot_canonical_bytes_survive_optional_reuse_declarations()
    {
        const string original = "{\"configuration\":\"P_Prepare\",\"failure\":\"CANCEL\",\"cancel\":\"CANCEL\"}";
        var options = Vivarium.Controller.Catalog.DefinitionValidation.Json;
        var old = JsonSerializer.Deserialize<Vivarium.Definitions.Snapshot>(original, options)!;
        Assert.That(old.ReuseBuilds, Is.False);
        Assert.That(old.SuccessfulOnly, Is.False);
        Assert.That(JsonSerializer.Serialize(old, options), Is.EqualTo(original), "Old immutable collected plan hashes must remain valid.");
        var explicitReuse = old with { ReuseBuilds = true, SuccessfulOnly = true };
        Assert.That(JsonSerializer.Deserialize<Vivarium.Definitions.Snapshot>(JsonSerializer.Serialize(explicitReuse, options), options), Is.EqualTo(explicitReuse));
    }

    [Test]
    public void Omitted_snapshot_properties_and_missing_artifact_selector_retain_the_observed_reference_contract()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory,
            "TeamCity/Reference/dependency-defaults-observations.json")));
        var root = document.RootElement;
        Assert.That(root.GetProperty("complete").GetBoolean(), Is.True);
        Assert.That(root.GetProperty("ownedProjectRemoved").GetBoolean(), Is.True);
        Assert.That(root.GetProperty("permissionsRestored").GetBoolean(), Is.True);
        Assert.That(root.GetProperty("image").GetString(), Is.EqualTo("sha256:e62c922b94edbef8536bb0d514a193a38bd480fd3855fdac7a649e0a5f8a550a"));
        var properties = root.GetProperty("snapshot").GetProperty("properties").GetProperty("property")
            .EnumerateArray().ToDictionary(p => p.GetProperty("name").GetString()!, p => p.GetProperty("value").GetString());
        Assert.That(properties, Is.EquivalentTo(new Dictionary<string, string>
        {
            ["run-build-if-dependency-failed"] = "MAKE_FAILED_TO_START",
            ["run-build-if-dependency-failed-to-start"] = "MAKE_FAILED_TO_START",
            ["run-build-on-the-same-agent"] = "false", ["sync-revisions"] = "true",
            ["take-started-build-with-same-revisions"] = "false", ["take-successful-builds-only"] = "false",
        }));
        Assert.That(root.GetProperty("artifactReadback").GetProperty("count").GetInt32(), Is.Zero);
        var exchanges = root.GetProperty("exchanges").EnumerateArray().ToArray();
        var rejected = exchanges.Single(e => e.GetProperty("request").GetProperty("method").GetString() == "POST"
            && e.GetProperty("request").GetProperty("path").GetString()!.EndsWith("/artifact-dependencies", StringComparison.Ordinal));
        Assert.That(rejected.GetProperty("response").GetProperty("status").GetInt32(), Is.EqualTo(400));
        using var error = JsonDocument.Parse(Convert.FromBase64String(rejected.GetProperty("response").GetProperty("bodyBase64").GetString()!));
        Assert.That(error.RootElement.GetProperty("errors")[0].GetProperty("message").GetString(),
            Is.EqualTo("Error adding artifact dependency: jetbrains.buildServer.server.rest.errors.BadRequestException: Missing or empty artifact dependency property 'revisionName'. Should contain one of supported values."));
        foreach (var exchange in exchanges)
        foreach (var side in new[] { "request", "response" })
        {
            var entity = exchange.GetProperty(side);
            Assert.That(entity.GetProperty("headers").TryGetProperty("authorization", out _), Is.False);
            var bytes = Convert.FromBase64String(entity.GetProperty("bodyBase64").GetString()!);
            Assert.That(Convert.ToHexStringLower(SHA256.HashData(bytes)), Is.EqualTo(entity.GetProperty("bodySha256").GetString()));
            if (side == "response") Assert.That(bytes.Length, Is.EqualTo(entity.GetProperty("bodyBytes").GetInt32()));
        }
    }
}
