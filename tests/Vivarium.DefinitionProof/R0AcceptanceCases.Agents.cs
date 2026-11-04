using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vivarium.Contracts.V1;
using Vivarium.Controller.Agents;
using Vivarium.Controller.Builds.Runtime;
using Vivarium.Controller.Persistence;

namespace Vivarium.DefinitionProof;

public sealed partial class R0AcceptanceCases
{
    [Test]
    public async Task Missing_reported_platform_does_not_claim_work_or_stop_the_scheduler_and_corrected_reconnect_recovers()
    {
        var wire = new R0AgentWireFaults { OmitPlatform = true };
        await using var host = await installation.CreateCaseAsync(catalogued: true, configureServices: services =>
        { services.AddSingleton(wire); services.AddGrpc(options => options.Interceptors.Add<R0AgentWireFaults>()); });
        using var administrator = await host.AdministratorAsync(); var csrf = await host.AntiforgeryAsync(administrator);
        await host.StartWindowsAgentAsync(administrator, csrf);
        using var actor = await RestrictedActorAsync(host, administrator, csrf);
        var id = await SubmitAsync(actor.BuildClient, "P_Empty");
        await host.Services.GetRequiredService<RuntimeBuildCoordinator>().TickAsync(CancellationToken.None);
        var queued = await ReadBuildAsync(actor.BuildClient, id);
        Assert.That(queued.GetProperty("state").GetString(), Is.EqualTo("queued"));
        await using (var scope = host.Services.CreateAsyncScope())
            Assert.That(await scope.ServiceProvider.GetRequiredService<VivariumDbContext>().RuntimeBuildExecutions.CountAsync(e => e.BuildId == id), Is.Zero);
        wire.OmitPlatform = false;
        await host.StopOwnedAgentAsync(); await host.RestartOwnedAgentAsync();
        await FinishedAsync(actor.BuildClient, id);
        Assert.That((await ReadBuildAsync(actor.BuildClient, id)).GetProperty("status").GetString(), Is.EqualTo("SUCCESS"));
    }

