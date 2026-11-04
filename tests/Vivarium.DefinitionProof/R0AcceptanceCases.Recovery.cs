using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Vivarium.Controller.Agents;
using Vivarium.Controller.Builds.Runtime;
using Vivarium.Controller.Persistence;
using EyeAuras.Storage;

namespace Vivarium.DefinitionProof;

public sealed partial class R0AcceptanceCases
{
    [Test]
    public async Task Restart_between_Storage_commit_and_PG_reference_keeps_the_orphan_private_and_retries_the_owned_output()
    {
        var handoff = new R0OutputByteHandoff();
        await using var host = await installation.CreateCaseAsync(catalogued: true, configureServices: services =>
            services.Replace(ServiceDescriptor.Singleton(provider => new RuntimeBuildBytes(provider.GetRequiredService<IObjectStorage>(), handoff.Handler()))));
        try
        {
            using var administrator = await host.AdministratorAsync(); var csrf = await host.AntiforgeryAsync(administrator);
            await host.StartWindowsAgentAsync(administrator, csrf);
            using var actor = await RestrictedActorAsync(host, administrator, csrf);
            var gate = Path.Combine(host.Root, "byte-handoff-gate"); Directory.CreateDirectory(gate);
            var id = await SubmitAsync(actor.BuildClient, "P_Log", new() { ["env.CASE_DIR"] = gate });
            var prefix = await WaitOutputAsync(actor.BuildClient, id, page => page.GetProperty("items").GetArrayLength() >= 2);
            handoff.Arm(); await File.WriteAllTextAsync(Path.Combine(gate, "release"), "release");
            await handoff.Stored.WaitAsync(TimeSpan.FromSeconds(15));
            string orphan;
            await using (var scope = host.Services.CreateAsyncScope())
                orphan = await scope.ServiceProvider.GetRequiredService<VivariumDbContext>().RuntimeOutputDeletions.Select(d => d.ObjectKey).SingleAsync();
            var unpublished = await OutputAsync(actor.BuildClient, id);
            Assert.That(unpublished.GetProperty("items").GetRawText(), Is.EqualTo(prefix.GetProperty("items").GetRawText()),
                "Successful private Storage PUT alone is not published output.");
            await host.RestartAsync(); handoff.Release();
            await host.Services.GetRequiredService<RuntimeOutputRetention>().TickAsync(CancellationToken.None);
            var missing = Assert.ThrowsAsync<HttpRequestException>(() => host.Services.GetRequiredService<RuntimeBuildBytes>().ReadAsync(
                orphan, 15, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData("owned-log-tail\n"u8)), CancellationToken.None));
            Assert.That(missing!.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            await FinishedAsync(actor.BuildClient, id);
            var complete = await OutputAsync(actor.BuildClient, id);
            var streams = new Dictionary<string, List<byte>> { ["stdout"] = [], ["stderr"] = [] };
            await AppendAsync(actor.BuildClient, complete, streams, []);
            Assert.That(Encoding.UTF8.GetString(streams["stdout"].ToArray()), Is.EqualTo("owned-log-stdout\nowned-log-tail\n"));
            Assert.That(Encoding.UTF8.GetString(streams["stderr"].ToArray()), Is.EqualTo("owned-log-stderr\n"));
            Assert.That(complete.GetProperty("items").EnumerateArray().Take(2).Select(e => e.GetRawText()),
                Is.EqualTo(prefix.GetProperty("items").EnumerateArray().Select(e => e.GetRawText())));
        }
        finally { handoff.Release(); }
    }

