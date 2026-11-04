using System.Security.Cryptography;
using System.Text.Json;

namespace Vivarium.Tests.TeamCity;

[Category("TeamCityReplayControl")]
public sealed class QueuedAuthorityObservationTests
{
    [Test]
    public void Accepted_build_executes_after_verified_run_denial_with_scoped_authorization_enabled()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory,
            "TeamCity/Reference/queued-authority-observations.json")));
        var root = document.RootElement;
        Assert.That(root.GetProperty("complete").GetBoolean(), Is.True);
        Assert.That(root.TryGetProperty("failure", out _), Is.False);
        Assert.That(root.GetProperty("scopedAuthenticationSettings").GetProperty("perProjectPermissions").GetBoolean(), Is.True);
        Assert.That(root.GetProperty("afterWithdrawalRunStatus").GetInt32(), Is.EqualTo(403));
        Assert.That(root.GetProperty("userRolesAfterWithdrawal").GetArrayLength(), Is.Zero);
        Assert.That(root.GetProperty("allUsersRolesAfterWithdrawal").GetArrayLength(), Is.Zero);
        var runs = root.GetProperty("cases")[0].GetProperty("exchanges").EnumerateArray()
            .Where(e => e.GetProperty("actor").GetString() == "owned-account"
                && e.GetProperty("request").GetProperty("method").GetString() == "POST").ToArray();
        Assert.That(runs, Has.Length.EqualTo(2));
        Assert.That(runs.Select(e => e.GetProperty("response").GetProperty("status").GetInt32()), Is.EqualTo(new[] { 200, 403 }));
        Assert.That(runs.Select(e => e.GetProperty("request").GetProperty("bodySha256").GetString()).Distinct().ToArray(), Has.Length.EqualTo(1));
        var final = root.GetProperty("finalBuild");
        Assert.That(final.GetProperty("id").GetInt64(), Is.EqualTo(root.GetProperty("buildId").GetInt64()));
        Assert.That(final.GetProperty("state").GetString(), Is.EqualTo("finished"));
        Assert.That(final.GetProperty("status").GetString(), Is.EqualTo("SUCCESS"));
        var artifact = root.GetProperty("artifact");
        var expected = "owned-queued-authority\n"u8.ToArray();
        Assert.That(artifact.GetProperty("status").GetInt32(), Is.EqualTo(200));
        Assert.That(artifact.GetProperty("length").GetInt32(), Is.EqualTo(expected.Length));
        Assert.That(artifact.GetProperty("sha256").GetString(), Is.EqualTo(Convert.ToHexStringLower(SHA256.HashData(expected))));
        Assert.That(artifact.GetProperty("matchesPreparedLiteral").GetBoolean(), Is.True);
        Assert.That(root.GetProperty("viewerArtifact").GetProperty("status").GetInt32(), Is.EqualTo(200));
        Assert.That(root.GetProperty("viewerArtifact").GetProperty("sha256").GetString(), Is.EqualTo(Convert.ToHexStringLower(SHA256.HashData(expected))));
        Assert.That(root.GetProperty("authenticationSettingsAfter").GetRawText(), Is.EqualTo(root.GetProperty("authenticationSettings").GetRawText()));
        Assert.That(root.GetProperty("allUsersRolesAfter").GetRawText(), Is.EqualTo(root.GetProperty("allUsersRolesBefore").GetRawText()));
        foreach (var exchange in root.GetProperty("setup").EnumerateArray().Concat(root.GetProperty("cases").EnumerateArray())
            .Concat(root.GetProperty("cleanup").EnumerateArray()).Where(g => g.TryGetProperty("exchanges", out _))
            .SelectMany(g => g.GetProperty("exchanges").EnumerateArray()))
            foreach (var side in new[] { "request", "response" })
            {
                var entity = exchange.GetProperty(side);
                var bytes = Convert.FromBase64String(entity.GetProperty("bodyBase64").GetString()!);
                Assert.That(bytes.Length, Is.EqualTo(entity.GetProperty("bodyBytes").GetInt32()));
                Assert.That(Convert.ToHexStringLower(SHA256.HashData(bytes)), Is.EqualTo(entity.GetProperty("bodySha256").GetString()));
                Assert.That(entity.GetProperty("headers").TryGetProperty("authorization", out _), Is.False);
            }
        // The capture's original limits text mentions the setup PAT. The decisive two run requests
        // use the same owned ordinary HTTP-Basic account, not that PAT. No PAT-revocation claim.
    }
}
