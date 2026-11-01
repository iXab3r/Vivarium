using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vivarium.DefinitionProof;

// Representation and source-preserving edits, not runner/build/package acceptance.
[NonParallelizable]
public sealed class RecipeSourceTests
{
    [Test]
    public async Task Explicit_T3_recipe_survives_Git_compilation_and_owned_fixture_edits()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Vivarium.slnx"))) directory = directory.Parent;
        var root = directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
        var source = Path.Combine(root, "tools/definition-proof");
        var referencePath = Path.Combine(source, "reference/t3personal.settings.kts");
        var referenceBytes = Encoding.UTF8.GetBytes((await File.ReadAllTextAsync(referencePath)).Replace("\r\n", "\n", StringComparison.Ordinal));
        var blobBytes = Encoding.UTF8.GetBytes("blob " + referenceBytes.Length + "\0").Concat(referenceBytes).ToArray();
        Assert.That(Convert.ToHexStringLower(SHA1.HashData(blobBytes)), Is.EqualTo("2eccef048973d5151c93aba0df0b96c913bfd20d"));
        var run = Path.Combine(root, "artifacts/us013-recipe", "source-run-" + Guid.NewGuid().ToString("N"));
        var control = Path.Combine(run, "control");
        var product = Path.Combine(run, "product");
        var evaluation = Path.Combine(run, "evaluation");
        foreach (var path in new[] { control, product })
        {
            Directory.CreateDirectory(path);
            await SourceCompiler.GitAsync(path, "init", "-b", "main");
            await SourceCompiler.GitAsync(path, "config", "core.autocrlf", "false");
            await SourceCompiler.GitAsync(path, "config", "user.name", "Vivarium P0 proof");
            await SourceCompiler.GitAsync(path, "config", "user.email", "proof@example.invalid");
        }
        var inputs = new[] { "Model.cs", "Program.cs", "ControlReader.cs", "Control.g.cs", "UserCode.cs", "T3Recipe.cs", "render.py", "Evaluator.csproj" }
            .Select(p => Path.Combine(source, p)).Append(Path.Combine(root, "global.json"))
            .Append(referencePath).Append(Path.Combine(root, "Directory.Build.props"))
            .Append(Path.Combine(root, "tests/Vivarium.DefinitionProof/SourceCompiler.cs"))
            .Append(Path.Combine(root, "tests/Vivarium.DefinitionProof/ProofProcess.cs"))
            .Append(Path.Combine(root, "tests/Vivarium.DefinitionProof/Vivarium.DefinitionProof.csproj"))
            .Append(Path.Combine(root, "tests/Vivarium.DefinitionProof/RecipeSourceTests.cs"))
            .ToDictionary(p => Path.GetRelativePath(root, p), Hash);
        var userBytes = await File.ReadAllBytesAsync(Path.Combine(source, "UserCode.cs"));
        var settingsDirectory = Path.Combine(product, ".vivarium/settings");
        Directory.CreateDirectory(settingsDirectory);
        File.Copy(Path.Combine(source, "T3Recipe.cs"), Path.Combine(settingsDirectory, "Settings.g.cs"));
        await File.WriteAllBytesAsync(Path.Combine(settingsDirectory, "UserCode.cs"), userBytes);
        // Control is separate, even for this trusted local fixture.
        var controlText = await File.ReadAllTextAsync(Path.Combine(source, "Control.g.cs"));
        Assert.That(controlText, Does.Contain(", null);"));
        controlText = controlText.Replace(", null);", ", new(\"product-proof\", \".vivarium/settings/Settings.g.cs\", \"main\", \"local-owned\", \"credential-ref-only\"));", StringComparison.Ordinal);
        await File.WriteAllTextAsync(Path.Combine(control, "Control.g.cs"), controlText);
        async Task<string> Commit(string repository, string message)
        {
            await SourceCompiler.GitAsync(repository, "add", "--all");
            await SourceCompiler.GitAsync(repository, "commit", "-m", message);
            return await SourceCompiler.GitAsync(repository, "rev-parse", "HEAD");
        }
        var controlCommit = await Commit(control, "proof: bind owned literal recipe");
        var referenceCommit = await Commit(product, "proof: explicit pinned T3 settings");
        var compiler = new SourceCompiler(new(root, control, product, evaluation));
        var original = await compiler.CompileAsync(controlCommit, referenceCommit);
        using var plan = JsonDocument.Parse(original.Plan);
        var configs = plan.RootElement.GetProperty("configurations").EnumerateArray().ToArray();
        Assert.That(configs.Select(c => c.GetProperty("timeoutMinutes").GetInt32()), Is.EqualTo(new[] { 90, 90, 15 }));
        Assert.That(configs.Select(c => c.GetProperty("checkoutMode").GetString()), Is.All.EqualTo("ON_AGENT"));
        Assert.That(plan.RootElement.GetProperty("source").GetProperty("checkoutRules").GetString(), Is.EqualTo("+:.\n-:.repos"));
        Assert.That(configs[0].GetProperty("steps")[0].GetProperty("properties").GetArrayLength(), Is.EqualTo(4));
        Assert.That(configs[1].GetProperty("steps")[0].GetProperty("properties").GetArrayLength(), Is.EqualTo(5));
        Assert.That(configs[2].GetProperty("steps")[0].GetProperty("properties").GetArrayLength(), Is.EqualTo(4));
        Assert.That(configs[0].GetProperty("steps")[0].GetProperty("properties")[0].GetProperty("value").GetString(),
            Does.Contain("result=${PIPESTATUS[0]}").And.Contain("exit \"$result\""));
        Assert.That(configs[2].GetProperty("parameters")[0].GetProperty("value").GetString(), Is.EqualTo("release"));
        Assert.That(configs[2].GetProperty("triggers")[0].GetProperty("quietPeriodMode").GetString(), Is.EqualTo("USE_CUSTOM"));
        Assert.That(configs[2].GetProperty("triggers")[0].GetProperty("rules").ValueKind, Is.EqualTo(JsonValueKind.Null), "Later exclusions are not historical cohort settings.");
        Assert.That(configs[2].GetProperty("artifacts").EnumerateArray().Select(a => a.GetProperty("rules").GetString()),
            Is.EqualTo(new[] { "windows/** => ci-input/windows", "linux/** => ci-input/linux" }));
        await File.WriteAllTextAsync(Path.Combine(run, "reference-plan.json"), original.Plan);
        var dotnet = Path.Combine(root, "artifacts/p0-rest-first/tooling/dotnet/dotnet.exe");
        var declared = await ProofProcess.RunAsync(dotnet, [Path.Combine(evaluation, "bin/Debug/net10.0/Evaluator.dll"), "declared"]);
        var data = JsonNode.Parse(declared.Text)!;
        async Task<string> Edit(string name)
        {
            var input = Path.Combine(run, name + ".json");
            await File.WriteAllTextAsync(input, data.ToJsonString());
            // JSON is transient evaluator/editor input. Only the resulting C# is committed/evaluated.
            await ProofProcess.RunAsync("python", ["-c",
                "import sys,json,pathlib;sys.dont_write_bytecode=True;sys.path.insert(0,sys.argv[1]);import render;pathlib.Path(sys.argv[3]).write_text(render.settings(json.loads(pathlib.Path(sys.argv[2]).read_text(encoding='utf8'))['settings']),encoding='utf8',newline='\\n')",
                source, input, Path.Combine(settingsDirectory, "Settings.g.cs")]);
            return await Commit(product, "proof: " + name);
        }
        var roundtrip = await compiler.CompileAsync(controlCommit, await Edit("managed-roundtrip"));
        Assert.That(roundtrip.Plan, Is.EqualTo(original.Plan), "Every explicit recipe field must survive the editor.");
        var settings = data["settings"]!;
        settings["source"]!["url"] = "https://github.com/iXab3r/vivarium-us013-e2e.git";
        for (var index = 0; index < 3; index++)
        {
            var config = settings["configurations"]![index]!;
            config["requirements"]![0]!["value"] = index == 1 ? "us013-owned-windows" : "us013-owned-linux";
            // These names describe the future adapted workload; no script mutation/execution is asserted.
            if (index == 1) config["name"] = "Windows x64 / build and package";
        }
        settings["configurations"]![0]!["steps"]![0]!["properties"]![3]!["literal"] =
            "--user 0 --init --platform linux/amd64 --volume us013-owned-vite:/root/.vite-plus --volume us013-owned-cargo:/root/.cargo --volume us013-owned-rustup:/root/.rustup";
        // Used later delta is explicit, separate from the historical cohort.
        settings["configurations"]![2]!["triggers"]![0]!["rules"] = "-:README.md\n-:AGENTS.md\n-:docs/operations/**\n-:.teamcity/**";
        var adaptedCommit = await Edit("owned-configuration-candidate");
        var adapted = await compiler.CompileAsync(controlCommit, adaptedCommit);
        await File.WriteAllTextAsync(Path.Combine(run, "owned-plan.json"), adapted.Plan);
        using var owned = JsonDocument.Parse(adapted.Plan);
        Assert.That(owned.RootElement.GetProperty("source").GetProperty("url").GetString(), Is.EqualTo("https://github.com/iXab3r/vivarium-us013-e2e.git"));
        Assert.That(owned.RootElement.GetProperty("configurations")[2].GetProperty("triggers")[0].GetProperty("rules").GetString(),
            Is.EqualTo("-:README.md\n-:AGENTS.md\n-:docs/operations/**\n-:.teamcity/**"));
        Assert.That((await compiler.CompileAsync(controlCommit, referenceCommit)).Plan, Is.EqualTo(original.Plan), "Cold evaluation reads exact old committed C#.");
        Assert.That(await File.ReadAllBytesAsync(Path.Combine(settingsDirectory, "UserCode.cs")), Is.EqualTo(userBytes));
        foreach (var input in inputs) Assert.That(Hash(Path.Combine(root, input.Key)), Is.EqualTo(input.Value), input.Key);
        await File.WriteAllTextAsync(Path.Combine(run, "receipt.json"), JsonSerializer.Serialize(new
        {
            scope = "Explicit pinned T3 settings representation and managed edits only; not resolved defaults, SDK, workload adaptation or execution.",
            source_revision = "7ff65cada80dd817aa5d7467b85e78ec30c578a9",
            settings_revision = "7495b6859c91952ad91478a2428be805a4e48f1a",
            kotlin_blob = "2eccef048973d5151c93aba0df0b96c913bfd20d",
            inputs, controlCommit, referenceCommit, roundtrip, adaptedCommit, original.PlanHash,
            adapted_plan_hash = adapted.PlanHash, result = "PASS", workload_executed = false,
            limits = "Stable step IDs are attributed proof IDs. Checkout/source is common to all three. Implicit defaults, computed categories, modes, concurrency, current grants, secret resolution, capabilities and full script adaptation remain open."
        }, new JsonSerializerOptions { WriteIndented = true }));
        TestContext.Out.WriteLine("Recipe receipt: " + Path.Combine(run, "receipt.json"));
    }

    private static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
}
