using Cake.Frosting;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

[TaskName("PayloadSmoke")]
[TaskDescription("Publishes and runs the self-contained NUnit reference payload for the host RID.")]
public sealed class PayloadSmokeTask : AsyncFrostingTask<BuildContext>
{
    public override async Task RunAsync(BuildContext context)
    {
        var rid = context.TargetRid;
        EnsureNativeRid(rid);
        var output = Path.Combine(context.OutRoot, "payload", rid);
        var results = Path.Combine(context.OutRoot, "payload-results", rid);
        RecreateDirectory(output);
        RecreateDirectory(results);

        var publish = new List<string>
        {
            "publish",
            Path.Combine("samples", "payload-nunit"),
            "-c",
            context.BuildConfiguration,
            "--maxcpucount:1",
            "-r",
            rid,
            "--self-contained",
            "-o",
            output,
        };
        publish.AddRange(context.ContinuousIntegrationProperties());
        await BuildProcess.RunAsync("dotnet", publish, context.Root);

        var executable = Path.Combine(output, OperatingSystem.IsWindows() ? "PayloadTests.exe" : "PayloadTests");
        if (!File.Exists(executable)) throw new FileNotFoundException("Published payload executable is missing.", executable);
        var trx = Path.Combine(results, "results.trx");
        await BuildProcess.RunAsync(
            executable,
            ["--report-trx", "--report-trx-filename", "results.trx", "--results-directory", results],
            context.Root);
        TestTask.VerifyTrx(trx);
    }

    private static void EnsureNativeRid(string rid)
    {
        var expectedPrefix = OperatingSystem.IsWindows()
            ? "win-"
            : OperatingSystem.IsLinux()
                ? "linux-"
                : OperatingSystem.IsMacOS()
                    ? "osx-"
                    : "unsupported-";
        if (!rid.StartsWith(expectedPrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"PayloadSmoke executes native output and cannot run RID '{rid}' on this host.");
        }
    }

    internal static void RecreateDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        Directory.CreateDirectory(path);
    }
}

[TaskName("PayloadCrossMacPublish")]
[TaskDescription("Cross-publishes the macOS arm64 payload for exact artifact transfer.")]
public sealed class PayloadCrossMacPublishTask : AsyncFrostingTask<BuildContext>
{
    public override async Task RunAsync(BuildContext context)
    {
        PayloadSmokeTask.RecreateDirectory(context.PayloadDirectory);
        var publish = new List<string>
        {
            "publish",
            Path.Combine("samples", "payload-nunit"),
            "-c",
            context.BuildConfiguration,
            "--maxcpucount:1",
            "-r",
            "osx-arm64",
            "--self-contained",
            "-o",
            context.PayloadDirectory,
        };
        publish.AddRange(context.ContinuousIntegrationProperties());
        await BuildProcess.RunAsync("dotnet", publish, context.Root);
    }
}

[TaskName("PayloadCrossMacRun")]
[TaskDescription("Runs a transferred Linux-published payload on macOS.")]
public sealed class PayloadCrossMacRunTask : AsyncFrostingTask<BuildContext>
{
    public override async Task RunAsync(BuildContext context)
    {
        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("PayloadCrossMacRun requires macOS.");
        }

        var executable = Path.Combine(context.PayloadDirectory, "PayloadTests");
        if (!File.Exists(executable)) throw new FileNotFoundException("Transferred payload is missing.", executable);
        File.SetUnixFileMode(
            executable,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        var results = Path.Combine(context.OutRoot, "payload-cross-macos-results");
        PayloadSmokeTask.RecreateDirectory(results);
        await BuildProcess.RunAsync(
            executable,
            ["--report-trx", "--report-trx-filename", "results.trx", "--results-directory", results],
            context.Root);
        TestTask.VerifyTrx(Path.Combine(results, "results.trx"));
    }
}

