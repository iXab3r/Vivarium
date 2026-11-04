using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.AspNetCore.DataProtection;
using OpenIddict.Validation;
using Vivarium.Controller.Identity;
using Vivarium.Controller.Security;
using Vivarium.Controller.Rest.Common;
using Vivarium.Controller.Builds.Runtime;
using Vivarium.Controller.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using EyeAuras.Storage.Contracts;
using EyeAuras.Storage;

namespace Vivarium.DefinitionProof;

/// <summary>One real ordinary account/key journey. Credentials stay only in private in-memory HTTP state.</summary>
internal sealed class ScopedKeyScenario : IDisposable
{
    private readonly UnchangedCliScenario cli;
    private readonly LinuxCliCancellationScenario linux;
    private readonly HttpClient browser;
    private readonly HttpClient api;
    private readonly Uri origin;
    private readonly CookieContainer browserCookies = new();
    private long buildId;
    private string? artifactHref;
    private string? outputHref;
    private Guid userId;
    private string? issuedKeyId;
    private string? resumeCursor;
    private string? protectedOutputHref;
    private string? retainedCursor;
    private readonly ScenarioClock validationClock = new();
    private readonly ScenarioClock outputClock = new();
    private readonly ScenarioClock retentionClock = new();
    internal HttpClient BuildClient => api;
    internal Guid UserId => userId;
    internal async Task AddBrowserSessionAsync(Microsoft.Playwright.IBrowserContext context)
    {
        // Prior ordinary Identity session, prepared by the real SignInManager above. This is
        // fixture setup, not a second login mechanism or a claim to repeat the OIDC UI proof.
        await context.AddCookiesAsync(browserCookies.GetCookies(origin).Cast<Cookie>().Select(cookie => new Microsoft.Playwright.Cookie
        {
            Name = cookie.Name, Value = cookie.Value, Domain = origin.Host, Path = cookie.Path,
            Secure = cookie.Secure, HttpOnly = cookie.HttpOnly, SameSite = Microsoft.Playwright.SameSiteAttribute.Lax,
        }));
    }
    internal void ResetRetentionTime() => retentionClock.Reset();

    internal async Task CheckRunSecretAdmissionAsync(HttpClient administrator)
    {
        var shell = await administrator.GetStringAsync("/access");
        var bootstrap = Regex.Match(shell, "<script id=\"vivarium-session\" type=\"application/json\">(.*?)</script>", RegexOptions.Singleline);
        using var session = JsonDocument.Parse(bootstrap.Groups[1].Value);
        var csrf = session.RootElement.GetProperty("requestVerificationToken").GetString()!;
        var runOnly = $"/api/v1/users/{userId}/roles/OWNED_RUN_ONLY/projects/P";
        var viewer = $"/api/v1/users/{userId}/roles/PROJECT_VIEWER/projects/P";
        await MutateAsync(administrator, csrf, HttpMethod.Put, runOnly, null, HttpStatusCode.NoContent);
        await MutateAsync(administrator, csrf, HttpMethod.Put, viewer, null, HttpStatusCode.NoContent);
        await MutateAsync(administrator, csrf, HttpMethod.Delete, RolePath, null, HttpStatusCode.NoContent);
        const string input = "{\"configurationId\":\"P_Secret\",\"secretEnvironment\":{\"US013_SECRET\":\"owned-denied-secret\"}}";
        await MutateAsync(api, csrf, HttpMethod.Post, "/api/v1/builds", input, HttpStatusCode.Forbidden);
        await MutateAsync(administrator, csrf, HttpMethod.Put, RolePath, null, HttpStatusCode.NoContent);
        await MutateAsync(administrator, csrf, HttpMethod.Delete, runOnly, null, HttpStatusCode.NoContent);
        await MutateAsync(administrator, csrf, HttpMethod.Delete, viewer, null, HttpStatusCode.NoContent);
        await MutateAsync(api, csrf, HttpMethod.Post, "/api/v1/builds", input.Replace("P_Secret", "Q_Secret"), HttpStatusCode.NotFound);
        await MutateAsync(api, csrf, HttpMethod.Post, "/api/v1/builds", input.Replace("US013_SECRET", "env.SECRET"), HttpStatusCode.BadRequest);
    }

    internal void ConfigureServices(IServiceCollection services)
    {
        services.Configure<OpenIddictValidationOptions>(options => options.TimeProvider = validationClock);
        services.Replace(ServiceDescriptor.Singleton<RestCursorCodec>(provider =>
            new RestCursorCodec(provider.GetRequiredService<IDataProtectionProvider>(), outputClock)));
        services.Replace(ServiceDescriptor.Singleton<RuntimeOutputRetention>(provider => new RuntimeOutputRetention(
            provider.GetRequiredService<IDbContextFactory<VivariumDbContext>>(), provider.GetRequiredService<RuntimeBuildBytes>(),
            provider.GetRequiredService<IConfiguration>(), retentionClock, provider.GetRequiredService<ILogger<RuntimeOutputRetention>>())));
    }

