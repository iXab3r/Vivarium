using System.Net;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using EyeAuras.Storage;
using Vivarium.Controller.Agents;
using Vivarium.Controller.Builds.Runtime;
using Vivarium.Controller.Persistence;

namespace Vivarium.DefinitionProof;

public sealed partial class R0AcceptanceCases
{
    [Test]
    public async Task Terminal_owning_Agent_cannot_allocate_new_private_artifacts_and_exact_upload_replay_preserves_bytes()
    {
        var wire = new R0AgentWireFaults { DropResultAcknowledgements = true };
        await using var host = await installation.CreateCaseAsync(catalogued: true, configureServices: services =>
        { services.AddSingleton(wire); services.AddGrpc(options => options.Interceptors.Add<R0AgentWireFaults>()); });
        using var administrator = await host.AdministratorAsync(); var csrf = await host.AntiforgeryAsync(administrator);
        await host.StartWindowsAgentAsync(administrator, csrf);
        using var actor = await RestrictedActorAsync(host, administrator, csrf);
        var id = await SubmitAsync(actor.BuildClient, "P_LocalSmoke"); await FinishedAsync(actor.BuildClient, id);
        using var agent = await AgentByteClientAsync(host, id);
        for (var index = 0; index < 8; index++)
        {
            var content = System.Text.Encoding.UTF8.GetBytes("owned-after-terminal-" + index);
            var hash = Convert.ToHexStringLower(SHA256.HashData(content));
            using var denied = await AgentUploadAsync(agent, content);
            Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
            var missing = Assert.ThrowsAsync<HttpRequestException>(() => host.Services.GetRequiredService<RuntimeBuildBytes>()
                .ReadAsync($"build-artifacts/{id}/{hash}", content.Length, hash, CancellationToken.None));
            Assert.That(missing!.StatusCode, Is.EqualTo(HttpStatusCode.NotFound), "A rejected new hash must not allocate private Storage bytes.");
        }
        using var replay = await AgentUploadAsync(agent, "owned-r0-smoke\n"u8.ToArray());
        Assert.That(replay.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        await using var database = await host.Services.GetRequiredService<IDbContextFactory<VivariumDbContext>>().CreateDbContextAsync();
        Assert.That(await database.RuntimeArtifactUploads.CountAsync(u => u.BuildId == id), Is.EqualTo(1));
        Assert.That(await database.RuntimeOutputDeletions.CountAsync(d => d.BuildId == id), Is.Zero);
    }

    [Test]
    public async Task Artifact_PUT_racing_actual_process_stop_leaves_durable_cleanup_instead_of_an_unindexed_object()
    {
        var handoff = new R0OutputByteHandoff();
        var wire = new R0AgentWireFaults { DropResultAcknowledgements = true };
        await using var host = await installation.CreateCaseAsync(catalogued: true, configureServices: services =>
        {
            services.AddSingleton(wire); services.AddGrpc(options => options.Interceptors.Add<R0AgentWireFaults>());
            services.Replace(ServiceDescriptor.Singleton(provider => new RuntimeBuildBytes(provider.GetRequiredService<IObjectStorage>(), handoff.Handler())));
        });
        try
        {
            using var administrator = await host.AdministratorAsync(); var csrf = await host.AntiforgeryAsync(administrator);
            await host.StartWindowsAgentAsync(administrator, csrf);
            using var actor = await RestrictedActorAsync(host, administrator, csrf);
            var gate = Path.Combine(host.Root, "artifact-race-gate"); Directory.CreateDirectory(gate);
            var id = await SubmitAsync(actor.BuildClient, "P_Log", new() { ["env.CASE_DIR"] = gate });
            await WaitOutputAsync(actor.BuildClient, id, page => page.GetProperty("items").GetArrayLength() >= 2);
            using var agent = await AgentByteClientAsync(host, id);
            // Pending reservations consume the same object budget as accepted content. These
            // synthetic reservations have no Storage objects and are removed after the boundary check.
            await using (var database = await host.Services.GetRequiredService<IDbContextFactory<VivariumDbContext>>().CreateDbContextAsync())
            {
                var pending = Enumerable.Range(0, 128).Select(index => new RuntimeOutputDeletion
                {
                    BuildId = id, CreatedAt = DateTimeOffset.UtcNow,
                    ObjectKey = $"build-artifacts/{id}/" + Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("owned-reservation-" + index))),
                }).ToArray();
                database.RuntimeOutputDeletions.AddRange(pending); await database.SaveChangesAsync();
                var blockedBytes = "owned-over-budget"u8.ToArray(); var blockedHash = Convert.ToHexStringLower(SHA256.HashData(blockedBytes));
                using var blocked = await AgentUploadAsync(agent, blockedBytes);
                Assert.That(blocked.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
                var absent = Assert.ThrowsAsync<HttpRequestException>(() => host.Services.GetRequiredService<RuntimeBuildBytes>()
                    .ReadAsync($"build-artifacts/{id}/{blockedHash}", blockedBytes.Length, blockedHash, CancellationToken.None));
                Assert.That(absent!.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
                var keys = pending.Select(p => p.ObjectKey).ToArray();
                await database.RuntimeOutputDeletions.Where(d => keys.Contains(d.ObjectKey)).ExecuteDeleteAsync();
            }
            var content = "owned-racing-artifact"u8.ToArray(); var hash = Convert.ToHexStringLower(SHA256.HashData(content));
            var key = $"build-artifacts/{id}/{hash}";
            handoff.Arm(); var upload = AgentUploadAsync(agent, content);
            await handoff.Stored.WaitAsync(TimeSpan.FromSeconds(10));
            await R0AcceptanceInstallation.Case.MutateAsync(administrator, csrf, HttpMethod.Put, $"/api/v1/builds/{id}/cancellation",
                "{\"reason\":\"Owned upload/result race\",\"mode\":\"force\"}", HttpStatusCode.Accepted);
            Assert.That((await WaitBuildStateAsync(actor.BuildClient, id, "finished")).GetProperty("status").GetString(), Is.EqualTo("UNKNOWN"));
            handoff.Release(); using var rejected = await upload;
            Assert.That(rejected.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
            var databases = host.Services.GetRequiredService<IDbContextFactory<VivariumDbContext>>();
            await using (var database = await databases.CreateDbContextAsync())
            {
                Assert.That(await database.RuntimeOutputDeletions.AnyAsync(d => d.ObjectKey == key), Is.True);
                Assert.That(await database.RuntimeArtifactUploads.AnyAsync(u => u.BuildId == id && u.Sha256 == hash), Is.False);
            }
            Assert.That(await host.Services.GetRequiredService<RuntimeBuildBytes>().ReadAsync(key, content.Length, hash, CancellationToken.None), Is.EqualTo(content));
            await host.RestartAsync();
            await host.Services.GetRequiredService<RuntimeOutputRetention>().TickAsync(CancellationToken.None);
            var missing = Assert.ThrowsAsync<HttpRequestException>(() => host.Services.GetRequiredService<RuntimeBuildBytes>()
                .ReadAsync(key, content.Length, hash, CancellationToken.None));
            Assert.That(missing!.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        }
        finally { handoff.Release(); }
    }

    private static async Task<HttpClient> AgentByteClientAsync(R0AcceptanceInstallation.Case host, long id)
    {
        var client = host.Client();
        client.DefaultRequestHeaders.Authorization = new("Bearer", (await File.ReadAllTextAsync(Path.Combine(host.Root, "agent-data/auth.token"))).Trim());
        await using var database = await host.Services.GetRequiredService<IDbContextFactory<VivariumDbContext>>().CreateDbContextAsync();
        var owner = await database.RuntimeBuildExecutions.AsNoTracking().SingleAsync(e => e.BuildId == id);
        Assert.That(owner.ReleasedAt, Is.Null, "Lost result ACK retains the actual owning lease.");
        client.DefaultRequestHeaders.Add("X-Vivarium-Build-Id", owner.WorkloadId);
        client.DefaultRequestHeaders.Add("X-Vivarium-Session-Id", owner.CurrentSessionId);
        return client;
    }

    private static async Task<HttpResponseMessage> AgentUploadAsync(HttpClient client, byte[] bytes)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        using var request = new HttpRequestMessage(HttpMethod.Put, "/blobs/" + hash) { Content = new ByteArrayContent(bytes) };
        request.Headers.Add("X-Vivarium-Blob-Declared-Size", bytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return await client.SendAsync(request);
    }
}
