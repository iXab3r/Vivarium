using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Vivarium.Controller.Builds.Runtime;
using Vivarium.Controller.Persistence;

namespace Vivarium.DefinitionProof;

[NonParallelizable, Category("US013RunInputs")]
public sealed class RunInputAcceptanceCases
{
    private R0AcceptanceInstallation installation = null!;
    [OneTimeSetUp] public async Task Prepare() { installation = new(); await installation.StartAsync(); }
    [OneTimeTearDown] public async Task Cleanup() => await installation.DisposeAsync();

    [Test]
    public async Task Unchanged_CLI_consumes_one_private_chain_input_with_targeted_masks_and_restart_recovery()
    {
        await using var host = await installation.CreateCaseAsync(catalogued: true);
        using var administrator = await host.AdministratorAsync(); var csrf = await host.AntiforgeryAsync(administrator);
        await ConfigureAsync(host, administrator, csrf);
        using var actor = await R0AcceptanceCases.RestrictedActorAsync(host, administrator, csrf);
        await using var linux = new OwnedDockerAgent(host.Root);
        await host.StartLinuxAgentAsync(linux, administrator, csrf);
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var staged = await StageAsync(actor.BuildClient, secret);
        var reference = staged.GetProperty("reference").GetString()!;
        var metadataPath = "/api/v1/build-run-inputs/" + reference[4..];
        await using (var database = await DatabaseAsync(host))
        {
            var pending = await database.RuntimeBuildRunInputs.SingleAsync();
            Assert.That(pending.ProtectedInputs.AsSpan().IndexOf(Encoding.UTF8.GetBytes(secret)), Is.EqualTo(-1));
            Assert.That(pending.State, Is.EqualTo("PENDING"));
        }
        await host.RestartAsync();
        Assert.That(await actor.BuildClient.GetStringAsync(metadataPath), Does.Not.Contain(secret));
        var cliRoot = Path.Combine(host.Root, "prepared-input-cli"); Directory.CreateDirectory(cliRoot);
        var cli = new UnchangedCliScenario(cliRoot, host.Origin, host.Certificate);
        var credential = actor.BuildClient.DefaultRequestHeaders.Authorization!.Parameter!;
        string userName;
        await using (var database = await DatabaseAsync(host)) userName = (await database.Users.SingleAsync(u => u.Id == actor.UserId)).UserName!;
        await cli.AuthenticateAsync(credential, userName);
        var id = await cli.RunWholeChainAsync(credential, reference);
        // A lost admission response is recoverable by metadata; retry cannot create another graph.
        await ExpectSubmissionAsync(actor.BuildClient, reference, "P_Release", HttpStatusCode.Conflict);
        await host.RestartAsync();
        using var recovered = JsonDocument.Parse(await actor.BuildClient.GetStringAsync(metadataPath));
        Assert.That(recovered.RootElement.GetProperty("acceptedBuildId").GetInt64(), Is.EqualTo(id));
        Assert.That(recovered.RootElement.GetProperty("state").GetString(), Is.EqualTo("consumed"));
        await using (var database = await DatabaseAsync(host))
        {
            var consumed = await database.RuntimeBuildRunInputs.SingleAsync();
            Assert.That(consumed.ProtectedInputs, Is.Empty);
            Assert.That(await database.RuntimeBuilds.CountAsync(), Is.EqualTo(3));
            var codec = host.Services.GetRequiredService<RuntimeBuildSecrets>();
            foreach (var input in await database.RuntimeBuildInputs.ToArrayAsync())
            {
                var type = await database.CatalogBuildTypes.SingleAsync(t => t.Id == input.BuildTypeId);
                Assert.That(input.OverridesJson, Does.Not.Contain(reference).And.Not.Contain(secret));
                var environment = codec.ReadEnvironment(input.BuildId, input.ProtectedEnvironment);
                Assert.That(environment.ContainsKey("US013_SECRET"), Is.EqualTo(type.ExternalId != "P_Windows"));
                var execution = await database.RuntimeBuildExecutions.SingleAsync(e => e.BuildId == input.BuildId);
                var assignment = codec.ReadAssignment(input.BuildId, execution.Assignment, execution.AssignmentProtectionVersion);
                Assert.That(assignment.OutputSecrets.Count, Is.EqualTo(type.ExternalId == "P_Windows" ? 0 : 1));
                Assert.That(execution.Assignment.AsSpan().IndexOf(Encoding.UTF8.GetBytes(secret)), Is.EqualTo(-1));
                using var output = JsonDocument.Parse(await actor.BuildClient.GetStringAsync($"/api/v1/builds/{input.BuildId}/output"));
                using var bytes = new MemoryStream();
                foreach (var item in output.RootElement.GetProperty("items").EnumerateArray())
                    await bytes.WriteAsync(await actor.BuildClient.GetByteArrayAsync(item.GetProperty("content").GetProperty("href").GetString()));
                var text = Encoding.UTF8.GetString(bytes.ToArray());
                Assert.That(text, Does.Not.Contain(secret));
                Assert.That(text, type.ExternalId == "P_Windows" ? Does.Contain("no-secret") : Does.Contain("[REDACTED]"));
            }
            Assert.That(JsonSerializer.Serialize(await database.AuditEvents.ToArrayAsync()), Does.Not.Contain(secret));
        }
        foreach (var file in Directory.EnumerateFiles(host.Definitions, "*.cs", SearchOption.AllDirectories))
            Assert.That(await File.ReadAllTextAsync(file), Does.Not.Contain(secret));
    }

