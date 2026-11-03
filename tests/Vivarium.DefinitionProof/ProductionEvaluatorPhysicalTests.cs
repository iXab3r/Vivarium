using System.Text.Json;
using Vivarium.Controller.Catalog;

namespace Vivarium.DefinitionProof;

[NonParallelizable]
public sealed class ProductionEvaluatorPhysicalTests
{
    internal static Dictionary<string, string> Sources(string? body = null) => new()
    {
        ["Control.g.cs"] = """
            using Vivarium.Definitions;
            public static class Control {
                public static Project Declare() => new("P", "_Root", "Project P", "", null);
            }
            """,
        ["Settings.g.cs"] = """
            using Vivarium.Definitions;
            public static class Managed {
                public static Settings Declare() {
            BODY
                    return new("P", null, [], [
                        new("P_LocalSmoke", "Local smoke", [], [
                            new("echo", "Echo", "simpleRunner", [new("script.content", "echo from CSharp Git", null)])
                        ], [], [], [], 1, [])
                    ]);
                }
            }
            """.Replace("BODY", body ?? "", StringComparison.Ordinal),
        ["UserCode.cs"] = "public static class UserCode { }",
    };

    [Test]
    public async Task Product_compiler_evaluates_the_owned_source_shape_inside_the_pinned_boundary()
    {
        var evaluator = new IsolatedDefinitionEvaluator(Guid.NewGuid());
        var result = await evaluator.EvaluateAsync(Sources());
        Assert.Multiple(() =>
        {
            Assert.That(result.ExitCode, Is.Zero, result.Error);
            Assert.That(result.DeadlineExceeded || result.OutputExceeded || result.OomKilled, Is.False);
            Assert.That(result.Container.GetProperty("Config").GetProperty("User").GetString(), Is.EqualTo("65532:65532"));
            Assert.That(result.Container.GetProperty("HostConfig").GetProperty("NetworkMode").GetString(), Is.EqualTo("none"));
            Assert.That(result.Container.GetProperty("HostConfig").GetProperty("ReadonlyRootfs").GetBoolean(), Is.True);
        });
        var plan = DefinitionValidation.Parse(result.Output, "P");
        Assert.That(plan.Configurations.Single().Steps.Single().Properties.Single().Value, Is.EqualTo("echo from CSharp Git"));
        Assert.Throws<InvalidDataException>(() => DefinitionValidation.Parse(result.Output, "Q"));
        await AssertRemovedAsync(evaluator);
    }

    [Test]
    public async Task Author_output_cannot_replace_the_project_owner_or_bypass_Host_validation()
    {
        var evaluator = new IsolatedDefinitionEvaluator(Guid.NewGuid());
        var result = await evaluator.EvaluateAsync(Sources("Console.Write(\"{\\\"project\\\":{\\\"id\\\":\\\"Q\\\"}}\");"));
        Assert.That(result.ExitCode, Is.Zero);
        Assert.Throws<InvalidDataException>(() => DefinitionValidation.Parse(result.Output, "P"));
        await AssertRemovedAsync(evaluator);
    }

    [Test]
    public async Task The_container_does_not_receive_the_parent_environment_or_Host_paths()
    {
        const string key = "VIVARIUM_PRODUCTION_EVALUATOR_TEST_SENTINEL";
        var previous = Environment.GetEnvironmentVariable(key);
        Environment.SetEnvironmentVariable(key, "synthetic-parent-only-value");
        var evaluator = new IsolatedDefinitionEvaluator(Guid.NewGuid());
        try
        {
            var result = await evaluator.EvaluateAsync(Sources("""
                if (Environment.GetEnvironmentVariable("VIVARIUM_PRODUCTION_EVALUATOR_TEST_SENTINEL") is not null)
                    throw new InvalidOperationException("Parent environment leaked.");
                if (Directory.Exists("/workspace") || Directory.Exists("/host"))
                    throw new InvalidOperationException("Host path leaked.");
                """));
            Assert.That(result.ExitCode, Is.Zero, result.Error);
            DefinitionValidation.Parse(result.Output, "P");
        }
        finally { Environment.SetEnvironmentVariable(key, previous); }
        await AssertRemovedAsync(evaluator);
    }

