using System.Security.Cryptography;
using System.Text.Json;

namespace Vivarium.Tests.TeamCity;

[Category("TeamCityReplayControl")]
public sealed class ContainerRunnerObservationTests
{
    [Test]
    public void Docker_control_retains_actual_process_cancel_and_artifact_observations_with_owned_cleanup()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory,
            "TeamCity/Reference/docker-runner-observations.json")));
        var root = document.RootElement;
        Assert.That(root.GetProperty("kind").GetString(), Is.EqualTo("PINNED_DOCKER_RUNNER_REFERENCE_NOT_PRODUCT_ACCEPTANCE"));
        foreach (var property in new[] { "complete", "ownedResourcesRemoved", "ownedProjectRemoved", "permissionsRestored" })
            Assert.That(root.GetProperty(property).GetBoolean(), Is.True, property);
        var repository = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "Vivarium.slnx"))) repository = repository.Parent;
        Assert.That(CapturedSourceBinding.OriginalCrlfSha256(File.ReadAllBytes(Path.Combine(repository!.FullName,
            "tools/teamcity-reference/docker-runner-capture.mjs"))), Is.EqualTo(root.GetProperty("sourceSha256").GetString()));
        var cases = root.GetProperty("cases").EnumerateArray().ToDictionary(c => c.GetProperty("id").GetString()!);
        Assert.That(cases.Count, Is.EqualTo(4));
        foreach (var (id, value) in cases)
        {
            Assert.That(value.GetProperty("terminalProven").GetBoolean(), Is.True);
            Assert.That(value.GetProperty("remainingContainers").GetString(), Is.Empty);
            Assert.That(value.GetProperty("counterBefore").GetString(), Is.EqualTo("1"));
            Assert.That(value.GetProperty("counterAfter").GetString(), Is.EqualTo("2"));
            var terminal = value.GetProperty("terminalObservation");
            Assert.That(terminal.GetProperty("failedToStart").GetBoolean(), Is.False);
            Assert.That(terminal.GetProperty("status").GetString(), Is.EqualTo(id switch {
                "docker:success" => "SUCCESS", "docker:cancel" => "UNKNOWN", _ => "FAILURE" }));
            foreach (var exchange in value.GetProperty("exchanges").EnumerateArray())
            foreach (var side in new[] { "request", "response" })
            {
                var entity = exchange.GetProperty(side);
                var bytes = Convert.FromBase64String(entity.GetProperty("bodyBase64").GetString()!);
                Assert.That(bytes.Length, Is.EqualTo(entity.GetProperty("bodyBytes").GetInt32()));
                Assert.That(Convert.ToHexStringLower(SHA256.HashData(bytes)), Is.EqualTo(entity.GetProperty("bodySha256").GetString()));
            }
        }
        Assert.That(cases["docker:cancel"].GetProperty("runningProcessExecutables").EnumerateArray().Select(p => p.GetString()),
            Does.Contain("/usr/local/bin/node"));
        Assert.That(cases["docker:success"].GetProperty("download").GetProperty("sha256").GetString(),
            Is.EqualTo("97dc31e36da90b592250f4e30e01d0364a3abcafa36d57fccaccbaedbd6629c8"));
    }
}