    [Test]
    public async Task Readiness_keeps_authorization_enablement_requirements_occupancy_and_connection_independent()
    {
        await using var host = await installation.CreateCaseAsync(catalogued: true);
        using var administrator = await host.AdministratorAsync(); var csrf = await host.AntiforgeryAsync(administrator);
        await host.StartWindowsAgentAsync(administrator, csrf);
        var pending = host.PendingRegistration;
        Assert.That(pending.GetProperty("connected").GetBoolean(), Is.True);
        Assert.That(pending.GetProperty("authorized").GetBoolean(), Is.False);
        Assert.That(pending.GetProperty("ready").GetBoolean(), Is.False);
        var authorized = host.AuthorizedRegistration;
        Assert.That(authorized.GetProperty("authorized").GetBoolean(), Is.True);
        Assert.That(authorized.GetProperty("reconciled").GetBoolean(), Is.True);
        Assert.That(authorized.GetProperty("enabled").GetBoolean(), Is.False);
        Assert.That(authorized.GetProperty("ready").GetBoolean(), Is.False);
        Assert.That(authorized.GetProperty("connectionGeneration").GetInt64(), Is.GreaterThan(pending.GetProperty("connectionGeneration").GetInt64()));
        using var actor = await RestrictedActorAsync(host, administrator, csrf);
        await R0AcceptanceInstallation.Case.MutateAsync(actor.BuildClient, csrf, HttpMethod.Put,
            "/api/v1/agents/" + host.AgentId + "/authorization", "{\"authorized\":false}", HttpStatusCode.Forbidden);
        await R0AcceptanceInstallation.Case.MutateAsync(actor.BuildClient, csrf, HttpMethod.Put,
            $"/app/rest/agents/id:{host.TeamCityAgentId}/enabledInfo", "{\"status\":false}", HttpStatusCode.Forbidden);
        var allowed = await AgentAsync(administrator, host);
        Assert.That(allowed.GetProperty("ready").GetBoolean(), Is.True);
        Assert.That(allowed.GetProperty("authorized").GetBoolean(), Is.True);
        Assert.That(allowed.GetProperty("enabled").GetBoolean(), Is.True);
        var enabledPath = $"/app/rest/agents/id:{host.TeamCityAgentId}/enabledInfo";
        await R0AcceptanceInstallation.Case.MutateAsync(administrator, csrf, HttpMethod.Put, enabledPath,
            "{\"status\":false,\"comment\":{\"text\":\"Owned readiness gate\"}}", HttpStatusCode.OK);
        var disabled = await AgentAsync(administrator, host);
        Assert.That(disabled.GetProperty("connected").GetBoolean(), Is.True);
        Assert.That(disabled.GetProperty("authorized").GetBoolean(), Is.True);
        Assert.That(disabled.GetProperty("ready").GetBoolean(), Is.False);
        var queuedId = await SubmitAsync(actor.BuildClient, "P_LocalSmoke");
        var queued = await ReadBuildAsync(actor.BuildClient, queuedId);
        Assert.That(queued.GetProperty("state").GetString(), Is.EqualTo("queued"));
        Assert.That(queued.GetProperty("agentId").ValueKind, Is.EqualTo(JsonValueKind.Null));
        await R0AcceptanceInstallation.Case.MutateAsync(administrator, csrf, HttpMethod.Put, enabledPath,
            "{\"status\":true,\"comment\":{\"text\":\"Owned readiness release\"}}", HttpStatusCode.OK);
        await FinishedAsync(actor.BuildClient, queuedId);

        var settingsPath = Path.Combine(host.Definitions, ".vivarium/unbound/P/Settings.g.cs");
        var settings = await File.ReadAllTextAsync(settingsPath);
        var candidate = settings.Replace("new(\"P_LocalSmoke\",", """
            new("P_LinuxOnly", "Linux only", [], [new("linux", "Linux", "simpleRunner", [new("script.content", "echo should-not-run", null)])],
                [new("system.os.family", "linux")], [], [], 1, []),
            new("P_LocalSmoke",
            """, StringComparison.Ordinal);
        Assert.That(candidate, Is.Not.EqualTo(settings)); await File.WriteAllTextAsync(settingsPath, candidate);
        await CommitCaseAsync(host, "fixture: require Linux independently of general Agent readiness");
        Assert.That((await ReconcileCaseAsync(administrator, csrf)).GetProperty("state").GetString(), Is.EqualTo("applied"));
        var incompatible = await SubmitAsync(actor.BuildClient, "P_LinuxOnly");
        var compatible = await SubmitAsync(actor.BuildClient, "P_LocalSmoke"); await FinishedAsync(actor.BuildClient, compatible);
        var waiting = await ReadBuildAsync(actor.BuildClient, incompatible);
        Assert.That(waiting.GetProperty("state").GetString(), Is.EqualTo("queued"));
        Assert.That(waiting.GetProperty("agentId").ValueKind, Is.EqualTo(JsonValueKind.Null));
        Assert.That(waiting.GetProperty("number").ValueKind, Is.EqualTo(JsonValueKind.Null));
        await R0AcceptanceInstallation.Case.MutateAsync(actor.BuildClient, csrf, HttpMethod.Put,
            $"/api/v1/builds/{incompatible}/cancellation", "{\"reason\":\"Owned incompatible fixture complete\"}", HttpStatusCode.Accepted);

        var gate = Path.Combine(host.Root, "occupancy-gate"); Directory.CreateDirectory(gate);
        var busy = await SubmitAsync(actor.BuildClient, "P_Log", new() { ["env.CASE_DIR"] = gate });
        await WaitOutputAsync(actor.BuildClient, busy, page => page.GetProperty("items").GetArrayLength() >= 2);
        var occupied = await AgentAsync(administrator, host);
        Assert.That(occupied.GetProperty("connected").GetBoolean(), Is.True);
        Assert.That(occupied.GetProperty("authorized").GetBoolean(), Is.True);
        Assert.That(occupied.GetProperty("enabled").GetBoolean(), Is.True);
        Assert.That(occupied.GetProperty("activity").GetString(), Is.EqualTo("building"));
        Assert.That(occupied.GetProperty("ready").GetBoolean(), Is.False);
        await File.WriteAllTextAsync(Path.Combine(gate, "release"), "release"); await FinishedAsync(actor.BuildClient, busy);
        await WaitAsync(async () => (await AgentAsync(administrator, host)).GetProperty("ready").GetBoolean());
        var beforeRestart = await AgentAsync(administrator, host);
        await host.StopOwnedAgentAsync();
        await WaitAsync(async () => !(await AgentAsync(administrator, host)).GetProperty("connected").GetBoolean());
        Assert.That((await AgentAsync(administrator, host)).GetProperty("ready").GetBoolean(), Is.False);
        await host.RestartOwnedAgentAsync();
        await WaitAsync(async () => (await AgentAsync(administrator, host)).GetProperty("ready").GetBoolean());
        var reconnected = await AgentAsync(administrator, host);
        Assert.That(reconnected.GetProperty("id").GetString(), Is.EqualTo(host.AgentId));
        Assert.That(reconnected.GetProperty("credentialGeneration").GetInt64(), Is.EqualTo(beforeRestart.GetProperty("credentialGeneration").GetInt64()));
        Assert.That(reconnected.GetProperty("connectionGeneration").GetInt64(), Is.GreaterThan(beforeRestart.GetProperty("connectionGeneration").GetInt64()));
    }

