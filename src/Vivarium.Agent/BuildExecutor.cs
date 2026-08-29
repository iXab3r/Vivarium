using System.Diagnostics;
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
    public static async Task<BuildResult> ExecuteAsync(
        string workRoot,
        BuildAssignment assignment,
        BlobClient blobs,
        Func<AgentMsg, CancellationToken, Task> send,
        string sessionId,
        CancellationToken ct)
    {
        var workdir = ResolveUnder(workRoot, assignment.BuildId);
        Directory.CreateDirectory(workdir);

        var result = new BuildResult { BuildId = assignment.BuildId, SessionId = sessionId };

        await SendStatusAsync(send, assignment.BuildId, -1, "FETCHING", ct);
        foreach (var blob in assignment.Payload)
        {
            await FetchBlobAsync(blob, workdir, assignment.BuildId, sessionId, blobs, ct);
        }

        var anyFailed = false;
        for (var i = 0; i < assignment.Steps.Count; i++)
        {
            var step = assignment.Steps[i];
            if (!ShouldRun(step.Policy, anyFailed))
            {
                result.Steps.Add(new StepResult { StepIndex = i, Skipped = true });
                continue;
            }

            await SendStatusAsync(send, assignment.BuildId, i, "RUNNING", ct);
            var stepResult = await RunStepAsync(workdir, assignment, i, step, send, ct);
            result.Steps.Add(stepResult);
            if (stepResult.ExitCode != 0 || stepResult.TimedOut)
            {
                anyFailed = true;
            }
        }

        await SendStatusAsync(send, assignment.BuildId, -1, "COLLECTING", ct);
        foreach (var relativePath in MatchCollectGlobs(workdir, assignment.Collect))
        {
            var fullPath = Path.Combine(workdir, relativePath);
            var sha = await blobs.UploadAsync(
                fullPath,
                assignment.BuildId,
                sessionId,
                ct);
            result.Artifacts.Add(new Artifact
            {
                Path = relativePath.Replace('\\', '/'),
                Sha256 = sha,
                Size = new FileInfo(fullPath).Length,
            });
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

    private static async Task<StepResult> RunStepAsync(
        string workdir,
        BuildAssignment assignment,
        int stepIndex,
        Step step,
        Func<AgentMsg, CancellationToken, Task> send,
        CancellationToken ct)
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

        var stdout = PumpAsync(process.StandardOutput.BaseStream, LogStream.Stdout);
        var stderr = PumpAsync(process.StandardError.BaseStream, LogStream.Stderr);

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
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }

            throw;
        }

        await Task.WhenAll(stdout, stderr);
        return new StepResult
        {
            StepIndex = stepIndex,
            ExitCode = timedOut ? -1 : process.ExitCode,
            TimedOut = timedOut,
        };

        async Task PumpAsync(Stream source, LogStream stream)
        {
            var buffer = new byte[8192];
            int read;
            while ((read = await source.ReadAsync(buffer, CancellationToken.None)) > 0)
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
                }, CancellationToken.None);
            }
        }
    }

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
