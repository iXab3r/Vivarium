namespace Vivarium.Controller.Identity;

// TeamCity roles/scopes are application data. Framework authentication claims are not grants.
public sealed class TeamCityRole
{
    public required string Id { get; set; }
    public required string Name { get; set; }
}

public sealed class RolePermission
{
    public required string RoleId { get; set; }
    public required string PermissionId { get; set; }
}

public sealed class IncludedRole
{
    public required string RoleId { get; set; }
    public required string IncludedRoleId { get; set; }
}

public sealed class UserRoleGrant
{
    public Guid UserId { get; set; }
    public required string RoleId { get; set; }
    public required string Scope { get; set; }
}

public sealed class TeamCityGroup
{
    public const string AllUsersKey = "ALL_USERS_GROUP";
    public required string Key { get; set; }
    public required string Name { get; set; }
}

public sealed class GroupRoleGrant
{
    public required string GroupKey { get; set; }
    public required string RoleId { get; set; }
    public required string Scope { get; set; }
}
