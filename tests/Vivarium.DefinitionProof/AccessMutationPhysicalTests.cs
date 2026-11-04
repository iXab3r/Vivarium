using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Vivarium.Controller.Auditing;
using Vivarium.Controller.Identity;
using Vivarium.Controller.Persistence;
using Vivarium.Controller.Security;

namespace Vivarium.DefinitionProof;

// Common-service effects, not a claim of browser/REST grant entry or complete TeamCity role conformance.
[NonParallelizable]
public sealed class AccessMutationPhysicalTests
{
    [Test]
    public async Task Installation_operator_delegates_editable_administration_and_audit_failure_rolls_back_the_grant()
    {
        await using var postgres = new OwnedPostgres();
        await postgres.StartAsync();
        VivariumDbContext Open() => new(new DbContextOptionsBuilder<VivariumDbContext>()
            .UseNpgsql(postgres.ConnectionString).Options);
        var adminId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Vivarium:Identity:SuperuserEnabled"] = "true" }).Build();
        ManagementRequestContext Actor(Guid id) => new(new ManagementPrincipal("user", id.ToString(), "owned-component", null),
            ManagementIdentifiers.NewId(), null, "owned-r0-access");
        var installation = Actor(VivariumIdentityConstants.SuperuserId);
        await using (var database = Open())
        {
            await PostgresSchema.MigrateAsync(database);
            var initialRoleCount = await database.Roles.CountAsync();
            database.Users.AddRange(new VivariumUser { Id = VivariumIdentityConstants.SuperuserId, UserName = "superuser" },
                new VivariumUser { Id = adminId, UserName = "owned-admin" },
                new VivariumUser { Id = targetId, UserName = "owned-target" });
            await database.SaveChangesAsync();
            var access = new CurrentAccess(database, configuration);
            var service = new TeamCityAccessService(database, access, new PostgresAuditStore(database), TimeProvider.System);
            Assert.That(await service.SetRoleAsync(Actor(adminId), "SYSTEM_ADMIN", "System administrator", ["manage_roles", "change_user"]),
                Is.EqualTo(AccessMutationResult.Denied));
            Assert.That(await database.Roles.CountAsync(), Is.EqualTo(initialRoleCount));
            Assert.That(await service.SetRoleAsync(installation, "SYSTEM_ADMIN", "System administrator", ["manage_roles", "change_user"]),
                Is.EqualTo(AccessMutationResult.Applied));
            Assert.That(await service.SetGlobalUserRoleAsync(installation, adminId, "SYSTEM_ADMIN", true),
                Is.EqualTo(AccessMutationResult.Applied));
            Assert.That(await access.AllowsAsync(adminId, "manage_roles"), Is.True);
            await database.Database.ExecuteSqlRawAsync("""
                CREATE FUNCTION public.owned_reject_grant_audit() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN IF NEW."Action" = 'user.role.update' THEN
                    RAISE EXCEPTION 'Owned grant audit fault' USING ERRCODE = '23514'; END IF; RETURN NEW; END $$;
                CREATE TRIGGER "Owned_GrantAuditFault" BEFORE INSERT ON "AuditEvents"
                    FOR EACH ROW EXECUTE FUNCTION public.owned_reject_grant_audit();
                """);
        }
        await using (var database = Open())
        {
            var service = new TeamCityAccessService(database, new CurrentAccess(database, configuration),
                new PostgresAuditStore(database), TimeProvider.System);
            Assert.ThrowsAsync<DbUpdateException>(() => service.SetGlobalUserRoleAsync(Actor(adminId), targetId, "SYSTEM_ADMIN", true));
        }
        await using (var database = Open())
        {
            Assert.That(await database.UserRoleGrants.AnyAsync(grant => grant.UserId == targetId), Is.False);
            await database.Database.ExecuteSqlRawAsync("""
                DROP TRIGGER "Owned_GrantAuditFault" ON "AuditEvents";
                DROP FUNCTION public.owned_reject_grant_audit();
                """);
            var access = new CurrentAccess(database, configuration);
            var service = new TeamCityAccessService(database, access, new PostgresAuditStore(database), TimeProvider.System);
            Assert.That(await service.SetGlobalUserRoleAsync(Actor(adminId), targetId, "SYSTEM_ADMIN", true),
                Is.EqualTo(AccessMutationResult.Applied));
            Assert.That(await access.AllowsAsync(targetId, "manage_roles"), Is.True);
            Assert.That(await service.SetRoleAsync(Actor(adminId), "SYSTEM_ADMIN", "Edited administrator", []),
                Is.EqualTo(AccessMutationResult.Applied));
            Assert.That(await access.AllowsAsync(adminId, "manage_roles"), Is.False);
            Assert.That(await service.SetOrdinaryUserEnabledAsync(Actor(adminId), targetId, false),
                Is.EqualTo(AccessMutationResult.Denied));
            Assert.That(await service.SetOrdinaryUserEnabledAsync(installation, targetId, false),
                Is.EqualTo(AccessMutationResult.Applied));
            Assert.That(await access.FindEligibleAsync(targetId), Is.Null);
            var grants = await database.AuditEvents.AsNoTracking().Where(entry => entry.Action == "user.role.update").ToArrayAsync();
            Assert.That(grants, Has.Length.EqualTo(2));
            Assert.That(grants.Single(entry => entry.TargetId == targetId.ToString()).ActorId, Is.EqualTo(adminId.ToString()));
            Assert.That(await database.AuditEvents.CountAsync(entry => entry.Outcome == "DENIED"), Is.EqualTo(2));
        }
    }
}
