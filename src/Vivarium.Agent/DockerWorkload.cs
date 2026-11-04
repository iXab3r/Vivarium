using System.Buffers.Binary;
using System.Text.Json;
using Google.Protobuf;
using Vivarium.Contracts;
using Vivarium.Contracts.V1;

namespace Vivarium.Agent;

/// <summary>One remote process, whose lifetime is fenced by its Engine and container identities.</summary>
internal static class DockerWorkload
{
    private const string OwnerLabel = "vivarium.runtime.scope";
    private const string BuildLabel = "vivarium.runtime.build";
    private const string Helper = "trap 'kill -TERM -1 2>/dev/null || :' TERM; while :; do sleep 1 & wait $! || :; done";

    internal static async Task<(StepResult Result, bool OutputComplete)> RunAsync(
        DockerRuntimeOptions options, string workdir, BuildAssignment assignment, int index, Step step,
        Func<AgentMsg, CancellationToken, Task> send, BuildStopController stop, CancellationToken token,
        Action<ContainerJournalRecord> observe, LiteralArtifactPublisher? publisher)
    {
        var spec = step.Container;
        ContainerStepProfile.Validate(spec);
        using var engine = new DockerEngine(options);
        var engineId = await Control(t => engine.IdentityAsync(t));
        var name = "vivarium-" + options.Scope + '-' + Guid.NewGuid().ToString("N");
        var journal = new ContainerJournalRecord(options.Scope, engineId, name, null, "PREPARING");
        observe(journal);
        var receipt = new ContainerStepReceipt { EngineId = engineId };
        var result = new StepResult { StepIndex = index, Container = receipt };
        try
        {
            using var pull = CancellationTokenSource.CreateLinkedTokenSource(token);
            pull.CancelAfter(TimeSpan.FromMinutes(5));
            await engine.EnsureImageAsync(spec.Image, spec.Platform, pull.Token);
        }
        catch (DockerEngineException exception) when (exception.Code == "image_pull_failed" || exception.Status == 404)
        {
            result.ExitCode = 1; receipt.FailureCode = "image_unavailable";
            return (result, true); // No container creation was issued and no user exit is invented.
        }

        var mounts = new List<object> { new { Type = "bind", Source = workdir, Target = "/workspace" } };
        if (assignment.VcsCheckout is not null || step.ScriptInput.Length != 0)
            mounts.Add(new { Type = "bind", Source = BuildExecutor.PrivateInputsForContainer(workdir, assignment.BuildId),
                Target = "/vivarium-inputs", ReadOnly = true });
        if (assignment.VcsCheckout is not null)
            mounts.Add(new { Type = "bind", Source = Path.Combine(BuildExecutor.PrivateInputsForContainer(workdir, assignment.BuildId), "container.git"),
                Target = "/workspace/.git", ReadOnly = true });
        foreach (var cache in spec.Caches)
        {
            var volume = "vivarium-" + options.Scope + '-' + cache.Name;
            using var existing = await Control(t => engine.GetAsync("volumes/" + volume, t, missingAllowed: true));
            if (existing is null)
            {
                using var created = await Control(t => engine.PostAsync("volumes/create", new {
                    Name = volume, Labels = new Dictionary<string, string> { [OwnerLabel] = options.Scope } }, t));
                CheckOwner(created.RootElement, options.Scope, null);
            }
            else CheckOwner(existing.RootElement, options.Scope, null);
            mounts.Add(new { Type = "volume", Source = volume, Target = cache.Target });
        }

        token.ThrowIfCancellationRequested();
        var creating = false;
        using var outputCts = new CancellationTokenSource();
        Task? output = null;
        try
        {
            journal = journal with { Phase = "CREATING" }; observe(journal); creating = true;
            using var created = await Control(t => engine.PostAsync("containers/create?name=" + name
                + "&platform=" + Uri.EscapeDataString(spec.Platform), new {
                Image = spec.Image, User = spec.User, Entrypoint = new[] { "/bin/sh", "-c" }, Cmd = new[] { Helper },
                WorkingDir = "/workspace", Tty = false,
                // Build secrets belong only to the volatile Exec environment, never Container.Config.
                Labels = new Dictionary<string, string> { [OwnerLabel] = options.Scope, [BuildLabel] = assignment.BuildId },
                HostConfig = new { Init = spec.Init, Mounts = mounts, LogConfig = new { Type = "none" } },
            }, t));
            var id = created.RootElement.GetProperty("Id").GetString()!;
            if (!IsId(id)) throw new InvalidDataException("The Engine returned an invalid container identity.");
            journal = journal with { Id = id, Phase = "CREATED" }; observe(journal);
            receipt.ContainerId = id;
            using (var inspected = await InspectOwned(engine, journal, assignment.BuildId))
                receipt.ImageId = inspected.RootElement.GetProperty("Image").GetString()!;
            token.ThrowIfCancellationRequested();
            await Control(t => engine.CommandAsync(HttpMethod.Post, "containers/" + id + "/start", t));
            var args = step.Args.ToArray();
            if (step.ScriptInput.Length != 0)
            {
                if (step.ScriptArgumentIndex < 0 || step.ScriptArgumentIndex >= args.Length
                    || args[step.ScriptArgumentIndex].Replace('\\', '/') != step.ScriptInput.Replace('\\', '/'))
                    throw new InvalidDataException("The container script argument differs from its private input.");
                args[step.ScriptArgumentIndex] = "/vivarium-inputs/" + step.ScriptInput;
            }
            var cwd = step.Cwd.Length == 0 ? "/workspace" : "/workspace/" + CheckedRelative(step.Cwd);
            var environment = new Dictionary<string, string>(StringComparer.Ordinal) {
                ["VIVARIUM_BUILD_ID"] = assignment.BuildId, ["VIVARIUM_WORKDIR"] = "/workspace", ["VIVARIUM_RESULTS_DIR"] = "/workspace/results" };
            if (assignment.VcsCheckout is not null)
            {
                environment["GIT_OPTIONAL_LOCKS"] = "0";
                // The installed runner may use a different UID from the checkout owner. Trust
                // only this lease's mounted worktree, never a global or wildcard safe-directory.
                environment["GIT_CONFIG_COUNT"] = "1";
                environment["GIT_CONFIG_KEY_0"] = "safe.directory";
                environment["GIT_CONFIG_VALUE_0"] = "/workspace";
            }
            foreach (var (key, value) in assignment.Parameters) environment["VIVARIUM_PARAM_" + key.ToUpperInvariant()] = value;
            if (assignment.Parameters.TryGetValue("cell", out var cell)) environment["VIVARIUM_CELL"] = cell;
            foreach (var (key, value) in step.Env) environment[key] = value;
            token.ThrowIfCancellationRequested();
            using var exec = await Control(t => engine.PostAsync("containers/" + id + "/exec", new {
                AttachStdout = true, AttachStderr = true, Tty = false, WorkingDir = cwd,
                Cmd = new[] { step.Program }.Concat(args).ToArray(), Env = environment.Select(p => p.Key + '=' + p.Value).ToArray(),
            }, t));
            var execId = exec.RootElement.GetProperty("Id").GetString()!;
            if (!IsId(execId)) throw new InvalidDataException("The Engine returned an invalid process identity.");
            receipt.ExecId = execId;
            token.ThrowIfCancellationRequested();
            // Start attaches directly to Exec output; no unmasked daemon log store is involved.
            using var response = await engine.StreamAsync("exec/" + execId + "/start", new { Detach = false, Tty = false }, outputCts.Token);
            await using var stream = await response.Content.ReadAsStreamAsync(outputCts.Token);
            output = PumpAsync(stream, assignment.BuildId, index, send, publisher, outputCts.Token);
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var termSent = false;
            var killSent = false;
            System.Diagnostics.Stopwatch? termination = null;
            while (true)
            {
                using var process = await Control(t => engine.GetAsync("exec/" + execId + "/json", t));
                if (!process!.RootElement.GetProperty("Running").GetBoolean())
                {
                    receipt.ProcessExitCode = process.RootElement.GetProperty("ExitCode").GetInt32();
                    result.ExitCode = receipt.ProcessExitCode;
                    break;
                }
                result.TimedOut |= step.TimeoutSec > 0 && timer.Elapsed >= TimeSpan.FromSeconds(step.TimeoutSec);
                if (!killSent && (stop.Mode == BuildStopMode.Force || result.TimedOut || output.IsFaulted))
                {
                    await Signal(engine, id, "SIGKILL"); killSent = true;
                    termination = System.Diagnostics.Stopwatch.StartNew();
                }
                else if (!termSent && stop.Mode == BuildStopMode.Graceful)
                {
                    await Signal(engine, id, "SIGTERM"); termSent = true;
                }
                if (termination?.Elapsed > TimeSpan.FromSeconds(10))
                    throw new WorkloadTerminationException("The container process did not acknowledge termination.");
                await Task.Delay(100);
            }
            var complete = false;
            try { await output.WaitAsync(TimeSpan.FromSeconds(5)); complete = true; }
            catch (Exception exception) when (exception is IOException or HttpRequestException or TimeoutException or OperationCanceledException) { }
            if (complete) publisher?.FinishStep();
            return (result, complete);
        }
        finally
        {
            outputCts.Cancel();
            try
            {
                if (creating)
                {
                    if (journal.Id is null)
                        throw new WorkloadTerminationException("Container creation acknowledgement is missing; ownership remains uncertain.");
                    await RemoveAsync(engine, journal, assignment.BuildId);
                    receipt.Removed = true;
                    observe(journal with { Phase = "REMOVED" });
                }
            }
            catch (Exception exception) when (exception is not WorkloadTerminationException)
            {
                throw new WorkloadTerminationException("Container termination could not be confirmed.", exception);
            }
            finally
            {
                if (output is not null)
                    try { await output.WaitAsync(TimeSpan.FromSeconds(5)); }
                    // Output shutdown must never replace a containment failure with an ordinary
                    // I/O exception: the Agent must retain quarantine when removal is unproven.
                    catch (Exception) { }
            }
        }
    }

