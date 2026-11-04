using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenIddict.Validation;
using Vivarium.Controller.Builds.Runtime;
using Vivarium.Controller.Rest.Common;
using Vivarium.Controller.Identity;
using Vivarium.Controller.Persistence;
using Vivarium.Controller.Security;

namespace Vivarium.DefinitionProof;

public sealed partial class R0AcceptanceCases
{
    [Test]
    public async Task Output_cursors_survive_restart_and_expiry_retains_explicit_gaps_and_current_access()
    {
        var cursorClock = new AcceptanceClock(); var retentionClock = new AcceptanceClock();
        await using var host = await installation.CreateCaseAsync(catalogued: true, outputRetentionDays: 1,
            configureServices: services =>
            {
                services.Replace(ServiceDescriptor.Singleton(provider => new RestCursorCodec(provider.GetRequiredService<IDataProtectionProvider>(), cursorClock)));
                services.Replace(ServiceDescriptor.Singleton(provider => new RuntimeOutputRetention(
                    provider.GetRequiredService<IDbContextFactory<VivariumDbContext>>(), provider.GetRequiredService<RuntimeBuildBytes>(),
                    provider.GetRequiredService<IConfiguration>(), retentionClock, provider.GetRequiredService<ILogger<RuntimeOutputRetention>>())));
            });
        using var administrator = await host.AdministratorAsync(); var csrf = await host.AntiforgeryAsync(administrator);
        await host.StartWindowsAgentAsync(administrator, csrf);
        using var actor = await RestrictedActorAsync(host, administrator, csrf);
        var id = await SubmitAsync(actor.BuildClient, "P_LocalSmoke"); await FinishedAsync(actor.BuildClient, id);
        var other = await SubmitAsync(actor.BuildClient, "P_Empty"); await FinishedAsync(actor.BuildClient, other);
        var links = await BuildLinksAsync(actor.BuildClient, id);
        var cursorPath = links[^1];
        await ExpectProblemAsync(actor.BuildClient, cursorPath + "!", HttpStatusCode.BadRequest, "invalid_cursor");
        await ExpectProblemAsync(actor.BuildClient, cursorPath.Replace($"builds/{id}/", $"builds/{other}/", StringComparison.Ordinal),
            HttpStatusCode.BadRequest, "invalid_cursor");
        using (var otherEntry = await actor.BuildClient.GetAsync(links[3].Replace($"builds/{id}/", $"builds/{other}/", StringComparison.Ordinal)))
            Assert.That(otherEntry.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        await host.RestartAsync();
        using (var resumed = await actor.BuildClient.GetAsync(cursorPath)) Assert.That(resumed.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        cursorClock.Advance(TimeSpan.FromMinutes(16));
        var recovery = await ExpectProblemAsync(actor.BuildClient, cursorPath, HttpStatusCode.Gone, "cursor_expired");
        var recoveryHref = recovery.GetProperty("recovery").GetProperty("href").GetString();
        Assert.That(recoveryHref, Is.EqualTo($"/api/v1/builds/{id}/output"));
        using (var recovered = await actor.BuildClient.GetAsync(recoveryHref)) Assert.That(recovered.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var current = await OutputAsync(actor.BuildClient, id);
        var freshCursorPath = $"/api/v1/builds/{id}/output?cursor=" + Uri.EscapeDataString(current.GetProperty("output").GetProperty("resumeCursor").GetString()!);
        byte[] before;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<VivariumDbContext>();
            before = JsonSerializer.SerializeToUtf8Bytes(await database.RuntimeOutputRanges.AsNoTracking().Where(r => r.BuildId == id).OrderBy(r => r.Id).ToArrayAsync());
        }
        retentionClock.Advance(TimeSpan.FromDays(2));
        await host.Services.GetRequiredService<RuntimeOutputRetention>().TickAsync(CancellationToken.None);
        await ExpectProblemAsync(actor.BuildClient, freshCursorPath, HttpStatusCode.Gone, "cursor_expired");
        await ExpectProblemAsync(actor.BuildClient, links[3], HttpStatusCode.Gone, "output_expired");
        using (var missing = await actor.BuildClient.GetAsync($"/api/v1/builds/{id}/output/{Guid.NewGuid()}/content"))
            Assert.That(missing.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        await R0AcceptanceInstallation.Case.MutateAsync(administrator, csrf, HttpMethod.Delete,
            $"/api/v1/users/{actor.UserId}/roles/OWNED_SIX/projects/P", null, HttpStatusCode.NoContent);
        using (var hidden = await actor.BuildClient.GetAsync(links[3])) Assert.That(hidden.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        await R0AcceptanceInstallation.Case.MutateAsync(administrator, csrf, HttpMethod.Put,
            $"/api/v1/users/{actor.UserId}/roles/OWNED_SIX/projects/P", null, HttpStatusCode.NoContent);
        await host.RestartAsync();
        var retained = await OutputAsync(actor.BuildClient, id);
        Assert.That(retained.GetProperty("output").GetProperty("state").GetString(), Is.EqualTo("complete-with-gaps"));
        Assert.That(retained.GetProperty("output").GetProperty("retention").GetProperty("epoch").GetInt64(), Is.EqualTo(1));
        Assert.That(retained.GetProperty("items").GetArrayLength(), Is.EqualTo(current.GetProperty("items").GetArrayLength()).And.Positive);
        foreach (var gap in retained.GetProperty("items").EnumerateArray())
        {
            Assert.That(gap.GetProperty("kind").GetString(), Is.EqualTo("gap"));
            Assert.That(gap.GetProperty("reason").GetString(), Is.EqualTo("retention"));
            Assert.That(gap.TryGetProperty("content", out _), Is.False);
        }
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<VivariumDbContext>();
            var after = JsonSerializer.SerializeToUtf8Bytes(await database.RuntimeOutputRanges.AsNoTracking().Where(r => r.BuildId == id).OrderBy(r => r.Id).ToArrayAsync());
            Assert.That(after, Is.EqualTo(before), "Retention must not rewrite the producer's sealed history.");
        }
        Assert.That(await actor.BuildClient.GetByteArrayAsync(links[2]), Is.EqualTo("owned-r0-smoke\n"u8.ToArray()));
    }

    [Test]
    public async Task Native_own_key_revocation_reproduces_the_three_frozen_reference_read_effects()
    {
        await using var host = await installation.CreateCaseAsync(catalogued: true);
        using var administrator = await host.AdministratorAsync(); var csrf = await host.AntiforgeryAsync(administrator);
        await host.StartWindowsAgentAsync(administrator, csrf);
        using var actor = await RestrictedActorAsync(host, administrator, csrf);
        var id = await SubmitAsync(actor.BuildClient, "P_LocalSmoke"); await FinishedAsync(actor.BuildClient, id);
        var artifactPath = $"/app/rest/builds/id:{id}/artifacts/files/smoke.txt?resolveParameters=false&logBuildUsage=false";
        Assert.That(await actor.BuildClient.GetByteArrayAsync(artifactPath), Is.EqualTo("owned-r0-smoke\n"u8.ToArray()));
        Assert.That(await administrator.GetByteArrayAsync(artifactPath), Is.EqualTo("owned-r0-smoke\n"u8.ToArray()));
        await actor.RevokeOwnKeyAsync();
        var repository = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "Vivarium.slnx"))) repository = repository.Parent;
        Assert.That(repository, Is.Not.Null);
        using var corpus = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(repository!.FullName,
            "tests/Vivarium.Tests/TeamCity/Reference/r0-access-observations.json")));
        var exchanges = corpus.RootElement.GetProperty("cases").EnumerateArray().Single(c => c.GetProperty("id").GetString()
            == "TCREST-R0-REVOKED-CREDENTIAL-BUILD-BYTES-DENIED-001").GetProperty("exchanges");
        // Only effects 2/3/4 are allocated to this native preparation, never TeamCity token DELETE.
        foreach (var index in new[] { 2, 3, 4 })
        {
            var exchange = exchanges[index]; var sourceRequest = exchange.GetProperty("request");
            var sourceBuild = corpus.RootElement.GetProperty("metadata").GetProperty("allowedBuildId").GetInt64();
            var path = sourceRequest.GetProperty("path").GetString()!.Replace("id:" + sourceBuild, "id:" + id, StringComparison.Ordinal);
            path += "?" + string.Join('&', sourceRequest.GetProperty("query").EnumerateObject()
                .Select(p => Uri.EscapeDataString(p.Name) + "=" + Uri.EscapeDataString(p.Value.GetString()!)));
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Accept.ParseAdd(sourceRequest.GetProperty("headers").GetProperty("accept").GetString()!);
            using var response = await (index == 4 ? administrator : actor.BuildClient).SendAsync(request);
            var expected = exchange.GetProperty("response");
            Assert.That((int)response.StatusCode, Is.EqualTo(expected.GetProperty("status").GetInt32()));
            var expectedMedia = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(expected.GetProperty("headers").GetProperty("content-type").GetString()!);
            Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo(expectedMedia.MediaType));
            Assert.That(response.Content.Headers.ContentType?.CharSet, Is.EqualTo(expectedMedia.CharSet).IgnoreCase);
            var bytes = await response.Content.ReadAsByteArrayAsync();
            Assert.That(bytes, Is.EqualTo(Convert.FromBase64String(expected.GetProperty("bodyBase64").GetString()!)));
            Assert.That(bytes.Length, Is.EqualTo(expected.GetProperty("bodyBytes").GetInt32()));
            Assert.That(Convert.ToHexStringLower(SHA256.HashData(bytes)), Is.EqualTo(expected.GetProperty("bodySha256").GetString()));
        }
    }

    [Test]
    public async Task Actual_key_expiry_and_account_disable_deny_build_output_and_artifacts_without_resurrecting_expired_keys()
    {
        var clock = new AcceptanceClock();
        await using var host = await installation.CreateCaseAsync(catalogued: true,
            configureServices: services => services.Configure<OpenIddictValidationOptions>(options => options.TimeProvider = clock));
        using var administrator = await host.AdministratorAsync(); var csrf = await host.AntiforgeryAsync(administrator);
        await host.StartWindowsAgentAsync(administrator, csrf);
        using var actor = await RestrictedActorAsync(host, administrator, csrf);
        var shortKey = actor.BuildClient.DefaultRequestHeaders.Authorization;
        await actor.CreateKeyAsync(lifetimeDays: 3);
        var longKey = actor.BuildClient.DefaultRequestHeaders.Authorization;
        var id = await SubmitAsync(actor.BuildClient, "P_LocalSmoke"); await FinishedAsync(actor.BuildClient, id);
        var paths = await BuildLinksAsync(actor.BuildClient, id);
        actor.BuildClient.DefaultRequestHeaders.Authorization = shortKey;
        await ExpectPathsAsync(actor.BuildClient, paths, HttpStatusCode.OK);
        clock.Advance(TimeSpan.FromDays(2));
        await ExpectPathsAsync(actor.BuildClient, paths, HttpStatusCode.Unauthorized);
        actor.BuildClient.DefaultRequestHeaders.Authorization = longKey;
        await ExpectPathsAsync(actor.BuildClient, paths, HttpStatusCode.OK);
        var enabled = $"/api/v1/users/{actor.UserId}/enabled";
        await R0AcceptanceInstallation.Case.MutateAsync(administrator, csrf, HttpMethod.Put, enabled, "{\"enabled\":false}", HttpStatusCode.NoContent);
        await ExpectPathsAsync(actor.BuildClient, paths, HttpStatusCode.Unauthorized);
        Assert.That(await administrator.GetByteArrayAsync(paths[2]), Is.EqualTo("owned-r0-smoke\n"u8.ToArray()));
        await R0AcceptanceInstallation.Case.MutateAsync(administrator, csrf, HttpMethod.Put, enabled, "{\"enabled\":true}", HttpStatusCode.NoContent);
        await ExpectPathsAsync(actor.BuildClient, paths, HttpStatusCode.OK);
        actor.BuildClient.DefaultRequestHeaders.Authorization = shortKey;
        await ExpectPathsAsync(actor.BuildClient, paths, HttpStatusCode.Unauthorized);
        actor.BuildClient.DefaultRequestHeaders.Authorization = longKey;
        actor.BuildClient.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        using var compatible = await actor.BuildClient.GetAsync($"/app/rest/builds/id:{id}");
        Assert.That(compatible.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        actor.BuildClient.DefaultRequestHeaders.Authorization = shortKey;
        using var denied = await actor.BuildClient.GetAsync($"/app/rest/builds/id:{id}");
        Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(await denied.Content.ReadAsStringAsync(), Is.EqualTo("Could not authenticate with provided token\n"));
    }

    [Test]
    public async Task Literal_process_streams_resume_from_the_live_head_and_finish_with_exact_bytes()
    {
        await using var host = await installation.CreateCaseAsync(catalogued: true);
        using var administrator = await host.AdministratorAsync();
        var csrf = await host.AntiforgeryAsync(administrator);
        await host.StartWindowsAgentAsync(administrator, csrf);
        using var actor = await RestrictedActorAsync(host, administrator, csrf);
        var gate = Path.Combine(host.Root, "log-gate"); Directory.CreateDirectory(gate);
        var id = await SubmitAsync(actor.BuildClient, "P_Log", new() { ["env.CASE_DIR"] = gate });
        var streams = new Dictionary<string, List<byte>> { ["stdout"] = [], ["stderr"] = [] };
        var entryIds = new HashSet<string>();
        string? cursor = null;
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!File.Exists(Path.Combine(gate, "entered")) || streams["stdout"].Count == 0 || streams["stderr"].Count == 0)
        {
            var page = await OutputAsync(actor.BuildClient, id, cursor, bound.Token);
            await AppendAsync(actor.BuildClient, page, streams, entryIds);
            cursor = page.GetProperty("output").GetProperty("resumeCursor").GetString();
            Assert.That(page.GetProperty("output").GetProperty("state").GetString(), Is.EqualTo("open"));
            await Task.Delay(50, bound.Token);
        }
        Assert.That(Encoding.UTF8.GetString(streams["stdout"].ToArray()), Is.EqualTo("owned-log-stdout\n"));
        Assert.That(Encoding.UTF8.GetString(streams["stderr"].ToArray()), Is.EqualTo("owned-log-stderr\n"));
        var live = await ReadBuildAsync(actor.BuildClient, id);
        Assert.That(live.GetProperty("state").GetString(), Is.EqualTo("running"));
        await File.WriteAllTextAsync(Path.Combine(gate, "release"), "release");
        while (true)
        {
            var page = await OutputAsync(actor.BuildClient, id, cursor, bound.Token);
            await AppendAsync(actor.BuildClient, page, streams, entryIds);
            cursor = page.GetProperty("output").GetProperty("resumeCursor").GetString();
            if (page.GetProperty("output").GetProperty("state").GetString() == "complete") break;
            await Task.Delay(50, bound.Token);
        }
        Assert.That(Encoding.UTF8.GetString(streams["stdout"].ToArray()), Is.EqualTo("owned-log-stdout\nowned-log-tail\n"));
        Assert.That(Encoding.UTF8.GetString(streams["stderr"].ToArray()), Is.EqualTo("owned-log-stderr\n"));
        await FinishedAsync(actor.BuildClient, id);
        var end = await OutputAsync(actor.BuildClient, id, cursor, bound.Token);
        Assert.That(end.GetProperty("items").GetArrayLength(), Is.Zero);
        Assert.That(end.GetProperty("output").GetProperty("state").GetString(), Is.EqualTo("complete"));
    }

    [Test]
    public async Task Registered_run_secret_is_masked_across_process_reads_before_persistence_and_replay()
    {
        await using var host = await installation.CreateCaseAsync(catalogued: true);
        using var administrator = await host.AdministratorAsync(); var csrf = await host.AntiforgeryAsync(administrator);
        await host.StartWindowsAgentAsync(administrator, csrf);
        using var actor = await RestrictedActorAsync(host, administrator, csrf);
        var secret = "owned-value-" + Guid.NewGuid().ToString("N");
        using var accepted = await actor.BuildClient.PostAsync("/api/v1/builds", Json(new
        { configurationId = "P_Secret", secretEnvironment = new Dictionary<string, string> { ["OWNED_SECRET"] = secret } }));
        Assert.That(accepted.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        using var admission = JsonDocument.Parse(await accepted.Content.ReadAsStringAsync());
        Assert.That(admission.RootElement.ToString(), Does.Not.Contain(secret));
        var id = admission.RootElement.GetProperty("id").GetInt64(); await FinishedAsync(actor.BuildClient, id);
        var page = await OutputAsync(actor.BuildClient, id);
        var streams = new Dictionary<string, List<byte>> { ["stdout"] = [], ["stderr"] = [] };
        await AppendAsync(actor.BuildClient, page, streams, []);
        Assert.That(Encoding.UTF8.GetString(streams["stdout"].ToArray()), Is.EqualTo("left-[REDACTED]-right\n"));
        Assert.That(Encoding.UTF8.GetString(streams["stderr"].ToArray()), Is.EqualTo("error-[REDACTED]-end\n"));
        Assert.That(page.ToString(), Does.Not.Contain(secret));
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<VivariumDbContext>();
            var audit = await database.AuditEvents.AsNoTracking().ToArrayAsync();
            Assert.That(JsonSerializer.Serialize(audit), Does.Not.Contain(secret));
            var inputs = await database.RuntimeBuildInputs.AsNoTracking().ToArrayAsync();
            Assert.That(JsonSerializer.Serialize(inputs), Does.Not.Contain(secret));
        }
        // Check the actual Agent spool and frozen execution files; no diagnostic dumps of their contents.
        await host.StopOwnedAgentAsync();
        var needle = Encoding.UTF8.GetBytes(secret);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(host.Root, "agent-data"), "*", SearchOption.AllDirectories))
        {
            var bytes = await File.ReadAllBytesAsync(file);
            Assert.That(bytes.AsSpan().IndexOf(needle), Is.EqualTo(-1), "The owned Agent persisted the registered value.");
        }
        await host.RestartAsync();
        var replay = await OutputAsync(actor.BuildClient, id);
        var retained = new Dictionary<string, List<byte>> { ["stdout"] = [], ["stderr"] = [] };
        await AppendAsync(actor.BuildClient, replay, retained, []);
        Assert.That(retained["stdout"], Is.EqualTo(streams["stdout"])); Assert.That(retained["stderr"], Is.EqualTo(streams["stderr"]));
    }

    [Test]
    public async Task Withdrawing_the_exact_six_permission_Project_grant_invalidates_old_links_and_denied_runs_have_no_effect()
    {
        await using var host = await installation.CreateCaseAsync(catalogued: true);
        using var administrator = await host.AdministratorAsync();
        var csrf = await host.AntiforgeryAsync(administrator);
        await host.StartWindowsAgentAsync(administrator, csrf);
        using var actor = await RestrictedActorAsync(host, administrator, csrf);
        var id = await SubmitAsync(actor.BuildClient, "P_LocalSmoke"); await FinishedAsync(actor.BuildClient, id);
        var q = await SubmitAsync(administrator, "Q_LocalSmoke", csrf: csrf); await FinishedAsync(administrator, q);
        var links = await BuildLinksAsync(actor.BuildClient, id);
        var qLinks = await BuildLinksAsync(administrator, q);
        foreach (var path in qLinks)
        {
            using var positive = await administrator.GetAsync(path); Assert.That(positive.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            using var denied = await actor.BuildClient.GetAsync(path); Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        }
        var role = $"/api/v1/users/{actor.UserId}/roles/OWNED_SIX/projects/P";
        await R0AcceptanceInstallation.Case.MutateAsync(administrator, csrf, HttpMethod.Delete, role, null, HttpStatusCode.NoContent);
        var count = await BuildCountAsync(host);
        foreach (var path in links)
        {
            using var denied = await actor.BuildClient.GetAsync(path); Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.NotFound), path);
            // Cursors belong to their actor; the independent admin control reads the same resource.
            using var positive = await administrator.GetAsync(path.Split('?', 2)[0]);
            Assert.That(positive.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        }
        actor.BuildClient.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        foreach (var path in new[] { $"/app/rest/builds/id:{id}", $"/app/rest/builds/id:{id}/artifacts/files/smoke.txt" })
        {
            using var denied = await actor.BuildClient.GetAsync(path); Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        }
        using (var denied = await actor.BuildClient.PostAsync("/api/v1/builds", Json(new { configurationId = "P_LocalSmoke" })))
            Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        using (var denied = await actor.BuildClient.PostAsync("/app/rest/buildQueue", Json(new { buildType = new { id = "P_LocalSmoke" } })))
            Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        Assert.That(await BuildCountAsync(host), Is.EqualTo(count));
        await R0AcceptanceInstallation.Case.MutateAsync(administrator, csrf, HttpMethod.Put, role, null, HttpStatusCode.NoContent);
        foreach (var path in links)
        {
            using var restored = await actor.BuildClient.GetAsync(path); Assert.That(restored.StatusCode, Is.EqualTo(HttpStatusCode.OK), path);
        }
        foreach (var path in qLinks)
        {
            using var denied = await actor.BuildClient.GetAsync(path); Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        }
        var next = await SubmitAsync(actor.BuildClient, "P_LocalSmoke"); await FinishedAsync(actor.BuildClient, next);
        Assert.That(await actor.BuildClient.GetByteArrayAsync(links[2]), Is.EqualTo("owned-r0-smoke\n"u8.ToArray()));
    }

    internal static Task<ScopedKeyScenario> RestrictedActorAsync(R0AcceptanceInstallation.Case host, HttpClient administrator, string csrf)
        => ProjectActorAsync(host, administrator, csrf, "OWNED_SIX",
            ["view_project", "run_build", "view_file_content", "view_build_runtime_data", "customize_build_parameters", "cancel_build"]);

    internal static async Task<ScopedKeyScenario> ProjectActorAsync(R0AcceptanceInstallation.Case host, HttpClient administrator,
        string csrf, string role, string[] permissions)
    {
        var actor = new ScopedKeyScenario(host.Origin, host.Thumbprint, host.Root, host.Certificate);
        try
        {
            await actor.InitializeUserAsync(host.Services, "r0-build-" + Guid.NewGuid().ToString("N")[..12]);
            await using (var scope = host.Services.CreateAsyncScope())
            {
                var database = scope.ServiceProvider.GetRequiredService<VivariumDbContext>();
                Assert.That(await database.GroupRoleGrants.CountAsync(), Is.Zero, "The owned fixture has no implicit All Users authority.");
                var administratorContext = new ManagementRequestContext(new ManagementPrincipal("user",
                    VivariumIdentityConstants.SuperuserId.ToString(), "owned-acceptance", null),
                    ManagementIdentifiers.NewId(), null, "owned-six-permission-role");
                var roles = scope.ServiceProvider.GetRequiredService<TeamCityAccessService>();
                Assert.That(await roles.SetRoleAsync(administratorContext, role, "Owned Project fixture role", permissions),
                    Is.EqualTo(AccessMutationResult.Applied));
                Assert.That(await database.RolePermissions.Where(p => p.RoleId == role).Select(p => p.PermissionId).ToArrayAsync(),
                    Is.EquivalentTo(permissions));
            }
            await R0AcceptanceInstallation.Case.MutateAsync(administrator, csrf, HttpMethod.Put,
                $"/api/v1/users/{actor.UserId}/roles/{role}/projects/P", null, HttpStatusCode.NoContent);
            await actor.CreateKeyAsync(); return actor;
        }
        catch { actor.Dispose(); throw; }
    }

    private static StringContent Json(object value) => new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");
    private static async Task<long> SubmitAsync(HttpClient client, string configuration, Dictionary<string, string>? parameters = null, string? csrf = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/builds")
            { Content = Json(new { configurationId = configuration, parameters }) };
        if (csrf is not null) request.Headers.Add("RequestVerificationToken", csrf);
        using var response = await client.SendAsync(request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        using var accepted = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return accepted.RootElement.GetProperty("id").GetInt64();
    }
    private static async Task<JsonElement> ReadBuildAsync(HttpClient client, long id)
    {
        using var response = JsonDocument.Parse(await client.GetStringAsync($"/api/v1/builds/{id}")); return response.RootElement.Clone();
    }
    private static async Task FinishedAsync(HttpClient client, long id, int timeoutSeconds = 30)
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        while (true)
        {
            var build = await ReadBuildAsync(client, id);
            if (build.GetProperty("state").GetString() == "finished")
            { Assert.That(build.GetProperty("status").GetString(), Is.EqualTo("SUCCESS")); return; }
            await Task.Delay(50, bound.Token);
        }
    }
    private static async Task<JsonElement> OutputAsync(HttpClient client, long id, string? cursor = null, CancellationToken token = default)
    {
        using var page = JsonDocument.Parse(await client.GetStringAsync($"/api/v1/builds/{id}/output" +
            (cursor is null ? "" : "?cursor=" + Uri.EscapeDataString(cursor)), token)); return page.RootElement.Clone();
    }
    private static async Task AppendAsync(HttpClient client, JsonElement page, Dictionary<string, List<byte>> streams, HashSet<string> ids)
    {
        foreach (var item in page.GetProperty("items").EnumerateArray())
        {
            Assert.That(item.GetProperty("kind").GetString(), Is.EqualTo("data"));
            Assert.That(ids.Add(item.GetProperty("id").GetString()!), Is.True, "A resumed entry must occur exactly once.");
            var content = item.GetProperty("content");
            var bytes = await client.GetByteArrayAsync(content.GetProperty("href").GetString());
            Assert.That(bytes.Length, Is.LessThanOrEqualTo(64 * 1024));
            Assert.That(bytes.LongLength, Is.EqualTo(content.GetProperty("length").GetInt64()));
            Assert.That(Convert.ToHexStringLower(SHA256.HashData(bytes)), Is.EqualTo(content.GetProperty("sha256").GetString()));
            streams[item.GetProperty("stream").GetString()!].AddRange(bytes);
            Assert.That(item.TryGetProperty("text", out _), Is.False); Assert.That(item.TryGetProperty("base64", out _), Is.False);
        }
    }
    private static async Task<string[]> BuildLinksAsync(HttpClient client, long id)
    {
        using var artifacts = JsonDocument.Parse(await client.GetStringAsync($"/api/v1/builds/{id}/artifacts"));
        var artifact = artifacts.RootElement.GetProperty("items")[0].GetProperty("href").GetString()!;
        var output = await OutputAsync(client, id);
        var entry = output.GetProperty("items").EnumerateArray().First(e => e.GetProperty("kind").GetString() == "data");
        return [$"/api/v1/builds/{id}", $"/api/v1/builds/{id}/output", artifact,
            entry.GetProperty("content").GetProperty("href").GetString()!,
            $"/api/v1/builds/{id}/output?cursor=" + Uri.EscapeDataString(output.GetProperty("output").GetProperty("resumeCursor").GetString()!)];
    }
    private static async Task<int> BuildCountAsync(R0AcceptanceInstallation.Case host)
    {
        await using var scope = host.Services.CreateAsyncScope(); return await scope.ServiceProvider.GetRequiredService<VivariumDbContext>().RuntimeBuilds.CountAsync();
    }

    private static async Task ExpectPathsAsync(HttpClient client, IEnumerable<string> paths, HttpStatusCode expected)
    {
        foreach (var path in paths)
        {
            using var response = await client.GetAsync(path);
            Assert.That(response.StatusCode, Is.EqualTo(expected), path.Split('?', 2)[0]);
            Assert.That(response.Headers.Location, Is.Null);
        }
    }

    private static async Task<JsonElement> ExpectProblemAsync(HttpClient client, string path, HttpStatusCode status, string code)
    {
        using var response = await client.GetAsync(path); Assert.That(response.StatusCode, Is.EqualTo(status));
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/problem+json"));
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.That(problem.RootElement.GetProperty("code").GetString(), Is.EqualTo(code));
        return problem.RootElement.Clone();
    }

    private sealed class AcceptanceClock : TimeProvider
    {
        private long offset;
        public override DateTimeOffset GetUtcNow() => System.GetUtcNow().AddTicks(Interlocked.Read(ref offset));
        internal void Advance(TimeSpan elapsed) => Interlocked.Add(ref offset, elapsed.Ticks);
    }
}
