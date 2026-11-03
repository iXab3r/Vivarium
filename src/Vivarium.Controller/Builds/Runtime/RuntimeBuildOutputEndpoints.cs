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
            IDbContextFactory<VivariumDbContext> databases, RuntimeBuildService builds, RestCursorCodec cursors, RuntimeOutputRetention retention) =>
            RuntimeManagement.ExecuteAsync(http, async actor =>
            {
                await using var database = await databases.CreateDbContextAsync(http.RequestAborted);
                await using var snapshot = await database.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, http.RequestAborted);
                if (await builds.VisibleAsync(database, actor, id, http.RequestAborted) is null) throw RuntimeBuildService.Missing();
                int limit;
                try { limit = RestPagination.ParseLimit(http.Request); }
                catch (RestApiException) { return InvalidQuery(http); }
                var resource = "/api/v1/builds/" + id.ToString(CultureInfo.InvariantCulture) + "/output";
                var state = await database.RuntimeBuildOutputs.AsNoTracking().SingleAsync(o => o.BuildId == id, http.RequestAborted);
                var query = RestQueryFingerprint.Create(http.Request);
                var sort = http.Request.Query["sort"].ToString();
                long position = 0;
                if (http.Request.Query.ContainsKey("cursor"))
                {
                    if (http.Request.Query["cursor"].Count != 1) return InvalidCursor(http);
                    try
                    {
                        var decoded = cursors.Decode(http.Request.Query["cursor"][0]!, actor.Principal, resource, query, sort);
                        var parts = decoded.Split(':');
                        long epoch = 0;
                        if (parts.Length is < 1 or > 2
                            || parts.Length == 2 && (!long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out epoch) || epoch < 0)
                            || !long.TryParse(parts[^1], NumberStyles.None, CultureInfo.InvariantCulture, out position) || position < 0)
                            return InvalidCursor(http);
                        if (epoch != state.RetentionEpoch) return ExpiredCursor(http, resource);
                    }
                    catch (ArgumentException) { return InvalidCursor(http); }
                    catch (RestApiException error)
                    {
                        if (error.Status != 410) return InvalidCursor(http);
                        return ExpiredCursor(http, resource);
                    }
                }
                if (sort.Length != 0 || http.Request.Query.Keys.Any(k => k is not ("limit" or "cursor" or "sort"))) return InvalidQuery(http);
                var rows = await database.RuntimeOutputRanges.AsNoTracking().Where(r => r.BuildId == id && r.Position > position)
                    .OrderBy(r => r.Position).Take(limit + 1).ToArrayAsync(http.RequestAborted);
                var page = rows.Take(limit).ToArray();
                var last = page.LastOrDefault()?.Position ?? position;
                var resume = cursors.Encode(state.RetentionEpoch.ToString(CultureInfo.InvariantCulture) + ":" + last.ToString(CultureInfo.InvariantCulture),
                    actor.Principal, resource, query, sort);
                var ids = page.Select(r => r.Id).ToArray();
                var expired = (await database.RuntimeOutputExpiries.Where(e => ids.Contains(e.RangeId)).Select(e => e.RangeId)
                    .ToArrayAsync(http.RequestAborted)).ToHashSet();
                var anyExpired = state.RetentionEpoch > 0 && await (from expiry in database.RuntimeOutputExpiries
                    join range in database.RuntimeOutputRanges on expiry.RangeId equals range.Id where range.BuildId == id select expiry.RangeId)
                    .AnyAsync(http.RequestAborted);
                return Results.Json(new { items = page.Select(r => View(r, resource, expired.Contains(r.Id))),
                    page = new { limit, nextCursor = rows.Length > limit ? resume : null },
                    output = new { state = anyExpired ? "complete-with-gaps" : state.State.ToLowerInvariant().Replace('_', '-'), resumeCursor = resume,
                        retention = new { days = retention.Days, epoch = state.RetentionEpoch } } });
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
                    if (await database.RuntimeOutputExpiries.AnyAsync(e => e.RangeId == entryId, http.RequestAborted)) return ExpiredContent(http);
                }
                byte[]? content = null;
                try { content = await bytes.ReadAsync(range.ObjectKey!, checked((long)(range.ByteEnd!.Value - range.ByteStart)), range.Sha256!, http.RequestAborted); }
                catch (Exception error) when (error is HttpRequestException or IOException or TimeoutException
                    || error is OperationCanceledException && !http.RequestAborted.IsCancellationRequested) { }
                // A link and an earlier grant do not authorize bytes after an intervening revocation.
                await using var current = await databases.CreateDbContextAsync(http.RequestAborted);
                if (await builds.VisibleAsync(current, actor, id, http.RequestAborted) is null) throw RuntimeBuildService.Missing();
                if (await current.RuntimeOutputExpiries.AnyAsync(e => e.RangeId == entryId, http.RequestAborted)) return ExpiredContent(http);
                if (content is null) throw new RestApiException(503, "output_unavailable", "Output bytes are unavailable", "Retry the retained output read.");
                return Results.Bytes(content, "application/octet-stream");
            }, source: "build-output"));
    }

    private static object View(RuntimeOutputRange range, string resource, bool expired)
    {
        var item = new Dictionary<string, object?>
        {
            ["id"] = range.Id, ["kind"] = range.Kind == "GAP" || expired ? "gap" : "data", ["buildId"] = range.BuildId,
            ["stepIndex"] = range.StepIndex, ["stream"] = range.Stream == 0 ? "stdout" : "stderr",
            ["sourceSequenceStart"] = range.Start.ToString(CultureInfo.InvariantCulture),
            ["sourceSequenceEndExclusive"] = range.End?.ToString(CultureInfo.InvariantCulture), ["receivedAt"] = range.ReceivedAt,
        };
        if (range.Kind == "GAP" || expired) item["reason"] = expired ? "retention" : range.Reason;
        else item["content"] = new { href = resource + "/" + range.Id + "/content",
            length = checked((long)(range.ByteEnd!.Value - range.ByteStart)), sha256 = range.Sha256 };
        return item;
    }
    private static IResult ExpiredCursor(HttpContext http, string resource)
    {
        var problem = new VivariumProblemDetails { Code = "cursor_expired", Status = 410,
            Type = "https://vivarium.dev/problems/cursor-expired", Title = "The output cursor has expired",
            Detail = "Read the current output resource without a cursor.", Instance = http.Request.Path,
            CorrelationId = RestCorrelation.Get(http) };
        problem.Extensions["recovery"] = new { href = resource };
        return Results.Json(problem, statusCode: 410, contentType: "application/problem+json");
    }
    private static IResult ExpiredContent(HttpContext http) => RestProblems.Create(http, 410, "output_expired",
        "Output bytes have expired", "Read the current output resource for retained availability.");
    private static IResult InvalidQuery(HttpContext http) => RestProblems.Create(http, 400, "invalid_query",
        "The output query is invalid", "Supply limit from 1 through 200 and an optional output cursor.");
    private static IResult InvalidCursor(HttpContext http) => RestProblems.Create(http, 400, "invalid_cursor",
        "The output cursor is invalid", "Use a cursor issued for this principal, Build and query.");
}
