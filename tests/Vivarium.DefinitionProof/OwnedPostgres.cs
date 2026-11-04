using System.Security.Cryptography;
using System.Text.Json;
using Npgsql;

namespace Vivarium.DefinitionProof;

internal sealed class OwnedPostgres : IAsyncDisposable
{
    private const string Owner = "us013-p0-definition-proof";
    private const string Image = "postgres@sha256:0af65001d05296a2ead57ac4a6412433d8913d1bb5d0c88435a7d1e1ee5cb04b";
    private string name = "vivarium-us013-proof-" + Guid.NewGuid().ToString("N")[..12];
    private bool containerCreated;
    private bool volumeCreated;
    private bool networkCreated;
    private string Volume => name + "-data";
    private string Network => name + "-network";
    public string ConnectionString { get; private set; } = "";
    public string PooledConnectionString => new NpgsqlConnectionStringBuilder(ConnectionString)
        { Pooling = true, MaxPoolSize = 16 }.ConnectionString;
    public string ServerVersion { get; private set; } = "";

    internal bool Retain { get; set; }
    internal object RecoveryState => new { name, ConnectionString };
    internal static async Task<OwnedPostgres> ReopenAsync(JsonElement state)
    {
        var value = new OwnedPostgres { name = state.GetProperty("name").GetString()!,
            ConnectionString = state.GetProperty("ConnectionString").GetString()!, Retain = true };
        if (!System.Text.RegularExpressions.Regex.IsMatch(value.name, "^vivarium-us013-proof-[a-f0-9]{12}$"))
            throw new InvalidDataException("Recovery resource name differs.");
        foreach (var resource in new[] { ("container", value.name), ("volume", value.Volume), ("network", value.Network) })
            if (!await VerifyAsync(resource.Item1, resource.Item2)) throw new InvalidDataException("Recovery resource absent.");
        value.containerCreated = value.volumeCreated = value.networkCreated = true;
        var mounts = await DockerAsync("inspect", "--format", "{{json .Mounts}}", value.name);
        using (var mounted = JsonDocument.Parse(mounts))
            if (!mounted.RootElement.EnumerateArray().Any(m => m.GetProperty("Type").GetString() == "volume" &&
                m.GetProperty("Name").GetString() == value.Volume && m.GetProperty("Destination").GetString() == "/var/lib/postgresql/data"))
                throw new InvalidDataException("Recovery database volume differs.");
        var original = new NpgsqlConnectionStringBuilder(value.ConnectionString);
        if (original.Host != "127.0.0.1") throw new InvalidDataException("Recovery database host differs.");
        value.ConnectionString = new NpgsqlConnectionStringBuilder(value.ConnectionString) { Port = await value.ReadPortAsync() }.ConnectionString;
        await using var connection = new NpgsqlConnection(value.ConnectionString);
        await connection.OpenAsync();
        return value;
    }

    public async Task StartAsync()
    {
        try
        {
            networkCreated = true;
            await DockerAsync("network", "create", "--label", "vivarium.owner=" + Owner, Network);
            volumeCreated = true;
            await DockerAsync("volume", "create", "--label", "vivarium.owner=" + Owner, Volume);
            var password = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            containerCreated = true;
            await ProofProcess.RunAsync("docker", ["run", "-d", "--name", name,
                "--label", "vivarium.owner=" + Owner, "--network", Network,
                "--memory", "512m", "--cpus", "1", "--restart", "no",
                "--publish", "127.0.0.1::5432", "--env", "POSTGRES_PASSWORD",
                "--mount", $"type=volume,source={Volume},target=/var/lib/postgresql/data", Image],
                environment: new Dictionary<string, string> { ["POSTGRES_PASSWORD"] = password });
            ConnectionString = new NpgsqlConnectionStringBuilder
            {
                Host = "127.0.0.1", Port = await ReadPortAsync(),
                Username = "postgres", Password = password, Database = "postgres",
                Timeout = 2, CommandTimeout = 30, Pooling = false,
                SslMode = SslMode.Disable, IncludeErrorDetail = false,
            }.ConnectionString;
            using var readiness = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            while (true)
            {
                try
                {
                    await using var connection = new NpgsqlConnection(ConnectionString);
                    await connection.OpenAsync(readiness.Token);
                    ServerVersion = connection.PostgreSqlVersion.ToString();
                    break;
                }
                catch (NpgsqlException) when (!readiness.IsCancellationRequested)
                {
                    await Task.Delay(100, readiness.Token);
                }
            }
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    public async Task RestartAsync()
    {
        if (!await VerifyAsync("container", name)) throw new InvalidOperationException("Proof database is absent.");
        await DockerAsync("restart", "--time", "10", name);
        // Docker can allocate a different dynamic host port on restart; the volume/DB is unchanged.
        ConnectionString = new NpgsqlConnectionStringBuilder(ConnectionString) { Port = await ReadPortAsync() }.ConnectionString;
        using var readiness = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        while (true)
        {
            try
            {
                await using var connection = new NpgsqlConnection(ConnectionString);
                await connection.OpenAsync(readiness.Token);
                return;
            }
            catch (NpgsqlException) when (!readiness.IsCancellationRequested)
            {
                await Task.Delay(100, readiness.Token);
            }
        }
    }

    private async Task<int> ReadPortAsync()
    {
        var portJson = await DockerAsync("inspect", "--format", "{{json (index .NetworkSettings.Ports \"5432/tcp\")}}", name);
        using var ports = JsonDocument.Parse(portJson);
        var binding = ports.RootElement[0];
        if (binding.GetProperty("HostIp").GetString() != "127.0.0.1") throw new InvalidOperationException("PG proof must bind loopback only.");
        return int.Parse(binding.GetProperty("HostPort").GetString()!);
    }

    private static async Task<string> DockerAsync(params string[] arguments) =>
        (await ProofProcess.RunAsync("docker", arguments)).Text;

    private static async Task<bool> VerifyAsync(string kind, string target)
    {
        var template = kind == "container" ? "{{index .Config.Labels \"vivarium.owner\"}}" : "{{index .Labels \"vivarium.owner\"}}";
        var inspected = await ProofProcess.RunAsync("docker", [kind, "inspect", "--format", template, target], allowFailure: true);
        if (inspected.ExitCode != 0)
        {
            if (System.Text.Encoding.UTF8.GetString(inspected.Error).Contains("No such", StringComparison.OrdinalIgnoreCase)) return false;
            throw new InvalidOperationException("Cannot verify proof resource ownership; cleanup refused.");
        }
        if (inspected.Text != Owner) throw new InvalidOperationException("Proof resource ownership differs; cleanup refused.");
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        if (ConnectionString.Length > 0)
            using (var pool = new NpgsqlConnection(PooledConnectionString)) NpgsqlConnection.ClearPool(pool);
        if (Retain) return;
        if (containerCreated)
        {
            if (await VerifyAsync("container", name)) await DockerAsync("rm", "--force", name);
            containerCreated = false;
        }
        if (volumeCreated)
        {
            if (await VerifyAsync("volume", Volume)) await DockerAsync("volume", "rm", Volume);
            volumeCreated = false;
        }
        if (networkCreated)
        {
            if (await VerifyAsync("network", Network)) await DockerAsync("network", "rm", Network);
            networkCreated = false;
        }
    }
}
