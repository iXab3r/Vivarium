using Microsoft.EntityFrameworkCore;
using Vivarium.Controller.Identity;
using Vivarium.Controller.Persistence;
using Vivarium.Controller.Security;

namespace Vivarium.Controller.Builds.Runtime;

internal static class RuntimeBuildAccess
{
    // All queue mutations take the permission fence first, then this queue fence, then an Agent
    // fence if assigned. Session/result/output transitions need only that exact Agent fence.
    internal static Task FencePermissionsAsync(VivariumDbContext database, CancellationToken token) =>
        database.Database.ExecuteSqlRawAsync("""
            LOCK TABLE "AspNetUsers", "UserRoleGrants", "RolePermissions", "IncludedRoles", "GroupRoleGrants" IN SHARE MODE
            """, token);
    internal static Task FenceQueueAsync(VivariumDbContext database, CancellationToken token) =>
        // LOCK is a utility statement: a repeatable-read admission takes its first MVCC snapshot
        // only after obtaining this fence. SELECT pg_advisory_xact_lock would snapshot before waiting
        // and could then allocate from a stale counter. Normal counter reads remain unblocked.
        database.Database.ExecuteSqlRawAsync("LOCK TABLE \"RuntimeBuildCounters\" IN EXCLUSIVE MODE", token);

    internal static async Task<string[]> AncestryAsync(VivariumDbContext database, long projectId, CancellationToken token)
    {
        var ancestors = new List<string>(); var visited = new HashSet<long>();
        long? current = projectId;
        while (current is not null)
        {
            if (!visited.Add(current.Value)) throw new InvalidDataException("The current Project ancestry is invalid.");
            var project = await database.CatalogProjects.AsNoTracking().SingleOrDefaultAsync(p => p.Id == current, token)
                ?? throw new InvalidDataException("A retained Build has no current Project identity.");
            ancestors.Add(project.ExternalId); current = project.ParentId;
        }
        if (ancestors.LastOrDefault() != "_Root") throw new InvalidDataException("The current Project root is missing.");
        return ancestors.ToArray();
    }

    internal static Task<bool> AllowsAsync(VivariumDbContext database, IConfiguration configuration,
        ManagementRequestContext actor, IReadOnlyList<string> permissions, IReadOnlyList<string> ancestry, CancellationToken token) =>
        actor.Principal.ActorType == "user" && Guid.TryParse(actor.Principal.ActorId, out var user)
            ? new CurrentAccess(database, configuration).AllowsAllAsync(user, permissions, ancestry, token)
            : Task.FromResult(false);
}
