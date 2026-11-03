using System.Text.Json.Serialization;
using Vivarium.Controller.Identity;
using Vivarium.Controller.Rest.Common;

namespace Vivarium.Controller.Builds.Runtime;

internal static class RuntimeBuildEndpoints
{
    internal static void MapRuntimeBuilds(this WebApplication app)
    {
        app.MapPost("/api/v1/builds", (HttpContext http, RuntimeBuildService builds) => RuntimeManagement.ExecuteAsync(http, async actor =>
        {
            if (!http.Request.HasJsonContentType()) return JsonRequired(http);
            var request = await http.Request.ReadFromJsonAsync<RuntimeRunRequest>(http.RequestAborted);
            if (request is null) return RuntimeManagement.InvalidInput(http);
            var accepted = await builds.SubmitAsync(actor, request, http.RequestAborted);
            return Results.Accepted("/api/v1/builds/" + accepted.Id, accepted);
        }, mutation: true, source: "builds"));
        app.MapGet("/api/v1/builds/{id:long}", (HttpContext http, long id, RuntimeBuildService builds) =>
            RuntimeManagement.ExecuteAsync(http, async actor =>
            {
                var build = await builds.GetAsync(actor, id, http.RequestAborted);
                return build is null ? RestProblems.NotFound(http, "build", id.ToString()) : Results.Json(build);
            }, source: "builds"));
        app.MapPut("/api/v1/builds/{id:long}/cancellation", (HttpContext http, long id, RuntimeBuildService builds) =>
            RuntimeManagement.ExecuteAsync(http, async actor =>
            {
                if (!http.Request.HasJsonContentType()) return JsonRequired(http);
                var request = await http.Request.ReadFromJsonAsync<CancellationInput>(http.RequestAborted);
                if (request is null) return RuntimeManagement.InvalidInput(http);
                var mode = request.Mode switch { "graceful" => "GRACEFUL", "force" => "FORCE", _ => "" };
                await builds.CancelAsync(actor, id, request.Reason ?? "", mode, http.RequestAborted);
                return Results.Accepted("/api/v1/builds/" + id, new { id });
            }, mutation: true, source: "builds"));
    }

    private static IResult JsonRequired(HttpContext http) => RestProblems.Create(http, 415, "unsupported_media_type",
        "JSON is required", "Use application/json for this mutation.");
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record CancellationInput(string? Reason = null, string Mode = "graceful");
}
