using Vivarium.Controller.Scheduling;

namespace Vivarium.Tests;

[TestFixture]
public class CompatibilityMatcherTests
{
    private static readonly IReadOnlyDictionary<string, string> WindowsParameters =
        new Dictionary<string, string>
        {
            ["os.family"] = "windows",
            ["os.build"] = "19045",
            ["arch"] = "x64",
        };

    [Test]
    public void Equality_selector_matches_reported_parameter()
    {
        var result = AgentCompatibilityMatcher.Match(
            "os.family == windows",
            "win10-box",
            WindowsParameters);

        Assert.Multiple(() =>
        {
            Assert.That(result.Compatible, Is.True);
            Assert.That(result.Reasons, Is.Empty);
        });
    }

    [Test]
    public void Name_selector_uses_agent_name_instead_of_reported_parameter()
    {
        var parameters = new Dictionary<string, string>(WindowsParameters)
        {
            ["name"] = "spoofed-name",
        };

        var result = AgentCompatibilityMatcher.Match("name == macbook", "macbook", parameters);

        Assert.That(result.Compatible, Is.True);
    }

    [Test]
    public void Conjunction_reports_every_unmet_requirement_in_source_order()
    {
        var result = AgentCompatibilityMatcher.Match(
            "os.family == linux && arch == arm64 && interactive == true",
            "win10-box",
            WindowsParameters);

        Assert.Multiple(() =>
        {
            Assert.That(result.Compatible, Is.False);
            Assert.That(result.Reasons, Is.EqualTo(new[]
            {
                "Parameter 'os.family' is 'windows', expected 'linux'.",
                "Parameter 'arch' is 'x64', expected 'arm64'.",
                "Agent does not report parameter 'interactive'.",
            }));
        });
    }

    [TestCase("os.family != windows")]
    [TestCase("os.family == windows || arch == x64")]
    [TestCase("os.family == windows &&")]
    [TestCase("name == \"macbook\"")]
    [TestCase("os.family")]
    public void Unsupported_or_malformed_syntax_fails_closed(string selector)
    {
        var result = AgentCompatibilityMatcher.Match(selector, "win10-box", WindowsParameters);

        Assert.Multiple(() =>
        {
            Assert.That(result.Compatible, Is.False);
            Assert.That(result.Reasons, Has.Count.EqualTo(1));
            Assert.That(result.Reasons[0], Does.Contain("Invalid agent selector"));
            Assert.That(result.Reasons[0], Does.Contain("Only equality clauses joined by '&&'"));
        });
    }

    [Test]
    public void Empty_selector_matches_every_agent()
    {
        var result = AgentCompatibilityMatcher.Match("  ", "win10-box", WindowsParameters);

        Assert.That(result.Compatible, Is.True);
    }

    [Test]
    public void Matching_is_ordinal_and_case_sensitive()
    {
        var result = AgentCompatibilityMatcher.Match(
            "os.family == Windows",
            "win10-box",
            WindowsParameters);

        Assert.That(result.Compatible, Is.False);
    }
}
