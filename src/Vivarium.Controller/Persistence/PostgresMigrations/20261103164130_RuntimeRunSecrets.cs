using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vivarium.Controller.Persistence.PostgresMigrations
{
    /// <inheritdoc />
    public partial class RuntimeRunSecrets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "ProtectedEnvironment",
                table: "RuntimeBuildInputs",
                type: "bytea",
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.AddColumn<int>(
                name: "AssignmentProtectionVersion",
                table: "RuntimeBuildExecutions",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProtectedEnvironment",
                table: "RuntimeBuildInputs");

            migrationBuilder.DropColumn(
                name: "AssignmentProtectionVersion",
                table: "RuntimeBuildExecutions");
        }
    }
}
