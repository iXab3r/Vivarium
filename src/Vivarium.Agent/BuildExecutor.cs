using System.Diagnostics;
using System.Runtime.InteropServices;
using Google.Protobuf;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;
using Vivarium.Contracts.V1;

namespace Vivarium.Agent;

/// <summary>
/// Runs one BuildAssignment: fetch payload → run steps → collect artifacts (D3).
/// The machine's cleanup is the controller's problem (clean policies, D5) — not this class's.
/// </summary>
public static partial class BuildExecutor
{
    private static readonly TimeSpan ForceTerminationTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan OutputDrainTimeout = TimeSpan.FromSeconds(5);

    public static async Task<BuildResult> ExecuteAsync(
        string workRoot,
        BuildAssignment assignment,
        BlobClient blobs,
        Func<AgentMsg, CancellationToken, Task> send,
        string sessionId,
        CancellationToken ct)
    {
        using var stop = new BuildStopController(ct);
        return await ExecuteAsync(
            workRoot, assignment, blobs, send, sessionId, stop, observeProcess: null);
    }

    internal static async Task<BuildResult> ExecuteAsync(
        string workRoot,
        BuildAssignment assignment,
        BlobClient blobs,
        Func<AgentMsg, CancellationToken, Task> send,
        string sessionId,
        BuildStopController stop,
        Action<Process?>? observeProcess,
        DockerRuntimeOptions? dockerRuntime = null,
        Action<ContainerJournalRecord>? observeContainer = null)
    {
        var workdir = ResolveUnder(workRoot, assignment.BuildId);
        Directory.CreateDirectory(workdir);

        var result = new BuildResult { BuildId = assignment.BuildId, SessionId = sessionId };
        var publisher = assignment.ArtifactPublicationContractMajor is 1 or 2
            ? new LiteralArtifactPublisher(allowRules: assignment.ArtifactPublicationContractMajor == 2) : null;

        try
        {
            await SendStatusAsync(
                send, assignment.BuildId, -1, "FETCHING", stop.GracefulToken);
            if (assignment.VcsCheckout is { } source)
            {
                result.VcsCheckout = await CheckoutAsync(workRoot, workdir, assignment.BuildId, source, stop, observeProcess);
                await send(new AgentMsg { Status = new StepStatus { BuildId = assignment.BuildId, StepIndex = -1,
                    Phase = "FETCHING", VcsCheckout = result.VcsCheckout.Clone() } }, stop.GracefulToken);
                if (!result.VcsCheckout.Succeeded)
                {
                    for (var index = 0; index < assignment.Steps.Count; index++) result.Steps.Add(new StepResult { StepIndex = index, Skipped = true });
                    result.Outcome = BuildOutcome.Failed;
                    return result;
                }
            }
            foreach (var blob in assignment.Payload)
            {
                await FetchBlobAsync(
                    blob, blob.PrivateInput ? PrivateInputs(workRoot, assignment.BuildId) : workdir,
                    assignment.BuildId, sessionId, blobs, stop.GracefulToken);
            }
        }
        catch (OperationCanceledException) when (stop.Mode != BuildStopMode.Unspecified)
        {
            return CancelledResult(result, stop);
        }

        var anyFailed = false;
        var stopping = false;
        for (var i = 0; i < assignment.Steps.Count; i++)
        {
            var step = assignment.Steps[i];
            if (stop.Mode == BuildStopMode.Force)
            {
                stopping = true;
                result.Steps.Add(new StepResult { StepIndex = i, Skipped = true });
                continue;
            }
            if (stop.Mode == BuildStopMode.Graceful)
            {
                stopping = true;
            }
            if (stopping && step.Policy != StepPolicy.Always ||
                !stopping && !ShouldRun(step.Policy, anyFailed))
            {
                result.Steps.Add(new StepResult { StepIndex = i, Skipped = true });
                continue;
            }

            var stepToken = stopping ? stop.ForceToken : stop.GracefulToken;
            StepExecution execution;
            try
            {
                await SendStatusAsync(
                    send,
                    assignment.BuildId,
                    i,
                    stopping ? "CANCELLATION_CLEANUP" : "RUNNING",
                    stepToken);
                if (step.Container is not null)
                {
                    Directory.CreateDirectory(ResolveUnder(workdir, "results"));
                    if (dockerRuntime is null || observeContainer is null)
                        throw new InvalidDataException("The container runtime is not installed.");
                    var container = await DockerWorkload.RunAsync(dockerRuntime, workdir, assignment, i, step,
                        send, stop, stepToken, observeContainer, publisher);
                    execution = new(container.Result, stop.Mode != BuildStopMode.Unspecified, container.OutputComplete);
                }
                else execution = await RunStepAsync(
                    workdir, assignment, i, step, send, stop, stepToken, observeProcess, publisher);
            }
            catch (OperationCanceledException) when (stop.Mode != BuildStopMode.Unspecified)
            {
                result.Steps.Add(new StepResult { StepIndex = i, Skipped = true });
                stopping = true;
                continue;
            }
            result.Steps.Add(execution.Result);
            if (stop.Mode != BuildStopMode.Force)
            {
                try
                {
                    await send(new AgentMsg { Status = new StepStatus { BuildId = assignment.BuildId,
                        StepIndex = i, Phase = "RUNNING", Completed = execution.Result.Clone() } }, stop.ForceToken);
                }
                catch (OperationCanceledException) when (stop.Mode == BuildStopMode.Force) { }
            }
            if (!execution.OutputComplete && assignment.OutputContractMajor == 1) result.OutputProducerLost = true;
            if (execution.Stopped)
            {
                stopping = true;
                continue;
            }
            if (execution.Result.ExitCode != 0 || execution.Result.TimedOut)
            {
                anyFailed = true;
            }
        }

        if (stop.Mode != BuildStopMode.Force)
        {
            var collectionToken = stop.Mode == BuildStopMode.Graceful
                ? stop.ForceToken
                : stop.GracefulToken;
            try
            {
                await SendStatusAsync(
                    send, assignment.BuildId, -1, "COLLECTING", collectionToken);
                var publications = MatchCollectGlobs(workdir, assignment.Collect).Select(p => (Source: p, Path: p))
                    .Concat(publisher?.PublishedFiles(workdir, collectionToken) ?? []).ToArray();
                if (publications.GroupBy(p => p.Path, StringComparer.OrdinalIgnoreCase).Any(g => g.Select(p => p.Source).Distinct(StringComparer.Ordinal).Count() > 1))
                    throw new InvalidDataException("Collected artifact paths conflict.");
                foreach (var file in publications.Distinct().OrderBy(p => p.Path, StringComparer.Ordinal))
                {
                    var fullPath = Path.Combine(workdir, file.Source);
                    var sha = await blobs.UploadAsync(
                        fullPath,
                        assignment.BuildId,
                        sessionId,
                        collectionToken);
                    result.Artifacts.Add(new Artifact
                    {
                        Path = file.Path.Replace('\\', '/'),
                        Sha256 = sha,
                        Size = new FileInfo(fullPath).Length,
                        ModificationTimeUnixMs = new DateTimeOffset(File.GetLastWriteTimeUtc(fullPath)).ToUnixTimeMilliseconds(),
                    });
                }
            }
            catch (OperationCanceledException) when (stop.Mode != BuildStopMode.Unspecified)
            {
            }
        }

        if (stop.Mode != BuildStopMode.Unspecified)
        {
            return CancelledResult(result, stop);
        }

        result.Outcome = anyFailed ? BuildOutcome.Failed : BuildOutcome.Succeeded;

        return result;
    }

