using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vivarium.Controller.Persistence.PostgresMigrations
{
    /// <inheritdoc />
    public partial class ApplicationAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuditEvents",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ActorType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ActorId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, collation: "C"),
                    CredentialKind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CorrelationId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    RequestId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Action = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    TargetType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TargetId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, collation: "C"),
                    Outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ReasonCode = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    DetailsJson = table.Column<string>(type: "jsonb", nullable: false),
                    BaseRevision = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ResultRevision = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEvents", x => x.Id);
                    table.CheckConstraint("CK_AuditEvents_Details", "length(\"DetailsJson\"::text) <= 4096");
                    table.CheckConstraint("CK_AuditEvents_Outcome", "\"Outcome\" IN ('SUCCEEDED', 'DENIED', 'FAILED', 'NO_CHANGE')");
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_ReceivedAt_Id",
                table: "AuditEvents",
                columns: new[] { "ReceivedAt", "Id" });
            migrationBuilder.Sql("""
                CREATE FUNCTION public.vivarium_protect_audit() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'Audit history is append-only' USING ERRCODE = '23514';
                END
                $$;
                CREATE TRIGGER "AuditEvents_AppendOnly" BEFORE UPDATE OR DELETE OR TRUNCATE
                    ON "AuditEvents" FOR EACH STATEMENT EXECUTE FUNCTION public.vivarium_protect_audit();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditEvents");
            migrationBuilder.Sql("DROP FUNCTION public.vivarium_protect_audit()");
        }
    }
}
