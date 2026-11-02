using System.Text.Json;
using Vivarium.DefinitionProof;
using Vivarium.Controller.Catalog;

if (args is ["--production-evaluator-worker", var invocation])
{
    var sources = ProductionEvaluatorPhysicalTests.Sources("File.WriteAllText(\"/work/author-ready\", \"ready\"); UserCode.Attack();");
    sources["UserCode.cs"] = """
        using System.Runtime.InteropServices;
        public static class UserCode {
            [DllImport("libc", EntryPoint = "kill")] private static extern int Kill(int pid, int signal);
            public static void Attack() { Kill(1, 19); Thread.Sleep(Timeout.Infinite); }
        }
        """;
    var result = await new IsolatedDefinitionEvaluator(Guid.Parse(invocation)).EvaluateAsync(sources);
    return result.ExitCode;
}

if (args is ["--isolated-worker", var sourcePath, var containerName])
{
    var source = await File.ReadAllTextAsync(sourcePath);
    var result = await new IsolatedCompilerStudy(containerName).EvaluateAsync(
        new Dictionary<string, string> { ["Program.cs"] = source }, TimeSpan.FromMinutes(2));
    return result.ExitCode;
}

if (args is not ["--worker", var resourceFile, var operationText]) return 2;
var connection = Environment.GetEnvironmentVariable("VIVARIUM_PROOF_PG")
    ?? throw new InvalidOperationException("Private proof connection is absent.");
var resources = JsonSerializer.Deserialize<StudyResources>(await File.ReadAllTextAsync(resourceFile))
    ?? throw new InvalidDataException("Proof resources are absent.");
var outcome = await new RevisionSetStudy(connection, resources).AdvanceAsync(Guid.Parse(operationText));
Console.WriteLine(outcome);
return 0;
