using Cake.Frosting;
using System.Text;
using System.Text.Json;

[TaskName("Compile")]
[TaskDescription("Compiles runnable binaries for one supported RID.")]
public sealed class CompileTask : AsyncFrostingTask<BuildContext>
{
    public override Task RunAsync(BuildContext context) =>
        PlatformCompiler.CompileAsync(context, context.TargetRid);
}

[TaskName("CompileAll")]
[TaskDescription("Compiles runnable binaries for every supported RID.")]
public sealed class CompileAllTask : AsyncFrostingTask<BuildContext>
{
    public override async Task RunAsync(BuildContext context)
    {
        if (context.RequestedRid is not null)
        {
            throw new InvalidOperationException("CompileAll does not accept --rid; use Compile for one target.");
        }

        foreach (var rid in ReleaseLayout.SupportedRids)
        {
            await PlatformCompiler.CompileAsync(context, rid);
        }
    }
}

[TaskName("CompileSmoke")]
[TaskDescription("Runs native controller, CLI, agent, and updater probes from one Compile output.")]
public sealed class CompileSmokeTask : AsyncFrostingTask<BuildContext>
{
    public override async Task RunAsync(BuildContext context)
    {
        var rid = context.TargetRid;
        if (!string.Equals(rid, context.HostRid, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"CompileSmoke requires the native host RID; requested {rid}, host {context.HostRid}.");
        }

        var root = ReleaseLayout.CompileRoot(context, rid);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Compile output is missing: {root}");
        }

        var extension = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
        var smokeRoot = Path.Combine(context.OutRoot, "compile-smoke", rid);
        PayloadSmokeTask.RecreateDirectory(smokeRoot);
        var serverRoot = Path.Combine(root, "server");
        await ReleaseSmokeTask.SmokeControllerAsync(
            Path.Combine(serverRoot, "viv-server" + extension),
            serverRoot,
            Path.Combine(smokeRoot, "controller-data"));

        var cliRoot = Path.Combine(root, "cli");
        await ReleaseSmokeTask.SmokeCliAsync(
            Path.Combine(cliRoot, "viv-cli" + extension),
            cliRoot,
            context.ProductVersion);

        var agentRoot = Path.Combine(root, "agent");
        await BuildProcess.RunExpectingExitCodeAsync(
            Path.Combine(agentRoot, "agent", "current", "viv-agent" + extension),
            [],
            agentRoot,
            expectedExitCode: 2,
            timeoutSeconds: 30);
        await BuildProcess.RunExpectingExitCodeAsync(
            Path.Combine(agentRoot, "viv-agent-update" + extension),
            [],
            agentRoot,
            expectedExitCode: 2,
            timeoutSeconds: 30);
    }
}

internal static class PlatformCompiler
{
    public static async Task CompileAsync(BuildContext context, string rid)
    {
        context.SetTeamCityBuildNumber();
        ReleaseLayout.RequireSupportedRid(rid);
        var version = context.ProductVersion;
        var sourceSha = context.SourceSha is null ? null : context.RequireSourceSha();
        var target = Path.Combine(context.OutRoot, "build", rid);
        var publishRoot = Path.Combine(context.OutRoot, "build-publish", rid);
        PayloadSmokeTask.RecreateDirectory(target);
        PayloadSmokeTask.RecreateDirectory(publishRoot);

        try
        {
            foreach (var component in ReleaseLayout.Components)
            {
                var componentOutput = Path.Combine(publishRoot, component.Id);
                await DotNetPublisher.PublishAsync(context, component, rid, componentOutput, version);
                var (destination, releaseName) = LocalDestination(target, component);
                DotNetPublisher.CopyOutput(componentOutput, destination, component, rid, releaseName);
                Directory.Delete(componentOutput, recursive: true);
            }

            var agentRoot = Path.Combine(target, "agent");
            File.Copy(
                Path.Combine(context.Root, "build", "assets", "bootstrap.json.sample"),
                Path.Combine(agentRoot, "bootstrap.json.sample"),
                overwrite: false);
            File.WriteAllText(
                Path.Combine(agentRoot, "agent", "version"),
                version + "\n",
                new UTF8Encoding(false));
            CompileManifestWriter.Write(target, rid, version, sourceSha);
        }
        catch
        {
            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }
            throw;
        }
        finally
        {
            if (Directory.Exists(publishRoot))
            {
                Directory.Delete(publishRoot, recursive: true);
            }
            var intermediateRoot = Path.GetDirectoryName(publishRoot)!;
            if (Directory.Exists(intermediateRoot) && !Directory.EnumerateFileSystemEntries(intermediateRoot).Any())
            {
                Directory.Delete(intermediateRoot);
            }
        }

