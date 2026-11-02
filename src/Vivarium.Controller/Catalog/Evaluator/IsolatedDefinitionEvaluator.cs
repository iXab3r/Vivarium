using System.Diagnostics;
using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Vivarium.Controller.Catalog;

public sealed record DefinitionEvaluation(int ExitCode, string Output, string Error,
    bool DeadlineExceeded, bool OutputExceeded, bool OomKilled, JsonElement Container);

// D43 execution boundary. The catalogue journals invocation identity before calling this component.
public sealed class IsolatedDefinitionEvaluator
{
    internal const string Owner = "vivarium-definition-evaluator-v1";
    internal const string Image = "mcr.microsoft.com/dotnet/sdk@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317";
    internal const int OutputLimit = 64 * 1024;
    internal const int InputLimit = 256 * 1024;
    internal const long MemoryLimit = 512L * 1024 * 1024;
    internal const string Sdk = "10.0.401";
    private static readonly HashSet<string> SourceNames = new(StringComparer.Ordinal)
        { "Control.g.cs", "Settings.g.cs", "UserCode.cs" };
    private static readonly SemaphoreSlim Gate = new(1, 1);
    public string Name { get; }

    private static string Resource(string name)
    {
        using var stream = typeof(IsolatedDefinitionEvaluator).Assembly.GetManifestResourceStream(
            "Vivarium.Controller.Catalog." + name) ?? throw new InvalidOperationException("Evaluator resource is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    public static string ContractHash(bool controlOnly = false) => Hash(Resource("DefinitionModel.cs") + "\0"
        + Resource("Evaluator.ControlReader.cs.txt") + (controlOnly ? "" : "\0" + Resource("Evaluator.Program.cs.txt")));

    public IsolatedDefinitionEvaluator(Guid invocation)
    {
        Name = "vivarium-definition-eval-" + invocation.ToString("N");
        ValidateName(Name);
    }

    private static void ValidateName(string name)
    {
        if (!Regex.IsMatch(name, "^vivarium-definition-eval-[a-f0-9]{32}$", RegexOptions.CultureInvariant))
            throw new InvalidDataException("Evaluator invocation identity is invalid.");
    }

    private static byte[] Input(IReadOnlyDictionary<string, string> sources, bool controlOnly)
    {
        if (!(controlOnly ? sources.Count == 1 && sources.ContainsKey("Control.g.cs") : SourceNames.SetEquals(sources.Keys)) ||
            sources.Sum(p => Encoding.UTF8.GetByteCount(p.Value)) > InputLimit)
            throw new InvalidDataException("Evaluator source input exceeds its fixed file/byte boundary.");
        var files = new Dictionary<string, string>(sources, StringComparer.Ordinal)
        {
            ["Program.cs"] = Resource(controlOnly ? "Evaluator.ControlReader.cs.txt" : "Evaluator.Program.cs.txt"),
            ["Model.cs"] = Resource("DefinitionModel.cs"),
            ["Evaluator.csproj"] = """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable>
                    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                    <UseAppHost>false</UseAppHost><NuGetAudit>false</NuGetAudit>
                  </PropertyGroup>
                  <ItemGroup><Compile Include="SOURCE_FILES" /></ItemGroup>
                </Project>
                """.Replace("SOURCE_FILES", string.Join(';', sources.Keys.Append("Program.cs").Append("Model.cs").Order(StringComparer.Ordinal)), StringComparison.Ordinal),
            ["NuGet.Config"] = "<configuration><packageSources><clear /></packageSources></configuration>",
            ["global.json"] = "{\"sdk\":{\"version\":\"" + Sdk + "\",\"rollForward\":\"disable\"}}",
        };
        using var bytes = new MemoryStream();
        using (var tar = new TarWriter(bytes, leaveOpen: true))
        foreach (var file in files.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            using var data = new MemoryStream(Encoding.UTF8.GetBytes(file.Value));
            tar.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, file.Key)
            {
                DataStream = data, ModificationTime = DateTimeOffset.UnixEpoch,
                Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            });
        }
        return bytes.ToArray();
    }

    private static async Task<JsonElement?> InspectAsync(string name)
    {
        ValidateName(name);
        var result = await DockerCommand.RunAsync(["inspect", name], allowFailure: true);
        if (result.ExitCode != 0)
        {
            var error = Encoding.UTF8.GetString(result.Error);
            if (error.Contains("No such object", StringComparison.OrdinalIgnoreCase)) return null;
            throw new IOException("Cannot inspect the owned evaluator container.");
        }
        using var document = JsonDocument.Parse(result.Output);
        var container = document.RootElement[0].Clone();
        if (container.GetProperty("Name").GetString() != "/" + name ||
            container.GetProperty("Config").GetProperty("Labels").GetProperty("vivarium.owner").GetString() != Owner ||
            container.GetProperty("Config").GetProperty("Labels").GetProperty("vivarium.invocation").GetString() != name)
            throw new InvalidDataException("Evaluator container ownership differs.");
        return container;
    }

    public static async Task RemoveOwnedAsync(string name)
    {
        var container = await InspectAsync(name);
        if (container is null) return;
        var id = container.Value.GetProperty("Id").GetString()!;
        if (!Regex.IsMatch(id, "^[a-f0-9]{64}$", RegexOptions.CultureInvariant))
            throw new InvalidDataException("Evaluator container identity differs.");
        await DockerCommand.RunAsync(["rm", "--force", "--volumes", id]);
        if (await InspectAsync(name) is not null) throw new IOException("Evaluator container remains after cleanup.");
    }

    private static void VerifyBoundary(JsonElement container)
    {
        var config = container.GetProperty("Config");
        var host = container.GetProperty("HostConfig");
        if (config.GetProperty("User").GetString() != "65532:65532" ||
            config.GetProperty("Image").GetString() != Image ||
            host.GetProperty("NetworkMode").GetString() != "none" ||
            host.GetProperty("PidMode").GetString() != "" ||
            host.GetProperty("IpcMode").GetString() != "none" ||
            host.GetProperty("CgroupnsMode").GetString() != "private" ||
            host.GetProperty("Privileged").GetBoolean() ||
            !host.GetProperty("ReadonlyRootfs").GetBoolean() ||
            host.GetProperty("Memory").GetInt64() != MemoryLimit ||
            host.GetProperty("MemorySwap").GetInt64() != MemoryLimit ||
            host.GetProperty("NanoCpus").GetInt64() != 1_000_000_000 ||
            host.GetProperty("PidsLimit").GetInt32() != 128 ||
            host.GetProperty("LogConfig").GetProperty("Type").GetString() != "none" ||
            !host.GetProperty("CapDrop").EnumerateArray().Any(v => v.GetString() == "ALL") ||
            !host.GetProperty("SecurityOpt").EnumerateArray().Any(v => v.GetString() == "no-new-privileges=true") ||
            container.GetProperty("Mounts").GetArrayLength() != 0 ||
            !host.GetProperty("Tmpfs").EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal)
                .SetEquals(new[] { "/work", "/tmp" }))
            throw new InvalidDataException("Docker did not establish the required evaluator boundary.");
    }

    public async Task<DefinitionEvaluation> EvaluateAsync(IReadOnlyDictionary<string, string> sources,
        CancellationToken cancellationToken = default)
        => await ExecuteAsync(sources, controlOnly: false, cancellationToken);

    public async Task<DefinitionEvaluation> EvaluateControlAsync(string source, CancellationToken cancellationToken = default)
        => await ExecuteAsync(new Dictionary<string, string> { ["Control.g.cs"] = source }, controlOnly: true, cancellationToken);

    private async Task<DefinitionEvaluation> ExecuteAsync(IReadOnlyDictionary<string, string> sources,
        bool controlOnly, CancellationToken cancellationToken)
    {
        var input = Input(sources, controlOnly); // Reject bad inputs before any Docker mutation.
        await Gate.WaitAsync(cancellationToken);
        const string script = """
            set -eu
            mkdir -p /work/input /work/home /work/tmp
            tar --no-same-owner --no-same-permissions -xf - -C /work/input
            cd /work/input
            if ! dotnet build Evaluator.csproj -m:1 -p:UseSharedCompilation=false --disable-build-servers --nologo -v:quiet > /work/build.log 2>&1; then
              cat /work/build.log >&2
              exit 1
            fi
            exec dotnet bin/Debug/net10.0/Evaluator.dll
            """;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var image = await DockerCommand.RunAsync(["image", "inspect", Image], token: cancellationToken);
            using (var metadata = JsonDocument.Parse(image.Output))
            {
                var value = metadata.RootElement[0];
                if (value.GetProperty("Id").GetString() != Image.Split('@')[1]
                    || value.GetProperty("Os").GetString() != "linux"
                    || value.GetProperty("Architecture").GetString() != "amd64")
                    throw new InvalidDataException("The pinned evaluator image/platform is unavailable.");
            }
            await DockerCommand.RunAsync(["create", "--pull", "never", "--interactive", "--name", Name,
                "--label", "vivarium.owner=" + Owner, "--label", "vivarium.invocation=" + Name,
                "--network", "none", "--ipc", "none", "--cgroupns", "private", "--read-only",
                "--user", "65532:65532", "--cap-drop", "ALL", "--security-opt", "no-new-privileges=true",
                "--memory", "512m", "--memory-swap", "512m", "--cpus", "1", "--pids-limit", "128",
                "--ulimit", "core=0", "--ulimit", "nofile=512:512", "--log-driver", "none",
                "--tmpfs", "/work:rw,noexec,nosuid,nodev,size=128m,mode=1777",
                "--tmpfs", "/tmp:rw,noexec,nosuid,nodev,size=32m,mode=1777",
                "--env", "HOME=/work/home", "--env", "TMPDIR=/work/tmp", "--env", "DOTNET_CLI_HOME=/work/home",
                "--env", "DOTNET_NOLOGO=1", "--env", "DOTNET_CLI_TELEMETRY_OPTOUT=1",
                "--env", "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1", "--env", "DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE=true",
                "--env", "DOTNET_EnableDiagnostics=0", "--env", "DOTNET_PROCESSOR_COUNT=1",
                // PID 1 enforces the elapsed bound even if the Host coordinator dies.
                Image, "/usr/bin/timeout", "--signal=KILL", "30s", "/bin/sh", "-c",
                script.ReplaceLineEndings("\n")], token: cancellationToken);
            var initial = await InspectAsync(Name) ?? throw new IOException("Evaluator was not created.");
            VerifyBoundary(initial);
            var info = new ProcessStartInfo("docker")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            foreach (var arg in new[] { "start", "--attach", "--interactive", Name }) info.ArgumentList.Add(arg);
            using var process = Process.Start(info) ?? throw new IOException("Evaluator attach did not start.");
            using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timer.CancelAfter(TimeSpan.FromSeconds(30));
            var outputExceeded = false;
            var bytesObserved = 0;
            async Task<byte[]> ReadAsync(Stream stream)
            {
                using var result = new MemoryStream();
                var buffer = new byte[4096];
                try
                {
                    int count;
                    while ((count = await stream.ReadAsync(buffer, timer.Token)) != 0)
                    {
                        if (Interlocked.Add(ref bytesObserved, count) > OutputLimit)
                        {
                            outputExceeded = true;
                            await timer.CancelAsync();
                            break;
                        }
                        result.Write(buffer, 0, count);
                    }
                }
                catch (OperationCanceledException) when (timer.IsCancellationRequested) { }
                return result.ToArray();
            }
            async Task SendAsync()
            {
                await process.StandardInput.BaseStream.WriteAsync(input, timer.Token);
                process.StandardInput.Close();
            }
            var output = ReadAsync(process.StandardOutput.BaseStream);
            var error = ReadAsync(process.StandardError.BaseStream);
            var send = SendAsync();
            try
            {
                await Task.WhenAny(process.WaitForExitAsync(), Task.Delay(Timeout.InfiniteTimeSpan, timer.Token));
            }
            finally
            {
                if (!process.HasExited)
                {
                    // Release a flooded attach connection before asking the daemon to stop work.
                    // Otherwise unread output can hold up even independent management requests.
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    await DockerCommand.RunAsync(["kill", initial.GetProperty("Id").GetString()!], allowFailure: true);
                }
            }
            try { await Task.WhenAll(output, error, send).WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (OperationCanceledException) when (timer.IsCancellationRequested) { }
            var completed = await InspectAsync(Name) ?? throw new IOException("Evaluator disappeared before completion readback.");
            var state = completed.GetProperty("State");
            if (state.GetProperty("Running").GetBoolean() ||
                state.GetProperty("StartedAt").GetString()!.StartsWith("0001", StringComparison.Ordinal))
                throw new IOException("Evaluator did not reach a stopped invocation state.");
            var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            try
            {
                return new(state.GetProperty("ExitCode").GetInt32(), utf8.GetString(await output),
                    utf8.GetString(await error), timer.IsCancellationRequested && !outputExceeded,
                    outputExceeded, state.GetProperty("OOMKilled").GetBoolean(), completed);
            }
            catch (DecoderFallbackException) { throw new InvalidDataException("Evaluator output is not valid UTF-8."); }
        }
        finally
        {
            try { await RemoveOwnedAsync(Name); }
            finally { Gate.Release(); }
        }
    }

    public static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
