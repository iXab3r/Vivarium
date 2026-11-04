using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vivarium.Controller.Persistence.PostgresMigrations
{
    /// <inheritdoc />
    public partial class RuntimeBuildProgress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RuntimeBuildObservations",
                columns: table => new
                {
                    BuildId = table.Column<long>(type: "bigint", nullable: false),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    Phase = table.Column<string>(type: "text", nullable: false),
                    StepIndex = table.Column<int>(type: "integer", nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExitCode = table.Column<int>(type: "integer", nullable: true),
                    TimedOut = table.Column<bool>(type: "boolean", nullable: true),
                    Skipped = table.Column<bool>(type: "boolean", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RuntimeBuildObservations", x => new { x.BuildId, x.Ordinal });
                    table.CheckConstraint("CK_RuntimeBuildObservations_Phase", "(\"Phase\" IN ('FETCHING','COLLECTING') AND \"StepIndex\" = -1) OR (\"Phase\" IN ('RUNNING','COMPLETED') AND \"StepIndex\" >= 0)");
                    table.CheckConstraint("CK_RuntimeBuildObservations_Position", "\"Ordinal\" BETWEEN 0 AND 257 AND \"StepIndex\" BETWEEN -1 AND 127");
                    table.CheckConstraint("CK_RuntimeBuildObservations_Result", "(\"Phase\" = 'COMPLETED' AND \"ExitCode\" IS NOT NULL AND \"TimedOut\" IS NOT NULL AND \"Skipped\" IS NOT NULL) OR (\"Phase\" <> 'COMPLETED' AND \"ExitCode\" IS NULL AND \"TimedOut\" IS NULL AND \"Skipped\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_RuntimeBuildObservations_RuntimeBuildExecutions_BuildId",
                        column: x => x.BuildId,
                        principalTable: "RuntimeBuildExecutions",
                        principalColumn: "BuildId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RuntimeBuildObservations_BuildId_Phase_StepIndex",
                table: "RuntimeBuildObservations",
                columns: new[] { "BuildId", "Phase", "StepIndex" },
                unique: true);
            migrationBuilder.Sql("""
                CREATE TRIGGER "RuntimeBuildObservations_Immutable" BEFORE UPDATE OR DELETE OR TRUNCATE
                    ON "RuntimeBuildObservations" FOR EACH STATEMENT EXECUTE FUNCTION public.vivarium_protect_build_evidence();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RuntimeBuildObservations");
        }
    }
}
