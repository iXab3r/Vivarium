using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Vivarium.Controller.Persistence.PostgresMigrations
{
    /// <inheritdoc />
    public partial class CatalogueBuildExecution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "WorkloadFence",
                table: "AgentRegistrations",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "RuntimeBuildCounters",
                columns: table => new
                {
                    BuildTypeId = table.Column<long>(type: "bigint", nullable: false),
                    NextValue = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RuntimeBuildCounters", x => x.BuildTypeId);
                    table.CheckConstraint("CK_RuntimeBuildCounters_Positive", "\"NextValue\" > 0");
                    table.ForeignKey(
                        name: "FK_RuntimeBuildCounters_CatalogBuildTypes_BuildTypeId",
                        column: x => x.BuildTypeId,
                        principalTable: "CatalogBuildTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RuntimeBuilds",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    BuildTypeId = table.Column<long>(type: "bigint", nullable: false),
                    ProjectId = table.Column<long>(type: "bigint", nullable: false),
                    Counter = table.Column<long>(type: "bigint", nullable: false),
                    Number = table.Column<string>(type: "text", nullable: false),
                    ActorId = table.Column<Guid>(type: "uuid", nullable: false),
                    CredentialKind = table.Column<string>(type: "text", nullable: false),
                    CorrelationId = table.Column<string>(type: "text", nullable: false),
                    State = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    StatusText = table.Column<string>(type: "text", nullable: false),
                    QueuedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    QueueDeadline = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CancelRequestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CancelReason = table.Column<string>(type: "text", nullable: true),
                    CancelOperationId = table.Column<Guid>(type: "uuid", nullable: true),
                    StopMode = table.Column<string>(type: "text", nullable: true),
                    StopDeadline = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    StopAcknowledgedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FailedToStart = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RuntimeBuilds", x => x.Id);
                    table.UniqueConstraint("AK_RuntimeBuilds_Id_BuildTypeId_ProjectId", x => new { x.Id, x.BuildTypeId, x.ProjectId });
                    table.CheckConstraint("CK_RuntimeBuilds_Id", "\"Id\" > 0 AND \"Counter\" > 0");
                    table.CheckConstraint("CK_RuntimeBuilds_State", "\"State\" IN ('QUEUED','RUNNING','RELEASING','FINISHED')");
                    table.CheckConstraint("CK_RuntimeBuilds_Status", "\"Status\" IN ('UNKNOWN','SUCCESS','FAILURE')");
                    table.CheckConstraint("CK_RuntimeBuilds_Stop", "\"StopMode\" IS NULL OR \"StopMode\" IN ('GRACEFUL','FORCE')");
                    table.ForeignKey(
                        name: "FK_RuntimeBuilds_CatalogBuildTypes_BuildTypeId_ProjectId",
                        columns: x => new { x.BuildTypeId, x.ProjectId },
                        principalTable: "CatalogBuildTypes",
                        principalColumns: new[] { "Id", "ProjectId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RuntimeArtifactUploads",
                columns: table => new
                {
                    BuildId = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "text", nullable: false),
                    ObjectKey = table.Column<string>(type: "text", nullable: false),
                    Size = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RuntimeArtifactUploads", x => new { x.BuildId, x.Sha256 });
                    table.CheckConstraint("CK_RuntimeArtifactUploads_Content", "\"Size\" >= 0 AND \"Sha256\" ~ '^[a-f0-9]{64}$'");
                    table.ForeignKey(
                        name: "FK_RuntimeArtifactUploads_RuntimeBuilds_BuildId",
                        column: x => x.BuildId,
                        principalTable: "RuntimeBuilds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RuntimeBuildExecutions",
                columns: table => new
                {
                    BuildId = table.Column<long>(type: "bigint", nullable: false),
                    WorkloadId = table.Column<string>(type: "text", nullable: false),
                    AgentId = table.Column<string>(type: "text", nullable: false),
                    LeaseFence = table.Column<long>(type: "bigint", nullable: false),
                    CredentialGeneration = table.Column<long>(type: "bigint", nullable: false),
                    AssignedSessionId = table.Column<string>(type: "text", nullable: false),
                    AssignedConnectionGeneration = table.Column<long>(type: "bigint", nullable: false),
                    CurrentSessionId = table.Column<string>(type: "text", nullable: false),
                    CurrentConnectionGeneration = table.Column<long>(type: "bigint", nullable: false),
                    SelectionSnapshotJson = table.Column<string>(type: "jsonb", nullable: false),
                    TargetSnapshotJson = table.Column<string>(type: "jsonb", nullable: true),
                    Assignment = table.Column<byte[]>(type: "bytea", nullable: false),
                    Phase = table.Column<string>(type: "text", nullable: false),
                    PreparedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PreparationDeadline = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AcknowledgementDeadline = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SentAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AcceptedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ExecutionDeadline = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReconnectDeadline = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TerminalReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    OutputDeadline = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReleasedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RuntimeBuildExecutions", x => x.BuildId);
                    table.CheckConstraint("CK_RuntimeBuildExecutions_Fences", "\"LeaseFence\" > 0 AND \"CredentialGeneration\" > 0 AND \"AssignedConnectionGeneration\" > 0 AND \"CurrentConnectionGeneration\" > 0");
                    table.CheckConstraint("CK_RuntimeBuildExecutions_Phase", "\"Phase\" IN ('PREPARING','DISPATCHING','ACCEPTED','TERMINAL','RELEASED')");
                    table.ForeignKey(
                        name: "FK_RuntimeBuildExecutions_AgentRegistrations_AgentId",
                        column: x => x.AgentId,
                        principalTable: "AgentRegistrations",
                        principalColumn: "AgentId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RuntimeBuildExecutions_RuntimeBuilds_BuildId",
                        column: x => x.BuildId,
                        principalTable: "RuntimeBuilds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RuntimeBuildInputs",
                columns: table => new
                {
                    BuildId = table.Column<long>(type: "bigint", nullable: false),
                    BuildTypeId = table.Column<long>(type: "bigint", nullable: false),
                    ProjectId = table.Column<long>(type: "bigint", nullable: false),
                    RevisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    CollectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    OverridesJson = table.Column<string>(type: "jsonb", nullable: false),
                    AdmissionJson = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RuntimeBuildInputs", x => x.BuildId);
                    table.ForeignKey(
                        name: "FK_RuntimeBuildInputs_CatalogBuildDefinitions_BuildTypeId_Revi~",
                        columns: x => new { x.BuildTypeId, x.RevisionId },
                        principalTable: "CatalogBuildDefinitions",
                        principalColumns: new[] { "BuildTypeId", "RevisionId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RuntimeBuildInputs_RuntimeBuilds_BuildId_BuildTypeId_Projec~",
                        columns: x => new { x.BuildId, x.BuildTypeId, x.ProjectId },
                        principalTable: "RuntimeBuilds",
                        principalColumns: new[] { "Id", "BuildTypeId", "ProjectId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RuntimeBuildOutputs",
                columns: table => new
                {
                    BuildId = table.Column<long>(type: "bigint", nullable: false),
                    State = table.Column<string>(type: "text", nullable: false),
                    NextPosition = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RuntimeBuildOutputs", x => x.BuildId);
                    table.CheckConstraint("CK_RuntimeBuildOutputs_State", "\"State\" IN ('OPEN','SEALING','COMPLETE','COMPLETE_WITH_GAPS') AND \"NextPosition\" > 0");
                    table.ForeignKey(
                        name: "FK_RuntimeBuildOutputs_RuntimeBuilds_BuildId",
                        column: x => x.BuildId,
                        principalTable: "RuntimeBuilds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RuntimeBuildPayloads",
                columns: table => new
                {
                    BuildId = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "text", nullable: false),
                    ObjectKey = table.Column<string>(type: "text", nullable: false),
                    Size = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RuntimeBuildPayloads", x => new { x.BuildId, x.Sha256 });
                    table.CheckConstraint("CK_RuntimeBuildPayloads_Content", "\"Size\" >= 0 AND \"Sha256\" ~ '^[a-f0-9]{64}$'");
                    table.ForeignKey(
                        name: "FK_RuntimeBuildPayloads_RuntimeBuilds_BuildId",
                        column: x => x.BuildId,
                        principalTable: "RuntimeBuilds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RuntimeBuildResults",
                columns: table => new
                {
                    BuildId = table.Column<long>(type: "bigint", nullable: false),
                    Evidence = table.Column<byte[]>(type: "bytea", nullable: false),
                    SemanticHash = table.Column<string>(type: "text", nullable: false),
                    OriginalOutcome = table.Column<string>(type: "text", nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RuntimeBuildResults", x => x.BuildId);
                    table.CheckConstraint("CK_RuntimeBuildResults_Hash", "\"SemanticHash\" ~ '^[a-f0-9]{64}$'");
                    table.ForeignKey(
                        name: "FK_RuntimeBuildResults_RuntimeBuilds_BuildId",
                        column: x => x.BuildId,
                        principalTable: "RuntimeBuilds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RuntimeBuildArtifacts",
                columns: table => new
                {
                    BuildId = table.Column<long>(type: "bigint", nullable: false),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    Path = table.Column<string>(type: "text", nullable: false),
                    Sha256 = table.Column<string>(type: "text", nullable: false),
                    Size = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RuntimeBuildArtifacts", x => new { x.BuildId, x.Ordinal });
                    table.CheckConstraint("CK_RuntimeBuildArtifacts_Content", "\"Ordinal\" >= 0 AND \"Size\" >= 0");
                    table.ForeignKey(
                        name: "FK_RuntimeBuildArtifacts_RuntimeArtifactUploads_BuildId_Sha256",
                        columns: x => new { x.BuildId, x.Sha256 },
                        principalTable: "RuntimeArtifactUploads",
                        principalColumns: new[] { "BuildId", "Sha256" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RuntimeOutputStreams",
                columns: table => new
                {
                    BuildId = table.Column<long>(type: "bigint", nullable: false),
                    StepIndex = table.Column<int>(type: "integer", nullable: false),
                    Stream = table.Column<int>(type: "integer", nullable: false),
                    CommittedSequence = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    CommittedBytes = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    FinalSequence = table.Column<decimal>(type: "numeric(20,0)", nullable: true),
                    FinalBytes = table.Column<decimal>(type: "numeric(20,0)", nullable: true),
                    TailStarted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RuntimeOutputStreams", x => new { x.BuildId, x.StepIndex, x.Stream });
                    table.CheckConstraint("CK_RuntimeOutputStreams_Final", "(\"FinalSequence\" IS NULL AND \"FinalBytes\" IS NULL) OR (\"FinalSequence\" IS NOT NULL AND \"FinalBytes\" IS NOT NULL AND \"FinalSequence\" BETWEEN 0 AND 18446744073709551615 AND \"FinalBytes\" BETWEEN 0 AND 18446744073709551615)");
                    table.CheckConstraint("CK_RuntimeOutputStreams_Prefix", "\"CommittedSequence\" BETWEEN 0 AND 18446744073709551615 AND \"CommittedBytes\" BETWEEN 0 AND 18446744073709551615");
                    table.CheckConstraint("CK_RuntimeOutputStreams_Scope", "\"StepIndex\" >= 0 AND \"Stream\" IN (0,1)");
                    table.ForeignKey(
                        name: "FK_RuntimeOutputStreams_RuntimeBuildOutputs_BuildId",
                        column: x => x.BuildId,
                        principalTable: "RuntimeBuildOutputs",
                        principalColumn: "BuildId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RuntimeOutputRanges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BuildId = table.Column<long>(type: "bigint", nullable: false),
                    StepIndex = table.Column<int>(type: "integer", nullable: false),
                    Stream = table.Column<int>(type: "integer", nullable: false),
                    Start = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    End = table.Column<decimal>(type: "numeric(20,0)", nullable: true),
                    ByteStart = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    ByteEnd = table.Column<decimal>(type: "numeric(20,0)", nullable: true),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    Sha256 = table.Column<string>(type: "text", nullable: true),
                    ObjectKey = table.Column<string>(type: "text", nullable: true),
                    Reason = table.Column<string>(type: "text", nullable: true),
                    Position = table.Column<long>(type: "bigint", nullable: true),
                    ReceiptOrder = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RuntimeOutputRanges", x => x.Id);
                    table.CheckConstraint("CK_RuntimeOutputRanges_Content", "(\"Kind\" IN ('HEAD','TAIL') AND \"End\" IS NOT NULL AND \"ByteEnd\" IS NOT NULL AND \"End\" = \"Start\" + 1 AND \"ByteEnd\" - \"ByteStart\" BETWEEN 1 AND 65536 AND \"Sha256\" IS NOT NULL AND \"Sha256\" ~ '^[a-f0-9]{64}$' AND \"ObjectKey\" IS NOT NULL AND \"Reason\" IS NULL) OR (\"Kind\" = 'GAP' AND \"Sha256\" IS NULL AND \"ObjectKey\" IS NULL AND \"Reason\" IS NOT NULL AND \"Reason\" IN ('delivery-deadline','quota-exceeded','spool-overflow','storage-failure','producer-lost','retention'))");
                    table.CheckConstraint("CK_RuntimeOutputRanges_Kind", "\"Kind\" IN ('HEAD','TAIL','GAP')");
                    table.CheckConstraint("CK_RuntimeOutputRanges_Sequence", "\"Start\" BETWEEN 0 AND 18446744073709551615 AND \"ByteStart\" BETWEEN 0 AND 18446744073709551615 AND ((\"End\" IS NOT NULL AND \"ByteEnd\" IS NOT NULL AND \"End\" > \"Start\" AND \"End\" <= 18446744073709551615 AND \"ByteEnd\" > \"ByteStart\" AND \"ByteEnd\" <= 18446744073709551615) OR (\"End\" IS NULL AND \"ByteEnd\" IS NULL AND \"Kind\" = 'GAP' AND \"Reason\" = 'producer-lost'))");
                    table.ForeignKey(
                        name: "FK_RuntimeOutputRanges_RuntimeOutputStreams_BuildId_StepIndex_~",
                        columns: x => new { x.BuildId, x.StepIndex, x.Stream },
                        principalTable: "RuntimeOutputStreams",
                        principalColumns: new[] { "BuildId", "StepIndex", "Stream" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentRegistrations_AuthTokenHash",
                table: "AgentRegistrations",
                column: "AuthTokenHash",
                unique: true,
                filter: "\"AuthTokenHash\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_AgentRegistrations_WorkloadFence",
                table: "AgentRegistrations",
                sql: "\"WorkloadFence\" >= 0");

            migrationBuilder.CreateIndex(
                name: "IX_RuntimeBuildArtifacts_BuildId_Path",
                table: "RuntimeBuildArtifacts",
                columns: new[] { "BuildId", "Path" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RuntimeBuildArtifacts_BuildId_Sha256",
                table: "RuntimeBuildArtifacts",
                columns: new[] { "BuildId", "Sha256" });

            migrationBuilder.CreateIndex(
                name: "IX_RuntimeBuildExecutions_AgentId",
                table: "RuntimeBuildExecutions",
                column: "AgentId",
                unique: true,
                filter: "\"ReleasedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_RuntimeBuildExecutions_WorkloadId",
                table: "RuntimeBuildExecutions",
                column: "WorkloadId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RuntimeBuildInputs_BuildId_BuildTypeId_ProjectId",
                table: "RuntimeBuildInputs",
                columns: new[] { "BuildId", "BuildTypeId", "ProjectId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RuntimeBuildInputs_BuildTypeId_RevisionId",
                table: "RuntimeBuildInputs",
                columns: new[] { "BuildTypeId", "RevisionId" });

            migrationBuilder.CreateIndex(
                name: "IX_RuntimeBuilds_BuildTypeId_Counter",
                table: "RuntimeBuilds",
                columns: new[] { "BuildTypeId", "Counter" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RuntimeBuilds_BuildTypeId_ProjectId",
                table: "RuntimeBuilds",
                columns: new[] { "BuildTypeId", "ProjectId" });

            migrationBuilder.CreateIndex(
                name: "IX_RuntimeBuilds_State_QueuedAt_Id",
                table: "RuntimeBuilds",
                columns: new[] { "State", "QueuedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_RuntimeOutputRanges_BuildId_Position",
                table: "RuntimeOutputRanges",
                columns: new[] { "BuildId", "Position" },
                unique: true,
                filter: "\"Position\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_RuntimeOutputRanges_BuildId_StepIndex_Stream_Start",
                table: "RuntimeOutputRanges",
                columns: new[] { "BuildId", "StepIndex", "Stream", "Start" },
                unique: true);
            migrationBuilder.Sql("""
                CREATE FUNCTION public.vivarium_protect_build_evidence() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION 'Build evidence is immutable' USING ERRCODE = '23514'; END $$;
                CREATE TRIGGER "RuntimeBuildInputs_Immutable" BEFORE UPDATE OR DELETE OR TRUNCATE
                    ON "RuntimeBuildInputs" FOR EACH STATEMENT EXECUTE FUNCTION public.vivarium_protect_build_evidence();
                CREATE TRIGGER "RuntimeBuildResults_Immutable" BEFORE UPDATE OR DELETE OR TRUNCATE
                    ON "RuntimeBuildResults" FOR EACH STATEMENT EXECUTE FUNCTION public.vivarium_protect_build_evidence();
                CREATE TRIGGER "RuntimeBuildPayloads_Immutable" BEFORE UPDATE OR DELETE OR TRUNCATE
                    ON "RuntimeBuildPayloads" FOR EACH STATEMENT EXECUTE FUNCTION public.vivarium_protect_build_evidence();
                CREATE TRIGGER "RuntimeBuildArtifacts_Immutable" BEFORE UPDATE OR DELETE OR TRUNCATE
                    ON "RuntimeBuildArtifacts" FOR EACH STATEMENT EXECUTE FUNCTION public.vivarium_protect_build_evidence();
                CREATE TRIGGER "RuntimeArtifactUploads_Immutable" BEFORE UPDATE OR DELETE OR TRUNCATE
                    ON "RuntimeArtifactUploads" FOR EACH STATEMENT EXECUTE FUNCTION public.vivarium_protect_build_evidence();
                CREATE FUNCTION public.vivarium_protect_execution_origin() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF ROW(NEW."BuildId", NEW."WorkloadId", NEW."AgentId", NEW."LeaseFence", NEW."CredentialGeneration",
                        NEW."AssignedSessionId", NEW."AssignedConnectionGeneration", NEW."SelectionSnapshotJson", NEW."PreparedAt", NEW."PreparationDeadline")
                        IS DISTINCT FROM ROW(OLD."BuildId", OLD."WorkloadId", OLD."AgentId", OLD."LeaseFence", OLD."CredentialGeneration",
                        OLD."AssignedSessionId", OLD."AssignedConnectionGeneration", OLD."SelectionSnapshotJson", OLD."PreparedAt", OLD."PreparationDeadline")
                        OR (OLD."TargetSnapshotJson" IS NOT NULL AND NEW."TargetSnapshotJson" IS DISTINCT FROM OLD."TargetSnapshotJson")
                        OR (octet_length(OLD."Assignment") > 0 AND NEW."Assignment" IS DISTINCT FROM OLD."Assignment")
                        OR (OLD."AcknowledgementDeadline" IS NOT NULL AND NEW."AcknowledgementDeadline" IS DISTINCT FROM OLD."AcknowledgementDeadline")
                    THEN RAISE EXCEPTION 'Execution origin is immutable' USING ERRCODE = '23514'; END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER "RuntimeBuildExecutions_Origin" BEFORE UPDATE ON "RuntimeBuildExecutions"
                    FOR EACH ROW EXECUTE FUNCTION public.vivarium_protect_execution_origin();
                CREATE FUNCTION public.vivarium_protect_output_history() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF OLD."Position" IS NOT NULL THEN
                        RAISE EXCEPTION 'Published output is immutable' USING ERRCODE = '23514';
                    END IF;
                    IF TG_OP = 'UPDATE' AND (ROW(NEW."Id", NEW."BuildId", NEW."StepIndex", NEW."Stream", NEW."Start", NEW."ByteStart", NEW."ReceiptOrder", NEW."ReceivedAt")
                        IS DISTINCT FROM ROW(OLD."Id", OLD."BuildId", OLD."StepIndex", OLD."Stream", OLD."Start", OLD."ByteStart", OLD."ReceiptOrder", OLD."ReceivedAt")
                        OR (OLD."Sha256" IS NOT NULL AND NEW."Sha256" IS NOT NULL AND OLD."Sha256" <> NEW."Sha256"))
                    THEN RAISE EXCEPTION 'Output identity is immutable' USING ERRCODE = '23514'; END IF;
                    IF TG_OP = 'DELETE' THEN RETURN OLD; END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER "RuntimeOutputRanges_History" BEFORE UPDATE OR DELETE ON "RuntimeOutputRanges"
                    FOR EACH ROW EXECUTE FUNCTION public.vivarium_protect_output_history();
                CREATE TRIGGER "RuntimeOutputRanges_NoTruncate" BEFORE TRUNCATE ON "RuntimeOutputRanges"
                    FOR EACH STATEMENT EXECUTE FUNCTION public.vivarium_protect_build_evidence();CREATE TRIGGER "RuntimeBuildExecutions_NoDelete" BEFORE DELETE OR TRUNCATE ON "RuntimeBuildExecutions"
                    FOR EACH STATEMENT EXECUTE FUNCTION public.vivarium_protect_build_evidence();
                CREATE FUNCTION public.vivarium_protect_build_origin() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF ROW(NEW."Id", NEW."BuildTypeId", NEW."ProjectId", NEW."Counter", NEW."ActorId", NEW."CredentialKind", NEW."CorrelationId", NEW."QueuedAt", NEW."QueueDeadline")
                        IS DISTINCT FROM ROW(OLD."Id", OLD."BuildTypeId", OLD."ProjectId", OLD."Counter", OLD."ActorId", OLD."CredentialKind", OLD."CorrelationId", OLD."QueuedAt", OLD."QueueDeadline")
                    THEN RAISE EXCEPTION 'Build origin is immutable' USING ERRCODE = '23514'; END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER "RuntimeBuilds_Origin" BEFORE UPDATE ON "RuntimeBuilds"
                    FOR EACH ROW EXECUTE FUNCTION public.vivarium_protect_build_origin();
                CREATE TRIGGER "RuntimeBuilds_NoDelete" BEFORE DELETE OR TRUNCATE ON "RuntimeBuilds"
                    FOR EACH STATEMENT EXECUTE FUNCTION public.vivarium_protect_build_evidence();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RuntimeBuildArtifacts");

            migrationBuilder.DropTable(
                name: "RuntimeBuildCounters");

            migrationBuilder.DropTable(
                name: "RuntimeBuildExecutions");

            migrationBuilder.DropTable(
                name: "RuntimeBuildInputs");

            migrationBuilder.DropTable(
                name: "RuntimeBuildPayloads");

            migrationBuilder.DropTable(
                name: "RuntimeBuildResults");

            migrationBuilder.DropTable(
                name: "RuntimeOutputRanges");

            migrationBuilder.DropTable(
                name: "RuntimeArtifactUploads");

            migrationBuilder.DropTable(
                name: "RuntimeOutputStreams");

            migrationBuilder.DropTable(
                name: "RuntimeBuildOutputs");

            migrationBuilder.DropTable(
                name: "RuntimeBuilds");

            migrationBuilder.DropIndex(
                name: "IX_AgentRegistrations_AuthTokenHash",
                table: "AgentRegistrations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_AgentRegistrations_WorkloadFence",
                table: "AgentRegistrations");

            migrationBuilder.DropColumn(
                name: "WorkloadFence",
                table: "AgentRegistrations");
            migrationBuilder.Sql("""
                DROP FUNCTION public.vivarium_protect_output_history();
                DROP FUNCTION public.vivarium_protect_execution_origin();
                DROP FUNCTION public.vivarium_protect_build_evidence();
                DROP FUNCTION public.vivarium_protect_build_origin();
                """);
        }
    }
}
