using Vivarium.Controller.Configuration.Git;
using Vivarium.Controller.Configuration.Reconciliation;
using Vivarium.Controller.Persistence;
using Vivarium.Controller.Security;

namespace Vivarium.Tests;

[TestFixture]
[NonParallelizable]
public sealed class AuthorizationPolicyTests
{
    private string rootDir = null!;

    [SetUp]
    public void SetUp()
    {
        rootDir = Path.Combine(
            Path.GetTempPath(), "vivarium-authorization-policy-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootDir);
        Directory.CreateDirectory(Path.Combine(rootDir, "data"));
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            Directory.Delete(rootDir, recursive: true);
        }
        catch
        {
            // Best effort: preserve the original failure.
        }
    }

    [Test]
    public async Task Applied_git_user_and_system_administrator_binding_drive_the_shared_evaluator()
    {
        await using var database = new VivariumDatabase(Path.Combine(rootDir, "data"));
        var repository = await ManagedGitRepository.OpenOrCreateAsync(
            Path.Combine(rootDir, "configuration"), "controller");
        var reconciler = new ConfigurationReconciler(database, TimeProvider.System);
        await reconciler.ReconcileAuthoritativeHeadAsync(
            ManagementRequestContext.System("authorization-baseline"),
            "controller",
            repository);

        var head = await repository.GetAuthoritativeHeadAsync();
        var user = await UpsertAsync(
            repository,
            head,
            ".vivarium/rbac/users/user-admin.yaml",
            ConfigurationTreeValidator.RenderUser(
                "user-admin", "admin", "Vivarium Administrator", active: true),
            "authorization-user");
        var binding = await UpsertAsync(
            repository,
            user.ResultRevision!,
            ".vivarium/rbac/bindings/system-admin.yaml",
            ConfigurationTreeValidator.RenderRoleBinding(
                "system-admin",
                "user",
                "user-admin",
                AuthorizationRoleIds.SystemAdministrator,
                "global",
                "global"),
            "authorization-binding");
        var applied = await reconciler.ReconcileAuthoritativeHeadAsync(
            ManagementRequestContext.System("authorization-apply"),
            "controller",
            repository);

        var authorizer = new ManagementAuthorizer(database);
        var principal = new ManagementPrincipal("user", "user-admin", "password-session", null);
        var decisions = Enum.GetValues<ManagementPermission>()
            .Select(permission => authorizer.Evaluate(principal, permission))
            .ToArray();
        var projection = await database.ReadAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT u.login, u.display_name, u.desired_active,
                       b.role_id, b.scope_kind, b.scope_id,
                       u.source_commit, b.source_commit,
                       u.source_revision_set_id, b.source_revision_set_id
                FROM authorization_desired_users u
                JOIN authorization_role_bindings b ON b.principal_id = u.user_id
                WHERE u.user_id = 'user-admin';
                """;
            using var reader = command.ExecuteReader();
            Assert.That(reader.Read(), Is.True);
            return Enumerable.Range(0, 10)
                .Select(index => index == 2
                    ? reader.GetInt64(index).ToString()
                    : reader.GetString(index))
                .ToArray();
        });

        Assert.Multiple(() =>
        {
            Assert.That(user.Outcome, Is.EqualTo(ConfigurationCommitOutcome.Committed));
            Assert.That(binding.Outcome, Is.EqualTo(ConfigurationCommitOutcome.Committed));
            Assert.That(applied.Outcome, Is.EqualTo(ConfigurationReconciliationOutcome.Applied));
            Assert.That(decisions, Has.All.Matches<AuthorizationDecision>(decision => decision.Allowed));
            Assert.That(decisions.Select(decision => decision.RoleId),
                Has.All.EqualTo(AuthorizationRoleIds.SystemAdministrator));
            Assert.That(decisions.Select(decision => decision.AppliedRevisionSetId),
                Has.All.EqualTo(applied.State.Active!.RevisionSetId));
            Assert.That(projection[0], Is.EqualTo("admin"));
            Assert.That(projection[1], Is.EqualTo("Vivarium Administrator"));
            Assert.That(projection[2], Is.EqualTo("1"));
            Assert.That(projection[3], Is.EqualTo(AuthorizationRoleIds.SystemAdministrator));
            Assert.That(projection[4..6], Is.EqualTo(new[] { "global", "global" }));
            Assert.That(projection[6], Is.EqualTo(binding.ResultRevision!.Commit));
            Assert.That(projection[7], Is.EqualTo(binding.ResultRevision.Commit));
            Assert.That(projection[8], Is.EqualTo(applied.State.Active.RevisionSetId));
            Assert.That(projection[9], Is.EqualTo(applied.State.Active.RevisionSetId));
        });

        var inactive = await UpsertAsync(
            repository,
            binding.ResultRevision!,
            ".vivarium/rbac/users/user-admin.yaml",
            ConfigurationTreeValidator.RenderUser(
                "user-admin", "admin", "Vivarium Administrator", active: false),
            "authorization-disable-user");
        await reconciler.ReconcileAuthoritativeHeadAsync(
            ManagementRequestContext.System("authorization-disable-apply"),
            "controller",
            repository);
        var inactiveDecision = authorizer.Evaluate(principal, ManagementPermission.AgentManage);

        Assert.Multiple(() =>
        {
            Assert.That(inactive.Outcome, Is.EqualTo(ConfigurationCommitOutcome.Committed));
            Assert.That(inactiveDecision.Allowed, Is.False);
            Assert.That(inactiveDecision.ReasonCode, Is.EqualTo("principal_inactive"));
        });
    }

    [Test]
    public async Task Role_bundles_and_scope_trees_do_not_cross_project_and_fleet_authority()
    {
        await using var database = new VivariumDatabase(Path.Combine(rootDir, "data"));
        var repository = await ManagedGitRepository.OpenOrCreateAsync(
            Path.Combine(rootDir, "configuration"), "controller");
        var reconciler = new ConfigurationReconciler(database, TimeProvider.System);
        await reconciler.ReconcileAuthoritativeHeadAsync(
            ManagementRequestContext.System("authorization-baseline"), "controller", repository);
        var head = await repository.GetAuthoritativeHeadAsync();
        var projectUser = await UpsertAsync(
            repository,
            head,
            ".vivarium/rbac/users/project-admin.yaml",
            ConfigurationTreeValidator.RenderUser(
                "project-admin", "project.admin", "Project Admin", true),
            "project-user");
        var projectBinding = await UpsertAsync(
            repository,
            projectUser.ResultRevision!,
            ".vivarium/rbac/bindings/project-admin-one.yaml",
            ConfigurationTreeValidator.RenderRoleBinding(
                "project-admin-one", "user", "project-admin",
                AuthorizationRoleIds.ProjectAdministrator, "project", "project-one"),
            "project-binding");
        var fleetUser = await UpsertAsync(
            repository,
            projectBinding.ResultRevision!,
            ".vivarium/rbac/users/fleet-manager.yaml",
            ConfigurationTreeValidator.RenderUser(
                "fleet-manager", "fleet.manager", "Fleet Manager", true),
            "fleet-user");
        await UpsertAsync(
            repository,
            fleetUser.ResultRevision!,
            ".vivarium/rbac/bindings/fleet-manager-root.yaml",
            ConfigurationTreeValidator.RenderRoleBinding(
                "fleet-manager-root", "user", "fleet-manager",
                AuthorizationRoleIds.AgentManager, "fleet", "fleet-root"),
            "fleet-binding");
        await reconciler.ReconcileAuthoritativeHeadAsync(
            ManagementRequestContext.System("authorization-apply"), "controller", repository);

        var authorizer = new ManagementAuthorizer(database);
        var projectPrincipal = new ManagementPrincipal(
            "user", "project-admin", "password-session", null);
        var fleetPrincipal = new ManagementPrincipal(
            "user", "fleet-manager", "password-session", null);

        Assert.Multiple(() =>
        {
            Assert.That(authorizer.Allows(
                    projectPrincipal,
                    ManagementPermission.BuildSubmit,
                    new AuthorizationResource(AuthorizationScopeKind.Project, "project-one")),
                Is.True);
            Assert.That(authorizer.Allows(
                    projectPrincipal,
                    ManagementPermission.BuildSubmit,
                    new AuthorizationResource(AuthorizationScopeKind.Project, "project-two")),
                Is.False);
            Assert.That(authorizer.Allows(projectPrincipal, ManagementPermission.PanelAccess),
                Is.True);
            Assert.That(authorizer.Allows(projectPrincipal, ManagementPermission.AgentList), Is.False);
            Assert.That(authorizer.Allows(fleetPrincipal, ManagementPermission.PanelAccess), Is.True);
            Assert.That(authorizer.Allows(fleetPrincipal, ManagementPermission.AgentList), Is.True);
            Assert.That(authorizer.Allows(fleetPrincipal, ManagementPermission.AgentManage), Is.True);
            Assert.That(authorizer.Allows(fleetPrincipal, ManagementPermission.BuildSubmit), Is.False);
            Assert.That(AuthorizationBuiltInRoles.Contains(
                AuthorizationRoleIds.AgentManager,
                AuthorizationPermissionIds.FleetCommandExecute), Is.False);
            Assert.That(AuthorizationBuiltInRoles.Contains(
                AuthorizationRoleIds.SystemAdministrator,
                AuthorizationPermissionIds.FleetCommandExecute), Is.True);
        });
    }

    [Test]
    public async Task Git_validation_rejects_dangling_duplicate_or_illegally_scoped_authority()
    {
        var repository = await ManagedGitRepository.OpenOrCreateAsync(
            Path.Combine(rootDir, "configuration"), "controller");
        var head = await repository.GetAuthoritativeHeadAsync();
        var dangling = await UpsertAsync(
            repository,
            head,
            ".vivarium/rbac/bindings/dangling.yaml",
            ConfigurationTreeValidator.RenderRoleBinding(
                "dangling", "user", "missing-user",
                AuthorizationRoleIds.SystemAdministrator, "global", "global"),
            "dangling-binding");
        var firstUser = await UpsertAsync(
            repository,
            head,
            ".vivarium/rbac/users/first-user.yaml",
            ConfigurationTreeValidator.RenderUser(
                "first-user", "duplicate", "First User", true),
            "first-user");
        var duplicate = await UpsertAsync(
            repository,
            firstUser.ResultRevision!,
            ".vivarium/rbac/users/second-user.yaml",
            ConfigurationTreeValidator.RenderUser(
                "second-user", "DUPLICATE", "Second User", true),
            "duplicate-user");
        var illegalScope = await UpsertAsync(
            repository,
            firstUser.ResultRevision!,
            ".vivarium/rbac/bindings/illegal-scope.yaml",
            ConfigurationTreeValidator.RenderRoleBinding(
                "illegal-scope", "user", "first-user",
                AuthorizationRoleIds.ProjectAdministrator, "fleet", "fleet-root"),
            "illegal-scope");

        Assert.Multiple(() =>
        {
            Assert.That(dangling.Outcome, Is.EqualTo(ConfigurationCommitOutcome.Rejected));
            Assert.That(dangling.Diagnostics.Select(diagnostic => diagnostic.Code),
                Does.Contain("CONFIG_ROLE_BINDING_PRINCIPAL_NOT_FOUND"));
            Assert.That(duplicate.Outcome, Is.EqualTo(ConfigurationCommitOutcome.Rejected));
            Assert.That(duplicate.Diagnostics.Select(diagnostic => diagnostic.Code),
                Does.Contain("CONFIG_USER_LOGIN_DUPLICATE"));
            Assert.That(illegalScope.Outcome, Is.EqualTo(ConfigurationCommitOutcome.Rejected));
            Assert.That(illegalScope.Diagnostics.Select(diagnostic => diagnostic.Code),
                Does.Contain("CONFIG_ROLE_BINDING_SCOPE_INVALID"));
        });
    }

    private static Task<ConfigurationCommitResult> UpsertAsync(
        ManagedGitRepository repository,
        ConfigurationRevision expected,
        string path,
        ReadOnlyMemory<byte> content,
        string operationId)
    {
        var metadata = new ConfigurationCommitMetadata(
            "Update authorization policy",
            operationId,
            operationId,
            operationId,
            new ConfigurationCommitActor("test-user", "user", "Test User"));
        return repository.UpsertDocumentAsync(
            new ConfigurationDocumentMutation(expected, path, content, metadata));
    }
}
