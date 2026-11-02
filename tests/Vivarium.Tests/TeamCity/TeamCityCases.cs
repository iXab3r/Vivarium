using System.Net;

namespace Vivarium.Tests.TeamCity;

public sealed record TeamCityCase(string Id, string PathAndQuery, string Accept, string CaptureFile)
{
    public required RestExpectation Expected { get; init; }

    internal HttpRequestMessage Request()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, PathAndQuery);
        request.Headers.Accept.ParseAdd(Accept);
        return request;
    }

    public override string ToString() => Id;
}

public static class TeamCityCases
{
    // Independent installed-reference capture, not Vivarium output. Provenance/limits:
    // docs/design/teamcity-rest-first-cohort.md. Add only ready, individually named cases.
    public static IEnumerable<TeamCityCase> Reads =>
    [
        new("TCREST-SERVER-API-VERSION-TEXT-001", "/app/rest/apiVersion", "text/plain", "api-version.json")
        {
            Expected = new RestExpectation(HttpStatusCode.OK, "text/plain", "2026.1"u8.ToArray()),
        },
        new("TCREST-PROJECT-ROOT-FIELDS-JSON-001", "/app/rest/projects/id:_Root?fields=id%2Cname", "application/json", "root-project.json")
        {
            Expected = new RestExpectation(HttpStatusCode.OK, "application/json",
                "{\"id\":\"_Root\",\"name\":\"<Root project>\"}"u8.ToArray()),
        },
    ];
}
