using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Features;
using Vivarium.Controller.Rest.Common;

namespace Vivarium.Controller.TeamCity;

internal static class TeamCityAgentEndpoints
{
    internal static void MapTeamCityAgents(this WebApplication app)
    {
        app.MapGet("/app/rest/agents/id:{id:int}/enabledInfo", (HttpContext http, int id, TeamCityAgentService agents) =>
            TeamCityProtocol.ExecuteAsync(http, async actor => TeamCityProtocol.Entity(http, "enabledInfo",
                await agents.GetEnabledAsync(actor, id, http.RequestAborted))));
        app.MapPut("/app/rest/agents/id:{id:int}/enabledInfo", (HttpContext http, int id, TeamCityAgentService agents) =>
            TeamCityProtocol.ExecuteAsync(http, async actor =>
            {
                if (!http.Request.HasJsonContentType()) throw new RestApiException(415, "unsupported_media_type", "JSON required", "Use application/json");
                var feature = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
                if (feature is { IsReadOnly: false }) feature.MaxRequestBodySize = 64 * 1024;
                var input = await http.Request.ReadFromJsonAsync<EnabledInput>(http.RequestAborted)
                    ?? throw new RestApiException(400, "invalid_request", "Invalid request", "Agent enablement is required");
                return TeamCityProtocol.Entity(http, "enabledInfo", await agents.SetEnabledAsync(actor, id, input.Status,
                    input.Comment?.Text ?? "", http.RequestAborted));
            }, mutation: true));
    }
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record EnabledInput(bool Status, CommentInput? Comment = null);
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record CommentInput(string? Text);
}
