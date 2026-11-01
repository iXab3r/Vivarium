using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json;

namespace Vivarium.Tests.TeamCity;

public sealed record RestExpectation(HttpStatusCode Status, string? MediaType, byte[] Body);

internal static class RestResponseAssert
{
    public static async Task MatchesAsync(HttpResponseMessage actual, RestExpectation expected)
        => Assert.That(await DifferenceAsync(actual, expected), Is.Null);

    public static async Task<string?> DifferenceAsync(HttpResponseMessage actual, RestExpectation expected)
    {
        var bytes = await actual.Content.ReadAsByteArrayAsync();
        if (actual.StatusCode != expected.Status)
        {
            return $"HTTP status: expected {(int)expected.Status}, actual {(int)actual.StatusCode}";
        }
        if (actual.Content.Headers.ContentType?.MediaType != expected.MediaType)
        {
            return $"media type: expected {expected.MediaType}, actual {actual.Content.Headers.ContentType?.MediaType}";
        }
        var detail = $"actual UTF-8: {Encoding.UTF8.GetString(bytes.AsSpan(0, Math.Min(bytes.Length, 512)))}";
        if (expected.Body.Length == 0)
        {
            return bytes.Length == 0 ? null : $"response bytes differ; {detail}";
        }
        if (expected.MediaType == "application/json")
        {
            var expectedJson = JsonNode.Parse(expected.Body);
            JsonNode? actualJson;
            try
            {
                actualJson = JsonNode.Parse(bytes);
            }
            catch (JsonException)
            {
                return $"malformed response JSON; {detail}";
            }
            return JsonNode.DeepEquals(actualJson, expectedJson)
                ? null : $"JSON fields/types/array order differ; {detail}";
        }
        else
        {
            return bytes.AsSpan().SequenceEqual(expected.Body) ? null : $"response bytes differ; {detail}";
        }
    }
}
