using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Vivarium.Controller.Persistence;

/// <summary>Schema generation only; the running Host always resolves its Chiral database setting.</summary>
public sealed class VivariumDesignTimeFactory : IDesignTimeDbContextFactory<VivariumDbContext>
{
    public VivariumDbContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<VivariumDbContext>()
            .UseNpgsql("Host=127.0.0.1;Database=vivarium-design-only;Username=unused").Options);
}
