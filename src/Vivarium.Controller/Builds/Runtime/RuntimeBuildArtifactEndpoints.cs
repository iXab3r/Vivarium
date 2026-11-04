using System.Globalization;
using Vivarium.Controller.Identity;
using Vivarium.Controller.Rest.Common;

namespace Vivarium.Controller.Builds.Runtime;

internal static class RuntimeBuildArtifactEndpoints
{
    internal static void MapRuntimeBuildArtifacts(this WebApplication app)
    {
        app.MapGet("/api/v1/builds/{id:long}/artifacts", (HttpContext http, long id,
            RuntimeBuildArtifacts reads, RestCursorCodec cursors) => RuntimeManagement.ExecuteAsync(http, async actor =>
        {
            var manifest = await reads.ListAsync(actor, id, http.RequestAborted);
            var limit = RestPagination.ParseLimit(http.Request); var position = -1;
            var resource = $"/api/v1/builds/{id}/artifacts";
            if (http.Request.Query.TryGetValue("cursor", out var cursor))
            {
                if (cursor.Count != 1 || string.IsNullOrWhiteSpace(cursor[0])) throw InvalidQuery();
                var decoded = cursors.Decode(cursor[0]!, actor.Principal, resource, "all", "ordinal");
                if (!int.TryParse(decoded, NumberStyles.None, CultureInfo.InvariantCulture, out position) || position < 0) throw InvalidQuery();
            }
            if (http.Request.Query.Keys.Any(k => k is not ("cursor" or "limit"))) throw InvalidQuery();
            var artifacts = manifest.Where(a => a.Ordinal > position).Take(limit + 1).ToArray();
            var selected = artifacts.Take(limit).ToArray();
            var next = artifacts.Length > limit ? cursors.Encode(selected[^1].Ordinal.ToString(CultureInfo.InvariantCulture),
                actor.Principal, resource, "all", "ordinal") : null;
            return Results.Json(new { items = selected.Select(a => new { path = a.Path, sha256 = a.Sha256, length = a.Size,
                modifiedAt = a.ModifiedAt, href = $"/api/v1/builds/{id}/artifacts/{a.Ordinal}/content" }), page = new { limit, nextCursor = next } });
        }, source: "build-artifacts"));
        app.MapGet("/api/v1/builds/{id:long}/artifacts/{ordinal:int}/content", (HttpContext http, long id, int ordinal,
            RuntimeBuildArtifacts reads) =>
            RuntimeManagement.ExecuteAsync(http, async actor =>
            {
                var content = await reads.OpenAsync(actor, id, ordinal, null, http.RequestAborted);
                return Results.Stream(content, "application/octet-stream");
            }, source: "build-artifacts"));
    }
    private static RestApiException InvalidQuery() => new(400, "invalid_query", "The artifact query is invalid", "Use an artifact cursor and an optional page limit.");
}
