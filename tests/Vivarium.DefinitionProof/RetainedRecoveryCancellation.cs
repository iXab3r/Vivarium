using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Vivarium.Controller.Builds.Runtime;

namespace Vivarium.DefinitionProof;

public sealed partial class ActualT3AcceptanceCases
{
    [Test, Category("US013IsolatedReopen")]
    public async Task Retained_reopen_composes_common_authorization_and_cancellation_without_a_worker()
    {
        await using var installation = new R0AcceptanceInstallation(retainDatabase: true);
        await installation.StartAsync();
        void Isolate(IServiceCollection services) => services.Remove(services.Single(s =>
            s.ServiceType == typeof(IHostedService) && s.ImplementationType == typeof(RuntimeBuildWorker)));
        var host = await installation.CreateCaseAsync(catalogued: true, configureServices: Isolate);
        var checkpoint = Path.Combine(host.Root, "recovery.private.json");
        using var administrator = await host.AdministratorAsync();
        var csrf = await host.AntiforgeryAsync(administrator);
        using var actor = await R0AcceptanceCases.RestrictedActorAsync(host, administrator, csrf);
        using var admission = await actor.BuildClient.PostAsJsonAsync("/api/v1/builds", new { configurationId = "P_LocalSmoke" });
        Assert.That(admission.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        using var accepted = JsonDocument.Parse(await admission.Content.ReadAsStringAsync());
        var id = accepted.RootElement.GetProperty("id").GetInt64();
        await host.DisposeAsync();
        await using (var reopened = await R0AcceptanceInstallation.ReopenRetainedAsync(checkpoint, Isolate))
        {
            Assert.That(reopened.Services.GetServices<IHostedService>().Any(s => s is RuntimeBuildWorker), Is.False);
            await reopened.RestartAsync();
            Assert.That(reopened.Services.GetServices<IHostedService>().Any(s => s is RuntimeBuildWorker), Is.False);
            using var view = JsonDocument.Parse(await actor.BuildClient.GetStringAsync($"/api/v1/builds/{id}"));
            Assert.That(view.RootElement.GetProperty("state").GetString(), Is.EqualTo("queued"));
            using var anonymous = reopened.Client();
            using var denied = await anonymous.PutAsJsonAsync($"/api/v1/builds/{id}/cancellation", new { mode = "graceful" });
            Assert.That(denied.IsSuccessStatusCode, Is.False);
            using var cancelled = await actor.BuildClient.PutAsJsonAsync($"/api/v1/builds/{id}/cancellation", new { mode = "graceful" });
            Assert.That(cancelled.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
            using var final = JsonDocument.Parse(await actor.BuildClient.GetStringAsync($"/api/v1/builds/{id}"));
            Assert.That(final.RootElement.GetProperty("state").GetString(), Is.EqualTo("finished"));
            await using var database = await DatabaseAsync(reopened);
            Assert.That(await database.RuntimeBuildExecutions.CountAsync(), Is.Zero);
            Assert.That(await database.AuditEvents.AnyAsync(a => a.Action == "build.cancel" && a.TargetId == id.ToString()), Is.True);
            using var stale = new RuntimeBuildWorker(reopened.Services);
            await stale.StartAsync(CancellationToken.None);
            await Task.Delay(500);
            stale.EnsureHealthy();
            await reopened.RestartAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (true)
            {
                try { stale.EnsureHealthy(); }
                catch (InvalidOperationException error)
                {
                    Assert.That(error.Message, Does.Contain("ObjectDisposedException"));
                    break;
                }
                await Task.Delay(100, deadline.Token);
            }
            using var current = new RuntimeBuildWorker(reopened.Services);
            await current.StartAsync(CancellationToken.None);
            await Task.Delay(500);
            current.EnsureHealthy();
            await current.StopAsync(CancellationToken.None);
        }
        installation.ReleaseRetainedPostgres();
    }

    [Test, Explicit("One-time authorized retained installation cleanup"), Category("US013RetainedCancel")]
    public async Task Retained_recovery_queue_is_cancelled_without_dispatch()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Retained credential requires Windows DPAPI.");
        var root = Path.Combine(Repository(), "artifacts/w/a0e29c82/r0_case_35700a740669");
        var checkpoint = Path.Combine(root, "recovery.private.json");
        using (var saved = JsonDocument.Parse(await File.ReadAllTextAsync(checkpoint)))
        {
            Assert.That(saved.RootElement.GetProperty("databaseName").GetString(), Is.EqualTo("r0_case_35700a740669"));
            Assert.That(saved.RootElement.GetProperty("postgres").GetProperty("name").GetString(), Is.EqualTo("vivarium-us013-proof-cee6f093c34d"));
        }
        await using var host = await R0AcceptanceInstallation.ReopenRetainedAsync(checkpoint, services =>
        {
            foreach (var worker in services.Where(s => s.ServiceType == typeof(IHostedService) && s.ImplementationType == typeof(RuntimeBuildWorker)).ToArray())
                services.Remove(worker);
        });
        Assert.That(host.Services.GetServices<IHostedService>().Any(s => s is RuntimeBuildWorker), Is.False);
        using var actor = host.Client();
        var file = Directory.GetFiles(Path.Combine(root, "actual-t3-cli/cli-state/roaming/teamcity-cli/owned-r0/secrets"), "*.dpapi").Single();
        var bytes = ProtectedData.Unprotect(await File.ReadAllBytesAsync(file), Encoding.UTF8.GetBytes("[\"ai-cli-factory:teamcity-cli\",\"owned-r0:token\"]"), DataProtectionScope.CurrentUser);
        try { actor.DefaultRequestHeaders.Authorization = new("Bearer", Encoding.UTF8.GetString(bytes)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
        using var identity = JsonDocument.Parse(await actor.GetStringAsync("/api/v1/users/current"));
        var actorId = identity.RootElement.GetProperty("id").GetGuid();
        await using var database = await DatabaseAsync(host);
        var before = await database.RuntimeBuilds.AsNoTracking().OrderBy(b => b.Id).ToArrayAsync();
        Assert.That(before.Select(b => b.Id), Is.EqualTo(new long[] { 1, 2, 3, 4, 5, 6 }));
        Assert.That(before.All(b => b.ActorId == actorId), Is.True);
        var original = JsonSerializer.Serialize(before.Where(b => b.Id <= 3));
        var executionCount = await database.RuntimeBuildExecutions.CountAsync();
        Assert.That(await database.RuntimeBuildExecutions.AnyAsync(e => e.BuildId > 3), Is.False);
        Assert.That(await database.RuntimeBuildResolutions.AnyAsync(r => r.RequestedBuildId > 3), Is.False);
        Assert.That(await database.RuntimeBuildSourceSelections.AnyAsync(s => s.BuildId > 3), Is.False);
        foreach (var (id, configuration) in new[] { (4L, "T3Personal_Prepare"), (5L, "T3Personal_Windows"), (6L, "T3Personal_Release") })
        {
            var build = before.Single(b => b.Id == id);
            Assert.That(build.State, Is.EqualTo("QUEUED"));
            Assert.That(build.Counter, Is.Null);
            Assert.That(build.StartedAt, Is.Null);
            Assert.That((await database.CatalogBuildTypes.SingleAsync(t => t.Id == build.BuildTypeId)).ExternalId, Is.EqualTo(configuration));
            using var view = JsonDocument.Parse(await actor.GetStringAsync($"/api/v1/builds/{id}"));
            Assert.That(view.RootElement.GetProperty("id").GetInt64(), Is.EqualTo(id));
            Assert.That(view.RootElement.GetProperty("state").GetString(), Is.EqualTo("queued"));
        }
        Assert.That(await database.RuntimeBuildDependencies.AnyAsync(d => d.BuildId == 6 && d.UpstreamBuildId == 5), Is.True);
        Assert.That(await database.RuntimeBuildDependencies.AnyAsync(d => d.BuildId == 5 && d.UpstreamBuildId == 4), Is.True);
        var output = Path.Combine(Repository(), "artifacts/pilot-chain/isolated-cancel-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(output);
        object Projection(IEnumerable<RuntimeBuild> rows) => rows.Select(b => new { b.Id, b.ActorId, b.BuildTypeId, b.State, b.Status, b.StatusText, b.Counter, b.StartedAt, b.FinishedAt, b.CancelActorId, b.CancelOperationId, b.StopMode });
        await File.WriteAllTextAsync(Path.Combine(output, "before.json"), JsonSerializer.Serialize(new { builds = Projection(before), executionCount, workerPresent = false }));
        foreach (var id in new long[] { 6, 5, 4 })
        {
            using var response = await actor.PutAsJsonAsync($"/api/v1/builds/{id}/cancellation", new { reason = "Owned retained recovery cleanup", mode = "graceful" });
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
            using var accepted = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.That(accepted.RootElement.GetProperty("id").GetInt64(), Is.EqualTo(id));
            using var view = JsonDocument.Parse(await actor.GetStringAsync($"/api/v1/builds/{id}"));
            Assert.That(view.RootElement.GetProperty("state").GetString(), Is.EqualTo("finished"));
            var build = await database.RuntimeBuilds.AsNoTracking().SingleAsync(b => b.Id == id);
            Assert.That(build.Status, Is.EqualTo("UNKNOWN"));
            Assert.That(build.StatusText, Is.EqualTo("Cancelled before execution"));
            Assert.That(build.CancelActorId, Is.EqualTo(actorId));
            Assert.That(await database.RuntimeBuildExecutions.AnyAsync(e => e.BuildId > 3), Is.False);
            Assert.That(await database.AuditEvents.AnyAsync(a => a.Action == "build.cancel" && a.TargetId == id.ToString()), Is.True);
            await File.WriteAllTextAsync(Path.Combine(output, $"cancel-{id}.json"), JsonSerializer.Serialize(new { httpStatus = (int)response.StatusCode, build = Projection(new[] { build }) }));
        }
        var after = await database.RuntimeBuilds.AsNoTracking().OrderBy(b => b.Id).ToArrayAsync();
        Assert.That(JsonSerializer.Serialize(after.Where(b => b.Id <= 3)), Is.EqualTo(original));
        Assert.That(await database.RuntimeBuildExecutions.CountAsync(), Is.EqualTo(executionCount));
        await File.WriteAllTextAsync(Path.Combine(output, "after.json"), JsonSerializer.Serialize(new { builds = Projection(after), executionCount, originalsUnchanged = true,
            audit = await database.AuditEvents.AsNoTracking().Where(a => a.Action == "build.cancel" && (a.TargetId == "4" || a.TargetId == "5" || a.TargetId == "6")).ToArrayAsync() }));
    }
}
