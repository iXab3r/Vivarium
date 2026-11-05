using Cake.Frosting;

[TaskName("PrepareDependencies")]
[TaskDescription("Acquires and verifies the manifest-pinned owner release packages before product restore.")]
public sealed class PrepareDependenciesTask : AsyncFrostingTask<BuildContext>
{
    public override async Task RunAsync(BuildContext context)
    {
        using var http = DependencyArtifacts.CreateHttpClient();
        await DependencyArtifacts.PrepareAsync(context.Root, http);
    }
}
