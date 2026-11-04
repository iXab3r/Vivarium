namespace Vivarium.DefinitionProof;

/// <summary>A dedicated Engine with its own graph, TLS keys and workspace; no outer Docker socket.</summary>
internal sealed class OwnedContainerRuntime : IAsyncDisposable
{
    internal const string Sdk = "mcr.microsoft.com/dotnet/sdk@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317";
    internal const string Node = "node:26.12.1-bookworm@sha256:3b764117159bda01d2396391e7789b6a9106af9729cfd001ef55e98557868310";
    private const string Dind = "docker:29.3.1-dind@sha256:686d2c5464787b0ca42420e0aa03a94fc9781ff9bf8b5ff947614b9fa4c3c3e0";
    internal string Scope { get; } = "us013-container-" + Guid.NewGuid().ToString("N")[..12];
    internal string Network => Scope + "-network";
    internal string Workspace => Scope + "-workspace";
    internal string Certificates => Scope + "-certificates";
    private string Daemon => Scope + "-daemon";
    private string Graph => Scope + "-graph";
    private readonly List<(string Kind, string Name)> resources = [];

    internal async Task StartAsync(bool actualT3 = false)
    {
        await Docker(["network", "create", "--label", "vivarium.owner=" + Scope, Network]);
        resources.Add(("network", Network));
        foreach (var name in new[] { Graph, Certificates, Workspace })
        {
            await Docker(["volume", "create", "--label", "vivarium.owner=" + Scope, name]);
            resources.Add(("volume", name));
        }
        resources.Add(("container", Daemon));
        await Docker(["run", "-d", "--pull", "never", "--name", Daemon, "--label", "vivarium.owner=" + Scope,
            "--hostname", "docker", "--network", Network, "--network-alias", "docker", "--privileged",
            "--memory", actualT3 ? "12g" : "2g", "--cpus", actualT3 ? "8" : "2", "--pids-limit", actualT3 ? "2048" : "512", "--env", "DOCKER_TLS_CERTDIR=/certs",
            "--tmpfs", "/run:rw,nosuid,size=128m", "--tmpfs", actualT3 ? "/tmp:rw,nosuid,size=1g" : "/tmp:rw,nosuid,size=64m",
            "--mount", $"type=volume,source={Graph},target=/var/lib/docker",
            "--mount", $"type=volume,source={Certificates},target=/certs/client",
            "--mount", $"type=volume,source={Workspace},target=/data/builds", Dind]);
        var ready = false;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var response = await Docker(["exec", Daemon, "docker", "--host", "unix:///var/run/docker.sock", "info", "--format", "{{.ID}}"], true);
            if (response.ExitCode == 0) { ready = true; break; }
            await Task.Delay(250);
        }
        if (!ready) throw new InvalidOperationException("The owned Engine did not become ready.");
        await Docker(["run", "--rm", "--pull", "never", "--label", "vivarium.owner=" + Scope, "--network", "none",
            "--read-only", "--cap-drop", "ALL", "--cap-add", "CHOWN", "--user", "0:0",
            "--mount", $"type=volume,source={Certificates},target=/cert-client",
            "--mount", $"type=volume,source={Workspace},target=/workspace", Sdk, "sh", "-c",
            "chmod 600 /cert-client/ca.pem /cert-client/cert.pem /cert-client/key.pem && chmod 700 /cert-client && chown 1654:1654 /cert-client/ca.pem /cert-client/cert.pem /cert-client/key.pem /cert-client /workspace"]);
    }

    internal string[] MountArguments => ["--network", Network,
        "--mount", $"type=volume,source={Workspace},target=/data/builds",
        "--mount", $"type=volume,source={Certificates},target=/cert-client,readonly"];

    internal async Task<ProofProcessResult> WorkerAsync(string binaries, string worker, string resultDirectory)
    {
        var name = Scope + "-worker-" + Guid.NewGuid().ToString("N")[..8];
        resources.Add(("container", name));
        return await Docker(["run", "--name", name, "--pull", "never", "--label", "vivarium.owner=" + Scope,
            "--read-only", "--cap-drop", "ALL", "--security-opt", "no-new-privileges", "--user", "1654:1654",
            "--memory", "512m", "--cpus", "1", "--pids-limit", "128", "--tmpfs", "/tmp:rw,nosuid,size=32m",
            .. MountArguments, "--mount", $"type=bind,source={binaries},target=/app,readonly",
            "--mount", $"type=bind,source={resultDirectory},target=/results",
            Sdk, "dotnet", "/app/Vivarium.DefinitionProof.dll", worker, Scope], true, TimeSpan.FromMinutes(4));
    }

    internal async Task<string> RemainingAsync() => (await Docker(["exec", Daemon, "docker", "--host", "unix:///var/run/docker.sock",
        "ps", "-a", "--format", "{{.ID}} {{.State}}"])).Text;

    internal async Task<bool> NodeRunningAsync()
    {
        var containers = (await Docker(["exec", Daemon, "docker", "--host", "unix:///var/run/docker.sock", "ps", "--format", "{{.ID}}"])).Text;
        foreach (var id in containers.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var executables = await Docker(["exec", Daemon, "docker", "--host", "unix:///var/run/docker.sock", "exec", id, "sh", "-c",
                "for p in /proc/[0-9]*/exe; do readlink \"$p\" 2>/dev/null || :; done"], true);
            if (executables.Text.Split('\n').Contains("/usr/local/bin/node")) return true;
        }
        return false;
    }

    internal async Task VerifyVolatileSecretBoundaryAsync()
    {
        var mounts = (await Docker(["exec", Daemon, "sh", "-c", "awk '$2 == \"/run\" || $2 == \"/tmp\" {print $2, $3}' /proc/mounts"])).Text;
        Assert.That(mounts.Split('\n'), Is.EquivalentTo(new[] { "/run tmpfs", "/tmp tmpfs" }));
        var persisted = await Docker(["exec", Daemon, "sh", "-c",
            "set -eu; f=$(mktemp /tmp/vivarium-metadata-XXXXXX); trap 'rm -f \"$f\"' EXIT; "
            + "find /var/lib/docker -xdev -type f \\( -name config.v2.json -o -name hostconfig.json -o -name '*.log' -o -name '*.db' -o -name process.json \\) -print0 >\"$f\"; "
            + "xargs -0 -r sh -c 'for p do if grep -alF synthetic-container-mask- \"$p\"; then :; else [ \"$?\" = 1 ] || exit 2; fi; done' sh <\"$f\""]);
        Assert.That(persisted.Text, Is.Empty, "A live synthetic run secret must not appear in persistent Engine metadata/logs.");
    }

    public async ValueTask DisposeAsync()
    {
        var failures = new List<Exception>();
        foreach (var (kind, name) in resources.AsEnumerable().Reverse())
        {
            try
            {
                var format = kind == "container" ? "{{index .Config.Labels \"vivarium.owner\"}}" : "{{index .Labels \"vivarium.owner\"}}";
                var inspected = await Docker([kind, "inspect", "--format", format, name], true);
                if (inspected.ExitCode != 0) continue;
                if (inspected.Text != Scope) throw new InvalidDataException("Owned runtime cleanup identity differs.");
                await Docker(kind == "container" ? ["rm", "--force", "--volumes", name] : [kind, "rm", name]);
            }
            catch (Exception exception) { failures.Add(exception); }
        }
        if (failures.Count != 0) throw new AggregateException("Owned runtime cleanup was incomplete.", failures);
    }

    private static Task<ProofProcessResult> Docker(string[] arguments, bool allowFailure = false, TimeSpan? deadline = null) =>
        ProofProcess.RunAsync("docker", arguments, allowFailure: allowFailure, deadline: deadline);
}
