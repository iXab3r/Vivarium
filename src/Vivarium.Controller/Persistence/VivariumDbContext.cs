using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Vivarium.Controller.Identity;
using Vivarium.Controller.Auditing;
using Vivarium.Controller.Catalog;
using Vivarium.Controller.Agents;
using Vivarium.Controller.Builds.Runtime;

namespace Vivarium.Controller.Persistence;

/// <summary>PostgreSQL application authority for the fresh D34 installation; no Git identity projection.</summary>
public sealed class VivariumDbContext(DbContextOptions<VivariumDbContext> options)
    : IdentityUserContext<VivariumUser, Guid>(options)
{
    public DbSet<PersistentAuditEvent> AuditEvents => Set<PersistentAuditEvent>();
    public DbSet<TeamCityRole> Roles => Set<TeamCityRole>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<TeamCityPermissionDefinition> PermissionDefinitions => Set<TeamCityPermissionDefinition>();
    public DbSet<IncludedRole> IncludedRoles => Set<IncludedRole>();
    public DbSet<UserRoleGrant> UserRoleGrants => Set<UserRoleGrant>();
    public DbSet<TeamCityGroup> Groups => Set<TeamCityGroup>();
    public DbSet<GroupRoleGrant> GroupRoleGrants => Set<GroupRoleGrant>();
    public DbSet<CatalogChange> CatalogChanges => Set<CatalogChange>();
    public DbSet<CatalogEvaluation> CatalogEvaluations => Set<CatalogEvaluation>();
    public DbSet<CatalogRevision> CatalogRevisions => Set<CatalogRevision>();
    public DbSet<CatalogActive> ActiveCatalogs => Set<CatalogActive>();
    public DbSet<CatalogProjectIdentity> CatalogProjects => Set<CatalogProjectIdentity>();
    public DbSet<CatalogBuildType> CatalogBuildTypes => Set<CatalogBuildType>();
    public DbSet<CatalogBuildDefinition> CatalogBuildDefinitions => Set<CatalogBuildDefinition>();
    public DbSet<AgentRegistration> AgentRegistrations => Set<AgentRegistration>();
    public DbSet<AgentEnrollment> AgentEnrollments => Set<AgentEnrollment>();
    public DbSet<RuntimeBuild> RuntimeBuilds => Set<RuntimeBuild>();
    public DbSet<RuntimeBuildInput> RuntimeBuildInputs => Set<RuntimeBuildInput>();
    public DbSet<RuntimeBuildRunInput> RuntimeBuildRunInputs => Set<RuntimeBuildRunInput>();
    public DbSet<RuntimeVcsRootInstance> RuntimeVcsRootInstances => Set<RuntimeVcsRootInstance>();
    public DbSet<RuntimeBuildSourceSelection> RuntimeBuildSourceSelections => Set<RuntimeBuildSourceSelection>();
    public DbSet<RuntimeBuildSourceReceipt> RuntimeBuildSourceReceipts => Set<RuntimeBuildSourceReceipt>();
    public DbSet<RuntimeBuildNumberObservation> RuntimeBuildNumberObservations => Set<RuntimeBuildNumberObservation>();
    public DbSet<RuntimeBuildDependency> RuntimeBuildDependencies => Set<RuntimeBuildDependency>();
    public DbSet<RuntimeBuildResolution> RuntimeBuildResolutions => Set<RuntimeBuildResolution>();
    public DbSet<RuntimeBuildConsumedArtifact> RuntimeBuildConsumedArtifacts => Set<RuntimeBuildConsumedArtifact>();
    public DbSet<RuntimeBuildCounter> RuntimeBuildCounters => Set<RuntimeBuildCounter>();
    public DbSet<RuntimeBuildExecution> RuntimeBuildExecutions => Set<RuntimeBuildExecution>();
    public DbSet<RuntimeBuildObservation> RuntimeBuildObservations => Set<RuntimeBuildObservation>();
    public DbSet<RuntimeBuildResult> RuntimeBuildResults => Set<RuntimeBuildResult>();
    public DbSet<RuntimeBuildPayload> RuntimeBuildPayloads => Set<RuntimeBuildPayload>();
    public DbSet<RuntimeArtifactUpload> RuntimeArtifactUploads => Set<RuntimeArtifactUpload>();
    public DbSet<RuntimeBuildArtifact> RuntimeBuildArtifacts => Set<RuntimeBuildArtifact>();
    public DbSet<RuntimeBuildOutput> RuntimeBuildOutputs => Set<RuntimeBuildOutput>();
    public DbSet<RuntimeOutputStream> RuntimeOutputStreams => Set<RuntimeOutputStream>();
    public DbSet<RuntimeOutputRange> RuntimeOutputRanges => Set<RuntimeOutputRange>();
    public DbSet<RuntimeOutputExpiry> RuntimeOutputExpiries => Set<RuntimeOutputExpiry>();
    public DbSet<RuntimeOutputDeletion> RuntimeOutputDeletions => Set<RuntimeOutputDeletion>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        RuntimeBuildModel.Configure(modelBuilder);
        var agent = modelBuilder.Entity<AgentRegistration>();
        agent.HasKey(entry => entry.AgentId);
        agent.Property(entry => entry.AgentId).UseCollation("C");
        agent.Property(entry => entry.TeamCityId).UseIdentityAlwaysColumn();
        agent.HasIndex(entry => entry.TeamCityId).IsUnique();
        agent.HasIndex(entry => entry.AuthTokenHash).IsUnique().HasFilter("\"AuthTokenHash\" IS NOT NULL");
        agent.Property(entry => entry.LastHeartbeatSequence).HasColumnType("numeric(20,0)");
        agent.Property(entry => entry.CustomParametersJson).HasColumnType("jsonb");
        agent.Property(entry => entry.ReportedParametersJson).HasColumnType("jsonb");
        agent.Property(entry => entry.AdvertisedCapabilitiesJson).HasColumnType("jsonb");
        agent.Property(entry => entry.NegotiatedCapabilitiesJson).HasColumnType("jsonb");
        agent.Property(entry => entry.StaticObservationJson).HasColumnType("jsonb");
        agent.ToTable("AgentRegistrations", table =>
        {
            table.HasCheckConstraint("CK_AgentRegistrations_TeamCityId", "\"TeamCityId\" > 0");
            table.HasCheckConstraint("CK_AgentRegistrations_Generations", "\"CredentialGeneration\" >= 0 AND \"ConnectionGeneration\" >= 0 AND \"SessionCredentialGeneration\" >= 0");
            table.HasCheckConstraint("CK_AgentRegistrations_HeartbeatSequence", "\"LastHeartbeatSequence\" BETWEEN 0 AND 18446744073709551615");
            table.HasCheckConstraint("CK_AgentRegistrations_Health", "\"Health\" IN ('UNKNOWN','HEALTHY','UNHEALTHY')");
            table.HasCheckConstraint("CK_AgentRegistrations_EnabledProjection", "NOT \"DesiredEnabled\" OR \"DesiredRevision\" IS NOT NULL");
            table.HasCheckConstraint("CK_AgentRegistrations_WorkloadFence", "\"WorkloadFence\" >= 0");
        });
        var enrollment = modelBuilder.Entity<AgentEnrollment>();
        enrollment.HasKey(entry => entry.Id);
        enrollment.HasIndex(entry => entry.TokenHash).IsUnique();
        enrollment.HasOne<AgentRegistration>().WithMany().HasForeignKey(entry => entry.ClaimedAgentId).OnDelete(DeleteBehavior.Restrict);
        var change = modelBuilder.Entity<CatalogChange>();
        change.HasKey(entry => entry.Id);
        change.Property(entry => entry.MaterializedJson).HasColumnType("jsonb");
        change.Property(entry => entry.SelectedRevisionsJson).HasColumnType("jsonb");
        change.Property(entry => entry.RepositoryId).UseCollation("C");
        change.Property(entry => entry.Kind).HasDefaultValue("RECONCILE");
        change.HasIndex(entry => new { entry.RepositoryId, entry.CreatedAt });
        change.ToTable("CatalogChanges", table =>
        {
            table.HasCheckConstraint("CK_CatalogChanges_State", "\"State\" IN ('PENDING','EVALUATED','APPLIED','REJECTED','CONFLICT')");
            table.HasCheckConstraint("CK_CatalogChanges_Origin", "\"Origin\" = 'MANAGED'");
            table.HasCheckConstraint("CK_CatalogChanges_Kind", "(\"Kind\" = 'RECONCILE' AND \"TargetAgentId\" IS NULL) OR (\"Kind\" = 'AGENT_ENABLEMENT' AND \"TargetAgentId\" IS NOT NULL)");
        });
        var evaluation = modelBuilder.Entity<CatalogEvaluation>();
        evaluation.HasKey(entry => entry.Id);
        evaluation.Property(entry => entry.InputHashesJson).HasColumnType("jsonb");
        evaluation.Property(entry => entry.OutputJson).HasColumnType("jsonb");
        evaluation.HasIndex(entry => entry.ChangeId);
        evaluation.HasOne<CatalogChange>().WithMany().HasForeignKey(entry => entry.ChangeId).OnDelete(DeleteBehavior.Restrict);
        evaluation.ToTable("CatalogEvaluations", table =>
            table.HasCheckConstraint("CK_CatalogEvaluations_State", "\"State\" IN ('RUNNING','SUCCEEDED','FAILED','INTERRUPTED')"));
        var revision = modelBuilder.Entity<CatalogRevision>();
        revision.HasKey(entry => entry.Id);
        revision.Property(entry => entry.RepositoryId).UseCollation("C");
        revision.Property(entry => entry.DefinitionJson).HasColumnType("jsonb");
        revision.HasIndex(entry => entry.ChangeId).IsUnique();
        revision.HasOne<CatalogChange>().WithMany().HasForeignKey(entry => entry.ChangeId).OnDelete(DeleteBehavior.Restrict);
        var active = modelBuilder.Entity<CatalogActive>();
        active.HasKey(entry => entry.RepositoryId);
        active.Property(entry => entry.RepositoryId).UseCollation("C");
        active.HasOne<CatalogRevision>().WithMany().HasForeignKey(entry => entry.RevisionId).OnDelete(DeleteBehavior.Restrict);
        active.HasOne<CatalogRevision>().WithMany().HasForeignKey(entry => entry.LastKnownGoodRevisionId).OnDelete(DeleteBehavior.Restrict);
        var project = modelBuilder.Entity<CatalogProjectIdentity>();
        project.HasKey(entry => entry.Id);
        project.Property(entry => entry.Id).UseIdentityAlwaysColumn();
        project.Property(entry => entry.ExternalId).UseCollation("C");
        project.Property(entry => entry.RepositoryId).UseCollation("C");
        project.HasIndex(entry => entry.ExternalId).IsUnique();
        project.HasOne<CatalogProjectIdentity>().WithMany().HasForeignKey(entry => entry.ParentId).OnDelete(DeleteBehavior.Restrict);
        project.HasOne<CatalogRevision>().WithMany().HasForeignKey(entry => entry.ActiveRevisionId).OnDelete(DeleteBehavior.Restrict);
        project.ToTable("CatalogProjects", table => table.HasCheckConstraint("CK_CatalogProjects_Id", "\"Id\" > 0"));
        var buildType = modelBuilder.Entity<CatalogBuildType>();
        buildType.HasKey(entry => entry.Id);
        buildType.Property(entry => entry.Id).UseIdentityAlwaysColumn();
        buildType.Property(entry => entry.ExternalId).UseCollation("C");
        buildType.HasIndex(entry => entry.ExternalId).IsUnique();
        buildType.HasAlternateKey(entry => new { entry.Id, entry.ProjectId });
        buildType.HasOne<CatalogProjectIdentity>().WithMany().HasForeignKey(entry => entry.ProjectId).OnDelete(DeleteBehavior.Restrict);
        buildType.HasOne<CatalogRevision>().WithMany().HasForeignKey(entry => entry.ActiveRevisionId).OnDelete(DeleteBehavior.Restrict);
        buildType.ToTable("CatalogBuildTypes", table => table.HasCheckConstraint("CK_CatalogBuildTypes_Id", "\"Id\" > 0"));
        var definition = modelBuilder.Entity<CatalogBuildDefinition>();
        definition.HasKey(entry => new { entry.BuildTypeId, entry.RevisionId });
        definition.Property(entry => entry.PlanJson).HasColumnType("jsonb");
        definition.HasOne<CatalogBuildType>().WithMany().HasForeignKey(entry => new { entry.BuildTypeId, entry.ProjectId })
            .HasPrincipalKey(entry => new { entry.Id, entry.ProjectId }).OnDelete(DeleteBehavior.Restrict);
        definition.HasOne<CatalogRevision>().WithMany().HasForeignKey(entry => entry.RevisionId).OnDelete(DeleteBehavior.Restrict);
        definition.ToTable("CatalogBuildDefinitions", table => table.HasCheckConstraint("CK_CatalogBuildDefinitions_Hash",
            "\"PlanHash\" ~ '^[0-9a-f]{64}$'"));
        modelBuilder.UseOpenIddict();
        var account = modelBuilder.Entity<VivariumUser>();
        account.Property(user => user.DisplayName).HasMaxLength(256);
        account.Property(user => user.TeamCityId).UseIdentityByDefaultColumn();
        account.HasIndex(user => user.TeamCityId).IsUnique();
        account.ToTable("AspNetUsers", table =>
            table.HasCheckConstraint("CK_User_TeamCityId", "\"TeamCityId\" > 0"));
        modelBuilder.Entity<IdentityUserLogin<Guid>>().Property(login => login.LoginProvider).HasMaxLength(1024);
        modelBuilder.Entity<IdentityUserLogin<Guid>>().Property(login => login.ProviderKey).HasMaxLength(255);
        var audit = modelBuilder.Entity<PersistentAuditEvent>();
        audit.ToTable("AuditEvents", table =>
        {
            table.HasCheckConstraint("CK_AuditEvents_Outcome", "\"Outcome\" IN ('SUCCEEDED', 'DENIED', 'FAILED', 'NO_CHANGE')");
            table.HasCheckConstraint("CK_AuditEvents_Details", "length(\"DetailsJson\"::text) <= 4096");
        });
        audit.HasKey(entry => entry.Id);
        audit.Property(entry => entry.Id).HasMaxLength(64).UseCollation("C");
        audit.Property(entry => entry.ActorType).HasMaxLength(32);
        audit.Property(entry => entry.ActorId).HasMaxLength(256).UseCollation("C");
        audit.Property(entry => entry.CredentialKind).HasMaxLength(64);
        audit.Property(entry => entry.CorrelationId).HasMaxLength(128);
        audit.Property(entry => entry.RequestId).HasMaxLength(256);
        audit.Property(entry => entry.Action).HasMaxLength(128);
        audit.Property(entry => entry.TargetType).HasMaxLength(64);
        audit.Property(entry => entry.TargetId).HasMaxLength(256).UseCollation("C");
        audit.Property(entry => entry.Outcome).HasMaxLength(16);
        audit.Property(entry => entry.ReasonCode).HasMaxLength(128);
        audit.Property(entry => entry.DetailsJson).HasColumnType("jsonb");
        audit.Property(entry => entry.BaseRevision).HasMaxLength(256);
        audit.Property(entry => entry.ResultRevision).HasMaxLength(256);
        audit.HasIndex(entry => new { entry.ReceivedAt, entry.Id });
        var role = modelBuilder.Entity<TeamCityRole>();
        role.HasKey(entry => entry.Id);
        role.Property(entry => entry.Id).UseCollation("C");
        var permission = modelBuilder.Entity<RolePermission>();
        var permissionDefinition = modelBuilder.Entity<TeamCityPermissionDefinition>();
        permissionDefinition.HasKey(entry => entry.Id);
        permissionDefinition.Property(entry => entry.Id).UseCollation("C");
        permission.HasKey(entry => new { entry.RoleId, entry.PermissionId });
        permission.Property(entry => entry.RoleId).UseCollation("C");
        permission.Property(entry => entry.PermissionId).UseCollation("C");
        permission.HasOne<TeamCityRole>().WithMany().HasForeignKey(entry => entry.RoleId).OnDelete(DeleteBehavior.Cascade);
        var included = modelBuilder.Entity<IncludedRole>();
        included.HasKey(entry => new { entry.RoleId, entry.IncludedRoleId });
        included.Property(entry => entry.RoleId).UseCollation("C");
        included.Property(entry => entry.IncludedRoleId).UseCollation("C");
        included.HasOne<TeamCityRole>().WithMany().HasForeignKey(entry => entry.RoleId).OnDelete(DeleteBehavior.Cascade);
        included.HasOne<TeamCityRole>().WithMany().HasForeignKey(entry => entry.IncludedRoleId).OnDelete(DeleteBehavior.Cascade);
        var userGrant = modelBuilder.Entity<UserRoleGrant>();
        userGrant.HasKey(entry => new { entry.UserId, entry.RoleId, entry.Scope });
        userGrant.Property(entry => entry.RoleId).UseCollation("C");
        userGrant.Property(entry => entry.Scope).UseCollation("C");
        userGrant.HasOne<VivariumUser>().WithMany().HasForeignKey(entry => entry.UserId).OnDelete(DeleteBehavior.Cascade);
        userGrant.HasOne<TeamCityRole>().WithMany().HasForeignKey(entry => entry.RoleId).OnDelete(DeleteBehavior.Cascade);
        var group = modelBuilder.Entity<TeamCityGroup>();
        group.HasKey(entry => entry.Key);
        group.Property(entry => entry.Key).UseCollation("C");
        var groupGrant = modelBuilder.Entity<GroupRoleGrant>();
        groupGrant.HasKey(entry => new { entry.GroupKey, entry.RoleId, entry.Scope });
        groupGrant.Property(entry => entry.GroupKey).UseCollation("C");
        groupGrant.Property(entry => entry.RoleId).UseCollation("C");
        groupGrant.Property(entry => entry.Scope).UseCollation("C");
        groupGrant.HasOne<TeamCityGroup>().WithMany().HasForeignKey(entry => entry.GroupKey).OnDelete(DeleteBehavior.Cascade);
        groupGrant.HasOne<TeamCityRole>().WithMany().HasForeignKey(entry => entry.RoleId).OnDelete(DeleteBehavior.Cascade);
    }
}
