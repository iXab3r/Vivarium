using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Vivarium.Controller.Builds.Runtime;
using Vivarium.Contracts.V1;

namespace Vivarium.DefinitionProof;

public sealed partial class ActualT3AcceptanceCases
{
    [Test, Category("US013ReleaseDiagnosis")]
    public async Task Retained_release_dispatch_isolated_diagnosis()
    {
        var output = Path.Combine(Repository(), "artifacts/pilot-chain/release-worker-diagnosis.json");
        await using var host = await R0AcceptanceInstallation.ReopenRetainedAsync(
            Path.Combine(Repository(), "artifacts/w/a0e29c82/r0_case_35700a740669/recovery.private.json"),
            services => services.Remove(services.Single(s => s.ServiceType == typeof(IHostedService)
                && s.ImplementationType == typeof(RuntimeBuildWorker))));
        await using var database = await DatabaseAsync(host);
        var builds = await database.RuntimeBuilds.AsNoTracking().OrderBy(b => b.Id)
            .Select(b => new { b.Id, b.State, b.Status, b.ActorId, b.BuildTypeId, b.StatusText }).ToArrayAsync();
        var agents = await database.AgentRegistrations.AsNoTracking().Select(a => new {
            a.AgentId, a.Authorized, a.DesiredEnabled, a.Reconciled, a.Quarantined, a.MaintenanceDrain,
            a.CurrentWorkloadId, a.SessionAuthenticated }).ToArrayAsync();
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { builds, agents,
            executions = await database.RuntimeBuildExecutions.Select(e => e.BuildId).ToArrayAsync(),
            edges = await database.RuntimeBuildDependencies.Where(d => d.BuildId == 7).ToArrayAsync(),
            selections = await database.RuntimeBuildSourceSelections.Where(s => s.BuildId == 7).ToArrayAsync() }));
        Assert.That(builds.Single(b => b.Id == 7).State, Is.EqualTo("QUEUED"));
        Assert.That(await database.RuntimeBuildExecutions.OrderBy(e => e.BuildId).Select(e => e.BuildId).ToArrayAsync(), Is.EqualTo(new long[] { 1, 2, 3, 7 }));
    }

    [Test, Category("US013ReleaseRecoveryPreflight")]
    public Task Retained_release_read_only_preflight() => RecoverReleaseAsync(preflightOnly: true);

    [Test, Category("US013ReleaseRecovery")]
    public Task Retained_actual_release_reuses_original_producers_before_dispatch() => RecoverReleaseAsync(preflightOnly: false);

    private async Task RecoverReleaseAsync(bool preflightOnly)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Retained credential requires Windows DPAPI.");
        const string revision = "3ae8f481bc7c118747c8d01f0ec14f3f18e8b6ec";
        var repository = Repository();
        var caseRoot = Path.Combine(repository, "artifacts/w/a0e29c82/r0_case_35700a740669");
        var output = Path.Combine(repository, "artifacts/pilot-chain/release-recovery-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(output);
        await using var host = await R0AcceptanceInstallation.ReopenRetainedAsync(Path.Combine(caseRoot, "recovery.private.json"), services =>
            services.Remove(services.Single(s => s.ServiceType == typeof(IHostedService) && s.ImplementationType == typeof(RuntimeBuildWorker))));
        using var actor = host.Client();
        // Recover the unchanged CLI's existing credential in memory only. Never create a user/key/grant.
        var credentialFile = Directory.GetFiles(Path.Combine(caseRoot, "actual-t3-cli/cli-state/roaming/teamcity-cli/owned-r0/secrets"), "*.dpapi").Single();
        var encrypted = await File.ReadAllBytesAsync(credentialFile);
        var credentialBytes = ProtectedData.Unprotect(encrypted, Encoding.UTF8.GetBytes("[\"ai-cli-factory:teamcity-cli\",\"owned-r0:token\"]"), DataProtectionScope.CurrentUser);
        try { actor.DefaultRequestHeaders.Authorization = new("Bearer", Encoding.UTF8.GetString(credentialBytes)); }
        finally { CryptographicOperations.ZeroMemory(credentialBytes); }
        actor.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        string sourceUrl, hostname;
        await using (var database = await DatabaseAsync(host))
        {
            var history = await database.RuntimeBuilds.AsNoTracking().OrderBy(b => b.Id).ToArrayAsync();
            Assert.That(history.Select(b => b.Id), Is.EqualTo(new long[] { 1, 2, 3, 4, 5, 6, 7 }));
            Assert.That(history.Where(b => b.Id > 3 && b.Id < 7).All(b => b.State == "FINISHED" && b.CancelRequestedAt != null), Is.True);
            var builds = history.Where(b => b.Id <= 3).ToArray();
            Assert.That(builds.Select(b => b.Id), Is.EqualTo(new long[] { 1, 2, 3 }));
            Assert.That(builds.Select(b => b.Status), Is.EqualTo(new[] { "SUCCESS", "SUCCESS", "FAILURE" }));
            Assert.That(builds.All(b => b.State == "FINISHED"), Is.True);
            foreach (var (id, configuration) in new[] { (1L, "T3Personal_Prepare"), (2L, "T3Personal_Windows"), (3L, "T3Personal_Release") })
            {
                var buildTypeId = builds.Single(b => b.Id == id).BuildTypeId;
                Assert.That((await database.CatalogBuildTypes.SingleAsync(t => t.Id == buildTypeId)).ExternalId, Is.EqualTo(configuration));
            }
            using var identity = JsonDocument.Parse(await actor.GetStringAsync("/api/v1/users/current"));
            // Successful authorized reads below also evaluate the original credential's current rights.
            Assert.That(builds.Select(b => b.ActorId).Distinct().Count(), Is.EqualTo(1));
            Assert.That(identity.RootElement.GetProperty("id").GetGuid(), Is.EqualTo(builds[0].ActorId));
            var hostConfiguration = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>(host.Services);
            var ancestry = await Vivarium.Controller.Builds.Runtime.RuntimeBuildAccess.AncestryAsync(database, builds[2].ProjectId, CancellationToken.None);
            Assert.That(await new Vivarium.Controller.Identity.CurrentAccess(database, hostConfiguration).AllowsAllAsync(builds[0].ActorId,
                ["view_project", "run_build", "customize_build_parameters"], ancestry), Is.True);
            foreach (var original in builds)
            {
                using var view = JsonDocument.Parse(await actor.GetStringAsync($"/api/v1/builds/{original.Id}"));
                Assert.That(view.RootElement.GetProperty("state").GetString(), Is.EqualTo("finished"));
                Assert.That(view.RootElement.GetProperty("status").GetString(), Is.EqualTo(original.Status));
                Assert.That(view.RootElement.GetProperty("number").GetString(), Is.EqualTo("0.1.6-nightly.20261104.918"));
            }
            Assert.That(await database.RuntimeBuildDependencies.AnyAsync(d => d.BuildId == 2 && d.UpstreamBuildId == 1), Is.True);
            Assert.That(await database.RuntimeBuildDependencies.AnyAsync(d => d.BuildId == 3 && d.UpstreamBuildId == 2), Is.True);
            var consumedArtifacts = await database.RuntimeBuildConsumedArtifacts.AsNoTracking().ToArrayAsync();
            Assert.That(consumedArtifacts.Any(a => a.BuildId == 2 && a.ProducerBuildId == 1), Is.True);
            Assert.That(consumedArtifacts.Any(a => a.BuildId == 3 && a.ProducerBuildId == 2), Is.True);
            var selections = await database.RuntimeBuildSourceSelections.AsNoTracking().ToArrayAsync();
            Assert.That(selections.Where(s => s.BuildId <= 3).Select(s => s.BuildId), Is.EquivalentTo(new long[] { 1, 2, 3 }));
            Assert.That(selections.Where(s => s.BuildId <= 3).All(s => s.Version == revision), Is.True);
            var sourceReceipts = await database.RuntimeBuildSourceReceipts.AsNoTracking().ToArrayAsync();
            Assert.That(sourceReceipts.Where(r => r.BuildId <= 3).Select(r => r.BuildId), Is.EquivalentTo(new long[] { 1, 2, 3 }));
            foreach (var receipt in sourceReceipts)
                Assert.That(VcsCheckoutReceipt.Parser.ParseFrom(receipt.Evidence).Revision, Is.EqualTo(revision));
            await File.WriteAllTextAsync(Path.Combine(output, "original-provenance.json"), JsonSerializer.Serialize(new {
                builds = builds.Select(b => new { b.Id, b.BuildTypeId, b.ActorId, b.Number, b.State, b.Status }), selections,
                dependencies = await database.RuntimeBuildDependencies.AsNoTracking().ToArrayAsync(),
                consumed = await database.RuntimeBuildConsumedArtifacts.AsNoTracking().ToArrayAsync() }));
            foreach (var build in builds)
            {
                using var inventory = JsonDocument.Parse(await actor.GetStringAsync($"/api/v1/builds/{build.Id}/artifacts"));
                await File.WriteAllTextAsync(Path.Combine(output, $"build-{build.Id}-inventory.json"), inventory.RootElement.GetRawText());
                var artifacts = await database.RuntimeBuildArtifacts.Where(a => a.BuildId == build.Id).OrderBy(a => a.Ordinal).ToArrayAsync();
                foreach (var artifact in artifacts)
                {
                    var receiptPath = Path.Combine(caseRoot, $"recovery-verified-{build.Id}-{artifact.Ordinal}.json");
                    var binding = JsonSerializer.Serialize(new { checkpointSha256 = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(caseRoot, "recovery.private.json")))), build.Id, build.ActorId, revision, artifact });
                    if (File.Exists(receiptPath) && await File.ReadAllTextAsync(receiptPath) == binding) continue;
                    using var response = await actor.GetAsync($"/app/rest/builds/id:{build.Id}/artifacts/files/" + ArtifactPath(artifact.Path), HttpCompletionOption.ResponseHeadersRead);
                    response.EnsureSuccessStatusCode();
                    await using var bytes = await response.Content.ReadAsStreamAsync();
                    Assert.That(Convert.ToHexStringLower(await SHA256.HashDataAsync(bytes)), Is.EqualTo(artifact.Sha256));
                    await File.WriteAllTextAsync(receiptPath, binding);
                }
                await File.WriteAllTextAsync(Path.Combine(output, $"build-{build.Id}-verified.json"), JsonSerializer.Serialize(artifacts));
            }
            var collected = await database.RuntimeBuildInputs.SingleAsync(i => i.BuildId == 3);
            sourceUrl = System.Text.RegularExpressions.Regex.Match(collected.SourceJson!, @"git://host\.docker\.internal:\d+/source\.git").Value;
            Assert.That(sourceUrl, Is.Not.Empty);
            var execution = await database.RuntimeBuildExecutions.SingleAsync(e => e.BuildId == 1);
            hostname = JsonSerializer.Deserialize<Dictionary<string, string>>((await database.AgentRegistrations.SingleAsync(a => a.AgentId == execution.AgentId)).ReportedParametersJson)!["system.hostname"];
            Assert.That(history.Single(b => b.Id == 7).State, Is.EqualTo("FINISHED"));
            Assert.That(history.Single(b => b.Id == 7).Status, Is.EqualTo("FAILURE"));
            Assert.That(history.Single(b => b.Id == 7).ActorId, Is.EqualTo(builds[0].ActorId));
            Assert.That(history.Single(b => b.Id == 7).BuildTypeId, Is.EqualTo(builds[2].BuildTypeId));
            Assert.That(await database.RuntimeBuildExecutions.OrderBy(e => e.BuildId).Select(e => e.BuildId).ToArrayAsync(), Is.EqualTo(new long[] { 1, 2, 3, 7 }));
        }
        await using var git = new OwnedGitSource(caseRoot, retainedUrl: sourceUrl);
        Assert.That(await git.StartAsync(), Is.EqualTo(revision));
        var secretPath = Environment.GetEnvironmentVariable("VIVARIUM_US013_TOKEN_FILE") ?? throw new InvalidOperationException("Parent publication credential expired or absent.");
        if (!File.Exists(secretPath)) throw new InvalidOperationException("Parent publication credential expired or absent.");
        var secret = (await File.ReadAllTextAsync(secretPath)).Trim();
        using (var github = GitHub(secret))
        using (var draft = await JsonAsync(github, "repos/iXab3r/vivarium-us013-e2e/releases/434102890"))
        {
            using var listed = await JsonAsync(github, "repos/iXab3r/vivarium-us013-e2e/releases?per_page=100");
            var selected = listed.RootElement.EnumerateArray().Where(r => r.GetProperty("tag_name").GetString() == "v0.1.6-nightly.20261104.918").ToArray();
            Assert.That(selected, Has.Length.EqualTo(1), "The unchanged publisher must select the exact draft from the release list.");
            Assert.That(selected[0].GetProperty("id").GetInt64(), Is.EqualTo(434102890));
            using var tag = await JsonAsync(github, "repos/iXab3r/vivarium-us013-e2e/git/ref/tags/v0.1.6-nightly.20261104.918");
            Assert.That(tag.RootElement.GetProperty("object").GetProperty("type").GetString(), Is.EqualTo("commit"));
            Assert.That(tag.RootElement.GetProperty("object").GetProperty("sha").GetString(), Is.EqualTo("551ae1f5580629142f8289d360e21122c99c8e21"));
            Assert.That(draft.RootElement.GetProperty("prerelease").GetBoolean(), Is.True);
            Assert.That(draft.RootElement.GetProperty("body").GetString()!.Split('\n')[0], Is.EqualTo("Vivarium US-013 acceptance source " + revision + " target 551ae1f5580629142f8289d360e21122c99c8e21"));
            Assert.That(draft.RootElement.GetProperty("id").GetInt64(), Is.EqualTo(434102890));
            Assert.That(draft.RootElement.GetProperty("draft").GetBoolean(), Is.True);
            Assert.That(draft.RootElement.GetProperty("tag_name").GetString(), Is.EqualTo("v0.1.6-nightly.20261104.918"));
            Assert.That(draft.RootElement.GetProperty("target_commitish").GetString(), Is.EqualTo("551ae1f5580629142f8289d360e21122c99c8e21"));
            // The failed original publisher stopped before uploading. Exit 75 below must create the first asset.
            Assert.That(draft.RootElement.GetProperty("assets").GetArrayLength(), Is.Zero);
            await File.WriteAllTextAsync(Path.Combine(output, "draft-before.json"), draft.RootElement.GetRawText());
        }
        if (preflightOnly)
        {
            await File.WriteAllTextAsync(Path.Combine(output, "preflight.json"), JsonSerializer.Serialize(new { revision, sourceUrl, hostname, admission = false }));
            return;
        }
        long root = 0;
        try
        {
            using var administrator = await host.AdministratorAsync(); var csrf = await host.AntiforgeryAsync(administrator);
            var helper = await File.ReadAllTextAsync(Path.Combine(repository, "tools/us013-acceptance/publisher/publication-state.mjs"));
            var helperHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(helper)));
            // Private generated step inputs own this repair. Never modify the old
            // checkout or claim that the adapter belonged to its package commit.
            var repairedScript = await RepairedPublisherAsync();
            var definitionPath = Path.Combine(host.Definitions, ".vivarium/unbound/P/Settings.g.cs");
            var originalDefinition = await File.ReadAllTextAsync(definitionPath);
            var originalLiteral = JsonSerializer.Serialize(Publisher);
            Assert.That(originalDefinition.Split(originalLiteral).Length, Is.EqualTo(2));
            await File.WriteAllTextAsync(definitionPath, originalDefinition.Replace(originalLiteral, JsonSerializer.Serialize(repairedScript), StringComparison.Ordinal));
            await R0AcceptanceCases.CommitCaseAsync(host, "fixture: versioned Release publisher derived-checksum repair; retain original package SHA");
            Assert.That((await R0AcceptanceCases.ReconcileCaseAsync(administrator, csrf)).GetProperty("state").GetString(), Is.EqualTo("applied"));
            var definitionCommit = await ProofProcess.RunAsync("git", ["rev-parse", "HEAD"], host.Definitions);
            await File.WriteAllTextAsync(Path.Combine(output, "definition-repair.json"), JsonSerializer.Serialize(new {
                packageSource = revision, helperHash, definitionGit = definitionCommit.Text,
                scriptSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(repairedScript))) }));
            using var staged = await actor.PostAsJsonAsync("/api/v1/build-run-inputs", new {
                configurationId = "T3Personal_Release", environments = new Dictionary<string, Dictionary<string,string>> {
                    ["T3Personal_Release"] = new() { ["GITHUB_TOKEN"] = secret } } });
            Assert.That(staged.StatusCode, Is.EqualTo(HttpStatusCode.Created));
            using var prepared = JsonDocument.Parse(await staged.Content.ReadAsStringAsync());
            using var admission = await actor.PostAsJsonAsync("/api/v1/builds", new {
                configurationId = "T3Personal_Release", selectedDependencies = new[] { new { id = 1L }, new { id = 2L } },
                parameters = new Dictionary<string,string> { ["vivarium.run-input"] = prepared.RootElement.GetProperty("reference").GetString()! } });
            admission.EnsureSuccessStatusCode();
            using var accepted = JsonDocument.Parse(await admission.Content.ReadAsStringAsync());
            root = accepted.RootElement.GetProperty("id").GetInt64();
            Assert.That(root, Is.EqualTo(8));
            Assert.That(host.Services.GetServices<IHostedService>().Any(s => s is RuntimeBuildWorker), Is.False);
            await using (var database = await DatabaseAsync(host))
            {
                Assert.That(await database.RuntimeBuildExecutions.CountAsync(), Is.EqualTo(4));
                Assert.That(await database.RuntimeBuilds.CountAsync(), Is.EqualTo(8));
                var input = await database.RuntimeBuildInputs.SingleAsync(i => i.BuildId == root);
                await host.Services.GetRequiredService<RuntimeBuildSources>().CollectQueuedAsync(input, CancellationToken.None);
                var edges = await database.RuntimeBuildDependencies.Where(d => d.BuildId == root).ToArrayAsync();
                Assert.That(edges.Where(d => d.Kind == "SNAPSHOT").Select(d => d.UpstreamBuildId), Is.EqualTo(new long[] { 2 }));
                Assert.That((await database.RuntimeBuildSourceSelections.SingleAsync(s => s.BuildId == root)).Version, Is.EqualTo(revision));
                await File.WriteAllTextAsync(Path.Combine(output, "predispatch-guard.json"), JsonSerializer.Serialize(new { root, edges, originalPrepare = 1, originalWindows = 2, executions = 4, dispatchIsolated = true, realSourceCollector = true }));
            }
            await using var runtime = new OwnedContainerRuntime(); await runtime.StartAsync(actualT3: true);
            await using var linux = new OwnedDockerAgent(caseRoot, gitTools: true, runtime: runtime, actualT3: true, hostname: hostname);
            await host.StartLinuxAgentAsync(linux, administrator, csrf);
            // Enrollment restarts the Host; resolve the worker from that final service provider.
            using var worker = new RuntimeBuildWorker(host.Services);
            try
            {
                await worker.StartAsync(CancellationToken.None);
                using var bound = new CancellationTokenSource(TimeSpan.FromMinutes(17));
                while (true)
                {
                    worker.EnsureHealthy();
                    using var state = JsonDocument.Parse(await actor.GetStringAsync($"/api/v1/builds/{root}", bound.Token));
                    if (state.RootElement.GetProperty("state").GetString() == "finished")
                    { Assert.That(state.RootElement.GetProperty("status").GetString(), Is.EqualTo("SUCCESS")); break; }
                    await Task.Delay(1000, bound.Token);
                }
            }
            finally
            {
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await worker.StopAsync(stop.Token).WaitAsync(stop.Token);
            }
            using var github = GitHub(secret);
            using var release = await JsonAsync(github, "repos/iXab3r/vivarium-us013-e2e/releases/434102890");
            Assert.That(release.RootElement.GetProperty("draft").GetBoolean(), Is.False);
            Assert.That(release.RootElement.GetProperty("tag_name").GetString(), Is.EqualTo("v0.1.6-nightly.20261104.918"));
            using var partial = JsonDocument.Parse(await actor.GetStringAsync($"/app/rest/builds/id:{root}/artifacts/files/publication-proof/partial.json"));
            Assert.That(partial.RootElement.GetProperty("id").GetInt64(), Is.EqualTo(434102890));
            Assert.That(partial.RootElement.GetProperty("draft").GetBoolean(), Is.True);
            Assert.That(partial.RootElement.GetProperty("assets").GetArrayLength(), Is.EqualTo(1));
            var first = partial.RootElement.GetProperty("assets")[0];
            Assert.That(release.RootElement.GetProperty("assets").EnumerateArray().Single(a => a.GetProperty("name").GetString() == first.GetProperty("name").GetString()).GetProperty("id").GetInt64(), Is.EqualTo(first.GetProperty("id").GetInt64()));
            await using var finalDb = await DatabaseAsync(host);
            Assert.That(await finalDb.RuntimeBuildExecutions.CountAsync(), Is.EqualTo(5));
            Assert.That(await finalDb.RuntimeBuildExecutions.CountAsync(e => e.BuildId <= 3), Is.EqualTo(3));
            Assert.That(await finalDb.RuntimeBuilds.CountAsync(), Is.EqualTo(8));
            var finalBuild = await finalDb.RuntimeBuilds.SingleAsync(b => b.Id == root);
            Assert.That(finalBuild.Number, Is.EqualTo("0.1.6-nightly.20261104.918"));
            Assert.That((await finalDb.RuntimeBuildSourceSelections.SingleAsync(s => s.BuildId == root)).Version, Is.EqualTo(revision));
            Assert.That(VcsCheckoutReceipt.Parser.ParseFrom((await finalDb.RuntimeBuildSourceReceipts.SingleAsync(s => s.BuildId == root)).Evidence).Revision, Is.EqualTo(revision));
            Assert.That(await finalDb.RuntimeBuildConsumedArtifacts.AnyAsync(a => a.BuildId == root && a.ProducerBuildId == 2), Is.True);
            Assert.That(await finalDb.RuntimeBuilds.Where(b => b.Id <= 3).OrderBy(b => b.Id).Select(b => b.Status).ToArrayAsync(), Is.EqualTo(new[] { "SUCCESS", "SUCCESS", "FAILURE" }));
            foreach (var asset in await finalDb.RuntimeBuildArtifacts.Where(a => a.BuildId == root && a.Path.StartsWith("release/")).ToArrayAsync())
            {
                var remote = release.RootElement.GetProperty("assets").EnumerateArray().Single(a => a.GetProperty("name").GetString() == asset.Path.Substring(8));
                Assert.That(remote.GetProperty("digest").GetString(), Is.EqualTo("sha256:" + asset.Sha256));
                using var request = new HttpRequestMessage(HttpMethod.Get, "repos/iXab3r/vivarium-us013-e2e/releases/assets/" + remote.GetProperty("id").GetInt64());
                request.Headers.Accept.ParseAdd("application/octet-stream");
                using var downloaded = await github.SendAsync(request, HttpCompletionOption.ResponseHeadersRead); downloaded.EnsureSuccessStatusCode();
                await using var bytes = await downloaded.Content.ReadAsStreamAsync();
                Assert.That(Convert.ToHexStringLower(await SHA256.HashDataAsync(bytes)), Is.EqualTo(asset.Sha256));
            }
            await File.WriteAllTextAsync(Path.Combine(output, "receipt.json"), JsonSerializer.Serialize(new { root, revision, originalPrepare = 1, originalWindows = 2, releaseId = 434102890, builtApplicationLaunched = false }));
        }
        finally
        {
            await SaveProgressAsync(host, output, revision);
            await RetainEvidenceAsync(host, actor, output, secret);
        }
    }
}
