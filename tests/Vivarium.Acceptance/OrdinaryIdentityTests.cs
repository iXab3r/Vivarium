using System.Net.Http.Headers;
using System.Text.Json;
using EyeAuras.Automation;
using EyeAuras.Automation.Playwright;
using Microsoft.Playwright;
using Npgsql;
using Vivarium.Acceptance.Fixtures;

namespace Vivarium.Acceptance;

/// <summary>Selected AC-AUTH-01/06 and R0 credential lifecycle components; not whole-story acceptance.</summary>
[NonParallelizable]
public sealed class OrdinaryIdentityTests
{
    [Test, Category("identity")]
    public async Task Trusted_provider_login_and_My_keys_use_real_framework_issuance_and_immediate_revocation()
    {
        var run = await AutomationRun.ConnectAsync();
        var scope = run.CreateScope();
        Exception? failure = null;
        var phase = "fixture-startup";
        var reached = new List<string>();
        var responses = new System.Collections.Concurrent.ConcurrentQueue<string>();
        void Phase(string value) { phase = value; reached.Add(value); }
        try
        {
            var postgres = await PostgresFixture.StartAsync(scope, "/usr/lib/postgresql/16/bin");
            var tls = await RunTls.CreateAsync(scope);
            var origin = $"https://127.0.0.1:{FixturePorts.Loopback()}";
            var issuer = await IssuerFixture.StartAsync(scope, postgres, tls, origin + "/signin-oidc/eyeauras");
            await issuer.SetPersonaAsync("owned-ordinary-alice");
            var host = await HostFixture.StartAsync(scope, postgres, tls, issuer, origin);
            using var playwright = await Microsoft.Playwright.Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new()
                { Headless = true, Env = tls.Environment.ToDictionary(pair => pair.Key, pair => pair.Value!) });
            // Credential surfaces cannot be written into traces, screenshots, HAR or exception excerpts.
            var session = await BrowserSession.CreateAsync(browser, Path.Combine(scope.ArtifactDirectory, "browser"),
                new() { CaptureDiagnostics = false });
            try
            {
                Phase("external-sign-in");
                var page = session.Page;
                page.Response += (_, response) =>
                {
                    var uri = new Uri(response.Url);
                    var owner = uri.GetLeftPart(UriPartial.Authority) == host.Origin ? "host"
                        : uri.GetLeftPart(UriPartial.Authority) == issuer.Issuer.TrimEnd('/') ? "issuer" : "upstream";
                    // Never record query/fragment, URLs, headers or bodies on credential surfaces.
                    var route = owner == "host" && uri.AbsolutePath == "/signin-oidc/eyeauras" ? "oidc-callback"
                        : owner == "host" && uri.AbsolutePath == "/Identity/Account/Manage/Tokens" ? "my-keys" : "page";
                    responses.Enqueue(owner + ":" + route + ":" + response.Status);
                };
                await page.GotoAsync(host.Origin + "/Identity/Account/Manage/Tokens");
                await page.GetByRole(AriaRole.Button, new() { Name = "Continue with eyeauras", Exact = true }).ClickAsync();
                // The real issuer forwards to its deterministic upstream; only the upstream persona is synthetic.
                await page.GetByTestId("login-external-provider-google").ClickAsync(new() { Timeout = 60000 });
                await page.GetByRole(AriaRole.Heading, new() { Name = "My keys", Exact = true }).WaitForAsync(new() { Timeout = 60000 });
                await using var current = await page.Context.APIRequest.GetAsync(host.Origin + "/api/v1/users/current");
                Assert.That(current.Status, Is.EqualTo(200));
                Phase("current-account");
                using var account = JsonDocument.Parse(await current.TextAsync());
                var id = account.RootElement.GetProperty("id").GetGuid();
                Assert.That(account.RootElement.GetProperty("teamCityId").GetInt64(), Is.Positive);
                Phase("issue-key");
                await page.GetByLabel("Label", new() { Exact = true }).FillAsync("Owned fixture key");
                await page.GetByRole(AriaRole.Button, new() { Name = "Get token", Exact = true }).ClickAsync();
                await page.GetByRole(AriaRole.Heading, new() { Name = "New token", Exact = true }).WaitForAsync();
                var raw = (await page.Locator("#token").InputValueAsync()).Trim();
                Assert.That(raw.Length > 100, Is.True, "One-time framework credential absent.");
                Phase("bearer-read");
                using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false });
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", raw);
                using (var response = await http.GetAsync(host.Origin + "/api/v1/users/current"))
                {
                    Assert.That((int)response.StatusCode, Is.EqualTo(200));
                    using var tokenAccount = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                    Assert.That(tokenAccount.RootElement.GetProperty("id").GetGuid(), Is.EqualTo(id));
                }
                await page.GetByRole(AriaRole.Link, new() { Name = "Done", Exact = true }).ClickAsync();
                Assert.That(await page.Locator("#token").CountAsync(), Is.Zero);
                Phase("audit-rollback");
                await using var state = new NpgsqlConnection(postgres.MaintenanceConnection);
                await state.OpenAsync();
                await using (var fault = new NpgsqlCommand("""
                    CREATE FUNCTION public.owned_reject_revoke_audit() RETURNS trigger LANGUAGE plpgsql AS $$
                    BEGIN IF NEW."Action" = 'credential.revoke' THEN
                        RAISE EXCEPTION 'Owned credential audit fault' USING ERRCODE = '23514'; END IF; RETURN NEW; END $$;
                    CREATE TRIGGER "Owned_RevokeAuditFault" BEFORE INSERT ON "AuditEvents"
                        FOR EACH ROW EXECUTE FUNCTION public.owned_reject_revoke_audit();
                    """, state)) await fault.ExecuteNonQueryAsync();
                try
                {
                    var form = page.Locator("form[action*='handler=Revoke']");
                    var tokenId = await form.Locator("input[name='id']").InputValueAsync();
                    var csrf = await form.Locator("input[name='__RequestVerificationToken']").InputValueAsync();
                    var revokeInput = page.Context.APIRequest.CreateFormData();
                    revokeInput.Set("id", tokenId);
                    revokeInput.Set("__RequestVerificationToken", csrf);
                    await using var rejected = await page.Context.APIRequest.PostAsync(
                        host.Origin + "/Identity/Account/Manage/Tokens?handler=Revoke", new()
                        { Form = revokeInput });
                    Assert.That(rejected.Status, Is.EqualTo(500));
                    using var stillValid = await http.GetAsync(host.Origin + "/api/v1/users/current");
                    Assert.That((int)stillValid.StatusCode, Is.EqualTo(200), "A failed audit rolls back revocation, including framework cache effects.");
                }
                finally
                {
                    await using var removeFault = new NpgsqlCommand("""
                        DROP TRIGGER "Owned_RevokeAuditFault" ON "AuditEvents";
                        DROP FUNCTION public.owned_reject_revoke_audit();
                        """, state);
                    await removeFault.ExecuteNonQueryAsync();
                }
                Phase("revoke-key");
                await page.GetByRole(AriaRole.Button, new() { Name = "Revoke", Exact = true }).ClickAsync();
                using (var revoked = await http.GetAsync(host.Origin + "/api/v1/users/current"))
                    Assert.That((int)revoked.StatusCode, Is.EqualTo(401));
                await using (var sessionStillValid = await page.Context.APIRequest.GetAsync(host.Origin + "/api/v1/users/current"))
                    Assert.That(sessionStillValid.Status, Is.EqualTo(200));
                // Even with a live cookie, an explicitly invalid bearer must never inherit its authority.
                await using (var mixed = await page.Context.APIRequest.GetAsync(host.Origin + "/api/v1/users/current",
                    new() { Headers = new Dictionary<string, string> { ["Authorization"] = "Bearer owned-invalid" } }))
                    Assert.That(mixed.Status, Is.EqualTo(401));
                Phase("issuer-and-audit-readback");
                Assert.That(await issuer.CountUsersAsync(), Is.EqualTo(1));
                Assert.That(await issuer.FindUserAsync("owned-ordinary-alice"), Is.Not.Null);
                await using var audit = new NpgsqlCommand("""
                    SELECT (SELECT count(*) FROM "AuditEvents" WHERE "Action"='credential.issue'),
                           (SELECT count(*) FROM "AuditEvents" WHERE "Action"='credential.revoke'),
                           (SELECT count(*) FROM "UserRoleGrants"),
                           (SELECT count(*) FROM "GroupRoleGrants")
                    """, state);
                await using var auditRows = await audit.ExecuteReaderAsync();
                await auditRows.ReadAsync();
                Assert.That(auditRows.GetInt64(0), Is.EqualTo(1));
                Assert.That(auditRows.GetInt64(1), Is.EqualTo(1));
                Assert.That(auditRows.GetInt64(2), Is.Zero);
                Assert.That(auditRows.GetInt64(3), Is.Zero);
                Phase("assertions-complete");
            }
            catch (Exception error) when (error is PlaywrightException or TimeoutException)
            {
                failure = new InvalidOperationException("Identity flow failed at " + phase + "; credential diagnostics disabled.");
                throw failure;
            }
            catch (Exception error) { failure = error; throw; }
            finally
            {
                await File.WriteAllTextAsync(Path.Combine(scope.ArtifactDirectory, "identity-progress.json"),
                    JsonSerializer.Serialize(new { phase, reached, responses,
                        assertionsComplete = phase == "assertions-complete" }));
                try { await session.FinishAsync(failure is not null); }
                catch (Exception) when (failure is not null)
                {
                    throw new AggregateException(failure,
                        new InvalidOperationException("Browser cleanup/checks also failed; credential diagnostics disabled."));
                }
            }
        }
        catch (Exception error) when (error is PlaywrightException or TimeoutException)
        {
            // Playwright action diagnostics can include callback URLs/codes or the token textarea.
            failure = new InvalidOperationException("Identity flow failed at " + phase + "; credential diagnostics disabled.");
            throw failure;
        }
        catch (Exception error) { failure = error; throw; }
        finally { await scope.FinishAsync(failure); }
    }
}
