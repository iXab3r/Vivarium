using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Vivarium.Controller.Identity;
using Vivarium.Controller.Persistence;

namespace Vivarium.DefinitionProof;

// Model evidence only: these arrangements do not prove an authorized HTTP/UI grant mutation.
[NonParallelizable]
public sealed class CurrentAccessPhysicalTests
{
    [Test]
    public async Task Current_role_definitions_scopes_inclusions_and_eligibility_replace_stale_tracked_data()
    {
        await using var postgres = new OwnedPostgres();
        await postgres.StartAsync();
        VivariumDbContext Open() => new(new DbContextOptionsBuilder<VivariumDbContext>()
            .UseNpgsql(postgres.ConnectionString).Options);
        var userId = Guid.NewGuid();
        var configuration = new ConfigurationBuilder().Build();
        await using var reader = Open();
        await PostgresSchema.MigrateAsync(reader);
        reader.Users.Add(new VivariumUser { Id = userId, UserName = "owned-current-user" });
        reader.Roles.AddRange(new TeamCityRole { Id = "SYSTEM_ADMIN", Name = "System administrator" },
            new TeamCityRole { Id = "RUNNER", Name = "Runner" },
            new TeamCityRole { Id = "VIEWER", Name = "Viewer" });
        reader.Groups.Add(new TeamCityGroup { Key = TeamCityGroup.AllUsersKey, Name = "All Users" });
        reader.RolePermissions.AddRange(new RolePermission { RoleId = "SYSTEM_ADMIN", PermissionId = "manage_roles" },
            new RolePermission { RoleId = "RUNNER", PermissionId = "run_build" },
            new RolePermission { RoleId = "VIEWER", PermissionId = "view_project" });
        reader.IncludedRoles.Add(new IncludedRole { RoleId = "RUNNER", IncludedRoleId = "VIEWER" });
        reader.UserRoleGrants.Add(new UserRoleGrant { UserId = userId, RoleId = "RUNNER", Scope = "p:P" });
        await reader.SaveChangesAsync();
        var access = new CurrentAccess(reader, configuration);
        Assert.That(await access.AllowsAsync(userId, "run_build", ["P.Child", "P", "_Root"]), Is.True);
        Assert.That(await access.AllowsAsync(userId, "view_project", ["P.Child", "P", "_Root"]), Is.True);
        Assert.That(await access.AllowsAsync(userId, "run_build", ["Sibling", "_Root"]), Is.False);
        Assert.That(await access.AllowsAsync(userId, "run_build"), Is.False);

        await using (var writer = Open())
        {
            writer.GroupRoleGrants.Add(new GroupRoleGrant
                { GroupKey = TeamCityGroup.AllUsersKey, RoleId = "VIEWER", Scope = "g" });
            writer.UserRoleGrants.Add(new UserRoleGrant { UserId = userId, RoleId = "SYSTEM_ADMIN", Scope = "g" });
            await writer.SaveChangesAsync();
        }
        Assert.That(await access.AllowsAsync(userId, "view_project", ["Sibling", "_Root"]), Is.True);
        Assert.That(await access.AllowsAsync(userId, "manage_roles"), Is.True);
        Assert.That(await access.AllowsAsync(userId, "invented_permission"), Is.False);
        await using (var writer = Open())
        {
            await writer.RolePermissions.Where(permission => permission.RoleId == "SYSTEM_ADMIN").ExecuteDeleteAsync();
            await writer.IncludedRoles.ExecuteDeleteAsync();
            await writer.GroupRoleGrants.ExecuteDeleteAsync();
        }
        Assert.That(await access.AllowsAsync(userId, "manage_roles"), Is.False, "No immutable SYSTEM_ADMIN floor.");
        Assert.That(await access.AllowsAsync(userId, "view_project", ["P", "_Root"]), Is.False);
        Assert.That(await access.AllowsAsync(userId, "run_build", ["P", "_Root"]), Is.True);
        await using (var writer = Open())
            await writer.Users.Where(user => user.Id == userId).ExecuteUpdateAsync(set => set.SetProperty(user => user.IsEnabled, false));
        Assert.That(await access.FindEligibleAsync(userId), Is.Null);
        Assert.That(await access.AllowsAsync(userId, "run_build", ["P", "_Root"]), Is.False);
        Assert.That(reader.Users.Local.Single().IsEnabled, Is.True, "The reader still holds stale tracked state.");
        await using (var writer = Open())
        {
            await writer.Users.Where(user => user.Id == userId).ExecuteUpdateAsync(set => set.SetProperty(user => user.IsEnabled, true));
            await writer.UserRoleGrants.ExecuteDeleteAsync();
        }
        Assert.That(await access.FindEligibleAsync(userId), Is.Not.Null);
        Assert.That(await access.AllowsAsync(userId, "run_build", ["P", "_Root"]), Is.False);
    }

    [Test]
    public async Task Only_the_enabled_reserved_installation_account_has_configuration_controlled_maximal_access()
    {
        await using var postgres = new OwnedPostgres();
        await postgres.StartAsync();
        await using var database = new VivariumDbContext(new DbContextOptionsBuilder<VivariumDbContext>()
            .UseNpgsql(postgres.ConnectionString).Options);
        await PostgresSchema.MigrateAsync(database);
        database.Users.Add(new VivariumUser { Id = VivariumIdentityConstants.SuperuserId, UserName = "superuser" });
        var ordinaryId = Guid.NewGuid();
        database.Users.Add(new VivariumUser { Id = ordinaryId, UserName = "owned-name-lookalike", DisplayName = "superuser" });
        await database.SaveChangesAsync();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Vivarium:Identity:SuperuserEnabled"] = "true" }).Build();
        var access = new CurrentAccess(database, configuration);
        Assert.That(await access.AllowsAsync(VivariumIdentityConstants.SuperuserId, "manage_roles"), Is.True);
        Assert.That(await access.AllowsAsync(ordinaryId, "manage_roles"), Is.False);
        configuration["Vivarium:Identity:SuperuserEnabled"] = "false";
        Assert.That(await access.FindEligibleAsync(VivariumIdentityConstants.SuperuserId), Is.Null);
        Assert.That(await access.AllowsAsync(VivariumIdentityConstants.SuperuserId, "manage_roles"), Is.False);
        configuration["Vivarium:Identity:SuperuserEnabled"] = "true";
        await database.Users.Where(user => user.Id == VivariumIdentityConstants.SuperuserId)
            .ExecuteUpdateAsync(set => set.SetProperty(user => user.IsEnabled, false));
        Assert.That(await access.AllowsAsync(VivariumIdentityConstants.SuperuserId, "manage_roles"), Is.False);
    }
}
