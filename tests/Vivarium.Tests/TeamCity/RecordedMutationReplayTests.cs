using System.Net;
using WireMock.Matchers;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using WireMock.Settings;

namespace Vivarium.Tests.TeamCity;

[TestFixture]
[Category("TeamCityReplayControl")]
public sealed class RecordedMutationReplayTests
{
    [Test]
    public void Local_scope_has_twelve_unique_records_and_two_ready_product_workflows()
    {
        var all = RecordedMutationCases.All.ToArray();
        Assert.That(all.Select(item => item.Id).Distinct().ToArray(), Has.Length.EqualTo(12));
        Assert.That(all, Has.Length.EqualTo(12));
        Assert.That(RecordedMutationCases.ProductReady.Select(item => item.Id), Is.EquivalentTo(new[]
        {
            "TCREST-PROJECT-CREATE-READBACK-002", "TCREST-PROJECT-MALFORMED-ID-001",
        }));
    }

    [Test]
    public void Captured_restricted_actor_has_only_the_observed_project_view_permission()
    {
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(
            TestContext.CurrentContext.TestDirectory, "TeamCity", "Reference", "local-mutations.json")));
        var fixture = document.RootElement;
        var evidence = fixture.GetProperty("actorEvidence").GetProperty("exchanges")[0];
        Assert.That(evidence.GetProperty("response").GetProperty("status").GetInt32(), Is.EqualTo(200));
        var response = evidence.GetProperty("response");
        var bytes = Convert.FromBase64String(response.GetProperty("bodyBase64").GetString()!);
        Assert.That(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)),
            Is.EqualTo(response.GetProperty("bodySha256").GetString()));
        using var entity = System.Text.Json.JsonDocument.Parse(bytes);
        var token = entity.RootElement.GetProperty("token").EnumerateArray().Single(item =>
            item.GetProperty("name").GetString() == fixture.GetProperty("restrictedActor").GetProperty("tokenName").GetString());
        var permissions = token.GetProperty("permissionRestrictions").GetProperty("permissionRestriction");
        Assert.That(permissions.GetArrayLength(), Is.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(permissions[0].GetProperty("permission").GetProperty("id").GetString(), Is.EqualTo("view_project"));
            Assert.That(permissions[0].GetProperty("project").GetProperty("id").GetString(), Is.EqualTo("VivP0Second_20261101A_Project"));
            Assert.That(permissions[0].GetProperty("isGlobalScope").GetBoolean(), Is.False);
        });
    }

    [TestCaseSource(typeof(RecordedMutationCases), nameof(RecordedMutationCases.All))]
    public async Task Captured_mutation_workflow_replays_exact_requests_and_effects(RecordedMutationCase testCase)
    {
        using var server = StartReplay(testCase);
        using var http = new HttpClient { BaseAddress = new Uri(server.Url!) };
        await testCase.ExecuteAsync(http, replay: true);
        Assert.Multiple(() =>
        {
            Assert.That(server.LogEntries, Has.Count.EqualTo(testCase.Exchanges.Count));
            Assert.That(server.LogEntries.All(entry => entry.MappingGuid is not null), Is.True,
                "an unmatched 404 is always a replay failure");
        });
        server.ResetScenarios();
        var last = testCase.Exchanges[^1];
        Assert.That(last.Method, Is.EqualTo("GET"), "every mutation/denial needs a follow-up observation");
        using var prematureRead = last.Request("fixture-" + last.Actor);
        using var response = await http.SendAsync(prematureRead);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That(server.LogEntries.Last().MappingGuid, Is.Null,
            "reset must make a late workflow read unmatched even when its expected status is 404");
    }

    [Test]
    public async Task Successful_create_without_the_recorded_readback_fails_the_workflow()
    {
        var testCase = RecordedMutationCases.All.Single(item => item.Id == "TCREST-PROJECT-CREATE-READBACK-002");
        using var server = StartReplay(testCase, corruptReadback: true);
        using var http = new HttpClient { BaseAddress = new Uri(server.Url!) };
        var create = testCase.Exchanges[0];
        using var createRequest = create.Request("fixture-" + create.Actor);
        using var created = await http.SendAsync(createRequest);
        await RestResponseAssert.MatchesAsync(created, create.ExpectedAt(http.BaseAddress));
        var read = testCase.Exchanges[^1];
        using var readRequest = read.Request("fixture-" + read.Actor);
        using var observed = await http.SendAsync(readRequest);
        Assert.That(await RestResponseAssert.DifferenceAsync(observed, read.ExpectedAt(http.BaseAddress)), Is.Not.Null);
        Assert.That(server.LogEntries, Has.Count.EqualTo(testCase.Exchanges.Count),
            "the assertion must reject readback after a successful mutation, not a missing mapping");
        Assert.That(server.LogEntries.All(entry => entry.MappingGuid is not null), Is.True);
    }

    private static WireMockServer StartReplay(RecordedMutationCase testCase, bool corruptReadback = false)
    {
        var server = WireMockServer.Start(new WireMockServerSettings
        {
            StartAdminInterface = false,
            AllowPartialMapping = false,
            MaxRequestLogCount = 30,
        });
        for (var index = 0; index < testCase.Exchanges.Count; index++)
        {
            var exchange = testCase.Exchanges[index];
            var request = Request.Create()
                .WithUrl(new ExactMatcher(server.Url + exchange.PathAndQuery))
                .UsingMethod(exchange.Method)
                .WithHeader("Accept", new ExactMatcher(exchange.Headers["accept"]))
                .WithHeader("Authorization", new ExactMatcher("Bearer fixture-" + exchange.Actor));
            if (exchange.Body is not null)
            {
                request.WithHeader("Content-Type", new ExactMatcher(exchange.Headers["content-type"]))
                    .WithBody(new ExactMatcher(exchange.Body));
            }
            var mapping = server.Given(request).InScenario(testCase.Id);
            if (index != 0)
            {
                mapping.WhenStateIs(index.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            mapping.WillSetStateTo((index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture))
                .RespondWith(Response.Create().WithStatusCode((int)exchange.Expected.Status)
                    .WithHeader("Content-Type", exchange.Expected.MediaType)
                    .WithBody(corruptReadback && index == testCase.Exchanges.Count - 1
                        ? "{}"u8.ToArray() : exchange.ExpectedAt(new Uri(server.Url!)).Body));
        }
        return server;
    }

}
