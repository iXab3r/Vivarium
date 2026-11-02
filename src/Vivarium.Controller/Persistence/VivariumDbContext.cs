using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Vivarium.Controller.Identity;

namespace Vivarium.Controller.Persistence;

/// <summary>PostgreSQL application authority for the fresh D34 installation; no Git identity projection.</summary>
public sealed class VivariumDbContext(DbContextOptions<VivariumDbContext> options)
    : IdentityDbContext<VivariumUser, IdentityRole<Guid>, Guid>(options)
{
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
    }
}
