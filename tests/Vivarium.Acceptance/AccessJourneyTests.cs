using System.Text.Json;
using EyeAuras.Automation;
using EyeAuras.Automation.Playwright;
using Microsoft.Playwright;
using Npgsql;
using Vivarium.Acceptance.Fixtures;

namespace Vivarium.Acceptance;

/// <summary>AC-SU-07 and R0 current grants with the ordinary account's unchanged cookie and My keys bearer.</summary>
[NonParallelizable]
public sealed class AccessJourneyTests
{
    [Test, Category("access")]
    public async Task Installation_operator_delegates_through_Workbench_and_withdrawal_and_disable_affect_the_same_cookie()
    {
        var run = await AutomationRun.ConnectAsync();
        var scope = run.CreateScope();
        Exception? failure = null;
        var phase = "startup";
        var browserCategories = new System.Collections.Concurrent.ConcurrentQueue<string>();
        try
        {
            var postgres = await PostgresFixture.StartAsync(scope, "/usr/lib/postgresql/16/bin");
            var tls = await RunTls.CreateAsync(scope);
            var origin = $"https://127.0.0.1:{FixturePorts.Loopback()}";
            var issuer = await IssuerFixture.StartAsync(scope, postgres, tls, origin + "/signin-oidc/eyeauras");
            await issuer.SetPersonaAsync("owned-access-admin");
            var host = await HostFixture.StartAsync(scope, postgres, tls, issuer, origin, installationAccess: true);
            using var playwright = await Microsoft.Playwright.Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new()
                { Headless = true, Env = tls.Environment.ToDictionary(pair => pair.Key, pair => pair.Value!) });
            var ordinary = await BrowserSession.CreateAsync(browser, Path.Combine(scope.ArtifactDirectory, "ordinary"),
                new() { CaptureDiagnostics = false });
            var installation = await BrowserSession.CreateAsync(browser, Path.Combine(scope.ArtifactDirectory, "installation"),
                new() { CaptureDiagnostics = false });
            foreach (var page in new[] { ordinary.Page, installation.Page })
            {
                page.Response += (_, response) =>
                {
                    if (response.Status < 400) return;
                    var path = new Uri(response.Url).AbsolutePath;
                    browserCategories.Enqueue((path == "/favicon.ico" ? "favicon" : path.StartsWith("/workbench/") ? "asset"
                        : path.StartsWith("/api/v1/") ? "api" : "other") + ":" + response.Status);
                };
                page.Console += (_, message) =>
                {
                    if (message.Type == "error") browserCategories.Enqueue(message.Text.StartsWith("Failed to load resource", StringComparison.Ordinal)
                        ? "console-resource-error" : "console-script-error");
                };
            }
            try
            {
                phase = "ordinary-sign-in";
                var person = ordinary.Page;
                await person.GotoAsync(origin + "/Identity/Account/Login?ReturnUrl=%2Faccess");
                await person.GetByRole(AriaRole.Button, new() { Name = "Continue with eyeauras", Exact = true }).ClickAsync();
                await person.GetByTestId("login-external-provider-google").ClickAsync(new() { Timeout = 60000 });
                await person.GetByText("Current permissions do not allow access management.", new() { Exact = true }).WaitForAsync(new() { Timeout = 60000 });
                await using var current = await person.Context.APIRequest.GetAsync(origin + "/api/v1/users/current");
                Assert.That(current.Status, Is.EqualTo(200));
                using var currentJson = JsonDocument.Parse(await current.TextAsync());
                var userId = currentJson.RootElement.GetProperty("id").GetGuid();
                var displayName = currentJson.RootElement.GetProperty("displayName").GetString()!;
                var teamCityId = currentJson.RootElement.GetProperty("teamCityId").GetInt64();
                Assert.That(teamCityId, Is.Positive);
                await ExpectAsync(person, origin + "/api/v1/users", 403);

                phase = "issue-before-grant";
                await person.GotoAsync(origin + "/Identity/Account/Manage/Tokens");
                await person.GetByLabel("Label", new() { Exact = true }).FillAsync("Owned current-grant key");
                await person.GetByRole(AriaRole.Button, new() { Name = "Get token", Exact = true }).ClickAsync();
                await person.GetByRole(AriaRole.Heading, new() { Name = "New token", Exact = true }).WaitForAsync();
                var credential = (await person.Locator("#token").InputValueAsync()).Trim();
                Assert.That(credential.Length > 100, Is.True, "The one-time framework credential is absent.");
                await BearerExpectAsync(person, origin + "/api/v1/users", credential, 403);
                await using (var tokenCurrent = await person.Context.APIRequest.GetAsync(origin + "/api/v1/users/current",
                    new() { Headers = new Dictionary<string, string> { ["Authorization"] = "Bearer " + credential } }))
                {
                    Assert.That(tokenCurrent.Status, Is.EqualTo(200));
                    using var tokenAccount = JsonDocument.Parse(await tokenCurrent.TextAsync());
                    Assert.That(tokenAccount.RootElement.GetProperty("id").GetGuid(), Is.EqualTo(userId));
                    Assert.That(tokenAccount.RootElement.GetProperty("teamCityId").GetInt64(), Is.EqualTo(teamCityId));
                }
                await person.GetByRole(AriaRole.Link, new() { Name = "Done", Exact = true }).ClickAsync();
                Assert.That(await person.Locator("#token").CountAsync(), Is.Zero);
                await person.GotoAsync(origin + "/access");

                phase = "installation-sign-in";
                var operatorPage = installation.Page;
                await operatorPage.GotoAsync(origin + "/Identity/Account/Login?ReturnUrl=%2Faccess");
                await operatorPage.Locator("summary").Filter(new() { HasText = "Installation account" }).ClickAsync();
                await operatorPage.Locator("input[name='login']").FillAsync("superuser");
                await operatorPage.Locator("input[name='token']").FillAsync(await host.InstallationPasswordAsync(scope.CancellationToken));
                await operatorPage.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).ClickAsync();
                await operatorPage.GetByRole(AriaRole.Button, new() { Name = displayName, Exact = true }).WaitForAsync();
                await operatorPage.GetByRole(AriaRole.Button, new() { Name = displayName, Exact = true }).ClickAsync();
                var administrator = operatorPage.GetByRole(AriaRole.Checkbox, new() { Name = "System administrator", Exact = true });
                Assert.That(await administrator.IsCheckedAsync(), Is.False);

                phase = "grant-administrator";
                var assignmentUrl = origin + $"/api/v1/users/{userId}/roles/SYSTEM_ADMIN/g";
                var grant = await operatorPage.RunAndWaitForResponseAsync(() => administrator.CheckAsync(),
                    response => response.Url == assignmentUrl && response.Request.Method == "PUT");
                Assert.That(grant.Status, Is.EqualTo(204));
                await ExpectAsync(person, origin + "/api/v1/users", 200);
                await BearerExpectAsync(person, origin + "/api/v1/users", credential, 200);
                await person.ReloadAsync();
                await person.GetByRole(AriaRole.Button, new() { Name = displayName, Exact = true }).WaitForAsync();
                await person.GetByRole(AriaRole.Button, new() { Name = displayName, Exact = true }).ClickAsync();
                Assert.That(await person.GetByRole(AriaRole.Checkbox,
                    new() { Name = "System administrator", Exact = true }).IsCheckedAsync(), Is.True);

                phase = "withdraw-administrator";
                var withdraw = await operatorPage.RunAndWaitForResponseAsync(() => administrator.UncheckAsync(),
                    response => response.Url == assignmentUrl && response.Request.Method == "DELETE");
                Assert.That(withdraw.Status, Is.EqualTo(204));
                await ExpectAsync(person, origin + "/api/v1/users", 403);
                await BearerExpectAsync(person, origin + "/api/v1/users", credential, 403);
                await person.ReloadAsync();
                await person.GetByText("Current permissions do not allow access management.", new() { Exact = true }).WaitForAsync();

                phase = "disable-account";
                var enabledUrl = origin + $"/api/v1/users/{userId}/enabled";
                var disabled = await operatorPage.RunAndWaitForResponseAsync(
                    () => operatorPage.GetByRole(AriaRole.Button, new() { Name = "Disable account", Exact = true }).ClickAsync(),
                    response => response.Url == enabledUrl && response.Request.Method == "PUT");
                Assert.That(disabled.Status, Is.EqualTo(204));
                await ExpectAsync(person, origin + "/api/v1/users/current", 401);
                await BearerExpectAsync(person, origin + "/api/v1/users/current", credential, 401);
                phase = "enable-account";
                var enabled = await operatorPage.RunAndWaitForResponseAsync(
                    () => operatorPage.GetByRole(AriaRole.Button, new() { Name = "Enable account", Exact = true }).ClickAsync(),
                    response => response.Url == enabledUrl && response.Request.Method == "PUT");
                Assert.That(enabled.Status, Is.EqualTo(204));
                await ExpectAsync(person, origin + "/api/v1/users/current", 200);
                await ExpectAsync(person, origin + "/api/v1/users", 403);
                await BearerExpectAsync(person, origin + "/api/v1/users/current", credential, 200);
                await BearerExpectAsync(person, origin + "/api/v1/users", credential, 403);

                phase = "installation-key-exclusion";
                await using (var excluded = await operatorPage.Context.APIRequest.GetAsync(
                    origin + "/Identity/Account/Manage/Tokens", new() { MaxRedirects = 0 }))
                {
                    Assert.That(excluded.Status, Is.EqualTo(403));
                }
                var verifier = new string('v', 43);
                var challenge = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
                var authorize = origin + "/connect/authorize?client_id=vivarium-token-ui&response_type=code&scope=vivarium_api"
                    + "&redirect_uri=" + Uri.EscapeDataString(origin + "/signin-vivarium-token")
                    + "&code_challenge_method=S256&code_challenge=" + challenge + "&vivarium_label=Owned&vivarium_lifetime_days=1";
                await using (var excluded = await operatorPage.Context.APIRequest.GetAsync(authorize, new() { MaxRedirects = 0 }))
                    Assert.That(excluded.Status, Is.EqualTo(403));
                var redemption = operatorPage.Context.APIRequest.CreateFormData();
                redemption.Set("grant_type", "authorization_code"); redemption.Set("client_id", "vivarium-token-ui");
                redemption.Set("redirect_uri", origin + "/signin-vivarium-token");
                redemption.Set("code_verifier", verifier); redemption.Set("code", "owned-no-installation-code");
                await using (var excluded = await operatorPage.Context.APIRequest.PostAsync(origin + "/connect/token", new() { Form = redemption }))
                {
                    // An installation cookie cannot substitute for the first-party client's proof.
                    // Its inability to acquire a valid code is checked at authorize above.
                    Assert.That(excluded.Status, Is.EqualTo(401));
                    using var rejected = JsonDocument.Parse(await excluded.TextAsync());
                    Assert.That(rejected.RootElement.GetProperty("error").GetString(), Is.EqualTo("invalid_client"));
                    Assert.That(rejected.RootElement.TryGetProperty("access_token", out _), Is.False);
                }

                await using var state = new NpgsqlConnection(postgres.MaintenanceConnection);
                await state.OpenAsync();
                await using var command = new NpgsqlCommand("""
                    SELECT count(*) FROM "AuditEvents" WHERE "Action" = 'user.role.update'
                      AND "ActorId" = @actor AND "TargetId" = @target AND "Outcome" = 'SUCCEEDED'
                    """, state);
                command.Parameters.AddWithValue("actor", "53a74e90-6a7c-4965-a530-8d498c4e523e");
                command.Parameters.AddWithValue("target", userId.ToString());
                Assert.That((long)(await command.ExecuteScalarAsync())!, Is.EqualTo(2));
                phase = "assertions-complete";
            }
            catch (Exception error) when (error is PlaywrightException or TimeoutException)
            {
                failure = new InvalidOperationException("Access journey failed at " + phase + "; credential diagnostics disabled.");
                throw failure;
            }
            catch (Exception error) { failure = error; throw; }
            finally
            {
                await File.WriteAllTextAsync(Path.Combine(scope.ArtifactDirectory, "identity-progress.json"),
                    JsonSerializer.Serialize(new { phase, browserCategories, assertionsComplete = phase == "assertions-complete" }));
                var cleanup = new List<Exception>();
                foreach (var session in new[] { installation, ordinary })
                    try { await session.FinishAsync(failure is not null); }
                    catch (Exception) { cleanup.Add(new InvalidOperationException("Access browser cleanup/checks failed; credential diagnostics disabled.")); }
                if (cleanup.Count > 0) throw new AggregateException(failure is null ? cleanup : new[] { failure }.Concat(cleanup));
            }
        }
        catch (Exception error) when (error is PlaywrightException or TimeoutException)
        {
            failure = new InvalidOperationException("Access journey failed at " + phase + "; credential diagnostics disabled.");
            throw failure;
        }
        catch (Exception error) { failure = error; throw; }
        finally { await scope.FinishAsync(failure); }
    }

    private static async Task ExpectAsync(IPage page, string url, int status)
    {
        await using var response = await page.Context.APIRequest.GetAsync(url);
        Assert.That(response.Status, Is.EqualTo(status));
    }

    private static async Task BearerExpectAsync(IPage page, string url, string credential, int status)
    {
        await using var response = await page.Context.APIRequest.GetAsync(url,
            new() { Headers = new Dictionary<string, string> { ["Authorization"] = "Bearer " + credential } });
        Assert.That(response.Status, Is.EqualTo(status));
    }
}
