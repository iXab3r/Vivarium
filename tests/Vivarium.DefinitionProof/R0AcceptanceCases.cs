using System.Net;
using System.Reflection;
using System.Text.Json;
using EyeAuras.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vivarium.Controller.Builds.Runtime;
using Vivarium.Controller.Hosting;

namespace Vivarium.DefinitionProof;

[NonParallelizable]
public sealed partial class R0AcceptanceCases
{
    private R0AcceptanceInstallation installation = null!;

    [OneTimeSetUp]
    public async Task Prepare()
    {
        installation = new(); await installation.StartAsync();
    }

    [OneTimeTearDown]
    public async Task Cleanup() => await installation.DisposeAsync();

    [Test]
    public async Task Host_discovery_uses_the_published_CLI_and_reports_this_started_Vivarium_release()
    {
        var before = DateTimeOffset.UtcNow;
        await using var host = await installation.CreateCaseAsync();
        using var actor = new ScopedKeyScenario(host.Origin, host.Thumbprint, host.Root, host.Certificate);
        var name = "r0-discovery-" + Guid.NewGuid().ToString("N")[..12];
        await actor.InitializeUserAsync(host.Services, name);
        await actor.CreateKeyAsync();
        var credential = actor.BuildClient.DefaultRequestHeaders.Authorization!.Parameter!;
        var cli = new UnchangedCliScenario(host.Root, host.Origin, host.Certificate);
        await cli.AuthenticateAsync(credential, name);
        var server = await cli.RunAsync(credential, 0, "-p", "owned-r0", "server", "status");
        // The external build metadata is the oracle, independently of the endpoint's version helper.
        var informational = typeof(VivariumRuntime).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        Assert.That(server.GetProperty("version").GetString(), Is.EqualTo(informational.Split('+', 2)[0]));
        Assert.That(server.GetProperty("buildNumber").GetString(), Is.EqualTo(informational));
        Assert.That(server.GetProperty("webUrl").GetString(), Is.EqualTo(host.Origin.AbsoluteUri.TrimEnd('/')));
        Assert.That(server.GetProperty("role").GetString(), Is.EqualTo("main"));
        Assert.That(server.EnumerateObject().Select(p => p.Name), Is.EquivalentTo(new[]
            { "version", "versionMajor", "versionMinor", "buildNumber", "startTime", "currentTime", "role", "webUrl" }));
        var start = server.GetProperty("startTime").GetString()!;
        var current = server.GetProperty("currentTime").GetString()!;
        var after = DateTimeOffset.UtcNow;
        string Stamp(DateTimeOffset value) => value.ToString("yyyyMMdd'T'HHmmss'+0000'", System.Globalization.CultureInfo.InvariantCulture);
        Assert.That(start, Does.Match("^[0-9]{8}T[0-9]{6}[+]0000$"));
        Assert.That(string.CompareOrdinal(start, Stamp(before)), Is.GreaterThanOrEqualTo(0));
        Assert.That(string.CompareOrdinal(start, current), Is.LessThanOrEqualTo(0));
        Assert.That(string.CompareOrdinal(current, Stamp(after)), Is.LessThanOrEqualTo(0));
        actor.BuildClient.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        using var metadata = JsonDocument.Parse(await actor.BuildClient.GetStringAsync("/app/rest/server"));
        Assert.That(metadata.RootElement.GetProperty("product").GetString(), Is.EqualTo("Vivarium"));
        Assert.That(metadata.RootElement.GetProperty("compatibilityProfile").GetString(), Is.EqualTo("TeamCity 2026.1.3 / build 222742"));
    }

