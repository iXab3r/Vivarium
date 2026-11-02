using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Vivarium.Controller.Persistence.PostgresMigrations
{
    /// <inheritdoc />
    public partial class CSharpCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CatalogChanges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorId = table.Column<Guid>(type: "uuid", nullable: false),
                    CorrelationId = table.Column<string>(type: "text", nullable: false),
                    CredentialKind = table.Column<string>(type: "text", nullable: false),
                    RequestId = table.Column<string>(type: "text", nullable: true),
                    Origin = table.Column<string>(type: "text", nullable: false),
                    RepositoryId = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    CandidateCommit = table.Column<string>(type: "text", nullable: false),
                    SelectedRevisionsJson = table.Column<string>(type: "jsonb", nullable: false),
                    BaseRevisionId = table.Column<Guid>(type: "uuid", nullable: true),
                    State = table.Column<string>(type: "text", nullable: false),
                    FailureCode = table.Column<string>(type: "text", nullable: true),
                    MaterializedJson = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CatalogChanges", x => x.Id);
                    table.CheckConstraint("CK_CatalogChanges_Origin", "\"Origin\" = 'MANAGED'");
                    table.CheckConstraint("CK_CatalogChanges_State", "\"State\" IN ('PENDING','EVALUATED','APPLIED','REJECTED','CONFLICT')");
                });

            migrationBuilder.CreateTable(
                name: "CatalogEvaluations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ChangeId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<string>(type: "text", nullable: false),
                    Phase = table.Column<string>(type: "text", nullable: false),
                    State = table.Column<string>(type: "text", nullable: false),
                    InputHashesJson = table.Column<string>(type: "jsonb", nullable: false),
                    OutputJson = table.Column<string>(type: "jsonb", nullable: true),
                    OutputHash = table.Column<string>(type: "text", nullable: true),
                    FailureCode = table.Column<string>(type: "text", nullable: true),
                    CleanupConfirmed = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CatalogEvaluations", x => x.Id);
                    table.CheckConstraint("CK_CatalogEvaluations_State", "\"State\" IN ('RUNNING','SUCCEEDED','FAILED','INTERRUPTED')");
                    table.ForeignKey(
                        name: "FK_CatalogEvaluations_CatalogChanges_ChangeId",
                        column: x => x.ChangeId,
                        principalTable: "CatalogChanges",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CatalogRevisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ChangeId = table.Column<Guid>(type: "uuid", nullable: false),
                    RepositoryId = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    ControlCommit = table.Column<string>(type: "text", nullable: false),
                    CompilerImage = table.Column<string>(type: "text", nullable: false),
                    CompilerSdk = table.Column<string>(type: "text", nullable: false),
                    ContractHash = table.Column<string>(type: "text", nullable: false),
                    DefinitionHash = table.Column<string>(type: "text", nullable: false),
                    DefinitionJson = table.Column<string>(type: "jsonb", nullable: false),
                    ActivatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CatalogRevisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CatalogRevisions_CatalogChanges_ChangeId",
                        column: x => x.ChangeId,
                        principalTable: "CatalogChanges",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ActiveCatalogs",
                columns: table => new
                {
                    RepositoryId = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    RevisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    LastKnownGoodRevisionId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActiveCatalogs", x => x.RepositoryId);
                    table.ForeignKey(
                        name: "FK_ActiveCatalogs_CatalogRevisions_LastKnownGoodRevisionId",
                        column: x => x.LastKnownGoodRevisionId,
                        principalTable: "CatalogRevisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ActiveCatalogs_CatalogRevisions_RevisionId",
                        column: x => x.RevisionId,
                        principalTable: "CatalogRevisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CatalogProjects",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    ExternalId = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    ParentId = table.Column<long>(type: "bigint", nullable: true),
                    RepositoryId = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    ActiveRevisionId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CatalogProjects", x => x.Id);
                    table.CheckConstraint("CK_CatalogProjects_Id", "\"Id\" > 0");
                    table.ForeignKey(
                        name: "FK_CatalogProjects_CatalogProjects_ParentId",
                        column: x => x.ParentId,
                        principalTable: "CatalogProjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CatalogProjects_CatalogRevisions_ActiveRevisionId",
                        column: x => x.ActiveRevisionId,
                        principalTable: "CatalogRevisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CatalogBuildTypes",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    ExternalId = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    ProjectId = table.Column<long>(type: "bigint", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    IsTemplate = table.Column<bool>(type: "boolean", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    ActiveRevisionId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CatalogBuildTypes", x => x.Id);
                    table.UniqueConstraint("AK_CatalogBuildTypes_Id_ProjectId", x => new { x.Id, x.ProjectId });
                    table.CheckConstraint("CK_CatalogBuildTypes_Id", "\"Id\" > 0");
                    table.ForeignKey(
                        name: "FK_CatalogBuildTypes_CatalogProjects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "CatalogProjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CatalogBuildTypes_CatalogRevisions_ActiveRevisionId",
                        column: x => x.ActiveRevisionId,
                        principalTable: "CatalogRevisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CatalogBuildDefinitions",
                columns: table => new
                {
                    BuildTypeId = table.Column<long>(type: "bigint", nullable: false),
                    RevisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<long>(type: "bigint", nullable: false),
                    PlanHash = table.Column<string>(type: "text", nullable: false),
                    PlanJson = table.Column<string>(type: "jsonb", nullable: false),
                    SettingsRepositoryId = table.Column<string>(type: "text", nullable: false),
                    SettingsCommit = table.Column<string>(type: "text", nullable: false),
                    SettingsPath = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CatalogBuildDefinitions", x => new { x.BuildTypeId, x.RevisionId });
                    table.CheckConstraint("CK_CatalogBuildDefinitions_Hash", "\"PlanHash\" ~ '^[0-9a-f]{64}$'");
                    table.ForeignKey(
                        name: "FK_CatalogBuildDefinitions_CatalogBuildTypes_BuildTypeId_Proje~",
                        columns: x => new { x.BuildTypeId, x.ProjectId },
                        principalTable: "CatalogBuildTypes",
                        principalColumns: new[] { "Id", "ProjectId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CatalogBuildDefinitions_CatalogRevisions_RevisionId",
                        column: x => x.RevisionId,
                        principalTable: "CatalogRevisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ActiveCatalogs_LastKnownGoodRevisionId",
                table: "ActiveCatalogs",
                column: "LastKnownGoodRevisionId");

            migrationBuilder.CreateIndex(
                name: "IX_ActiveCatalogs_RevisionId",
                table: "ActiveCatalogs",
                column: "RevisionId");

            migrationBuilder.CreateIndex(
                name: "IX_CatalogBuildDefinitions_BuildTypeId_ProjectId",
                table: "CatalogBuildDefinitions",
                columns: new[] { "BuildTypeId", "ProjectId" });

            migrationBuilder.CreateIndex(
                name: "IX_CatalogBuildDefinitions_RevisionId",
                table: "CatalogBuildDefinitions",
                column: "RevisionId");

            migrationBuilder.CreateIndex(
                name: "IX_CatalogBuildTypes_ActiveRevisionId",
                table: "CatalogBuildTypes",
                column: "ActiveRevisionId");

            migrationBuilder.CreateIndex(
                name: "IX_CatalogBuildTypes_ExternalId",
                table: "CatalogBuildTypes",
                column: "ExternalId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CatalogBuildTypes_ProjectId",
                table: "CatalogBuildTypes",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_CatalogChanges_RepositoryId_CreatedAt",
                table: "CatalogChanges",
                columns: new[] { "RepositoryId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_CatalogEvaluations_ChangeId",
                table: "CatalogEvaluations",
                column: "ChangeId");

            migrationBuilder.CreateIndex(
                name: "IX_CatalogProjects_ActiveRevisionId",
                table: "CatalogProjects",
                column: "ActiveRevisionId");

            migrationBuilder.CreateIndex(
                name: "IX_CatalogProjects_ExternalId",
                table: "CatalogProjects",
                column: "ExternalId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CatalogProjects_ParentId",
                table: "CatalogProjects",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_CatalogRevisions_ChangeId",
                table: "CatalogRevisions",
                column: "ChangeId",
                unique: true);
            migrationBuilder.Sql("""
                CREATE FUNCTION public.vivarium_protect_catalogue() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'Catalogue revision history is immutable' USING ERRCODE = '23514';
                END
                $$;
                CREATE TRIGGER "CatalogRevisions_Immutable" BEFORE UPDATE OR DELETE OR TRUNCATE
                    ON "CatalogRevisions" FOR EACH STATEMENT EXECUTE FUNCTION public.vivarium_protect_catalogue();
                CREATE TRIGGER "CatalogBuildDefinitions_Immutable" BEFORE UPDATE OR DELETE OR TRUNCATE
                    ON "CatalogBuildDefinitions" FOR EACH STATEMENT EXECUTE FUNCTION public.vivarium_protect_catalogue();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ActiveCatalogs");

            migrationBuilder.DropTable(
                name: "CatalogBuildDefinitions");

            migrationBuilder.DropTable(
                name: "CatalogEvaluations");

            migrationBuilder.DropTable(
                name: "CatalogBuildTypes");

            migrationBuilder.DropTable(
                name: "CatalogProjects");

            migrationBuilder.DropTable(
                name: "CatalogRevisions");
            migrationBuilder.Sql("DROP FUNCTION public.vivarium_protect_catalogue()");

            migrationBuilder.DropTable(
                name: "CatalogChanges");
        }
    }
}
