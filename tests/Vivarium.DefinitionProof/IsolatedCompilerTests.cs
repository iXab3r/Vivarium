using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Vivarium.DefinitionProof;

[NonParallelizable]
public sealed class IsolatedCompilerTests
{
    private string root = "", run = "";
    private Dictionary<string, string> inputs = [];
    private readonly List<object> observations = [];

    private static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    [OneTimeSetUp]
    public async Task StartAsync()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Vivarium.slnx"))) directory = directory.Parent;
        root = directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
        run = Path.Combine(root, "artifacts/us013-isolation-20261102", "physical-run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(run);
        inputs = Directory.GetFiles(Path.Combine(root, "tests/Vivarium.DefinitionProof"), "*.cs")
            .Concat(Directory.GetFiles(Path.Combine(root, "tools/definition-proof"), "*", SearchOption.TopDirectoryOnly))
            .Where(p => Path.GetExtension(p) is ".cs" or ".py" or ".csproj")
            .Append(Path.Combine(root, "tests/Vivarium.DefinitionProof/Vivarium.DefinitionProof.csproj"))
            .Append(Path.Combine(root, "global.json")).Append(Path.Combine(root, "Directory.Build.props"))
            .ToDictionary(p => Path.GetRelativePath(root, p), Hash);
        var image = await ProofProcess.RunAsync("docker", ["image", "inspect", IsolatedCompilerStudy.Image]);
        using var metadata = JsonDocument.Parse(image.Output);
        Assert.That(metadata.RootElement[0].GetProperty("Id").GetString(),
            Is.EqualTo(IsolatedCompilerStudy.Image.Split('@')[1]));
    }

    private async Task<IsolatedCompilation> EvaluateAsync(string label, Dictionary<string, string> source,
        TimeSpan? deadline = null)
    {
        var compiler = new IsolatedCompilerStudy();
        var clock = Stopwatch.StartNew();
        var result = await compiler.EvaluateAsync(source, deadline);
        clock.Stop();
        await File.WriteAllTextAsync(Path.Combine(run, label + ".stdout"), result.Output);
        await File.WriteAllTextAsync(Path.Combine(run, label + ".stderr"), result.Error);
        await File.WriteAllTextAsync(Path.Combine(run, label + ".container.json"), result.Container.GetRawText());
        observations.Add(new
        {
            label, container = compiler.Name, image = IsolatedCompilerStudy.Image, sdk = IsolatedCompilerStudy.Sdk,
            result.ExitCode, result.DeadlineExceeded, result.OutputExceeded, result.OomKilled,
            source = source.ToDictionary(p => p.Key, p => IsolatedCompilerStudy.Hash(p.Value)),
            output_sha256 = IsolatedCompilerStudy.Hash(result.Output), error_sha256 = IsolatedCompilerStudy.Hash(result.Error),
            elapsed_milliseconds = clock.ElapsedMilliseconds, removed = true,
        });
        return result;
    }

    private Task<IsolatedCompilation> ProgramAsync(string label, string source, TimeSpan? deadline = null) =>
        EvaluateAsync(label, new() { ["Program.cs"] = source }, deadline);

    private static void Success(IsolatedCompilation result)
    {
        Assert.Multiple(() =>
        {
            Assert.That(result.ExitCode, Is.Zero, result.Error);
            Assert.That(result.DeadlineExceeded, Is.False);
            Assert.That(result.OutputExceeded, Is.False);
            Assert.That(result.OomKilled, Is.False);
        });
    }

