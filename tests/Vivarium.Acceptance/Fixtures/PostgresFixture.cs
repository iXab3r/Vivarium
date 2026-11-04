// Adapted from EyeAuras.Depot 20238384013326cddb3296963a6d101a0970efc0; consumer-owned fixture policy.
using System.Security.Cryptography;
using CliWrap;
using EyeAuras.Automation;
using Npgsql;

namespace Vivarium.Acceptance.Fixtures;

/// <summary>Owned loopback PostgreSQL: create isolated test data or reopen explicitly prepared retained data.</summary>
public sealed class PostgresFixture
{
    /// <summary>Private privileged connection used only for fixture provisioning/migrations, never stored as runtime settings.</summary>
    public required string MaintenanceConnection { get; init; }
    /// <summary>Private DML-only connection for production EF and Host execution.</summary>
    public required string RuntimeConnection { get; init; }

    /// <summary>Initialize/run new PostgreSQL data under the scope and register graceful stop before framework cleanup.</summary>
    public static async Task<PostgresFixture> StartAsync(AutomationScope scope, string binaries)
    {
        var run = await AutomationRun.ConnectAsync(scope.CancellationToken);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(scope.WorkingDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var root = Path.Combine(scope.WorkingDirectory, "postgres");
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var passwordFile = Path.Combine(scope.WorkingDirectory, "postgres-password");
        await File.WriteAllTextAsync(passwordFile, password, scope.CancellationToken);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(passwordFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var initdb = Path.Combine(binaries, "initdb");
        if (!File.Exists(initdb)) throw new InvalidOperationException("Prepare PostgreSQL binaries before this run.");
        await PrivateCommand.ExecuteAsync(run, Cli.Wrap(initdb).WithArguments([
            "--pgdata", root, "--username", "vivarium_owner", "--auth-host", "scram-sha-256",
            "--auth-local", "trust", "--pwfile", passwordFile, "--locale=C", "--encoding=UTF8"
        ]).WithWorkingDirectory(scope.WorkingDirectory), null, scope.CancellationToken);
        var owner = new NpgsqlConnectionStringBuilder
        {
            Host = "127.0.0.1", Port = FixturePorts.Loopback(), Database = "postgres", Username = "vivarium_owner",
            Password = password, Pooling = false, Timeout = 2, CommandTimeout = 5
        };
        await OpenServerAsync(scope, binaries, root, owner.ConnectionString);
        await using var connection = new NpgsqlConnection(owner.ConnectionString);
        await connection.OpenAsync(scope.CancellationToken);
        var database = "vivarium_" + scope.Id;
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\" OWNER vivarium_owner", connection))
            await create.ExecuteNonQueryAsync(scope.CancellationToken);
        var runtimePassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        await using (var role = new NpgsqlCommand($"CREATE ROLE vivarium_runtime LOGIN PASSWORD '{runtimePassword}' NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT", connection))
            await role.ExecuteNonQueryAsync(scope.CancellationToken);
        await using (var grants = new NpgsqlCommand($"REVOKE ALL ON DATABASE \"{database}\" FROM PUBLIC; GRANT CONNECT ON DATABASE \"{database}\" TO vivarium_runtime", connection))
            await grants.ExecuteNonQueryAsync(scope.CancellationToken);
        owner.Database = database;
        var runtime = new NpgsqlConnectionStringBuilder(owner.ConnectionString)
            { Username = "vivarium_runtime", Password = runtimePassword };
        return new() { MaintenanceConnection = owner.ConnectionString, RuntimeConnection = runtime.ConnectionString };
    }

