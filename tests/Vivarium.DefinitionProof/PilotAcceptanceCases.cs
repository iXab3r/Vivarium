using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vivarium.Controller.Persistence;

namespace Vivarium.DefinitionProof;

// Selected whole-chain behavior, independent of background progress estimates (D46).
[NonParallelizable, Category("US013Pilot")]
public sealed class PilotAcceptanceCases
{
    private R0AcceptanceInstallation installation = null!;
    [OneTimeSetUp] public async Task Prepare() { installation = new(); await installation.StartAsync(); }
    [OneTimeTearDown] public async Task Cleanup() => await installation.DisposeAsync();

    [TestCase("failure", TestName = "PILOT-CHAIN-UPSTREAM-FAILURE-001")]
    [TestCase("cancel-upstream", TestName = "PILOT-CHAIN-UPSTREAM-CANCEL-001")]
    [TestCase("cancel-root", TestName = "PILOT-CHAIN-ROOT-CANCEL-001")]
    public async Task Chain_terminal_effects_preserve_the_pinned_upstream_and_root_cancellation_distinction(string mode)
    {
        await using var host = await installation.CreateCaseAsync(catalogued: true);
        using var administrator = await host.AdministratorAsync(); var csrf = await host.AntiforgeryAsync(administrator);
        var settings = Settings;
        foreach (var name in new[] { "Prepare", "Windows", "Release" })
            settings = settings.Replace("\"" + name + "\"", "\"" + mode + " " + name + "\"", StringComparison.Ordinal);
        if (mode == "failure") settings = settings.Replace("set -eu\\n", "set -eu\\nexit 7\\n", StringComparison.Ordinal);
        await File.WriteAllTextAsync(Path.Combine(host.Definitions, ".vivarium/unbound/P/Settings.g.cs"), settings);
        await R0AcceptanceCases.CommitCaseAsync(host, "fixture: pin chain failure and cancellation effects");
        Assert.That((await R0AcceptanceCases.ReconcileCaseAsync(administrator, csrf)).GetProperty("state").GetString(), Is.EqualTo("applied"));
        using var actor = await R0AcceptanceCases.RestrictedActorAsync(host, administrator, csrf);
        actor.BuildClient.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        using var request = new StringContent("{\"buildType\":{\"id\":\"P_Release\"}}", Encoding.UTF8, "application/json");
        using var accepted = await actor.BuildClient.PostAsync("/app/rest/buildQueue", request);
        Assert.That(accepted.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        using var admission = JsonDocument.Parse(await accepted.Content.ReadAsStringAsync());
        var releaseId = admission.RootElement.GetProperty("id").GetInt64();
        var release = await ReadAsync(actor.BuildClient, releaseId);
        var windowsId = release.GetProperty("snapshot-dependencies").GetProperty("build")[0].GetProperty("id").GetInt64();
        var windows = await ReadAsync(actor.BuildClient, windowsId);
        var prepareId = windows.GetProperty("snapshot-dependencies").GetProperty("build")[0].GetProperty("id").GetInt64();
        await using var linux = new OwnedDockerAgent(host.Root);
        if (mode == "failure") await host.StartLinuxAgentAsync(linux, administrator, csrf);
        else
        {
            var target = mode == "cancel-upstream" ? prepareId : releaseId;
            using var cancel = new StringContent("{\"comment\":\"Owned lifecycle cancel\",\"readdIntoQueue\":false}", Encoding.UTF8, "application/json");
            using var response = await actor.BuildClient.PostAsync("/app/rest/buildQueue/id:" + target, cancel);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while ((release = await ReadAsync(actor.BuildClient, releaseId, deadline.Token)).GetProperty("state").GetString() != "finished")
            await Task.Delay(100, deadline.Token);
        var actual = new[] { await ReadAsync(actor.BuildClient, prepareId), await ReadAsync(actor.BuildClient, windowsId), release };
        var repository = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "Vivarium.slnx"))) repository = repository.Parent;
        using var source = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(repository!.FullName,
            "tests/Vivarium.Tests/TeamCity/Reference/chain-lifecycle-observations.json")));
        var expected = source.RootElement.GetProperty("cases").EnumerateArray().Single(c => c.GetProperty("id").GetString()
            == "chain:" + mode + "-explicit-cancel-edges").GetProperty("finalObservation");
        for (var index = 0; index < actual.Length; index++)
        {
            foreach (var field in new[] { "state", "status", "failedToStart" })
            {
                Assert.That(actual[index].TryGetProperty(field, out var value), Is.EqualTo(expected[index].TryGetProperty(field, out var reference)), field);
                if (value.ValueKind != JsonValueKind.Undefined) Assert.That(value.GetRawText(), Is.EqualTo(reference.GetRawText()), field);
            }
            if (index > 0 && mode != "cancel-root")
            {
                Assert.That(actual[index].GetProperty("statusText").GetString(), Is.EqualTo(expected[index].GetProperty("statusText").GetString()));
                Assert.That(actual[index].GetProperty("canceledInfo").GetProperty("text").GetString(),
                    Is.EqualTo(expected[index].GetProperty("canceledInfo").GetProperty("text").GetString()));
                Assert.That(actual[index].GetProperty("number").GetString(), Is.EqualTo("N/A"));
            }
        }
        await using var database = await host.Services.GetRequiredService<IDbContextFactory<VivariumDbContext>>().CreateDbContextAsync();
        Assert.That(await database.RuntimeBuildExecutions.CountAsync(), Is.EqualTo(mode == "failure" ? 1 : 0), "Canceled dependents never execute.");
    }

    private static async Task<JsonElement> ReadAsync(HttpClient client, long id, CancellationToken token = default)
    {
        using var document = JsonDocument.Parse(await client.GetStringAsync("/app/rest/builds/id:" + id, token));
        return document.RootElement.Clone();
    }

    [Test]
    public async Task Release_entry_runs_all_three_configurations_on_Windows_and_Linux_with_exact_selected_bytes()
        => await WholeChainAsync(viaCli: false);

    [Test]
    public async Task Unchanged_published_CLI_enters_its_own_complete_chain_and_downloads_the_exact_release()
        => await WholeChainAsync(viaCli: true);

    [Test]
    public async Task Native_chain_inspection_exposes_active_edges_requirements_and_actual_selected_Builds()
    {
        await using var host = await installation.CreateCaseAsync(catalogued: true);
        using var administrator = await host.AdministratorAsync(); var csrf = await host.AntiforgeryAsync(administrator);
        await File.WriteAllTextAsync(Path.Combine(host.Definitions, ".vivarium/unbound/P/Settings.g.cs"), Settings);
        await R0AcceptanceCases.CommitCaseAsync(host, "fixture: native chain inspection");
        Assert.That((await R0AcceptanceCases.ReconcileCaseAsync(administrator, csrf)).GetProperty("state").GetString(), Is.EqualTo("applied"));
        using var actor = await R0AcceptanceCases.RestrictedActorAsync(host, administrator, csrf);
        using var configuration = JsonDocument.Parse(await actor.BuildClient.GetStringAsync("/api/v1/build-configurations/P_Release"));
        Assert.That(configuration.RootElement.GetProperty("snapshotDependencies").EnumerateArray().Select(p => p.GetString()), Is.EqualTo(new[] { "P_Windows" }));
        Assert.That(configuration.RootElement.GetProperty("requirements")[0].GetProperty("value").GetString(), Is.EqualTo("linux"));
        using var request = new StringContent("{\"configurationId\":\"P_Release\"}", Encoding.UTF8, "application/json");
        using var accepted = await actor.BuildClient.PostAsync("/api/v1/builds", request);
        Assert.That(accepted.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        using var admission = JsonDocument.Parse(await accepted.Content.ReadAsStringAsync());
        var id = admission.RootElement.GetProperty("id").GetInt64();
        using var selected = JsonDocument.Parse(await actor.BuildClient.GetStringAsync("/api/v1/builds/" + id));
        var windows = selected.RootElement.GetProperty("snapshotDependencies")[0];
        Assert.That(windows.GetProperty("configurationId").GetString(), Is.EqualTo("P_Windows"));
        Assert.That(windows.GetProperty("state").GetString(), Is.EqualTo("queued"));
        Assert.That(selected.RootElement.GetProperty("artifactDependencies").EnumerateArray().Select(p => p.GetProperty("configurationId").GetString()),
            Is.EquivalentTo(new[] { "P_Prepare", "P_Windows" }));
    }

    [Test]
    public async Task Workbench_operator_runs_inspects_logs_and_downloads_its_own_complete_chain()
    {
        await using var host = await installation.CreateCaseAsync(catalogued: true);
        using var administrator = await host.AdministratorAsync(); var csrf = await host.AntiforgeryAsync(administrator);
        await File.WriteAllTextAsync(Path.Combine(host.Definitions, ".vivarium/unbound/P/Settings.g.cs"), Settings);
        await R0AcceptanceCases.CommitCaseAsync(host, "fixture: ordinary Workbench whole-chain journey");
        Assert.That((await R0AcceptanceCases.ReconcileCaseAsync(administrator, csrf)).GetProperty("state").GetString(), Is.EqualTo("applied"));
        await host.StartWindowsAgentAsync(administrator, csrf);
        await using var linux = new OwnedDockerAgent(host.Root);
        await host.StartLinuxAgentAsync(linux, administrator, csrf);
        using var actor = await R0AcceptanceCases.RestrictedActorAsync(host, administrator, csrf);
        using var playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true, AcceptDownloads = true });
        await actor.AddBrowserSessionAsync(context);
        var page = await context.NewPageAsync();
        var response = await page.GotoAsync(host.Origin + "builds");
        Assert.That(response!.Status, Is.EqualTo(200), "The actual Workbench Builds route must exist.");
        await page.GetByRole(Microsoft.Playwright.AriaRole.Button, new() { Name = "P", Exact = true }).ClickAsync();
        await page.GetByRole(Microsoft.Playwright.AriaRole.Button, new() { Name = "Release", Exact = true }).ClickAsync();
        await page.GetByText("system.os.family = linux", new() { Exact = true }).WaitForAsync();
        await page.GetByRole(Microsoft.Playwright.AriaRole.Region, new() { Name = "Snapshot dependencies", Exact = true })
            .GetByRole(Microsoft.Playwright.AriaRole.Link, new() { Name = "P_Windows", Exact = true }).WaitForAsync();
        await page.GetByRole(Microsoft.Playwright.AriaRole.Button, new() { Name = "Run Release", Exact = true }).ClickAsync();
        await page.WaitForURLAsync("**/builds/*");
        var id = long.Parse(new Uri(page.Url).Segments[^1], System.Globalization.CultureInfo.InvariantCulture);
        await page.GetByTestId("build-status").Filter(new() { HasText = "finished · SUCCESS" }).WaitForAsync(new() { Timeout = 90000 });
        await page.GetByRole(Microsoft.Playwright.AriaRole.Region, new() { Name = "Snapshot dependencies", Exact = true })
            .GetByRole(Microsoft.Playwright.AriaRole.Link, new() { Name = "P_Windows", Exact = true }).WaitForAsync();
        await page.GetByRole(Microsoft.Playwright.AriaRole.Button, new() { Name = "Read logs", Exact = true }).ClickAsync();
        await page.GetByTestId("build-log").Filter(new() { HasText = "##teamcity[publishArtifacts 'release.txt']" }).WaitForAsync();
        var download = await page.RunAndWaitForDownloadAsync(() => page.GetByRole(Microsoft.Playwright.AriaRole.Link,
            new() { Name = "release.txt", Exact = true }).ClickAsync());
        var saved = Path.Combine(host.Root, "browser-release.txt"); await download.SaveAsAsync(saved);
        Assert.That(await File.ReadAllBytesAsync(saved), Is.EqualTo("coherent-reference-version\n"u8.ToArray()));
        using var actual = JsonDocument.Parse(await actor.BuildClient.GetStringAsync("/api/v1/builds/" + id));
        Assert.That(actual.RootElement.GetProperty("configurationId").GetString(), Is.EqualTo("P_Release"));
        await using var database = await host.Services.GetRequiredService<IDbContextFactory<VivariumDbContext>>().CreateDbContextAsync();
        Assert.That(await database.RuntimeBuilds.CountAsync(), Is.EqualTo(3), "Only the UI action admits this chain.");
        await page.ReloadAsync();
        await page.GetByTestId("build-status").Filter(new() { HasText = "finished · SUCCESS" }).WaitForAsync();
        Assert.That(await database.RuntimeBuilds.CountAsync(), Is.EqualTo(3), "A deep-link reload never submits work.");
        await page.GetByRole(Microsoft.Playwright.AriaRole.Button, new() { Name = "Read logs", Exact = true }).ClickAsync();
        await page.GetByTestId("build-log").Filter(new() { HasText = "##teamcity[publishArtifacts 'release.txt']" }).WaitForAsync();
        var repository = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "Vivarium.slnx"))) repository = repository.Parent;
        var screenshot = Path.Combine(repository!.FullName, "artifacts/pilot-chain/workbench-" + Guid.NewGuid().ToString("N"));
        await page.ScreenshotAsync(new() { Path = screenshot + "-wide.png", FullPage = true });
        await page.SetViewportSizeAsync(390, 844);
        Assert.That(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= window.innerWidth"), Is.True, "Narrow views must not overflow the viewport.");
        await page.ScreenshotAsync(new() { Path = screenshot + "-narrow.png", FullPage = true });
        TestContext.Out.WriteLine("Workbench screenshots: " + screenshot);
        await R0AcceptanceInstallation.Case.MutateAsync(administrator, csrf, HttpMethod.Delete,
            $"/api/v1/users/{actor.UserId}/roles/OWNED_SIX/projects/P", null, HttpStatusCode.NoContent);
        await page.ReloadAsync();
        await page.GetByRole(Microsoft.Playwright.AriaRole.Alert).Filter(new() { HasText = "This item is no longer available." }).WaitForAsync();
        Assert.That(await page.GetByTestId("build-status").CountAsync(), Is.Zero, "The same cookie no longer discloses the Build after withdrawal.");
        Assert.That(await database.RuntimeBuilds.CountAsync(), Is.EqualTo(3));
    }

    private async Task WholeChainAsync(bool viaCli)
    {
        await using var host = await installation.CreateCaseAsync(catalogued: true);
        using var administrator = await host.AdministratorAsync(); var csrf = await host.AntiforgeryAsync(administrator);
        await File.WriteAllTextAsync(Path.Combine(host.Definitions, ".vivarium/unbound/P/Settings.g.cs"), Settings);
        await R0AcceptanceCases.CommitCaseAsync(host, "fixture: prepare the complete small Linux Windows Release chain");
        Assert.That((await R0AcceptanceCases.ReconcileCaseAsync(administrator, csrf)).GetProperty("state").GetString(), Is.EqualTo("applied"));
        using var actor = await R0AcceptanceCases.RestrictedActorAsync(host, administrator, csrf);
        actor.BuildClient.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        long id = 0;
        if (!viaCli)
        {
            using var request = new StringContent("{\"buildType\":{\"id\":\"P_Release\"}}", Encoding.UTF8, "application/json");
            using var admitted = await actor.BuildClient.PostAsync("/app/rest/buildQueue", request);
            Assert.That(admitted.StatusCode, Is.EqualTo(HttpStatusCode.OK), "Reference chain:ordinary-release-entry-and-selected-bytes admission.");
            using var admission = JsonDocument.Parse(await admitted.Content.ReadAsStringAsync());
            id = admission.RootElement.GetProperty("id").GetInt64();
            Assert.That(admission.RootElement.GetProperty("buildTypeId").GetString(), Is.EqualTo("P_Release"));
            Assert.That(admission.RootElement.GetProperty("state").GetString(), Is.EqualTo("queued"));
        }

        await host.StartWindowsAgentAsync(administrator, csrf); var windowsAgent = host.AgentId;
        await using var linux = new OwnedDockerAgent(host.Root);
        await host.StartLinuxAgentAsync(linux, administrator, csrf); var linuxAgent = host.AgentId;
        Assert.That(linuxAgent, Is.Not.EqualTo(windowsAgent));
        if (viaCli)
        {
            using var current = JsonDocument.Parse(await actor.BuildClient.GetStringAsync("/app/rest/users/current"));
            var cli = new UnchangedCliScenario(host.Root, host.Origin, host.Certificate);
            var credential = actor.BuildClient.DefaultRequestHeaders.Authorization!.Parameter!;
            await cli.AuthenticateAsync(credential, current.RootElement.GetProperty("username").GetString()!);
            id = await cli.RunWholeChainAsync(credential);
        }
        JsonElement release;
        try { release = await FinishedAsync(actor.BuildClient, id); }
        catch
        {
            TestContext.Out.WriteLine(await linux.ProcessStatesAsync());
            await using var database = await host.Services.GetRequiredService<IDbContextFactory<VivariumDbContext>>().CreateDbContextAsync();
            TestContext.Out.WriteLine(JsonSerializer.Serialize(await database.RuntimeBuilds.OrderBy(b => b.Id)
                .Select(b => new { b.Id, b.State, b.Status, b.StatusText }).ToArrayAsync()));
            TestContext.Out.WriteLine(JsonSerializer.Serialize(await database.RuntimeBuildExecutions.OrderBy(e => e.BuildId)
                .Select(e => new { e.BuildId, e.Phase, e.AgentId, e.ReleasedAt }).ToArrayAsync()));
            TestContext.Out.WriteLine(JsonSerializer.Serialize(await database.RuntimeBuildObservations.OrderBy(e => e.BuildId).ThenBy(e => e.Ordinal)
                .Select(e => new { e.BuildId, e.Phase, e.StepIndex, e.ExitCode }).ToArrayAsync()));
            throw;
        }
        Assert.That(release.GetProperty("buildTypeId").GetString(), Is.EqualTo("P_Release"));
        Assert.That(release.TryGetProperty("composite", out var composite) && composite.GetBoolean(), Is.False);
        var windowsId = release.GetProperty("snapshot-dependencies").GetProperty("build").EnumerateArray().Single().GetProperty("id").GetInt64();
        var windows = await FinishedAsync(actor.BuildClient, windowsId);
        Assert.That(windows.GetProperty("buildTypeId").GetString(), Is.EqualTo("P_Windows"));
        var prepareId = windows.GetProperty("snapshot-dependencies").GetProperty("build").EnumerateArray().Single().GetProperty("id").GetInt64();
        var prepare = await FinishedAsync(actor.BuildClient, prepareId);
        Assert.That(prepare.GetProperty("buildTypeId").GetString(), Is.EqualTo("P_Prepare"));
        Assert.That(new[] { id, windowsId, prepareId }.Distinct().Count(), Is.EqualTo(3));
        Assert.That(release.GetProperty("artifact-dependencies").GetProperty("build").EnumerateArray().Select(b => b.GetProperty("id").GetInt64()),
            Is.EquivalentTo(new[] { prepareId, windowsId }));
        foreach (var (build, agent) in new[] { (id, linuxAgent), (windowsId, windowsAgent), (prepareId, linuxAgent) })
        {
            using var native = JsonDocument.Parse(await actor.BuildClient.GetStringAsync("/api/v1/builds/" + build));
            Assert.That(native.RootElement.GetProperty("agentId").GetString(), Is.EqualTo(agent));
        }
        foreach (var (build, file) in new[] { (prepareId, "prepare.txt"), (windowsId, "windows.txt"), (id, "release.txt") })
        {
            using var response = await actor.BuildClient.GetAsync($"/app/rest/builds/id:{build}/artifacts/files/{file}");
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            var bytes = await response.Content.ReadAsByteArrayAsync();
            Assert.That(bytes, Is.EqualTo("coherent-reference-version\n"u8.ToArray()));
            Assert.That(Convert.ToHexStringLower(SHA256.HashData(bytes)), Is.EqualTo("97dc31e36da90b592250f4e30e01d0364a3abcafa36d57fccaccbaedbd6629c8"));
        }
    }

    private static async Task<JsonElement> FinishedAsync(HttpClient client, long id)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        while (true)
        {
            using var response = JsonDocument.Parse(await client.GetStringAsync("/app/rest/builds/id:" + id, deadline.Token));
            var build = response.RootElement;
            if (build.GetProperty("state").GetString() == "finished")
            { Assert.That(build.GetProperty("status").GetString(), Is.EqualTo("SUCCESS")); return build.Clone(); }
            await Task.Delay(100, deadline.Token);
        }
    }

    private const string Settings = """
        using Vivarium.Definitions;
        public static class Managed {
          public static Settings Declare() => new("P", null, [], [
            new("P_Prepare", "Prepare", [], [new("prepare", "Prepare", "simpleRunner", [new("script.content",
              "#!/bin/sh\nset -eu\nprintf 'coherent-reference-version\\n' > prepare.txt\necho \"##teamcity[publishArtifacts 'prepare.txt']\"", null)])],
              [new("system.os.family", "linux")], [], [], 2, []),
            new("P_Windows", "Windows", [], [new("windows", "Windows", "simpleRunner", [new("script.content",
              "copy /b ci-input\\prepare\\prepare.txt windows.txt >nul\nif errorlevel 1 exit /b 71\necho ##teamcity[publishArtifacts 'windows.txt']", null)])],
              [new("system.os.family", "windows")], [new("P_Prepare", "CANCEL", "CANCEL")],
              [new("P_Prepare", "sameChainOrLastFinished", "prepare.txt => ci-input/prepare", true)], 2, []),
            new("P_Release", "Release", [], [new("release", "Release", "simpleRunner", [new("script.content",
              "#!/bin/sh\nset -eu\ncmp ci-input/prepare/prepare.txt ci-input/windows/windows.txt\ncat ci-input/windows/windows.txt > release.txt\necho \"##teamcity[publishArtifacts 'release.txt']\"", null)])],
              [new("system.os.family", "linux")], [new("P_Windows", "CANCEL", "CANCEL")],
              [new("P_Windows", "sameChainOrLastFinished", "windows.txt => ci-input/windows", true),
               new("P_Prepare", "sameChainOrLastFinished", "prepare.txt => ci-input/prepare", true)], 2, [])
          ]);
        }
        """;
}
