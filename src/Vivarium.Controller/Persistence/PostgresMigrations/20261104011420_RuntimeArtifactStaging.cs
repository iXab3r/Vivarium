using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vivarium.Controller.Persistence.PostgresMigrations
{
    /// <inheritdoc />
    public partial class RuntimeArtifactStaging : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_RuntimeOutputDeletions_Key",
                table: "RuntimeOutputDeletions");

            migrationBuilder.AddCheckConstraint(
                name: "CK_RuntimeOutputDeletions_Key",
                table: "RuntimeOutputDeletions",
                sql: "\"ObjectKey\" ~ '^build-output/[a-f0-9]{32}$' OR \"ObjectKey\" ~ ('^build-artifacts/' || \"BuildId\"::text || '/[a-f0-9]{64}$')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_RuntimeOutputDeletions_Key",
                table: "RuntimeOutputDeletions");

            migrationBuilder.AddCheckConstraint(
                name: "CK_RuntimeOutputDeletions_Key",
                table: "RuntimeOutputDeletions",
                sql: "\"ObjectKey\" ~ '^build-output/[a-f0-9]{32}$'");
        }
    }
}
