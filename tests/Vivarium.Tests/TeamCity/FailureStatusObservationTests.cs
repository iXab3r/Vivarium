using System.Security.Cryptography;
using System.Text.Json;
using Vivarium.Controller.Builds.Runtime;

namespace Vivarium.Tests.TeamCity;

[Category("TeamCityReplayControl")]
public sealed class FailureStatusObservationTests
{
    [Test]
    public void Literal_sequential_exit_observations_distinguish_repetition_recovery_and_changed_exit_code()
    {
        using var source = JsonDocument.Parse(File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory,
            "TeamCity/Reference/failure-status-observations.json")));
        var receipt = source.RootElement;
        Assert.That(receipt.GetProperty("complete").GetBoolean(), Is.True);
        foreach (var name in new[] { "failure", "cleanupFailure", "userCleanupFailure", "resourceCleanupFailure",
            "unpauseCleanupFailure", "authenticationRestoreFailure", "allUsersRestoreFailure", "gateRestoreFailure" })
            Assert.That(receipt.TryGetProperty(name, out _), Is.False, name);
        var observations = receipt.GetProperty("failureStatuses").EnumerateArray().ToArray();
        Assert.That(observations.Select(o => o.GetProperty("mode").GetString()), Is.EqualTo(new[] { "7", "7", "0", "7", "8", "8" }));
        var repeated = new[] { false, true, false, false, false, true };
        for (var index = 0; index < observations.Length; index++)
        {
            var mode = int.Parse(observations[index].GetProperty("mode").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
            var actual = mode == 0 ? "Success" : RuntimeBuildStatus.ExitFailure(mode, "Owned literal producer", repeated[index]);
            Assert.That(actual, Is.EqualTo(observations[index].GetProperty("build").GetProperty("statusText").GetString()));
        }
        foreach (var item in receipt.GetProperty("cases").EnumerateArray())
        foreach (var exchange in item.GetProperty("exchanges").EnumerateArray())
        {
            var response = exchange.GetProperty("response");
            var bytes = Convert.FromBase64String(response.GetProperty("bodyBase64").GetString()!);
            Assert.That(Convert.ToHexStringLower(SHA256.HashData(bytes)), Is.EqualTo(response.GetProperty("bodySha256").GetString()));
        }
        Assert.That(receipt.GetProperty("authenticationSettingsAfter").GetRawText(), Is.EqualTo(receipt.GetProperty("authenticationSettings").GetRawText()));
        Assert.That(receipt.GetProperty("finalPermissions").GetRawText(), Is.EqualTo(receipt.GetProperty("initialPermissions").GetRawText()));
    }
}
