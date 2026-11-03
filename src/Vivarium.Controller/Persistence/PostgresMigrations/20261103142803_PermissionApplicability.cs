using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

using System.Text.Json;

namespace Vivarium.Controller.Persistence.PostgresMigrations
{
    /// <inheritdoc />
    public partial class PermissionApplicability : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PermissionDefinitions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    Name = table.Column<string>(type: "text", nullable: false),
                    IsGlobal = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PermissionDefinitions", x => x.Id);
                });
            using var source = typeof(PermissionApplicability).Assembly.GetManifestResourceStream(
                "Vivarium.Controller.Identity.Defaults.teamcity-2026.1.3-permissions.json")
                ?? throw new InvalidDataException("The pinned permission applicability is absent.");
            using var document = JsonDocument.Parse(source);
            foreach (var permission in document.RootElement.GetProperty("permissions").EnumerateArray())
                migrationBuilder.InsertData("PermissionDefinitions", ["Id", "Name", "IsGlobal"], ["text", "text", "boolean"],
                    [permission.GetProperty("id").GetString()!, permission.GetProperty("name").GetString()!, permission.GetProperty("global").GetBoolean()]);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PermissionDefinitions");
        }
    }
}
