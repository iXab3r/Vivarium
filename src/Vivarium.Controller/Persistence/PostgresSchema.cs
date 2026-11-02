using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Vivarium.Controller.Persistence;

/// <summary>Explicit local maintenance with a durable checksum intent around stock EF migrations.</summary>
public static class PostgresSchema
{
    private const long MigrationLock = 90695025114;
    private sealed record Checkpoint(string Hash, bool Completed);

    public static async Task MigrateAsync(VivariumDbContext database, CancellationToken cancellationToken = default)
    {
        var connection = database.Database.GetDbConnection();
        var opened = connection.State != ConnectionState.Open;
        if (opened) await database.Database.OpenConnectionAsync(cancellationToken);
        var locked = false;
        try
        {
            await database.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_lock({MigrationLock})", cancellationToken);
            locked = true;
            var manifest = Manifest(database);
            var applied = (await database.Database.GetAppliedMigrationsAsync(cancellationToken)).ToArray();
            RequireKnownPrefix(manifest.Keys.ToArray(), applied);
            var checkpoints = await ReadCheckpointsAsync(database, cancellationToken);
            Validate(manifest, applied, checkpoints, requireComplete: false);
            await database.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS "__VivariumMigrations" (
                    "Id" text PRIMARY KEY, "Sha256" text NOT NULL, "Completed" boolean NOT NULL)
                """, cancellationToken);
            // Intent precedes EF's own transaction. Interrupted application can resume only with
            // the same bytes; an applied migration without its earlier intent is never adopted.
            foreach (var (id, hash) in manifest)
                await database.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO "__VivariumMigrations" ("Id", "Sha256", "Completed")
                    VALUES ({id}, {hash}, false) ON CONFLICT ("Id") DO NOTHING
                    """, cancellationToken);
            await database.Database.MigrateAsync(cancellationToken);
            foreach (var (id, hash) in manifest)
                await database.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE "__VivariumMigrations" SET "Completed" = true WHERE "Id" = {id} AND "Sha256" = {hash}
                    """, cancellationToken);
            await VerifyReadyAsync(database, cancellationToken);
        }
        finally
        {
            try
            {
                if (locked)
                    await database.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_unlock({MigrationLock})", CancellationToken.None);
            }
            finally { if (opened) await database.Database.CloseConnectionAsync(); }
        }
    }

    public static async Task VerifyReadyAsync(VivariumDbContext database, CancellationToken cancellationToken = default)
    {
        var manifest = Manifest(database);
        var applied = (await database.Database.GetAppliedMigrationsAsync(cancellationToken)).ToArray();
        RequireKnownPrefix(manifest.Keys.ToArray(), applied);
        if (applied.Length != manifest.Count)
            throw new InvalidDataException("PostgreSQL migrations are pending; run explicit local maintenance.");
        Validate(manifest, applied, await ReadCheckpointsAsync(database, cancellationToken), requireComplete: true);
    }

    private static Dictionary<string, string> Manifest(VivariumDbContext database)
    {
        if (database.Database.HasPendingModelChanges())
            throw new InvalidDataException("The PostgreSQL model has no matching compiled migration.");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var migrator = database.GetService<IMigrator>();
        var previous = Migration.InitialDatabase;
        foreach (var id in database.Database.GetMigrations())
        {
            var script = migrator.GenerateScript(previous, id).ReplaceLineEndings("\n");
            result.Add(id, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(script))));
            previous = id;
        }
        if (result.Count == 0) throw new InvalidDataException("The PostgreSQL migration manifest is empty.");
        return result;
    }

    private static void RequireKnownPrefix(string[] known, string[] applied)
    {
        if (!applied.SequenceEqual(known.Take(applied.Length), StringComparer.Ordinal))
            throw new InvalidDataException("PostgreSQL has an unknown or noncontiguous migration history.");
    }

    private static void Validate(Dictionary<string, string> manifest, string[] applied,
        Dictionary<string, Checkpoint> checkpoints, bool requireComplete)
    {
        foreach (var (id, checkpoint) in checkpoints)
            if (!manifest.TryGetValue(id, out var expected) || checkpoint.Hash != expected
                || checkpoint.Completed && !applied.Contains(id, StringComparer.Ordinal))
                throw new InvalidDataException("PostgreSQL migration checksum or completion history differs.");
        foreach (var id in applied)
            if (!checkpoints.TryGetValue(id, out var checkpoint) || requireComplete && !checkpoint.Completed)
                throw new InvalidDataException("An applied PostgreSQL migration lacks its completed checksum intent.");
    }

    private static async Task<Dictionary<string, Checkpoint>> ReadCheckpointsAsync(VivariumDbContext database,
        CancellationToken cancellationToken)
    {
        var connection = database.Database.GetDbConnection();
        var opened = connection.State != ConnectionState.Open;
        if (opened) await database.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var exists = connection.CreateCommand();
            exists.CommandText = "SELECT to_regclass('public.\"__VivariumMigrations\"') IS NOT NULL";
            if (!(bool)(await exists.ExecuteScalarAsync(cancellationToken))!) return new(StringComparer.Ordinal);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT \"Id\", \"Sha256\", \"Completed\" FROM \"__VivariumMigrations\" ORDER BY \"Id\"";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var result = new Dictionary<string, Checkpoint>(StringComparer.Ordinal);
            while (await reader.ReadAsync(cancellationToken))
                result.Add(reader.GetString(0), new(reader.GetString(1), reader.GetBoolean(2)));
            return result;
        }
        finally { if (opened) await database.Database.CloseConnectionAsync(); }
    }
}
