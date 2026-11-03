using Microsoft.EntityFrameworkCore;
using Vivarium.Controller.Auditing;
using Vivarium.Controller.Persistence;
using Vivarium.Controller.Security;
using Vivarium.Controller.Catalog;
using Vivarium.Controller.Rest.Common;
using System.Data;

namespace Vivarium.Controller.Identity;

public enum AccessMutationResult { Applied, Unchanged, Denied, NotFound }

/// <summary>Current authorized/audited global and project user grants; group management retains its own due contract.</summary>
public sealed class TeamCityAccessService(VivariumDbContext database, CurrentAccess access,
    PostgresAuditStore audits, TimeProvider time, CatalogQueries? catalogue = null)
{
    /// <summary>Replace an editable role's direct permissions; no built-in role has a permission floor.</summary>
    public async Task<AccessMutationResult> SetRoleAsync(ManagementRequestContext actor, string roleId,
        string name, IReadOnlyCollection<string> permissions, CancellationToken token = default)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        await FenceAsync(token);
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
        await FenceAsync(token);
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
        await FenceAsync(token);
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

    public async Task<AccessMutationResult> SetProjectUserRoleAsync(ManagementRequestContext actor, Guid userId,
        string roleId, string projectId, bool assigned, CancellationToken token = default)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        await FenceAsync(token);
        var snapshot = catalogue is null ? null : await catalogue.ActiveAsync(token);
        if (snapshot is null) throw new RestApiException(503, "catalogue_unavailable", "The catalogue is unavailable",
            "Activate the Project catalogue before assigning project roles.");
        if (projectId != "_Root" && !snapshot.ProjectIds.ContainsKey(projectId))
            return await MissingAsync(actor, "user.role.update", "user", userId.ToString(), transaction, token);
        var global = await AllowsAsync(actor, "change_user", token) || await AllowsAsync(actor, "manage_roles", token);
        if (!global)
        {
            if (actor.Principal.ActorType != "user" || !Guid.TryParse(actor.Principal.ActorId, out var actorId)
                || !await access.AllowsAsync(actorId, "change_user_roles_in_project", snapshot.Ancestry(projectId), token))
                return await DeniedAsync(actor, "user.role.update", "user", userId.ToString(), transaction, token);
            // The pinned reference also rejects a narrow project delegator granting an unheld
            // non-global permission, and Project Admin granting System Admin/Agent Manager.
            var permissions = await database.Database.SqlQuery<string>($"""
                WITH RECURSIVE roles("Id") AS (
                    SELECT "Id" FROM "Roles" WHERE "Id" = {roleId}
                    UNION SELECT edge."IncludedRoleId" FROM "IncludedRoles" edge JOIN roles r ON edge."RoleId" = r."Id"
                ) SELECT DISTINCT permission."PermissionId" AS "Value"
                    FROM "RolePermissions" permission JOIN roles r ON permission."RoleId" = r."Id"
                """).ToArrayAsync(token);
            if (!await access.AllowsAllAsync(actorId, permissions, snapshot.Ancestry(projectId), token))
                return await DeniedAsync(actor, "user.role.update", "user", userId.ToString(), transaction, token);
        }
        if (!await database.Users.AnyAsync(user => user.Id == userId, token)
            || !await database.Roles.AnyAsync(role => role.Id == roleId, token))
            return await MissingAsync(actor, "user.role.update", "user", userId.ToString(), transaction, token);
        var scope = "p:" + projectId;
        var grant = await database.UserRoleGrants.SingleOrDefaultAsync(g => g.UserId == userId && g.RoleId == roleId && g.Scope == scope, token);
        var changed = assigned != (grant is not null);
        if (assigned && grant is null) database.UserRoleGrants.Add(new() { UserId = userId, RoleId = roleId, Scope = scope });
        else if (!assigned && grant is not null) database.UserRoleGrants.Remove(grant);
        audits.Stage(AuditEventDraft.Create(actor, time.GetUtcNow(), "user.role.update", "user", userId.ToString(),
            changed ? AuditOutcome.Succeeded : AuditOutcome.NoChange,
            details: new Dictionary<string, string> { ["roleId"] = roleId, ["scope"] = scope, ["assigned"] = assigned ? "true" : "false" }));
        await database.SaveChangesAsync(token); await transaction.CommitAsync(token);
        return changed ? AccessMutationResult.Applied : AccessMutationResult.Unchanged;
    }

    // Serialize privilege mutations before taking a read snapshot. A SHARE fence followed by a
    // write upgrade could deadlock two role edits; this mode also fences protected admissions.
    private Task FenceAsync(CancellationToken token) => database.Database.ExecuteSqlRawAsync("""
        LOCK TABLE "AspNetUsers", "UserRoleGrants", "RolePermissions", "IncludedRoles", "GroupRoleGrants" IN SHARE ROW EXCLUSIVE MODE
        """, token);

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