    private static async Task OpenServerAsync(AutomationScope scope, string binaries, string dataDirectory, string connectionString)
    {
        if (!Path.IsPathFullyQualified(dataDirectory) || !File.Exists(Path.Combine(dataDirectory, "PG_VERSION")))
            throw new InvalidOperationException("An initialized owned PostgreSQL directory is required.");
        var owner = new NpgsqlConnectionStringBuilder(connectionString);
        if (owner.Host != "127.0.0.1" || owner.Port is < 1024 or > 65535)
            throw new InvalidOperationException("Owned PostgreSQL must use an explicit loopback port.");
        var run = await AutomationRun.ConnectAsync(scope.CancellationToken);
        if (File.Exists(Path.Combine(dataDirectory, "postmaster.pid")))
            throw new InvalidOperationException("The owned PostgreSQL directory is already in use or needs explicit stale-lock inspection.");
        var process = await scope.StartProcessAsync(Cli.Wrap(Path.Combine(binaries, "postgres")).WithArguments([
            "-D", dataDirectory, "-h", "127.0.0.1", "-p", owner.Port.ToString(), "-k", "",
            "-c", "max_connections=20", "-c", "log_statement=none", "-c", "log_min_error_statement=panic",
            "-c", "log_parameter_max_length_on_error=0"
        ]).WithWorkingDirectory(scope.WorkingDirectory));
        scope.BeforeStop("Stop owned PostgreSQL", async (_, token) =>
        {
            if ((await process.GetStatusAsync(token)).HasExited) return;
            using var pid = new StreamReader(new FileStream(Path.Combine(dataDirectory, "postmaster.pid"),
                FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            if (await pid.ReadLineAsync(token) != process.ProcessId.ToString())
                throw new InvalidOperationException("PostgreSQL process ownership changed; refusing to stop a borrowed server.");
            await PrivateCommand.ExecuteAsync(run, Cli.Wrap(Path.Combine(binaries, "pg_ctl"))
                .WithArguments(["-D", dataDirectory, "-m", "fast", "-w", "stop"])
                .WithWorkingDirectory(scope.WorkingDirectory), null, token);
        }, timeoutSeconds: 15);
        await AutomationWait.UntilAsync(async token =>
        {
            try
            {
                var pidPath = Path.Combine(dataDirectory, "postmaster.pid");
                if (!File.Exists(pidPath)) return false;
                using var pid = new StreamReader(new FileStream(pidPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
                if (await pid.ReadLineAsync(token) != process.ProcessId.ToString()) return false;
                if ((await process.GetStatusAsync(token)).HasExited) return false;
                await using var probe = new NpgsqlConnection(owner.ConnectionString);
                await probe.OpenAsync(token);
                return true;
            }
            catch (NpgsqlException) { return false; }
        }, ready => ready, "owned PostgreSQL readiness", TimeSpan.FromSeconds(15), scope.CancellationToken,
            ensureAlive: scope.EnsureRunningAsync);
    }

    /// <summary>Create the fixture-required isolated issuer database inside this owned cluster, never a remote/shared database.</summary>
    public async Task<string> CreateIssuerDatabaseAsync(CancellationToken token)
    {
        var builder = new NpgsqlConnectionStringBuilder(MaintenanceConnection);
        var database = "eyeauras_auth_e2e_" + Guid.NewGuid().ToString("N");
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(token);
        await using var command = new NpgsqlCommand($"CREATE DATABASE \"{database}\" OWNER vivarium_owner", connection);
        await command.ExecuteNonQueryAsync(token);
        builder.Database = database;
        return builder.ConnectionString;
    }

    /// <summary>Grant runtime only existing-schema DML after privileged migrations have completed.</summary>
    public async Task GrantRuntimeAsync(CancellationToken token)
    {
        await using var connection = new NpgsqlConnection(MaintenanceConnection);
        await connection.OpenAsync(token);
        await using var command = new NpgsqlCommand("""
            REVOKE CREATE ON SCHEMA public FROM PUBLIC;
            GRANT USAGE ON SCHEMA public TO vivarium_runtime;
            GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO vivarium_runtime;
            GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO vivarium_runtime;
            """, connection);
        await command.ExecuteNonQueryAsync(token);
    }
}
