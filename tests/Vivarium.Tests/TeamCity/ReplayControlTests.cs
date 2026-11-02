using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using WireMock.Matchers;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using WireMock.Settings;

namespace Vivarium.Tests.TeamCity;

[TestFixture]
[Category("TeamCityReplayControl")]
public sealed class ReplayControlTests
{
    [TestCaseSource(typeof(TeamCityCases), nameof(TeamCityCases.Reads))]
    public void Frozen_case_matches_its_original_sanitized_capture(TeamCityCase testCase)
    {
        using var capture = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            TestContext.CurrentContext.TestDirectory, "TeamCity", "Reference", testCase.CaptureFile)));
        var source = capture.RootElement;
        var bytes = Convert.FromBase64String(source.GetProperty("bodyBase64").GetString()!);
        Assert.Multiple(() =>
        {
            Assert.That(source.GetProperty("response").GetProperty("status").GetInt32(),
                Is.EqualTo((int)testCase.Expected.Status));
            Assert.That(source.GetProperty("response").GetProperty("headers").GetProperty("content-type").GetString(),
                Is.EqualTo(testCase.Expected.MediaType));
            Assert.That(bytes, Is.EqualTo(testCase.Expected.Body));
            Assert.That(Convert.ToHexStringLower(SHA256.HashData(bytes)),
                Is.EqualTo(source.GetProperty("bodySha256").GetString()));
        });
    }

    [TestCase("status")]
    [TestCase("media")]
    [TestCase("field")]
    [TestCase("type")]
    [TestCase("order")]
    [TestCase("byte")]
    [TestCase("malformed-json")]
    public async Task Comparator_rejects_corruption(string corruption)
    {
        // Synthetic assertion controls, never counted as reference or product cases.
        var expected = new RestExpectation(HttpStatusCode.OK,
            corruption == "byte" ? "application/octet-stream" : "application/json",
            "{\"id\":\"fixture\",\"count\":2,\"values\":[\"a\",\"b\"]}"u8.ToArray());
        using var response = new HttpResponseMessage(
            corruption == "status" ? HttpStatusCode.NotFound : expected.Status)
        {
            Content = new ByteArrayContent(corruption switch
            {
                "field" => "{\"count\":2,\"values\":[\"a\",\"b\"]}"u8.ToArray(),
                "type" => "{\"id\":\"fixture\",\"count\":\"2\",\"values\":[\"a\",\"b\"]}"u8.ToArray(),
                "order" => "{\"id\":\"fixture\",\"count\":2,\"values\":[\"b\",\"a\"]}"u8.ToArray(),
                "byte" => [.. expected.Body, (byte)'\n'],
                "malformed-json" => "{\"id\":"u8.ToArray(),
                _ => expected.Body,
            }),
        };
        response.Content.Headers.ContentType = new(
            corruption == "media" ? "text/html" : expected.MediaType!);
        Assert.That(await RestResponseAssert.DifferenceAsync(response, expected), Is.Not.Null);
    }

    [Test]
    public async Task Json_object_key_order_is_not_contractual()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"name\":\"fixture\",\"id\":\"sample\"}",
                System.Text.Encoding.UTF8, "application/json"),
        };
        await RestResponseAssert.MatchesAsync(response, new RestExpectation(
            HttpStatusCode.OK, "application/json", "{\"id\":\"sample\",\"name\":\"fixture\"}"u8.ToArray()));
    }

    [Test]
    public async Task Source_established_project_create_readback_replays_finite_sequence_and_resets_state()
    {
        using var server = WireMockServer.Start(new WireMockServerSettings
        {
            StartAdminInterface = false,
            AllowPartialMapping = false,
            MaxRequestLogCount = 20,
        });
        server.Given(Request.Create().WithUrl(new ExactMatcher(server.Url + ProjectCreationContract.CreatePath))
                .UsingPost().WithHeader("Accept", new ExactMatcher("application/json"))
                .WithHeader("Content-Type", new ExactMatcher("application/json; charset=utf-8"))
                .WithHeader("Authorization", new ExactMatcher("Bearer fixture-token"))
                .WithBody(new ExactMatcher(ProjectCreationContract.Body)))
            .InScenario("project-create").WillSetStateTo("created")
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                .WithBody("{}"));
        server.Given(Request.Create().WithUrl(new ExactMatcher(server.Url + ProjectCreationContract.ReadPath))
                .UsingGet().WithHeader("Accept", new ExactMatcher("application/json"))
                .WithHeader("Authorization", new ExactMatcher("Bearer fixture-token")))
            .InScenario("project-create").WhenStateIs("created")
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                .WithBody(ProjectCreationContract.ReadBody));
        using var http = new HttpClient { BaseAddress = new Uri(server.Url!) };
        http.DefaultRequestHeaders.Authorization = new("Bearer", "fixture-token");
        await ProjectCreationContract.ExecuteAsync(http);
        Assert.That(server.LogEntries, Has.Count.EqualTo(2));
        Assert.That(server.LogEntries.All(entry => entry.MappingGuid is not null), Is.True);
        server.ResetScenarios();
        using var prematureRead = new HttpRequestMessage(HttpMethod.Get, ProjectCreationContract.ReadPath);
        prematureRead.Headers.Accept.ParseAdd("application/json");
        using var afterReset = await http.SendAsync(prematureRead);
        Assert.That(afterReset.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That(server.LogEntries.Count(entry => entry.MappingGuid is null), Is.EqualTo(1),
            "reset removes transcript state; an unmatched read is a replay-control failure");
    }

    [TestCaseSource(typeof(TeamCityCases), nameof(TeamCityCases.Reads))]
    public async Task Frozen_reference_read_replays_with_an_exact_request_and_journal(TeamCityCase testCase)
    {
        using var server = WireMockServer.Start(new WireMockServerSettings
        {
            StartAdminInterface = false,
            AllowPartialMapping = false,
            MaxRequestLogCount = 20,
        });
        server.Given(Request.Create()
                .WithUrl(new ExactMatcher(server.Url + testCase.PathAndQuery))
                .UsingGet()
                .WithHeader("Accept", new ExactMatcher(testCase.Accept))
                .WithHeader("Authorization", new ExactMatcher("Bearer fixture-token")))
            .RespondWith(Response.Create().WithStatusCode((int)testCase.Expected.Status)
                .WithHeader("Content-Type", testCase.Expected.MediaType!)
                .WithBody(testCase.Expected.Body));
        using var http = new HttpClient { BaseAddress = new Uri(server.Url!) };
        using var request = testCase.Request();
        request.Headers.Authorization = new("Bearer", "fixture-token");
        using var response = await http.SendAsync(request);
        await RestResponseAssert.MatchesAsync(response, testCase.Expected);
        Assert.Multiple(() =>
        {
            Assert.That(server.LogEntries, Has.Count.EqualTo(1));
            Assert.That(server.LogEntries.Single().MappingGuid, Is.Not.Null,
                "an unmatched mapping is a replay failure even if its default status looks plausible");
        });
    }

    [Test]
    public async Task Strict_mapping_rejects_wrong_method_url_query_and_credentials_and_journals_every_request()
    {
        using var server = WireMockServer.Start(new WireMockServerSettings
        {
            StartAdminInterface = false,
            AllowPartialMapping = false,
            MaxRequestLogCount = 20,
        });
        server.Given(Request.Create()
                .WithUrl(new ExactMatcher(server.Url + "/control?fields=id"))
                .UsingGet()
                .WithHeader("Accept", new ExactMatcher("text/plain"))
                .WithHeader("Authorization", new ExactMatcher("Bearer fixture-token")))
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "text/plain").WithBody("fixture"));
        using var http = new HttpClient { BaseAddress = new Uri(server.Url!) };
        http.DefaultRequestHeaders.Accept.ParseAdd("text/plain");
        http.DefaultRequestHeaders.Authorization = new("Bearer", "fixture-token");
        using var positive = await http.GetAsync("/control?fields=id");
        await RestResponseAssert.MatchesAsync(positive,
            new RestExpectation(HttpStatusCode.OK, "text/plain", "fixture"u8.ToArray()));
        using var method = await http.PostAsync("/control?fields=id", new StringContent(""));
        using var path = await http.GetAsync("/other?fields=id");
        using var query = await http.GetAsync("/control?fields=id&extra=true");
        http.DefaultRequestHeaders.Authorization = new("Bearer", "wrong-token");
        using var credentials = await http.GetAsync("/control?fields=id");
        Assert.Multiple(() =>
        {
            Assert.That(method.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(path.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(query.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(credentials.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(server.LogEntries, Has.Count.EqualTo(5));
            Assert.That(server.LogEntries.Count(entry => entry.MappingGuid is null), Is.EqualTo(4),
                "unmatched replay requests are harness failures, never TeamCity not-found evidence");
        });
        server.Reset();
        Assert.That(server.LogEntries, Is.Empty, "a reset must discard the prior case journal");
        using var afterReset = await http.GetAsync("/control?fields=id");
        Assert.That(afterReset.StatusCode, Is.EqualTo(HttpStatusCode.NotFound),
            "a reset must also discard the prior case mappings");
    }
}
