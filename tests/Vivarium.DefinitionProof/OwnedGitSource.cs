using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Vivarium.DefinitionProof;

/// <summary>Read-only disposable anonymous source bytes; never mounts Host configuration or credentials.</summary>
internal sealed class OwnedGitSource(string root) : IAsyncDisposable
{
    private const string Owner = "us013-pilot-source-proof";
    private const string Image = "mcr.microsoft.com/dotnet/sdk@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317";
    private readonly string name = "vivarium-pilot-source-" + Guid.NewGuid().ToString("N")[..12];
    private string Work => Path.Combine(root, "source-work");
    private string Bare => Path.Combine(root, "source.git");
    private bool created;
    internal string Url { get; private set; } = "";

    internal async Task<string> StartAsync()
    {
        Directory.CreateDirectory(Work);
        await GitAsync(["init", "--initial-branch=main", Work]);
        await GitAsync(["-C", Work, "config", "user.name", "Owned source proof"]);
        await GitAsync(["-C", Work, "config", "user.email", "fixture@example.invalid"]);
        await File.WriteAllTextAsync(Path.Combine(Work, "selected.txt"), "old-queued-source\n", new UTF8Encoding(false));
        Directory.CreateDirectory(Path.Combine(Work, ".vivarium/steps"));
        await File.WriteAllTextAsync(Path.Combine(Work, ".vivarium/steps/0.sh"), "exit 83\n");
        Directory.CreateDirectory(Path.Combine(Work, ".repos"));
        await File.WriteAllTextAsync(Path.Combine(Work, ".repos/omitted.txt"), "excluded source\n");
        await GitAsync(["-C", Work, "add", "."]);
        await GitAsync(["-C", Work, "commit", "-m", "fixture: initial workload source"]);
        await GitAsync(["clone", "--bare", Work, Bare]);
        var listener = new TcpListener(IPAddress.Any, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        Url = "git://host.docker.internal:" + port + "/source.git";
        created = true;
        await ProofProcess.RunAsync("docker", ["run", "-d", "--name", name, "--label", "vivarium.owner=" + Owner,
            "--publish", port + ":9418", "--read-only", "--cap-drop", "ALL", "--security-opt", "no-new-privileges",
            "--user", "1654:1654", "--memory", "128m", "--cpus", "0.5", "--pids-limit", "64",
            "--mount", "type=bind,source=" + Bare + ",target=/sources/source.git,readonly", Image,
            "git", "-c", "safe.directory=*", "daemon", "--reuseaddr", "--export-all", "--strict-paths", "--port=9418", "--base-path=/sources", "/sources/source.git"]);
        var revision = (await GitAsync(["-C", Work, "rev-parse", "HEAD"])).Text;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var check = await GitAsync(["ls-remote", Url, "refs/heads/main"], allowFailure: true);
            if (check.ExitCode == 0 && check.Text.StartsWith(revision + "\t", StringComparison.Ordinal)) break;
            await Task.Delay(100, deadline.Token);
        }
        return revision;
    }

    internal async Task<string> ChangeAsync(string content = "coherent-reference-version\n")
    {
        await File.WriteAllTextAsync(Path.Combine(Work, "selected.txt"), content, new UTF8Encoding(false));
        await GitAsync(["-C", Work, "add", "selected.txt"]);
        await GitAsync(["-C", Work, "commit", "-m", "fixture: source changes while queued"]);
        // The only write is local to this owned fixture; no remote push or user Git configuration.
        await GitAsync(["-C", Work, "push", Bare, "main"]);
        return (await GitAsync(["-C", Work, "rev-parse", "HEAD"])).Text;
    }

    private static Task<ProofProcessResult> GitAsync(string[] args, bool allowFailure = false) => ProofProcess.RunAsync("git",
        ["-c", "core.hooksPath=NUL", "-c", "core.autocrlf=false", .. args], allowFailure: allowFailure);

    public async ValueTask DisposeAsync()
    {
        if (!created) return;
        var metadata = await ProofProcess.RunAsync("docker", ["inspect", "--format", "{{index .Config.Labels \"vivarium.owner\"}}", name], allowFailure: true);
        if (metadata.ExitCode == 0)
        {
            if (metadata.Text != Owner) throw new InvalidDataException("Source fixture ownership differs.");
            await ProofProcess.RunAsync("docker", ["rm", "--force", name]);
        }
    }
}
