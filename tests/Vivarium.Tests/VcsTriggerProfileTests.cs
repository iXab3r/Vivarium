using Vivarium.Controller.Builds.Runtime;
using Vivarium.Definitions;

namespace Vivarium.Tests;

public sealed class VcsTriggerProfileTests
{
    private static readonly Trigger Used = new("vcs", "+:<default>", 60, "USE_CUSTOM",
        "-:README.md\n-:AGENTS.md\n-:docs/operations/**\n-:.teamcity/**");

    [Test]
    public void Used_negative_paths_preserve_exact_file_and_directory_boundaries()
    {
        Assert.That(RuntimeVcsChanges.Relevant(["README.md", "AGENTS.md", "docs/operations/a/b.md", ".teamcity/settings.kts"], Used), Is.False);
        Assert.That(RuntimeVcsChanges.Rules(Used with { Type = "vcsTrigger" }), Is.EqualTo(RuntimeVcsChanges.Rules(Used)), "REST and C# trigger spellings share one profile.");
        foreach (var path in new[] { "readme.md", "README.md.cs", "sub/README.md", "docs/operations.cs", "docs/operations-other/a.md", "source.cs" })
            Assert.That(RuntimeVcsChanges.Relevant(["README.md", path], Used), Is.True, path);
    }

    [Test]
    public void Unsupported_rule_languages_cannot_silently_suppress_or_trigger_changes()
    {
        foreach (var rules in new[] { "+:**", "-:../private", "-:/absolute", "-:directory\\file", "-:name?", "-:**", "-:dir//file", "-:dir/**/file" })
            Assert.Throws<InvalidDataException>(() => RuntimeVcsChanges.Rules(Used with { Rules = rules }), rules);
        Assert.Throws<InvalidDataException>(() => RuntimeVcsChanges.Rules(Used with { BranchFilter = "+:*" }));
        Assert.Throws<InvalidDataException>(() => RuntimeVcsChanges.Rules(Used with { QuietPeriodMode = "USE_DEFAULT" }));
    }
}
