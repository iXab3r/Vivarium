using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Vivarium.Controller.Agents;
using Vivarium.Controller.Configuration.Git;
using Vivarium.Controller.Persistence;

namespace Vivarium.Controller.Catalog;

internal static class CatalogAgentSettings
{
    // Serialize only the short PG projection/registration transactions. In particular, a new
    // Agent must not miss an activation between reading its initial policy and inserting its row.
    internal static Task LockProjectionAsync(VivariumDbContext database, CancellationToken token) =>
        database.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(90695025117)", token);

    internal static CatalogAgentSetting[] Read(DefinitionTree control)
    {
        var files = control.Sources.Where(p => p.Key.StartsWith(".vivarium/agents/", StringComparison.Ordinal)).ToArray();
        if (files.Length > 0 && control.Sources[".vivarium/repository.yaml"].ReplaceLineEndings("\n").TrimEnd('\n')
            != DefinitionGitReader.Manifest(control.RepositoryId, control: true, agents: true))
            throw new InvalidDataException("The control manifest must declare Agent documents.");
        var result = new List<CatalogAgentSetting>();
        foreach (var (path, source) in files)
        {
            var id = path[".vivarium/agents/".Length..^".yaml".Length];
            if (!Regex.IsMatch(id, "^[a-z0-9](?:[a-z0-9.-]{0,125}[a-z0-9])?$", RegexOptions.CultureInvariant))
                throw new InvalidDataException("The Agent Git document path is unsupported.");
            var normalized = source.ReplaceLineEndings("\n").TrimEnd('\n');
            var yes = Encoding.UTF8.GetString(ConfigurationTreeValidator.RenderAgent(id, true)).TrimEnd('\n');
            var no = Encoding.UTF8.GetString(ConfigurationTreeValidator.RenderAgent(id, false)).TrimEnd('\n');
            if (normalized != yes && normalized != no) throw new InvalidDataException("The Agent Git declaration is invalid.");
            result.Add(new(id, normalized == yes));
        }
        return result.OrderBy(a => a.Id, StringComparer.Ordinal).ToArray();
    }

    internal static async Task<bool> ChangedAsync(VivariumDbContext database, Guid? priorId,
        CatalogDefinition next, CancellationToken token)
    {
        var before = priorId is null ? [] : (JsonSerializer.Deserialize<CatalogDefinition>(
            (await database.CatalogRevisions.SingleAsync(r => r.Id == priorId, token)).DefinitionJson, DefinitionValidation.Json)?.Agents ?? []);
        var after = next.Agents ?? [];
        if (before.Any(old => !after.Any(item => item.Id == old.Id)))
            throw new InvalidDataException("Agent declaration removal requires its owning lifecycle contract.");
        return after.Any(item => !before.Any(old => old.Id == item.Id && old.Enabled == item.Enabled));
    }

    internal static async Task ApplyAsync(VivariumDbContext database, CatalogRevision revision,
        CatalogDefinition definition, CancellationToken token)
    {
        var ids = (definition.Agents ?? []).Select(a => a.Id).ToArray();
        var rows = await database.AgentRegistrations.Where(a => ids.Contains(a.AgentId)).ToArrayAsync(token);
        foreach (var row in rows)
        {
            var selected = definition.Agents!.Single(a => a.Id == row.AgentId);
            if (row.DesiredRevision is not null && row.DesiredEnabled == selected.Enabled) continue;
            row.DesiredEnabled = selected.Enabled;
            row.DesiredRevision = revision.RepositoryId + "@" + revision.ControlCommit;
        }
    }

    internal static async Task InitializeAsync(VivariumDbContext database, IConfiguration configuration,
        AgentRegistration row, CancellationToken token)
    {
        var repository = configuration["Vivarium:Definitions:RepositoryId"];
        if (repository is null) return;
        var active = await database.ActiveCatalogs.AsNoTracking().SingleOrDefaultAsync(a => a.RepositoryId == repository, token);
        if (active is null) return;
        var revision = await database.CatalogRevisions.AsNoTracking().SingleAsync(r => r.Id == active.RevisionId, token);
        var selected = JsonSerializer.Deserialize<CatalogDefinition>(revision.DefinitionJson, DefinitionValidation.Json)?.Agents
            ?.SingleOrDefault(a => a.Id == row.AgentId);
        if (selected is null) return;
        row.DesiredEnabled = selected.Enabled; row.DesiredRevision = revision.RepositoryId + "@" + revision.ControlCommit;
    }
}
