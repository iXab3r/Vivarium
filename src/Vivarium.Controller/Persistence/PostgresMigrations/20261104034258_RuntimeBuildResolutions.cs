using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vivarium.Controller.Persistence.PostgresMigrations
{
    /// <inheritdoc />
    public partial class RuntimeBuildResolutions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RuntimeBuildResolutions",
                columns: table => new
                {
                    RequestedBuildId = table.Column<long>(type: "bigint", nullable: false),
                    ResolvedBuildId = table.Column<long>(type: "bigint", nullable: false),
                    BuildTypeId = table.Column<long>(type: "bigint", nullable: false),
                    ProjectId = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RuntimeBuildResolutions", x => x.RequestedBuildId);
                    table.CheckConstraint("CK_RuntimeBuildResolutions_NotSelf", "\"RequestedBuildId\" <> \"ResolvedBuildId\" AND \"UpdatedAt\" >= \"CreatedAt\"");
                    table.ForeignKey(
                        name: "FK_RuntimeBuildResolutions_RuntimeBuilds_RequestedBuildId_Buil~",
                        columns: x => new { x.RequestedBuildId, x.BuildTypeId, x.ProjectId },
                        principalTable: "RuntimeBuilds",
                        principalColumns: new[] { "Id", "BuildTypeId", "ProjectId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RuntimeBuildResolutions_RuntimeBuilds_ResolvedBuildId_Build~",
                        columns: x => new { x.ResolvedBuildId, x.BuildTypeId, x.ProjectId },
                        principalTable: "RuntimeBuilds",
                        principalColumns: new[] { "Id", "BuildTypeId", "ProjectId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RuntimeBuildResolutions_RequestedBuildId_BuildTypeId_Projec~",
                table: "RuntimeBuildResolutions",
                columns: new[] { "RequestedBuildId", "BuildTypeId", "ProjectId" });

            migrationBuilder.CreateIndex(
                name: "IX_RuntimeBuildResolutions_ResolvedBuildId_BuildTypeId_Project~",
                table: "RuntimeBuildResolutions",
                columns: new[] { "ResolvedBuildId", "BuildTypeId", "ProjectId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RuntimeBuildResolutions");
        }
    }
}
