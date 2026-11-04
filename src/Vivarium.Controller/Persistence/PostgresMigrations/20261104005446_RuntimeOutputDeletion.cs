using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vivarium.Controller.Persistence.PostgresMigrations
{
    /// <inheritdoc />
    public partial class RuntimeOutputDeletion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RuntimeOutputDeletions",
                columns: table => new
                {
                    ObjectKey = table.Column<string>(type: "text", nullable: false),
                    BuildId = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RuntimeOutputDeletions", x => x.ObjectKey);
                    table.CheckConstraint("CK_RuntimeOutputDeletions_Key", "\"ObjectKey\" ~ '^build-output/[a-f0-9]{32}$'");
                    table.ForeignKey(
                        name: "FK_RuntimeOutputDeletions_RuntimeBuilds_BuildId",
                        column: x => x.BuildId,
                        principalTable: "RuntimeBuilds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RuntimeOutputDeletions_BuildId",
                table: "RuntimeOutputDeletions",
                column: "BuildId");

            migrationBuilder.CreateIndex(
                name: "IX_RuntimeOutputDeletions_CreatedAt",
                table: "RuntimeOutputDeletions",
                column: "CreatedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RuntimeOutputDeletions");
        }
    }
}
