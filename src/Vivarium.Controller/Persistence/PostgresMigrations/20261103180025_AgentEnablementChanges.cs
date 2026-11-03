using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vivarium.Controller.Persistence.PostgresMigrations
{
    /// <inheritdoc />
    public partial class AgentEnablementChanges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "CatalogChanges",
                type: "text",
                nullable: false,
                defaultValue: "RECONCILE");

            migrationBuilder.AddColumn<string>(
                name: "TargetAgentId",
                table: "CatalogChanges",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EnabledChangedAt",
                table: "AgentRegistrations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EnabledComment",
                table: "AgentRegistrations",
                type: "text",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_CatalogChanges_Kind",
                table: "CatalogChanges",
                sql: "(\"Kind\" = 'RECONCILE' AND \"TargetAgentId\" IS NULL) OR (\"Kind\" = 'AGENT_ENABLEMENT' AND \"TargetAgentId\" IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_CatalogChanges_Kind",
                table: "CatalogChanges");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "CatalogChanges");

            migrationBuilder.DropColumn(
                name: "TargetAgentId",
                table: "CatalogChanges");

            migrationBuilder.DropColumn(
                name: "EnabledChangedAt",
                table: "AgentRegistrations");

            migrationBuilder.DropColumn(
                name: "EnabledComment",
                table: "AgentRegistrations");
        }
    }
}
