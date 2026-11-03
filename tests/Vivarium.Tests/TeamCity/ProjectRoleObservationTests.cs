using System.Text.Json;

namespace Vivarium.Tests.TeamCity;

[Category("TeamCityReplayControl")]
public sealed class ProjectRoleObservationTests
{
    [Test]
    public void Reference_observes_global_permission_characteristics_and_bounded_project_delegation_without_extra_actor_grants()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory,
            "TeamCity/Reference/project-role-observations.json")));
        var root = document.RootElement;
        Assert.That(root.GetProperty("complete").GetBoolean(), Is.True);
        Assert.That(root.GetProperty("scopedAuthenticationSettings").GetProperty("perProjectPermissions").GetBoolean(), Is.True);
        Assert.That(root.GetProperty("currentAllUsersRoles").GetArrayLength(), Is.Zero);
        var grants = root.GetProperty("actorRoles");
        Assert.That(grants.GetArrayLength(), Is.EqualTo(1));
        Assert.That(grants[0].GetProperty("roleId").GetString(), Is.EqualTo("PROJECT_ADMIN"));
        Assert.That(grants[0].GetProperty("scope").GetString(), Is.EqualTo("p:" + root.GetProperty("project").GetString()));
        var observations = root.GetProperty("observations").EnumerateArray().ToDictionary(v => v.GetProperty("id").GetString()!, v => v);
        var expected = new Dictionary<string, int> { ["project-admin-role-read"] = 200, ["project-admin-target-role-read"] = 200,
            ["project-admin-grants-own-project"] = 200, ["project-admin-grants-other-project"] = 403,
            ["project-admin-grants-global"] = 403, ["project-admin-grants-same-admin"] = 200,
            ["project-admin-grants-system-admin"] = 403, ["project-admin-grants-agent-manager"] = 403,
            ["project-admin-revokes-own-project"] = 204, ["narrow-delegator-grants-self-role"] = 200,
            ["narrow-delegator-grants-unheld-project-permission"] = 403 };
        foreach (var (id, status) in expected) Assert.That(observations[id].GetProperty("status").GetInt32(), Is.EqualTo(status), id);
        var permissions = root.GetProperty("permissionMetadata").EnumerateArray()
            .SelectMany(g => g.GetProperty("entity").GetProperty("permissions").GetProperty("permission").EnumerateArray())
            .GroupBy(p => p.GetProperty("id").GetString()!).ToDictionary(g => g.Key, g => g.First());
        Assert.That(permissions["view_user_profile"].GetProperty("global").GetBoolean(), Is.True);
        Assert.That(permissions["authorize_agent"].GetProperty("global").GetBoolean(), Is.True);
        Assert.That(permissions["view_project"].GetProperty("global").GetBoolean(), Is.False);
        Assert.That(permissions["run_build"].GetProperty("global").GetBoolean(), Is.False);
        var deniedRoles = observations["narrow-delegator-grants-unheld-project-permission"].GetProperty("roles").EnumerateArray()
            .Select(r => r.GetProperty("roleId").GetString()).ToArray();
        Assert.That(deniedRoles, Does.Not.Contain(root.GetProperty("runOnlyRole").GetProperty("id").GetString()));
        foreach (var name in new[] { "failure", "cleanupFailure", "userCleanupFailure", "resourceCleanupFailure", "authenticationRestoreFailure",
            "allUsersRestoreFailure", "gateRestoreFailure", "customRoleCleanupFailure" })
            Assert.That(root.TryGetProperty(name, out _), Is.False, name);
        Assert.That(root.GetProperty("authenticationSettingsAfter").GetRawText(), Is.EqualTo(root.GetProperty("authenticationSettings").GetRawText()));
        Assert.That(root.GetProperty("allUsersRolesAfter").GetRawText(), Is.EqualTo(root.GetProperty("allUsersRolesBefore").GetRawText()));
    }
}
