using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vivarium.Controller.Persistence;

namespace Vivarium.DefinitionProof;

[NonParallelizable, Category("US013ExplicitSelection")]
public sealed class ExplicitSelectionAcceptanceCases
{
    [Test]
    public async Task Explicit_selection_preserves_customized_producers_source_bytes_and_restart()
    {
        await using var installation = new R0AcceptanceInstallation(); await installation.StartAsync();
        await using var host = await installation.CreateCaseAsync(catalogued: true);
        using var administrator = await host.AdministratorAsync(); var csrf = await host.AntiforgeryAsync(administrator);
        await using var git = new OwnedGitSource(host.Root); await git.StartAsync();
        var firstRevision = await git.ChangeAsync();
        var settings = Settings.Replace("SOURCE_URL", JsonSerializer.Serialize(git.Url), StringComparison.Ordinal);
        await File.WriteAllTextAsync(Path.Combine(host.Definitions, ".vivarium/unbound/P/Settings.g.cs"), settings);
        await R0AcceptanceCases.CommitCaseAsync(host, "fixture: source-aware successful dependency reuse");
        Assert.That((await R0AcceptanceCases.ReconcileCaseAsync(administrator, csrf)).GetProperty("state").GetString(), Is.EqualTo("applied"));
        using var actor = await R0AcceptanceCases.RestrictedActorAsync(host, administrator, csrf);
        actor.BuildClient.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        await using var linux = new OwnedDockerAgent(host.Root, gitTools: true);
        await host.StartLinuxAgentAsync(linux, administrator, csrf);
        var repository = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "Vivarium.slnx"))) repository = repository.Parent;
        using var reference = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(repository!.FullName,
            "tests/Vivarium.Tests/TeamCity/Reference/explicit-selection-observations.json")));
        Assert.That(reference.RootElement.GetProperty("queuePosts").EnumerateArray().All(p => p.GetProperty("status").GetInt32() == 200), Is.True);
        Assert.That(reference.RootElement.GetProperty("cases")[0].GetProperty("newBuilds").GetInt32(), Is.EqualTo(1));
        using var producerRequest = await actor.BuildClient.PostAsJsonAsync("/api/v1/builds", new {
            configurationId = "P_Windows", secretEnvironment = new Dictionary<string,string> { ["DUMMY_INPUT"] = "synthetic-customized-producer" } });
        producerRequest.EnsureSuccessStatusCode();
        using var producerAccepted = JsonDocument.Parse(await producerRequest.Content.ReadAsStringAsync());
        var middle = await FinishedAsync(actor.BuildClient, producerAccepted.RootElement.GetProperty("id").GetInt64());
        var prepareId = middle.GetProperty("snapshot-dependencies").GetProperty("build")[0].GetProperty("id").GetInt64();
        var middleId = middle.GetProperty("id").GetInt64();
        // Repair only Release in a new desired-definition revision. The source
        // and both producers remain byte-identical to their collected plans.
        var repaired = settings.Replace("cp selected.txt release.txt", "echo definition-repair\\ncp selected.txt release.txt", StringComparison.Ordinal);
        Assert.That(repaired, Is.Not.EqualTo(settings));
        await File.WriteAllTextAsync(Path.Combine(host.Definitions, ".vivarium/unbound/P/Settings.g.cs"), repaired);
        await R0AcceptanceCases.CommitCaseAsync(host, "fixture: versioned Release-only definition repair");
        Assert.That((await R0AcceptanceCases.ReconcileCaseAsync(administrator, csrf)).GetProperty("state").GetString(), Is.EqualTo("applied"));
        await host.RestartAsync();
        var advancedRevision = await git.MetadataChangeAsync();
        Assert.That(advancedRevision, Is.Not.EqualTo(firstRevision));
        foreach(var native in new[] { false, true })
        {
            var dependencies = new[] { new { id = middleId, buildTypeId = "P_Windows" }, new { id = prepareId, buildTypeId = "P_Prepare" } };
            using var admission = native
                ? await actor.BuildClient.PostAsJsonAsync("/api/v1/builds", new { configurationId = "P_Release", selectedDependencies = dependencies, secretEnvironment = new Dictionary<string,string> { ["DUMMY_RELEASE_INPUT"] = "synthetic-release-input" } })
                : await actor.BuildClient.PostAsJsonAsync("/app/rest/buildQueue", new Dictionary<string,object> {
                    ["buildType"] = new { id = "P_Release" }, ["snapshot-dependencies"] = new { build = dependencies } });
            Assert.That(admission.StatusCode, Is.EqualTo(native ? HttpStatusCode.Accepted : HttpStatusCode.OK), await admission.Content.ReadAsStringAsync());
            using var accepted = JsonDocument.Parse(await admission.Content.ReadAsStringAsync());
            var release = await FinishedAsync(actor.BuildClient, accepted.RootElement.GetProperty("id").GetInt64());
            Assert.That(release.GetProperty("status").GetString(), Is.EqualTo("SUCCESS"));
            Assert.That(release.GetProperty("snapshot-dependencies").GetProperty("build")[0].GetProperty("id").GetInt64(), Is.EqualTo(middleId));
            Assert.That(release.GetProperty("revisions").GetProperty("revision")[0].GetProperty("version").GetString(), Is.EqualTo(advancedRevision));
            var bytes = await actor.BuildClient.GetByteArrayAsync("/app/rest/builds/id:" + release.GetProperty("id").GetInt64() + "/artifacts/files/release.txt");
            Assert.That(bytes, Is.EqualTo(Encoding.UTF8.GetBytes("coherent-reference-version\n")));
            await using var database = await host.Services.GetRequiredService<IDbContextFactory<VivariumDbContext>>().CreateDbContextAsync();
            Assert.That(await database.RuntimeBuildExecutions.CountAsync(), Is.EqualTo(native ? 4 : 3));
        }
        var missing = repaired.Replace("middle.txt => ci-input/middle", "required-absent.txt => ci-input/middle", StringComparison.Ordinal);
        await File.WriteAllTextAsync(Path.Combine(host.Definitions, ".vivarium/unbound/P/Settings.g.cs"), missing);
        await R0AcceptanceCases.CommitCaseAsync(host, "fixture: required artifact filename absent");
        await R0AcceptanceCases.ReconcileCaseAsync(administrator, csrf);
        foreach (var native in new[] { false, true })
        {
            await using var missingDatabase = await host.Services.GetRequiredService<IDbContextFactory<VivariumDbContext>>().CreateDbContextAsync();
            var beforeMissing = await missingDatabase.RuntimeBuilds.CountAsync();
            using var missingRequest = native
                ? await actor.BuildClient.PostAsJsonAsync("/api/v1/builds", new { configurationId = "P_Release", selectedDependencies = new[] { new { id = middleId } } })
                : await actor.BuildClient.PostAsJsonAsync("/app/rest/buildQueue", new Dictionary<string,object> {
                ["buildType"] = new { id = "P_Release" }, ["snapshot-dependencies"] = new { build = new[] { new { id = middleId } } } });
            Assert.That(missingRequest.StatusCode, Is.EqualTo(native ? HttpStatusCode.Accepted : HttpStatusCode.OK));
            using var missingBody = JsonDocument.Parse(await missingRequest.Content.ReadAsStringAsync());
            var missingResult = await FinishedAsync(actor.BuildClient, missingBody.RootElement.GetProperty("id").GetInt64());
            Assert.That(missingResult.GetProperty("status").GetString(), Is.EqualTo("FAILURE"));
            Assert.That(await missingDatabase.RuntimeBuilds.CountAsync(), Is.EqualTo(beforeMissing + 1));
            Assert.That(missingResult.GetProperty("failedToStart").GetBoolean(), Is.True);
            Assert.That(missingResult.GetProperty("artifact-dependencies").GetProperty("count").GetInt32(), Is.Zero);
            Assert.That(missingResult.GetProperty("snapshot-dependencies").GetProperty("build")[0].GetProperty("id").GetInt64(), Is.EqualTo(middleId));
        }
        await File.WriteAllTextAsync(Path.Combine(host.Definitions, ".vivarium/unbound/P/Settings.g.cs"), repaired);
        await R0AcceptanceCases.CommitCaseAsync(host, "fixture: restore required artifact rule");
        await R0AcceptanceCases.ReconcileCaseAsync(administrator, csrf);
        using var viewer = await R0AcceptanceCases.ProjectActorAsync(host, administrator, csrf,
            "OWNED_SELECTION_VIEW", ["view_project", "view_build_configuration_settings"]);
        using var deniedSelection = await viewer.BuildClient.PostAsJsonAsync("/app/rest/buildQueue", new Dictionary<string,object> {
            ["buildType"] = new { id = "P_Release" }, ["snapshot-dependencies"] = new { build = new[] { new { id = middleId } } } });
        Assert.That(deniedSelection.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        using var freshPrepareRequest = await actor.BuildClient.PostAsJsonAsync("/api/v1/builds", new {
            configurationId = "P_Prepare", secretEnvironment = new Dictionary<string,string> { ["DUMMY_INPUT"] = "new-customized-producer" } });
        freshPrepareRequest.EnsureSuccessStatusCode();
        using var freshPrepareBody = JsonDocument.Parse(await freshPrepareRequest.Content.ReadAsStringAsync());
        var freshPrepare = await FinishedAsync(actor.BuildClient, freshPrepareBody.RootElement.GetProperty("id").GetInt64());
        var conflicts = new[] { new { id = middleId }, new { id = freshPrepare.GetProperty("id").GetInt64() } };
        foreach (var native in new[] { false, true })
        {
            using var conflict = native
                ? await actor.BuildClient.PostAsJsonAsync("/api/v1/builds", new { configurationId = "P_Release", selectedDependencies = conflicts })
                : await actor.BuildClient.PostAsJsonAsync("/app/rest/buildQueue", new Dictionary<string,object> {
                    ["buildType"] = new { id = "P_Release" }, ["snapshot-dependencies"] = new { build = conflicts } });
            Assert.That(conflict.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), await conflict.Content.ReadAsStringAsync());
        }
        await linux.DisposeAsync();
        using var queued = await actor.BuildClient.PostAsJsonAsync("/api/v1/builds", new {
            configurationId = "P_Release", selectedDependencies = new[] { new { id = middleId } } });
        queued.EnsureSuccessStatusCode();
        using var queuedBody = JsonDocument.Parse(await queued.Content.ReadAsStringAsync());
        var queuedId = queuedBody.RootElement.GetProperty("id").GetInt64();
        await host.RestartAsync();
        var retained = await ReadAsync(actor.BuildClient, queuedId);
        Assert.That(retained.GetProperty("snapshot-dependencies").GetProperty("build")[0].GetProperty("id").GetInt64(), Is.EqualTo(middleId));
        using var cancel = await actor.BuildClient.PutAsJsonAsync($"/api/v1/builds/{queuedId}/cancellation", new { mode = "graceful" });
        Assert.That(cancel.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        Assert.That((await FinishedAsync(actor.BuildClient, queuedId)).GetProperty("status").GetString(), Is.Not.EqualTo("SUCCESS"));
        await R0AcceptanceInstallation.Case.MutateAsync(administrator, csrf, HttpMethod.Delete,
            $"/api/v1/users/{actor.UserId}/roles/OWNED_SIX/projects/P", null, HttpStatusCode.NoContent);
        using var denied = await actor.BuildClient.PostAsJsonAsync("/api/v1/builds", new {
            configurationId = "P_Release", selectedDependencies = new[] { new { id = middleId } } });
        Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.NotFound)); // Existing native hiding; no compatible denial oracle.

    }

    private static async Task<JsonElement> FinishedAsync(HttpClient api, long id)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        while(true) { var value = await ReadAsync(api,id,deadline.Token); if(value.GetProperty("state").GetString()=="finished") return value; await Task.Delay(100,deadline.Token); }
    }

    private static async Task<JsonElement> ReadAsync(HttpClient api, long id, CancellationToken token = default)
    { using var value = JsonDocument.Parse(await api.GetStringAsync("/app/rest/builds/id:" + id, token)); return value.RootElement.Clone(); }

    internal const string Settings = """
        using Vivarium.Definitions;
        public static class Managed {
          public static Settings Declare() => new("P", new("P_Git", SOURCE_URL, "refs/heads/main", "+:.\n-:.repos"), [], [
            new("P_Prepare", "Prepare", [], [new("prepare", "Prepare", "simpleRunner", [new("script.content", "#!/bin/sh\nset -eu\ngit rev-parse HEAD\ncat selected.txt > prepare.txt\necho \"##teamcity[publishArtifacts 'prepare.txt']\"", null)])],
              [new("system.os.family", "linux")], [], [], 2, [], "ON_AGENT"),
            new("P_Windows", "Intermediate", [], [new("middle", "Middle", "simpleRunner", [new("script.content", "#!/bin/sh\nset -eu\ngit rev-parse HEAD\ncmp selected.txt ci-input/prepare/prepare.txt\ncp selected.txt middle.txt\necho \"##teamcity[publishArtifacts 'middle.txt']\"", null)])],
              [new("system.os.family", "linux")], [new("P_Prepare", "CANCEL", "CANCEL", true, true)],
              [new("P_Prepare", "sameChainOrLastFinished", "prepare.txt => ci-input/prepare", true)], 2, [], "ON_AGENT"),
            new("P_Release", "Release", [], [new("release", "Release", "simpleRunner", [new("script.content", "#!/bin/sh\nset -eu\ngit rev-parse HEAD\ncmp selected.txt ci-input/middle/middle.txt\ncp selected.txt release.txt\necho \"##teamcity[publishArtifacts 'release.txt']\"", null)])],
              [new("system.os.family", "linux")], [new("P_Windows", "CANCEL", "CANCEL", true, true)],
              [new("P_Windows", "sameChainOrLastFinished", "middle.txt => ci-input/middle", true)], 2, [], "ON_AGENT")
          ]);
        }
        """;
}
