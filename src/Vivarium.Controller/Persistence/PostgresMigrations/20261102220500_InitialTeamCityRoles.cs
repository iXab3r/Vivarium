using System.Text.Json;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Vivarium.Controller.Persistence.PostgresMigrations;

/// <summary>One-time initial definitions, not assignments or a runtime permission floor.</summary>
[DbContext(typeof(VivariumDbContext))]
[Migration("20261102220500_InitialTeamCityRoles")]
public sealed class InitialTeamCityRoles : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        using var source = typeof(InitialTeamCityRoles).Assembly.GetManifestResourceStream(
            "Vivarium.Controller.Identity.Defaults.teamcity-2026.1.3-roles.json")
            ?? throw new InvalidDataException("The pinned initial role definitions are absent.");
        using var document = JsonDocument.Parse(source);
        var roles = document.RootElement.GetProperty("roles").EnumerateArray().ToArray();
        foreach (var role in roles)
            migrationBuilder.InsertData("Roles", ["Id", "Name"], ["text", "text"],
                [role.GetProperty("id").GetString()!, role.GetProperty("name").GetString()!]);
        foreach (var role in roles)
        {
            var id = role.GetProperty("id").GetString()!;
            foreach (var permission in role.GetProperty("permissions").EnumerateArray())
                migrationBuilder.InsertData("RolePermissions", ["RoleId", "PermissionId"], ["text", "text"], [id, permission.GetString()!]);
            foreach (var included in role.GetProperty("includedRoles").EnumerateArray())
                migrationBuilder.InsertData("IncludedRoles", ["RoleId", "IncludedRoleId"], ["text", "text"], [id, included.GetString()!]);
        }
        migrationBuilder.InsertData("Groups", ["Key", "Name"], ["text", "text"], ["ALL_USERS_GROUP", "All Users"]);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DELETE FROM "Roles" WHERE "Id" IN ('PROJECT_ADMIN','PROJECT_DEVELOPER','PROJECT_VIEWER','AGENT_MANAGER','SYSTEM_ADMIN');
            DELETE FROM "Groups" WHERE "Key" = 'ALL_USERS_GROUP';
            """);
    }
}
