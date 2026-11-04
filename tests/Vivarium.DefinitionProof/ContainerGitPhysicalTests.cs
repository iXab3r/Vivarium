using System.Text;
using System.Text.Json;
using Vivarium.Agent;
using Vivarium.Contracts.V1;

namespace Vivarium.DefinitionProof;

[NonParallelizable, Category("ContainerSdkPhysical")]
public sealed class ContainerGitPhysicalTests
{
    [Test]
    public async Task Actual_checkout_allows_local_git_config_and_nested_HEAD_without_writable_script_inputs()
    {
        var directory = Path.GetFullPath(Path.Combine("artifacts/pilot-chain", "container-git-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        await using var source = new OwnedGitSource(directory);
        var revision = await source.StartAsync();
        var originalConfig = await File.ReadAllBytesAsync(Path.Combine(directory, "source.git/config"));
        await File.WriteAllTextAsync(Path.Combine(directory, "source.json"), JsonSerializer.Serialize(new { source.Url, Revision = revision }));
        await using var runtime = new OwnedContainerRuntime();
        await runtime.StartAsync();
        var worker = await runtime.WorkerAsync(TestContext.CurrentContext.TestDirectory, "--container-git-worker", directory);
        await File.WriteAllBytesAsync(Path.Combine(directory, "worker.stdout"), worker.Output);
        Assert.That(worker.ExitCode, Is.Zero, Encoding.UTF8.GetString(worker.Error));
        using var receipt = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "git-receipt.json")));
        Assert.That(receipt.RootElement.GetProperty("outcome").GetString(), Is.EqualTo(nameof(BuildOutcome.Succeeded)), receipt.RootElement.GetRawText());
        Assert.That(receipt.RootElement.GetProperty("revision").GetString(), Is.EqualTo(revision));
        Assert.That(receipt.RootElement.GetProperty("output").GetString(), Does.Contain("git-config-and-private-inputs-ok"));
        Assert.That(await File.ReadAllBytesAsync(Path.Combine(directory, "source.git/config")), Is.EqualTo(originalConfig));
        Assert.That(await runtime.RemainingAsync(), Is.Empty);
    }

    internal static async Task<int> WorkerAsync(string scope)
    {
        using var source = JsonDocument.Parse(await File.ReadAllTextAsync("/results/source.json"));
        const string build = "owned-git-config", root = "/data/builds";
        var inputs = Path.Combine(root, ".inputs", build);
        Directory.CreateDirectory(inputs);
        const string script = """
            set -eu
            test "$(git rev-parse HEAD)" = "$EXPECTED_REVISION"
            git config --local proof.root writable
            mkdir child
            cd child
            git config --local proof.nested writable
            test "$(git config --local proof.root)" = writable
            test "$(git config --local proof.nested)" = writable
            test "$(git rev-parse HEAD)" = "$EXPECTED_REVISION"
            if (echo changed > /vivarium-inputs/proof.sh) 2>/dev/null; then exit 71; fi
            if (echo changed > /workspace/.git) 2>/dev/null; then exit 72; fi
            if (echo changed > /vivarium-inputs/new-input) 2>/dev/null; then exit 73; fi
            echo git-config-and-private-inputs-ok
            """;
        await File.WriteAllTextAsync(Path.Combine(inputs, "proof.sh"), script);
        var assignment = new BuildAssignment { BuildId = build, VcsCheckout = new() {
            Url = source.RootElement.GetProperty("Url").GetString()!, Branch = "refs/heads/main",
            Revision = source.RootElement.GetProperty("Revision").GetString()! } };
        var step = new Step { Program = "/bin/sh", ScriptInput = "proof.sh", ScriptArgumentIndex = 0,
            TimeoutSec = 30, Container = new() { Image = OwnedContainerRuntime.Sdk, Platform = "linux/amd64", User = "0", Init = true } };
        step.Args.Add("proof.sh");
        step.Env["EXPECTED_REVISION"] = assignment.VcsCheckout.Revision;
        assignment.Steps.Add(step);
        var output = new StringBuilder();
        using var blobs = new BlobClient("https://localhost", "unused");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var stop = new BuildStopController(deadline.Token);
        var result = await BuildExecutor.ExecuteAsync(root, assignment, blobs, (message, _) => {
            if (message.Log is { } log) output.Append(log.Data.ToStringUtf8());
            return Task.CompletedTask;
        }, "owned-git-session", stop, null, new("https://docker:2376", "/cert-client", scope), _ => { });
        Assert.That(await File.ReadAllTextAsync(Path.Combine(inputs, "proof.sh")), Is.EqualTo(script));
        await File.WriteAllTextAsync("/results/git-receipt.json", JsonSerializer.Serialize(new {
            outcome = result.Outcome.ToString(), revision = result.VcsCheckout?.Revision, output = output.ToString() }));
        return 0;
    }
}
