using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vivarium.Controller.Persistence.PostgresMigrations
{
    /// <inheritdoc />
    public partial class RuntimeBuildRunInputs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RuntimeBuildRunInputs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorId = table.Column<Guid>(type: "uuid", nullable: false),
                    BuildTypeId = table.Column<long>(type: "bigint", nullable: false),
                    ProjectId = table.Column<long>(type: "bigint", nullable: false),
                    RevisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    State = table.Column<string>(type: "text", nullable: false),
                    ProtectedInputs = table.Column<byte[]>(type: "bytea", nullable: false),
                    AcceptedBuildId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RuntimeBuildRunInputs", x => x.Id);
                    table.CheckConstraint("CK_RuntimeBuildRunInputs_Lifetime", "\"ExpiresAt\" > \"CreatedAt\"");
                    table.CheckConstraint("CK_RuntimeBuildRunInputs_State", "(\"State\" = 'PENDING' AND octet_length(\"ProtectedInputs\") > 0 AND \"AcceptedBuildId\" IS NULL) OR (\"State\" = 'CONSUMED' AND octet_length(\"ProtectedInputs\") = 0 AND \"AcceptedBuildId\" IS NOT NULL) OR (\"State\" = 'EXPIRED' AND octet_length(\"ProtectedInputs\") = 0 AND \"AcceptedBuildId\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_RuntimeBuildRunInputs_CatalogBuildTypes_BuildTypeId_Project~",
                        columns: x => new { x.BuildTypeId, x.ProjectId },
                        principalTable: "CatalogBuildTypes",
                        principalColumns: new[] { "Id", "ProjectId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RuntimeBuildRunInputs_CatalogRevisions_RevisionId",
                        column: x => x.RevisionId,
                        principalTable: "CatalogRevisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RuntimeBuildRunInputs_RuntimeBuilds_AcceptedBuildId_BuildTy~",
                        columns: x => new { x.AcceptedBuildId, x.BuildTypeId, x.ProjectId },
                        principalTable: "RuntimeBuilds",
                        principalColumns: new[] { "Id", "BuildTypeId", "ProjectId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RuntimeBuildRunInputs_AcceptedBuildId_BuildTypeId_ProjectId",
                table: "RuntimeBuildRunInputs",
                columns: new[] { "AcceptedBuildId", "BuildTypeId", "ProjectId" });

            migrationBuilder.CreateIndex(
                name: "IX_RuntimeBuildRunInputs_ActorId_State_ExpiresAt",
                table: "RuntimeBuildRunInputs",
                columns: new[] { "ActorId", "State", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_RuntimeBuildRunInputs_BuildTypeId_ProjectId",
                table: "RuntimeBuildRunInputs",
                columns: new[] { "BuildTypeId", "ProjectId" });

            migrationBuilder.CreateIndex(
                name: "IX_RuntimeBuildRunInputs_RevisionId",
                table: "RuntimeBuildRunInputs",
                column: "RevisionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RuntimeBuildRunInputs");
        }
    }
}
