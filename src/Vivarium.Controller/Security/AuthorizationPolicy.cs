namespace Vivarium.Controller.Security;

public enum AuthorizationScopeKind
{
    Global,
    Project,
    Fleet,
    Pool,
}

public sealed record AuthorizationResource(
    AuthorizationScopeKind ScopeKind,
    string ScopeId)
{
    public static AuthorizationResource Global { get; } =
        new(AuthorizationScopeKind.Global, "global");

    public static AuthorizationResource ProjectRoot { get; } =
        new(AuthorizationScopeKind.Project, "project-root");

    public static AuthorizationResource FleetRoot { get; } =
        new(AuthorizationScopeKind.Fleet, "fleet-root");
}

public sealed record AuthorizationDecision(
    bool Allowed,
    string PermissionId,
    AuthorizationResource Resource,
    string ReasonCode,
    string? RoleId,
    string? AppliedRevisionSetId);

public static class AuthorizationRoleIds
{
    public const string SystemAdministrator = "SYSTEM_ADMIN";
    public const string ProjectAdministrator = "PROJECT_ADMIN";
    public const string ProjectDeveloper = "PROJECT_DEVELOPER";
    public const string ProjectViewer = "PROJECT_VIEWER";
    public const string AgentManager = "AGENT_MANAGER";

    public static IReadOnlyList<string> BuiltIns { get; } =
    [
        SystemAdministrator,
        ProjectAdministrator,
        ProjectDeveloper,
        ProjectViewer,
        AgentManager,
    ];

    public static bool IsBuiltIn(string roleId) =>
        BuiltIns.Contains(roleId, StringComparer.Ordinal);

    public static bool IsLegalScope(string roleId, AuthorizationScopeKind scopeKind) => roleId switch
    {
        SystemAdministrator => scopeKind == AuthorizationScopeKind.Global,
        ProjectAdministrator or ProjectDeveloper or ProjectViewer =>
            scopeKind is AuthorizationScopeKind.Global or AuthorizationScopeKind.Project,
        AgentManager => scopeKind is AuthorizationScopeKind.Fleet or AuthorizationScopeKind.Pool,
        _ => false,
    };
}

public static class AuthorizationPermissionIds
{
    public const string ProjectView = "project.view";
    public const string ProjectSettingsView = "project.settings.view";
    public const string ProjectSettingsPropose = "project.settings.propose";
    public const string ProjectSettingsApprove = "project.settings.approve";
    public const string ProjectCreate = "project.create";
    public const string ProjectDelete = "project.delete";
    public const string ProjectRolesManage = "project.roles.manage";
    public const string BuildRun = "build.run";
    public const string BuildCancel = "build.cancel";
    public const string BuildForceStop = "build.force-stop";
    public const string BuildQueueManage = "build.queue.manage";
    public const string BuildParametersCustomize = "build.parameters.customize";
    public const string BuildLogView = "build.log.view";
    public const string BuildArtifactView = "build.artifact.view";
    public const string BuildRuntimeSensitiveView = "build.runtime-sensitive.view";
    public const string BuildAgentSummaryView = "build.agent-summary.view";
    public const string ProjectAgentEnable = "project.agent.enable";
    public const string ProjectAgentAuthorize = "project.agent.authorize";
    public const string ProjectAgentRemove = "project.agent.remove";
    public const string ProjectAgentPolicyChange = "project.agent.policy.change";
    public const string FleetSummaryView = "fleet.summary.view";
    public const string FleetInventoryView = "fleet.inventory.view";
    public const string FleetProcessCommandLineView = "fleet.process-commandline.view";
    public const string FleetEnvironmentNamesView = "fleet.environment-names.view";
    public const string FleetEnvironmentValuesView = "fleet.environment-values.view";
    public const string FleetAgentAuthorize = "fleet.agent.authorize";
    public const string FleetAgentEnable = "fleet.agent.enable";
    public const string FleetAgentSuspend = "fleet.agent.suspend";
    public const string FleetAgentManage = "fleet.agent.manage";
    public const string FleetPoolManage = "fleet.pool.manage";
    public const string FleetCommandExecute = "fleet.command.execute";
    public const string FleetProcessControl = "fleet.process.control";
    public const string FleetFilesRead = "fleet.files.read";
    public const string FleetFilesWrite = "fleet.files.write";
    public const string FleetSoftwareManage = "fleet.software.manage";
    public const string FleetAgentPower = "fleet.agent.power";
    public const string FleetAgentSnapshot = "fleet.agent.snapshot";
    public const string UsersManage = "users.manage";
    public const string UsersSuspend = "users.suspend";
    public const string RolesDefine = "roles.define";
    public const string TokensManageAll = "tokens.manage-all";
    public const string GitRepositoryBind = "git.repository.bind";
    public const string GitChangeReconcile = "git.change.reconcile";
    public const string GitPolicyManage = "git.policy.manage";
    public const string AuditView = "audit.view";
    public const string AuditSensitiveView = "audit.sensitive.view";
    public const string ServerManage = "server.manage";

