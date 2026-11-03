using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Vivarium.Controller.TeamCity;
using Vivarium.Controller.Rest.Common;

namespace Vivarium.Tests;

public sealed class TeamCityWireTests
{
    [TestCase("application/json", "/app/rest/builds/id:")]
    [TestCase("*/*", "/artifacts/")]
    public async Task Protected_build_errors_preserve_the_literal_reference_representation(string accept, string path)
    {
        using var source = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(TestContext.CurrentContext.TestDirectory,
            "TeamCity/Reference/r0-access-observations.json")));
        var reference = source.RootElement.GetProperty("cases").EnumerateArray()
            .Single(c => c.GetProperty("id").GetString() == "TCREST-R0-PROTECTED-SIBLING-BYTES-DENIED-001")
            .GetProperty("exchanges").EnumerateArray().First(e => e.GetProperty("response").GetProperty("status").GetInt32() == 403
                && (accept == "*/*" ? e.GetProperty("request").GetProperty("path").GetString()!.Contains(path, StringComparison.Ordinal)
                    : e.GetProperty("request").GetProperty("path").GetString()!.StartsWith(path, StringComparison.Ordinal)
                        && !e.GetProperty("request").GetProperty("path").GetString()!.Contains("artifacts", StringComparison.Ordinal)));
        var expected = Convert.FromBase64String(reference.GetProperty("response").GetProperty("bodyBase64").GetString()!);
        var body = Encoding.UTF8.GetString(expected);
        var id = System.Text.RegularExpressions.Regex.Match(body, "build with id: ([0-9]+)").Groups[1].Value;
        Assert.That(id, Is.Not.Empty);
        using var services = new ServiceCollection().AddLogging().AddOptions().BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = services };
        http.Request.Headers.Accept = accept;
        http.Response.Body = new MemoryStream();
        await TeamCityProtocol.Error(http, 403, $"Not enough permissions to access build with id: {id}").ExecuteAsync(http);
        Assert.That(http.Response.StatusCode, Is.EqualTo(403));
        var actual = ((MemoryStream)http.Response.Body).ToArray();
        if (accept == "*/*") Assert.That(actual, Is.EqualTo(expected));
        else Assert.That(JsonNode.DeepEquals(JsonNode.Parse(actual), JsonNode.Parse(expected)), Is.True);
    }

    [Test]
    public async Task Nested_fields_select_current_build_cancellation_and_agent_without_expanding_other_fields()
    {
        using var services = new ServiceCollection().AddLogging().AddOptions().BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = services };
        http.Request.Headers.Accept = "application/json";
        http.Request.QueryString = new("?fields=id,canceledInfo(timestamp),agent(id,name)");
        http.Response.Body = new MemoryStream();
        var value = JsonNode.Parse("""{"id":12,"status":"UNKNOWN","canceledInfo":{"timestamp":"20261103T120000+0000","text":"reason"},"agent":{"id":3,"name":"owned","href":"private"}}""")!.AsObject();
        await TeamCityProtocol.Entity(http, "build", value).ExecuteAsync(http);
        var expected = JsonNode.Parse("""{"id":12,"canceledInfo":{"timestamp":"20261103T120000+0000"},"agent":{"id":3,"name":"owned"}}""");
        Assert.That(JsonNode.DeepEquals(JsonNode.Parse(((MemoryStream)http.Response.Body).ToArray()), expected), Is.True);
    }

    [TestCase("id,")]
    [TestCase("agent(")]
    [TestCase("agent(id))")]
    [TestCase("id,fields=")]
    public void Invalid_field_syntax_is_a_bad_request(string fields)
    {
        var http = new DefaultHttpContext(); http.Request.QueryString = new("?fields=" + Uri.EscapeDataString(fields));
        var error = Assert.Throws<RestApiException>(() => TeamCityProtocol.Entity(http, "build", new()));
        Assert.That(error!.Status, Is.EqualTo(400));
    }
}
