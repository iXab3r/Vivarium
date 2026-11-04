using System.Globalization;
using System.Text.Json.Nodes;
using Vivarium.Controller.Builds.Runtime;
using Vivarium.Controller.Rest.Common;

namespace Vivarium.Controller.TeamCity;

internal static class TeamCityArtifactEndpoints
{
    internal static void MapTeamCityArtifacts(this WebApplication app)
    {
        app.MapGet("/app/rest/builds/id:{id:long}/artifacts", ListAsync);
        app.MapGet("/app/rest/builds/id:{id:long}/artifacts/children/", ListAsync);
        foreach (var resource in new[] { "files", "content" })
            app.MapGet("/app/rest/builds/id:{id:long}/artifacts/" + resource + "/{**path}",
                (HttpContext http, long id, string path, RuntimeBuildArtifacts reads) => TeamCityProtocol.ExecuteAsync(http, async actor =>
                {
                    // The selected reference file response is plain text without a charset.
                    // Other artifact media variants retain their explicit conformance obligations.
                    var mediaType = path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ? "text/plain" : "application/octet-stream";
                    return Results.Stream(await reads.OpenAsync(actor, id, null, path, http.RequestAborted, discloseDenied: true), mediaType);
                }, deniedMessage: TeamCityBuildEndpoints.Denied(id)));
    }

    private static Task<IResult> ListAsync(HttpContext http, long id, RuntimeBuildArtifacts reads) => TeamCityProtocol.ExecuteAsync(http, async actor =>
    {
        var files = await reads.ListAsync(actor, id, http.RequestAborted, discloseDenied: true);
        var count = 100; var start = 0; var recursive = false; var hidden = false;
        if (http.Request.Query.TryGetValue("locator", out var locator))
        {
            if (locator.Count != 1 || locator[0]!.Length > 4096) throw Invalid();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var clause in locator[0]!.Split(','))
            {
                var pair = clause.Split(':', 2);
                if (pair.Length != 2 || !seen.Add(pair[0])) throw Invalid();
                switch (pair[0])
                {
                    case "count" when int.TryParse(pair[1], NumberStyles.None, CultureInfo.InvariantCulture, out var size) && size >= 1: count = size; break;
                    case "start" when int.TryParse(pair[1], NumberStyles.None, CultureInfo.InvariantCulture, out var offset) && offset >= 0: start = offset; break;
                    case "recursive" when bool.TryParse(pair[1], out var value): recursive = value; break;
                    case "hidden" when bool.TryParse(pair[1], out var value): hidden = value; break;
                    case "browseArchives" when pair[1] == "false": break;
                    default: throw Invalid();
                }
            }
        }
        var entries = new SortedDictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            if (!hidden && file.Path.Split('/').Any(p => p.StartsWith('.'))) continue;
            var name = !recursive && file.Path.Contains('/') ? file.Path[..file.Path.IndexOf('/')] : file.Path;
            if (entries.ContainsKey(name)) continue;
            var entry = new JsonObject { ["name"] = name };
            if (name == file.Path)
            {
                entry["size"] = file.Size;
                if (file.ModifiedAt is { } modified) entry["modificationTime"] = TeamCityProtocol.Date(modified);
            }
            entries.Add(name, entry);
        }
        var selected = entries.Values.Skip(start).Take(count).ToArray();
        var result = new JsonObject { ["count"] = selected.Length, ["file"] = new JsonArray(selected.Cast<JsonNode?>().ToArray()) };
        if (start + selected.Length < entries.Count)
            result["nextHref"] = $"/app/rest/builds/id:{id}/artifacts?locator=" + Uri.EscapeDataString($"start:{start + selected.Length},count:{count},recursive:{recursive.ToString().ToLowerInvariant()},hidden:{hidden.ToString().ToLowerInvariant()},browseArchives:false")
                + (http.Request.Query.ContainsKey("fields") ? "&fields=" + Uri.EscapeDataString(http.Request.Query["fields"].ToString()) : "");
        return TeamCityProtocol.Entity(http, "files", result);
    }, deniedMessage: TeamCityBuildEndpoints.Denied(id));
    private static RestApiException Invalid() => new(400, "invalid_locator", "Invalid locator", "Invalid artifact locator");
}
