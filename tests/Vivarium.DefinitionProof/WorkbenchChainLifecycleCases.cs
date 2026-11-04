using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Vivarium.Controller.Persistence;

namespace Vivarium.DefinitionProof;

[NonParallelizable, Category("US013WorkbenchLifecycle")]
public sealed class WorkbenchChainLifecycleCases
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task Operator_cancels_an_upstream_from_the_actual_UI_and_finds_the_retained_chain_history(bool running)
    {
        await using var installation = new R0AcceptanceInstallation(); await installation.StartAsync();
        await using var host = await installation.CreateCaseAsync(catalogued: true);
        using var administrator = await host.AdministratorAsync(); var csrf = await host.AntiforgeryAsync(administrator);
        var settings = PilotAcceptanceCases.Settings;
        if (running) settings = settings.Replace("set -eu\\n", "set -eu\\necho owned-ui-running\\nsleep 90\\n", StringComparison.Ordinal);
        await File.WriteAllTextAsync(Path.Combine(host.Definitions, ".vivarium/unbound/P/Settings.g.cs"), settings);
        await R0AcceptanceCases.CommitCaseAsync(host, "fixture: Workbench chain cancellation and history");
        Assert.That((await VcsTriggerAcceptanceCases.ReconcileAsync(administrator, csrf)).GetProperty("state").GetString(), Is.EqualTo("applied"));
        using var actor = await R0AcceptanceCases.RestrictedActorAsync(host, administrator, csrf);
        actor.BuildClient.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        await using var linux = new OwnedDockerAgent(host.Root);
        if (running) await host.StartLinuxAgentAsync(linux, administrator, csrf);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true });
        await actor.AddBrowserSessionAsync(context); var page = await context.NewPageAsync();
        await page.GotoAsync(host.Origin + "build-configurations/P_Release");
        await page.GetByRole(AriaRole.Region, new() { Name = "Build history", Exact = true }).GetByText("No builds on this page.", new() { Exact = true }).WaitForAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Run Release", Exact = true }).ClickAsync();
        await page.WaitForURLAsync("**/builds/*"); var root = long.Parse(new Uri(page.Url).Segments[^1]);
        await page.GetByTestId("build-status").WaitForAsync(); // pushState precedes the asynchronous Build view.
        await page.GetByRole(AriaRole.Region, new() { Name = "Snapshot dependencies", Exact = true }).GetByRole(AriaRole.Link, new() { Name = "P_Windows", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Region, new() { Name = "Snapshot dependencies", Exact = true }).GetByRole(AriaRole.Link, new() { Name = "P_Prepare", Exact = true }).ClickAsync();
        var target = long.Parse(new Uri(page.Url).Segments[^1]);
        await page.GetByTestId("build-status").Filter(new() { HasText = running ? "running" : "queued" }).WaitForAsync(new() { Timeout = 30000 });
        if (running)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (true)
            {
                await using var db = await host.Services.GetRequiredService<IDbContextFactory<VivariumDbContext>>().CreateDbContextAsync();
                if (await db.RuntimeBuildObservations.AnyAsync(o => o.BuildId == target && o.Phase == "RUNNING", deadline.Token)) break;
                await Task.Delay(100, deadline.Token);
            }
            while (true)
            {
                await page.GetByRole(AriaRole.Button, new() { Name = "Read logs", Exact = true }).ClickAsync();
                await Task.Delay(300, deadline.Token);
                if ((await page.GetByTestId("build-log").InnerTextAsync()).Contains("owned-ui-running", StringComparison.Ordinal)) break;
            }
        }
        else await page.GetByText("No Agent assigned.", new() { Exact = true }).WaitForAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Cancel build", Exact = true }).ClickAsync();
        await page.GetByTestId("build-status").Filter(new() { HasText = "finished · UNKNOWN" }).WaitForAsync(new() { Timeout = 60000 });
        await page.GotoAsync(host.Origin + "builds/" + root);
        await page.GetByTestId("build-status").Filter(new() { HasText = "finished · UNKNOWN" }).WaitForAsync(new() { Timeout = 30000 });
        await page.GetByRole(AriaRole.Link, new() { Name = "Configuration history", Exact = true }).ClickAsync();
        var history = page.GetByRole(AriaRole.Region, new() { Name = "Build history", Exact = true });
        await history.GetByText("finished · UNKNOWN", new() { Exact = false }).WaitForAsync();
        Assert.That(await history.GetByRole(AriaRole.Link).CountAsync(), Is.EqualTo(1));
        await history.GetByRole(AriaRole.Link).ClickAsync(); await page.WaitForURLAsync("**/builds/" + root);
        await host.RestartAsync(); await page.ReloadAsync();
        await page.GetByTestId("build-status").Filter(new() { HasText = "finished · UNKNOWN" }).WaitForAsync();
        if (running)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (true)
            {
                await using var db = await host.Services.GetRequiredService<IDbContextFactory<VivariumDbContext>>().CreateDbContextAsync();
                var agent = await db.AgentRegistrations.AsNoTracking().SingleAsync(deadline.Token);
                Assert.That(agent.Quarantined, Is.False, "Post-cancel recovery health: " + agent.HealthReason);
                if (agent.SessionAuthenticated && agent.Reconciled && agent.CurrentWorkloadId is null) break;
                await Task.Delay(100, deadline.Token);
            }
        }
        await using (var db = await host.Services.GetRequiredService<IDbContextFactory<VivariumDbContext>>().CreateDbContextAsync())
        {
            Assert.That(await db.RuntimeBuilds.CountAsync(), Is.EqualTo(3));
            Assert.That(await db.RuntimeBuildExecutions.CountAsync(), Is.EqualTo(running ? 1 : 0));
            Assert.That(await db.RuntimeBuilds.AllAsync(b => b.State == "FINISHED" && b.Status == "UNKNOWN"), Is.True);
            Assert.That(await db.AgentRegistrations.AnyAsync(a => a.Quarantined || a.CurrentWorkloadId != null), Is.False);
        }
        await R0AcceptanceInstallation.Case.MutateAsync(administrator, csrf, HttpMethod.Delete,
            $"/api/v1/users/{actor.UserId}/roles/OWNED_SIX/projects/P", null, HttpStatusCode.NoContent);
        await page.GotoAsync(host.Origin + "build-configurations/P_Release");
        await page.GetByRole(AriaRole.Alert).Filter(new() { HasText = "This item is no longer available." }).WaitForAsync();
        Assert.That(await page.GetByRole(AriaRole.Region, new() { Name = "Build history", Exact = true }).CountAsync(), Is.Zero);
    }
}