    internal static async Task RecoverAsync(DockerRuntimeOptions options, ContainerJournalRecord journal, string buildId)
    {
        if (options.Scope != journal.Scope) throw new InvalidDataException("The installed runtime scope changed.");
        using var engine = new DockerEngine(options);
        if (await Control(t => engine.IdentityAsync(t)) != journal.EngineId)
            throw new InvalidDataException("The installed Engine identity changed.");
        if (journal.Phase == "PREPARING") return; // Creation was never dispatched.
        if (journal.Id is null) throw new InvalidDataException("Container creation ownership is uncertain.");
        await RemoveAsync(engine, journal, buildId);
    }

    private static async Task RemoveAsync(DockerEngine engine, ContainerJournalRecord journal, string buildId)
    {
        if (await Control(t => engine.IdentityAsync(t)) != journal.EngineId)
            throw new InvalidDataException("The Engine identity changed during execution.");
        using var inspected = await Control(t => engine.GetAsync("containers/" + journal.Id + "/json", t, missingAllowed: true));
        if (inspected is null) return;
        CheckContainer(inspected.RootElement, journal, buildId);
        // DELETE force waits for the owned namespace to stop; confirm absence independently.
        await Control(t => engine.CommandAsync(HttpMethod.Delete, "containers/" + journal.Id + "?force=true&v=true", t, missingAllowed: true));
        using var remaining = await Control(t => engine.GetAsync("containers/" + journal.Id + "/json", t, missingAllowed: true));
        if (remaining is not null) throw new WorkloadTerminationException("The owned container still exists after removal.");
    }