    [Test]
    public async Task Flooded_author_output_is_bounded_and_the_owned_container_is_removed()
    {
        var evaluator = new IsolatedDefinitionEvaluator(Guid.NewGuid());
        var result = await evaluator.EvaluateAsync(Sources("while (true) Console.Write(new string('x', 4096));"));
        Assert.That(result.OutputExceeded, Is.True);
        Assert.That(System.Text.Encoding.UTF8.GetByteCount(result.Output + result.Error), Is.LessThanOrEqualTo(64 * 1024));
        await AssertRemovedAsync(evaluator);
    }

    [Test]
    public async Task Cancellation_stops_author_code_and_removes_its_container()
    {
        var evaluator = new IsolatedDefinitionEvaluator(Guid.NewGuid());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var result = await evaluator.EvaluateAsync(Sources("Thread.Sleep(Timeout.Infinite);"), cancellation.Token);
        Assert.That(result.DeadlineExceeded, Is.True);
        await AssertRemovedAsync(evaluator);
    }

    [Test]
    public void Repository_build_hooks_are_rejected_before_Docker_mutation()
    {
        var evaluator = new IsolatedDefinitionEvaluator(Guid.NewGuid());
        var sources = Sources(); sources["Directory.Build.targets"] = "<Project/>";
        Assert.ThrowsAsync<InvalidDataException>(() => evaluator.EvaluateAsync(sources));
    }

    [Test]
    public async Task Malformed_UTF8_author_output_is_rejected_and_the_container_is_removed()
    {
        var evaluator = new IsolatedDefinitionEvaluator(Guid.NewGuid());
        Assert.ThrowsAsync<InvalidDataException>(() => evaluator.EvaluateAsync(Sources(
            "Console.OpenStandardOutput().Write(new byte[] { 0xff, 0xfe });")));
        await AssertRemovedAsync(evaluator);
    }

    [Test]
    public async Task The_container_enforces_its_elapsed_bound_after_the_coordinator_is_killed()
    {
        var invocation = Guid.NewGuid();
        var evaluator = new IsolatedDefinitionEvaluator(invocation);
        try
        {
            await KillCoordinatorAfterAuthorStartsAsync(invocation);
            // Neither the Host timer nor its finally/cleanup can now stop the container.
            var stopped = await ProofProcess.RunAsync("docker", ["wait", evaluator.Name], deadline: TimeSpan.FromSeconds(40));
            Assert.That(stopped.Text, Is.EqualTo("137"));
            var final = await ProofProcess.RunAsync("docker", ["inspect", evaluator.Name]);
            using var state = JsonDocument.Parse(final.Output);
            Assert.That(state.RootElement[0].GetProperty("State").GetProperty("Running").GetBoolean(), Is.False);
        }
        finally { await IsolatedDefinitionEvaluator.RemoveOwnedAsync(evaluator.Name); }
        await AssertRemovedAsync(evaluator);
    }

    internal static async Task KillCoordinatorAfterAuthorStartsAsync(Guid invocation)
    {
        var evaluator = new IsolatedDefinitionEvaluator(invocation);
        var dotnet = Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(),
            "../../..", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
        using var worker = ProofProcess.Start(dotnet,
            [typeof(ProductionEvaluatorPhysicalTests).Assembly.Location, "--production-evaluator-worker", invocation.ToString()]);
        try
        {
            var until = DateTimeOffset.UtcNow.AddSeconds(15);
            var running = false;
            while (!worker.HasExited && DateTimeOffset.UtcNow < until)
            {
                var ready = await ProofProcess.RunAsync("docker", ["exec", evaluator.Name,
                    "/bin/test", "-f", "/work/author-ready"], allowFailure: true);
                running = ready.ExitCode == 0;
                if (running) break;
                await Task.Delay(100);
            }
            Assert.That(running, Is.True, "Do not substitute compile-only interruption for actual running author code.");
            worker.Kill(entireProcessTree: true);
            await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            if (!worker.HasExited) { worker.Kill(entireProcessTree: true); await worker.WaitForExitAsync(); }
        }
    }

    private static async Task AssertRemovedAsync(IsolatedDefinitionEvaluator evaluator)
    {
        var result = await ProofProcess.RunAsync("docker", ["ps", "-a", "--filter", "name=^/" + evaluator.Name + "$", "--format", "{{.ID}}"]);
        Assert.That(result.Text, Is.Empty);
    }
}
