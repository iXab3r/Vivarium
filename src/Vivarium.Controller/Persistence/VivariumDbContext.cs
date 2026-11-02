using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Vivarium.Controller.Identity;
using Vivarium.Controller.Auditing;

namespace Vivarium.Controller.Persistence;

/// <summary>PostgreSQL application authority for the fresh D34 installation; no Git identity projection.</summary>
public sealed class VivariumDbContext(DbContextOptions<VivariumDbContext> options)
    : IdentityUserContext<VivariumUser, Guid>(options)
{
    public DbSet<PersistentAuditEvent> AuditEvents => Set<PersistentAuditEvent>();
    public DbSet<TeamCityRole> Roles => Set<TeamCityRole>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<IncludedRole> IncludedRoles => Set<IncludedRole>();
    public DbSet<UserRoleGrant> UserRoleGrants => Set<UserRoleGrant>();
    public DbSet<TeamCityGroup> Groups => Set<TeamCityGroup>();
    public DbSet<GroupRoleGrant> GroupRoleGrants => Set<GroupRoleGrant>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
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
