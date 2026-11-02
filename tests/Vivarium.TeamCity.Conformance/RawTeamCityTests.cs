using System.Diagnostics;
using Vivarium.Tests.TeamCity;

namespace Vivarium.TeamCity.Conformance;

[TestFixture]
[NonParallelizable]
public sealed class RawTeamCityTests
{
    private CurrentController controller = null!;
    private readonly Stopwatch elapsed = new();

    [OneTimeSetUp]
    public async Task Start_healthy_production_controller()
    {
        elapsed.Start();
        controller = new CurrentController();
        await controller.StartAsync();
        TestContext.Progress.WriteLine(
            $"Current product: {controller.Host.Url}; production Kestrel/auth/Git/SQLite; setup {elapsed.ElapsedMilliseconds}ms; process working set {Environment.WorkingSet} bytes");
    }

    [OneTimeTearDown]
    public async Task Stop_and_remove_isolated_controller()
    {
        if (controller is not null)
        {
            await controller.DisposeAsync();
            Assert.That(Directory.Exists(controller.Root), Is.False, "fixture cleanup");
        }
        TestContext.Progress.WriteLine($"Raw target total {elapsed.ElapsedMilliseconds}ms");
    }

    [TestCaseSource(typeof(TeamCityCases), nameof(TeamCityCases.Reads))]
    public async Task Current_product_matches_frozen_reference_read(TeamCityCase testCase)
    {
        using var request = testCase.Request();
        using var response = await controller.Http.SendAsync(request);
        TestContext.Out.WriteLine(
            $"{testCase.Id}: GET {testCase.PathAndQuery}; observed {(int)response.StatusCode}; reference {(int)testCase.Expected.Status}");
        await RestResponseAssert.MatchesAsync(response, testCase.Expected);
    }

    [TestCase(TestName = ProjectCreationContract.Id)]
    public async Task TCREST_PROJECT_CREATE_READBACK_JSON_001()
    {
        TestContext.Out.WriteLine($"{ProjectCreationContract.Id}: real POST then GET through current product; no database state rewrite");
        await ProjectCreationContract.ExecuteAsync(controller.Http);
    }

    [TestCaseSource(typeof(RecordedMutationCases), nameof(RecordedMutationCases.ProductReady))]
    public async Task Current_product_matches_ready_local_reference_workflow(RecordedMutationCase testCase)
    {
        await testCase.ExecuteAsync(controller.Http, replay: false);
    }
}
