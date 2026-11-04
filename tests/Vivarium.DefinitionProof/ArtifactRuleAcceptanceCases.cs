using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vivarium.Controller.Persistence;

namespace Vivarium.DefinitionProof;

[NonParallelizable, Category("US013Artifacts")]
public sealed class ArtifactRuleAcceptanceCases
{
    private R0AcceptanceInstallation installation = null!;
    [OneTimeSetUp] public async Task Prepare() { installation = new(); await installation.StartAsync(); }
    [OneTimeTearDown] public async Task Cleanup() => await installation.DisposeAsync();

    [TestCase(false), TestCase(true)]
    public async Task Recipe_publication_and_dependency_rules_keep_exact_nested_binary_bytes_and_missing_input_failure(bool missing)
    {
        await using var host = await installation.CreateCaseAsync(catalogued: true);
        using var administrator = await host.AdministratorAsync(); var csrf = await host.AntiforgeryAsync(administrator);
        var rules = missing ? "absent/** => ci-input/required"
            : "prepare/** => ci-input/prepare\nlinux/** => ci-input/linux\ntree/** => ci-input/tree\nbinary/** => ci-input/binary";
        var source = Settings.Replace("PRODUCER", JsonSerializer.Serialize(Producer), StringComparison.Ordinal)
            .Replace("CONSUMER", JsonSerializer.Serialize(Consumer), StringComparison.Ordinal)
            .Replace("RULES", JsonSerializer.Serialize(rules), StringComparison.Ordinal);
        await File.WriteAllTextAsync(Path.Combine(host.Definitions, ".vivarium/unbound/P/Settings.g.cs"), source);
        await R0AcceptanceCases.CommitCaseAsync(host, "fixture: captured recipe artifact rules and binary handoff");
        Assert.That((await R0AcceptanceCases.ReconcileCaseAsync(administrator, csrf)).GetProperty("state").GetString(), Is.EqualTo("applied"));
        using var actor = await R0AcceptanceCases.RestrictedActorAsync(host, administrator, csrf);
        actor.BuildClient.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        using var body = new StringContent("{\"buildType\":{\"id\":\"P_Release\"}}", Encoding.UTF8, "application/json");
        using var admission = await actor.BuildClient.PostAsync("/app/rest/buildQueue", body);
        Assert.That(admission.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        using var admitted = JsonDocument.Parse(await admission.Content.ReadAsStringAsync());
        var id = admitted.RootElement.GetProperty("id").GetInt64();
        await using var linux = new OwnedDockerAgent(host.Root);
        await host.StartLinuxAgentAsync(linux, administrator, csrf);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        JsonElement terminal;
        while (true)
        {
            using var response = JsonDocument.Parse(await actor.BuildClient.GetStringAsync("/app/rest/builds/id:" + id, deadline.Token));
            terminal = response.RootElement.Clone();
            if (terminal.GetProperty("state").GetString() == "finished") break;
            await Task.Delay(100, deadline.Token);
        }
        var repository = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "Vivarium.slnx"))) repository = repository.Parent;
        using var oracle = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(repository!.FullName,
            "tests/Vivarium.Tests/TeamCity/Reference/artifact-rules-observations.json")));
        var reference = oracle.RootElement.GetProperty("cases")[missing ? 1 : 0];
        var expected = reference.GetProperty("terminalObservation")[0];
        foreach (var field in new[] { "state", "status", "failedToStart", "statusText", "number" })
            Assert.That(terminal.GetProperty(field).GetRawText(), Is.EqualTo(expected.GetProperty(field).GetRawText()), field);
        var prepareId = terminal.GetProperty("snapshot-dependencies").GetProperty("build")[0].GetProperty("id").GetInt64();
        var referenceArtifacts = reference.GetProperty("artifacts").EnumerateObject().ToArray();
        foreach (var (build, suffix) in new[] { (prepareId, "_Prepare"), (id, missing ? "_Missing" : "_Consume") })
        {
            var files = referenceArtifacts.Single(p => p.Name.EndsWith(suffix, StringComparison.Ordinal)).Value;
            using var manifest = JsonDocument.Parse(await actor.BuildClient.GetStringAsync("/api/v1/builds/" + build + "/artifacts"));
            Assert.That(manifest.RootElement.GetProperty("items").EnumerateArray().Select(a => a.GetProperty("path").GetString()),
                Is.EquivalentTo(files.EnumerateArray().Select(a => a.GetProperty("path").GetString())));
            foreach (var file in files.EnumerateArray())
            {
                var bytes = await actor.BuildClient.GetByteArrayAsync("/app/rest/builds/id:" + build + "/artifacts/files/" + file.GetProperty("path").GetString());
                Assert.That(bytes.Length, Is.EqualTo(file.GetProperty("bytes").GetInt32()));
                Assert.That(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)), Is.EqualTo(file.GetProperty("sha256").GetString()));
            }
        }
        await using var database = await host.Services.GetRequiredService<IDbContextFactory<VivariumDbContext>>().CreateDbContextAsync();
        Assert.That(await database.RuntimeBuildResults.AnyAsync(r => r.BuildId == id), Is.EqualTo(!missing), "Missing required input never invents a user result.");
    }

    private const string Producer = """
        #!/bin/sh
        set -eu
        mkdir -p ci-out/linux ci-out/nested/deep
        printf 'coherent-reference-version\n' > ci-out/a.tar.gz
        printf '{"owned":true}\n' > ci-out/manifest.json
        printf 'owned-linux\n' > ci-out/linux/file.zip
        printf 'owned-deep\n' > ci-out/nested/deep/file.txt
        dd if=/dev/zero of=ci-out/large.bin bs=1024 count=257 2>/dev/null
        echo "##teamcity[publishArtifacts 'ci-out/*.tar.gz => prepare']"
        echo "##teamcity[publishArtifacts 'ci-out/manifest.json => prepare']"
        echo "##teamcity[publishArtifacts 'ci-out/linux/* => linux']"
        echo "##teamcity[publishArtifacts 'ci-out/nested/** => tree']"
        echo "##teamcity[publishArtifacts 'ci-out/large.bin => binary']"
        echo "##teamcity[publishArtifacts 'ci-out/empty/*.txt => unused']"
        """;
    private const string Consumer = """
        #!/bin/sh
        set -eu
        find ci-input -type f -print | sort > inspection.txt
        find ci-input -type f -print | sort | xargs sha256sum >> inspection.txt
        echo "##teamcity[publishArtifacts 'inspection.txt']"
        """;
    private const string Settings = """
        using Vivarium.Definitions;
        public static class Managed {
          public static Settings Declare() => new("P", null, [], [
            new("P_Prepare", "Prepare", [], [new("prepare", "Prepare", "simpleRunner", [new("script.content", PRODUCER, null)])],
              [new("system.os.family", "linux")], [], [], 2, []),
            new("P_Release", "Release", [], [new("release", "Release", "simpleRunner", [new("script.content", CONSUMER, null)])],
              [new("system.os.family", "linux")], [new("P_Prepare", "CANCEL", "CANCEL")],
              [new("P_Prepare", "sameChainOrLastFinished", RULES, true)], 2, [])
          ]);
        }
        """;
}
