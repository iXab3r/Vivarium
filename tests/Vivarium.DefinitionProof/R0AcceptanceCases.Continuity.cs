using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Validation;

namespace Vivarium.DefinitionProof;

public sealed partial class R0AcceptanceCases
{
    [Test]
    public async Task Host_restart_preserves_eligible_credentials_and_never_resurrects_revoked_disabled_or_expired_keys()
    {
        var clock = new AcceptanceClock();
        await using var host = await installation.CreateCaseAsync(catalogued: true,
            configureServices: services => services.Configure<OpenIddictValidationOptions>(options => options.TimeProvider = clock));
        using var administrator = await host.AdministratorAsync(); var csrf = await host.AntiforgeryAsync(administrator);
        await host.StartWindowsAgentAsync(administrator, csrf);
        using var valid = await RestrictedActorAsync(host, administrator, csrf); await valid.CreateKeyAsync(lifetimeDays: 3);
        using var expired = await RestrictedActorAsync(host, administrator, csrf);
        using var revoked = await RestrictedActorAsync(host, administrator, csrf); await revoked.CreateKeyAsync(lifetimeDays: 3);
        await revoked.RevokeOwnKeyAsync();
        using var disabled = await RestrictedActorAsync(host, administrator, csrf); await disabled.CreateKeyAsync(lifetimeDays: 3);
        await R0AcceptanceInstallation.Case.MutateAsync(administrator, csrf, HttpMethod.Put,
            $"/api/v1/users/{disabled.UserId}/enabled", "{\"enabled\":false}", HttpStatusCode.NoContent);
        var id = await SubmitAsync(valid.BuildClient, "P_LocalSmoke"); await FinishedAsync(valid.BuildClient, id);
        var paths = await BuildLinksAsync(valid.BuildClient, id);
        // Each cursor is actor-bound, so the other accounts use the same metadata/content links.
        var resourcePaths = paths.Take(4).Concat(new[] { "/api/v1/users/current", $"/app/rest/builds/id:{id}",
            $"/app/rest/builds/id:{id}/artifacts/files/smoke.txt" }).ToArray();
        clock.Advance(TimeSpan.FromDays(2));
        async Task CheckAsync()
        {
            await ExpectPathsAsync(valid.BuildClient, resourcePaths, HttpStatusCode.OK);
            foreach (var ineligible in new[] { expired, revoked, disabled })
                await ExpectPathsAsync(ineligible.BuildClient, resourcePaths, HttpStatusCode.Unauthorized);
        }
        await CheckAsync();
        await host.RestartAsync(); await CheckAsync();
        Assert.That(await valid.BuildClient.GetByteArrayAsync(paths[2]), Is.EqualTo("owned-r0-smoke\n"u8.ToArray()));
        await R0AcceptanceInstallation.Case.MutateAsync(administrator, csrf, HttpMethod.Put,
            $"/api/v1/users/{disabled.UserId}/enabled", "{\"enabled\":true}", HttpStatusCode.NoContent);
        await ExpectPathsAsync(disabled.BuildClient, resourcePaths, HttpStatusCode.OK);
        await ExpectPathsAsync(revoked.BuildClient, resourcePaths, HttpStatusCode.Unauthorized);
        await ExpectPathsAsync(expired.BuildClient, resourcePaths, HttpStatusCode.Unauthorized);
        var grant = $"/api/v1/users/{valid.UserId}/roles/OWNED_SIX/projects/P";
        await R0AcceptanceInstallation.Case.MutateAsync(administrator, csrf, HttpMethod.Delete, grant, null, HttpStatusCode.NoContent);
        using (var denied = await valid.BuildClient.GetAsync(paths[3])) Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        await R0AcceptanceInstallation.Case.MutateAsync(administrator, csrf, HttpMethod.Put, grant, null, HttpStatusCode.NoContent);
        Assert.That(await valid.BuildClient.GetStringAsync(paths[3]), Does.Contain("owned-r0-stdout\n"));
    }