    [Test]
    public async Task Reconnected_assignment_keeps_one_process_owner_and_rejects_stale_ACKs_without_duplicate_results()
    {
        await using var host = await installation.CreateCaseAsync(catalogued: true);
        using var administrator = await host.AdministratorAsync(); var csrf = await host.AntiforgeryAsync(administrator);
        await host.StartWindowsAgentAsync(administrator, csrf);
        using var actor = await RestrictedActorAsync(host, administrator, csrf);
        var gate = Path.Combine(host.Root, "ownership-gate"); Directory.CreateDirectory(gate);
        var id = await SubmitAsync(actor.BuildClient, "P_Log", new() { ["env.CASE_DIR"] = gate });
        await WaitOutputAsync(actor.BuildClient, id, page => page.GetProperty("items").GetArrayLength() >= 2);
        await WaitAsync(async () => File.Exists(Path.Combine(gate, "entered")) && (await File.ReadAllTextAsync(Path.Combine(gate, "entered"))).EndsWith('\n'));
        RuntimeBuildExecution before;
        await using (var scope = host.Services.CreateAsyncScope())
            before = await scope.ServiceProvider.GetRequiredService<VivariumDbContext>().RuntimeBuildExecutions.AsNoTracking().SingleAsync(e => e.BuildId == id);
        var sessions = host.Services.GetRequiredService<RuntimeAgentSessions>();
        Assert.That(sessions.TryGetCurrent(host.AgentId!, out var old), Is.True);
        await old!.Lifetime.CancelAsync();
        await WaitAsync(async () =>
        {
            var state = await AgentAsync(administrator, host);
            return state.GetProperty("connectionGeneration").GetInt64() > before.CurrentConnectionGeneration
                && state.GetProperty("reconciled").GetBoolean() && state.GetProperty("connected").GetBoolean();
        });
        Assert.That(sessions.TryGetCurrent(host.AgentId!, out var current), Is.True);
        var coordinator = host.Services.GetRequiredService<RuntimeBuildCoordinator>();
        Assert.ThrowsAsync<InvalidDataException>(() => coordinator.ReceiveAsync(old, new AgentMsg
            { AssignmentAccepted = new() { BuildId = before.WorkloadId, SessionId = old.Handle.SessionId } }, CancellationToken.None));
        await coordinator.ReceiveAsync(current!, new AgentMsg
            { AssignmentAccepted = new() { BuildId = before.WorkloadId, SessionId = current!.Handle.SessionId } }, CancellationToken.None);
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<VivariumDbContext>();
            var after = await database.RuntimeBuildExecutions.AsNoTracking().SingleAsync(e => e.BuildId == id);
            Assert.That(after.AgentId, Is.EqualTo(before.AgentId)); Assert.That(after.LeaseFence, Is.EqualTo(before.LeaseFence));
            Assert.That(after.SelectionSnapshotJson, Is.EqualTo(before.SelectionSnapshotJson));
            Assert.That(after.AssignedConnectionGeneration, Is.EqualTo(before.AssignedConnectionGeneration));
            Assert.That(after.CurrentConnectionGeneration, Is.GreaterThan(before.CurrentConnectionGeneration));
            Assert.That(await database.AuditEvents.CountAsync(e => e.Action == "build.assignment.accepted" && e.TargetId == id.ToString()), Is.EqualTo(1));
        }
        Assert.That((await File.ReadAllLinesAsync(Path.Combine(gate, "entered"))).Length, Is.EqualTo(1));
        await File.WriteAllTextAsync(Path.Combine(gate, "release"), "release"); await FinishedAsync(actor.BuildClient, id);
        await WaitAsync(async () => (await AgentAsync(administrator, host)).GetProperty("ready").GetBoolean());
        await RuntimeResultReplayScenario.VerifyAsync(host.Services, id);
        Assert.That((await File.ReadAllLinesAsync(Path.Combine(gate, "entered"))).Length, Is.EqualTo(1));
        var next = await SubmitAsync(actor.BuildClient, "P_LocalSmoke"); await FinishedAsync(actor.BuildClient, next);
    }

    private static async Task<JsonElement> AgentAsync(HttpClient administrator, R0AcceptanceInstallation.Case host)
    {
        using var document = JsonDocument.Parse(await administrator.GetStringAsync("/api/v1/agents/" + host.AgentId));
        return document.RootElement.Clone();
    }
}
