using System.Text.Json;
using System.Data;
using Microsoft.EntityFrameworkCore;
using Vivarium.Controller.Identity;
using Vivarium.Controller.Persistence;
using Vivarium.Controller.Security;
using Vivarium.Definitions;

namespace Vivarium.Controller.Catalog;

public sealed record CatalogProjectView(string Id, long InternalId, string Name, string ParentId, string Description, Guid ActiveRevisionId);
public sealed record CatalogConfigurationView(string Id, long InternalId, string Name, string ProjectId, Guid ActiveRevisionId);
public sealed record CatalogSnapshot(CatalogRevision Revision, CatalogDefinition Definition,
    IReadOnlyDictionary<string, long> ProjectIds, IReadOnlyDictionary<string, long> BuildTypeIds)
{
    public string[] Ancestry(string project)
    {
        var result = new List<string>();
        while (project != "_Root")
        {
            if (result.Contains(project, StringComparer.Ordinal)) throw new InvalidDataException("Invalid active Project ancestry.");
            result.Add(project);
            project = Definition.Projects.Single(p => p.Control.Id == project).Control.Parent;
        }
        result.Add("_Root"); return result.ToArray();
    }
}

public sealed class CatalogQueries(VivariumDbContext database, IConfiguration configuration, CurrentAccess access)
{
    public async Task<CatalogSnapshot?> ActiveAsync(CancellationToken token = default)
    {
        var repository = configuration["Vivarium:Definitions:RepositoryId"];
        if (repository is null) return null;
        // Metadata and permission reads use one current database snapshot. A new grant on an old
        // ancestor must not authorize a project that has already moved to a different parent.
        if (database.Database.CurrentTransaction is null)
            await database.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        var pointer = await database.ActiveCatalogs.AsNoTracking().SingleOrDefaultAsync(a => a.RepositoryId == repository, token);
        if (pointer is null) return null;
        var revision = await database.CatalogRevisions.AsNoTracking().SingleAsync(r => r.Id == pointer.RevisionId, token);
        var definition = JsonSerializer.Deserialize<CatalogDefinition>(revision.DefinitionJson, DefinitionValidation.Json)
            ?? throw new InvalidDataException("The active catalogue is invalid.");
        var canonical = JsonSerializer.Serialize(definition, DefinitionValidation.Json);
        if (definition.SchemaVersion != "1" || IsolatedDefinitionEvaluator.Hash(canonical) != revision.DefinitionHash)
            throw new InvalidDataException("The active catalogue integrity differs.");
        var projects = await database.CatalogProjects.AsNoTracking().Where(p => p.IsActive && p.ActiveRevisionId == revision.Id)
            .ToDictionaryAsync(p => p.ExternalId, p => p.Id, StringComparer.Ordinal, token);
        var types = await database.CatalogBuildTypes.AsNoTracking().Where(t => t.IsActive && t.ActiveRevisionId == revision.Id)
            .ToDictionaryAsync(t => t.ExternalId, t => t.Id, StringComparer.Ordinal, token);
        if (definition.Projects.Any(p => !projects.ContainsKey(p.Control.Id)
            || p.Definition.Configurations.Any(c => !types.ContainsKey(c.Id))))
            throw new InvalidDataException("The active catalogue identity index differs.");
        return new(revision, definition, projects, types);
    }

    public async Task<bool> VisibleAsync(ManagementRequestContext actor, CatalogSnapshot snapshot, string project,
        CancellationToken token = default) => actor.Principal.ActorType == "user"
        && Guid.TryParse(actor.Principal.ActorId, out var id)
        && await access.AllowsAsync(id, "view_project", snapshot.Ancestry(project), token);

    public static CatalogProjectView Project(CatalogSnapshot snapshot, CatalogProject project) =>
        new(project.Control.Id, snapshot.ProjectIds[project.Control.Id], project.Control.Name, project.Control.Parent, project.Control.Description, snapshot.Revision.Id);
    public static CatalogConfigurationView Configuration(CatalogSnapshot snapshot, CatalogProject project, ResolvedConfiguration config) =>
        new(config.Id, snapshot.BuildTypeIds[config.Id], config.Name, project.Control.Id, snapshot.Revision.Id);
}
