using System.Text.Json;
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Vivarium.Controller.Agents;
using Vivarium.Controller.Builds.Runtime;
using Vivarium.Controller.Persistence;

namespace Vivarium.DefinitionProof;

public sealed partial class R0AcceptanceCases
{
    [TestCase(TestName = "R0-RUNNING-CANCEL-BEHAVIOR-001")]
    public async Task Ordinary_running_cancel_stops_the_real_Linux_process_retains_cold_history_and_reuses_the_Agent()
    {
        var clock = new FakeTimeProvider(ReferenceDate("20261102T124139+0000"));
        var wire = new R0AgentMessageGate { GateCompletions = true };
        await using var host = await installation.CreateCaseAsync(configureServices: services =>
        {
            ConfigureReferenceTime(services, clock); services.AddSingleton(wire);
            services.AddGrpc(options => options.Interceptors.Add<R0AgentMessageGate>());
        });
        await using var linux = new OwnedDockerAgent(host.Root);
        try
        {
            using var administrator = await host.AdministratorAsync(); var csrf = await host.AntiforgeryAsync(administrator);
            var path = Path.Combine(host.Definitions, ".vivarium/unbound/P/Settings.g.cs");
            const string script = """
                set -eu
                if [ "${MODE:-}" = long ]; then
                  trap 'i=0; while [ ! -f /data/allow-cancel-exit ] && [ $i -lt 100 ]; do sleep 0.1; i=$((i+1)); done; exit 143' TERM
                  echo owned-running-start
                  while :; do sleep 0.1; done
                fi
                printf 'owned-r0-smoke\n' > smoke.txt
                echo "##teamcity[publishArtifacts 'smoke.txt']"
                """;
            var replaced = 0;
            var text = System.Text.RegularExpressions.Regex.Replace(await File.ReadAllTextAsync(path),
                "\"powershell -NoProfile -InputFormat Text -OutputFormat Text -EncodedCommand ([A-Za-z0-9+/=]+)\"", match =>
                {
                    var decoded = System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(match.Groups[1].Value));
                    if (!decoded.Contains("[IO.File]::WriteAllText('smoke.txt'", StringComparison.Ordinal)) return match.Value;
                    replaced++; return JsonSerializer.Serialize(script);
                });
            Assert.That(replaced, Is.EqualTo(1)); await File.WriteAllTextAsync(path, text);
            await PrepareReferenceNamesAsync(host, administrator, csrf);
            await host.StartLinuxAgentAsync(linux, administrator, csrf);
            using var actor = await RestrictedActorAsync(host, administrator, csrf);
            actor.BuildClient.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            using var admission = await actor.BuildClient.PostAsJsonAsync("/app/rest/buildQueue", new
            { buildType = new { id = "P_LocalSmoke" }, properties = new { property = new[] { new { name = "env.MODE", value = "long" } } } });
            Assert.That(admission.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            using var accepted = JsonDocument.Parse(await admission.Content.ReadAsStringAsync()); var id = accepted.RootElement.GetProperty("id").GetInt64();
            var coordinator = host.Services.GetRequiredService<RuntimeBuildCoordinator>();
            await coordinator.TickAsync(CancellationToken.None); await wire.Acceptance.Arrived.WaitAsync(TimeSpan.FromSeconds(20));
            wire.Acceptance.Release(); wire.Step.Release();
            await WaitOutputAsync(actor.BuildClient, id, page => page.GetProperty("items").GetArrayLength() > 0);
            var initial = await OutputAsync(actor.BuildClient, id);
            Assert.That(await actor.BuildClient.GetStringAsync(initial.GetProperty("items")[0].GetProperty("content").GetProperty("href").GetString()),
                Does.Contain("owned-running-start\n"));
            using var cancellation = await actor.BuildClient.PostAsJsonAsync($"/app/rest/builds/id:{id}", new { readdIntoQueue = false });
            Assert.That(cancellation.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            using var requested = JsonDocument.Parse(await cancellation.Content.ReadAsStringAsync());
            Assert.That(requested.RootElement.GetProperty("state").GetString(), Is.EqualTo("running"));
            Assert.That(requested.RootElement.GetProperty("status").GetString(), Is.EqualTo("UNKNOWN"));
            Assert.That(wire.Completion.Arrived.IsCompleted, Is.False, "Cancel acceptance does not complete the gated real process.");
            await linux.ReleaseCancellationAsync();
            await wire.Completion.Arrived.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.That(wire.CompletionMessage!.Status.Completed.ExitCode, Is.EqualTo(143));
            wire.Completion.Release(); await wire.Collection.Arrived.WaitAsync(TimeSpan.FromSeconds(20));
            var observed = await ReadBuildAsync(actor.BuildClient, id);
            Assert.That(observed.GetProperty("statusText").GetString(), Is.EqualTo("Canceled (Exit code 143 (Step: Owned byte producer (Command Line)) (new))"));
            await host.RestartAsync();
            var cold = await ReadBuildAsync(actor.BuildClient, id);
            foreach (var field in new[] { "id", "number", "configurationId", "definitionRevisionId", "startedAt", "status", "statusText", "cancelRequestedAt" })
                Assert.That(cold.GetProperty(field).GetRawText(), Is.EqualTo(observed.GetProperty(field).GetRawText()));
            wire.Collection.Release(); wire.Result.Release();
            await WaitAsync(async () =>
            {
                await host.Services.GetRequiredService<RuntimeBuildCoordinator>().TickAsync(CancellationToken.None);
                return (await ReadBuildAsync(actor.BuildClient, id)).GetProperty("state").GetString() == "finished";
            });
            using var retained = JsonDocument.Parse(await actor.BuildClient.GetStringAsync($"/app/rest/builds/id:{id}?fields=id,state,status,statusText,agent(id)"));
            Assert.That(retained.RootElement.GetProperty("state").GetString(), Is.EqualTo("finished"));
            Assert.That(retained.RootElement.GetProperty("status").GetString(), Is.EqualTo("UNKNOWN"));
            Assert.That(retained.RootElement.GetProperty("statusText").GetString(), Is.EqualTo(observed.GetProperty("statusText").GetString()));
            Assert.That(retained.RootElement.GetProperty("agent").GetProperty("id").GetInt32(), Is.EqualTo(host.TeamCityAgentId));
            await WaitAsync(async () => (await AgentAsync(administrator, host)).GetProperty("ready").GetBoolean());
            wire.Enabled = false;
            var next = await SubmitAsync(actor.BuildClient, "P_LocalSmoke");
            await WaitAsync(async () =>
            {
                await host.Services.GetRequiredService<RuntimeBuildCoordinator>().TickAsync(CancellationToken.None);
                return (await ReadBuildAsync(actor.BuildClient, next)).GetProperty("state").GetString() == "finished";
            });
            Assert.That(await actor.BuildClient.GetByteArrayAsync($"/app/rest/builds/id:{next}/artifacts/files/smoke.txt"), Is.EqualTo("owned-r0-smoke\n"u8.ToArray()));
            Assert.That((await ReadBuildAsync(actor.BuildClient, next)).GetProperty("agentId").GetString(), Is.EqualTo(host.AgentId));
        }
        finally { ReleaseProgress(wire); }
    }

    [TestCase(TestName = "R0-NONZERO-EXIT-BEHAVIOR-001")]
    public async Task Actual_step_failure_is_current_before_terminal_delivery_and_survives_a_cold_Host_restart()
    {
        var clock = new FakeTimeProvider(ReferenceDate("20261102T124127+0000"));
        var wire = new R0AgentMessageGate { GateCompletions = true };
        await using var host = await installation.CreateCaseAsync(catalogued: true, configureServices: services =>
        {
            ConfigureReferenceTime(services, clock); services.AddSingleton(wire);
            services.AddGrpc(options => options.Interceptors.Add<R0AgentMessageGate>());
        });
        try
        {
            using var administrator = await host.AdministratorAsync(); var csrf = await host.AntiforgeryAsync(administrator);
            await host.StartWindowsAgentAsync(administrator, csrf);
            using var actor = await RestrictedActorAsync(host, administrator, csrf);
            actor.BuildClient.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            using var admission = await actor.BuildClient.PostAsJsonAsync("/app/rest/buildQueue", new
            { buildType = new { id = "P_LocalSmoke" }, properties = new { property = new[] { new { name = "env.MODE", value = "fail" } } } });
            Assert.That(admission.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            using var accepted = JsonDocument.Parse(await admission.Content.ReadAsStringAsync());
            var id = accepted.RootElement.GetProperty("id").GetInt64();
            Assert.That(accepted.RootElement.GetProperty("state").GetString(), Is.EqualTo("queued"));
            var coordinator = host.Services.GetRequiredService<RuntimeBuildCoordinator>();
            await coordinator.TickAsync(CancellationToken.None); await wire.Acceptance.Arrived.WaitAsync(TimeSpan.FromSeconds(20));
            wire.Acceptance.Release(); await wire.Step.Arrived.WaitAsync(TimeSpan.FromSeconds(20));
            clock.Advance(TimeSpan.FromSeconds(1)); wire.Step.Release();
            await wire.Completion.Arrived.WaitAsync(TimeSpan.FromSeconds(20)); wire.Completion.Release();
            await wire.Collection.Arrived.WaitAsync(TimeSpan.FromSeconds(20));
            var current = await ReadBuildAsync(actor.BuildClient, id);
            Assert.That(current.GetProperty("state").GetString(), Is.EqualTo("running"));
            Assert.That(current.GetProperty("status").GetString(), Is.EqualTo("FAILURE"));
            Assert.That(current.GetProperty("statusText").GetString(), Is.EqualTo("Exit code 7 (Step: Owned byte producer (Command Line)) (new)"));
            actor.BuildClient.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            using (var compatible = JsonDocument.Parse(await actor.BuildClient.GetStringAsync($"/app/rest/builds/id:{id}?fields=state,status,statusText")))
            {
                Assert.That(compatible.RootElement.GetProperty("state").GetString(), Is.EqualTo("running"));
                Assert.That(compatible.RootElement.GetProperty("status").GetString(), Is.EqualTo("FAILURE"));
                Assert.That(compatible.RootElement.GetProperty("statusText").GetString(), Is.EqualTo(current.GetProperty("statusText").GetString()));
            }
            var sessions = host.Services.GetRequiredService<RuntimeAgentSessions>();
            Assert.That(sessions.TryGetCurrent(host.AgentId!, out var connection), Is.True);
            await coordinator.ReceiveAsync(connection!, wire.CompletionMessage!.Clone(), CancellationToken.None);
            await using (var scope = host.Services.CreateAsyncScope())
            {
                var database = scope.ServiceProvider.GetRequiredService<VivariumDbContext>();
                Assert.That(await database.RuntimeBuildObservations.CountAsync(o => o.BuildId == id), Is.EqualTo(3));
                Assert.That(await database.RuntimeBuildResults.AnyAsync(r => r.BuildId == id), Is.False);
            }
            var immutable = Assert.ThrowsAsync<PostgresException>(() => host.ExecuteDatabaseCommandAsync(
                $"UPDATE \"RuntimeBuildObservations\" SET \"ExitCode\" = 8 WHERE \"BuildId\" = {id} AND \"Phase\" = 'COMPLETED'"));
            Assert.That(immutable!.SqlState, Is.EqualTo("23514"));
            await host.RestartAsync();
            var cold = await ReadBuildAsync(actor.BuildClient, id);
            foreach (var field in new[] { "id", "number", "configurationId", "definitionRevisionId", "startedAt", "status", "statusText" })
                Assert.That(cold.GetProperty(field).GetRawText(), Is.EqualTo(current.GetProperty(field).GetRawText()));
            wire.Collection.Release(); wire.Result.Release();
            await WaitAsync(async () =>
            {
                await host.Services.GetRequiredService<RuntimeBuildCoordinator>().TickAsync(CancellationToken.None);
                return (await ReadBuildAsync(actor.BuildClient, id)).GetProperty("state").GetString() == "finished";
            });
            Assert.That((await ReadBuildAsync(actor.BuildClient, id)).GetProperty("statusText").GetString(), Is.EqualTo(current.GetProperty("statusText").GetString()));
            using var control = JsonDocument.Parse(await administrator.GetStringAsync($"/app/rest/builds/id:{id}?fields=id,state,status,statusText"));
            Assert.That(control.RootElement.GetProperty("state").GetString(), Is.EqualTo("finished"));
            Assert.That(control.RootElement.GetProperty("status").GetString(), Is.EqualTo("FAILURE"));
            Assert.That(control.RootElement.GetProperty("statusText").GetString(), Is.EqualTo(current.GetProperty("statusText").GetString()));
        }
        finally { ReleaseProgress(wire); }
    }

    [TestCase("step")]
    [TestCase("terminal")]
    public async Task Conflicting_actual_step_receipts_quarantine_the_owner_and_cannot_rewrite_observed_failure(string conflict)
    {
        var clock = new FakeTimeProvider(ReferenceDate("20261102T124127+0000"));
        var wire = new R0AgentMessageGate();
        await using var host = await installation.CreateCaseAsync(catalogued: true, configureServices: services =>
        {
            ConfigureReferenceTime(services, clock); services.AddSingleton(wire);
            services.AddGrpc(options => options.Interceptors.Add<R0AgentMessageGate>());
        });
        try
        {
            using var administrator = await host.AdministratorAsync(); var csrf = await host.AntiforgeryAsync(administrator);
            await host.StartWindowsAgentAsync(administrator, csrf);
            using var actor = await RestrictedActorAsync(host, administrator, csrf);
            var id = await SubmitAsync(actor.BuildClient, "P_LocalSmoke", new() { ["env.MODE"] = "fail" });
            var coordinator = host.Services.GetRequiredService<RuntimeBuildCoordinator>();
            await coordinator.TickAsync(CancellationToken.None); await wire.Acceptance.Arrived.WaitAsync(TimeSpan.FromSeconds(20));
            wire.Acceptance.Release(); wire.Step.Release(); await wire.Collection.Arrived.WaitAsync(TimeSpan.FromSeconds(20));
            var before = await ReadBuildAsync(actor.BuildClient, id);
            var changed = wire.CompletionMessage!.Clone(); changed.Status.Completed.ExitCode = 8;
            if (conflict == "terminal")
            {
                wire.Collection.Release(); await wire.Result.Arrived.WaitAsync(TimeSpan.FromSeconds(20));
                changed = wire.ResultMessage!.Clone(); changed.Result.Steps.Single(s => s.StepIndex == 0).ExitCode = 8;
            }
            Assert.That(host.Services.GetRequiredService<RuntimeAgentSessions>().TryGetCurrent(host.AgentId!, out var connection), Is.True);
            Assert.ThrowsAsync<InvalidDataException>(() => coordinator.ReceiveAsync(connection!, changed, CancellationToken.None));
            var quarantined = await AgentAsync(administrator, host);
            Assert.That(quarantined.GetProperty("ready").GetBoolean(), Is.False);
            Assert.That(quarantined.GetProperty("healthReason").GetString(), Is.EqualTo("conflicting_step_result"));
            var retained = await ReadBuildAsync(actor.BuildClient, id);
            Assert.That(retained.GetProperty("statusText").GetString(), Is.EqualTo(before.GetProperty("statusText").GetString()));
            await using (var scope = host.Services.CreateAsyncScope())
            {
                var database = scope.ServiceProvider.GetRequiredService<VivariumDbContext>();
                Assert.That((await database.RuntimeBuildObservations.SingleAsync(o => o.BuildId == id && o.Phase == "COMPLETED")).ExitCode, Is.EqualTo(7));
                Assert.That(await database.RuntimeBuildResults.AnyAsync(r => r.BuildId == id), Is.False);
            }
            var next = await SubmitAsync(actor.BuildClient, "P_LocalSmoke"); await coordinator.TickAsync(CancellationToken.None);
            Assert.That((await ReadBuildAsync(actor.BuildClient, next)).GetProperty("state").GetString(), Is.EqualTo("queued"));
        }
        finally { ReleaseProgress(wire); }
    }

    private static void ReleaseProgress(R0AgentMessageGate wire)
    { wire.Acceptance.Release(); wire.Step.Release(); wire.Completion.Release(); wire.Collection.Release(); wire.Result.Release(); }
}
