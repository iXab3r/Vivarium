using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vivarium.Controller.Identity;
using Vivarium.Controller.Persistence;

namespace Vivarium.DefinitionProof;

// Actual fresh-install PostgreSQL/framework boundary, separate from the P0 feasibility studies.
[NonParallelizable]
public sealed class HostPersistencePhysicalTests
{
    private static ServiceProvider Open(string connectionString)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<VivariumDbContext>(options => options.UseNpgsql(connectionString));
        services.AddIdentityCore<VivariumUser>().AddEntityFrameworkStores<VivariumDbContext>();
        return services.BuildServiceProvider(validateScopes: true);
    }

    [Test]
    public async Task Fresh_framework_accounts_keep_one_numeric_facet_and_external_binding_after_cold_reopen()
    {
        await using var postgres = new OwnedPostgres();
        await postgres.StartAsync();
        var accountId = Guid.NewGuid();
        long numericId;
        await using (var provider = Open(postgres.ConnectionString))
        await using (var scope = provider.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<VivariumDbContext>();
            await PostgresSchema.MigrateAsync(database);
            var users = scope.ServiceProvider.GetRequiredService<UserManager<VivariumUser>>();
            var user = new VivariumUser { Id = accountId, UserName = "owned-r0-user", DisplayName = "Owned R0 user" };
            Assert.That((await users.CreateAsync(user, "Owned-fixture-password-1!")).Succeeded, Is.True);
            Assert.That((await users.AddLoginAsync(user,
                new UserLoginInfo("https://issuer.example.invalid/", "opaque-subject", "Owned issuer"))).Succeeded, Is.True);
            numericId = user.TeamCityId;
            Assert.That(numericId, Is.Positive);
            Assert.That(user.PasswordHash, Is.Not.Null.And.Not.EqualTo("Owned-fixture-password-1!"));
        }
        await using (var provider = Open(postgres.ConnectionString))
        await using (var scope = provider.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<VivariumUser>>();
            var retained = await users.FindByLoginAsync("https://issuer.example.invalid/", "opaque-subject");
            Assert.That(retained, Is.Not.Null);
            Assert.Multiple(() =>
            {
                Assert.That(retained!.Id, Is.EqualTo(accountId));
                Assert.That(retained.TeamCityId, Is.EqualTo(numericId));
                Assert.That(retained.IsEnabled, Is.True);
            });
            Assert.That(await users.CheckPasswordAsync(retained!, "Owned-fixture-password-1!"), Is.True);
            Assert.That(await users.FindByLoginAsync("https://other.example.invalid/", "opaque-subject"), Is.Null);
        }
        var allocated = await Task.WhenAll(Enumerable.Range(0, 8).Select(async index =>
        {
            await using var provider = Open(postgres.ConnectionString);
            await using var scope = provider.CreateAsyncScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<VivariumUser>>();
            var user = new VivariumUser { Id = Guid.NewGuid(), UserName = "owned-concurrent-" + index };
            Assert.That((await users.CreateAsync(user)).Succeeded, Is.True);
            return user.TeamCityId;
        }));
        Assert.Multiple(() =>
        {
            Assert.That(allocated.Distinct().Count(), Is.EqualTo(8));
            Assert.That(allocated, Has.All.Positive);
            Assert.That(allocated, Does.Not.Contain(numericId));
        });
    }

    [TestCase("checksum")]
    [TestCase("unknown-version")]
    [TestCase("missing-intent")]
    public async Task Migration_drift_is_refused_before_schema_or_account_mutation(string fault)
    {
        await using var postgres = new OwnedPostgres();
        await postgres.StartAsync();
        await using var provider = Open(postgres.ConnectionString);
        await using var scope = provider.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<VivariumDbContext>();
        await PostgresSchema.MigrateAsync(database);
        await database.Database.ExecuteSqlRawAsync(fault switch
        {
            "checksum" => "UPDATE \"__VivariumMigrations\" SET \"Sha256\" = 'changed'",
            "unknown-version" => "INSERT INTO \"__EFMigrationsHistory\" VALUES ('99999999999999_Future', '99.0.0')",
            "missing-intent" => "DELETE FROM \"__VivariumMigrations\"",
            _ => throw new ArgumentOutOfRangeException(nameof(fault)),
        });
        Assert.ThrowsAsync<InvalidDataException>(() => PostgresSchema.VerifyReadyAsync(database));
        Assert.ThrowsAsync<InvalidDataException>(() => PostgresSchema.MigrateAsync(database));
        Assert.That(await database.Users.CountAsync(), Is.Zero);
        // A second cold read must still see the corruption, rather than a repair/reset fallback.
        await using var coldProvider = Open(postgres.ConnectionString);
        await using var coldScope = coldProvider.CreateAsyncScope();
        Assert.ThrowsAsync<InvalidDataException>(() => PostgresSchema.VerifyReadyAsync(
            coldScope.ServiceProvider.GetRequiredService<VivariumDbContext>()));
    }

    [Test]
    public async Task Committed_EF_migration_with_interrupted_completion_resumes_the_same_checksum_intent()
    {
        await using var postgres = new OwnedPostgres();
        await postgres.StartAsync();
        await using var provider = Open(postgres.ConnectionString);
        await using var scope = provider.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<VivariumDbContext>();
        await PostgresSchema.MigrateAsync(database);
        var migrationIds = (await database.Database.GetAppliedMigrationsAsync()).ToArray();
        await database.Database.ExecuteSqlRawAsync("UPDATE \"__VivariumMigrations\" SET \"Completed\" = false");
        Assert.ThrowsAsync<InvalidDataException>(() => PostgresSchema.VerifyReadyAsync(database));
        await PostgresSchema.MigrateAsync(database);
        await PostgresSchema.VerifyReadyAsync(database);
        Assert.That(await database.Database.GetAppliedMigrationsAsync(), Is.EqualTo(migrationIds));
    }
}