    [Test]
    public async Task Lost_output_acknowledgements_replay_across_Host_restart_without_duplicate_or_rebased_entries()
    {
        var wire = new R0AgentWireFaults { DropOutputAcknowledgements = true };
        await using var host = await installation.CreateCaseAsync(catalogued: true, configureServices: services =>
        { services.AddSingleton(wire); services.AddGrpc(options => options.Interceptors.Add<R0AgentWireFaults>()); });
        using var administrator = await host.AdministratorAsync(); var csrf = await host.AntiforgeryAsync(administrator);
        await host.StartWindowsAgentAsync(administrator, csrf);
        using var actor = await RestrictedActorAsync(host, administrator, csrf);
        var gate = Path.Combine(host.Root, "replay-gate"); Directory.CreateDirectory(gate);
        var id = await SubmitAsync(actor.BuildClient, "P_Log", new() { ["env.CASE_DIR"] = gate });
        var prefix = await WaitOutputAsync(actor.BuildClient, id, page => page.GetProperty("items").GetArrayLength() >= 2);
        var cursor = prefix.GetProperty("output").GetProperty("resumeCursor").GetString();
        Assert.That(wire.DroppedAcknowledgements, Is.GreaterThanOrEqualTo(2));
        Assert.That(wire.ObservedSessionCount, Is.EqualTo(1));
        var originalItems = prefix.GetProperty("items").GetRawText();
        var originalBuild = await ReadBuildAsync(actor.BuildClient, id);
        await host.RestartAsync();
        await WaitAsync(() => Task.FromResult(wire.ObservedSessionCount >= 2));
        var replayed = await OutputAsync(actor.BuildClient, id);
        Assert.That(replayed.GetProperty("items").GetRawText(), Is.EqualTo(originalItems));
        var sameBuild = await ReadBuildAsync(actor.BuildClient, id);
        foreach (var field in new[] { "id", "agentId", "configurationId", "definitionRevisionId", "definitionHash", "startedAt" })
            Assert.That(sameBuild.GetProperty(field).GetRawText(), Is.EqualTo(originalBuild.GetProperty(field).GetRawText()));
        var resumed = await OutputAsync(actor.BuildClient, id, cursor);
        Assert.That(resumed.GetProperty("items").GetArrayLength(), Is.Zero);
        wire.DropOutputAcknowledgements = false;
        await File.WriteAllTextAsync(Path.Combine(gate, "release"), "release");
        await FinishedAsync(actor.BuildClient, id);
        var complete = await OutputAsync(actor.BuildClient, id);
        var streams = new Dictionary<string, List<byte>> { ["stdout"] = [], ["stderr"] = [] };
        await AppendAsync(actor.BuildClient, complete, streams, []);
        Assert.That(Encoding.UTF8.GetString(streams["stdout"].ToArray()), Is.EqualTo("owned-log-stdout\nowned-log-tail\n"));
        Assert.That(Encoding.UTF8.GetString(streams["stderr"].ToArray()), Is.EqualTo("owned-log-stderr\n"));
        await host.RestartAsync();
        var cold = await OutputAsync(actor.BuildClient, id);
        Assert.That(cold.GetProperty("items").GetRawText(), Is.EqualTo(complete.GetProperty("items").GetRawText()));
        Assert.That(cold.GetProperty("output").GetProperty("state").GetString(), Is.EqualTo("complete"));
    }

    [Test]
    public async Task Missing_final_output_seals_known_gaps_only_at_the_deadline_and_late_delivery_preserves_the_sealed_history()
    {
        var wire = new R0AgentWireFaults { DropOutputData = true }; var clock = new AcceptanceClock();
        await using var host = await installation.CreateCaseAsync(catalogued: true, configureServices: services =>
        {
            services.AddSingleton(wire); services.AddGrpc(options => options.Interceptors.Add<R0AgentWireFaults>());
            services.Replace(ServiceDescriptor.Singleton(provider => new RuntimeBuildOutputStore(
                provider.GetRequiredService<IDbContextFactory<VivariumDbContext>>(), provider.GetRequiredService<RuntimeBuildBytes>(),
                provider.GetRequiredService<AgentRegistry>(), clock, provider.GetRequiredService<ILogger<RuntimeBuildOutputStore>>())));
        });
        using var administrator = await host.AdministratorAsync(); var csrf = await host.AntiforgeryAsync(administrator);
        await host.StartWindowsAgentAsync(administrator, csrf);
        using var actor = await RestrictedActorAsync(host, administrator, csrf);
        var id = await SubmitAsync(actor.BuildClient, "P_LocalSmoke");
        var sealing = await WaitOutputAsync(actor.BuildClient, id, page => page.GetProperty("output").GetProperty("state").GetString() == "sealing");
        Assert.That(sealing.GetProperty("items").GetArrayLength(), Is.Zero);
        var notFinished = await ReadBuildAsync(actor.BuildClient, id);
        Assert.That(notFinished.GetProperty("state").GetString(), Is.Not.EqualTo("finished"));
        Assert.That(wire.Observed.Length, Is.Positive);
        clock.Advance(TimeSpan.FromSeconds(31));
        await FinishedAsync(actor.BuildClient, id);
        var sealedOutput = await OutputAsync(actor.BuildClient, id);
        Assert.That(sealedOutput.GetProperty("output").GetProperty("state").GetString(), Is.EqualTo("complete-with-gaps"));
        foreach (var gap in sealedOutput.GetProperty("items").EnumerateArray())
        {
            Assert.That(gap.GetProperty("kind").GetString(), Is.EqualTo("gap"));
            Assert.That(gap.GetProperty("reason").GetString(), Is.EqualTo("delivery-deadline"));
            Assert.That(gap.GetProperty("sourceSequenceStart").GetString(), Is.EqualTo("0"));
            Assert.That(ulong.Parse(gap.GetProperty("sourceSequenceEndExclusive").GetString()!), Is.Positive);
            Assert.That(gap.TryGetProperty("content", out _), Is.False);
        }
        Assert.That(sealedOutput.GetProperty("items").GetArrayLength(), Is.EqualTo(1));
        var captured = wire.Observed.SelectMany(frame => frame.Data.ToByteArray()).ToArray();
        Assert.That(Encoding.UTF8.GetString(captured), Is.EqualTo("owned-r0-stdout\n##teamcity[publishArtifacts 'smoke.txt']\r\n"));
        wire.DropOutputData = false;
        await WaitAsync(async () =>
        {
            using var state = JsonDocument.Parse(await administrator.GetStringAsync("/api/v1/agents/" + host.AgentId));
            return state.RootElement.GetProperty("ready").GetBoolean();
        });
        var later = await OutputAsync(actor.BuildClient, id);
        Assert.That(later.GetProperty("items").GetRawText(), Is.EqualTo(sealedOutput.GetProperty("items").GetRawText()));
        var next = await SubmitAsync(actor.BuildClient, "P_Empty"); await FinishedAsync(actor.BuildClient, next);
    }

