using System.Data;
using System.Text.Json.Nodes;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vivarium.Controller.Catalog;
using Vivarium.Controller.Identity;
using Vivarium.Controller.Persistence;
using Vivarium.Controller.Rest.Common;
using Vivarium.Controller.Security;

namespace Vivarium.Controller.TeamCity;

internal sealed class TeamCityAgentService(IDbContextFactory<VivariumDbContext> databases, IConfiguration configuration, CatalogService catalog)
{
    internal async Task<JsonObject> GetEnabledAsync(ManagementRequestContext actor, int id, CancellationToken token)
    {
        await using var database = await databases.CreateDbContextAsync(token);
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        var access = new CurrentAccess(database, configuration);
        var user = Guid.Parse(actor.Principal.ActorId);
        var permitted = await access.AllowsAsync(user, "view_agent_details", cancellationToken: token);
        if (!permitted)
        {
            // The initial Default pool is associated with every active Project. Preserve the
            // source's project-scoped Agent read without changing native global fleet visibility.
            var reads = new CatalogQueries(database, configuration, access);
            var snapshot = await reads.ActiveAsync(token);
            foreach (var project in snapshot?.Definition.Projects ?? [])
                if (await access.AllowsAsync(user, "view_agent_details_for_project", snapshot!.Ancestry(project.Control.Id), token))
                { permitted = true; break; }
        }
        if (!permitted) throw new AccessPermissionException();
        var agent = await database.AgentRegistrations.AsNoTracking().SingleOrDefaultAsync(a => a.TeamCityId == id && a.DeletedAt == null, token)
            ?? throw Missing();
        return View(agent.DesiredEnabled, agent.EnabledComment, agent.EnabledChangedAt);
    }

    private static JsonObject View(bool status, string? text, DateTimeOffset? changedAt)
    {
        var result = new JsonObject { ["status"] = status };
        if (changedAt is { } changed)
        {
            var comment = new JsonObject { ["timestamp"] = TeamCityProtocol.Date(changed) };
            if (text is not null) comment["text"] = text;
            result["comment"] = comment;
        }
        return result;
    }

    internal async Task<JsonObject> SetEnabledAsync(ManagementRequestContext actor, int id, bool enabled, string comment, CancellationToken token)
    {
        await using var database = await databases.CreateDbContextAsync(token);
        var agent = await database.AgentRegistrations.AsNoTracking().SingleOrDefaultAsync(a => a.TeamCityId == id && a.DeletedAt == null, token)
            ?? throw Missing();
        CatalogChange operation;
        try { operation = await catalog.SetAgentEnabledAsync(actor, agent.AgentId, enabled, comment, token); }
        catch (AccessPermissionException)
        {
            throw new TeamCityPermissionException("You do not have \"Enable / disable agents associated with project\" permission for pool 'Default'");
        }
        if (operation.FailureCode == "permission_denied")
            throw new TeamCityPermissionException("You do not have \"Enable / disable agents associated with project\" permission for pool 'Default'");
        if (operation.State != "APPLIED") throw new RestApiException(409, "configuration_not_applied", "Configuration not applied", "The Agent setting was not applied");
        var applied = JsonSerializer.Deserialize<CatalogDefinition>(operation.MaterializedJson!, DefinitionValidation.Json)!.Agents!
            .Single(a => a.Id == agent.AgentId);
        return View(applied.Enabled, applied.Comment, applied.ChangedAt);
    }

    private static RestApiException Missing() => new(404, "resource_not_found", "Agent not found", "The Agent is not found");
}
