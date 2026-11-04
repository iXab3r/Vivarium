using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vivarium.Controller.Persistence.PostgresMigrations
{
    /// <inheritdoc />
    public partial class RuntimeBuildDependencies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_RuntimeBuilds_Id_BuildTypeId",
                table: "RuntimeBuilds",
                columns: new[] { "Id", "BuildTypeId" });

            migrationBuilder.CreateTable(
                name: "RuntimeBuildConsumedArtifacts",
                columns: table => new
                {
                    BuildId = table.Column<long>(type: "bigint", nullable: false),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    ProducerBuildId = table.Column<long>(type: "bigint", nullable: false),
                    ProducerArtifactOrdinal = table.Column<int>(type: "integer", nullable: false),
                    DestinationPath = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RuntimeBuildConsumedArtifacts", x => new { x.BuildId, x.Ordinal });
                    table.CheckConstraint("CK_RuntimeBuildConsumedArtifacts_Position", "\"Ordinal\" BETWEEN 0 AND 127 AND length(\"DestinationPath\") BETWEEN 1 AND 2048");
                    table.ForeignKey(
                        name: "FK_RuntimeBuildConsumedArtifacts_RuntimeBuildArtifacts_Produce~",
                        columns: x => new { x.ProducerBuildId, x.ProducerArtifactOrdinal },
                        principalTable: "RuntimeBuildArtifacts",
                        principalColumns: new[] { "BuildId", "Ordinal" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RuntimeBuildConsumedArtifacts_RuntimeBuilds_BuildId",
                        column: x => x.BuildId,
                        principalTable: "RuntimeBuilds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RuntimeBuildDependencies",
                columns: table => new
                {
                    BuildId = table.Column<long>(type: "bigint", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    UpstreamBuildTypeId = table.Column<long>(type: "bigint", nullable: false),
                    UpstreamBuildId = table.Column<long>(type: "bigint", nullable: false),
                    SelectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RuntimeBuildDependencies", x => new { x.BuildId, x.Kind, x.UpstreamBuildTypeId });
                    table.CheckConstraint("CK_RuntimeBuildDependencies_Kind", "\"Kind\" IN ('SNAPSHOT','ARTIFACT')");
                    table.CheckConstraint("CK_RuntimeBuildDependencies_NotSelf", "\"BuildId\" <> \"UpstreamBuildId\"");
                    table.ForeignKey(
                        name: "FK_RuntimeBuildDependencies_RuntimeBuilds_BuildId",
                        column: x => x.BuildId,
                        principalTable: "RuntimeBuilds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RuntimeBuildDependencies_RuntimeBuilds_UpstreamBuildId_Upst~",
                        columns: x => new { x.UpstreamBuildId, x.UpstreamBuildTypeId },
                        principalTable: "RuntimeBuilds",
                        principalColumns: new[] { "Id", "BuildTypeId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RuntimeBuildConsumedArtifacts_BuildId_DestinationPath",
                table: "RuntimeBuildConsumedArtifacts",
                columns: new[] { "BuildId", "DestinationPath" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RuntimeBuildConsumedArtifacts_ProducerBuildId_ProducerArtif~",
                table: "RuntimeBuildConsumedArtifacts",
                columns: new[] { "ProducerBuildId", "ProducerArtifactOrdinal" });

            migrationBuilder.CreateIndex(
                name: "IX_RuntimeBuildDependencies_UpstreamBuildId_UpstreamBuildTypeId",
                table: "RuntimeBuildDependencies",
                columns: new[] { "UpstreamBuildId", "UpstreamBuildTypeId" });
            migrationBuilder.Sql("""
                CREATE TRIGGER "RuntimeBuildDependencies_Immutable" BEFORE UPDATE OR DELETE OR TRUNCATE
                    ON "RuntimeBuildDependencies" FOR EACH STATEMENT EXECUTE FUNCTION public.vivarium_protect_build_evidence();
                CREATE TRIGGER "RuntimeBuildConsumedArtifacts_Immutable" BEFORE UPDATE OR DELETE OR TRUNCATE
                    ON "RuntimeBuildConsumedArtifacts" FOR EACH STATEMENT EXECUTE FUNCTION public.vivarium_protect_build_evidence();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RuntimeBuildConsumedArtifacts");

            migrationBuilder.DropTable(
                name: "RuntimeBuildDependencies");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_RuntimeBuilds_Id_BuildTypeId",
                table: "RuntimeBuilds");
        }
    }
}