[TaskName("PayloadNextest")]
[TaskDescription("Archives and runs the Rust payload with a checksum-verified cargo-nextest binary.")]
public sealed class PayloadNextestTask : AsyncFrostingTask<BuildContext>
{
    public override async Task RunAsync(BuildContext context)
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            throw new PlatformNotSupportedException("PayloadNextest currently pins the Linux x64 binary used by CI.");
        }

        _ = await BuildProcess.CaptureAsync("cargo", ["--version"], context.Root);
        var nextest = await LockedTool.ResolveCargoNextestAsync(context);
        var nextestRoot = Path.Combine(context.Root, "samples", "payload-rust");
        var output = Path.Combine(context.OutRoot, "payload-nextest");
        var simulatedTarget = Path.Combine(output, "target-machine");
        var remappedRoot = Path.Combine(simulatedTarget, "payload-rust");
        var archive = Path.Combine(output, "payload.tar.zst");
        PayloadSmokeTask.RecreateDirectory(output);

        await BuildProcess.RunAsync(
            nextest,
            ["nextest", "archive", "--archive-file", archive],
            nextestRoot);
        CopyDirectory(nextestRoot, remappedRoot);
        await BuildProcess.RunAsync(
            nextest,
            ["nextest", "run", "--archive-file", archive, "--workspace-remap", remappedRoot],
            nextestRoot);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            if (relative.Split(Path.DirectorySeparatorChar).Any(segment => segment == "target")) continue;
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }
}

internal static class LockedTool
{
    public static async Task<string> ResolveCargoNextestAsync(BuildContext context)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("The locked cargo-nextest artifact is available for Linux only.");
        }

        var lockPath = Path.Combine(context.Root, "toolchains.lock.json");
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(lockPath));
        var tool = document.RootElement.GetProperty("tools").GetProperty("cargoNextest");
        var version = tool.GetProperty("version").GetString()
            ?? throw new InvalidDataException("cargoNextest version is missing from toolchains.lock.json.");
        var artifact = tool.GetProperty("artifacts").GetProperty("linux-x64");
        var url = artifact.GetProperty("url").GetString()
            ?? throw new InvalidDataException("cargoNextest URL is missing from toolchains.lock.json.");
        var expectedDigest = artifact.GetProperty("digest").GetString()
            ?? throw new InvalidDataException("cargoNextest digest is missing from toolchains.lock.json.");
        var toolRoot = Path.Combine(context.OutRoot, "tools", "cargo-nextest", version, "linux-x64");
        var executable = Path.Combine(toolRoot, "cargo-nextest");
        var marker = Path.Combine(toolRoot, ".verified-sha256");
        if (File.Exists(executable) && File.Exists(marker) &&
            string.Equals((await File.ReadAllTextAsync(marker)).Trim(), expectedDigest, StringComparison.OrdinalIgnoreCase))
        {
            return executable;
        }

        var download = Path.Combine(context.OutRoot, "downloads", $"cargo-nextest-{version}-linux-x64.tar.gz");
        Directory.CreateDirectory(Path.GetDirectoryName(download)!);
        using (var http = new HttpClient())
        await using (var input = await http.GetStreamAsync(url))
        await using (var output = File.Create(download))
        {
            await input.CopyToAsync(output);
        }

        await using (var input = File.OpenRead(download))
        {
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(input)).ToLowerInvariant();
            if (!string.Equals(actual, expectedDigest, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"cargo-nextest digest mismatch: expected {expectedDigest}, got {actual}.");
            }
        }

        PayloadSmokeTask.RecreateDirectory(toolRoot);
        await using (var input = File.OpenRead(download))
        using (var gzip = new GZipStream(input, CompressionMode.Decompress))
        {
            TarFile.ExtractToDirectory(gzip, toolRoot, overwriteFiles: false);
        }
        if (!File.Exists(executable))
        {
            throw new InvalidDataException("Verified cargo-nextest archive did not contain cargo-nextest.");
        }
        File.SetUnixFileMode(
            executable,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        await File.WriteAllTextAsync(marker, expectedDigest + Environment.NewLine);
        var reported = await BuildProcess.CaptureAsync(executable, ["nextest", "--version"], context.Root);
        if (!reported.Contains(version, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"cargo-nextest reported '{reported}', expected version {version}.");
        }

        return executable;
    }
}