    public static IReadOnlySet<string> Catalog { get; } = new HashSet<string>(
    [
        ProjectView, ProjectSettingsView, ProjectSettingsPropose, ProjectSettingsApprove,
        ProjectCreate, ProjectDelete, ProjectRolesManage, BuildRun, BuildCancel, BuildForceStop,
        BuildQueueManage, BuildParametersCustomize, BuildLogView, BuildArtifactView,
        BuildRuntimeSensitiveView, BuildAgentSummaryView, ProjectAgentEnable,
        ProjectAgentAuthorize, ProjectAgentRemove, ProjectAgentPolicyChange,
        FleetSummaryView, FleetInventoryView, FleetProcessCommandLineView,
        FleetEnvironmentNamesView, FleetEnvironmentValuesView, FleetAgentAuthorize,
        FleetAgentEnable, FleetAgentSuspend, FleetAgentManage, FleetPoolManage,
        FleetCommandExecute, FleetProcessControl, FleetFilesRead, FleetFilesWrite,
        FleetSoftwareManage, FleetAgentPower, FleetAgentSnapshot, UsersManage,
        UsersSuspend, RolesDefine, TokensManageAll, GitRepositoryBind,
        GitChangeReconcile, GitPolicyManage, AuditView, AuditSensitiveView, ServerManage,
    ], StringComparer.Ordinal);
}

internal static class AuthorizationBuiltInRoles
{
    private static readonly IReadOnlySet<string> ProjectViewer = Set(
        AuthorizationPermissionIds.ProjectView,
        AuthorizationPermissionIds.ProjectSettingsView,
        AuthorizationPermissionIds.BuildLogView,
        AuthorizationPermissionIds.BuildArtifactView);

    private static readonly IReadOnlySet<string> ProjectDeveloper = Union(
        ProjectViewer,
        AuthorizationPermissionIds.BuildRun,
        AuthorizationPermissionIds.BuildCancel,
        AuthorizationPermissionIds.BuildQueueManage,
        AuthorizationPermissionIds.BuildParametersCustomize,
        AuthorizationPermissionIds.BuildAgentSummaryView);

    private static readonly IReadOnlySet<string> ProjectAdministrator = Union(
        ProjectDeveloper,
        AuthorizationPermissionIds.ProjectSettingsPropose,
        AuthorizationPermissionIds.ProjectSettingsApprove,
        AuthorizationPermissionIds.BuildForceStop,
        AuthorizationPermissionIds.ProjectCreate,
        AuthorizationPermissionIds.ProjectDelete,
        AuthorizationPermissionIds.ProjectRolesManage,
        AuthorizationPermissionIds.ProjectAgentEnable,
        AuthorizationPermissionIds.ProjectAgentAuthorize,
        AuthorizationPermissionIds.ProjectAgentRemove,
        AuthorizationPermissionIds.ProjectAgentPolicyChange);

    private static readonly IReadOnlySet<string> AgentManager = Set(
        AuthorizationPermissionIds.FleetSummaryView,
        AuthorizationPermissionIds.FleetInventoryView,
        AuthorizationPermissionIds.FleetEnvironmentNamesView,
        AuthorizationPermissionIds.FleetAgentAuthorize,
        AuthorizationPermissionIds.FleetAgentEnable,
        AuthorizationPermissionIds.FleetAgentManage,
        AuthorizationPermissionIds.FleetPoolManage);

    public static bool Contains(string roleId, string permissionId) => roleId switch
    {
        AuthorizationRoleIds.SystemAdministrator =>
            AuthorizationPermissionIds.Catalog.Contains(permissionId),
        AuthorizationRoleIds.ProjectAdministrator => ProjectAdministrator.Contains(permissionId),
        AuthorizationRoleIds.ProjectDeveloper => ProjectDeveloper.Contains(permissionId),
        AuthorizationRoleIds.ProjectViewer => ProjectViewer.Contains(permissionId),
        AuthorizationRoleIds.AgentManager => AgentManager.Contains(permissionId),
        _ => false,
    };

    private static IReadOnlySet<string> Set(params string[] permissions) =>
        new HashSet<string>(permissions, StringComparer.Ordinal);

    private static IReadOnlySet<string> Union(
        IReadOnlySet<string> inherited,
        params string[] permissions)
    {
        var result = new HashSet<string>(inherited, StringComparer.Ordinal);
        result.UnionWith(permissions);
        return result;
    }
}
