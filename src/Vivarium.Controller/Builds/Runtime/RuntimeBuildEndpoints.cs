using System.Text.Json.Serialization;
using Vivarium.Controller.Identity;
using Vivarium.Controller.Rest.Common;

namespace Vivarium.Controller.Builds.Runtime;

internal static class RuntimeBuildEndpoints
{
    internal static void MapRuntimeBuilds(this WebApplication app)
    {
        app.MapPost("/api/v1/build-run-inputs", (HttpContext http, RuntimeBuildRunInputs inputs) => RuntimeManagement.ExecuteAsync(http, async actor =>
        {
            if (!http.Request.HasJsonContentType()) return JsonRequired(http);
            var limit = http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
            if (limit is { IsReadOnly: false }) limit.MaxRequestBodySize = 2 * 1024 * 1024;
            var request = await http.Request.ReadFromJsonAsync<RuntimeRunInputRequest>(http.RequestAborted);
            if (request is null) return RuntimeManagement.InvalidInput(http);
            var created = await inputs.CreateAsync(actor, request, http.RequestAborted);
            http.Response.Headers.CacheControl = "no-store";
            return Results.Created("/api/v1/build-run-inputs/" + created.Reference[4..], created);
        }, mutation: true, source: "build-run-inputs"));
        app.MapGet("/api/v1/build-run-inputs/{id:guid}", (HttpContext http, Guid id, RuntimeBuildRunInputs inputs) =>
            RuntimeManagement.ExecuteAsync(http, async actor =>
            {
                http.Response.Headers.CacheControl = "no-store";
                var input = await inputs.GetAsync(actor, id, http.RequestAborted);
                return input is null ? RestProblems.NotFound(http, "build-run-input", id.ToString("N")) : Results.Json(input);
            }, source: "build-run-inputs"));
        app.MapPost("/api/v1/builds", (HttpContext http, RuntimeBuildService builds) => RuntimeManagement.ExecuteAsync(http, async actor =>
        {
            if (!http.Request.HasJsonContentType()) return JsonRequired(http);
            var limit = http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
            if (limit is { IsReadOnly: false }) limit.MaxRequestBodySize = 2 * 1024 * 1024;
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
                var resolved = await builds.CancelAsync(actor, id, request.Reason ?? "", mode, http.RequestAborted);
                return Results.Accepted("/api/v1/builds/" + resolved, new { id = resolved });
            }, mutation: true, source: "builds"));
    }

    private static IResult JsonRequired(HttpContext http) => RestProblems.Create(http, 415, "unsupported_media_type",
        "JSON is required", "Use application/json for this mutation.");
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record CancellationInput(string? Reason = null, string Mode = "graceful");
}