    private static async Task<JsonDocument> InspectOwned(DockerEngine engine, ContainerJournalRecord journal, string buildId)
    {
        var inspected = await Control(t => engine.GetAsync("containers/" + journal.Id + "/json", t))
            ?? throw new InvalidDataException("The owned container is absent.");
        try { CheckContainer(inspected.RootElement, journal, buildId); return inspected; }
        catch { inspected.Dispose(); throw; }
    }

    private static void CheckContainer(JsonElement value, ContainerJournalRecord journal, string buildId)
    {
        if (value.GetProperty("Id").GetString() != journal.Id || value.GetProperty("Name").GetString() != '/' + journal.Name)
            throw new InvalidDataException("The workload container identity differs.");
        CheckOwner(value.GetProperty("Config"), journal.Scope, buildId);
    }

    private static void CheckOwner(JsonElement value, string scope, string? buildId)
    {
        var labels = value.GetProperty("Labels");
        if (labels.GetProperty(OwnerLabel).GetString() != scope || buildId is not null && labels.GetProperty(BuildLabel).GetString() != buildId)
            throw new InvalidDataException("The runtime resource owner differs.");
    }

    private static async Task Signal(DockerEngine engine, string id, string signal)
    {
        try { await Control(t => engine.CommandAsync(HttpMethod.Post, "containers/" + id + "/kill?signal=" + signal, t)); }
        catch (DockerEngineException exception) when (exception.Status == 409) { } // Already stopped; Exec inspection is still required.
    }

    private static bool IsId(string value) => value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static string CheckedRelative(string value)
    {
        if (value.Length > 2048 || value.Split('/').Any(p => p is "" or "." or "..")
            || value.Any(c => char.IsControl(c) || c is '\\' or ':')) throw new InvalidDataException("Invalid container working directory.");
        return value;
    }

    private static async Task<T> Control<T>(Func<CancellationToken, Task<T>> action)
    { using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30)); return await action(deadline.Token); }
    private static async Task Control(Func<CancellationToken, Task> action)
    { using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30)); await action(deadline.Token); }

    private static async Task PumpAsync(Stream source, string buildId, int index,
        Func<AgentMsg, CancellationToken, Task> send, LiteralArtifactPublisher? publisher, CancellationToken token)
    {
        var header = new byte[8]; var buffer = new byte[8192];
        while (true)
        {
            if (await source.ReadAsync(header.AsMemory(0, 1), token) == 0) return;
            await source.ReadExactlyAsync(header.AsMemory(1), token);
            if (header[0] is not (1 or 2) || header[1] != 0 || header[2] != 0 || header[3] != 0)
                throw new InvalidDataException("The container output frame is invalid.");
            var remaining = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4));
            while (remaining != 0)
            {
                var count = (int)Math.Min((uint)buffer.Length, remaining);
                await source.ReadExactlyAsync(buffer.AsMemory(0, count), token); remaining -= (uint)count;
                if (header[0] == 1) publisher?.Append(buffer.AsSpan(0, count));
                await send(new AgentMsg { Log = new LogChunk { BuildId = buildId, StepIndex = index,
                    Stream = header[0] == 1 ? LogStream.Stdout : LogStream.Stderr, Data = ByteString.CopyFrom(buffer, 0, count) } }, token);
            }
        }
    }
}
