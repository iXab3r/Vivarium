using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Vivarium.Agent;
using Vivarium.Contracts.V1;

namespace Vivarium.DefinitionProof;

[NonParallelizable, Category("ContainerSdkPhysical")]
public sealed class ContainerSdkPhysicalTests
{
    [Test]
    public async Task Actual_engine_proves_process_outcomes_output_stop_and_durable_container_recovery()
    {
        var directory = Path.GetFullPath(Path.Combine("artifacts/pilot-chain", "container-sdk-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        await using var runtime = new OwnedContainerRuntime();
        await runtime.StartAsync();
        var running = runtime.WorkerAsync(TestContext.CurrentContext.TestDirectory, "--container-sdk-worker", directory);
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
        {
            while (!File.Exists(Path.Combine(directory, "at-rest-ready")) && !running.IsCompleted)
                await Task.Delay(100, deadline.Token);
            Assert.That(running.IsCompleted, Is.False, "The metadata scan must observe a live workload, before removal.");
            await runtime.VerifyVolatileSecretBoundaryAsync();
            await File.WriteAllTextAsync(Path.Combine(directory, "at-rest-continue"), "continue");
        }
        var worker = await running;
        await File.WriteAllBytesAsync(Path.Combine(directory, "worker.stdout"), worker.Output);
        using var receipt = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "receipt.json")));
        Assert.That(worker.ExitCode, Is.Zero, receipt.RootElement.GetRawText());
        Assert.That(receipt.RootElement.GetProperty("complete").GetBoolean(), Is.True);
        Assert.That(receipt.RootElement.GetProperty("cases").GetArrayLength(), Is.EqualTo(7));
        Assert.That(await runtime.RemainingAsync(), Is.Empty);
        var orphan = await runtime.WorkerAsync(TestContext.CurrentContext.TestDirectory, "--container-orphan-worker", directory);
        Assert.That(orphan.ExitCode, Is.EqualTo(73), "The owning process exits while the remote Node is still alive.");
        Assert.That(await runtime.NodeRunningAsync(), Is.True);
        var recovered = await runtime.WorkerAsync(TestContext.CurrentContext.TestDirectory, "--container-recovery-worker", directory);
        Assert.That(recovered.ExitCode, Is.Zero, Encoding.UTF8.GetString(recovered.Error));
        using var recovery = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "recovery.json")));
        Assert.That(recovery.RootElement.GetProperty("complete").GetBoolean(), Is.True);
        Assert.That(await runtime.RemainingAsync(), Is.Empty);
    }

    internal static async Task<int> OrphanWorkerAsync(string scope)
    {
        const string data = "/data/builds/orphan-private", build = "owned-orphan";
        Directory.CreateDirectory(data); Directory.CreateDirectory("/data/builds/" + build);
        var journal = new ActiveBuildJournal(data); journal.Accept(build);
        using var stop = new BuildStopController();
        var step = new Step { Program = "node", Container = new() { Image = OwnedContainerRuntime.Node,
            Platform = "linux/amd64", User = "0", Init = true } };
        step.Args.Add(["-e", "process.stdout.write('owned-orphan-ready\\n');setInterval(()=>{},1000)"]);
        await DockerWorkload.RunAsync(new("https://docker:2376", "/cert-client", scope), "/data/builds/" + build,
            new() { BuildId = build }, 0, step, (message, _) => {
                if (message.Log?.Data.ToStringUtf8().Contains("owned-orphan-ready", StringComparison.Ordinal) == true)
                    Environment.Exit(73); // No finally: the fresh process must recover durable remote ownership.
                return Task.CompletedTask;
            }, stop, stop.GracefulToken, c => journal.ObserveContainer(build, c), null);
        return 1;
    }

    internal static async Task<int> RecoveryWorkerAsync(string scope)
    {
        var journal = new ActiveBuildJournal("/data/builds/orphan-private");
        var record = journal.Current ?? throw new InvalidDataException("The interrupted journal is absent.");
        var options = new DockerRuntimeOptions("https://docker:2376", "/cert-client", scope);
        Assert.That(record.Container?.Phase, Is.EqualTo("CREATED"));
        Assert.ThrowsAsync<InvalidDataException>(() => DockerWorkload.RecoverAsync(options, record.Container!, "foreign-build"));
        Assert.ThrowsAsync<InvalidDataException>(() => DockerWorkload.RecoverAsync(options,
            record.Container! with { EngineId = "different-engine" }, record.BuildId));
        await DockerWorkload.RecoverAsync(options, record.Container!, record.BuildId);
        journal.Complete(record.BuildId);
        await File.WriteAllTextAsync("/results/recovery.json", JsonSerializer.Serialize(new { complete = true,
            record.BuildId, record.Container!.EngineId, record.Container.Id, foreignBuildRejected = true, changedEngineRejected = true }));
        return 0;
    }

    internal static async Task<int> WorkerAsync(string scope)
    {
        var observations = new List<object>();
        var options = new DockerRuntimeOptions("https://docker:2376", "/cert-client", scope);
        try
        {
            foreach (var mode in new[] { "at-rest", "success", "exit7", "missing-image", "graceful", "force", "output-failure" })
            {
                var buildId = "owned-" + mode;
                var data = "/data/builds/private-" + mode;
                Directory.CreateDirectory(data);
                var work = "/data/builds/" + buildId; Directory.CreateDirectory(work);
                Directory.CreateDirectory(Path.Combine(work, "results"));
                var journal = new ActiveBuildJournal(data); journal.Accept(buildId);
                using var stop = new BuildStopController();
                var assignment = new BuildAssignment { BuildId = buildId };
                var fake = "synthetic-container-mask-" + Guid.NewGuid().ToString("N");
                var output = BuildOutputSpool.Create(data, buildId, 1, [ByteString.CopyFromUtf8(fake)]);
                var step = new Step { Program = "node", Container = new() { Image = mode == "missing-image"
                    ? "node@sha256:" + new string('0', 64) : OwnedContainerRuntime.Node, Platform = "linux/amd64", User = "0", Init = true }, TimeoutSec = 30 };
                step.Env["OWNED_MASK"] = fake;
                var waiting = mode is "at-rest" or "graceful" or "force" or "output-failure";
                var script = "process.stdout.write(process.env.OWNED_MASK+'\\n');process.stderr.write('owned-stderr\\n');"
                    + (waiting ? "process.stdout.write('owned-ready\\n');setInterval(()=>{},1000);" : mode == "exit7" ? "process.exit(7);" : "require('fs').writeFileSync('/workspace/proof.txt','coherent-reference-version\\n');");
                step.Args.Add(["-e", script]);
                var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var run = DockerWorkload.RunAsync(options, work, assignment, 0, step, (message, _) =>
                {
                    if (message.Log is { } log)
                    {
                        output.Append(0, log.Stream, log.Data.Span);
                        if (log.Data.ToStringUtf8().Contains("owned-ready", StringComparison.Ordinal))
                        {
                            started.TrySetResult();
                            if (mode == "output-failure") throw new IOException("Owned output sink failed.");
                        }
                    }
                    return Task.CompletedTask;
                }, stop, stop.GracefulToken, c => journal.ObserveContainer(buildId, c), null);
                if (waiting)
                {
                    await started.Task.WaitAsync(TimeSpan.FromSeconds(45));
                    // A reopened on-disk journal must independently identify the actual container.
                    var retained = new ActiveBuildJournal(data).Current!;
                    Assert.That(retained.Container?.Phase, Is.EqualTo("CREATED"));
                    if (mode == "at-rest")
                    {
                        using var engine = new DockerEngine(options);
                        using var metadata = await engine.GetAsync("containers/" + retained.Container!.Id + "/json", CancellationToken.None);
                        Assert.That(metadata!.RootElement.GetProperty("Config").GetProperty("Env").EnumerateArray()
                            .Select(e => e.GetString()), Has.None.Contains(fake));
                        Assert.That(metadata.RootElement.GetProperty("HostConfig").GetProperty("LogConfig").GetProperty("Type").GetString(), Is.EqualTo("none"));
                        await File.WriteAllTextAsync("/results/at-rest-ready", "ready");
                        using var gate = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                        while (!File.Exists("/results/at-rest-continue")) await Task.Delay(50, gate.Token);
                        stop.Request(BuildStopMode.Force, "Owned metadata scan finished", null);
                    }
                    if (mode == "graceful") stop.Request(BuildStopMode.Graceful, "Owned cancellation", null);
                    if (mode == "force") stop.Request(BuildStopMode.Force, "Owned force", null);
                }
                var result = await run.WaitAsync(TimeSpan.FromSeconds(45));
                Assert.That(result.Result.Container.Removed, Is.EqualTo(mode != "missing-image"));
                Assert.That(result.Result.Container.HasProcessExitCode, Is.EqualTo(mode != "missing-image"));
                if (mode == "exit7") Assert.That(result.Result.Container.ProcessExitCode, Is.EqualTo(7));
                if (mode == "success")
                {
                    Assert.That(result.Result.Container.ProcessExitCode, Is.Zero);
                    Assert.That(await File.ReadAllTextAsync(Path.Combine(work, "proof.txt")), Is.EqualTo("coherent-reference-version\n"));
                }
                if (mode == "missing-image")
                {
                    Assert.That(result.Result.ExitCode, Is.EqualTo(1));
                    Assert.That(result.Result.Container.FailureCode, Is.EqualTo("image_unavailable"));
                }
                if (mode == "output-failure") { Assert.That(result.OutputComplete, Is.False); output.MarkProducerLost(); }
                else output.Seal();
                var persisted = string.Concat(output.Snapshot("owned").Select(o => o.Data.ToStringUtf8()));
                Assert.That(persisted, Does.Not.Contain(fake));
                if (mode != "missing-image") Assert.That(persisted, Does.Contain("[REDACTED]"));
                var recovery = new ActiveBuildJournal(data).Current!.Container!;
                await DockerWorkload.RecoverAsync(options, recovery, buildId);
                if (mode == "success")
                {
                    Assert.ThrowsAsync<InvalidDataException>(() => DockerWorkload.RecoverAsync(options,
                        recovery with { EngineId = "different-engine" }, buildId));
                    Assert.ThrowsAsync<InvalidDataException>(() => DockerWorkload.RecoverAsync(options,
                        recovery with { Id = null, Phase = "CREATING" }, buildId));
                }
                observations.Add(new { mode, exit = result.Result.ExitCode, receipt = new { result.Result.Container.EngineId,
                    result.Result.Container.ContainerId, result.Result.Container.ImageId, result.Result.Container.ExecId,
                    processExit = result.Result.Container.HasProcessExitCode ? (int?)result.Result.Container.ProcessExitCode : null,
                    result.Result.Container.Removed, result.Result.Container.FailureCode }, result.OutputComplete, masked = !persisted.Contains(fake) });
                journal.Complete(buildId);
            }
            await File.WriteAllTextAsync("/results/receipt.json", JsonSerializer.Serialize(new { complete = true, cases = observations }));
            return 0;
        }
        catch (Exception exception)
        {
            await File.WriteAllTextAsync("/results/receipt.json", JsonSerializer.Serialize(new { complete = false, cases = observations,
                failure = exception.GetType().Name, detail = exception.Message }));
            return 1;
        }
    }
}
