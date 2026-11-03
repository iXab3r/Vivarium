using Microsoft.EntityFrameworkCore;
using Vivarium.Controller.Identity;
using Vivarium.Controller.Persistence;
using Vivarium.Controller.Rest.Common;
using Vivarium.Controller.Security;

namespace Vivarium.Controller.Catalog;

internal static class CatalogEndpoints
{
    internal static void MapCatalog(this WebApplication app)
    {
        app.MapPost("/api/v1/configuration-changes/reconcile", (HttpContext http, IServiceProvider services) => ExecuteAsync(http, async actor =>
        {
            if (!CatalogWorker.Configured(app.Configuration)) return Unavailable(http);
            if (http.Request.ContentLength is > 0 || http.Request.Headers.ContainsKey("Transfer-Encoding"))
                return RestProblems.Create(http, 400, "invalid_request", "Invalid request", "This action accepts no request body.");
            var operation = await services.GetRequiredService<CatalogService>().SubmitAsync(actor, http.RequestAborted);
            var url = "/api/v1/configuration-changes/" + operation.Id;
            return Results.Accepted(url, Change(operation, null));
        }, mutation: true));
        app.MapGet("/api/v1/configuration-changes/{id:guid}", (HttpContext http, Guid id, VivariumDbContext database,
            CurrentAccess access) => ExecuteAsync(http, async actor =>
        {
            if (!await CanManageAsync(actor, access, http.RequestAborted)) throw new AccessPermissionException();
            var repository = app.Configuration["Vivarium:Definitions:RepositoryId"];
            var operation = await database.CatalogChanges.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id && c.RepositoryId == repository,
                http.RequestAborted);
            if (operation is null) return RestProblems.NotFound(http, "configuration-change", id.ToString());
            var revision = operation.State == "APPLIED" ? await database.CatalogRevisions.AsNoTracking().Where(r => r.ChangeId == id)
                .Select(r => (Guid?)r.Id).SingleOrDefaultAsync(http.RequestAborted) : null;
            var diagnostics = await database.CatalogEvaluations.AsNoTracking().Where(e => e.ChangeId == id
                && (e.State == "FAILED" || e.State == "INTERRUPTED")).OrderBy(e => e.ProjectId).ThenBy(e => e.Phase)
                .Select(e => new EvaluationDiagnostic(e.ProjectId, e.Phase, e.FailureCode)).Take(32).ToArrayAsync(http.RequestAborted);
            return Results.Json(Change(operation, revision, diagnostics));
        }));
        app.MapGet("/api/v1/projects", (HttpContext http, CatalogQueries reads) => ExecuteAsync(http, async actor =>
        {
            if (!Page(http, out var offset, out var limit)) return InvalidPage(http);
            var snapshot = await reads.ActiveAsync(http.RequestAborted);
            if (snapshot is null) return Unavailable(http);
            var visible = new List<CatalogProjectView>();
            foreach (var project in snapshot.Definition.Projects.OrderBy(p => p.Control.Id, StringComparer.Ordinal))
                if (await reads.VisibleAsync(actor, snapshot, project.Control.Id, http.RequestAborted))
                    visible.Add(CatalogQueries.Project(snapshot, project));
            return Results.Json(new AccessPage<CatalogProjectView>(visible.Skip(offset).Take(limit).ToArray(), visible.Count, offset, limit));
        }));
        app.MapGet("/api/v1/projects/{projectId}", (HttpContext http, string projectId, CatalogQueries reads) => ExecuteAsync(http, async actor =>
        {
            var snapshot = await reads.ActiveAsync(http.RequestAborted);
            if (snapshot is null) return Unavailable(http);
            var project = snapshot.Definition.Projects.SingleOrDefault(p => p.Control.Id == projectId);
            if (project is null || !await reads.VisibleAsync(actor, snapshot, projectId, http.RequestAborted))
                return RestProblems.NotFound(http, "project", projectId);
            return Results.Json(CatalogQueries.Project(snapshot, project));
        }));
        app.MapGet("/api/v1/projects/{projectId}/build-configurations",
            (HttpContext http, string projectId, CatalogQueries reads) => ExecuteAsync(http, async actor =>
        {
            if (!Page(http, out var offset, out var limit)) return InvalidPage(http);
            var snapshot = await reads.ActiveAsync(http.RequestAborted);
            if (snapshot is null) return Unavailable(http);
            var project = snapshot.Definition.Projects.SingleOrDefault(p => p.Control.Id == projectId);
            if (project is null || !await reads.VisibleAsync(actor, snapshot, projectId, http.RequestAborted))
                return RestProblems.NotFound(http, "project", projectId);
            var configs = project.Definition.Configurations.OrderBy(c => c.Id, StringComparer.Ordinal).ToArray();
            return Results.Json(new AccessPage<CatalogConfigurationView>(configs.Skip(offset).Take(limit)
                .Select(c => CatalogQueries.Configuration(snapshot, project, c)).ToArray(), configs.Length, offset, limit));
        }));
        app.MapGet("/api/v1/build-configurations/{id}", (HttpContext http, string id, CatalogQueries reads) => ExecuteAsync(http, async actor =>
        {
            var snapshot = await reads.ActiveAsync(http.RequestAborted);
            if (snapshot is null) return Unavailable(http);
            foreach (var project in snapshot.Definition.Projects)
            {
                var config = project.Definition.Configurations.SingleOrDefault(c => c.Id == id);
                if (config is not null && await reads.VisibleAsync(actor, snapshot, project.Control.Id, http.RequestAborted))
                    return Results.Json(CatalogQueries.Configuration(snapshot, project, config));
            }
            return RestProblems.NotFound(http, "build-configuration", id);
        }));
    }

    private sealed record EvaluationDiagnostic(string ProjectId, string Phase, string? Code);
    private static object Change(CatalogChange operation, Guid? revision, EvaluationDiagnostic[]? diagnostics = null) => new
    {
        operation.Id, state = operation.State.ToLowerInvariant(), operation.CreatedAt, operation.CompletedAt,
        headConfigurationRevision = operation.RepositoryId + "@" + operation.CandidateCommit,
        selectedRevisions = System.Text.Json.JsonSerializer.Deserialize<CatalogMember[]>(operation.SelectedRevisionsJson, DefinitionValidation.Json),
        appliedRevisionId = revision, createdBy = new { type = "user", id = operation.ActorId },
        failureCode = operation.FailureCode, url = "/api/v1/configuration-changes/" + operation.Id,
        diagnostics = diagnostics ?? [],
    };

    private static Task<IResult> ExecuteAsync(HttpContext http, Func<ManagementRequestContext, Task<IResult>> action, bool mutation = false) =>
        RuntimeManagement.ExecuteAsync(http, async actor =>
        {
            try { return await action(actor); }
            catch (CatalogBusyException) { return RestProblems.Create(http, 409, "catalogue_busy", "Catalogue is busy", "Retry after the current configuration operation completes."); }
        }, mutation, "catalogue");
    private static async Task<bool> CanManageAsync(ManagementRequestContext actor, CurrentAccess access, CancellationToken token) =>
        Guid.TryParse(actor.Principal.ActorId, out var id)
        && await access.AllowsAllAsync(id, ["edit_project", "create_sub_project"], cancellationToken: token);
    private static IResult Unavailable(HttpContext http) => RestProblems.Create(http, 503, "catalogue_unavailable", "Catalogue is unavailable", "Configure and activate a valid Git definition revision.");
    private static bool Page(HttpContext http, out int offset, out int limit)
    {
        offset = 0; limit = 50;
        return (!http.Request.Query.ContainsKey("offset") || int.TryParse(http.Request.Query["offset"], out offset))
            && (!http.Request.Query.ContainsKey("limit") || int.TryParse(http.Request.Query["limit"], out limit))
            && offset >= 0 && limit is >= 1 and <= 200;
    }
    private static IResult InvalidPage(HttpContext http) => RestProblems.Create(http, 400, "invalid_pagination", "Invalid pagination", "Offset must be nonnegative and limit between 1 and 200.");
}
