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
public static class BuildExecutor
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
        Action<Process?>? observeProcess)
    {
        var workdir = ResolveUnder(workRoot, assignment.BuildId);
        Directory.CreateDirectory(workdir);

        var result = new BuildResult { BuildId = assignment.BuildId, SessionId = sessionId };

        try
        {
            await SendStatusAsync(
                send, assignment.BuildId, -1, "FETCHING", stop.GracefulToken);
            foreach (var blob in assignment.Payload)
            {
                await FetchBlobAsync(
                    blob, workdir, assignment.BuildId, sessionId, blobs, stop.GracefulToken);
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
                execution = await RunStepAsync(
                    workdir, assignment, i, step, send, stop, stepToken, observeProcess);
            }
            catch (OperationCanceledException) when (stop.Mode != BuildStopMode.Unspecified)
            {
                result.Steps.Add(new StepResult { StepIndex = i, Skipped = true });
                stopping = true;
                continue;
            }
            result.Steps.Add(execution.Result);
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
                foreach (var relativePath in MatchCollectGlobs(workdir, assignment.Collect))
                {
                    var fullPath = Path.Combine(workdir, relativePath);
                    var sha = await blobs.UploadAsync(
                        fullPath,
                        assignment.BuildId,
                        sessionId,
                        collectionToken);
                    result.Artifacts.Add(new Artifact
                    {
                        Path = relativePath.Replace('\\', '/'),
                        Sha256 = sha,
                        Size = new FileInfo(fullPath).Length,
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
        Action<Process?>? observeProcess)
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
        };
        foreach (var arg in step.Args)
        {
            psi.ArgumentList.Add(arg);
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
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            timedOut = true;
            await ForceTerminateAsync(process);
        }
        catch (OperationCanceledException) when (stop.Mode != BuildStopMode.Unspecified)
        {
            await StopProcessAsync(process, stop);
        }

        if (!process.HasExited)
        {
            throw new WorkloadTerminationException(
                $"workload process {process.Id} remained alive after termination");
        }
        observeProcess?.Invoke(null);
        await DrainOutputAsync(stdout, stderr, outputCts);
        return new StepExecution(new StepResult
        {
            StepIndex = stepIndex,
            ExitCode = timedOut || stop.Mode != BuildStopMode.Unspecified ? -1 : process.ExitCode,
            TimedOut = timedOut,
        }, stop.Mode != BuildStopMode.Unspecified);

        async Task PumpAsync(Stream source, LogStream stream, CancellationToken outputToken)
        {
            var buffer = new byte[8192];
            int read;
            try
            {
                while ((read = await source.ReadAsync(buffer, outputToken)) > 0)
                {
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
        }
    }

    private static async Task StopProcessAsync(Process process, BuildStopController stop)
    {
        if (stop.Mode == BuildStopMode.Graceful && !process.HasExited)
        {
            RequestGracefulTermination(process);
            try
            {
                // A graceful deadline is controller evidence, not permission for a hard kill. Keep
                // the Agent control loop alive and wait for either process exit or an explicit,
                // authorized force command that cancels ForceToken.
                await process.WaitForExitAsync(stop.ForceToken);
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

    private static async Task DrainOutputAsync(
        Task stdout,
        Task stderr,
        CancellationTokenSource outputCts)
    {
        try
        {
            await Task.WhenAll(stdout, stderr).WaitAsync(OutputDrainTimeout);
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

    private sealed record StepExecution(StepResult Result, bool Stopped);

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
