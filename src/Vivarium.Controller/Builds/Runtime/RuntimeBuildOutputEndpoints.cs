using System.Data;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Vivarium.Controller.Identity;
using Vivarium.Controller.Persistence;
using Vivarium.Controller.Rest.Common;

namespace Vivarium.Controller.Builds.Runtime;

internal static class RuntimeBuildOutputEndpoints
{
    internal static void MapRuntimeBuildOutput(this WebApplication app)
    {
        app.MapGet("/api/v1/builds/{id:long}/output", (HttpContext http, long id,
            IDbContextFactory<VivariumDbContext> databases, RuntimeBuildService builds, RestCursorCodec cursors) =>
            RuntimeManagement.ExecuteAsync(http, async actor =>
            {
                await using var database = await databases.CreateDbContextAsync(http.RequestAborted);
                await using var snapshot = await database.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, http.RequestAborted);
                if (await builds.VisibleAsync(database, actor, id, http.RequestAborted) is null) throw RuntimeBuildService.Missing();
                int limit;
                try { limit = RestPagination.ParseLimit(http.Request); }
                catch (RestApiException) { return InvalidQuery(http); }
                var resource = "/api/v1/builds/" + id.ToString(CultureInfo.InvariantCulture) + "/output";
                var query = RestQueryFingerprint.Create(http.Request);
                var sort = http.Request.Query["sort"].ToString();
                long position = 0;
                if (http.Request.Query.ContainsKey("cursor"))
                {
                    if (http.Request.Query["cursor"].Count != 1) return InvalidCursor(http);
                    try
                    {
                        var decoded = cursors.Decode(http.Request.Query["cursor"][0]!, actor.Principal, resource, query, sort);
                        if (!long.TryParse(decoded, NumberStyles.None, CultureInfo.InvariantCulture, out position) || position < 0)
                            return InvalidCursor(http);
                    }
                    catch (ArgumentException) { return InvalidCursor(http); }
                    catch (RestApiException error)
                    {
                        if (error.Status != 410) return InvalidCursor(http);
                        var problem = new VivariumProblemDetails { Code = "cursor_expired", Status = 410,
                            Type = "https://vivarium.dev/problems/cursor-expired", Title = "The output cursor has expired",
                            Detail = "Read the current output resource without a cursor.", Instance = http.Request.Path,
                            CorrelationId = RestCorrelation.Get(http) };
                        problem.Extensions["recovery"] = new { href = resource };
                        return Results.Json(problem, statusCode: 410, contentType: "application/problem+json");
                    }
                }
                if (sort.Length != 0 || http.Request.Query.Keys.Any(k => k is not ("limit" or "cursor" or "sort"))) return InvalidQuery(http);
                var rows = await database.RuntimeOutputRanges.AsNoTracking().Where(r => r.BuildId == id && r.Position > position)
                    .OrderBy(r => r.Position).Take(limit + 1).ToArrayAsync(http.RequestAborted);
                var page = rows.Take(limit).ToArray();
                var last = page.LastOrDefault()?.Position ?? position;
                var resume = cursors.Encode(last.ToString(CultureInfo.InvariantCulture), actor.Principal, resource, query, sort);
                var state = await database.RuntimeBuildOutputs.AsNoTracking().SingleAsync(o => o.BuildId == id, http.RequestAborted);
                return Results.Json(new { items = page.Select(r => View(r, resource)),
                    page = new { limit, nextCursor = rows.Length > limit ? resume : null },
                    output = new { state = state.State.ToLowerInvariant().Replace('_', '-'), resumeCursor = resume } });
            }, source: "build-output"));

        app.MapGet("/api/v1/builds/{id:long}/output/{entryId:guid}/content", (HttpContext http, long id, Guid entryId,
            IDbContextFactory<VivariumDbContext> databases, RuntimeBuildService builds, RuntimeBuildBytes bytes) =>
            RuntimeManagement.ExecuteAsync(http, async actor =>
            {
                RuntimeOutputRange? range;
                await using (var database = await databases.CreateDbContextAsync(http.RequestAborted))
                {
                    await using var snapshot = await database.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, http.RequestAborted);
                    if (await builds.VisibleAsync(database, actor, id, http.RequestAborted) is null) throw RuntimeBuildService.Missing();
                    range = await database.RuntimeOutputRanges.AsNoTracking().SingleOrDefaultAsync(r => r.BuildId == id
                        && r.Id == entryId && r.Position != null && r.Kind != "GAP", http.RequestAborted);
                    if (range is null) return RestProblems.NotFound(http, "output-entry", entryId.ToString());
                }
                var content = await bytes.ReadAsync(range.ObjectKey!, checked((long)(range.ByteEnd!.Value - range.ByteStart)), range.Sha256!, http.RequestAborted);
                // A link and an earlier grant do not authorize bytes after an intervening revocation.
                await using var current = await databases.CreateDbContextAsync(http.RequestAborted);
                if (await builds.VisibleAsync(current, actor, id, http.RequestAborted) is null) throw RuntimeBuildService.Missing();
                return Results.Bytes(content, "application/octet-stream");
            }, source: "build-output"));
    }

    private static object View(RuntimeOutputRange range, string resource)
    {
        var item = new Dictionary<string, object?>
        {
            ["id"] = range.Id, ["kind"] = range.Kind == "GAP" ? "gap" : "data", ["buildId"] = range.BuildId,
            ["stepIndex"] = range.StepIndex, ["stream"] = range.Stream == 0 ? "stdout" : "stderr",
            ["sourceSequenceStart"] = range.Start.ToString(CultureInfo.InvariantCulture),
            ["sourceSequenceEndExclusive"] = range.End?.ToString(CultureInfo.InvariantCulture), ["receivedAt"] = range.ReceivedAt,
        };
        if (range.Kind == "GAP") item["reason"] = range.Reason;
        else item["content"] = new { href = resource + "/" + range.Id + "/content",
            length = checked((long)(range.ByteEnd!.Value - range.ByteStart)), sha256 = range.Sha256 };
        return item;
    }
    private static IResult InvalidQuery(HttpContext http) => RestProblems.Create(http, 400, "invalid_query",
        "The output query is invalid", "Supply limit from 1 through 200 and an optional output cursor.");
    private static IResult InvalidCursor(HttpContext http) => RestProblems.Create(http, 400, "invalid_cursor",
        "The output cursor is invalid", "Use a cursor issued for this principal, Build and query.");
}