    [Test]
    public async Task Running_build_and_completed_artifacts_keep_their_identity_and_exact_bytes_across_cold_Host_reads()
    {
        await using var host = await installation.CreateCaseAsync(catalogued: true);
        using var administrator = await host.AdministratorAsync(); var csrf = await host.AntiforgeryAsync(administrator);
        await host.StartWindowsAgentAsync(administrator, csrf);
        using var actor = await RestrictedActorAsync(host, administrator, csrf);
        var finishedId = await SubmitAsync(actor.BuildClient, "P_LocalSmoke"); await FinishedAsync(actor.BuildClient, finishedId);
        var oldLinks = await BuildLinksAsync(actor.BuildClient, finishedId);
        var artifact = await actor.BuildClient.GetByteArrayAsync(oldLinks[2]); Assert.That(artifact, Is.EqualTo("owned-r0-smoke\n"u8.ToArray()));
        var gate = Path.Combine(host.Root, "continuity-gate"); Directory.CreateDirectory(gate);
        var id = await SubmitAsync(actor.BuildClient, "P_Log", new() { ["env.CASE_DIR"] = gate });
        var prefix = await WaitOutputAsync(actor.BuildClient, id, page => page.GetProperty("items").GetArrayLength() >= 2);
        var original = await ReadBuildAsync(actor.BuildClient, id);
        await WaitAsync(async () => File.Exists(Path.Combine(gate, "entered"))
            && (await File.ReadAllTextAsync(Path.Combine(gate, "entered"))).EndsWith('\n'));
        var process = await File.ReadAllLinesAsync(Path.Combine(gate, "entered")); Assert.That(process.Length, Is.EqualTo(1));
        await host.RestartAsync();
        Assert.That(await actor.BuildClient.GetByteArrayAsync(oldLinks[2]), Is.EqualTo(artifact));
        var after = await ReadBuildAsync(actor.BuildClient, id);
        foreach (var field in new[] { "id", "agentId", "state", "status", "definitionRevisionId", "definitionHash" })
            Assert.That(after.GetProperty(field).GetRawText(), Is.EqualTo(original.GetProperty(field).GetRawText()));
        Assert.That(await File.ReadAllLinesAsync(Path.Combine(gate, "entered")), Is.EqualTo(process));
        var resumed = await OutputAsync(actor.BuildClient, id, prefix.GetProperty("output").GetProperty("resumeCursor").GetString());
        Assert.That(resumed.GetProperty("items").GetArrayLength(), Is.Zero);
        await File.WriteAllTextAsync(Path.Combine(gate, "release"), "release"); await FinishedAsync(actor.BuildClient, id);
        await host.RestartAsync();
        var complete = await OutputAsync(actor.BuildClient, id);
        var streams = new Dictionary<string, List<byte>> { ["stdout"] = [], ["stderr"] = [] };
        await AppendAsync(actor.BuildClient, complete, streams, []);
        Assert.That(Encoding.UTF8.GetString(streams["stdout"].ToArray()), Is.EqualTo("owned-log-stdout\nowned-log-tail\n"));
        Assert.That(Encoding.UTF8.GetString(streams["stderr"].ToArray()), Is.EqualTo("owned-log-stderr\n"));
        Assert.That(await File.ReadAllLinesAsync(Path.Combine(gate, "entered")), Is.EqualTo(process), "The actual process must execute once.");
        Assert.That(await actor.BuildClient.GetByteArrayAsync(oldLinks[2]), Is.EqualTo(artifact));
        actor.BuildClient.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        using var compatible = JsonDocument.Parse(await actor.BuildClient.GetStringAsync($"/app/rest/builds/id:{id}?fields=id,state,status"));
        Assert.That(compatible.RootElement.GetProperty("id").GetInt64(), Is.EqualTo(id));
        Assert.That(compatible.RootElement.GetProperty("state").GetString(), Is.EqualTo("finished"));
        Assert.That(compatible.RootElement.GetProperty("status").GetString(), Is.EqualTo("SUCCESS"));
    }
}
