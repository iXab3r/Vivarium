using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vivarium.Controller.Persistence.PostgresMigrations
{
    /// <inheritdoc />
    public partial class RuntimeVcsTriggers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RuntimeBuildVcsTriggers",
                columns: table => new
                {
                    BuildId = table.Column<long>(type: "bigint", nullable: false),
                    RootBuildId = table.Column<long>(type: "bigint", nullable: false),
                    EventId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RuntimeBuildVcsTriggers", x => x.BuildId);
                    table.CheckConstraint("CK_RuntimeBuildVcsTriggers_Version", "\"Version\" ~ '^[a-f0-9]{40}$'");
                    table.ForeignKey(
                        name: "FK_RuntimeBuildVcsTriggers_RuntimeBuilds_BuildId",
                        column: x => x.BuildId,
                        principalTable: "RuntimeBuilds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RuntimeBuildVcsTriggers_RuntimeBuilds_RootBuildId",
                        column: x => x.RootBuildId,
                        principalTable: "RuntimeBuilds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RuntimeVcsTriggerStates",
                columns: table => new
                {
                    BuildTypeId = table.Column<long>(type: "bigint", nullable: false),
                    RevisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProfileHash = table.Column<string>(type: "text", nullable: false),
                    ObservedVersion = table.Column<string>(type: "text", nullable: false),
                    PendingVersion = table.Column<string>(type: "text", nullable: true),
                    PendingId = table.Column<Guid>(type: "uuid", nullable: true),
                    DueAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ObservedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RuntimeVcsTriggerStates", x => x.BuildTypeId);
                    table.CheckConstraint("CK_RuntimeVcsTriggerStates_Pending", "(\"PendingVersion\" IS NULL AND \"PendingId\" IS NULL AND \"DueAt\" IS NULL) OR (\"PendingVersion\" IS NOT NULL AND \"PendingVersion\" ~ '^[a-f0-9]{40}$' AND \"PendingId\" IS NOT NULL AND \"DueAt\" IS NOT NULL)");
                    table.CheckConstraint("CK_RuntimeVcsTriggerStates_Profile", "\"ProfileHash\" ~ '^[a-f0-9]{64}$' AND \"ObservedVersion\" ~ '^[a-f0-9]{40}$'");
                    table.ForeignKey(
                        name: "FK_RuntimeVcsTriggerStates_CatalogBuildTypes_BuildTypeId",
                        column: x => x.BuildTypeId,
                        principalTable: "CatalogBuildTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RuntimeVcsTriggerStates_CatalogRevisions_RevisionId",
                        column: x => x.RevisionId,
                        principalTable: "CatalogRevisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RuntimeBuildVcsTriggers_EventId",
                table: "RuntimeBuildVcsTriggers",
                column: "EventId",
                unique: true,
                filter: "\"BuildId\" = \"RootBuildId\"");

            migrationBuilder.CreateIndex(
                name: "IX_RuntimeBuildVcsTriggers_RootBuildId_BuildId",
                table: "RuntimeBuildVcsTriggers",
                columns: new[] { "RootBuildId", "BuildId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RuntimeVcsTriggerStates_RevisionId",
                table: "RuntimeVcsTriggerStates",
                column: "RevisionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RuntimeBuildVcsTriggers");

            migrationBuilder.DropTable(
                name: "RuntimeVcsTriggerStates");
        }
    }
}
