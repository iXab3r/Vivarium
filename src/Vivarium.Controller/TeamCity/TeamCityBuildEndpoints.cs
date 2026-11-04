using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Features;
using Vivarium.Controller.Builds.Runtime;
using Vivarium.Controller.Rest.Common;

namespace Vivarium.Controller.TeamCity;

internal static class TeamCityBuildEndpoints
{
    internal static void MapTeamCityBuilds(this WebApplication app)
    {
        app.MapPost("/app/rest/buildQueue", (HttpContext http, RuntimeBuildService builds, TeamCityBuildQueries reads,
            TeamCityCatalogQueries catalog) => TeamCityProtocol.ExecuteAsync(http, async actor =>
        {
            BoundBody(http);
            var input = await http.Request.ReadFromJsonAsync<RunInput>(http.RequestAborted);
            if (input?.BuildType?.Id is not { Length: > 0 } id) throw Invalid("A buildType id is required");
            var properties = input.Properties?.Property ?? [];
            if (properties.Length > 256 || properties.Any(p => string.IsNullOrEmpty(p.Name) || p.Value is null)
                || properties.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != properties.Length)
                throw Invalid("Build properties are invalid");
            RuntimeBuildView accepted;
            try
            {
                accepted = await builds.SubmitAsync(actor, new RuntimeRunRequest(id,
                    properties.ToDictionary(p => p.Name!, p => p.Value!, StringComparer.Ordinal)), http.RequestAborted);
            }
            catch (RestApiException error) when (error.Status == 404)
            {
                // Admission owns its denial audit. Only the protocol's disclosure differs from
                // native 404 hiding; a facade preflight must not bypass the owning application service.
                await catalog.ConfigurationAsync(actor, id, TeamCityEndpoints.Origin(http), http.RequestAborted);
                throw;
            }
            return TeamCityProtocol.Entity(http, "build", await reads.GetAsync(actor, accepted.Id,
                TeamCityEndpoints.Origin(http), full: true, http.RequestAborted), applyFields: false);
        }, mutation: true));
        foreach (var resource in new[] { "builds", "buildQueue" })
        {
            app.MapGet("/app/rest/" + resource + "/id:{id:long}", (HttpContext http, long id, TeamCityBuildQueries reads) =>
                TeamCityProtocol.ExecuteAsync(http, async actor => TeamCityProtocol.Entity(http, "build",
                    await reads.GetAsync(actor, id, TeamCityEndpoints.Origin(http), full: false, http.RequestAborted)),
                    deniedMessage: Denied(id)));
            app.MapPost("/app/rest/" + resource + "/id:{id:long}", (HttpContext http, long id,
                RuntimeBuildService builds, TeamCityBuildQueries reads) => TeamCityProtocol.ExecuteAsync(http, async actor =>
            {
                BoundBody(http);
                var input = await http.Request.ReadFromJsonAsync<CancelInput>(http.RequestAborted);
                if (input is null) throw Invalid("Cancellation input is required");
                if (input.ReaddIntoQueue) throw new RestApiException(422, "execution_subset_unavailable", "Unavailable", "Requeue after cancellation is not available yet");
                var before = await reads.GetAsync(actor, id, TeamCityEndpoints.Origin(http), full: false, http.RequestAborted);
                var queued = before["state"]!.GetValue<string>() == "queued";
                try { await builds.CancelAsync(actor, id, input.Comment ?? "", "GRACEFUL", http.RequestAborted); }
                catch (RestApiException error) when (error.Code == "build_already_finished")
                { throw new RestApiException(400, "build_already_finished", "Cannot cancel finished build", "Cannot cancel finished build"); }
                return TeamCityProtocol.Entity(http, "build", await reads.GetAsync(actor, id,
                    TeamCityEndpoints.Origin(http), full: queued, http.RequestAborted), applyFields: !queued);
            }, mutation: true, deniedMessage: Denied(id)));
        }
    }

    internal static string Denied(long id) => $"Not enough permissions to access build with id: {id}";
    private static void BoundBody(HttpContext http)
    {
        if (!http.Request.HasJsonContentType()) throw new RestApiException(415, "unsupported_media_type", "JSON required", "Use application/json");
        var feature = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false }) feature.MaxRequestBodySize = 2 * 1024 * 1024;
    }
    private static RestApiException Invalid(string detail) => new(400, "invalid_request", "Invalid request", detail);
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record RunInput(TypeInput? BuildType, PropertiesInput? Properties = null);
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record TypeInput(string? Id);
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record PropertiesInput(PropertyInput[]? Property);
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record PropertyInput(string? Name, string? Value);
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record CancelInput(string? Comment = null, bool ReaddIntoQueue = false);
}
