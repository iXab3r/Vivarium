using Cake.Frosting;

[TaskName("DockerImage")]
[TaskDescription("Builds and smokes the linux-x64 viv-server Docker image from Compile output.")]
public sealed class DockerImageTask : AsyncFrostingTask<BuildContext>
{
    public override async Task RunAsync(BuildContext context)
    {
        var serverRoot = Path.Combine(
            ReleaseLayout.CompileRoot(context, "linux-x64"),
            "server");
        var executable = Path.Combine(serverRoot, "viv-server");
        if (!File.Exists(executable))
        {
            throw new FileNotFoundException(
                "DockerImage requires the linux-x64 Compile output.",
                executable);
        }

        var version = context.ProductVersion;
        var sourceSha = string.IsNullOrWhiteSpace(context.SourceSha) ? "local" : context.SourceSha;
        var image = $"viv-server:{version}";
        await BuildProcess.RunAsync(
            "docker",
            [
                "build",
                "--pull",
                "--file",
                Path.Combine(context.Root, "build", "docker", "viv-server.Dockerfile"),
                "--build-arg",
                $"VIVARIUM_VERSION={version}",
                "--build-arg",
                $"VIVARIUM_SOURCE_SHA={sourceSha}",
                "--tag",
                image,
                serverRoot,
            ],
            context.Root);

        var reported = await BuildProcess.CaptureAsync(
            "docker",
            ["run", "--rm", image, "--version"],
            context.Root,
            timeoutSeconds: 60);
        Console.WriteLine(reported);
        if (!string.Equals(reported, $"viv-server {version}", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Docker server version mismatch: expected viv-server {version}, got {reported}.");
        }
    }
}
