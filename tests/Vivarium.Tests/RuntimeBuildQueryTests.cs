using Microsoft.EntityFrameworkCore;
using Vivarium.Controller.Builds.Runtime;
using Vivarium.Controller.Persistence;

namespace Vivarium.Tests;

public sealed class RuntimeBuildQueryTests
{
    [Test]
    public void Resolved_dependency_handles_support_server_side_filters_and_execution_joins()
    {
        using var database = new VivariumDbContext(new DbContextOptionsBuilder<VivariumDbContext>()
            .UseNpgsql("Host=127.0.0.1;Database=unused;Username=unused").Options);
        var query = RuntimeBuildService.DependencyHandles(database, 1).Where(d => d.Kind == "SNAPSHOT")
            .Join(database.RuntimeBuilds, d => d.Id, b => b.Id, (d, b) => b).OrderBy(b => b.Id);
        Assert.That(() => query.ToQueryString(), Throws.Nothing, "Public reads and scheduling must translate to SQL, not fail after admission commits.");
    }
}
