using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vivarium.DefinitionProof;

[NonParallelizable]
public sealed class ComputedSourceTests
{
    // Trusted C# fixture, not a public SDK or a TeamCity-derived oracle for unobserved modes.
    private const string UserSource = """
        using DefinitionProof;
        public static class UserCode
        {
            private const string Version = "v1";
            public static string Evaluate(string expression) => throw new InvalidOperationException(expression);
            public static Settings Declare() => new("T3Personal", null,
                [new("env.MODE", "base", null), new("env.USER_VERSION", Version, null)],
                [new("T3Personal_Prepare", string.Concat("Prepare-", Version),
                    [new("env.MODE", string.Join("", new[] { "computed", "-mode" }), null)],
                    Enumerable.Range(1, 2).Select(i => new Step("step-" + i, Version + "-step-" + i,
                        "simpleRunner", [new("script.content", "echo " + Version + "-" + i, null)])).ToArray(),
                    [], [], [], 90, [])]);
        }
        """;

    [Test]
    public async Task Managed_edits_follow_user_computation_without_rewriting_user_source()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Vivarium.slnx"))) directory = directory.Parent;
        var root = directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
        var source = Path.Combine(root, "tools/definition-proof");
        var run = Path.Combine(root, "artifacts/us013-computed", "source-run-" + Guid.NewGuid().ToString("N"));
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
        var settings = Path.Combine(product, ".vivarium/settings/Settings.g.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
        var user = Path.Combine(Path.GetDirectoryName(settings)!, "UserCode.cs");
        await File.WriteAllTextAsync(user, UserSource);
        await File.WriteAllTextAsync(Path.Combine(control, "Control.g.cs"), """
            using DefinitionProof;
            public static class Control
            {
                public static Project Declare() => new("T3Personal", "_Root", "T3Personal", "Computed fixture",
                    new("product-proof", ".vivarium/settings/Settings.g.cs", "main", "local-owned", "credential-ref-only"));
            }
            """);
        var inputs = Directory.GetFiles(source, "*", SearchOption.TopDirectoryOnly)
            .Where(p => Path.GetExtension(p) is ".cs" or ".py" or ".csproj")
            .Concat(Directory.GetFiles(Path.Combine(root, "tests/Vivarium.DefinitionProof"), "*.cs"))
            .Append(Path.Combine(root, "global.json")).Append(Path.Combine(root, "Directory.Build.props"))
            .Append(Path.Combine(root, "tests/Vivarium.DefinitionProof/Vivarium.DefinitionProof.csproj"))
            .ToDictionary(p => Path.GetRelativePath(root, p), Hash);
        var observations = new List<object>();
        async Task<string> Commit(string repository, string name)
        {
            await SourceCompiler.GitAsync(repository, "add", "--all");
            await SourceCompiler.GitAsync(repository, "commit", "-m", "proof: " + name);
            return await SourceCompiler.GitAsync(repository, "rev-parse", "HEAD");
        }
        var controlCommit = await Commit(control, "bind computed settings");
        var compiler = new SourceCompiler(new(root, control, product, Path.Combine(run, "evaluation")));
        async Task<JsonArray> ReadEdits()
        {
            var dotnet = Path.Combine(root, "artifacts/p0-rest-first/tooling/dotnet/dotnet.exe");
            var result = await ProofProcess.RunAsync(dotnet, [Path.Combine(run, "evaluation/bin/Debug/net10.0/Evaluator.dll"), "declared"]);
            var declared = JsonNode.Parse(result.Text)!;
            Assert.That(declared["managedEdits"], Is.TypeOf<JsonArray>(), "A later edit must read actual declared edits rather than replace them from effective values.");
            return declared["managedEdits"]!.AsArray();
        }
        async Task<JsonDocument> Evaluate(string name, object edits)
        {
            var input = Path.Combine(run, name + "-editor-input.json");
            await File.WriteAllTextAsync(input, JsonSerializer.Serialize(edits));
            var expectedUserHash = Hash(user);
            await ProofProcess.RunAsync("python", ["-c",
                "import sys,json,pathlib;sys.dont_write_bytecode=True;sys.path.insert(0,sys.argv[1]);import render;pathlib.Path(sys.argv[3]).write_text(render.computed_settings(json.loads(pathlib.Path(sys.argv[2]).read_text(encoding='utf8'))),encoding='utf8',newline='\\n')",
                source, input, settings]);
            Assert.That(Hash(user), Is.EqualTo(expectedUserHash));
            var commit = await Commit(product, name);
            var plan = await compiler.CompileAsync(controlCommit, commit);
            await File.WriteAllTextAsync(Path.Combine(run, name + "-plan.json"), plan.Plan);
            observations.Add(new { name, commit, plan.PlanHash, user_source_hash = expectedUserHash });
            return JsonDocument.Parse(plan.Plan);
        }
        static JsonElement Config(JsonDocument document) => document.RootElement.GetProperty("configurations")[0];
        static string? Mode(JsonDocument document) => Config(document).GetProperty("parameters")[0].GetProperty("value").GetString();
        using var initial = await Evaluate("computed-base", Array.Empty<object>());
        Assert.That(await ReadEdits(), Is.Empty);
        var initialCommit = await SourceCompiler.GitAsync(product, "rev-parse", "HEAD");
        Assert.That(Mode(initial), Is.EqualTo("computed-mode"));
        using var overridden = await Evaluate("override", new[] { new { configuration = "T3Personal_Prepare", parameters = new[] { new { name = "env.MODE", value = "managed-override" } } } });
        Assert.That(Mode(overridden), Is.EqualTo("managed-override"));
        await File.WriteAllTextAsync(user, UserSource.Replace("\"v1\"", "\"v2\"", StringComparison.Ordinal));
        using var changedBase = await Evaluate("author-changed-unrelated-computations", new[] { new { configuration = "T3Personal_Prepare", parameters = new[] { new { name = "env.MODE", value = "managed-override" } } } });
        Assert.That(Mode(changedBase), Is.EqualTo("managed-override"));
        Assert.That(Config(changedBase).GetProperty("name").GetString(), Is.EqualTo("Prepare-v2"));
        Assert.That(Config(changedBase).GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("name").GetString()), Is.EqualTo(new[] { "v2-step-1", "v2-step-2" }));
        using var removed = await Evaluate("remove-local-computed-parameter", new[] { new { configuration = "T3Personal_Prepare", parameters = new[] { new { name = "env.MODE", remove = true } } } });
        Assert.That(Mode(removed), Is.EqualTo("base"));
        Assert.That(Config(removed).GetProperty("parameters")[0].GetProperty("inherited").GetBoolean(), Is.True);
        var retainedEdits = await ReadEdits();
        retainedEdits[0]!["name"] = "Unrelated name edit";
        using var unrelatedEdit = await Evaluate("unrelated-edit-keeps-local-removal", retainedEdits);
        Assert.That(Mode(unrelatedEdit), Is.EqualTo("base"));
        Assert.That(Config(unrelatedEdit).GetProperty("parameters")[0].GetProperty("inherited").GetBoolean(), Is.True);
        Assert.That(Config(unrelatedEdit).GetProperty("name").GetString(), Is.EqualTo("Unrelated name edit"));
        var steps = new[] { new { id = "managed-2", name = "Second", runner = "simpleRunner", properties = Array.Empty<object>() }, new { id = "managed-1", name = "First", runner = "simpleRunner", properties = Array.Empty<object>() } };
        retainedEdits = await ReadEdits();
        retainedEdits[0]!["name"] = "Managed name";
        retainedEdits[0]!["steps"] = JsonSerializer.SerializeToNode(steps);
        retainedEdits[0]!["timeoutMinutes"] = 15;
        using var replaced = await Evaluate("replace-computed-collection-and-scalar", retainedEdits);
        Assert.That(Mode(replaced), Is.EqualTo("base"), "An unrelated scalar/collection edit must retain local-removal intent.");
        Assert.That(Config(replaced).GetProperty("name").GetString(), Is.EqualTo("Managed name"));
        Assert.That(Config(replaced).GetProperty("timeoutMinutes").GetInt32(), Is.EqualTo(15));
        Assert.That(Config(replaced).GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("name").GetString()), Is.EqualTo(new[] { "Second", "First" }));
        retainedEdits = await ReadEdits();
        retainedEdits[0]!["steps"] = JsonSerializer.SerializeToNode(steps.Take(1).ToArray());
        using var omitted = await Evaluate("omit-computed-collection-item", retainedEdits);
        Assert.That(Config(omitted).GetProperty("steps").GetArrayLength(), Is.EqualTo(1));
        Assert.That(Mode(omitted), Is.EqualTo("base"));
        await File.WriteAllTextAsync(user, UserSource.Replace("\"v1\"", "\"v3\"", StringComparison.Ordinal));
        using var laterAuthor = await Evaluate("later-author-keeps-managed-intent", await ReadEdits());
        Assert.That(Config(laterAuthor).GetProperty("name").GetString(), Is.EqualTo("Managed name"));
        Assert.That(Config(laterAuthor).GetProperty("steps")[0].GetProperty("name").GetString(), Is.EqualTo("Second"));
        Assert.That(Config(laterAuthor).GetProperty("timeoutMinutes").GetInt32(), Is.EqualTo(15));
        Assert.That(Mode(laterAuthor), Is.EqualTo("base"));
        Assert.That(Config(laterAuthor).GetProperty("parameters").EnumerateArray().Single(p => p.GetProperty("name").GetString() == "env.USER_VERSION").GetProperty("value").GetString(), Is.EqualTo("v3"));
        // Removing an edit in Git is distinct from REST DELETE of a computed local parameter.
        using var cleared = await Evaluate("author-clears-all-managed-edits", Array.Empty<object>());
        Assert.That(Mode(cleared), Is.EqualTo("computed-mode"));
        Assert.That(Config(cleared).GetProperty("name").GetString(), Is.EqualTo("Prepare-v3"));
        Assert.That((await compiler.CompileAsync(controlCommit, initialCommit)).Plan, Is.EqualTo(initial.RootElement.GetRawText()));
        foreach (var input in inputs) Assert.That(Hash(Path.Combine(root, input.Key)), Is.EqualTo(input.Value), input.Key);
        await File.WriteAllTextAsync(Path.Combine(run, "receipt.json"), JsonSerializer.Serialize(new
        {
            scope = "Trusted C#/Git computed-source and typed managed-edit feasibility; no public SDK, REST mode or production activation claim.",
            inputs, controlCommit, initialCommit, observations, result = "PASS",
            reference_basis = "Observed Kotlin computed parameter -> REST override -> removal exposing inherited base; other computed categories are source-representation evidence, not unobserved TeamCity conformance.",
            limits = "Only named scalar/parameter/step edits. No arbitrary AST edit, auth/mode/concurrency proof, full schema adoption, Host or workload execution."
        }, new JsonSerializerOptions { WriteIndented = true }));
        TestContext.Out.WriteLine("Computed receipt: " + Path.Combine(run, "receipt.json"));
    }

    private static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
}
