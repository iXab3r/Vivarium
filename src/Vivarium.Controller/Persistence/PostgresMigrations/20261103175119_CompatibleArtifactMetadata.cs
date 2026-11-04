using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vivarium.Controller.Persistence.PostgresMigrations
{
    /// <inheritdoc />
    public partial class CompatibleArtifactMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CancelActorId",
                table: "RuntimeBuilds",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ModifiedAt",
                table: "RuntimeBuildArtifacts",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CancelActorId",
                table: "RuntimeBuilds");

            migrationBuilder.DropColumn(
                name: "ModifiedAt",
                table: "RuntimeBuildArtifacts");
        }
    }
}
