using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Vivarium.Controller.Agents;
using Vivarium.Controller.Builds.Runtime;
using Vivarium.Controller.Persistence;

namespace Vivarium.DefinitionProof;

[NonParallelizable, Category("US013VcsTriggerRecovery")]
public sealed class VcsTriggerRecoveryCases
{
    [Test]
    public async Task Pending_deadline_survives_restart_consumes_once_and_cannot_dispatch_after_Git_withdrawal()
    {
        await using var installation = new R0AcceptanceInstallation(); await installation.StartAsync();
        await using var host = await installation.CreateCaseAsync(catalogued: true, configureServices: services =>
            services.Remove(services.Single(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(RuntimeBuildWorker))));
        using var administrator = await host.AdministratorAsync(); var csrf = await host.AntiforgeryAsync(administrator);
        await using var git = new OwnedGitSource(host.Root); await git.StartAsync();
        const string declaration = "[new(\"vcs\", \"+:<default>\", 60, \"USE_CUSTOM\", \"-:README.md\\n-:AGENTS.md\\n-:docs/operations/**\\n-:.teamcity/**\")]";
        var settings = SourceReuseAcceptanceCases.Settings.Replace("SOURCE_URL", JsonSerializer.Serialize(git.Url), StringComparison.Ordinal)
            .Replace("2, [], \"ON_AGENT\")\n  ]", "2, " + declaration + ", \"ON_AGENT\")\n  ]", StringComparison.Ordinal);
        Assert.That(settings, Does.Contain(declaration));
        var path = Path.Combine(host.Definitions, ".vivarium/unbound/P/Settings.g.cs");
        await File.WriteAllTextAsync(path, settings);
        await R0AcceptanceCases.CommitCaseAsync(host, "fixture: durable trigger observation");
        Assert.That((await VcsTriggerAcceptanceCases.ReconcileAsync(administrator, csrf)).GetProperty("state").GetString(), Is.EqualTo("applied"));
        using var actor = await R0AcceptanceCases.RestrictedActorAsync(host, administrator, csrf);
        actor.BuildClient.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        var clock = new FakeTimeProvider(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds())); var (observer, builds) = Services();
        await observer.TickAsync(CancellationToken.None);
        var version = await git.ChangeAsync(); await observer.TickAsync(CancellationToken.None);
        var pending = await PendingAsync(); Assert.That(pending.DueAt, Is.EqualTo(clock.GetUtcNow().AddSeconds(60)));
        clock.Advance(TimeSpan.FromSeconds(59)); await observer.TickAsync(CancellationToken.None);
        Assert.That(await CountAsync(), Is.Zero);
        await host.RestartAsync(); (observer, builds) = Services();
        await observer.TickAsync(CancellationToken.None);
        var recovered = await PendingAsync(); Assert.That(recovered.PendingId, Is.EqualTo(pending.PendingId)); Assert.That(recovered.DueAt, Is.EqualTo(pending.DueAt));
        var admission = new RuntimeVcsAdmission(recovered.BuildTypeId, recovered.RevisionId, recovered.PendingId!.Value, version);
        Assert.That(await builds.SubmitVcsAsync(admission, CancellationToken.None), Is.Null, "Before the exact deadline nothing is admitted.");
        clock.Advance(TimeSpan.FromSeconds(1));
        var outcomes = await Task.WhenAll(builds.SubmitVcsAsync(admission, CancellationToken.None), builds.SubmitVcsAsync(admission, CancellationToken.None));
        var accepted = outcomes.Single(o => o is not null)!;
        Assert.That(outcomes.Count(o => o is null), Is.EqualTo(1)); Assert.That(await CountAsync(), Is.EqualTo(3));
        await using (var database = await DatabaseAsync())
        {
            Assert.That(await database.RuntimeBuildVcsTriggers.CountAsync(), Is.EqualTo(3));
            Assert.That(await database.RuntimeBuildSourceSelections.Select(s => s.Version).Distinct().ToArrayAsync(), Is.EqualTo(new[] { version }));
            Assert.That(await database.RuntimeBuilds.AllAsync(b => b.ActorId == Guid.Empty && b.CredentialKind == "vcs-trigger"), Is.True);
            Assert.That(await database.RuntimeBuildExecutions.CountAsync(), Is.Zero);
        }
        using (var native = JsonDocument.Parse(await actor.BuildClient.GetStringAsync("/api/v1/builds/" + accepted.Id)))
            Assert.That(native.RootElement.GetProperty("triggered").GetProperty("eventId").GetGuid(), Is.EqualTo(admission.EventId));
        using (var history = JsonDocument.Parse(await actor.BuildClient.GetStringAsync("/api/v1/build-configurations/P_Release/builds?offset=0&limit=1")))
        {
            Assert.That(history.RootElement.GetProperty("total").GetInt32(), Is.EqualTo(1));
            Assert.That(history.RootElement.GetProperty("items")[0].GetProperty("id").GetInt64(), Is.EqualTo(accepted.Id));
            Assert.That(history.RootElement.GetProperty("items")[0].GetProperty("state").GetString(), Is.EqualTo("queued"));
        }
        using (var history = JsonDocument.Parse(await actor.BuildClient.GetStringAsync("/api/v1/build-configurations/P_Release/builds?offset=1&limit=1")))
            Assert.That(history.RootElement.GetProperty("items").GetArrayLength(), Is.Zero);
        using (var invalid = await actor.BuildClient.GetAsync("/api/v1/build-configurations/P_Release/builds?limit=0")) Assert.That(invalid.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        await R0AcceptanceInstallation.Case.MutateAsync(administrator, csrf, HttpMethod.Delete,
            $"/api/v1/users/{actor.UserId}/roles/OWNED_SIX/projects/P", null, HttpStatusCode.NoContent);
        using (var denied = await actor.BuildClient.GetAsync("/api/v1/builds/" + accepted.Id)) Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        using (var denied = await actor.BuildClient.GetAsync("/app/rest/builds/id:" + accepted.Id)) Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        using (var denied = await actor.BuildClient.GetAsync("/api/v1/build-configurations/P_Release/builds")) Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        using (var denied = await actor.BuildClient.PutAsJsonAsync("/api/v1/builds/" + accepted.Id + "/cancellation", new { reason = "withdrawn", mode = "graceful" }))
            Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        await git.ChangeAsync("withdraw before deadline\n"); await observer.TickAsync(CancellationToken.None); var withdrawn = await PendingAsync();
        await File.WriteAllTextAsync(path, settings.Replace(declaration, "[]", StringComparison.Ordinal));
        await R0AcceptanceCases.CommitCaseAsync(host, "fixture: withdraw trigger before dispatch");
        Assert.That((await VcsTriggerAcceptanceCases.ReconcileAsync(administrator, csrf)).GetProperty("state").GetString(), Is.EqualTo("applied"));
        clock.Advance(TimeSpan.FromSeconds(60));
        Assert.That(await builds.SubmitVcsAsync(new(withdrawn.BuildTypeId, withdrawn.RevisionId, withdrawn.PendingId!.Value, withdrawn.PendingVersion!), CancellationToken.None), Is.Null);
        await observer.TickAsync(CancellationToken.None); Assert.That(await CountAsync(), Is.EqualTo(3));

        Task<VivariumDbContext> DatabaseAsync() => host.Services.GetRequiredService<IDbContextFactory<VivariumDbContext>>().CreateDbContextAsync();
        async Task<int> CountAsync() { await using var db = await DatabaseAsync(); return await db.RuntimeBuilds.CountAsync(); }
        async Task<RuntimeVcsTriggerState> PendingAsync() { await using var db = await DatabaseAsync(); return await db.RuntimeVcsTriggerStates.AsNoTracking().SingleAsync(); }
        (RuntimeVcsTriggers, RuntimeBuildService) Services()
        {
            var services = host.Services; var factory = services.GetRequiredService<IDbContextFactory<VivariumDbContext>>(); var config = services.GetRequiredService<IConfiguration>();
            var service = new RuntimeBuildService(factory, config, services.GetRequiredService<AgentRegistry>(), clock,
                services.GetRequiredService<RuntimeBuildSecrets>(), services.GetRequiredService<RuntimeBuildRunInputs>());
            return (new(factory, config, service, clock, services.GetRequiredService<ILogger<RuntimeVcsTriggers>>()), service);
        }
    }
}
