using Cake.Frosting;

[TaskName("Publish")]
[TaskDescription("Publishes runnable local binaries for one supported RID.")]
public sealed class PublishTask : AsyncFrostingTask<BuildContext>
{
    public override Task RunAsync(BuildContext context) =>
        LocalPublisher.PublishAsync(context, context.TargetRid);
}

[TaskName("PublishAll")]
[TaskDescription("Publishes runnable local binaries for every supported RID.")]
public sealed class PublishAllTask : AsyncFrostingTask<BuildContext>
{
    public override async Task RunAsync(BuildContext context)
    {
        if (context.RequestedRid is not null)
        {
            throw new InvalidOperationException("PublishAll does not accept --rid; use Publish for one target.");
        }

        foreach (var rid in ReleaseLayout.SupportedRids)
        {
            await LocalPublisher.PublishAsync(context, rid);
        }
    }
}

internal static class LocalPublisher
{
    public static async Task PublishAsync(BuildContext context, string rid)
    {
        ReleaseLayout.RequireSupportedRid(rid);
        var target = Path.Combine(context.OutRoot, "build", rid);
        var publishRoot = Path.Combine(context.OutRoot, "build-publish", rid);
        PayloadSmokeTask.RecreateDirectory(target);
        PayloadSmokeTask.RecreateDirectory(publishRoot);

        try
        {
            var version = context.ProductVersion;
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
                version + "\n");
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

        Console.WriteLine($"Vivarium {rid} local build: {target}");
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
