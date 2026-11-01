using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Vivarium.DefinitionProof;

internal sealed record StudyResources(string Root, string Control, string Product, string Evaluation);
internal sealed record CompiledRevisionPair(string ControlCommit, string SettingsRepository,
    string SettingsCommit, string PlanHash, string Plan);

internal sealed class SourceCompiler(StudyResources resources)
{
    public static async Task<string> GitAsync(string repository, params string[] arguments) =>
        (await ProofProcess.RunAsync("git", ["-C", repository, .. arguments])).Text;

    public static async Task<bool> CompareExchangeAsync(string repository, string expected, string result)
    {
        if (await GitAsync(repository, "rev-parse", "main") == result) return true;
        var changed = await ProofProcess.RunAsync("git", ["-C", repository, "-c",
            "core.filesRefLockTimeout=30000", "update-ref", "refs/heads/main", result, expected], allowFailure: true);
        return changed.ExitCode == 0;
    }

    private async Task CopyCommittedAsync(string repository, string commit, string path, string name)
    {
        var data = await ProofProcess.RunAsync("git", ["-C", repository, "show", commit + ":" + path]);
        await File.WriteAllBytesAsync(Path.Combine(resources.Evaluation, name), data.Output);
    }

    private async Task<string> EvaluateAsync(bool controlOnly)
    {
        var dotnet = Path.Combine(resources.Root, "artifacts", "p0-rest-first", "tooling", "dotnet", "dotnet.exe");
        var built = await ProofProcess.RunAsync(dotnet, ["build", "Evaluator.csproj", "-m:1",
            "-p:SettingsSource=Settings.g.cs", "-p:ReadControlOnly=" + controlOnly.ToString().ToLowerInvariant(),
            "-p:UseSharedCompilation=false", "--disable-build-servers", "--nologo"],
            resources.Evaluation, allowFailure: true);
        if (built.ExitCode != 0) throw new InvalidDataException("Committed source does not compile.");
        var evaluated = await ProofProcess.RunAsync(dotnet,
            [Path.Combine(resources.Evaluation, "bin", "Debug", "net10.0", "Evaluator.dll")],
            resources.Evaluation, allowFailure: true, deadline: TimeSpan.FromSeconds(10));
        if (evaluated.ExitCode != 0) throw new InvalidDataException("Committed source does not evaluate.");
        return Encoding.UTF8.GetString(evaluated.Output);
    }

    public async Task<CompiledRevisionPair> CompileAsync(string controlCommit, string productCommit)
    {
        Directory.CreateDirectory(resources.Evaluation);
        var templates = Path.Combine(resources.Root, "tools", "definition-proof");
        foreach (var name in new[] { "Evaluator.csproj", "Model.cs", "Program.cs", "ControlReader.cs" })
            File.Copy(Path.Combine(templates, name), Path.Combine(resources.Evaluation, name), overwrite: true);
        File.Copy(Path.Combine(resources.Root, "global.json"), Path.Combine(resources.Evaluation, "global.json"), overwrite: true);
        await CopyCommittedAsync(resources.Control, controlCommit, "Control.g.cs", "Control.g.cs");
        using var control = JsonDocument.Parse(await EvaluateAsync(controlOnly: true));
        if (control.RootElement.GetProperty("id").GetString() != "T3Personal")
            throw new InvalidDataException("This bounded proof owns only its fixed Project.");
        var binding = control.RootElement.GetProperty("binding");
        var bound = binding.ValueKind != JsonValueKind.Null;
        var settingsRepository = bound ? resources.Product : resources.Control;
        var settingsCommit = bound ? productCommit : controlCommit;
        var path = bound ? ".vivarium/settings/Settings.g.cs" : ".vivarium/unbound/T3Personal/Settings.g.cs";
        if (bound && (binding.GetProperty("repository").GetString() != "product-proof" ||
            binding.GetProperty("branch").GetString() != "main" || binding.GetProperty("path").GetString() != path ||
            binding.GetProperty("trustReference").GetString() != "local-owned"))
            throw new InvalidDataException("Binding differs from the explicitly trusted proof fixture.");
        await CopyCommittedAsync(settingsRepository, settingsCommit, path, "Settings.g.cs");
        var userPath = path[..path.LastIndexOf('/')] + "/UserCode.cs";
        await CopyCommittedAsync(settingsRepository, settingsCommit, userPath, "UserCode.cs");
        var plan = await EvaluateAsync(controlOnly: false);
        return new(controlCommit, bound ? "product-proof" : "control-proof", settingsCommit,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(plan))), plan);
    }
}
