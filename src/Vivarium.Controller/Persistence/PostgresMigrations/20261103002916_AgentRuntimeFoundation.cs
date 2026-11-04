using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Vivarium.Controller.Persistence.PostgresMigrations
{
    /// <inheritdoc />
    public partial class AgentRuntimeFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AgentRegistrations",
                columns: table => new
                {
                    AgentId = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    TeamCityId = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    ReportedName = table.Column<string>(type: "text", nullable: false),
                    Authorized = table.Column<bool>(type: "boolean", nullable: false),
                    MaintenanceDrain = table.Column<bool>(type: "boolean", nullable: false),
                    DesiredEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    DesiredRevision = table.Column<string>(type: "text", nullable: true),
                    DesiredName = table.Column<string>(type: "text", nullable: true),
                    CustomParametersJson = table.Column<string>(type: "jsonb", nullable: false),
                    FirstSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CredentialGeneration = table.Column<long>(type: "bigint", nullable: false),
                    ConnectionGeneration = table.Column<long>(type: "bigint", nullable: false),
                    CurrentSessionId = table.Column<string>(type: "text", nullable: true),
                    SessionCredentialGeneration = table.Column<long>(type: "bigint", nullable: false),
                    SessionAuthenticated = table.Column<bool>(type: "boolean", nullable: false),
                    ProcessInstanceId = table.Column<string>(type: "text", nullable: true),
                    CurrentWorkloadId = table.Column<string>(type: "text", nullable: true),
                    LastHeartbeatSequence = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    LastHeartbeatAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Reconciled = table.Column<bool>(type: "boolean", nullable: false),
                    Quarantined = table.Column<bool>(type: "boolean", nullable: false),
                    Health = table.Column<string>(type: "text", nullable: false),
                    HealthReason = table.Column<string>(type: "text", nullable: false),
                    ReportedParametersJson = table.Column<string>(type: "jsonb", nullable: false),
                    AdvertisedCapabilitiesJson = table.Column<string>(type: "jsonb", nullable: false),
                    NegotiatedCapabilitiesJson = table.Column<string>(type: "jsonb", nullable: false),
                    StaticObservationJson = table.Column<string>(type: "jsonb", nullable: true),
                    EnrollmentTokenHash = table.Column<string>(type: "text", nullable: true),
                    AuthTokenHash = table.Column<string>(type: "text", nullable: true),
                    ProtectedPendingAuthToken = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentRegistrations", x => x.AgentId);
                    table.CheckConstraint("CK_AgentRegistrations_EnabledProjection", "NOT \"DesiredEnabled\" OR \"DesiredRevision\" IS NOT NULL");
                    table.CheckConstraint("CK_AgentRegistrations_Generations", "\"CredentialGeneration\" >= 0 AND \"ConnectionGeneration\" >= 0 AND \"SessionCredentialGeneration\" >= 0");
                    table.CheckConstraint("CK_AgentRegistrations_Health", "\"Health\" IN ('UNKNOWN','HEALTHY','UNHEALTHY')");
                    table.CheckConstraint("CK_AgentRegistrations_HeartbeatSequence", "\"LastHeartbeatSequence\" BETWEEN 0 AND 18446744073709551615");
                    table.CheckConstraint("CK_AgentRegistrations_TeamCityId", "\"TeamCityId\" > 0");
                });

            migrationBuilder.CreateTable(
                name: "AgentEnrollments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TokenHash = table.Column<string>(type: "text", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ClaimedAgentId = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentEnrollments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentEnrollments_AgentRegistrations_ClaimedAgentId",
                        column: x => x.ClaimedAgentId,
                        principalTable: "AgentRegistrations",
                        principalColumn: "AgentId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentEnrollments_ClaimedAgentId",
                table: "AgentEnrollments",
                column: "ClaimedAgentId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentEnrollments_TokenHash",
                table: "AgentEnrollments",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentRegistrations_TeamCityId",
                table: "AgentRegistrations",
                column: "TeamCityId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentEnrollments");

            migrationBuilder.DropTable(
                name: "AgentRegistrations");
        }
    }
}
