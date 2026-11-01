using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vivarium.Tests.TeamCity;

// Literal finite HTTP records, not a catalog, role model or mutation simulator.
public sealed record RecordedExchange(string Actor, string Method, string PathAndQuery,
    IReadOnlyDictionary<string, string> Headers, string? Body, RestExpectation Expected)
{
    public HttpRequestMessage Request(string? fixtureCredential = null)
    {
        var request = new HttpRequestMessage(new HttpMethod(Method), PathAndQuery);
        if (Body is not null)
        {
            request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(Body));
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(Headers["content-type"]);
        }
        request.Headers.Accept.ParseAdd(Headers["accept"]);
        if (fixtureCredential is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", fixtureCredential);
        }
        return request;
    }

    public RestExpectation ExpectedAt(Uri target)
    {
        if (Expected.MediaType != "application/json")
        {
            return Expected;
        }
        var json = JsonNode.Parse(Expected.Body)!;
        MapPublicUrls(json, target.GetLeftPart(UriPartial.Authority));
        return Expected with { Body = Encoding.UTF8.GetBytes(json.ToJsonString()) };
    }

    private static void MapPublicUrls(JsonNode node, string target)
    {
        if (node is JsonObject item)
        {
            if (item["webUrl"] is JsonValue webUrl)
            {
                const string origin = "http://127.0.0.1:18111";
                var value = webUrl.GetValue<string>();
                Assert.That(value, Does.StartWith(origin + "/"), "explicit reference-origin mapping");
                item["webUrl"] = target + value[origin.Length..];
            }
            foreach (var child in item.Select(property => property.Value).OfType<JsonNode>())
            {
                MapPublicUrls(child, target);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array.OfType<JsonNode>())
            {
                MapPublicUrls(child, target);
            }
        }
    }
}

public sealed record RecordedMutationCase(string Id, IReadOnlyList<RecordedExchange> Exchanges)
{
    public override string ToString() => Id;

    public async Task ExecuteAsync(HttpClient http, bool replay)
    {
        foreach (var exchange in Exchanges)
        {
            if (!replay)
            {
                Assert.That(exchange.Actor, Is.EqualTo("admin"),
                    "a native administrator cannot stand in for a restricted TeamCity actor");
            }
            TestContext.Out.WriteLine($"{Id}: {exchange.Actor} {exchange.Method} {exchange.PathAndQuery}; reference {(int)exchange.Expected.Status}");
            using var request = exchange.Request(replay ? "fixture-" + exchange.Actor : null);
            using var response = await http.SendAsync(request);
            TestContext.Out.WriteLine($"observed {(int)response.StatusCode}");
            await RestResponseAssert.MatchesAsync(response, exchange.ExpectedAt(http.BaseAddress!));
        }
    }
}

internal static class RecordedMutationCases
{
    public static IEnumerable<RecordedMutationCase> All => Load();

    // No existing object or restricted actor is required by these two workflows. Dependent
    // catalog/actor workflows stay replay-bound and explicitly product-blocked in the receipt.
    public static IEnumerable<RecordedMutationCase> ProductReady => Load().Where(testCase =>
        testCase.Id is "TCREST-PROJECT-CREATE-READBACK-002" or "TCREST-PROJECT-MALFORMED-ID-001");

    private static IReadOnlyList<RecordedMutationCase> Load()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            TestContext.CurrentContext.TestDirectory, "TeamCity", "Reference", "local-mutations.json")));
        return document.RootElement.GetProperty("cases").EnumerateArray().Select(item =>
            new RecordedMutationCase(item.GetProperty("id").GetString()!.Replace("TC-", "TCREST-", StringComparison.Ordinal),
                item.GetProperty("exchanges").EnumerateArray().Select(exchange =>
                {
                    var request = exchange.GetProperty("request");
                    var response = exchange.GetProperty("response");
                    var bytes = Convert.FromBase64String(response.GetProperty("bodyBase64").GetString()!);
                    Assert.That(bytes.Length, Is.EqualTo(response.GetProperty("bodyBytes").GetInt32()));
                    Assert.That(Convert.ToHexStringLower(SHA256.HashData(bytes)),
                        Is.EqualTo(response.GetProperty("bodySha256").GetString()), "independent entity integrity");
                    var query = request.GetProperty("query").EnumerateObject().Select(property =>
                        Uri.EscapeDataString(property.Name) + "=" + Uri.EscapeDataString(property.Value.GetString()!));
                    var suffix = string.Join("&", query);
                    var headers = request.GetProperty("headers").EnumerateObject()
                        .ToDictionary(property => property.Name, property => property.Value.GetString()!);
                    var media = response.GetProperty("headers").GetProperty("content-type").GetString()!.Split(';')[0];
                    return new RecordedExchange(exchange.GetProperty("actor").GetString()!,
                        request.GetProperty("method").GetString()!,
                        request.GetProperty("path").GetString()! + (suffix.Length == 0 ? "" : "?" + suffix),
                        headers, request.TryGetProperty("body", out var body) ? body.GetString() : null,
                        new RestExpectation((HttpStatusCode)response.GetProperty("status").GetInt32(), media, bytes));
                }).ToArray())).ToArray();
    }
}
