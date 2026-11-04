using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text;

namespace Vivarium.DefinitionProof;

/// <summary>One dedicated Linux process, with only its own data and bootstrap plus read-only Agent binaries.</summary>
internal sealed class OwnedDockerAgent(string root, bool gitTools = false) : IAsyncDisposable
{
    private const string Owner = "us013-r0-runtime-proof";
    private string Image => gitTools ? "mcr.microsoft.com/dotnet/sdk@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317"
        : "mcr.microsoft.com/dotnet/runtime@sha256:939555726cae4c916d212323ac3c0792996dcac2614f00e82cc40fcf059e1af3";
    private readonly string name = "vivarium-r0-agent-" + Guid.NewGuid().ToString("N")[..12];
    private bool created;
    private bool volumeCreated;
    private string Volume => name + "-data";

    internal async Task StartAsync(Uri origin, string certificatePath, string enrollment)
    {
        var repository = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "Vivarium.slnx"))) repository = repository.Parent;
        var binaries = Path.Combine(repository?.FullName ?? throw new InvalidDataException("The proof repository is absent."), "src/Vivarium.Agent/bin/Debug/net10.0");
        var bootstrap = Path.Combine(root, "linux-agent-bootstrap.json");
        using var certificate = X509CertificateLoader.LoadPkcs12FromFile(certificatePath, null);
        await File.WriteAllTextAsync(bootstrap, JsonSerializer.Serialize(new
        {
            controllerUrl = $"https://host.docker.internal:{origin.Port}",
            certFingerprint = certificate.GetCertHashString(HashAlgorithmName.SHA256), enrollToken = enrollment,
        }));
        volumeCreated = true;
        await ProofProcess.RunAsync("docker", ["volume", "create", "--label", "vivarium.owner=" + Owner, Volume]);
        await ProofProcess.RunAsync("docker", ["run", "--rm", "--label", "vivarium.owner=" + Owner, "--network", "none",
            "--read-only", "--cap-drop", "ALL", "--cap-add", "CHOWN", "--mount", $"type=volume,source={Volume},target=/data",
            "--user", "0:0", Image, "chown", "1654:1654", "/data"]);
        created = true;
        await ProofProcess.RunAsync("docker", ["run", "-d", "--name", name, "--label", "vivarium.owner=" + Owner,
            "--restart", "no", "--memory", "256m", "--cpus", "1", "--pids-limit", "128", "--cap-drop", "ALL",
            "--security-opt", "no-new-privileges", "--read-only", "--tmpfs", "/tmp:rw,nosuid,size=16m", "--user", "1654:1654",
            "--mount", $"type=bind,source={binaries},target=/app,readonly",
            "--mount", $"type=bind,source={bootstrap},target=/bootstrap.json,readonly",
            "--mount", $"type=volume,source={Volume},target=/data", Image,
            "dotnet", "/app/Vivarium.Agent.dll", "--config", "/bootstrap.json", "--data", "/data"]);
    }

    public async ValueTask DisposeAsync()
    {
        if (created)
        {
            var inspected = await ProofProcess.RunAsync("docker", ["inspect", "--format", "{{index .Config.Labels \"vivarium.owner\"}}", name], allowFailure: true);
            if (inspected.ExitCode == 0)
            {
                if (inspected.Text != Owner) throw new InvalidDataException("The owned Agent container label differs.");
                await ProofProcess.RunAsync("docker", ["rm", "--force", name]);
            }
            created = false;
        }
        if (volumeCreated)
        {
            var inspected = await ProofProcess.RunAsync("docker", ["volume", "inspect", "--format", "{{index .Labels \"vivarium.owner\"}}", Volume], allowFailure: true);
            if (inspected.ExitCode == 0)
            {
                if (inspected.Text != Owner) throw new InvalidDataException("The owned Agent volume label differs.");
                await ProofProcess.RunAsync("docker", ["volume", "rm", Volume]);
            }
            volumeCreated = false;
        }
    }

    internal async Task<string> DiagnosticsAsync(string enrollment)
    {
        var result = await ProofProcess.RunAsync("docker", ["logs", "--tail", "20", name], allowFailure: true);
        var text = Encoding.UTF8.GetString(result.Output) + Encoding.UTF8.GetString(result.Error);
        return text.Replace(enrollment, "[REDACTED]", StringComparison.Ordinal);
    }

    internal async Task<string> ProcessStatesAsync()
    {
        // Process names/state/wait channels only: arguments and environments can contain secrets.
        var result = await ProofProcess.RunAsync("docker", ["exec", name, "/bin/sh", "-c",
            "for p in /proc/[0-9]*; do [ -r \"$p/comm\" ] || continue; printf '%s ' \"${p##*/}\"; cat \"$p/comm\"; sed -n '/^State:/p' \"$p/status\"; cat \"$p/wchan\"; echo; done"], allowFailure: true);
        return result.Text;
    }

    internal async Task ReleaseCancellationAsync() => await ProofProcess.RunAsync("docker", ["exec", "--user", "1654:1654", name, "touch", "/data/allow-cancel-exit"]);
}