    private static async Task FetchBlobAsync(
        Blob blob,
        string workdir,
        string buildId,
        string sessionId,
        BlobClient blobs,
        CancellationToken ct)
    {
        if (blob.Archive)
        {
            var zipPath = Path.Combine(workdir, ".payload", blob.Sha256 + ".zip");
            await blobs.DownloadAsync(blob.Sha256, zipPath, buildId, sessionId, ct);
            var destination = ResolveUnder(workdir, blob.UnpackTo);
            Directory.CreateDirectory(destination);
            PayloadArchiveExtractor.Extract(zipPath, destination);
            File.Delete(zipPath);
        }
        else
        {
            var target = ResolveUnder(workdir, blob.FileName);
            await blobs.DownloadAsync(blob.Sha256, target, buildId, sessionId, ct);
        }
    }

    private static string ResolveUnder(string workdir, string relative)
    {
        var root = Path.GetFullPath(workdir);
        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var relativeToRoot = Path.GetRelativePath(root, full);
        if (Path.IsPathRooted(relativeToRoot) ||
            relativeToRoot.Equals("..", StringComparison.Ordinal) ||
            relativeToRoot.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"payload path escapes the workdir: '{relative}'");
        }

        return full;
    }

    private static bool ShouldRun(StepPolicy policy, bool anyFailed) => policy switch
    {
        StepPolicy.EvenIfFailed => true,
        StepPolicy.Always => true,
        _ => !anyFailed,
    };

    private static async Task<StepExecution> RunStepAsync(
        string workdir,
        BuildAssignment assignment,
        int stepIndex,
        Step step,
        Func<AgentMsg, CancellationToken, Task> send,
        BuildStopController stop,
        CancellationToken ct,
        Action<Process?>? observeProcess, LiteralArtifactPublisher? publisher)
    {
        var resultsDir = ResolveUnder(workdir, "results");
        Directory.CreateDirectory(resultsDir);
        var workingDirectory = step.Cwd.Length > 0 ? ResolveUnder(workdir, step.Cwd) : workdir;

        var psi = new ProcessStartInfo
        {
            FileName = ResolveProgram(workdir, workingDirectory, step.Program),
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = assignment.OutputContractMajor == 1,
        };
        foreach (var arg in step.Args)
        {
            psi.ArgumentList.Add(arg);
        }
        if (step.ScriptInput.Length != 0)
        {
            if (assignment.VcsCheckout is null || step.ScriptArgumentIndex < 0 || step.ScriptArgumentIndex >= step.Args.Count
                || step.Args[step.ScriptArgumentIndex].Replace('\\', '/') != step.ScriptInput.Replace('\\', '/'))
                throw new InvalidDataException("The generated script input differs from its argument.");
            psi.ArgumentList[step.ScriptArgumentIndex] = ResolveUnder(PrivateInputs(Path.GetDirectoryName(workdir)!, assignment.BuildId), step.ScriptInput);
        }

        psi.Environment["VIVARIUM_BUILD_ID"] = assignment.BuildId;
        psi.Environment["VIVARIUM_WORKDIR"] = workdir;
        psi.Environment["VIVARIUM_RESULTS_DIR"] = resultsDir;
        if (assignment.Parameters.TryGetValue("cell", out var cell))
        {
            psi.Environment["VIVARIUM_CELL"] = cell;
        }

        foreach (var (key, value) in assignment.Parameters)
        {
            psi.Environment["VIVARIUM_PARAM_" + key.ToUpperInvariant()] = value;
        }

        foreach (var (key, value) in step.Env)
        {
            psi.Environment[key] = value;
        }

        // Retire a prior container's removal proof before a new native creation can race a crash.
        observeProcess?.Invoke(null);
        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"failed to start '{step.Program}'");
        try
        {
            observeProcess?.Invoke(process);
        }
        catch
        {
            await ForceTerminateAsync(process);
            throw;
        }

        using var outputCts = new CancellationTokenSource();
        var outputFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stdout = PumpAsync(process.StandardOutput.BaseStream, LogStream.Stdout, outputCts.Token);
        var stderr = PumpAsync(process.StandardError.BaseStream, LogStream.Stderr, outputCts.Token);

        var timedOut = false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (step.TimeoutSec > 0)
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(step.TimeoutSec));
        }

        try
        {
            var exit = process.WaitForExitAsync(timeout.Token);
            if (await Task.WhenAny(exit, outputFailed.Task) == outputFailed.Task)
                await ForceTerminateAsync(process);
            await exit;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            timedOut = true;
            await ForceTerminateAsync(process);
        }
        catch (OperationCanceledException) when (stop.Mode != BuildStopMode.Unspecified)
        {
            await StopProcessAsync(process, stop, outputFailed.Task);
        }

        if (!process.HasExited)
        {
            throw new WorkloadTerminationException(
                $"workload process {process.Id} remained alive after termination");
        }
        observeProcess?.Invoke(null);
        var outputComplete = await DrainOutputAsync(stdout, stderr, outputCts);
        if (outputComplete) publisher?.FinishStep();
        return new StepExecution(new StepResult
        {
            StepIndex = stepIndex,
            ExitCode = process.ExitCode,
            TimedOut = timedOut,
        }, stop.Mode != BuildStopMode.Unspecified, outputComplete);

        async Task PumpAsync(Stream source, LogStream stream, CancellationToken outputToken)
        {
            var buffer = new byte[8192];
            int read;
            try
            {
                while ((read = await source.ReadAsync(buffer, outputToken)) > 0)
                {
                    if (stream == LogStream.Stdout) publisher?.Append(buffer.AsSpan(0, read));
                    await send(new AgentMsg
                    {
                        Log = new LogChunk
                        {
                            BuildId = assignment.BuildId,
                            StepIndex = stepIndex,
                            Stream = stream,
                            Data = ByteString.CopyFrom(buffer, 0, read),
                        },
                    }, outputToken);
                }
            }
            catch (OperationCanceledException) when (outputToken.IsCancellationRequested)
            {
            }
            catch
            {
                // A failed reader must not leave a live producer blocked forever on a full pipe.
                outputFailed.TrySetResult();
                throw;
            }
        }
    }

    private static async Task StopProcessAsync(Process process, BuildStopController stop, Task outputFailed)
    {
        if (stop.Mode == BuildStopMode.Graceful && !process.HasExited)
        {
            RequestGracefulTermination(process);
            try
            {
                // A graceful deadline is controller evidence, not permission for a hard kill. Keep
                // the Agent control loop alive and wait for either process exit or an explicit,
                // authorized force command that cancels ForceToken. A broken output reader is
                // an independent execution failure requiring containment, including during stop.
                var exit = process.WaitForExitAsync(stop.ForceToken);
                if (await Task.WhenAny(exit, outputFailed) == outputFailed)
                    await ForceTerminateAsync(process);
                await exit;
            }
            catch (OperationCanceledException) when (stop.Mode == BuildStopMode.Force)
            {
            }
        }

        if (!process.HasExited && stop.Mode == BuildStopMode.Force)
        {
            await ForceTerminateAsync(process);
        }
    }

    private static void RequestGracefulTermination(Process process)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                _ = process.CloseMainWindow();
                return;
            }
            _ = Kill(process.Id, 15); // SIGTERM; hard-stop requires a later explicit force command.
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static async Task ForceTerminateAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new WorkloadTerminationException(
                $"could not force-terminate workload process {process.Id}", exception);
        }

        try
        {
            await process.WaitForExitAsync().WaitAsync(ForceTerminationTimeout);
        }
        catch (TimeoutException exception)
        {
            throw new WorkloadTerminationException(
                $"workload process {process.Id} ignored force termination", exception);
        }
    }

    private static async Task<bool> DrainOutputAsync(
        Task stdout,
        Task stderr,
        CancellationTokenSource outputCts)
    {
        try
        {
            await Task.WhenAll(stdout, stderr).WaitAsync(OutputDrainTimeout);
            return true;
        }
        catch (TimeoutException)
        {
            outputCts.Cancel();
            try
            {
                await Task.WhenAll(stdout, stderr);
            }
            catch (OperationCanceledException)
            {
            }
            return false;
        }
    }

    private static BuildResult CancelledResult(BuildResult result, BuildStopController stop)
    {
        result.Outcome = BuildOutcome.Cancelled;
        result.StatusText = stop.Reason ?? (stop.Mode == BuildStopMode.Force
            ? "build force-stopped"
            : "build cancelled");
        return result;
    }

    private sealed record StepExecution(StepResult Result, bool Stopped, bool OutputComplete);

    internal static string PrivateInputsForContainer(string workdir, string buildId) =>
        PrivateInputs(Path.GetDirectoryName(workdir)!, buildId);

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int Kill(int processId, int signal);

    private static string ResolveProgram(string workdir, string workingDirectory, string program)
    {
        if (Path.IsPathRooted(program))
        {
            return program;
        }

        var normalized = program.Replace('/', Path.DirectorySeparatorChar);
        var candidate = Path.GetFullPath(Path.Combine(workingDirectory, normalized));
        var pathLike = program.Contains('/') || program.Contains('\\');
        if (!pathLike && !File.Exists(candidate))
        {
            return program; // A system command resolved through PATH.
        }

        var relativeToWorkdir = Path.GetRelativePath(workdir, candidate);
        return ResolveUnder(workdir, relativeToWorkdir);
    }

    private static IEnumerable<string> MatchCollectGlobs(string workdir, IEnumerable<string> globs)
    {
        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        var any = false;
        foreach (var glob in globs)
        {
            matcher.AddInclude(glob);
            any = true;
        }

        if (!any)
        {
            return [];
        }

        var result = matcher.Execute(new DirectoryInfoWrapper(new DirectoryInfo(workdir)));
        return result.Files.Select(f => f.Path);
    }

    private static Task SendStatusAsync(Func<AgentMsg, CancellationToken, Task> send, string buildId, int stepIndex, string phase, CancellationToken ct) =>
        send(new AgentMsg
        {
            Status = new StepStatus { BuildId = buildId, StepIndex = stepIndex, Phase = phase },
        }, ct);
}