    [Test]
    public async Task Committed_CSharp_edits_and_cold_revision_reads_compile_inside_the_boundary()
    {
        var repository = Path.Combine(run, "source.git-work");
        Directory.CreateDirectory(repository);
        await SourceCompiler.GitAsync(repository, "init", "-b", "main");
        await SourceCompiler.GitAsync(repository, "config", "core.autocrlf", "false");
        await SourceCompiler.GitAsync(repository, "config", "user.name", "Vivarium isolation proof");
        await SourceCompiler.GitAsync(repository, "config", "user.email", "proof@example.invalid");
        var names = new[] { "Program.cs", "Model.cs", "Control.g.cs", "Settings.g.cs", "UserCode.cs" };
        foreach (var name in names) File.Copy(Path.Combine(root, "tools/definition-proof", name), Path.Combine(repository, name));
        async Task<string> Commit(string message)
        {
            await SourceCompiler.GitAsync(repository, "add", "--all");
            await SourceCompiler.GitAsync(repository, "commit", "-m", message);
            return await SourceCompiler.GitAsync(repository, "rev-parse", "HEAD");
        }
        async Task<IsolatedCompilation> Compile(string label, string commit)
        {
            var sources = new Dictionary<string, string>();
            foreach (var name in names)
            {
                var bytes = await ProofProcess.RunAsync("git", ["-C", repository, "show", commit + ":" + name]);
                sources.Add(name, Encoding.UTF8.GetString(bytes.Output));
            }
            observations.Add(new { label, repository_commit = commit });
            return await EvaluateAsync(label, sources);
        }
        var firstCommit = await Commit("proof: committed CSharp input");
        var first = await Compile("committed-original", firstCommit);
        Success(first);
        using (var plan = JsonDocument.Parse(first.Output))
        {
            Assert.That(plan.RootElement.GetProperty("project").GetProperty("id").GetString(), Is.EqualTo("T3Personal"));
            Assert.That(plan.RootElement.GetProperty("configurations").GetArrayLength(), Is.EqualTo(3));
            Assert.That(plan.RootElement.GetProperty("configurations")[2].GetProperty("timeoutMinutes").GetInt32(), Is.EqualTo(15));
            Assert.That(first.Output, Does.Contain("computed-mode"));
        }
        await File.WriteAllTextAsync(Path.Combine(repository, "UserCode.cs"), """
            namespace DefinitionProof;
            public static class UserCode
            {
                public static string Evaluate(string expression) => string.Concat(new[] { "isolated", "-edit" });
            }
            """);
        var changed = await Compile("committed-edit", await Commit("proof: ordinary computed edit"));
        Success(changed);
        Assert.That(changed.Output, Does.Contain("isolated-edit").And.Not.Contain("computed-mode"));
        var cold = await Compile("committed-cold", firstCommit);
        Success(cold);
        Assert.That(cold.Output, Is.EqualTo(first.Output), "This fixed fixture is reproducible; arbitrary C# need not be.");
    }

    [Test]
    public async Task Author_code_cannot_read_Host_files_environment_or_Docker_and_each_scratch_is_private()
    {
        var canary = Path.Combine(run, "host-only-canary.txt");
        var value = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
        await File.WriteAllTextAsync(canary, value);
        var suffix = canary.Replace('\\', '/')[3..];
        var paths = new[] { canary, "/host_mnt/d/" + suffix, "/run/desktop/mnt/host/d/" + suffix, "/mnt/host/d/" + suffix };
        var code = $$"""
            using System.Text.Json;
            using System.Runtime.InteropServices;
            string[] paths = {{JsonSerializer.Serialize(paths)}};
            var before = File.Exists("/work/previous-invocation");
            File.WriteAllText("/work/previous-invocation", "private scratch");
            bool rootWrite;
            try { File.WriteAllText("/host-escape", "forbidden"); rootWrite = true; }
            catch (IOException) { rootWrite = false; }
            catch (UnauthorizedAccessException) { rootWrite = false; }
            var status = File.ReadAllLines("/proc/self/status");
            Console.Write(JsonSerializer.Serialize(new {
                hostFile = paths.Any(File.Exists),
                hostEnvironment = Environment.GetEnvironmentVariable("VIVARIUM_ISOLATION_PARENT_SENTINEL"),
                dockerSocket = File.Exists("/var/run/docker.sock"), rootWrite, previousScratch = before,
                rootEscalation = Native.setuid(0) == 0, uid = Native.geteuid(),
                capabilities = status.Single(l => l.StartsWith("CapEff:")),
                noNewPrivileges = status.Single(l => l.StartsWith("NoNewPrivs:"))
            }));
            static class Native
            {
                [DllImport("libc")] internal static extern int setuid(uint uid);
                [DllImport("libc")] internal static extern uint geteuid();
            }
            """;
        foreach (var label in new[] { "access-first", "access-fresh" })
        {
            var result = await ProgramAsync(label, code);
            Success(result);
            using var output = JsonDocument.Parse(result.Output);
            var body = output.RootElement;
            foreach (var name in new[] { "hostFile", "dockerSocket", "rootWrite", "previousScratch", "rootEscalation" })
                Assert.That(body.GetProperty(name).GetBoolean(), Is.False, name);
            Assert.That(body.GetProperty("hostEnvironment").ValueKind, Is.EqualTo(JsonValueKind.Null));
            Assert.That(body.GetProperty("uid").GetUInt32(), Is.EqualTo(65532));
            Assert.That(body.GetProperty("capabilities").GetString()!.Split(':')[1].Trim(), Is.EqualTo("0000000000000000"));
            Assert.That(body.GetProperty("noNewPrivileges").GetString()!.Split(':')[1].Trim(), Is.EqualTo("1"));
            Assert.That(result.Output, Does.Not.Contain(value));
        }
        Assert.That(await File.ReadAllTextAsync(canary), Is.EqualTo(value), "The Host positive control must remain intact.");
    }

