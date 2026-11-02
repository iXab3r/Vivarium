using Microsoft.EntityFrameworkCore;
using Vivarium.Controller.Persistence;

namespace Vivarium.Controller.Identity;

/// <summary>The R0 direct/All Users contribution; rights are read from current PG data, never credential roles.</summary>
public sealed class CurrentAccess(VivariumDbContext database, IConfiguration configuration)
{
    public async Task<VivariumUser?> FindEligibleAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await database.Users.AsNoTracking().SingleOrDefaultAsync(user => user.Id == userId, cancellationToken);
        return user is { IsEnabled: true }
            && (user.Id != VivariumIdentityConstants.SuperuserId
                || configuration.GetValue<bool>("Vivarium:Identity:SuperuserEnabled")) ? user : null;
    }

    public async Task<bool> AllowsAsync(Guid userId, string permissionId,
        IReadOnlyList<string>? projectAndAncestors = null, CancellationToken cancellationToken = default)
    {
        var user = await FindEligibleAsync(userId, cancellationToken);
        if (user is null) return false;
        if (user.Id == VivariumIdentityConstants.SuperuserId)
            return true;
        // Project identities and ancestry come from the owning active catalogue projection.
        // A transport must never accept a caller-supplied ancestry list as authority.
        var scopes = new HashSet<string>(StringComparer.Ordinal) { "g" };
        if (projectAndAncestors is not null)
            foreach (var project in projectAndAncestors) scopes.Add("p:" + project);
        var userGrants = await database.UserRoleGrants.AsNoTracking().Where(grant => grant.UserId == userId)
            .Select(grant => new { grant.RoleId, grant.Scope }).ToArrayAsync(cancellationToken);
        var allUsersGrants = await database.GroupRoleGrants.AsNoTracking()
            .Where(grant => grant.GroupKey == TeamCityGroup.AllUsersKey)
            .Select(grant => new { grant.RoleId, grant.Scope }).ToArrayAsync(cancellationToken);
        var roles = userGrants.Concat(allUsersGrants).Where(grant => scopes.Contains(grant.Scope))
            .Select(grant => grant.RoleId).ToHashSet(StringComparer.Ordinal);
        var pending = new Queue<string>(roles);
        while (pending.TryDequeue(out var role))
        {
            var included = await database.IncludedRoles.AsNoTracking().Where(edge => edge.RoleId == role)
                .Select(edge => edge.IncludedRoleId).ToArrayAsync(cancellationToken);
            foreach (var id in included)
                if (roles.Add(id)) pending.Enqueue(id);
        }
        return await database.RolePermissions.AsNoTracking()
            .AnyAsync(permission => roles.Contains(permission.RoleId) && permission.PermissionId == permissionId, cancellationToken);
    }
}
