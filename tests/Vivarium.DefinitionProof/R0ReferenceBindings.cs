using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vivarium.DefinitionProof;

/// <summary>Explicit allocated-resource bindings, never omitted fields or product-derived behavior expectations.</summary>
internal sealed class R0ReferenceBindings(JsonElement metadata, Uri target, long userId, int agentId, string agentName)
{
    internal Dictionary<long, long> Builds { get; } = [];
    internal Dictionary<string, long> InternalProjects { get; } = [];
    internal Dictionary<long, long> Users { get; } = new() { [long.Parse(metadata.GetProperty("userId").GetString()!)] = userId };

    internal string Path(string source)
    {
        var result = source.Replace(metadata.GetProperty("protectedJob").GetString()!, "Q_LocalSmoke", StringComparison.Ordinal)
            .Replace(metadata.GetProperty("job").GetString()!, "P_LocalSmoke", StringComparison.Ordinal)
            .Replace(metadata.GetProperty("protectedProject").GetString()!, "Q", StringComparison.Ordinal)
            .Replace(metadata.GetProperty("project").GetString()!, "P", StringComparison.Ordinal)
            .Replace("http://127.0.0.1:18111", target.AbsoluteUri.TrimEnd('/'), StringComparison.Ordinal);
        if (result.StartsWith("/app/rest/agents/id:", StringComparison.Ordinal))
            result = result.Replace("id:" + metadata.GetProperty("agentId").GetInt32() + "/", "id:" + agentId + "/", StringComparison.Ordinal);
        foreach (var (before, after) in Users)
            if (result == "/app/rest/users/id:" + before) result = "/app/rest/users/id:" + after;
        foreach (var (before, after) in Builds)
            result = System.Text.RegularExpressions.Regex.Replace(result, @"\bid:" + before + @"(?=[/),&?]|$)", "id:" + after)
                .Replace("/build/" + before, "/build/" + after, StringComparison.Ordinal)
                .Replace("/P_LocalSmoke/" + before, "/P_LocalSmoke/" + after, StringComparison.Ordinal)
                .Replace("/Q_LocalSmoke/" + before, "/Q_LocalSmoke/" + after, StringComparison.Ordinal);
        foreach (var (before, after) in Builds)
            result = System.Text.RegularExpressions.Regex.Replace(result, @"(?<=build with id: )" + before + @"\b", after.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var (before, after) in InternalProjects)
            result = System.Text.RegularExpressions.Regex.Replace(result, @"\bproject" + before + @"\b", "project" + after);
        return result;
    }

    internal JsonNode Bind(JsonNode source, string kind = "build")
    {
        var result = source.DeepClone(); Visit(result, kind); return result;
    }

    private void Visit(JsonNode node, string kind)
    {
        if (node is JsonObject item)
        {
            foreach (var property in item.ToArray())
            {
                if (property.Value is JsonValue value)
                {
                    if (value.TryGetValue<string>(out var text))
                        item[property.Key] = kind == "agent" && property.Key == "name" && item.ContainsKey("id") ? agentName : Path(text);
                    else if (property.Key == "id" && value.TryGetValue<long>(out var id))
                    {
                        if (kind == "user") { Assert.That(Users.ContainsKey(id), Is.True, "Unbound source user identity."); item[property.Key] = Users[id]; }
                        else if (kind == "agent") { Assert.That(id, Is.EqualTo(metadata.GetProperty("agentId").GetInt32())); item[property.Key] = agentId; }
                        else if (kind == "build" && Builds.TryGetValue(id, out var actual)) item[property.Key] = actual;
                    }
                }
                else if (property.Value is not null)
                    Visit(property.Value, property.Key is "user" or "agent" or "build" ? property.Key : property.Key == "buildType" ? "buildType" : kind);
            }
        }
        else if (node is JsonArray array) foreach (var child in array.OfType<JsonNode>()) Visit(child, kind);
    }
}
