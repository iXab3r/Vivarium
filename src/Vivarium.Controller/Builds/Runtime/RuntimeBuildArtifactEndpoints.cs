using System.Data;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Vivarium.Controller.Identity;
using Vivarium.Controller.Persistence;
using Vivarium.Controller.Rest.Common;

namespace Vivarium.Controller.Builds.Runtime;

internal static class RuntimeBuildArtifactEndpoints
{
    internal static void MapRuntimeBuildArtifacts(this WebApplication app)
    {
        app.MapGet("/api/v1/builds/{id:long}/artifacts", (HttpContext http, long id,
            IDbContextFactory<VivariumDbContext> databases, RuntimeBuildService builds, RestCursorCodec cursors) => RuntimeManagement.ExecuteAsync(http, async actor =>
        {
            await using var database = await databases.CreateDbContextAsync(http.RequestAborted);
            await using var snapshot = await database.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, http.RequestAborted);
            if (await builds.VisibleAsync(database, actor, id, http.RequestAborted) is null) throw RuntimeBuildService.Missing();
            var limit = RestPagination.ParseLimit(http.Request); var position = -1;
            var resource = $"/api/v1/builds/{id}/artifacts";
            if (http.Request.Query.TryGetValue("cursor", out var cursor))
            {
                if (cursor.Count != 1 || string.IsNullOrWhiteSpace(cursor[0])) throw InvalidQuery();
                var decoded = cursors.Decode(cursor[0]!, actor.Principal, resource, "all", "ordinal");
                if (!int.TryParse(decoded, NumberStyles.None, CultureInfo.InvariantCulture, out position) || position < 0) throw InvalidQuery();
            }
            if (http.Request.Query.Keys.Any(k => k is not ("cursor" or "limit"))) throw InvalidQuery();
            var artifacts = await database.RuntimeBuildArtifacts.AsNoTracking().Where(a => a.BuildId == id && a.Ordinal > position)
                .OrderBy(a => a.Ordinal).Take(limit + 1).ToArrayAsync(http.RequestAborted);
            var selected = artifacts.Take(limit).ToArray();
            var next = artifacts.Length > limit ? cursors.Encode(selected[^1].Ordinal.ToString(CultureInfo.InvariantCulture),
                actor.Principal, resource, "all", "ordinal") : null;
            return Results.Json(new { items = selected.Select(a => new { path = a.Path, sha256 = a.Sha256, length = a.Size,
                href = $"/api/v1/builds/{id}/artifacts/{a.Ordinal}/content" }), page = new { limit, nextCursor = next } });
        }, source: "build-artifacts"));
        app.MapGet("/api/v1/builds/{id:long}/artifacts/{ordinal:int}/content", (HttpContext http, long id, int ordinal,
            IDbContextFactory<VivariumDbContext> databases, RuntimeBuildService builds, RuntimeBuildBytes bytes) =>
            RuntimeManagement.ExecuteAsync(http, async actor =>
            {
                RuntimeArtifactUpload upload;
                await using (var database = await databases.CreateDbContextAsync(http.RequestAborted))
                {
                    await using var snapshot = await database.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, http.RequestAborted);
                    if (await builds.VisibleAsync(database, actor, id, http.RequestAborted) is null) throw RuntimeBuildService.Missing();
                    var artifact = await database.RuntimeBuildArtifacts.AsNoTracking().SingleOrDefaultAsync(a => a.BuildId == id && a.Ordinal == ordinal, http.RequestAborted);
                    if (artifact is null) return RestProblems.NotFound(http, "artifact", ordinal.ToString());
                    upload = await database.RuntimeArtifactUploads.AsNoTracking().SingleAsync(u => u.BuildId == id && u.Sha256 == artifact.Sha256, http.RequestAborted);
                }
                var content = await bytes.ReadAsync(upload.ObjectKey, upload.Size, upload.Sha256, http.RequestAborted);
                await using var current = await databases.CreateDbContextAsync(http.RequestAborted);
                if (await builds.VisibleAsync(current, actor, id, http.RequestAborted) is null) throw RuntimeBuildService.Missing();
                return Results.Bytes(content, "application/octet-stream");
            }, source: "build-artifacts"));
    }
    private static RestApiException InvalidQuery() => new(400, "invalid_query", "The artifact query is invalid", "Use an artifact cursor and an optional page limit.");
}
