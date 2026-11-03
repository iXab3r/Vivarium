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
        => await AllowsAllAsync(userId, [permissionId], projectAndAncestors, cancellationToken);

    public async Task<bool> AllowsAllAsync(Guid userId, IReadOnlyList<string> permissionIds,
        IReadOnlyList<string>? projectAndAncestors = null, CancellationToken cancellationToken = default)
    {
        // Project identities and ancestry come from the owning active catalogue projection.
        // A transport must never accept a caller-supplied ancestry list as authority.
        var scopes = new HashSet<string>(StringComparer.Ordinal) { "g" };
        if (projectAndAncestors is not null)
            foreach (var project in projectAndAncestors) scopes.Add("p:" + project);
        var scopeValues = scopes.ToArray();
        var permissionValues = permissionIds.ToArray();
        var installationId = VivariumIdentityConstants.SuperuserId;
        var installationEnabled = configuration.GetValue<bool>("Vivarium:Identity:SuperuserEnabled");
        // One PostgreSQL statement snapshot covers eligibility, grants, transitive inclusions and
        // permissions. Separate queries must not combine an old enabled account with a newer grant.
        return await database.Database.SqlQuery<bool>($"""
            WITH RECURSIVE effective_roles("RoleId", "Scope") AS (
                SELECT "RoleId", "Scope" FROM "UserRoleGrants"
                    WHERE "UserId" = {userId}
                UNION
                SELECT "RoleId", "Scope" FROM "GroupRoleGrants"
                    WHERE "GroupKey" = {TeamCityGroup.AllUsersKey}
                UNION
                SELECT edge."IncludedRoleId", inherited."Scope" FROM "IncludedRoles" edge
                    JOIN effective_roles inherited ON inherited."RoleId" = edge."RoleId"
            )
            SELECT EXISTS (
                SELECT 1 FROM "AspNetUsers" account
                WHERE account."Id" = {userId} AND account."IsEnabled"
                    AND (account."Id" <> {installationId} OR {installationEnabled})
                    AND (account."Id" = {installationId} OR NOT EXISTS (
                        SELECT 1 FROM unnest({permissionValues}) requested("PermissionId") WHERE NOT EXISTS (
                            SELECT 1 FROM "RolePermissions" permission JOIN effective_roles role
                                ON role."RoleId" = permission."RoleId"
                            LEFT JOIN "PermissionDefinitions" definition ON definition."Id" = permission."PermissionId"
                            WHERE permission."PermissionId" = requested."PermissionId"
                                AND (role."Scope" = ANY({scopeValues}) OR definition."IsGlobal" IS TRUE)
                        )
                    ))
            ) AS "Value"
            """).SingleAsync(cancellationToken);
    }
}