    [Test]
    public async Task Network_none_blocks_a_receiver_that_a_separate_trusted_control_can_reach()
    {
        using var listener = new TcpListener(IPAddress.Any, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var name = new IsolatedCompilerStudy().Name;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var accept = listener.AcceptTcpClientAsync(deadline.Token).AsTask();
        string address;
        try
        {
            var control = await ProofProcess.RunAsync("docker", ["run", "--name", name,
                "--label", "vivarium.owner=" + IsolatedCompilerStudy.Owner, "--label", "vivarium.invocation=" + name,
                "--label", "vivarium.purpose=trusted-network-control", "--network", "bridge", "--read-only",
                "--user", "65532:65532", "--cap-drop", "ALL", "--security-opt", "no-new-privileges=true",
                "--memory", "64m", "--pids-limit", "16", "--cpus", "1", "--log-driver", "none",
                IsolatedCompilerStudy.Image, "/bin/bash", "-c",
                "set -eu; getent ahostsv4 host.docker.internal | head -1 | awk '{print $1}'; " +
                "printf 'owned-canary' > /dev/tcp/host.docker.internal/" + port]);
            address = IPAddress.Parse(control.Text).ToString();
            using var client = await accept;
            using var reader = new StreamReader(client.GetStream());
            Assert.That(await reader.ReadToEndAsync(deadline.Token), Is.EqualTo("owned-canary"));
        }
        finally { await IsolatedCompilerStudy.RemoveOwnedAsync(name); }
        var result = await ProgramAsync("network-denied", $$"""
            using System.Net;
            using System.Net.Sockets;
            using System.Text.Json;
            using var client = new TcpClient();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            bool connected;
            try { await client.ConnectAsync(IPAddress.Parse("{{address}}"), {{port}}, timeout.Token); connected = true; }
            catch (SocketException) { connected = false; }
            catch (OperationCanceledException) { connected = false; }
            Console.Write(JsonSerializer.Serialize(new { connected }));
            """);
        Success(result);
        using var value = JsonDocument.Parse(result.Output);
        Assert.That(value.RootElement.GetProperty("connected").GetBoolean(), Is.False);
        Assert.That(listener.Pending(), Is.False);
        using var positive = new TcpClient();
        await positive.ConnectAsync(IPAddress.Loopback, port, deadline.Token);
        using var stillListening = await listener.AcceptTcpClientAsync(deadline.Token);
        observations.Add(new { label = "network-positive-controls", trusted_container_reached_receiver = true, host_receiver_still_live = true });
    }

    [Test]
    public async Task Infinite_author_code_hits_the_deadline_and_the_container_is_removed()
    {
        var result = await ProgramAsync("deadline", "Console.Write(\"entered\"); while (true) { }", TimeSpan.FromSeconds(15));
        Assert.That(result.Output, Does.StartWith("entered"), "The hostile evaluation, not just compilation, must have started.");
        Assert.That(result.DeadlineExceeded, Is.True);
        Assert.That(result.OutputExceeded, Is.False);
        Assert.That(result.ExitCode, Is.Not.Zero);
    }

    [Test]
    public async Task Hostile_output_is_stopped_at_a_combined_byte_bound()
    {
        var result = await ProgramAsync("output-bound", "while (true) { Console.Write(new string('x', 4096)); Console.Error.Write(new string('y', 4096)); }");
        Assert.That(result.OutputExceeded, Is.True);
        Assert.That(result.DeadlineExceeded, Is.False);
        Assert.That(Encoding.UTF8.GetByteCount(result.Output) + Encoding.UTF8.GetByteCount(result.Error), Is.LessThanOrEqualTo(IsolatedCompilerStudy.OutputLimit));
        Assert.That(result.ExitCode, Is.Not.Zero);
    }

    [Test]
    public async Task Unmanaged_memory_cannot_bypass_the_container_memory_budget()
    {
        var result = await ProgramAsync("memory-bound", """
            using System.Runtime.InteropServices;
            Console.Write("entered");
            while (true)
            {
                var memory = Marshal.AllocHGlobal(64 * 1024 * 1024);
                for (var offset = 0; offset < 64 * 1024 * 1024; offset += 4096) Marshal.WriteByte(memory, offset, 1);
            }
            """);
        Assert.That(result.Output, Does.StartWith("entered"));
        Assert.That(result.OomKilled, Is.True, result.Error);
        Assert.That(result.DeadlineExceeded, Is.False);
        Assert.That(result.ExitCode, Is.Not.Zero);
    }

    [Test]
    public async Task Process_fanout_stops_at_the_enforced_pid_limit()
    {
        var result = await ProgramAsync("process-bound", """
            using System.ComponentModel;
            using System.Diagnostics;
            using System.Text.Json;
            var started = 0;
            try
            {
                for (; started < 160; started++) Process.Start(new ProcessStartInfo("/bin/sleep", "30") { UseShellExecute = false });
                throw new Exception("PID budget did not constrain the process fanout.");
            }
            catch (Win32Exception error) { Console.Write(JsonSerializer.Serialize(new { started, error.NativeErrorCode })); }
            """);
        Success(result);
        using var output = JsonDocument.Parse(result.Output);
        Assert.That(output.RootElement.GetProperty("started").GetInt32(), Is.InRange(1, 127));
        Assert.That(output.RootElement.GetProperty("NativeErrorCode").GetInt32(), Is.EqualTo(11));
    }

    [TestCase("Evaluator.csproj")]
    [TestCase("../Program.cs")]
    [TestCase("Directory.Build.targets")]
    public void Repository_build_instructions_and_paths_are_rejected_before_container_creation(string name)
    {
        var compiler = new IsolatedCompilerStudy();
        Assert.ThrowsAsync<InvalidDataException>(() => compiler.EvaluateAsync(new Dictionary<string, string> { [name] = "not trusted build instructions" }));
    }

    [Test]
    public void Oversized_source_is_rejected_before_container_creation()
    {
        var compiler = new IsolatedCompilerStudy();
        Assert.ThrowsAsync<InvalidDataException>(() => compiler.EvaluateAsync(new Dictionary<string, string>
            { ["Program.cs"] = new string('x', IsolatedCompilerStudy.InputLimit + 1) }));
    }

    [Test]
    public async Task Invalid_compilation_and_nonzero_exit_do_not_become_a_successful_plan()
    {
        var invalid = await ProgramAsync("invalid-compilation", "this is not valid CSharp;");
        Assert.That(invalid.ExitCode, Is.Not.Zero);
        Assert.That(invalid.Output, Is.Empty);
        Assert.That(invalid.Error, Does.Contain("error CS"));
        Assert.That(invalid.DeadlineExceeded, Is.False);
        var failed = await ProgramAsync("nonzero-exit", "Console.Write(\"{}\"); Environment.Exit(7);");
        Assert.That(failed.Output, Is.EqualTo("{}"));
        Assert.That(failed.ExitCode, Is.EqualTo(7));
        Assert.That(failed.DeadlineExceeded, Is.False);
    }

    [Test]
    public void Malformed_UTF8_is_rejected_and_still_removes_the_owned_container()
    {
        var compiler = new IsolatedCompilerStudy();
        Assert.ThrowsAsync<DecoderFallbackException>(() => compiler.EvaluateAsync(new Dictionary<string, string>
            { ["Program.cs"] = "Console.OpenStandardOutput().WriteByte(255);" }));
        observations.Add(new { label = "malformed-utf8", container = compiler.Name, rejected = true, removed = true });
    }

    [Test]
    public async Task Killed_coordinator_leaves_an_identifiable_container_for_a_fresh_cleanup_pass()
    {
        var name = new IsolatedCompilerStudy().Name;
        var sourcePath = Path.Combine(run, "orphan-source.cs");
        var journalPath = Path.Combine(run, "owned-invocation.json");
        await File.WriteAllTextAsync(sourcePath, "File.WriteAllText(\"/work/evaluator-ready\", \"ready\"); while (true) { }");
        // This descriptor precedes launch and survives the worker process; no credential is stored.
        await File.WriteAllTextAsync(journalPath, JsonSerializer.Serialize(new { name, image = IsolatedCompilerStudy.Image }));
        var dotnet = Path.Combine(root, "artifacts/p0-rest-first/tooling/dotnet/dotnet.exe");
        using var worker = ProofProcess.Start(dotnet,
            [typeof(IsolatedCompilerTests).Assembly.Location, "--isolated-worker", sourcePath, name], root);
        var observed = ProofProcess.ObserveAsync(worker, allowFailure: true, deadline: TimeSpan.FromSeconds(90));
        try
        {
            var clock = Stopwatch.StartNew();
            var ready = false;
            while (clock.Elapsed < TimeSpan.FromSeconds(40))
            {
                if (worker.HasExited) throw new InvalidOperationException("The coordinator exited before the actual evaluator started.");
                var check = await ProofProcess.RunAsync("docker", ["exec", name, "/bin/test", "-f", "/work/evaluator-ready"], allowFailure: true);
                if (check.ExitCode == 0) { ready = true; break; }
                await Task.Delay(100);
            }
            Assert.That(ready, Is.True, "Do not substitute a compile-only interruption for running author code.");
            worker.Kill(entireProcessTree: true);
            await observed;
            var inspect = await ProofProcess.RunAsync("docker", ["inspect", name]);
            using (var container = JsonDocument.Parse(inspect.Output))
                Assert.That(container.RootElement[0].GetProperty("State").GetProperty("Running").GetBoolean(), Is.True,
                    "The daemon-owned container survives a killed coordinator and needs explicit recovery.");
            using var journal = JsonDocument.Parse(await File.ReadAllTextAsync(journalPath));
            Assert.That(journal.RootElement.GetProperty("image").GetString(), Is.EqualTo(IsolatedCompilerStudy.Image));
            await IsolatedCompilerStudy.RemoveOwnedAsync(journal.RootElement.GetProperty("name").GetString()!);
            observations.Add(new { label = "coordinator-kill-recovery", container = name, actual_author_code_started = true,
                survived_worker = true, recovered_from_descriptor = true, removed = true });
        }
        finally
        {
            if (!worker.HasExited) worker.Kill(entireProcessTree: true);
            await observed;
            await IsolatedCompilerStudy.RemoveOwnedAsync(name);
        }
    }

    [OneTimeTearDown]
    public async Task RecordAsync()
    {
        if (run.Length == 0) return;
        var stable = inputs.All(p => Hash(Path.Combine(root, p.Key)) == p.Value);
        var containers = await ProofProcess.RunAsync("docker", ["ps", "-aq", "--filter", "label=vivarium.owner=" + IsolatedCompilerStudy.Owner]);
        Assert.That(containers.Text, Is.Empty, "An owned evaluator or network-control container leaked.");
        Assert.That(stable, Is.True);
        await File.WriteAllTextAsync(Path.Combine(run, "receipt.json"), JsonSerializer.Serialize(new
        {
            kind = "D43_ISOLATION_BOUNDARY_STUDY_NOT_PRODUCTION_EVALUATOR", image = IsolatedCompilerStudy.Image,
            sdk = IsolatedCompilerStudy.Sdk, host_project_sdk = "10.0.303", inputs, inputsStable = stable,
            observations, owned_containers_remaining = 0,
            limits = "Windows coordinator/Linux Docker evidence only; fixed SDK-only project. No production SDK, public endpoint, catalogue activation, global admission bound or hostile-kernel/daemon guarantee.",
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
