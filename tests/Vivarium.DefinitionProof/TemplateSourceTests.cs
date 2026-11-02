using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vivarium.DefinitionProof;

[NonParallelizable]
public sealed class TemplateSourceTests
{
    [Test]
    public async Task Managed_CSharp_preserves_observed_parameter_template_order_project_priority_and_local_reset()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Vivarium.slnx"))) directory = directory.Parent;
        var root = directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
        var source = Path.Combine(root, "tools/definition-proof");
        var run = Path.Combine(root, "artifacts/us013-templates-20261102", "source-run-" + Guid.NewGuid().ToString("N"));
        var control = Path.Combine(run, "control"); var product = Path.Combine(run, "product");
        foreach (var path in new[] { control, product })
        {
            Directory.CreateDirectory(path);
            await SourceCompiler.GitAsync(path, "init", "-b", "main");
            await SourceCompiler.GitAsync(path, "config", "core.autocrlf", "false");
            await SourceCompiler.GitAsync(path, "config", "user.name", "Vivarium P0 proof");
            await SourceCompiler.GitAsync(path, "config", "user.email", "proof@example.invalid");
        }
        var settings = Path.Combine(product, ".vivarium/settings/Settings.g.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
        var user = Path.Combine(Path.GetDirectoryName(settings)!, "UserCode.cs");
        await File.WriteAllTextAsync(user, "public static class UserCode { public static string Evaluate(string expression) => throw new System.InvalidOperationException(expression); }");
        var userHash = Hash(user);
        await File.WriteAllTextAsync(Path.Combine(control, "Control.g.cs"), """
            using DefinitionProof;
            public static class Control
            {
                public static Project Declare() => new("T3Personal", "_Root", "T3Personal", "Template fixture",
                    new("product-proof", ".vivarium/settings/Settings.g.cs", "main", "local-owned", "credential-ref-only"));
            }
            """);
        async Task<string> Commit(string repository, string name)
        {
            await SourceCompiler.GitAsync(repository, "add", "--all");
            await SourceCompiler.GitAsync(repository, "commit", "-m", "proof: " + name);
            return await SourceCompiler.GitAsync(repository, "rev-parse", "HEAD");
        }
        var controlCommit = await Commit(control, "template control");
        var inputs = Directory.GetFiles(source).Where(path => Path.GetExtension(path) is ".cs" or ".py" or ".csproj")
            .Concat(Directory.GetFiles(Path.Combine(root, "tests/Vivarium.DefinitionProof"), "*.cs"))
            .Append(Path.Combine(root, "tests/Vivarium.DefinitionProof/Vivarium.DefinitionProof.csproj"))
            .Append(Path.Combine(root, "tests/Vivarium.Tests/TeamCity/CurrentController.cs"))
            .Append(Path.Combine(root, "tests/Vivarium.Tests/TeamCity/Reference/template-observations.json"))
            .Append(Path.Combine(root, "tools/teamcity-reference/template-parameter-capture.mjs"))
            .Append(Path.Combine(root, "global.json")).Append(Path.Combine(root, "Directory.Build.props"))
            .ToDictionary(path => Path.GetRelativePath(root, path), Hash);
        var value = JsonNode.Parse("""
            {"projectId":"T3Personal","source":null,"parameters":[],"configurations":[
              {"id":"T3Personal_Prepare","name":"Prepare","parameters":[],"steps":[],"requirements":[],"snapshots":[],"artifacts":[],"timeoutMinutes":0,"triggers":[],"templates":["A","B"]}],
             "templates":[{"id":"A","name":"Template A","parameters":[{"name":"env.SHARED","literal":"A","expression":null}]},
                          {"id":"B","name":"Template B","parameters":[{"name":"env.SHARED","literal":"B","expression":null}]}]}
            """)!;
        var compiler = new SourceCompiler(new(root, control, product, Path.Combine(run, "evaluation")));
        var observations = new List<object>();
        async Task<string> RenderCommit(string name)
        {
            var input = Path.Combine(run, name + ".json"); await File.WriteAllTextAsync(input, value.ToJsonString());
            await ProofProcess.RunAsync("python", ["-c", "import sys,json,pathlib;sys.dont_write_bytecode=True;sys.path.insert(0,sys.argv[1]);import render;pathlib.Path(sys.argv[3]).write_text(render.settings(json.loads(pathlib.Path(sys.argv[2]).read_text())),encoding='utf8',newline='\\n')", source, input, settings]);
            return await Commit(product, name);
        }
        async Task<CompiledRevisionPair> Evaluate(string name, string expected, bool inherited)
        {
            var productCommit = await RenderCommit(name);
            var plan = await compiler.CompileAsync(controlCommit, productCommit);
            await File.WriteAllTextAsync(Path.Combine(run, name + "-plan.json"), plan.Plan);
            var parameters = JsonNode.Parse(plan.Plan)!["configurations"]![0]!["parameters"]!.AsArray();
            Assert.That(parameters.Any(p => p!["name"]!.GetValue<string>() == "env.SHARED"), Is.True, "Rendered C# must retain the inherited template parameter.");
            var parameter = parameters.Single(p => p!["name"]!.GetValue<string>() == "env.SHARED")!;
            Assert.That(JsonNode.DeepEquals(JsonNode.Parse(plan.Plan)!["configurations"]![0]!["templates"], value["configurations"]![0]!["templates"]), Is.True);
            Assert.That(parameter["value"]!.GetValue<string>(), Is.EqualTo(expected), name);
            Assert.That(parameter["inherited"]!.GetValue<bool>(), Is.EqualTo(inherited), name);
            Assert.That(Hash(user), Is.EqualTo(userHash));
            observations.Add(new { name, productCommit, plan.PlanHash, expected, inherited });
            return plan;
        }
        var original = await Evaluate("a-before-b", "A", true);
        value["configurations"]![0]!["templates"] = new JsonArray("B", "A");
        await Evaluate("b-before-a", "B", true);
        value["parameters"] = JsonNode.Parse("""[{"name":"env.SHARED","literal":"PROJECT","expression":null}]""");
        await Evaluate("project-before-templates", "PROJECT", true);
        value["configurations"]![0]!["parameters"] = JsonNode.Parse("""[{"name":"env.SHARED","literal":"LOCAL","expression":null}]""");
        await Evaluate("local-before-inherited", "LOCAL", false);
        value["configurations"]![0]!["parameters"] = new JsonArray();
        await Evaluate("local-reset", "PROJECT", true);
        value["parameters"] = new JsonArray(); value["templates"]![1]!["parameters"]![0]!["literal"] = "B2";
        var changed = await Evaluate("edit-owned-template", "B2", true);
        var dotnet = Path.Combine(root, "artifacts/p0-rest-first/tooling/dotnet/dotnet.exe");
        var declared = await ProofProcess.RunAsync(dotnet, [Path.Combine(run, "evaluation/bin/Debug/net10.0/Evaluator.dll"), "declared"]);
        var readbackInput = Path.Combine(run, "declared-readback.json");
        await File.WriteAllTextAsync(readbackInput, JsonNode.Parse(declared.Text)!["settings"]!.ToJsonString());
        var readbackSource = Path.Combine(run, "declared-readback.cs");
        await ProofProcess.RunAsync("python", ["-c", "import sys,json,pathlib;sys.dont_write_bytecode=True;sys.path.insert(0,sys.argv[1]);import render;pathlib.Path(sys.argv[3]).write_text(render.settings(json.loads(pathlib.Path(sys.argv[2]).read_text())),encoding='utf8',newline='\\n')", source, readbackInput, readbackSource]);
        Assert.That(await File.ReadAllTextAsync(readbackSource), Is.EqualTo(await File.ReadAllTextAsync(settings)), "Compiled declaration readback retains templates and attachment order.");
        Assert.That((await compiler.CompileAsync(original.ControlCommit, original.SettingsCommit)).Plan, Is.EqualTo(original.Plan));
        value["configurations"]![0]!["templates"] = new JsonArray("missing-template");
        var missing = await RenderCommit("missing-template-rejected");
        Assert.ThrowsAsync<InvalidDataException>(async () => await compiler.CompileAsync(controlCommit, missing));
        value["configurations"]![0]!["templates"] = new JsonArray("B", "B");
        var duplicate = await RenderCommit("duplicate-template-attachment-rejected");
        Assert.ThrowsAsync<InvalidDataException>(async () => await compiler.CompileAsync(controlCommit, duplicate));
        Assert.That((await compiler.CompileAsync(changed.ControlCommit, changed.SettingsCommit)).Plan, Is.EqualTo(changed.Plan), "Rejected current declarations do not rewrite an old accepted source revision.");
        foreach (var input in inputs) Assert.That(Hash(Path.Combine(root, input.Key)), Is.EqualTo(input.Value), input.Key);
        await File.WriteAllTextAsync(Path.Combine(run, "receipt.json"), JsonSerializer.Serialize(new
        {
            result = "PASS", inputs, controlCommit, original = original.PlanHash, changed = changed.PlanHash, userHash, observations,
            scope = "Trusted C#/Git parameter-template declaration/edit/effective precedence; not a public SDK or full template semantics.",
            reference_basis = new[] {
                "artifacts/us013-templates-20261102/run-fbf3a757359645b5a72d155114bc60cb/receipt.json" }
                .Select(path => new { path, sha256 = Hash(Path.Combine(root, path)) }).ToArray(),
            limits = "Only parameter inheritance is observed/proven. Other template field kinds, hierarchy, defaults/inline/optimization, permissions, concurrency and production activation remain separately due."
        }, new JsonSerializerOptions { WriteIndented = true }));
        TestContext.Out.WriteLine("Template source receipt: " + Path.Combine(run, "receipt.json"));
    }

    private static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
}
