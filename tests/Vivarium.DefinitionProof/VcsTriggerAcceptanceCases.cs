using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vivarium.Controller.Persistence;

namespace Vivarium.DefinitionProof;

[NonParallelizable, Category("US013VcsTrigger")]
public sealed class VcsTriggerAcceptanceCases
{
    [Test]
    public async Task Used_trigger_excludes_metadata_coalesces_revisions_and_respects_completed_manual_coverage()
    {
        var repository = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "Vivarium.slnx"))) repository = repository.Parent;
        using var reference = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(repository!.FullName,
            "tests/Vivarium.Tests/TeamCity/Reference/vcs-trigger-observations.json")));
        Assert.That(reference.RootElement.GetProperty("complete").GetBoolean(), Is.True);
        foreach (var flag in new[] { "ownedProjectRemoved", "ownedResourcesRemoved", "permissionsRestored" })
            Assert.That(reference.RootElement.GetProperty(flag).GetBoolean(), Is.True);
        var oracle = reference.RootElement.GetProperty("cases").EnumerateArray()
            .Single(c => c.GetProperty("id").GetString() == "trigger:quiet-period-and-coalesced-source").GetProperty("terminalObservation");
        await using var installation = new R0AcceptanceInstallation(); await installation.StartAsync();
        await using var host = await installation.CreateCaseAsync(catalogued: true);
        using var administrator = await host.AdministratorAsync(); var csrf = await host.AntiforgeryAsync(administrator);
        await using var git = new OwnedGitSource(host.Root); await git.StartAsync(); await git.ChangeAsync();
        var settings = SourceReuseAcceptanceCases.Settings.Replace("SOURCE_URL", JsonSerializer.Serialize(git.Url), StringComparison.Ordinal);
        settings = settings.Replace("2, [], \"ON_AGENT\")\n  ]", "2, [new(\"vcs\", \"+:<default>\", 60, \"USE_CUSTOM\", \"-:README.md\\n-:AGENTS.md\\n-:docs/operations/**\\n-:.teamcity/**\")], \"ON_AGENT\")\n  ]", StringComparison.Ordinal);
        Assert.That(settings, Does.Contain("new(\"vcs\""));
        await File.WriteAllTextAsync(Path.Combine(host.Definitions, ".vivarium/unbound/P/Settings.g.cs"), settings);
        await R0AcceptanceCases.CommitCaseAsync(host, "fixture: used default branch VCS trigger");
        Assert.That((await ReconcileAsync(administrator, csrf)).GetProperty("state").GetString(), Is.EqualTo("applied"));
        using var actor = await R0AcceptanceCases.RestrictedActorAsync(host, administrator, csrf);
        actor.BuildClient.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        await using var linux = new OwnedDockerAgent(host.Root, gitTools: true); await host.StartLinuxAgentAsync(linux, administrator, csrf);
        await ManualAsync(); TestContext.Progress.WriteLine("VCS trigger: manual baseline finished");
        var before = await RootsAsync(); Assert.That(before, Has.Length.EqualTo(1));
        await git.MetadataChangeAsync();
        await ObserveNoNewRootAsync(before.Length, TimeSpan.FromSeconds(70));
        TestContext.Progress.WriteLine("VCS trigger: metadata exclusions observed");
        await git.ChangeAsync("first relevant\n"); await Task.Delay(TimeSpan.FromSeconds(15));
        var second = await git.ChangeAsync("coalesced relevant\n"); var changed = DateTimeOffset.UtcNow;
        await ObserveNoNewRootAsync(before.Length, TimeSpan.FromSeconds(59));
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(65)))
        {
            while ((await RootsAsync()).Length == before.Length) await Task.Delay(250, deadline.Token);
        }
        var automatic = (await RootsAsync()).Single(b => !before.Contains(b));
        var root = await FinishedAsync(automatic);
        Assert.That(root.GetProperty("status").GetString(), Is.EqualTo("SUCCESS"));
        Assert.That(root.GetProperty("triggered").GetProperty("type").GetString(), Is.EqualTo(oracle[2].GetProperty("triggered").GetProperty("type").GetString()));
        Assert.That(root.GetProperty("triggered").TryGetProperty("user", out _), Is.False);
        using (var native = JsonDocument.Parse(await actor.BuildClient.GetStringAsync("/api/v1/builds/" + automatic)))
            Assert.That(native.RootElement.GetProperty("triggered").GetProperty("type").GetString(), Is.EqualTo("vcs"));
        Assert.That(root.GetProperty("revisions").GetProperty("revision")[0].GetProperty("version").GetString(), Is.EqualTo(second));
        await using (var db = await host.Services.GetRequiredService<IDbContextFactory<VivariumDbContext>>().CreateDbContextAsync())
            Assert.That((await db.RuntimeBuilds.SingleAsync(b => b.Id == automatic)).QueuedAt - changed, Is.GreaterThanOrEqualTo(TimeSpan.FromSeconds(60)));
        var third = await git.ChangeAsync("manual coverage\n"); await ManualAsync(third);
        await host.RestartAsync();
        await ObserveNoNewRootAsync(3, TimeSpan.FromSeconds(70));

        async Task<long[]> RootsAsync()
        {
            await using var db = await host.Services.GetRequiredService<IDbContextFactory<VivariumDbContext>>().CreateDbContextAsync();
            return await (from b in db.RuntimeBuilds join t in db.CatalogBuildTypes on b.BuildTypeId equals t.Id
                where t.ExternalId == "P_Release" orderby b.Id select b.Id).ToArrayAsync();
        }
        async Task ObserveNoNewRootAsync(int count, TimeSpan duration)
        {
            var end = DateTimeOffset.UtcNow + duration;
            do { Assert.That(await RootsAsync(), Has.Length.EqualTo(count)); await Task.Delay(250); } while (DateTimeOffset.UtcNow < end);
        }
        async Task<JsonElement> FinishedAsync(long id)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            while (true)
            {
                using var response = JsonDocument.Parse(await actor.BuildClient.GetStringAsync("/app/rest/builds/id:" + id, deadline.Token));
                if (response.RootElement.GetProperty("state").GetString() == "finished") return response.RootElement.Clone();
                await Task.Delay(100, deadline.Token);
            }
        }
        async Task ManualAsync(string? revision = null)
        {
            using var response = await actor.BuildClient.PostAsJsonAsync("/app/rest/buildQueue", new { buildType = new { id = "P_Release" } });
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            using var accepted = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = await FinishedAsync(accepted.RootElement.GetProperty("id").GetInt64());
            Assert.That(root.GetProperty("status").GetString(), Is.EqualTo("SUCCESS"));
            if (revision is not null) Assert.That(root.GetProperty("revisions").GetProperty("revision")[0].GetProperty("version").GetString(), Is.EqualTo(revision));
        }
    }
    internal static async Task<JsonElement> ReconcileAsync(HttpClient administrator, string csrf)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        while (true)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/configuration-changes/reconcile");
            request.Headers.Add("RequestVerificationToken", csrf);
            using var response = await administrator.SendAsync(request, deadline.Token);
            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert.That(problem.RootElement.GetProperty("code").GetString(), Is.EqualTo("catalogue_busy"));
                await Task.Delay(100, deadline.Token); continue;
            }
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
            while (true)
            {
                using var state = JsonDocument.Parse(await administrator.GetStringAsync(response.Headers.Location, deadline.Token));
                if (state.RootElement.GetProperty("state").GetString() is not ("pending" or "evaluated")) return state.RootElement.Clone();
                await Task.Delay(100, deadline.Token);
            }
        }
    }

}