    [Test]
    public async Task Foreign_wrong_root_stale_expired_and_withdrawn_inputs_cannot_admit_builds()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var host = await installation.CreateCaseAsync(catalogued: true,
            configureServices: services => services.Replace(ServiceDescriptor.Singleton<TimeProvider>(clock)));
        using var administrator = await host.AdministratorAsync(); var csrf = await host.AntiforgeryAsync(administrator);
        await ConfigureAsync(host, administrator, csrf);
        using var actor = await R0AcceptanceCases.RestrictedActorAsync(host, administrator, csrf);
        using var other = await R0AcceptanceCases.RestrictedActorAsync(host, administrator, csrf);
        var reference = (await StageAsync(actor.BuildClient, "synthetic-owned-input")).GetProperty("reference").GetString()!;
        var path = "/api/v1/build-run-inputs/" + reference[4..];
        using (var denied = await other.BuildClient.GetAsync(path)) Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        await ExpectSubmissionAsync(other.BuildClient, reference, "P_Release", HttpStatusCode.NotFound);
        await ExpectSubmissionAsync(actor.BuildClient, reference, "P_Prepare", HttpStatusCode.NotFound);
        using (var invalid = await actor.BuildClient.PostAsJsonAsync("/api/v1/build-run-inputs", new { configurationId = "P_Prepare",
            environments = new Dictionary<string, object> { ["P_Release"] = new { A = "synthetic" } } }))
            Assert.That(invalid.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        await ConfigureAsync(host, administrator, csrf, "Changed Release");
        await ExpectSubmissionAsync(actor.BuildClient, reference, "P_Release", HttpStatusCode.Conflict);
        var expiring = (await StageAsync(actor.BuildClient, "synthetic-expiring-input")).GetProperty("reference").GetString()!;
        clock.Advance(TimeSpan.FromMinutes(16));
        await ExpectSubmissionAsync(actor.BuildClient, expiring, "P_Release", HttpStatusCode.Gone);
        await host.Services.GetRequiredService<RuntimeBuildRunInputs>().ExpireAsync(CancellationToken.None);
        await using (var database = await DatabaseAsync(host))
        {
            Assert.That(await database.RuntimeBuilds.CountAsync(), Is.Zero);
            Assert.That(await database.RuntimeBuildCounters.AnyAsync(c => c.NextValue != 1), Is.False);
            Assert.That((await database.RuntimeBuildRunInputs.ToArrayAsync()).All(i => i.ProtectedInputs.Length == 0 && i.State == "EXPIRED"), Is.True);
        }
        var withdrawn = (await StageAsync(actor.BuildClient, "synthetic-withdrawn-input")).GetProperty("reference").GetString()!;
        await R0AcceptanceInstallation.Case.MutateAsync(administrator, csrf, HttpMethod.Delete,
            $"/api/v1/users/{actor.UserId}/roles/OWNED_SIX/projects/P", null, HttpStatusCode.NoContent);
        using (var denied = await actor.BuildClient.GetAsync("/api/v1/build-run-inputs/" + withdrawn[4..]))
            Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        await ExpectSubmissionAsync(actor.BuildClient, withdrawn, "P_Release", HttpStatusCode.NotFound);
        await using var final = await DatabaseAsync(host);
        Assert.That(await final.RuntimeBuilds.CountAsync(), Is.Zero);
        // Bound encrypted pending material, then race the same one-use input through real admissions.
        string? raced = null;
        for (var index = 0; index < 16; index++)
            raced = (await StageAsync(other.BuildClient, "synthetic-quota-input-" + index)).GetProperty("reference").GetString();
        using (var limited = await other.BuildClient.PostAsJsonAsync("/api/v1/build-run-inputs", new { configurationId = "P_Release",
            environments = new Dictionary<string, Dictionary<string, string>> { ["P_Release"] = new() { ["A"] = "synthetic" } } }))
            Assert.That(limited.StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests));
        var submissions = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => other.BuildClient.PostAsJsonAsync("/api/v1/builds",
            new { configurationId = "P_Release", parameters = new Dictionary<string, string> { [RuntimeBuildRunInputs.Parameter] = raced! } })));
        try { Assert.That(submissions.Select(r => r.StatusCode), Is.EquivalentTo(new[] { HttpStatusCode.Accepted, HttpStatusCode.Conflict })); }
        finally { foreach (var response in submissions) response.Dispose(); }
        Assert.That(await final.RuntimeBuilds.CountAsync(), Is.EqualTo(3), "One admission transaction creates exactly one three-member graph.");
    }

    private static async Task<JsonElement> StageAsync(HttpClient api, string secret)
    {
        using var response = await api.PostAsJsonAsync("/api/v1/build-run-inputs", new { configurationId = "P_Release",
            environments = new Dictionary<string, Dictionary<string, string>> {
                ["P_Prepare"] = new() { ["US013_SECRET"] = secret }, ["P_Release"] = new() { ["US013_SECRET"] = secret } } });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        var text = await response.Content.ReadAsStringAsync(); Assert.That(text, Does.Not.Contain(secret));
        using var json = JsonDocument.Parse(text); return json.RootElement.Clone();
    }
    private static async Task ExpectSubmissionAsync(HttpClient api, string reference, string configurationId, HttpStatusCode expected)
    {
        using var response = await api.PostAsJsonAsync("/api/v1/builds", new { configurationId,
            parameters = new Dictionary<string, string> { [RuntimeBuildRunInputs.Parameter] = reference } });
        Assert.That(response.StatusCode, Is.EqualTo(expected));
    }
    private static Task<VivariumDbContext> DatabaseAsync(R0AcceptanceInstallation.Case host) =>
        host.Services.GetRequiredService<IDbContextFactory<VivariumDbContext>>().CreateDbContextAsync();
    private static async Task ConfigureAsync(R0AcceptanceInstallation.Case host, HttpClient administrator, string csrf, string release = "Release")
    {
        await File.WriteAllTextAsync(Path.Combine(host.Definitions, ".vivarium/unbound/P/Settings.g.cs"), Settings.Replace("RELEASE_NAME", release, StringComparison.Ordinal));
        await R0AcceptanceCases.CommitCaseAsync(host, "fixture: one-use targeted chain execution input");
        Assert.That((await R0AcceptanceCases.ReconcileCaseAsync(administrator, csrf)).GetProperty("state").GetString(), Is.EqualTo("applied"));
    }
    private const string Settings = """
        using Vivarium.Definitions;
        public static class Managed {
          public static Settings Declare() => new("P", null, [], [
            new("P_Prepare", "Prepare", [], [new("prepare", "Prepare", "simpleRunner", [new("script.content", "set -eu\ntest -n \"$US013_SECRET\"\nprintf '%s\\n' \"$US013_SECRET\"", null)])],
              [new("system.os.family", "linux")], [], [], 2, []),
            new("P_Windows", "Intermediate", [], [new("middle", "Middle", "simpleRunner", [new("script.content", "set -eu\ntest -z \"${US013_SECRET:-}\"\necho no-secret", null)])],
              [new("system.os.family", "linux")], [new("P_Prepare", "CANCEL", "CANCEL")], [], 2, []),
            new("P_Release", "RELEASE_NAME", [], [new("release", "Release", "simpleRunner", [new("script.content", "set -eu\ntest -n \"$US013_SECRET\"\nprintf '%s\\n' \"$US013_SECRET\"\nprintf 'coherent-reference-version\\n' > release.txt\necho \"##teamcity[publishArtifacts 'release.txt']\"", null)])],
              [new("system.os.family", "linux")], [new("P_Windows", "CANCEL", "CANCEL")], [], 2, [])
          ]);
        }
        """;
}
