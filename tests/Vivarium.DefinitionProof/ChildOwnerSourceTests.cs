using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vivarium.DefinitionProof;

[NonParallelizable]
public sealed class ChildOwnerSourceTests
{
    [Test]
    public async Task Compiled_child_control_owner_survives_parent_source_edits_and_reintroduction()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Vivarium.slnx"))) directory = directory.Parent;
        var root = directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
        var run = Path.Combine(root, "artifacts/us013-child-owner-20261102", "source-run-" + Guid.NewGuid().ToString("N"));
        var parentControl = Path.Combine(run, "parent-control"); var product = Path.Combine(run, "product");
        var childControl = Path.Combine(run, "child-control");
        foreach (var repository in new[] { parentControl, product, childControl })
        {
            Directory.CreateDirectory(repository);
            await SourceCompiler.GitAsync(repository, "init", "-b", "main");
            await SourceCompiler.GitAsync(repository, "config", "core.autocrlf", "false");
            await SourceCompiler.GitAsync(repository, "config", "user.name", "Vivarium P0 proof");
            await SourceCompiler.GitAsync(repository, "config", "user.email", "proof@example.invalid");
        }
        async Task<string> Commit(string repository, string message)
        {
            await SourceCompiler.GitAsync(repository, "add", "--all");
            await SourceCompiler.GitAsync(repository, "commit", "-m", "proof: " + message);
            return await SourceCompiler.GitAsync(repository, "rev-parse", "HEAD");
        }
        await File.WriteAllTextAsync(Path.Combine(parentControl, "Control.g.cs"), """
            using DefinitionProof;
            public static class Control
            {
                public static Project Declare() => new("T3Personal", "_Root", "Parent", "Owner fixture",
                    new("product-proof", ".vivarium/settings/Settings.g.cs", "main", "local-owned", "credential-ref-only"));
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(childControl, "Control.g.cs"), """
            using DefinitionProof;
            public static class Control
            {
                public static Project Declare() => new("T3Personal_Child", "T3Personal", "Child", "Separate control owner", null);
            }
            """);
        var parentSettings = Path.Combine(product, ".vivarium/settings/Settings.g.cs");
        var childSettings = Path.Combine(childControl, ".vivarium/unbound/T3Personal_Child/Settings.g.cs");
        foreach (var path in new[] { parentSettings, childSettings })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(path)!, "UserCode.cs"),
                "public static class UserCode { public static string Evaluate(string expression) => throw new System.InvalidOperationException(expression); }");
        }
        var parentSource = """
            using DefinitionProof;
            public static class Managed
            {
                public static Settings Declare() => new("T3Personal", null, [new("env.MODE", "base", null)],
                    [new("Parent_Build", "Parent", [], [], [], [], [], 0, [])]);
            }
            """;
        var childSource = """
            using DefinitionProof;
            public static class Managed
            {
                public static Settings Declare() => new("T3Personal_Child", null, [],
                    [new("Child_Build", "Child", [new("env.CHILD", "disabled-child-write", null)], [], [], [], [], 0, [])]);
            }
            """;
        await File.WriteAllTextAsync(parentSettings, parentSource);
        await File.WriteAllTextAsync(childSettings, childSource);
        var parentControlCommit = await Commit(parentControl, "parent binding");
        var parentOriginalCommit = await Commit(product, "parent declaration");
        var childOriginalCommit = await Commit(childControl, "child local owner");
        var inputs = Directory.GetFiles(Path.Combine(root, "tools/definition-proof"))
            .Where(path => Path.GetExtension(path) is ".cs" or ".py" or ".csproj")
            .Concat(Directory.GetFiles(Path.Combine(root, "tests/Vivarium.DefinitionProof"), "*.cs"))
            .Append(Path.Combine(root, "tests/Vivarium.DefinitionProof/Vivarium.DefinitionProof.csproj"))
            .Append(Path.Combine(root, "tests/Vivarium.Tests/TeamCity/CurrentController.cs"))
            .Append(Path.Combine(root, "tests/Vivarium.Tests/TeamCity/Reference/child-owner-observations.json"))
            .Append(Path.Combine(root, "tools/teamcity-reference/reference/child-owner-probe.mjs.txt"))
            .Append(Path.Combine(root, "global.json")).Append(Path.Combine(root, "Directory.Build.props"))
            .ToDictionary(path => Path.GetRelativePath(root, path), Hash);
        var parentCompiler = new SourceCompiler(new(root, parentControl, product, Path.Combine(run, "parent-evaluation")));
        var childCompiler = new SourceCompiler(new(root, childControl, product, Path.Combine(run, "child-evaluation")));
        var parentOriginal = await parentCompiler.CompileAsync(parentControlCommit, parentOriginalCommit);
        var childOriginal = await childCompiler.CompileAsync(childOriginalCommit, parentOriginalCommit);
        static string Parameter(CompiledRevisionPair pair, string name) => JsonNode.Parse(pair.Plan)!["configurations"]![0]!["parameters"]!
            .AsArray().Single(p => p!["name"]!.GetValue<string>() == name)!["value"]!.GetValue<string>();
        Assert.That(JsonNode.Parse(childOriginal.Plan)!["project"]!["parent"]!.GetValue<string>(), Is.EqualTo("T3Personal"));
        Assert.That(childOriginal.SettingsRepository, Is.EqualTo("control-proof"));
        Assert.That(Parameter(childOriginal, "env.CHILD"), Is.EqualTo("disabled-child-write"));
        await File.WriteAllTextAsync(parentSettings, parentSource.Replace("\"base\"", "\"parent-source-changed\"") + """

            // An ancestor source can declare a foreign project; the child's selected control owner remains authoritative.
            public static class ForeignChild
            {
                public static Settings Declare() => new("T3Personal_Child", null, [],
                    [new("Child_Build", "Shadow", [new("env.CHILD", "ancestor-reintroduced-child-value", null)], [], [], [], [], 0, [])]);
            }
            """);
        var parentChangedCommit = await Commit(product, "parent edit and foreign child declaration");
        var parentChanged = await parentCompiler.CompileAsync(parentControlCommit, parentChangedCommit);
        var childAfterParent = await childCompiler.CompileAsync(childOriginalCommit, parentChangedCommit);
        Assert.That(Parameter(parentChanged, "env.MODE"), Is.EqualTo("parent-source-changed"));
        Assert.That(childAfterParent.Plan, Is.EqualTo(childOriginal.Plan));
        Assert.That(childAfterParent.SettingsCommit, Is.EqualTo(childOriginalCommit));
        await File.WriteAllTextAsync(childSettings, childSource.Replace("disabled-child-write", "owned-child-edit"));
        var childChangedCommit = await Commit(childControl, "edit child owned declaration");
        var childChanged = await childCompiler.CompileAsync(childChangedCommit, parentChangedCommit);
        Assert.That(Parameter(childChanged, "env.CHILD"), Is.EqualTo("owned-child-edit"));
        Assert.That((await parentCompiler.CompileAsync(parentControlCommit, parentChangedCommit)).Plan, Is.EqualTo(parentChanged.Plan));
        Assert.That((await childCompiler.CompileAsync(childOriginalCommit, parentOriginalCommit)).Plan, Is.EqualTo(childOriginal.Plan));
        Assert.That((await parentCompiler.CompileAsync(parentControlCommit, parentOriginalCommit)).Plan, Is.EqualTo(parentOriginal.Plan));
        foreach (var input in inputs) Assert.That(Hash(Path.Combine(root, input.Key)), Is.EqualTo(input.Value), input.Key);
        await File.WriteAllTextAsync(Path.Combine(run, "receipt.json"), JsonSerializer.Serialize(new
        {
            result = "PASS", inputs, parentOriginal, parentChanged, childOriginal, childChanged,
            reference_basis = "tests/Vivarium.Tests/TeamCity/Reference/child-owner-observations.json",
            limits = "Trusted bounded C#/Git separate source-owner selection and retained parent identity. No hierarchy scheduler, mode/permission API, cross-project effective inheritance, public SDK or production activation proof."
        }, new JsonSerializerOptions { WriteIndented = true }));
        TestContext.Out.WriteLine("Child owner source receipt: " + Path.Combine(run, "receipt.json"));
    }

    private static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
}
