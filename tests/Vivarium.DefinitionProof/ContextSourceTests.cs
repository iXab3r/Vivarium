using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vivarium.DefinitionProof;

[NonParallelizable]
public sealed class ContextSourceTests
{
    [Test]
    public async Task Control_context_changes_effective_settings_without_rewriting_product_source()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Vivarium.slnx"))) directory = directory.Parent;
        var root = directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
        var source = Path.Combine(root, "tools/definition-proof");
        var run = Path.Combine(root, "artifacts/us013-context", "source-run-" + Guid.NewGuid().ToString("N"));
        var control = Path.Combine(run, "control");
        var product = Path.Combine(run, "product");
        foreach (var path in new[] { control, product })
        {
            Directory.CreateDirectory(path);
            await SourceCompiler.GitAsync(path, "init", "-b", "main");
            await SourceCompiler.GitAsync(path, "config", "core.autocrlf", "false");
            await SourceCompiler.GitAsync(path, "config", "user.name", "Vivarium P0 proof");
            await SourceCompiler.GitAsync(path, "config", "user.email", "proof@example.invalid");
        }
        var settingsRoot = Path.Combine(product, ".vivarium/settings");
        Directory.CreateDirectory(settingsRoot);
        File.Copy(Path.Combine(source, "Settings.g.cs"), Path.Combine(settingsRoot, "Settings.g.cs"));
        var user = Path.Combine(settingsRoot, "UserCode.cs");
        await File.WriteAllTextAsync(user, """
            namespace DefinitionProof;
            public static class UserCode
            {
                public static string Evaluate(string expression) => DefinitionContext.Get("fixture.mode", "ctx-default");
            }
            """);
        var inputs = Directory.GetFiles(source, "*", SearchOption.TopDirectoryOnly)
            .Where(p => Path.GetExtension(p) is ".cs" or ".py" or ".csproj")
            .Concat(Directory.GetFiles(Path.Combine(root, "tests/Vivarium.DefinitionProof"), "*.cs"))
            .Append(Path.Combine(root, "global.json")).Append(Path.Combine(root, "Directory.Build.props"))
            .Append(Path.Combine(root, "tests/Vivarium.DefinitionProof/Vivarium.DefinitionProof.csproj"))
            .ToDictionary(p => Path.GetRelativePath(root, p), Hash);
        async Task<string> Commit(string repository, string name)
        {
            await SourceCompiler.GitAsync(repository, "add", "--all");
            await SourceCompiler.GitAsync(repository, "commit", "-m", "proof: " + name);
            return await SourceCompiler.GitAsync(repository, "rev-parse", "HEAD");
        }
        var productCommit = await Commit(product, "context-consuming product source");
        var productUserHash = Hash(user);
        var compiler = new SourceCompiler(new(root, control, product, Path.Combine(run, "evaluation")));
        var observations = new List<object>();
        async Task<CompiledRevisionPair> Evaluate(string name, object[] context)
        {
            var input = Path.Combine(run, name + "-control-input.json");
            await File.WriteAllTextAsync(input, JsonSerializer.Serialize(new
            {
                id = "T3Personal", parent = "_Root", name = "T3Personal", description = "Context fixture",
                binding = new { repository = "product-proof", path = ".vivarium/settings/Settings.g.cs", branch = "main", trustReference = "local-owned", credentialReference = "credential-ref-only", context }
            }));
            await ProofProcess.RunAsync("python", ["-c",
                "import sys,json,pathlib;sys.dont_write_bytecode=True;sys.path.insert(0,sys.argv[1]);import render;pathlib.Path(sys.argv[3]).write_text(render.control(json.loads(pathlib.Path(sys.argv[2]).read_text(encoding='utf8'))),encoding='utf8',newline='\\n')",
                source, input, Path.Combine(control, "Control.g.cs")]);
            var controlCommit = await Commit(control, name);
            var plan = await compiler.CompileAsync(controlCommit, productCommit);
            await File.WriteAllTextAsync(Path.Combine(run, name + "-plan.json"), plan.Plan);
            observations.Add(new { name, controlCommit, productCommit, plan.PlanHash });
            Assert.That(await SourceCompiler.GitAsync(product, "rev-parse", "HEAD"), Is.EqualTo(productCommit));
            Assert.That(Hash(user), Is.EqualTo(productUserHash));
            return plan;
        }
        static JsonNode Mode(CompiledRevisionPair plan) => JsonNode.Parse(plan.Plan)!["configurations"]![0]!["parameters"]!
            .AsArray().Single(p => p!["name"]!.GetValue<string>() == "env.MODE")!["value"]!;
        var first = await Evaluate("context-a", [new { name = "fixture.mode", value = "ctx-a" }]);
        Assert.That(Mode(first).GetValue<string>(), Is.EqualTo("ctx-a"));
        var second = await Evaluate("context-b", [new { name = "fixture.mode", value = "ctx-b" }]);
        Assert.That(Mode(second).GetValue<string>(), Is.EqualTo("ctx-b"));
        Assert.That(second.SettingsCommit, Is.EqualTo(first.SettingsCommit));
        Assert.That(second.PlanHash, Is.Not.EqualTo(first.PlanHash));
        var fallback = await Evaluate("context-default", []);
        Assert.That(Mode(fallback).GetValue<string>(), Is.EqualTo("ctx-default"));
        var reads = JsonNode.Parse(fallback.Plan)!["contextReads"]!.AsArray();
        Assert.That(reads, Has.Count.EqualTo(1));
        Assert.That(reads[0]!["usedDefault"]!.GetValue<bool>(), Is.True);
        Assert.That(reads[0]!["name"]!.GetValue<string>(), Is.EqualTo("fixture.mode"));
        await File.WriteAllTextAsync(user, """
            namespace DefinitionProof;
            public static class UserCode
            {
                public static string Evaluate(string expression) => "context-unused";
            }
            """);
        var unusedCommit = await Commit(product, "author stops using configured context");
        var unused = await compiler.CompileAsync(second.ControlCommit, unusedCommit);
        Assert.That(Mode(unused).GetValue<string>(), Is.EqualTo("context-unused"));
        Assert.That(JsonNode.Parse(unused.Plan)!["contextReads"]!.AsArray(), Is.Empty);
        observations.Add(new { name = "configured-but-unused", unusedCommit, unused.PlanHash });
        Assert.That((await compiler.CompileAsync(first.ControlCommit, productCommit)).Plan, Is.EqualTo(first.Plan));
        foreach (var input in inputs) Assert.That(Hash(Path.Combine(root, input.Key)), Is.EqualTo(input.Value), input.Key);
        await File.WriteAllTextAsync(Path.Combine(run, "receipt.json"), JsonSerializer.Serialize(new
        {
            scope = "Trusted control-context C#/Git evaluation and immutable input provenance; not REST authorization/editability or production activation.",
            inputs, productCommit, unusedCommit, productUserHash, observations, result = "PASS",
            reference_basis = "Pinned TeamCity 2026.1.3 used context changes effective values at one product/settings commit, including default-valued context use.",
            limits = "No public SDK, untrusted source sandbox, mode/grant/mutation or Host/workload acceptance."
        }, new JsonSerializerOptions { WriteIndented = true }));
        TestContext.Out.WriteLine("Context receipt: " + Path.Combine(run, "receipt.json"));
    }

    private static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
}
