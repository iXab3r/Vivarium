using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vivarium.Controller.Persistence.PostgresMigrations
{
    /// <inheritdoc />
    public partial class DeferredBuildCounter : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_RuntimeBuilds_Id",
                table: "RuntimeBuilds");

            migrationBuilder.AlterColumn<string>(
                name: "Number",
                table: "RuntimeBuilds",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<long>(
                name: "Counter",
                table: "RuntimeBuilds",
                type: "bigint",
                nullable: true,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.AddCheckConstraint(
                name: "CK_RuntimeBuilds_Id",
                table: "RuntimeBuilds",
                sql: "\"Id\" > 0 AND (\"Counter\" IS NULL OR \"Counter\" > 0)");
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION public.vivarium_protect_build_origin() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF ROW(NEW."Id", NEW."BuildTypeId", NEW."ProjectId", NEW."ActorId", NEW."CredentialKind", NEW."CorrelationId", NEW."QueuedAt", NEW."QueueDeadline")
                        IS DISTINCT FROM ROW(OLD."Id", OLD."BuildTypeId", OLD."ProjectId", OLD."ActorId", OLD."CredentialKind", OLD."CorrelationId", OLD."QueuedAt", OLD."QueueDeadline")
                        OR (OLD."Counter" IS NOT NULL AND NEW."Counter" IS DISTINCT FROM OLD."Counter")
                    THEN RAISE EXCEPTION 'Build origin and allocated counter are immutable' USING ERRCODE = '23514'; END IF;
                    RETURN NEW;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$ BEGIN IF EXISTS (SELECT 1 FROM "RuntimeBuilds" WHERE "Counter" IS NULL OR "Number" IS NULL)
                    THEN RAISE EXCEPTION 'Cannot roll back queued Build numbering without rewriting retained history' USING ERRCODE = '23514'; END IF; END $$;
                CREATE OR REPLACE FUNCTION public.vivarium_protect_build_origin() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF ROW(NEW."Id", NEW."BuildTypeId", NEW."ProjectId", NEW."Counter", NEW."ActorId", NEW."CredentialKind", NEW."CorrelationId", NEW."QueuedAt", NEW."QueueDeadline")
                        IS DISTINCT FROM ROW(OLD."Id", OLD."BuildTypeId", OLD."ProjectId", OLD."Counter", OLD."ActorId", OLD."CredentialKind", OLD."CorrelationId", OLD."QueuedAt", OLD."QueueDeadline")
                    THEN RAISE EXCEPTION 'Build origin is immutable' USING ERRCODE = '23514'; END IF;
                    RETURN NEW;
                END $$;
                """);
            migrationBuilder.DropCheckConstraint(
                name: "CK_RuntimeBuilds_Id",
                table: "RuntimeBuilds");

            migrationBuilder.AlterColumn<string>(
                name: "Number",
                table: "RuntimeBuilds",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<long>(
                name: "Counter",
                table: "RuntimeBuilds",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_RuntimeBuilds_Id",
                table: "RuntimeBuilds",
                sql: "\"Id\" > 0 AND \"Counter\" > 0");
        }
    }
}
