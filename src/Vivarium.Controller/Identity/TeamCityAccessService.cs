using Microsoft.EntityFrameworkCore;
using Vivarium.Controller.Auditing;
using Vivarium.Controller.Persistence;
using Vivarium.Controller.Security;

namespace Vivarium.Controller.Identity;

public enum AccessMutationResult { Applied, Unchanged, Denied, NotFound }

/// <summary>R0 global role/account operations. Project delegation and group management retain their own due contracts.</summary>
public sealed class TeamCityAccessService(VivariumDbContext database, CurrentAccess access,
    PostgresAuditStore audits, TimeProvider time)
{
    /// <summary>Replace an editable role's direct permissions; no built-in role has a permission floor.</summary>
    public async Task<AccessMutationResult> SetRoleAsync(ManagementRequestContext actor, string roleId,
        string name, IReadOnlyCollection<string> permissions, CancellationToken token = default)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        if (!await AllowsAsync(actor, "manage_roles", token))
            return await DeniedAsync(actor, "role.update", "role", roleId, transaction, token);
        var role = await database.Roles.SingleOrDefaultAsync(role => role.Id == roleId, token);
        if (role is null) database.Roles.Add(new TeamCityRole { Id = roleId, Name = name });
        else role.Name = name;
        var desired = permissions.ToHashSet(StringComparer.Ordinal);
        var existing = await database.RolePermissions.Where(permission => permission.RoleId == roleId).ToArrayAsync(token);
        database.RolePermissions.RemoveRange(existing.Where(permission => !desired.Contains(permission.PermissionId)));
        foreach (var permission in desired.Except(existing.Select(permission => permission.PermissionId), StringComparer.Ordinal))
            database.RolePermissions.Add(new RolePermission { RoleId = roleId, PermissionId = permission });
        audits.Stage(AuditEventDraft.Create(actor, time.GetUtcNow(), "role.update", "role", roleId));
        await database.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return AccessMutationResult.Applied;
    }

    /// <summary>Grant/withdraw a global user assignment. The R0 reference proves both permission paths in its recorded actor context.</summary>
    public async Task<AccessMutationResult> SetGlobalUserRoleAsync(ManagementRequestContext actor, Guid userId,
        string roleId, bool assigned, CancellationToken token = default)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        if (!await AllowsAsync(actor, "change_user", token) && !await AllowsAsync(actor, "manage_roles", token))
            return await DeniedAsync(actor, "user.role.update", "user", userId.ToString(), transaction, token);
        if (!await database.Users.AnyAsync(user => user.Id == userId, token)
            || !await database.Roles.AnyAsync(role => role.Id == roleId, token))
            return await MissingAsync(actor, "user.role.update", "user", userId.ToString(), transaction, token);
        var grant = await database.UserRoleGrants.SingleOrDefaultAsync(
            grant => grant.UserId == userId && grant.RoleId == roleId && grant.Scope == "g", token);
        var changed = assigned != (grant is not null);
        if (assigned && grant is null) database.UserRoleGrants.Add(new UserRoleGrant { UserId = userId, RoleId = roleId, Scope = "g" });
        else if (!assigned && grant is not null) database.UserRoleGrants.Remove(grant);
        audits.Stage(AuditEventDraft.Create(actor, time.GetUtcNow(), "user.role.update", "user", userId.ToString(),
            changed ? AuditOutcome.Succeeded : AuditOutcome.NoChange,
            details: new Dictionary<string, string> { ["roleId"] = roleId, ["scope"] = "g", ["assigned"] = assigned ? "true" : "false" }));
        await database.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return changed ? AccessMutationResult.Applied : AccessMutationResult.Unchanged;
    }

    public async Task<AccessMutationResult> SetOrdinaryUserEnabledAsync(ManagementRequestContext actor, Guid userId,
        bool enabled, CancellationToken token = default)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        if (!await AllowsAsync(actor, "change_user", token))
            return await DeniedAsync(actor, "account.enable", "user", userId.ToString(), transaction, token);
        // Chiral's installation setting is the sole authority for the reserved technical account.
        var user = await database.Users.SingleOrDefaultAsync(user => user.Id == userId
            && user.Id != VivariumIdentityConstants.SuperuserId, token);
        if (user is null) return await MissingAsync(actor, "account.enable", "user", userId.ToString(), transaction, token);
        var changed = user.IsEnabled != enabled;
        user.IsEnabled = enabled;
        audits.Stage(AuditEventDraft.Create(actor, time.GetUtcNow(), "account.enable", "user", userId.ToString(),
            changed ? AuditOutcome.Succeeded : AuditOutcome.NoChange,
            details: new Dictionary<string, string> { ["enabled"] = enabled ? "true" : "false" }));
        await database.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return changed ? AccessMutationResult.Applied : AccessMutationResult.Unchanged;
    }

    private async Task<bool> AllowsAsync(ManagementRequestContext actor, string permission, CancellationToken token) =>
        actor.Principal.ActorType == "user" && Guid.TryParse(actor.Principal.ActorId, out var id)
            && await access.AllowsAsync(id, permission, cancellationToken: token);

    private async Task<AccessMutationResult> MissingAsync(ManagementRequestContext actor, string action,
        string targetType, string targetId, Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction, CancellationToken token)
    {
        audits.Stage(AuditEventDraft.Create(actor, time.GetUtcNow(), action, targetType, targetId,
            AuditOutcome.Failed, "resource_not_found"));
        await database.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return AccessMutationResult.NotFound;
    }

    private async Task<AccessMutationResult> DeniedAsync(ManagementRequestContext actor, string action,
        string targetType, string targetId, Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction, CancellationToken token)
    {
        audits.Stage(AuditEventDraft.Create(actor, time.GetUtcNow(), action, targetType, targetId,
            AuditOutcome.Denied, "permission_denied"));
        await database.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return AccessMutationResult.Denied;
    }
}