    [Test]
    public async Task API_challenges_preserve_stock_browser_redirects_and_deny_before_build_or_byte_reads()
    {
        var byteReads = new ByteReadObserver();
        await using var host = await installation.CreateCaseAsync(catalogued: true, configureServices: services =>
            services.Replace(ServiceDescriptor.Singleton(provider => new RuntimeBuildBytes(provider.GetRequiredService<IObjectStorage>(), byteReads))));
        using var administrator = await host.AdministratorAsync(); var csrf = await host.AntiforgeryAsync(administrator);
        await host.StartWindowsAgentAsync(administrator, csrf);
        using var actor = new ScopedKeyScenario(host.Origin, host.Thumbprint, host.Root, host.Certificate);
        await actor.InitializeUserAsync(host.Services, "r0-challenge-" + Guid.NewGuid().ToString("N")[..12]);
        await R0AcceptanceInstallation.Case.MutateAsync(administrator, csrf, HttpMethod.Put,
            $"/api/v1/users/{actor.UserId}/roles/PROJECT_DEVELOPER/projects/P", null, HttpStatusCode.NoContent);
        await actor.CreateKeyAsync();
        using var admission = await actor.BuildClient.PostAsync("/api/v1/builds", new StringContent("{\"configurationId\":\"P_LocalSmoke\"}", System.Text.Encoding.UTF8, "application/json"));
        Assert.That(admission.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        using var submitted = JsonDocument.Parse(await admission.Content.ReadAsStringAsync());
        var id = submitted.RootElement.GetProperty("id").GetInt64();
        using (var bound = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
            while (true)
            {
                using var build = JsonDocument.Parse(await actor.BuildClient.GetStringAsync($"/api/v1/builds/{id}", bound.Token));
                if (build.RootElement.GetProperty("state").GetString() == "finished")
                { Assert.That(build.RootElement.GetProperty("status").GetString(), Is.EqualTo("SUCCESS")); break; }
                await Task.Delay(100, bound.Token);
            }
        using var artifacts = JsonDocument.Parse(await actor.BuildClient.GetStringAsync($"/api/v1/builds/{id}/artifacts"));
        var artifact = artifacts.RootElement.GetProperty("items")[0].GetProperty("href").GetString()!;
        Assert.That(await actor.BuildClient.GetByteArrayAsync(artifact), Is.EqualTo("owned-r0-smoke\n"u8.ToArray()));
        using var output = JsonDocument.Parse(await actor.BuildClient.GetStringAsync($"/api/v1/builds/{id}/output"));
        var outputHref = output.RootElement.GetProperty("items").EnumerateArray().First(e => e.GetProperty("kind").GetString() == "data")
            .GetProperty("content").GetProperty("href").GetString()!;
        Assert.That(await actor.BuildClient.GetStringAsync(outputHref), Does.Contain("owned-r0-stdout\n"));
        var paths = new[] { $"/app/rest/builds/id:{id}", $"/app/rest/builds/id:{id}/artifacts/files/smoke.txt",
            $"/api/v1/builds/{id}", $"/api/v1/builds/{id}/output", outputHref, artifact };
        using var anonymous = host.Client();
        using var invalid = host.Client(); invalid.DefaultRequestHeaders.Authorization = new("Bearer", "owned-invalid-credential");
        using var installationBearer = host.InstallationPasswordAsBearerClient();
        var readsBeforeDenial = byteReads.Count;
        Assert.That(readsBeforeDenial, Is.GreaterThanOrEqualTo(2), "The positive controls must actually read private bytes.");
        foreach (var path in paths)
        {
            foreach (var client in new[] { anonymous, invalid, installationBearer, administrator })
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, path);
                // A valid browser cookie must not rescue an explicitly invalid bearer.
                if (client == administrator) request.Headers.Authorization = new("Bearer", "owned-invalid-credential");
                using var rejected = await client.SendAsync(request);
                Assert.That(rejected.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized), path);
                Assert.That(rejected.Headers.Location, Is.Null);
                if (path.StartsWith("/app/rest", StringComparison.Ordinal))
                {
                    Assert.That(rejected.Content.Headers.ContentType?.MediaType, Is.EqualTo("text/plain"));
                    Assert.That(await rejected.Content.ReadAsStringAsync(), Is.EqualTo("Could not authenticate with provided token\n"));
                }
                else
                {
                    Assert.That(rejected.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/problem+json"));
                    Assert.That(rejected.Headers.WwwAuthenticate.Any(h => h.Scheme == "Bearer"), Is.True);
                    using var problem = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync());
                    Assert.That(problem.RootElement.GetProperty("code").GetString(), Is.EqualTo("authentication_required"));
                }
            }
        }
        Assert.That(byteReads.Count, Is.EqualTo(readsBeforeDenial), "Authentication denial must precede private Storage reads.");
        using var stillAllowed = await actor.BuildClient.GetAsync($"/api/v1/builds/{id}");
        Assert.That(stillAllowed.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        using var cookieAllowed = await administrator.GetAsync("/api/v1/users/current");
        Assert.That(cookieAllowed.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        using var browser = await anonymous.GetAsync("/Identity/Account/Manage/Tokens");
        Assert.That(browser.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
        Assert.That(browser.Headers.Location?.OriginalString, Does.Contain("/Identity/Account/Login"));
    }

    private sealed class ByteReadObserver : DelegatingHandler
    {
        private int count;
        internal int Count => Volatile.Read(ref count);
        internal ByteReadObserver() : base(new HttpClientHandler { AllowAutoRedirect = false }) { }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get) Interlocked.Increment(ref count);
            return base.SendAsync(request, cancellationToken);
        }
    }

    [Test]
    public async Task Case_databases_roots_and_stock_credentials_are_isolated()
    {
        string credential; string firstRoot;
        await using (var first = await installation.CreateCaseAsync())
        {
            firstRoot = first.Root;
            using var actor = new ScopedKeyScenario(first.Origin, first.Thumbprint, first.Root, first.Certificate);
            await actor.InitializeUserAsync(first.Services, "r0-first-" + Guid.NewGuid().ToString("N")[..12]);
            await actor.CreateKeyAsync(); credential = actor.BuildClient.DefaultRequestHeaders.Authorization!.Parameter!;
            using var allowed = await actor.BuildClient.GetAsync("/api/v1/users/current");
            Assert.That(allowed.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        }
        await using var second = await installation.CreateCaseAsync();
        Assert.That(second.Root, Is.Not.EqualTo(firstRoot));
        using var invalid = second.Client(); invalid.DefaultRequestHeaders.Authorization = new("Bearer", credential);
        using var rejected = await invalid.GetAsync("/api/v1/users/current");
        Assert.That(rejected.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        using var next = new ScopedKeyScenario(second.Origin, second.Thumbprint, second.Root, second.Certificate);
        await next.InitializeUserAsync(second.Services, "r0-second-" + Guid.NewGuid().ToString("N")[..12]);
        await next.CreateKeyAsync();
        using var positive = await next.BuildClient.GetAsync("/api/v1/users/current");
        Assert.That(positive.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }
}
