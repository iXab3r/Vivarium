using NUnit.Framework;

namespace PayloadTests;

public class PayloadSmokeTests
{
    [Test]
    public void Arithmetic_survives_virtualization()
    {
        Assert.That(2 + 2, Is.EqualTo(4));
    }

    [Test]
    public void Filesystem_is_writable_in_the_workdir()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vivarium-payload-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, "hello from a pristine machine");
        Assert.That(File.ReadAllText(path), Does.Contain("pristine"));
        File.Delete(path);
    }

    [Test]
    public void Vivarium_env_is_visible_when_provided()
    {
        // Under a real Vivarium build the agent injects VIVARIUM_* (D3); standalone runs skip this.
        var buildId = Environment.GetEnvironmentVariable("VIVARIUM_BUILD_ID");
        if (buildId == null)
        {
            Assert.Ignore("not running under a Vivarium agent");
        }

        Assert.That(buildId, Is.Not.Empty);
    }
}
