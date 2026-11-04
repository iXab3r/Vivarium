using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Vivarium.Controller.Builds.Runtime;
using Vivarium.Controller.Identity;

namespace Vivarium.DefinitionProof;

public sealed partial class ActualT3AcceptanceCases
{
    // Continue observation of the one successful CLI execution after a fixture query failed.
    // No admission, Agent, Git mutation or GitHub write is performed by this continuation.
    [Test, Category("US013ActualT3Readback")]
    public async Task Retained_successful_CLI_chain_has_exact_remote_bytes_and_ordinary_readbacks()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Retained credential requires Windows DPAPI.");
        var repository = Repository();
        var caseRoot = Path.Combine(repository, "artifacts/w/716ca9b5/r0_case_6b108f5952f7");
        var output = Path.Combine(repository, "artifacts/us013-t3-acceptance/runs/f7139347660a4e8cb074cac8aac3fa9b");
        await using var host = await R0AcceptanceInstallation.ReopenRetainedAsync(Path.Combine(caseRoot, "recovery.private.json"), services =>
            services.Remove(services.Single(s => s.ServiceType == typeof(IHostedService) && s.ImplementationType == typeof(RuntimeBuildWorker))));
        using var actor = host.Client();
        var credentialFile = Directory.GetFiles(Path.Combine(caseRoot, "actual-t3-cli/cli-state/roaming/teamcity-cli/owned-r0/secrets"), "*.dpapi").Single();
        var bytes = ProtectedData.Unprotect(await File.ReadAllBytesAsync(credentialFile), Encoding.UTF8.GetBytes("[\"ai-cli-factory:teamcity-cli\",\"owned-r0:token\"]"), DataProtectionScope.CurrentUser);
        try { actor.DefaultRequestHeaders.Authorization = new("Bearer", Encoding.UTF8.GetString(bytes)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
        actor.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        using var cli = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(output, "cli-result.json")));
        Assert.That(cli.RootElement.GetProperty("outcome").GetString(), Is.EqualTo("succeeded"));
        var root = cli.RootElement.GetProperty("build").GetProperty("id").GetInt64();
        await using var database = await DatabaseAsync(host);
        var builds = await database.RuntimeBuilds.AsNoTracking().OrderBy(b => b.Id).ToArrayAsync();
        Assert.That(builds.Select(b => b.Id), Is.EqualTo(new long[] { 1, 2, 3 }));
        var executions = await database.RuntimeBuildExecutions.AsNoTracking().ToArrayAsync();
        Assert.That(executions, Has.Length.EqualTo(3));
        var windows = executions.Single(e => e.BuildId == 2).AgentId;
        var linux = executions.Single(e => e.BuildId == 1).AgentId;
        using var identity = JsonDocument.Parse(await actor.GetStringAsync("/api/v1/users/current"));
        Assert.That(identity.RootElement.GetProperty("id").GetGuid(), Is.EqualTo(builds[0].ActorId));
        var cookies = new CookieContainer();
        using (var scope = host.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<VivariumUser>>();
            var user = (await users.FindByIdAsync(builds[0].ActorId.ToString()))!;
            var accessor = scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
            var previous = accessor.HttpContext;
            var origin = host.Origin;
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
                    cookies.Add(origin, new Cookie(header.Name.ToString(), header.Value.ToString(), header.Path.ToString()) { HttpOnly = header.HttpOnly, Secure = header.Secure });
            }
            finally { accessor.HttpContext = previous; }
        }
        var secret = (await File.ReadAllTextAsync(Environment.GetEnvironmentVariable("VIVARIUM_US013_TOKEN_FILE")!)).Trim();
        using var github = GitHub(secret);
        await InspectActualT3Async(host, actor, async context =>
        {
            var origin = host.Origin;
            await context.AddCookiesAsync(cookies.GetCookies(origin).Cast<Cookie>().Select(cookie => new Microsoft.Playwright.Cookie {
                Name = cookie.Name, Value = cookie.Value, Domain = origin.Host, Path = cookie.Path,
                Secure = cookie.Secure, HttpOnly = cookie.HttpOnly, SameSite = Microsoft.Playwright.SameSiteAttribute.Lax }));
        }, github, secret, output, "3ae8f481bc7c118747c8d01f0ec14f3f18e8b6ec", windows, linux, root);
        Assert.That(await database.RuntimeBuildExecutions.CountAsync(), Is.EqualTo(3));
    }
}
