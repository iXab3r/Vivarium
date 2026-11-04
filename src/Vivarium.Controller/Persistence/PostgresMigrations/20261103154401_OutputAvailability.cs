using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vivarium.Controller.Persistence.PostgresMigrations
{
    /// <inheritdoc />
    public partial class OutputAvailability : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "RetentionEpoch",
                table: "RuntimeBuildOutputs",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "RuntimeOutputExpiries",
                columns: table => new
                {
                    RangeId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExpiredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RuntimeOutputExpiries", x => x.RangeId);
                    table.CheckConstraint("CK_RuntimeOutputExpiries_Deletion", "\"DeletedAt\" IS NULL OR \"DeletedAt\" >= \"ExpiredAt\"");
                    table.ForeignKey(
                        name: "FK_RuntimeOutputExpiries_RuntimeOutputRanges_RangeId",
                        column: x => x.RangeId,
                        principalTable: "RuntimeOutputRanges",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_RuntimeBuildOutputs_Retention",
                table: "RuntimeBuildOutputs",
                sql: "\"RetentionEpoch\" >= 0");

            migrationBuilder.CreateIndex(
                name: "IX_RuntimeOutputExpiries_DeletedAt",
                table: "RuntimeOutputExpiries",
                column: "DeletedAt",
                filter: "\"DeletedAt\" IS NULL");
            migrationBuilder.Sql("""
                CREATE FUNCTION public.vivarium_protect_output_expiry() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW."RangeId" IS DISTINCT FROM OLD."RangeId" OR NEW."ExpiredAt" IS DISTINCT FROM OLD."ExpiredAt"
                        OR (OLD."DeletedAt" IS NOT NULL AND NEW."DeletedAt" IS DISTINCT FROM OLD."DeletedAt")
                    THEN RAISE EXCEPTION 'Output expiry origin is immutable' USING ERRCODE = '23514'; END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER "RuntimeOutputExpiries_Origin" BEFORE UPDATE ON "RuntimeOutputExpiries"
                    FOR EACH ROW EXECUTE FUNCTION public.vivarium_protect_output_expiry();
                CREATE TRIGGER "RuntimeOutputExpiries_NoDelete" BEFORE DELETE OR TRUNCATE ON "RuntimeOutputExpiries"
                    FOR EACH STATEMENT EXECUTE FUNCTION public.vivarium_protect_build_evidence();
                CREATE FUNCTION public.vivarium_protect_output_epoch() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW."RetentionEpoch" < OLD."RetentionEpoch"
                    THEN RAISE EXCEPTION 'Output availability epoch cannot regress' USING ERRCODE = '23514'; END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER "RuntimeBuildOutputs_Epoch" BEFORE UPDATE ON "RuntimeBuildOutputs"
                    FOR EACH ROW EXECUTE FUNCTION public.vivarium_protect_output_epoch();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER "RuntimeBuildOutputs_Epoch" ON "RuntimeBuildOutputs";
                DROP FUNCTION public.vivarium_protect_output_epoch();
                """);
            migrationBuilder.DropTable(
                name: "RuntimeOutputExpiries");
            migrationBuilder.Sql("DROP FUNCTION public.vivarium_protect_output_expiry();");

            migrationBuilder.DropCheckConstraint(
                name: "CK_RuntimeBuildOutputs_Retention",
                table: "RuntimeBuildOutputs");

            migrationBuilder.DropColumn(
                name: "RetentionEpoch",
                table: "RuntimeBuildOutputs");
        }
    }
}
