using System.Security.Cryptography;
using System.Text.Json;

namespace Vivarium.Tests.TeamCity;

[Category("TeamCityReplayControl")]
public sealed class PowerShellProfileObservationTests
{
    [Test]
    public void PowerShell_profile_uses_the_pinned_vendor_code_and_keeps_its_control_boundary_explicit()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory,
            "TeamCity/Reference/powershell-profile-observations.json")));
        var root = document.RootElement;
        Assert.That(root.GetProperty("kind").GetString(), Is.EqualTo("PINNED_VENDOR_PLUGIN_CONTROL_NOT_REST_OR_WINDOWS_EXECUTION"));
        Assert.That(root.GetProperty("complete").GetBoolean(), Is.True);
        Assert.That(root.GetProperty("ownedResourcesRemoved").GetBoolean(), Is.True);
        Assert.That(root.GetProperty("pluginBuild").GetString(), Is.EqualTo("222742"));
        Assert.That(root.GetProperty("cleanup").EnumerateArray().All(c => c.GetProperty("removed").GetBoolean()), Is.True);
        var bytes = Convert.FromBase64String(root.GetProperty("stdoutBase64").GetString()!);
        Assert.That(Convert.ToHexStringLower(SHA256.HashData(bytes)), Is.EqualTo(root.GetProperty("stdoutSha256").GetString()));
        var repository = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "Vivarium.slnx"))) repository = repository.Parent;
        foreach (var (file, expected) in new[] {
            ("PowerShellProbe.java", root.GetProperty("inputs").GetProperty("PowerShellProbe.java").GetString()),
            ("powershell-profile-capture.py", root.GetProperty("producerSha256").GetString()) })
            Assert.That(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(repository!.FullName, "tools/teamcity-reference", file)))), Is.EqualTo(expected));
        Assert.That(root.GetProperty("observation").GetProperty("edition").GetString(), Is.EqualTo("Desktop"));
        Assert.That(root.GetProperty("observation").GetProperty("executable").GetString(), Is.EqualTo("powershell.exe"));
    }
}
