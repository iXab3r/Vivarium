using Microsoft.EntityFrameworkCore;
using Vivarium.Controller.Persistence;
using Vivarium.Controller.Security;

namespace Vivarium.Controller.Identity;

public sealed record AccessUser(Guid Id, long TeamCityId, string? UserName, string? DisplayName, bool IsEnabled, bool IsInstallation);
public sealed record AccessRole(string Id, string Name, string[] Permissions, string[] IncludedRoles);
public sealed record AccessGrant(string RoleId, string Scope);
public sealed record AccessPage<T>(T[] Items, long Total, int Offset, int Limit);
public sealed class AccessPermissionException() : Exception("Global profile-read or user-management permission is required.");

/// <summary>Authorized current PG reads shared by the management projections.</summary>
public sealed class AccessQueries(VivariumDbContext database, CurrentAccess access)
{
    public async Task<bool> CanReadProfilesAsync(ManagementRequestContext actor, CancellationToken token = default) =>
        actor.Principal.ActorType == "user" && Guid.TryParse(actor.Principal.ActorId, out var id)
        && (await access.AllowsAsync(id, "view_user_profile", cancellationToken: token)
            || await access.AllowsAsync(id, "change_user", cancellationToken: token));

    public async Task<AccessPage<AccessUser>> UsersAsync(ManagementRequestContext actor, int offset, int limit,
        CancellationToken token = default)
    {
        if (!await CanReadProfilesAsync(actor, token)) throw new AccessPermissionException();
        var users = database.Users.AsNoTracking();
        var total = await users.LongCountAsync(token);
        var rows = await users.OrderBy(user => user.TeamCityId).Skip(offset).Take(limit).Select(user =>
            new AccessUser(user.Id, user.TeamCityId, user.UserName, user.DisplayName, user.IsEnabled,
                user.Id == VivariumIdentityConstants.SuperuserId)).ToArrayAsync(token);
        return new(rows, total, offset, limit);
    }

    public async Task<AccessUser?> UserAsync(ManagementRequestContext actor, Guid userId, CancellationToken token = default)
    {
        if (!await CanReadProfilesAsync(actor, token)) throw new AccessPermissionException();
        return await database.Users.AsNoTracking().Where(user => user.Id == userId).Select(user =>
            new AccessUser(user.Id, user.TeamCityId, user.UserName, user.DisplayName, user.IsEnabled,
                user.Id == VivariumIdentityConstants.SuperuserId)).SingleOrDefaultAsync(token);
    }

    public async Task<AccessPage<AccessRole>> RolesAsync(ManagementRequestContext actor, int offset, int limit,
        CancellationToken token = default)
    {
        if (!await CanReadProfilesAsync(actor, token)) throw new AccessPermissionException();
        var total = await database.Roles.LongCountAsync(token);
        var roles = await database.Roles.AsNoTracking().OrderBy(role => role.Id).Skip(offset).Take(limit).ToArrayAsync(token);
        var ids = roles.Select(role => role.Id).ToArray();
        var permissions = await database.RolePermissions.AsNoTracking().Where(permission => ids.Contains(permission.RoleId))
            .OrderBy(permission => permission.PermissionId).ToArrayAsync(token);
        var included = await database.IncludedRoles.AsNoTracking().Where(edge => ids.Contains(edge.RoleId))
            .OrderBy(edge => edge.IncludedRoleId).ToArrayAsync(token);
        var items = roles.Select(role => new AccessRole(role.Id, role.Name,
            permissions.Where(permission => permission.RoleId == role.Id).Select(permission => permission.PermissionId).ToArray(),
            included.Where(edge => edge.RoleId == role.Id).Select(edge => edge.IncludedRoleId).ToArray())).ToArray();
        return new(items, total, offset, limit);
    }

    public async Task<AccessPage<AccessGrant>?> UserRolesAsync(ManagementRequestContext actor, Guid userId, int offset,
        int limit, string? scope = null, string[]? roleIds = null, CancellationToken token = default)
    {
        var self = actor.Principal.ActorType == "user" && Guid.TryParse(actor.Principal.ActorId, out var id)
            && id == userId && await access.FindEligibleAsync(id, token) is not null;
        if (!self && !await CanReadProfilesAsync(actor, token)) throw new AccessPermissionException();
        if (!await database.Users.AnyAsync(user => user.Id == userId, token)) return null;
        var grants = database.UserRoleGrants.AsNoTracking().Where(grant => grant.UserId == userId);
        if (scope is not null) grants = grants.Where(grant => grant.Scope == scope);
        if (roleIds is { Length: > 0 }) grants = grants.Where(grant => roleIds.Contains(grant.RoleId));
        var total = await grants.LongCountAsync(token);
        var items = await grants.OrderBy(grant => grant.RoleId).ThenBy(grant => grant.Scope).Skip(offset).Take(limit)
            .Select(grant => new AccessGrant(grant.RoleId, grant.Scope)).ToArrayAsync(token);
        return new(items, total, offset, limit);
    }
}
