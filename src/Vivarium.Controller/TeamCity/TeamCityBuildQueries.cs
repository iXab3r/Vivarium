using System.Data;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Vivarium.Controller.Builds.Runtime;
using Vivarium.Controller.Persistence;
using Vivarium.Controller.Security;

namespace Vivarium.Controller.TeamCity;

/// <summary>A compatible read model over the same Build, current access and immutable execution evidence.</summary>
internal sealed class TeamCityBuildQueries(IDbContextFactory<VivariumDbContext> databases, RuntimeBuildService builds)
{
    internal async Task<JsonObject> GetAsync(ManagementRequestContext actor, long id, string origin, bool full, CancellationToken token)
    {
        await using var database = await databases.CreateDbContextAsync(token);
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        var build = await builds.VisibleAsync(database, actor, id, token, discloseDenied: true) ?? throw RuntimeBuildService.Missing();
        id = build.Id;
        var type = await database.CatalogBuildTypes.AsNoTracking().SingleAsync(t => t.Id == build.BuildTypeId, token);
        var project = await database.CatalogProjects.AsNoTracking().SingleAsync(p => p.Id == build.ProjectId, token);
        var execution = await database.RuntimeBuildExecutions.AsNoTracking().SingleOrDefaultAsync(e => e.BuildId == id, token);
        var state = build.State == "RELEASING" ? "running" : build.State.ToLowerInvariant();
        var href = "/app/rest/" + (state == "queued" ? "buildQueue" : "builds") + "/id:" + id;
        var result = new JsonObject { ["id"] = id, ["buildTypeId"] = type.ExternalId, ["state"] = state,
            ["href"] = href, ["webUrl"] = origin + (state == "queued" ? "/build/" + id : "/buildConfiguration/" + Uri.EscapeDataString(type.ExternalId) + "/" + id),
            ["failedToStart"] = build.FailedToStart, ["personal"] = false, ["queuedDate"] = TeamCityProtocol.Date(build.QueuedAt) };
        if (build.Number is not null) result["number"] = build.Number;
        if (state != "queued")
        {
            result["status"] = build.Status;
            result["statusText"] = build.CancelRequestedAt is not null
                ? build.StatusText.StartsWith("Canceled (", StringComparison.Ordinal) ? build.StatusText : "Canceled"
                : state == "running" && build.StatusText is "Preparing execution" or "Starting execution" ? "Running" : build.StatusText;
        }
        if (state == "queued")
        {
            result["queuePosition"] = await database.RuntimeBuilds.CountAsync(b => b.State == "QUEUED"
                && (b.QueuedAt < build.QueuedAt || b.QueuedAt == build.QueuedAt && b.Id <= id), token);
            result["waitReason"] = build.StatusText;
        }
        // Exact background estimates are separately owned by SS-RUN@R2 (#166, D46).
        if (state == "running") result["percentageComplete"] = build.CancelRequestedAt is null ? -1 : 0;
        if (build.StartedAt is { } started) result["startDate"] = TeamCityProtocol.Date(started);
        if (build.FinishedAt is { } finished) result["finishDate"] = TeamCityProtocol.Date(finished);
        if (build.CancelRequestedAt is { } cancelled)
        {
            var cancelInfo = new JsonObject { ["timestamp"] = TeamCityProtocol.Date(cancelled) };
            if (!string.IsNullOrEmpty(build.CancelReason)) cancelInfo["text"] = build.CancelReason;
            if (full && build.CancelActorId is { } cancelActor)
            {
                var user = await database.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == cancelActor, token);
                if (user is not null) cancelInfo["user"] = TeamCityEndpoints.User(user, href: true);
            }
            result["canceledInfo"] = cancelInfo;
        }
        if (execution is not null)
        {
            using var selection = JsonDocument.Parse(execution.SelectionSnapshotJson);
            var selected = selection.RootElement;
            result["agent"] = new JsonObject { ["id"] = selected.GetProperty("teamCityId").GetInt32(),
                ["name"] = selected.GetProperty("desiredName").GetString() ?? selected.GetProperty("reportedName").GetString() };
        }
        else if (state == "finished" && build.CancelRequestedAt is not null)
        {
            result["agent"] = new JsonObject { ["name"] = "N/A", ["typeId"] = 0 };
            result["startDate"] = TeamCityProtocol.Date(build.CancelRequestedAt.Value);
        }
        var dependencies = await RuntimeBuildService.SelectionViewsAsync(database, id, token);
        var sourceInput = await database.RuntimeBuildInputs.AsNoTracking().SingleAsync(i => i.BuildId == id, token);
        if (sourceInput.SourceJson is not null)
        {
            var source = JsonSerializer.Deserialize<Vivarium.Definitions.Source>(sourceInput.SourceJson, Catalog.DefinitionValidation.Json)!;
            var selected = await database.RuntimeBuildSourceSelections.AsNoTracking().SingleOrDefaultAsync(s => s.BuildId == id, token);
            var revisions = new JsonArray();
            if (selected is not null)
            {
                var revision = new JsonObject { ["version"] = selected.Version, ["internalVersion"] = selected.Version,
                    ["vcs-root-instance"] = new JsonObject { ["id"] = sourceInput.VcsRootInstanceId!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), ["vcs-root-id"] = source.RootId } };
                if (selected.Version != "N/A") revision["vcsBranchName"] = source.Branch;
                revisions.Add(revision);
            }
            result["revisions"] = new JsonObject { ["count"] = revisions.Count, ["revision"] = revisions };
        }
        foreach (var kind in new[] { "SNAPSHOT", "ARTIFACT" })
        {
            var selected = dependencies.Where(d => d.Kind == kind).Select(d => d.Build).ToArray();
            if (selected.Length == 0) continue; // Preserve ordinary R0 representations unchanged.
            var items = new JsonArray();
            foreach (var dependency in selected)
            {
                var item = new JsonObject { ["id"] = dependency.Id, ["buildTypeId"] = dependency.ConfigurationId, ["state"] = dependency.State };
                if (dependency.State != "queued") item["status"] = dependency.Status;
                items.Add(item);
            }
            result[kind == "SNAPSHOT" ? "snapshot-dependencies" : "artifact-dependencies"] =
                new JsonObject { ["count"] = selected.Length, ["build"] = items };
        }
        if (full)
        {
            result.Remove("failedToStart"); result.Remove("personal"); result.Remove("queuePosition");
            result["buildType"] = new JsonObject { ["id"] = type.ExternalId, ["name"] = type.Name,
                ["projectName"] = project.Name, ["projectId"] = project.ExternalId,
                ["href"] = "/app/rest/buildTypes/id:" + Uri.EscapeDataString(type.ExternalId),
                ["webUrl"] = origin + "/buildConfiguration/" + Uri.EscapeDataString(type.ExternalId) + "?mode=builds" };
            var trigger = new JsonObject { ["type"] = "user", ["date"] = TeamCityProtocol.Date(build.QueuedAt) };
            var user = await database.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == build.ActorId, token);
            if (user is not null) trigger["user"] = TeamCityEndpoints.User(user, href: true);
            result["triggered"] = trigger;
            // R0 admits source-free configurations only. These links identify the same numeric Build.
            result["changes"] = new JsonObject { ["href"] = "/app/rest/changes?locator=build:(id:" + id + ")" };
            if (sourceInput.SourceJson is null) result["revisions"] = new JsonObject { ["count"] = 0 };
            if (state == "queued") result["compatibleAgents"] = new JsonObject { ["href"] = "/app/rest/agents?locator=compatible:(build:(id:" + id + "))" };
            result["artifacts"] = new JsonObject { ["href"] = "/app/rest/builds/id:" + id + "/artifacts/children/" };
            result["vcsLabels"] = new JsonArray(); result["customization"] = new JsonObject();
            if (state == "finished")
            {
                result["relatedIssues"] = new JsonObject { ["href"] = "/app/rest/builds/id:" + id + "/relatedIssues" };
                result["statistics"] = new JsonObject { ["href"] = "/app/rest/builds/id:" + id + "/statistics" };
                if (execution is null && build.CancelRequestedAt is not null && build.FinishedAt is { } cancelledAt)
                    result["finishOnAgentDate"] = TeamCityProtocol.Date(cancelledAt);
            }
        }
        return result;
    }
}
