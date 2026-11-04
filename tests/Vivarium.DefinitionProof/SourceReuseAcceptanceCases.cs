using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vivarium.Controller.Persistence;

namespace Vivarium.DefinitionProof;

[NonParallelizable, Category("US013SourceReuse")]
public sealed class SourceReuseAcceptanceCases
{
    [Test]
    public async Task Source_reuse_preserves_same_revision_producers_and_rebuilds_after_a_real_commit()
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
            "tests/Vivarium.Tests/TeamCity/Reference/source-reuse-observations.json")));
        Assert.That(reference.RootElement.GetProperty("complete").GetBoolean(), Is.True);
        Assert.That(reference.RootElement.GetProperty("ownedResourcesRemoved").GetBoolean(), Is.True);
        var observed = new List<JsonElement[]>();
        foreach (var phase in Enumerable.Range(0, 3))
        {
            var revision = phase == 2 ? await git.ChangeAsync("coherent-reference-version-changed\n") : firstRevision;
            using var admission = await actor.BuildClient.PostAsJsonAsync("/app/rest/buildQueue", new { buildType = new { id = "P_Release" } });
            Assert.That(admission.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            using var accepted = JsonDocument.Parse(await admission.Content.ReadAsStringAsync());
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var release = await ReadAsync(actor.BuildClient, accepted.RootElement.GetProperty("id").GetInt64(), deadline.Token);
            while (release.GetProperty("state").GetString() != "finished")
            { await Task.Delay(100, deadline.Token); release = await ReadAsync(actor.BuildClient, release.GetProperty("id").GetInt64(), deadline.Token); }
            var middle = await ReadAsync(actor.BuildClient, release.GetProperty("snapshot-dependencies").GetProperty("build")[0].GetProperty("id").GetInt64());
            var prepare = await ReadAsync(actor.BuildClient, middle.GetProperty("snapshot-dependencies").GetProperty("build")[0].GetProperty("id").GetInt64());
            var actual = new[] { prepare, middle, release }; observed.Add(actual);
            var oracle = reference.RootElement.GetProperty("cases")[phase].GetProperty("terminalObservation");
            foreach (var index in Enumerable.Range(0, 3))
            {
                foreach (var name in new[] { "state", "status", "statusText" })
                    Assert.That(actual[index].GetProperty(name).GetRawText(), Is.EqualTo(oracle[index].GetProperty(name).GetRawText()), name);
                Assert.That(actual[index].GetProperty("revisions").GetProperty("revision")[0].GetProperty("version").GetString(), Is.EqualTo(revision));
            }
            var bytes = await actor.BuildClient.GetByteArrayAsync("/app/rest/builds/id:" + release.GetProperty("id").GetInt64() + "/artifacts/files/release.txt");
            Assert.That(bytes, Is.EqualTo(Encoding.UTF8.GetBytes(phase == 2 ? "coherent-reference-version-changed\n" : "coherent-reference-version\n")));
            await using var database = await host.Services.GetRequiredService<IDbContextFactory<VivariumDbContext>>().CreateDbContextAsync();
            Assert.That(await database.RuntimeBuildExecutions.CountAsync(), Is.EqualTo(new[] { 3, 4, 7 }[phase]));
            if (phase == 0) await host.RestartAsync();
        }
        static long[] Ids(JsonElement[] builds) => builds.Select(b => b.GetProperty("id").GetInt64()).ToArray();
        Assert.That(Ids(observed[1])[..2], Is.EqualTo(Ids(observed[0])[..2]));
        Assert.That(Ids(observed[1])[2], Is.Not.EqualTo(Ids(observed[0])[2]));
        Assert.That(Ids(observed[2]).Intersect(Ids(observed[0]).Concat(Ids(observed[1]))), Is.Empty);
    }

    private static async Task<JsonElement> ReadAsync(HttpClient api, long id, CancellationToken token = default)
    { using var value = JsonDocument.Parse(await api.GetStringAsync("/app/rest/builds/id:" + id, token)); return value.RootElement.Clone(); }

    private const string Settings = """
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
