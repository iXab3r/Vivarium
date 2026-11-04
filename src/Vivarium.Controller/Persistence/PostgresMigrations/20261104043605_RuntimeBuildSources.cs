using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Vivarium.Controller.Persistence.PostgresMigrations
{
    /// <inheritdoc />
    public partial class RuntimeBuildSources : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SourceJson",
                table: "RuntimeBuildInputs",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "VcsRootInstanceId",
                table: "RuntimeBuildInputs",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "RuntimeBuildSourceReceipts",
                columns: table => new
                {
                    BuildId = table.Column<long>(type: "bigint", nullable: false),
                    Evidence = table.Column<byte[]>(type: "bytea", nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RuntimeBuildSourceReceipts", x => x.BuildId);
                    table.ForeignKey(
                        name: "FK_RuntimeBuildSourceReceipts_RuntimeBuildExecutions_BuildId",
                        column: x => x.BuildId,
                        principalTable: "RuntimeBuildExecutions",
                        principalColumn: "BuildId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RuntimeBuildSourceSelections",
                columns: table => new
                {
                    BuildId = table.Column<long>(type: "bigint", nullable: false),
                    Version = table.Column<string>(type: "text", nullable: false),
                    SelectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RuntimeBuildSourceSelections", x => x.BuildId);
                    table.CheckConstraint("CK_RuntimeBuildSourceSelections_Version", "\"Version\" = 'N/A' OR \"Version\" ~ '^[a-f0-9]{40}$'");
                    table.ForeignKey(
                        name: "FK_RuntimeBuildSourceSelections_RuntimeBuildInputs_BuildId",
                        column: x => x.BuildId,
                        principalTable: "RuntimeBuildInputs",
                        principalColumn: "BuildId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RuntimeVcsRootInstances",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    ProjectId = table.Column<long>(type: "bigint", nullable: false),
                    RootId = table.Column<string>(type: "text", nullable: false),
                    PropertiesHash = table.Column<string>(type: "text", nullable: false),
                    PropertiesJson = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RuntimeVcsRootInstances", x => x.Id);
                    table.UniqueConstraint("AK_RuntimeVcsRootInstances_Id_ProjectId", x => new { x.Id, x.ProjectId });
                    table.CheckConstraint("CK_RuntimeVcsRootInstances_Identity", "\"Id\" > 0 AND length(\"RootId\") BETWEEN 1 AND 256 AND \"PropertiesHash\" ~ '^[a-f0-9]{64}$'");
                    table.ForeignKey(
                        name: "FK_RuntimeVcsRootInstances_CatalogProjects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "CatalogProjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RuntimeBuildInputs_VcsRootInstanceId_ProjectId",
                table: "RuntimeBuildInputs",
                columns: new[] { "VcsRootInstanceId", "ProjectId" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_RuntimeBuildInputs_Source",
                table: "RuntimeBuildInputs",
                sql: "(\"VcsRootInstanceId\" IS NULL) = (\"SourceJson\" IS NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_RuntimeVcsRootInstances_ProjectId_RootId_PropertiesHash",
                table: "RuntimeVcsRootInstances",
                columns: new[] { "ProjectId", "RootId", "PropertiesHash" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_RuntimeBuildInputs_RuntimeVcsRootInstances_VcsRootInstanceI~",
                table: "RuntimeBuildInputs",
                columns: new[] { "VcsRootInstanceId", "ProjectId" },
                principalTable: "RuntimeVcsRootInstances",
                principalColumns: new[] { "Id", "ProjectId" },
                onDelete: ReferentialAction.Restrict);
            migrationBuilder.Sql("""
                CREATE TRIGGER "RuntimeVcsRootInstances_Immutable" BEFORE UPDATE OR DELETE OR TRUNCATE
                    ON "RuntimeVcsRootInstances" FOR EACH STATEMENT EXECUTE FUNCTION public.vivarium_protect_build_evidence();
                CREATE TRIGGER "RuntimeBuildSourceSelections_Immutable" BEFORE UPDATE OR DELETE OR TRUNCATE
                    ON "RuntimeBuildSourceSelections" FOR EACH STATEMENT EXECUTE FUNCTION public.vivarium_protect_build_evidence();
                CREATE TRIGGER "RuntimeBuildSourceReceipts_Immutable" BEFORE UPDATE OR DELETE OR TRUNCATE
                    ON "RuntimeBuildSourceReceipts" FOR EACH STATEMENT EXECUTE FUNCTION public.vivarium_protect_build_evidence();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_RuntimeBuildInputs_RuntimeVcsRootInstances_VcsRootInstanceI~",
                table: "RuntimeBuildInputs");

            migrationBuilder.DropTable(
                name: "RuntimeBuildSourceReceipts");

            migrationBuilder.DropTable(
                name: "RuntimeBuildSourceSelections");

            migrationBuilder.DropTable(
                name: "RuntimeVcsRootInstances");

            migrationBuilder.DropIndex(
                name: "IX_RuntimeBuildInputs_VcsRootInstanceId_ProjectId",
                table: "RuntimeBuildInputs");

            migrationBuilder.DropCheckConstraint(
                name: "CK_RuntimeBuildInputs_Source",
                table: "RuntimeBuildInputs");

            migrationBuilder.DropColumn(
                name: "SourceJson",
                table: "RuntimeBuildInputs");

            migrationBuilder.DropColumn(
                name: "VcsRootInstanceId",
                table: "RuntimeBuildInputs");
        }
    }
}
