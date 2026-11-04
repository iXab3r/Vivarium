using System.Text.Json.Nodes;
using Vivarium.Controller.Catalog;
using Vivarium.Controller.Rest.Common;
using Vivarium.Controller.Security;

namespace Vivarium.Controller.TeamCity;

internal sealed class TeamCityCatalogQueries(CatalogQueries catalog)
{
    internal async Task<JsonObject> ProjectAsync(ManagementRequestContext actor, string id, string origin, CancellationToken token)
    {
        var snapshot = await ActiveAsync(token);
        var project = snapshot.Definition.Projects.SingleOrDefault(p => p.Control.Id == id) ?? throw Missing("Project", id);
        await RequireVisibleAsync(actor, snapshot, id, token);
        var result = new JsonObject { ["id"] = id, ["name"] = project.Control.Name, ["parentProjectId"] = project.Control.Parent,
            ["archived"] = false, ["href"] = "/app/rest/projects/id:" + Uri.EscapeDataString(id),
            ["webUrl"] = origin + "/project/" + Uri.EscapeDataString(id) };
        if (!string.IsNullOrEmpty(project.Control.Description)) result["description"] = project.Control.Description;
        return result;
    }

    internal async Task<JsonObject> ConfigurationAsync(ManagementRequestContext actor, string id, string origin, CancellationToken token)
    {
        var snapshot = await ActiveAsync(token);
        var selected = snapshot.Definition.Projects.SelectMany(p => p.Definition.Configurations.Select(c => (Project: p, Configuration: c)))
            .SingleOrDefault(pair => pair.Configuration.Id == id);
        if (selected.Configuration is null) throw Missing("Build configuration", id);
        await RequireVisibleAsync(actor, snapshot, selected.Project.Control.Id, token);
        return new JsonObject { ["id"] = id, ["name"] = selected.Configuration.Name,
            ["projectId"] = selected.Project.Control.Id, ["projectName"] = selected.Project.Control.Name, ["paused"] = false,
            ["href"] = "/app/rest/buildTypes/id:" + Uri.EscapeDataString(id),
            ["webUrl"] = origin + "/buildConfiguration/" + Uri.EscapeDataString(id) + "?mode=builds" };
    }

    private async Task<CatalogSnapshot> ActiveAsync(CancellationToken token) => await catalog.ActiveAsync(token)
        ?? throw new RestApiException(503, "catalogue_unavailable", "Catalogue unavailable", "The catalogue is unavailable");
    private async Task RequireVisibleAsync(ManagementRequestContext actor, CatalogSnapshot snapshot, string id, CancellationToken token)
    {
        if (!await catalog.VisibleAsync(actor, snapshot, id, token))
            throw new TeamCityPermissionException($"You do not have enough permissions to access project with internal id: project{snapshot.ProjectIds[id]}");
    }
    private static RestApiException Missing(string kind, string id) => new(404, "resource_not_found", "Not found", kind + " is not found: " + id);
}

internal sealed class TeamCityPermissionException(string message) : Exception(message);