    internal ScopedKeyScenario(Uri origin, string thumbprint, string root, string certificatePath)
    {
        this.origin = origin;
        cli = new(root, origin, certificatePath);
        linux = new(root, origin, certificatePath);
        HttpClient Create(bool cookies) => new(new HttpClientHandler { AllowAutoRedirect = false,
            UseCookies = cookies, CookieContainer = cookies ? browserCookies : new CookieContainer(),
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) => certificate?.Thumbprint == thumbprint })
            { BaseAddress = origin, Timeout = TimeSpan.FromSeconds(15) };
        browser = Create(cookies: true); api = Create(cookies: false);
    }

    internal async Task InitializeUserAsync(IServiceProvider services, string name = "owned-scoped-user", string? displayName = "Owned scoped actor")
    {
        await using (var scope = services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<VivariumUser>>();
            var user = new VivariumUser { Id = Guid.NewGuid(), UserName = name, DisplayName = displayName };
            Assert.That((await users.CreateAsync(user)).Succeeded, Is.True);
            userId = user.Id;
            // Prior state: an eligible ordinary Identity session. OrdinaryIdentityTests owns the
            // real external-issuer/browser login proof. This HTTP journey starts at My keys and
            // uses the actual audited SignInManager/cookie handler, without a test auth scheme.
            var accessor = scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
            var previous = accessor.HttpContext;
            var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
            context.Request.Scheme = origin.Scheme; context.Request.Host = new HostString(origin.Host, origin.Port);
            try
            {
                accessor.HttpContext = context;
                var signIn = scope.ServiceProvider.GetRequiredService<SignInManager<VivariumUser>>();
                signIn.Context = context;
                Assert.That(await signIn.CanSignInAsync(user), Is.True);
                await signIn.SignInAsync(user, isPersistent: false);
                foreach (var header in Microsoft.Net.Http.Headers.SetCookieHeaderValue.ParseList(context.Response.Headers.SetCookie.Select(value => value!).ToArray()))
                    browserCookies.Add(origin, new Cookie(header.Name.ToString(), header.Value.ToString(), header.Path.ToString())
                        { HttpOnly = header.HttpOnly, Secure = header.Secure });
            }
            finally { accessor.HttpContext = previous; }
        }
    }

    internal async Task RunAsync(IServiceProvider services, HttpClient administrator, string csrf, string agentId)
    {
        await InitializeUserAsync(services);
        await MutateAsync(administrator, csrf, HttpMethod.Put, RolePath, null, HttpStatusCode.NoContent);
        await CreateKeyAsync();
        await cli.AuthenticateAsync(api.DefaultRequestHeaders.Authorization!.Parameter!);
        await cli.RunBuildAsync(api.DefaultRequestHeaders.Authorization!.Parameter!, api);
        Assert.That((await api.GetAsync("/api/v1/users/current")).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        await DelegationAsync(services, administrator, csrf);
        using (var denied = await api.PutAsync($"/api/v1/agents/{agentId}/authorization",
            new StringContent("{\"authorized\":false}", Encoding.UTF8, "application/json")))
            Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        int compatibleAgent;
        using (var control = JsonDocument.Parse(await administrator.GetStringAsync($"/api/v1/agents/{agentId}")))
        {
            Assert.That(control.RootElement.GetProperty("authorized").GetBoolean(), Is.True);
            compatibleAgent = control.RootElement.GetProperty("teamCityId").GetInt32();
        }
        var enabledPath = $"/app/rest/agents/id:{compatibleAgent}/enabledInfo?fields=status,comment(text,timestamp)";
        api.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        administrator.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        using (var enabled = JsonDocument.Parse(await api.GetStringAsync(enabledPath)))
            Assert.That(enabled.RootElement.GetProperty("status").GetBoolean(), Is.True);
        await MutateAsync(api, csrf, HttpMethod.Put, enabledPath, "{\"status\":false}", HttpStatusCode.Forbidden);
        await MutateAsync(administrator, csrf, HttpMethod.Put, enabledPath,
            "{\"status\":false,\"comment\":{\"text\":\"Owned CLI queued cancellation\"}}", HttpStatusCode.OK);
        var cliQueued = await cli.CancelQueuedAsync(api.DefaultRequestHeaders.Authorization!.Parameter!, api);
        await linux.RunAsync(administrator, api, csrf, api.DefaultRequestHeaders.Authorization!.Parameter!, cli, agentId);
        await MutateAsync(administrator, csrf, HttpMethod.Put, enabledPath,
            "{\"status\":true,\"comment\":{\"text\":\"Owned CLI queued cancellation complete\"}}", HttpStatusCode.OK);
        using (var retained = JsonDocument.Parse(await api.GetStringAsync($"/api/v1/builds/{cliQueued}")))
            Assert.That(retained.RootElement.GetProperty("state").GetString(), Is.EqualTo("finished"));

        // A real Q Build/artifact is the protected positive control, not a guessed absent identity.
        var protectedId = await SubmitAsync(administrator, "Q_LocalSmoke", csrf);
        await FinishedAsync(administrator, protectedId);
        using (var protectedArtifacts = JsonDocument.Parse(await administrator.GetStringAsync($"/api/v1/builds/{protectedId}/artifacts")))
        {
            var href = protectedArtifacts.RootElement.GetProperty("items")[0].GetProperty("href").GetString()!;
            Assert.That((await administrator.GetByteArrayAsync(href)).Length, Is.Positive);
            Assert.That((await api.GetAsync(href)).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        }
        Assert.That((await api.GetAsync($"/api/v1/builds/{protectedId}")).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        foreach (var path in new[] { "/app/rest/buildTypes/id:Q_LocalSmoke", $"/app/rest/builds/id:{protectedId}",
            $"/app/rest/builds/id:{protectedId}/artifacts/files/proof.txt" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Accept.ParseAdd(path.Contains("/artifacts/", StringComparison.Ordinal) ? "*/*" : "application/json");
            using var denied = await api.SendAsync(request);
            Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden), path);
            Assert.That(await denied.Content.ReadAsStringAsync(), Does.Not.Contain("owned-smoke-artifact"));
        }
        using (var protectedOutput = JsonDocument.Parse(await administrator.GetStringAsync($"/api/v1/builds/{protectedId}/output")))
        {
            protectedOutputHref = protectedOutput.RootElement.GetProperty("items").EnumerateArray().First(e => e.GetProperty("kind").GetString() == "data")
                .GetProperty("content").GetProperty("href").GetString();
            Assert.That((await administrator.GetByteArrayAsync(protectedOutputHref)).Length, Is.Positive);
            Assert.That((await api.GetAsync(protectedOutputHref)).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That((await api.GetAsync($"/api/v1/builds/{protectedId}/output")).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        }

        buildId = await SubmitAsync(api, "P_LocalSmoke");
        await FinishedAsync(api, buildId);
        using (var artifacts = JsonDocument.Parse(await api.GetStringAsync($"/api/v1/builds/{buildId}/artifacts")))
            artifactHref = artifacts.RootElement.GetProperty("items")[0].GetProperty("href").GetString();
        using (var output = JsonDocument.Parse(await api.GetStringAsync($"/api/v1/builds/{buildId}/output")))
        {
            resumeCursor = output.RootElement.GetProperty("output").GetProperty("resumeCursor").GetString();
            outputHref = output.RootElement.GetProperty("items").EnumerateArray().First(i => i.GetProperty("kind").GetString() == "data")
                .GetProperty("content").GetProperty("href").GetString();
        }
        Assert.That((await api.GetByteArrayAsync(artifactHref)).Length, Is.Positive);
        Assert.That((await api.GetByteArrayAsync(outputHref)).Length, Is.Positive);
        await FailureAndCancellationAsync(administrator, csrf);

        await MutateAsync(administrator, csrf, HttpMethod.Delete, RolePath, null, HttpStatusCode.NoContent);
        await AssertCurrentAsync(HttpStatusCode.OK, HttpStatusCode.NotFound);
        await MutateAsync(administrator, csrf, HttpMethod.Put, RolePath, null, HttpStatusCode.NoContent);
        await AssertCurrentAsync(HttpStatusCode.OK, HttpStatusCode.OK);
        await MutateAsync(administrator, csrf, HttpMethod.Put, $"/api/v1/users/{userId}/enabled", "{\"enabled\":false}", HttpStatusCode.NoContent);
        await AssertCurrentAsync(HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized);
        await MutateAsync(administrator, csrf, HttpMethod.Put, $"/api/v1/users/{userId}/enabled", "{\"enabled\":true}", HttpStatusCode.NoContent);
        await AssertCurrentAsync(HttpStatusCode.OK, HttpStatusCode.OK);
    }

    internal async Task AfterRestartAndRevokeAsync(IServiceProvider services, HttpClient administrator)
    {
        await AssertCurrentAsync(HttpStatusCode.OK, HttpStatusCode.OK);
        var cursorPath = $"/api/v1/builds/{buildId}/output?cursor=" + Uri.EscapeDataString(resumeCursor!);
        using (var resumed = await api.GetAsync(cursorPath)) Assert.That(resumed.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        outputClock.Advance(TimeSpan.FromMinutes(16));
        using (var expired = await api.GetAsync(cursorPath))
        {
            Assert.That(expired.StatusCode, Is.EqualTo(HttpStatusCode.Gone));
            using var problem = JsonDocument.Parse(await expired.Content.ReadAsStringAsync());
            Assert.That(problem.RootElement.GetProperty("code").GetString(), Is.EqualTo("cursor_expired"));
            using var recovered = await api.GetAsync(problem.RootElement.GetProperty("recovery").GetProperty("href").GetString());
            Assert.That(recovered.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        }
        using (var wrongActor = await administrator.GetAsync(cursorPath))
            Assert.That(wrongActor.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), "Cursor ownership is checked before expiry recovery.");
        using (var tampered = await api.GetAsync(cursorPath[..^1] + "!"))
            Assert.That(tampered.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        await RevokeOwnKeyAsync();
        await AssertCurrentAsync(HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized);
        await RetainedAdminBytesAsync(administrator);
        await CreateKeyAsync();
        await AssertCurrentAsync(HttpStatusCode.OK, HttpStatusCode.OK);
        var shorterKey = api.DefaultRequestHeaders.Authorization;
        await CreateKeyAsync(lifetimeDays: 3);
        var longerKey = api.DefaultRequestHeaders.Authorization;
        api.DefaultRequestHeaders.Authorization = shorterKey;
        validationClock.Advance(TimeSpan.FromDays(2));
        await AssertCurrentAsync(HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized);
        await RetainedAdminBytesAsync(administrator);
        api.DefaultRequestHeaders.Authorization = longerKey;
        await AssertCurrentAsync(HttpStatusCode.OK, HttpStatusCode.OK);
        await RetentionAsync(services, administrator);
    }

    private async Task RetainedAdminBytesAsync(HttpClient administrator)
    {
        Assert.That((await administrator.GetByteArrayAsync(artifactHref)).Length, Is.Positive);
        Assert.That((await administrator.GetByteArrayAsync(outputHref)).Length, Is.Positive);
    }

    private async Task RetentionAsync(IServiceProvider services, HttpClient administrator)
    {
        var databases = services.GetRequiredService<IDbContextFactory<VivariumDbContext>>();
        RuntimeOutputRange[] original;
        await using (var database = await databases.CreateDbContextAsync())
            original = await database.RuntimeOutputRanges.AsNoTracking().Where(r => r.BuildId == buildId).OrderBy(r => r.ReceiptOrder).ToArrayAsync();
        var producerHash = System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(original));
        using var current = JsonDocument.Parse(await api.GetStringAsync($"/api/v1/builds/{buildId}/output"));
        var currentCursor = current.RootElement.GetProperty("output").GetProperty("resumeCursor").GetString()!;
        var entryId = Guid.Parse(new Uri(origin, outputHref).Segments[^2].Trim('/'));
        var stored = await services.GetRequiredService<IObjectStorage>().DownloadAsync(new GetDownloadUrl(
            original.Single(r => r.Id == entryId).ObjectKey!, "owned-output.bin"));
        using var privateStorage = new HttpClient(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false })
            { Timeout = TimeSpan.FromSeconds(15) };
        Assert.That((await privateStorage.GetAsync(stored.Url)).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        await using (var database = await databases.CreateDbContextAsync())
        {
            await database.Database.ExecuteSqlRawAsync("""
                CREATE TABLE "OwnedRetentionDeleteFault" ("RangeId" uuid PRIMARY KEY);
                CREATE FUNCTION public.owned_reject_retention_delete_mark() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN IF OLD."DeletedAt" IS NULL AND NEW."DeletedAt" IS NOT NULL AND EXISTS (
                    SELECT 1 FROM "OwnedRetentionDeleteFault" fault WHERE fault."RangeId" = NEW."RangeId")
                    THEN RAISE EXCEPTION 'Owned retention deletion commit fault' USING ERRCODE = '23514'; END IF;
                    RETURN NEW; END $$;
                CREATE TRIGGER "OwnedRetention_DeleteCommitFault" BEFORE UPDATE ON "RuntimeOutputExpiries"
                    FOR EACH ROW EXECUTE FUNCTION public.owned_reject_retention_delete_mark();
                """);
            await database.Database.ExecuteSqlInterpolatedAsync($"""INSERT INTO "OwnedRetentionDeleteFault" ("RangeId") VALUES ({entryId})""");
        }
        // Only the production retention clock advances. The just-issued pagination cursor has not
        // aged out: this checks the retained-view epoch rather than the separate 15-minute timeout.
        retentionClock.Advance(TimeSpan.FromDays(2));
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var faultObserved = false;
        while (true)
        {
            try { await services.GetRequiredService<RuntimeOutputRetention>().TickAsync(bound.Token); }
            catch (Npgsql.PostgresException error) when (error.SqlState == "23514" && !faultObserved)
            {
                faultObserved = true;
                await using var state = await databases.CreateDbContextAsync(bound.Token);
                Assert.That(await state.RuntimeOutputExpiries.AnyAsync(e => e.RangeId == entryId && e.DeletedAt == null, bound.Token), Is.True);
                Assert.That((await privateStorage.GetAsync(stored.Url, bound.Token)).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
                Assert.That((await api.GetAsync(outputHref, bound.Token)).StatusCode, Is.EqualTo(HttpStatusCode.Gone));
                await state.Database.ExecuteSqlRawAsync("""
                    DROP TRIGGER "OwnedRetention_DeleteCommitFault" ON "RuntimeOutputExpiries";
                    DROP FUNCTION public.owned_reject_retention_delete_mark();
                    DROP TABLE "OwnedRetentionDeleteFault";
                    """, bound.Token);
            }
            await using var database = await databases.CreateDbContextAsync(bound.Token);
            if (await database.RuntimeOutputExpiries.AnyAsync(e => e.RangeId == entryId && e.DeletedAt != null, bound.Token)) break;
        }
        Assert.That(faultObserved, Is.True, "A physical deletion succeeded before its acknowledgement transaction failed; retry must adopt it.");
        using (var expiredCursor = await api.GetAsync($"/api/v1/builds/{buildId}/output?cursor=" + Uri.EscapeDataString(currentCursor)))
        {
            Assert.That(expiredCursor.StatusCode, Is.EqualTo(HttpStatusCode.Gone));
            using var problem = JsonDocument.Parse(await expiredCursor.Content.ReadAsStringAsync());
            Assert.That(problem.RootElement.GetProperty("code").GetString(), Is.EqualTo("cursor_expired"));
            using var recovered = JsonDocument.Parse(await api.GetStringAsync(problem.RootElement.GetProperty("recovery").GetProperty("href").GetString()));
            Assert.That(recovered.RootElement.GetProperty("output").GetProperty("state").GetString(), Is.EqualTo("complete-with-gaps"));
            Assert.That(recovered.RootElement.GetProperty("output").GetProperty("retention").GetProperty("days").GetInt32(), Is.EqualTo(1));
            retainedCursor = recovered.RootElement.GetProperty("output").GetProperty("resumeCursor").GetString();
            Assert.That(recovered.RootElement.GetProperty("items").EnumerateArray().All(e =>
                e.GetProperty("kind").GetString() == "gap" && e.GetProperty("reason").GetString() == "retention" && !e.TryGetProperty("content", out _)), Is.True);
        }
        Assert.That((await api.GetAsync(outputHref)).StatusCode, Is.EqualTo(HttpStatusCode.Gone));
        Assert.That((await privateStorage.GetAsync(stored.Url)).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That((await api.GetAsync($"/api/v1/builds/{buildId}/output/{Guid.NewGuid()}/content")).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That((await api.GetAsync(protectedOutputHref)).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        await using (var database = await databases.CreateDbContextAsync())
        {
            var unchanged = await database.RuntimeOutputRanges.AsNoTracking().Where(r => r.BuildId == buildId).OrderBy(r => r.ReceiptOrder).ToArrayAsync();
            Assert.That(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(unchanged)), Is.EqualTo(producerHash));
            Assert.That(await database.AuditEvents.CountAsync(e => e.Action == "build.output.expired" && e.TargetId == buildId.ToString()), Is.EqualTo(1));
        }
        var shell = await administrator.GetStringAsync("/");
        using var session = JsonDocument.Parse(Regex.Match(shell, "<script id=\"vivarium-session\" type=\"application/json\">(.*?)</script>", RegexOptions.Singleline).Groups[1].Value);
        var csrf = session.RootElement.GetProperty("requestVerificationToken").GetString()!;
        await MutateAsync(administrator, csrf, HttpMethod.Delete, RolePath, null, HttpStatusCode.NoContent);
        Assert.That((await api.GetAsync(outputHref)).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        await MutateAsync(administrator, csrf, HttpMethod.Put, RolePath, null, HttpStatusCode.NoContent);
        Assert.That((await api.GetAsync(outputHref)).StatusCode, Is.EqualTo(HttpStatusCode.Gone));
    }

    internal async Task AfterRetentionRestartAsync(Func<string> failureTypes)
    {
        using var build = await api.GetAsync($"/api/v1/builds/{buildId}");
        Assert.That(build.StatusCode, Is.EqualTo(HttpStatusCode.OK), failureTypes());
        using var expired = await api.GetAsync(outputHref);
        Assert.That(expired.StatusCode, Is.EqualTo(HttpStatusCode.Gone));
        using var resumed = await api.GetAsync($"/api/v1/builds/{buildId}/output?cursor=" + Uri.EscapeDataString(retainedCursor!));
        Assert.That(resumed.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        using var output = JsonDocument.Parse(await resumed.Content.ReadAsStringAsync());
        Assert.That(output.RootElement.GetProperty("items").GetArrayLength(), Is.Zero);
        Assert.That(output.RootElement.GetProperty("output").GetProperty("state").GetString(), Is.EqualTo("complete-with-gaps"));
    }

    internal async Task CreateKeyAsync(int lifetimeDays = 1)
    {
        var html = await browser.GetStringAsync("/Identity/Account/Manage/Tokens");
        var prior = KeyIds(html).ToHashSet(StringComparer.Ordinal);
        using var creation = await browser.PostAsync("/Identity/Account/Manage/Tokens?handler=Create",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["Label"] = "Owned scoped build key", ["LifetimeDays"] = lifetimeDays.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["__RequestVerificationToken"] = Antiforgery(html) }));
        var disclosure = await FollowAsync(creation);
        var match = Regex.Match(disclosure, "<textarea[^>]*id=\"token\"[^>]*>(.*?)</textarea>", RegexOptions.Singleline);
        if (!match.Success) throw new InvalidDataException("The ordinary framework key disclosure is absent.");
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", WebUtility.HtmlDecode(match.Groups[1].Value).Trim());
        issuedKeyId = KeyIds(await browser.GetStringAsync("/Identity/Account/Manage/Tokens")).Except(prior, StringComparer.Ordinal).Single();
    }

    internal async Task RevokeOwnKeyAsync()
    {
        var html = await browser.GetStringAsync("/Identity/Account/Manage/Tokens");
        if (issuedKeyId is null || !KeyIds(html).Contains(issuedKeyId, StringComparer.Ordinal))
            throw new InvalidDataException("The ordinary own-key metadata is absent.");
        using var revoked = await browser.PostAsync("/Identity/Account/Manage/Tokens?handler=Revoke",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["id"] = issuedKeyId,
                ["__RequestVerificationToken"] = Antiforgery(html) }));
        Assert.That(revoked.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
    }

    private static IEnumerable<string> KeyIds(string html) => Regex.Matches(html, "name=\"id\"[^>]*value=\"([^\"]+)\"")
        .Select(match => WebUtility.HtmlDecode(match.Groups[1].Value));

    private sealed class ScenarioClock : TimeProvider
    {
        private long offset;
        public override DateTimeOffset GetUtcNow() => System.GetUtcNow().AddTicks(Interlocked.Read(ref offset));
        internal void Advance(TimeSpan elapsed) => Interlocked.Add(ref offset, elapsed.Ticks);
        internal void Reset() => Interlocked.Exchange(ref offset, 0);
    }

    private async Task DelegationAsync(IServiceProvider services, HttpClient administrator, string csrf)
    {
        Guid targetId;
        await using (var scope = services.CreateAsyncScope())
        {
            var user = new VivariumUser { Id = Guid.NewGuid(), UserName = "owned-delegation-target" };
            Assert.That((await scope.ServiceProvider.GetRequiredService<UserManager<VivariumUser>>().CreateAsync(user)).Succeeded, Is.True);
            targetId = user.Id;
            var actor = new ManagementRequestContext(new ManagementPrincipal("user", VivariumIdentityConstants.SuperuserId.ToString(),
                "owned-component", null), ManagementIdentifiers.NewId(), null, "owned-role-prior-state");
            var changes = scope.ServiceProvider.GetRequiredService<TeamCityAccessService>();
            Assert.That(await changes.SetRoleAsync(actor, "OWNED_DELEGATOR", "Owned narrow delegator",
                ["change_user_roles_in_project", "view_project", "view_user_profile"]), Is.EqualTo(AccessMutationResult.Applied));
            Assert.That(await changes.SetRoleAsync(actor, "OWNED_RUN_ONLY", "Owned run only", ["run_build"]), Is.EqualTo(AccessMutationResult.Applied));
        }
        string Target(string role, string project) => $"/api/v1/users/{targetId}/roles/{role}/projects/{project}";
        string Own(string role) => $"/api/v1/users/{userId}/roles/{role}/projects/P";
        await MutateAsync(api, csrf, HttpMethod.Put, Target("PROJECT_DEVELOPER", "P"), null, HttpStatusCode.Forbidden);
        await MutateAsync(administrator, csrf, HttpMethod.Put, Own("PROJECT_ADMIN"), null, HttpStatusCode.NoContent);
        Assert.That((await api.GetAsync("/api/v1/roles")).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await api.GetAsync($"/api/v1/users/{targetId}/roles")).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        await MutateAsync(api, csrf, HttpMethod.Put, Target("PROJECT_DEVELOPER", "P"), null, HttpStatusCode.NoContent);
        await MutateAsync(api, csrf, HttpMethod.Put, Target("PROJECT_ADMIN", "P"), null, HttpStatusCode.NoContent);
        await MutateAsync(api, csrf, HttpMethod.Put, Target("PROJECT_DEVELOPER", "Q"), null, HttpStatusCode.Forbidden);
        await MutateAsync(api, csrf, HttpMethod.Put, Target("SYSTEM_ADMIN", "P"), null, HttpStatusCode.Forbidden);
        await MutateAsync(api, csrf, HttpMethod.Put, Target("AGENT_MANAGER", "P"), null, HttpStatusCode.Forbidden);
        await MutateAsync(api, csrf, HttpMethod.Put, $"/api/v1/users/{targetId}/roles/PROJECT_DEVELOPER/g", null, HttpStatusCode.Forbidden);
        await MutateAsync(api, csrf, HttpMethod.Delete, Target("PROJECT_DEVELOPER", "P"), null, HttpStatusCode.NoContent);
        await MutateAsync(api, csrf, HttpMethod.Delete, Target("PROJECT_ADMIN", "P"), null, HttpStatusCode.NoContent);
        await MutateAsync(administrator, csrf, HttpMethod.Delete, Own("PROJECT_ADMIN"), null, HttpStatusCode.NoContent);
        await MutateAsync(administrator, csrf, HttpMethod.Delete, RolePath, null, HttpStatusCode.NoContent);
        await MutateAsync(administrator, csrf, HttpMethod.Put, Own("OWNED_DELEGATOR"), null, HttpStatusCode.NoContent);
        await MutateAsync(api, csrf, HttpMethod.Put, Target("OWNED_DELEGATOR", "P"), null, HttpStatusCode.NoContent);
        await MutateAsync(api, csrf, HttpMethod.Put, Target("OWNED_RUN_ONLY", "P"), null, HttpStatusCode.Forbidden);
        await MutateAsync(api, csrf, HttpMethod.Delete, Target("OWNED_DELEGATOR", "P"), null, HttpStatusCode.NoContent);
        await MutateAsync(administrator, csrf, HttpMethod.Delete, Own("OWNED_DELEGATOR"), null, HttpStatusCode.NoContent);
        await MutateAsync(administrator, csrf, HttpMethod.Put, RolePath, null, HttpStatusCode.NoContent);
        using var grants = JsonDocument.Parse(await administrator.GetStringAsync($"/api/v1/users/{targetId}/roles"));
        Assert.That(grants.RootElement.GetProperty("items").GetArrayLength(), Is.Zero);
    }

    private string RolePath => $"/api/v1/users/{userId}/roles/PROJECT_DEVELOPER/projects/P";

    private async Task FailureAndCancellationAsync(HttpClient administrator, string csrf)
    {
        var failedId = await SubmitAsync(api, "P_Nonzero");
        var failed = await WaitBuildAsync(api, failedId, value => value.GetProperty("state").GetString() == "finished");
        Assert.That(failed.GetProperty("status").GetString(), Is.EqualTo("FAILURE"));
        Assert.That(failed.GetProperty("failedToStart").GetBoolean(), Is.False);
        var runningId = await SubmitAsync(api, "P_Wait");
        var running = await WaitBuildAsync(api, runningId, value => value.GetProperty("executionPhase").GetString() == "accepted");
        Assert.That(running.GetProperty("status").GetString(), Is.EqualTo("SUCCESS"),
            "The reference reports a running Build with no observed failure as SUCCESS, independently of terminal state.");
        using (var bound = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
        {
            var observed = false;
            while (!observed)
            {
                using var output = JsonDocument.Parse(await api.GetStringAsync($"/api/v1/builds/{runningId}/output", bound.Token));
                foreach (var entry in output.RootElement.GetProperty("items").EnumerateArray().Where(e => e.GetProperty("kind").GetString() == "data"))
                {
                    var text = await api.GetStringAsync(entry.GetProperty("content").GetProperty("href").GetString(), bound.Token);
                    observed |= text.Contains("owned-running-start", StringComparison.Ordinal);
                }
                if (!observed) await Task.Delay(100, bound.Token);
            }
        }
        var queuedId = await SubmitAsync(api, "P_LocalSmoke");
        using var previous = JsonDocument.Parse(await api.GetStringAsync($"/api/v1/builds/{buildId}"));
        var previousNumber = int.Parse(previous.RootElement.GetProperty("number").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        using (var queued = JsonDocument.Parse(await api.GetStringAsync($"/api/v1/builds/{queuedId}")))
        {
            Assert.That(queued.RootElement.GetProperty("state").GetString(), Is.EqualTo("queued"));
            Assert.That(queued.RootElement.GetProperty("number").ValueKind, Is.EqualTo(JsonValueKind.Null));
        }
        await MutateAsync(api, csrf, HttpMethod.Put, $"/api/v1/builds/{queuedId}/cancellation",
            "{\"reason\":\"Owned queued cancellation\"}", HttpStatusCode.Accepted);
        var cancelled = await WaitBuildAsync(api, queuedId, value => value.GetProperty("state").GetString() == "finished");
        Assert.That(cancelled.GetProperty("agentId").ValueKind, Is.EqualTo(JsonValueKind.Null));
        Assert.That(cancelled.GetProperty("status").GetString(), Is.EqualTo("UNKNOWN"));
        Assert.That(cancelled.GetProperty("number").GetString(), Is.EqualTo("N/A"));
        await MutateAsync(api, csrf, HttpMethod.Put, $"/api/v1/builds/{runningId}/cancellation",
            "{\"reason\":\"Owned running cancellation\"}", HttpStatusCode.Accepted);
        using (var pending = JsonDocument.Parse(await api.GetStringAsync($"/api/v1/builds/{runningId}")))
            Assert.That(pending.RootElement.GetProperty("state").GetString(), Is.Not.EqualTo("finished"),
                "A stop request is not process completion while the owned console process remains alive.");
        const string force = "{\"reason\":\"Owned explicit escalation\",\"mode\":\"force\"}";
        await MutateAsync(api, csrf, HttpMethod.Put, $"/api/v1/builds/{runningId}/cancellation", force, HttpStatusCode.Forbidden);
        await MutateAsync(administrator, csrf, HttpMethod.Put, $"/api/v1/builds/{runningId}/cancellation", force, HttpStatusCode.Accepted);
        var stopped = await WaitBuildAsync(api, runningId, value => value.GetProperty("state").GetString() == "finished");
        Assert.That(stopped.GetProperty("status").GetString(), Is.EqualTo("UNKNOWN"));
        Assert.That(stopped.GetProperty("cancelReason").GetString(), Is.EqualTo("Owned running cancellation"));
        Assert.That(stopped.GetProperty("statusText").GetString(), Is.EqualTo("Canceled"));
        // Actual later work proves the old queue entry does not run and the stopped lease releases.
        var nextId = await SubmitAsync(api, "P_LocalSmoke");
        await FinishedAsync(api, nextId);
        using var next = JsonDocument.Parse(await api.GetStringAsync($"/api/v1/builds/{nextId}"));
        Assert.That(next.RootElement.GetProperty("number").GetString(), Is.EqualTo((previousNumber + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)),
            "The reference queued-cancel case has N/A and does not consume the next started Build number.");
        using var retained = JsonDocument.Parse(await api.GetStringAsync($"/api/v1/builds/{queuedId}"));
        Assert.That(retained.RootElement.GetProperty("agentId").ValueKind, Is.EqualTo(JsonValueKind.Null));
    }
    private async Task AssertCurrentAsync(HttpStatusCode identityStatus, HttpStatusCode resourceStatus)
    {
        foreach (var (path, expected) in new[] { ("/api/v1/users/current", identityStatus), ($"/api/v1/builds/{buildId}", resourceStatus),
            (artifactHref!, resourceStatus), (outputHref!, resourceStatus) })
        {
            using var response = await api.GetAsync(path);
            Assert.That(response.StatusCode, Is.EqualTo(expected), "The same ordinary key must use current eligibility/grants for metadata and bytes.");
            Assert.That(response.Headers.Location, Is.Null);
        }
        foreach (var path in new[] { "/app/rest/users/current", $"/app/rest/builds/id:{buildId}",
            $"/app/rest/builds/id:{buildId}/artifacts/files/proof.txt" })
        {
            var expected = path == "/app/rest/users/current" ? identityStatus
                : resourceStatus == HttpStatusCode.NotFound ? HttpStatusCode.Forbidden : resourceStatus;
            using var response = await api.GetAsync(path);
            Assert.That(response.StatusCode, Is.EqualTo(expected), "Compatible reads use the same current credential and grants: " + path);
            if (expected == HttpStatusCode.Unauthorized)
                Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("Could not authenticate with provided token\n"));
        }
    }

    private async Task<string> FollowAsync(HttpResponseMessage first)
    {
        var response = first;
        try
        {
            for (var remaining = 0; remaining < 12; remaining++)
            {
                if (response.StatusCode == HttpStatusCode.OK) return await response.Content.ReadAsStringAsync();
                if (response.StatusCode is not (HttpStatusCode.Redirect or HttpStatusCode.SeeOther) || response.Headers.Location is null)
                {
                    var body = await response.Content.ReadAsStringAsync();
                    var code = "none";
                    try
                    {
                        using var failure = JsonDocument.Parse(body);
                        if (failure.RootElement.TryGetProperty("error", out var value) && value.GetString() is
                            "invalid_request" or "invalid_client" or "invalid_scope" or "server_error" or "access_denied" or "unauthorized_client")
                            code = value.GetString()!;
                    }
                    catch (JsonException) { }
                    var reference = Regex.Match(body, "https://documentation\\.openiddict\\.com/errors/(ID[0-9]{4})").Groups[1].Value;
                    throw new InvalidDataException($"The ordinary authorization-code flow did not complete: status {(int)response.StatusCode}, step {remaining}, route {response.RequestMessage?.RequestUri?.AbsolutePath}, media {response.Content.Headers.ContentType?.MediaType}, bytes {Encoding.UTF8.GetByteCount(body)}, protocol {code}, reference {reference}.");
                }
                var next = response.Headers.Location.IsAbsoluteUri ? response.Headers.Location : new Uri(origin, response.Headers.Location);
                if (next.GetLeftPart(UriPartial.Authority) != origin.GetLeftPart(UriPartial.Authority))
                    throw new InvalidDataException("The ordinary token flow left its owned origin.");
                var previous = response; response = await browser.GetAsync(next);
                if (!ReferenceEquals(previous, first)) previous.Dispose();
            }
            throw new InvalidDataException("The ordinary token redirect bound was exceeded.");
        }
        finally { if (!ReferenceEquals(response, first)) response.Dispose(); }
    }

    private static string Antiforgery(string html)
    {
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        return match.Success ? WebUtility.HtmlDecode(match.Groups[1].Value)
            : throw new InvalidDataException("The ordinary form request-verification token is absent.");
    }
    private static async Task MutateAsync(HttpClient http, string csrf, HttpMethod method, string path, string? body, HttpStatusCode expected)
    {
        using var request = new HttpRequestMessage(method, path); request.Headers.Add("RequestVerificationToken", csrf);
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request); Assert.That(response.StatusCode, Is.EqualTo(expected));
    }
    private static async Task<long> SubmitAsync(HttpClient http, string configuration, string? csrf = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/builds")
        { Content = new StringContent(JsonSerializer.Serialize(new { configurationId = configuration }), Encoding.UTF8, "application/json") };
        if (csrf is not null) request.Headers.Add("RequestVerificationToken", csrf);
        using var response = await http.SendAsync(request); Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        using var value = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); return value.RootElement.GetProperty("id").GetInt64();
    }
    private static async Task FinishedAsync(HttpClient http, long id)
    {
        var finished = await WaitBuildAsync(http, id, value => value.GetProperty("state").GetString() == "finished");
        Assert.That(finished.GetProperty("status").GetString(), Is.EqualTo("SUCCESS"));
    }
    private static async Task<JsonElement> WaitBuildAsync(HttpClient http, long id, Func<JsonElement, bool> predicate)
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        while (true)
        {
            using var response = await http.GetAsync($"/api/v1/builds/{id}", bound.Token);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            using var value = JsonDocument.Parse(await response.Content.ReadAsStringAsync(bound.Token));
            if (predicate(value.RootElement)) return value.RootElement.Clone();
            await Task.Delay(100, bound.Token);
        }
    }
    public void Dispose() { api.Dispose(); browser.Dispose(); }
}