    [Test]
    public async Task Lost_producer_without_a_watermark_retains_an_unknown_tail_and_quarantines_capacity()
    {
        var clock = new AcceptanceClock();
        await using var host = await installation.CreateCaseAsync(catalogued: true, configureServices: services =>
            services.Replace(ServiceDescriptor.Singleton(provider => new RuntimeBuildCoordinator(
                provider.GetRequiredService<IDbContextFactory<VivariumDbContext>>(), provider.GetRequiredService<RuntimeAgentSessions>(),
                provider.GetRequiredService<AgentRegistry>(), provider.GetRequiredService<RuntimeStepCompiler>(),
                provider.GetRequiredService<RuntimeBuildOutputStore>(), clock, provider.GetRequiredService<ILogger<RuntimeBuildCoordinator>>(),
                provider.GetRequiredService<RuntimeBuildSecrets>(), provider.GetRequiredService<RuntimeBuildSources>()))));
        using var administrator = await host.AdministratorAsync(); var csrf = await host.AntiforgeryAsync(administrator);
        await host.StartWindowsAgentAsync(administrator, csrf);
        using var actor = await RestrictedActorAsync(host, administrator, csrf);
        var gate = Path.Combine(host.Root, "lost-producer-gate"); Directory.CreateDirectory(gate);
        var id = await SubmitAsync(actor.BuildClient, "P_Log", new() { ["env.CASE_DIR"] = gate });
        var prefix = await WaitOutputAsync(actor.BuildClient, id, page => page.GetProperty("items").GetArrayLength() >= 2);
        await host.StopOwnedAgentAsync(); clock.Advance(TimeSpan.FromMinutes(1));
        var finished = await WaitBuildStateAsync(actor.BuildClient, id, "finished");
        Assert.That(finished.GetProperty("status").GetString(), Is.EqualTo("UNKNOWN"));
        Assert.That(finished.GetProperty("failedToStart").GetBoolean(), Is.False);
        var lost = await OutputAsync(actor.BuildClient, id);
        Assert.That(lost.GetProperty("output").GetProperty("state").GetString(), Is.EqualTo("complete-with-gaps"));
        var items = lost.GetProperty("items").EnumerateArray().ToArray();
        Assert.That(items.Take(2).Select(e => e.GetRawText()), Is.EqualTo(prefix.GetProperty("items").EnumerateArray().Select(e => e.GetRawText())));
        Assert.That(items.Length, Is.EqualTo(4));
        foreach (var tail in items.Skip(2))
        {
            Assert.That(tail.GetProperty("kind").GetString(), Is.EqualTo("gap"));
            Assert.That(tail.GetProperty("reason").GetString(), Is.EqualTo("producer-lost"));
            Assert.That(tail.GetProperty("sourceSequenceEndExclusive").ValueKind, Is.EqualTo(JsonValueKind.Null));
            Assert.That(tail.TryGetProperty("content", out _), Is.False);
        }
        using var state = JsonDocument.Parse(await administrator.GetStringAsync("/api/v1/agents/" + host.AgentId));
        Assert.That(state.RootElement.GetProperty("ready").GetBoolean(), Is.False);
        Assert.That(state.RootElement.GetProperty("quarantined").GetBoolean(), Is.True);
        await using var scope = host.Services.CreateAsyncScope(); var database = scope.ServiceProvider.GetRequiredService<VivariumDbContext>();
        Assert.That(await database.RuntimeBuildResults.CountAsync(r => r.BuildId == id), Is.Zero, "A lost producer has no invented process result.");
        var owner = await database.AgentRegistrations.SingleAsync(a => a.AgentId == host.AgentId);
        Assert.That(owner.CurrentWorkloadId, Is.Not.Null);
    }

    private static async Task<JsonElement> WaitOutputAsync(HttpClient client, long id, Func<JsonElement, bool> predicate)
    {
        JsonElement result = default;
        await WaitAsync(async () => { result = await OutputAsync(client, id); return predicate(result); }); return result;
    }
    private static async Task<JsonElement> WaitBuildStateAsync(HttpClient client, long id, string state)
    {
        JsonElement result = default;
        await WaitAsync(async () => { result = await ReadBuildAsync(client, id); return result.GetProperty("state").GetString() == state; }); return result;
    }
    private static async Task WaitAsync(Func<Task<bool>> predicate)
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!await predicate()) await Task.Delay(50, bound.Token);
    }
}
