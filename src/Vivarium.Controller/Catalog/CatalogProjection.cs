using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vivarium.Controller.Persistence;

namespace Vivarium.Controller.Catalog;

internal static class CatalogProjection
{
    // Caller owns the PG activation transaction and catalogue coordination fence.
    internal static async Task ApplyAsync(VivariumDbContext database, CatalogRevision revision,
        CatalogDefinition definition, CancellationToken token)
    {
        var projects = await database.CatalogProjects.ToDictionaryAsync(p => p.ExternalId, StringComparer.Ordinal, token);
        if (!projects.TryGetValue("_Root", out var root))
        {
            root = new() { ExternalId = "_Root", Name = "Root", Description = "", IsActive = true };
            database.CatalogProjects.Add(root); projects.Add(root.ExternalId, root);
            await database.SaveChangesAsync(token);
        }
        foreach (var row in projects.Values.Where(p => p.RepositoryId == revision.RepositoryId)) row.IsActive = false;
        foreach (var selected in definition.Projects)
        {
            if (!projects.TryGetValue(selected.Control.Id, out var project))
            {
                project = new() { ExternalId = selected.Control.Id, RepositoryId = revision.RepositoryId,
                    ParentId = root.Id, Name = selected.Control.Name, Description = selected.Control.Description };
                database.CatalogProjects.Add(project); projects.Add(project.ExternalId, project);
            }
            if (project.RepositoryId != revision.RepositoryId) throw new InvalidDataException("Another catalogue owns this Project identity.");
            project.Name = selected.Control.Name; project.Description = selected.Control.Description;
            project.IsActive = true; project.ActiveRevisionId = revision.Id;
        }
        await database.SaveChangesAsync(token);
        foreach (var selected in definition.Projects)
            projects[selected.Control.Id].ParentId = projects[selected.Control.Parent].Id;
        var types = await database.CatalogBuildTypes.ToDictionaryAsync(c => c.ExternalId, StringComparer.Ordinal, token);
        var ownedIds = projects.Values.Where(p => p.RepositoryId == revision.RepositoryId).Select(p => p.Id).ToHashSet();
        foreach (var row in types.Values.Where(t => ownedIds.Contains(t.ProjectId))) row.IsActive = false;
        foreach (var selected in definition.Projects)
        {
            var project = projects[selected.Control.Id];
            var declarations = selected.Definition.Configurations.Select(c => (c.Id, c.Name, Template: false))
                .Concat((selected.Definition.Declared.Templates ?? []).Select(t => (t.Id, t.Name, Template: true)));
            foreach (var declaration in declarations)
            {
                if (!types.TryGetValue(declaration.Id, out var type))
                {
                    type = new() { ExternalId = declaration.Id, ProjectId = project.Id,
                        Name = declaration.Name, IsTemplate = declaration.Template };
                    database.CatalogBuildTypes.Add(type); types.Add(type.ExternalId, type);
                }
                // Selected R0/PILOT edits preserve ownership/kind. Move/conversion processing has
                // its separate reference contract; do not silently rewrite retained Build relations.
                if (type.ProjectId != project.Id || type.IsTemplate != declaration.Template)
                    throw new InvalidDataException("BuildType ownership or kind changed.");
                type.Name = declaration.Name; type.IsActive = true; type.ActiveRevisionId = revision.Id;
            }
        }
        await database.SaveChangesAsync(token);
        foreach (var selected in definition.Projects)
        foreach (var config in selected.Definition.Configurations)
        {
            var plan = JsonSerializer.Serialize(config, DefinitionValidation.Json);
            database.CatalogBuildDefinitions.Add(new()
            {
                BuildTypeId = types[config.Id].Id, ProjectId = projects[selected.Control.Id].Id, RevisionId = revision.Id,
                PlanJson = plan, PlanHash = IsolatedDefinitionEvaluator.Hash(plan),
                SettingsRepositoryId = selected.SettingsRepositoryId, SettingsCommit = selected.SettingsCommit,
                SettingsPath = selected.SettingsPath,
            });
        }
    }
}
