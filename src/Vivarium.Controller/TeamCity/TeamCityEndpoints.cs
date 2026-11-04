using System.Text.Json.Nodes;
using Vivarium.Controller.Identity;
using Vivarium.Controller.Hosting;

namespace Vivarium.Controller.TeamCity;

internal static class TeamCityEndpoints
{
    internal static void MapTeamCity(this WebApplication app)
    {
        app.MapGet("/app/rest/server", (HttpContext http, RuntimeHostInfo host) => TeamCityProtocol.ExecuteAsync(http, _ =>
        {
            if (host.StartedAt is null) return Task.FromResult(TeamCityProtocol.Error(http, 503, "The Host has not started"));
            var version = Version.TryParse(host.Version.Split('-', 2)[0], out var parsed) ? parsed : null;
            return Task.FromResult(TeamCityProtocol.Entity(http, "server", new JsonObject
            {
                ["version"] = host.Version, ["versionMajor"] = version?.Major.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["versionMinor"] = version?.Minor.ToString(System.Globalization.CultureInfo.InvariantCulture), ["buildNumber"] = host.Build,
                ["startTime"] = TeamCityProtocol.Date(host.StartedAt.Value), ["currentTime"] = TeamCityProtocol.Date(host.CurrentTime),
                ["role"] = "main", ["webUrl"] = Origin(http), ["product"] = "Vivarium", ["compatibilityProfile"] = "TeamCity 2026.1.3 / build 222742",
            }));
        }));
        app.MapGet("/app/rest/users/current", (HttpContext http, RuntimeIdentity identity) => TeamCityProtocol.ExecuteAsync(http, async _ =>
        {
            var user = await identity.CurrentAsync(http);
            return TeamCityProtocol.Entity(http, "user", User(user!, href: true));
        }));
        app.MapGet("/app/rest/projects/id:{id}", (HttpContext http, string id, TeamCityCatalogQueries reads) =>
            TeamCityProtocol.ExecuteAsync(http, async actor => TeamCityProtocol.Entity(http, "project",
                await reads.ProjectAsync(actor, id, Origin(http), http.RequestAborted))));
        app.MapGet("/app/rest/buildTypes/id:{id}", (HttpContext http, string id, TeamCityCatalogQueries reads) =>
            TeamCityProtocol.ExecuteAsync(http, async actor => TeamCityProtocol.Entity(http, "buildType",
                await reads.ConfigurationAsync(actor, id, Origin(http), http.RequestAborted))));
    }

    internal static JsonObject User(VivariumUser user, bool href = false)
    {
        var result = new JsonObject { ["id"] = user.TeamCityId, ["username"] = user.UserName };
        if (user.DisplayName is not null) result["name"] = user.DisplayName;
        if (user.Email is not null) result["email"] = user.Email;
        if (href) result["href"] = "/app/rest/users/id:" + user.TeamCityId;
        return result;
    }

    internal static string Origin(HttpContext http) => $"{http.Request.Scheme}://{http.Request.Host}{http.Request.PathBase}";
}
