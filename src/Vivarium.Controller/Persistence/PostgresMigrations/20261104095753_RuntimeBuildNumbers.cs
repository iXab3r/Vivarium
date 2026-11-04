using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vivarium.Controller.Persistence.PostgresMigrations
{
    /// <inheritdoc />
    public partial class RuntimeBuildNumbers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RuntimeBuildNumberObservations",
                columns: table => new
                {
                    BuildId = table.Column<long>(type: "bigint", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    Value = table.Column<string>(type: "text", nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RuntimeBuildNumberObservations", x => new { x.BuildId, x.Sequence });
                    table.CheckConstraint("CK_RuntimeBuildNumberObservations_Bounds", "\"Sequence\" BETWEEN 1 AND 128 AND octet_length(\"Value\") BETWEEN 1 AND 4096");
                    table.ForeignKey(
                        name: "FK_RuntimeBuildNumberObservations_RuntimeBuilds_BuildId",
                        column: x => x.BuildId,
                        principalTable: "RuntimeBuilds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RuntimeBuildNumberObservations");
        }
    }
}