        Console.WriteLine($"Vivarium {rid} compile output: {target}");
    }

    private static (string Destination, string ReleaseName) LocalDestination(
        string target,
        ReleaseComponent component)
    {
        return component.Id switch
        {
            "controller" => (Path.Combine(target, "server"), "viv-server"),
            "agent" => (Path.Combine(target, "agent", "agent", "current"), "viv-agent"),
            "bootstrap" => (Path.Combine(target, "agent"), "viv-agent-update"),
            "cli" => (Path.Combine(target, "cli"), "viv-cli"),
            _ => throw new InvalidOperationException($"Unknown local-build component: {component.Id}"),
        };
    }
}

internal static class CompileManifestWriter
{
    public static void Write(string root, string rid, string version, string? sourceSha)
    {
        var manifest = new CompileManifest(1, rid, version, sourceSha, DescribeFiles(root));
        var json = JsonSerializer.Serialize(manifest, ReleaseLayout.JsonOptions) + "\n";
        File.WriteAllText(
            Path.Combine(root, ReleaseLayout.CompileManifestName),
            json,
            new UTF8Encoding(false));
    }

    internal static CompileFile[] DescribeFiles(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => !string.Equals(
                Path.GetRelativePath(root, path).Replace('\\', '/'),
                ReleaseLayout.CompileManifestName,
                StringComparison.Ordinal))
            .Select(path => new CompileFile(
                Path.GetRelativePath(root, path).Replace('\\', '/'),
                new FileInfo(path).Length,
                ReleaseAsset.HashFile(path)))
            .OrderBy(file => file.Path, StringComparer.Ordinal)
            .ToArray();
}

internal static class CompileManifestVerifier
{
    public static void Verify(
        BuildContext context,
        string rid,
        string expectedVersion,
        string expectedSourceSha)
    {
        var root = ReleaseLayout.CompileRoot(context, rid);
        var manifestPath = Path.Combine(root, ReleaseLayout.CompileManifestName);
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException($"Compile manifest is missing for {rid}.", manifestPath);
        }

        var manifest = JsonSerializer.Deserialize<CompileManifest>(
            File.ReadAllText(manifestPath),
            ReleaseLayout.JsonOptions) ?? throw new InvalidDataException($"Compile manifest is empty for {rid}.");
        if (manifest.SchemaVersion != 1 ||
            !string.Equals(manifest.Rid, rid, StringComparison.Ordinal) ||
            !string.Equals(manifest.Version, expectedVersion, StringComparison.Ordinal) ||
            !string.Equals(manifest.SourceSha, expectedSourceSha, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Compile manifest identity does not match Release for {rid}.");
        }

        var actualFiles = CompileManifestWriter.DescribeFiles(root);
        if (manifest.Files is null || manifest.Files.Length == 0 ||
            !manifest.Files.SequenceEqual(actualFiles))
        {
            throw new InvalidDataException($"Compile files do not match the verified manifest for {rid}.");
        }

        var versionMarker = Path.Combine(root, "agent", "agent", "version");
        if (!string.Equals(File.ReadAllText(versionMarker), expectedVersion + "\n", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Compile agent version marker mismatch for {rid}.");
        }
    }
}

internal sealed record CompileManifest(
    int SchemaVersion,
    string Rid,
    string Version,
    string? SourceSha,
    CompileFile[] Files);

internal sealed record CompileFile(string Path, long Size, string Sha256);

internal static class DotNetPublisher
{
    public static async Task PublishAsync(
        BuildContext context,
        ReleaseComponent component,
        string rid,
        string output,
        string? version = null)
    {
        Directory.CreateDirectory(output);
        var arguments = new List<string>
        {
            "publish",
            component.Project,
            "-c",
            context.BuildConfiguration,
            "--maxcpucount:1",
            "-r",
            rid,
            "--self-contained",
            "-o",
            output,
            "--nologo",
            "-p:DebugSymbols=false",
            "-p:DebugType=None",
        };
        if (version is not null)
        {
            arguments.Add($"-p:Version={version}");
        }
        arguments.AddRange(context.ContinuousIntegrationProperties());
        await BuildProcess.RunAsync("dotnet", arguments, context.Root);
    }

    public static void CopyOutput(
        string source,
        string destination,
        ReleaseComponent component,
        string rid,
        string releaseName)
    {
        Directory.CreateDirectory(destination);
        var extension = rid.StartsWith("win-", StringComparison.Ordinal) ? ".exe" : string.Empty;
        var publishedExecutable = component.AssemblyName + extension;
        var copiedExecutable = false;
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            if (string.Equals(relative, publishedExecutable, StringComparison.Ordinal))
            {
                relative = releaseName + extension;
                copiedExecutable = true;
            }

            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
            if (!OperatingSystem.IsWindows() && !rid.StartsWith("win-", StringComparison.Ordinal) &&
                string.Equals(relative, releaseName + extension, StringComparison.Ordinal))
            {
                File.SetUnixFileMode(target, (UnixFileMode)Convert.ToInt32("755", 8));
            }
        }

        if (!copiedExecutable)
        {
            throw new FileNotFoundException(
                $"Published {component.Id} executable is missing.",
                Path.Combine(source, publishedExecutable));
        }
    }
}
