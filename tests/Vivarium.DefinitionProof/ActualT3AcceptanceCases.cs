using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Chiral;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Playwright;
using Vivarium.Controller.Builds.Runtime;
using Vivarium.Controller.Persistence;

namespace Vivarium.DefinitionProof;

/// <summary>The actual owned recipe. No built T3 GUI, CLI or packaged server is executed.</summary>
[NonParallelizable]
public sealed partial class ActualT3AcceptanceCases
{
    internal static IReadOnlyDictionary<string, string> WindowsAgentEnvironment()
    {
        var native = Environment.GetFolderPath(Environment.SpecialFolder.System);
        if (!File.Exists(Path.Combine(native, "tar.exe"))) throw new FileNotFoundException("Native Windows tar is required.");
        return new Dictionary<string, string> { ["PATH"] = native + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH") };
    }

    [Test, Category("US013ActualT3Runtime")]
    public async Task Owned_windows_environment_extracts_drive_letter_archive_with_spaces_through_node()
    {
        var root = CreateWorkRoot();
        try
        {
            var source = Path.Combine(root, "archive source"); var target = Path.Combine(root, "extracted bytes");
            Directory.CreateDirectory(source); Directory.CreateDirectory(target);
            var bytes = Enumerable.Range(0, 4097).Select(i => (byte)(i % 251)).ToArray();
            await File.WriteAllBytesAsync(Path.Combine(source, "payload bytes.bin"), bytes);
            var archive = Path.Combine(root, "archive with spaces.tar.gz");
            var native = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "tar.exe");
            await ProofProcess.RunAsync(native, ["-czf", archive, "-C", source, "payload bytes.bin"]);
            const string script = "const c=require('child_process'); const r=c.spawnSync('tar',['-xzf',process.argv[1],'-C',process.argv[2]],{encoding:'utf8'}); if(r.error)throw r.error; process.stdout.write(JSON.stringify({status:r.status,error:r.stderr,selection:c.spawnSync('where.exe',['tar'],{encoding:'utf8'}).stdout.trim().split(/\\r?\\n/)[0],version:c.spawnSync('tar',['--version'],{encoding:'utf8'}).stdout.trim()}));";
            var inherited = await ProofProcess.RunAsync("node", ["-e", script, archive, target]);
            TestContext.Out.WriteLine("Inherited tar probe: " + inherited.Text);
            using (var negative = JsonDocument.Parse(inherited.Text))
                if (negative.RootElement.GetProperty("selection").GetString()!.Contains("cygwin", StringComparison.OrdinalIgnoreCase))
                { Assert.That(negative.RootElement.GetProperty("status").GetInt32(), Is.Not.Zero); Assert.That(File.Exists(Path.Combine(target, "payload bytes.bin")), Is.False); }
            var environment = WindowsAgentEnvironment();
            Assert.That(environment["PATH"], Does.EndWith(Environment.GetEnvironmentVariable("PATH") ?? throw new InvalidOperationException("Inherited PATH is required.")));
            var result = await ProofProcess.RunAsync("node", ["-e", script, archive, target], environment: environment);
            TestContext.Out.WriteLine("Owned Agent tar probe: " + result.Text);
            using var receipt = JsonDocument.Parse(result.Text);
            Assert.That(receipt.RootElement.GetProperty("status").GetInt32(), Is.Zero);
            Assert.That(receipt.RootElement.GetProperty("selection").GetString(), Is.EqualTo(native).IgnoreCase);
            Assert.That(receipt.RootElement.GetProperty("version").GetString(), Does.Contain("bsdtar"));
            Assert.That(await File.ReadAllBytesAsync(Path.Combine(target, "payload bytes.bin")), Is.EqualTo(bytes));
            TestContext.Out.WriteLine("Exact extracted SHA256: " + Convert.ToHexString(SHA256.HashData(bytes)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Test, Category("US013ActualT3Runtime")]
    public async Task Short_owned_checkout_launches_existing_esbuild_through_node()
    {
        var executable = Environment.GetEnvironmentVariable("US013_ESBUILD_PROBE")
            ?? throw new InvalidOperationException("Supply the existing esbuild executable for the focused runtime probe.");
        Assert.That(File.Exists(executable), Is.True);
        var root = CreateWorkRoot();
        try
        {
            var checkout = Path.Combine(root, "a", "builds", Guid.NewGuid().ToString("N"));
            var binary = Path.Combine(checkout, "node_modules", ".pnpm", "@esbuild+win32-x64@0.28.2", "node_modules", "@esbuild", "win32-x64", "esbuild.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(binary)!); File.Copy(executable, binary);
            Assert.That(Path.GetRelativePath(root, checkout), Does.StartWith("a" + Path.DirectorySeparatorChar));
            Assert.That(binary.Length, Is.LessThan(260));
            var probe = await ProofProcess.RunAsync("node", ["-e",
                "const c=require('child_process');const r=c.spawnSync(process.argv[1],['--version'],{encoding:'utf8'});if(r.error)throw r.error;if(r.status!==0)throw Error('exit '+r.status);const s=c.spawnSync(process.env.ComSpec,['/d','/s','/c','exit 0'],{cwd:require('path').dirname(process.argv[1])});if(s.error)throw s.error;if(s.status!==0)throw Error('shell exit '+s.status);process.stdout.write(r.stdout);", binary], checkout);
            Assert.That(probe.Text, Is.EqualTo("0.28.2"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Test, Category("US013ActualT3Runtime")]
    public async Task Retained_fixture_disposal_preserves_checkout_without_recursive_attribute_changes()
    {
        var root = CreateWorkRoot();
        var sentinel = Path.Combine(root, "retained-evidence");
        try
        {
            await File.WriteAllTextAsync(sentinel, "owned fixture evidence");
            File.SetAttributes(sentinel, FileAttributes.ReadOnly);
            await new R0AcceptanceInstallation(root).DisposeAsync();
            Assert.That(File.ReadAllText(sentinel), Is.EqualTo("owned fixture evidence"));
            Assert.That(File.GetAttributes(sentinel).HasFlag(FileAttributes.ReadOnly), Is.True);
        }
        finally
        {
            File.SetAttributes(sentinel, FileAttributes.Normal);
            Directory.Delete(root, recursive: true);
        }
    }

    [Test, Category("US013ActualT3Runtime")]
    public async Task Short_owned_checkout_compiles_existing_nsis_template_include()
    {
        var compiler = Environment.GetEnvironmentVariable("US013_NSIS_PROBE")
            ?? throw new InvalidOperationException("Supply the installed makensis executable.");
        var templates = Environment.GetEnvironmentVariable("US013_NSIS_TEMPLATES")
            ?? throw new InvalidOperationException("Supply the installed app-builder-lib templates directory.");
        var root = CreateWorkRoot();
        try
        {
            var checkout = Path.Combine(root, "a", "builds", Guid.NewGuid().ToString("N"));
            var target = Path.Combine(checkout, "node_modules", ".pnpm", "app-builder-lib@26.16.1_pat_7bbc6c7fe21eb385688749983dfd6938", "node_modules", "app-builder-lib", "templates");
            foreach (var file in Directory.EnumerateFiles(templates, "*", SearchOption.AllDirectories))
            {
                var destination = Path.Combine(target, Path.GetRelativePath(templates, file));
                Assert.That(destination.Length, Is.LessThan(250), destination);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination);
            }
            var include = Path.Combine(target, "nsis", "include", "StdUtils.nsh");
            var script = Path.Combine(checkout, "probe.nsi");
            await File.WriteAllTextAsync(script, $"!include \"{include}\"\nOutFile probe.exe\nSection\nSectionEnd\n");
            var result = await ProofProcess.RunAsync(compiler, ["/V2", script], checkout);
            Assert.That(result.ExitCode, Is.Zero);
            Assert.That(File.Exists(Path.Combine(checkout, "probe.exe")), Is.True);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Test, Category("US013Recovery")]
    public async Task Tiny_artifact_survives_checkpoint_disposal_and_existing_state_reopen()
    {
        var root = CreateWorkRoot();
        await using var installation = new R0AcceptanceInstallation(root, Path.Combine(root, "a"), WindowsAgentEnvironment(), retainDatabase: true);
        await installation.StartAsync();
        var host = await installation.CreateCaseAsync(catalogued: true);
        var checkpoint = Path.Combine(host.Root, "recovery.private.json");
        using var administrator = await host.AdministratorAsync();
        var csrf = await host.AntiforgeryAsync(administrator);
        await host.StartWindowsAgentAsync(administrator, csrf);
        using var actor = await R0AcceptanceCases.RestrictedActorAsync(host, administrator, csrf);
        using var admission = await actor.BuildClient.PostAsJsonAsync("/api/v1/builds", new { configurationId = "P_LocalSmoke" });
        Assert.That(admission.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        using var submitted = JsonDocument.Parse(await admission.Content.ReadAsStringAsync());
        var id = submitted.RootElement.GetProperty("id").GetInt64();
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        while (true)
        {
            using var build = JsonDocument.Parse(await actor.BuildClient.GetStringAsync($"/api/v1/builds/{id}", bound.Token));
            if (build.RootElement.GetProperty("state").GetString() == "finished")
            { Assert.That(build.RootElement.GetProperty("status").GetString(), Is.EqualTo("SUCCESS")); break; }
            await Task.Delay(100, bound.Token);
        }
        async Task<byte[]> DownloadAsync()
        {
            using var list = JsonDocument.Parse(await actor.BuildClient.GetStringAsync($"/api/v1/builds/{id}/artifacts"));
            return await actor.BuildClient.GetByteArrayAsync(list.RootElement.GetProperty("items")[0].GetProperty("href").GetString()!);
        }
        var before = await DownloadAsync();
        Assert.That(before, Is.EqualTo("owned-r0-smoke\n"u8.ToArray()));
        await host.DisposeAsync();
        await installation.DisposeAsync();
        await using (var reopened = await R0AcceptanceInstallation.ReopenRetainedAsync(checkpoint))
        {
            var after = await DownloadAsync();
            Assert.That(SHA256.HashData(after), Is.EqualTo(SHA256.HashData(before)));
            using var anonymous = reopened.Client();
            using var denied = await anonymous.GetAsync($"/api/v1/builds/{id}/artifacts");
            Assert.That(denied.IsSuccessStatusCode, Is.False);
        }
        installation.ReleaseRetainedPostgres();
    }

    [Test, Category("US013ActualT3Setup")]
    public async Task Actual_recipe_declarations_compile_and_expose_the_three_original_configurations()
    {
        var inputs = await InputsAsync();
        await using var installation = new R0AcceptanceInstallation(); await installation.StartAsync();
        await using var host = await installation.CreateCaseAsync(catalogued: true, configureServices: services =>
            services.Remove(services.Single(s => s.ServiceType == typeof(IHostedService) && s.ImplementationType == typeof(RuntimeBuildWorker))));
        using var administrator = await host.AdministratorAsync(); var csrf = await host.AntiforgeryAsync(administrator);
        await ConfigureStorageAsync(host);
        await ConfigureAsync(host, administrator, csrf, inputs, "git://127.0.0.1:9/source.git", "owned-linux", "owned-windows");
        using var actor = await R0AcceptanceCases.RestrictedActorAsync(host, administrator, csrf);
        using var response = JsonDocument.Parse(await actor.BuildClient.GetStringAsync("/api/v1/projects/P/build-configurations"));
        Assert.That(response.RootElement.GetProperty("items").EnumerateArray().Select(c => c.GetProperty("id").GetString()),
            Is.EquivalentTo(inputs["configurations"]!.AsArray().Select(c => c!["id"]!.GetValue<string>())));
        await using var database = await DatabaseAsync(host);
        Assert.That(await database.RuntimeBuilds.CountAsync(), Is.Zero, "Declaration setup is not execution acceptance.");
    }

    [Test, Category("US013ActualT3")]
    public async Task Published_CLI_builds_packages_and_publishes_actual_T3_with_partial_draft_recovery_and_exact_bytes()
    {
        var inputs = await InputsAsync();
        var secretPath = Environment.GetEnvironmentVariable("VIVARIUM_US013_TOKEN_FILE")
            ?? throw new InvalidOperationException("The private test-repository publication credential has not been delivered.");
        var secret = (await File.ReadAllTextAsync(secretPath)).Trim();
        if (secret.Length is 0 or > 4096 || secret.Any(char.IsWhiteSpace)) throw new InvalidDataException("The private publication credential is invalid.");
        var repository = Repository();
        var output = Path.Combine(repository, "artifacts/us013-t3-acceptance/runs/" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(output);
        using var github = GitHub(secret);
        await CheckCredentialAsync(github, inputs["publication_target"]!.GetValue<string>(), output);
        var workRoot = CreateWorkRoot();
        await File.WriteAllTextAsync(Path.Combine(output, "workspace.json"), JsonSerializer.Serialize(new { workRoot, retained = true }));
        await using var installation = new R0AcceptanceInstallation(workRoot, Path.Combine(workRoot, "a"), WindowsAgentEnvironment(), retainDatabase: true); await installation.StartAsync();
        await using var host = await installation.CreateCaseAsync(catalogued: true);
        using var administrator = await host.AdministratorAsync(); var csrf = await host.AntiforgeryAsync(administrator);
        await ConfigureStorageAsync(host);
        var source = Path.Combine(repository, "artifacts/us013-t3-recovery-v3/source");
        await using var git = new OwnedGitSource(host.Root, existingSource: source);
        var revision = await git.StartAsync(); Assert.That(revision, Is.EqualTo(inputs["adapted_source"]!.GetValue<string>()));
        await using var runtime = new OwnedContainerRuntime(); await runtime.StartAsync(actualT3: true);
        await host.StartWindowsAgentAsync(administrator, csrf); var windowsId = host.AgentId!;
        await using var linux = new OwnedDockerAgent(host.Root, gitTools: true, runtime: runtime, actualT3: true);
        await host.StartLinuxAgentAsync(linux, administrator, csrf); var linuxId = host.AgentId!;
        string windowsName, linuxName;
        await using (var database = await DatabaseAsync(host))
        {
            string Hostname(string value) => JsonSerializer.Deserialize<Dictionary<string, string>>(value)!["system.hostname"];
            windowsName = Hostname((await database.AgentRegistrations.SingleAsync(a => a.AgentId == windowsId)).ReportedParametersJson);
            linuxName = Hostname((await database.AgentRegistrations.SingleAsync(a => a.AgentId == linuxId)).ReportedParametersJson);
        }
        await ConfigureAsync(host, administrator, csrf, inputs, git.Url, linuxName, windowsName);
        // Fresh installations can repeat this expensive fixture in one day without reusing a tag.
        // This is fixture runtime data, not a desired-setting or counter-management API claim.
        await using (var database = await DatabaseAsync(host))
        {
            var prepare = await database.CatalogBuildTypes.SingleAsync(t => t.ExternalId == "T3Personal_Prepare");
            database.RuntimeBuildCounters.Add(new() { BuildTypeId = prepare.Id, NextValue = 1 + DateTimeOffset.UtcNow.Hour * 60 + DateTimeOffset.UtcNow.Minute });
            await database.SaveChangesAsync();
        }
        using var actor = await R0AcceptanceCases.RestrictedActorAsync(host, administrator, csrf);
        actor.BuildClient.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        using var staged = await actor.BuildClient.PostAsJsonAsync("/api/v1/build-run-inputs", new
        {
            configurationId = "T3Personal_Release", environments = new Dictionary<string, Dictionary<string, string>>
            { ["T3Personal_Prepare"] = new() { ["GITHUB_TOKEN"] = secret }, ["T3Personal_Release"] = new() { ["GITHUB_TOKEN"] = secret } }
        });
        Assert.That(staged.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        using var input = JsonDocument.Parse(await staged.Content.ReadAsStringAsync()); var marker = input.RootElement.GetProperty("reference").GetString()!;
        var cliRoot = Path.Combine(host.Root, "actual-t3-cli"); Directory.CreateDirectory(cliRoot);
        var cli = new UnchangedCliScenario(cliRoot, host.Origin, host.Certificate);
        var credential = actor.BuildClient.DefaultRequestHeaders.Authorization!.Parameter!;
        string user;
        await using (var database = await DatabaseAsync(host)) user = (await database.Users.SingleAsync(u => u.Id == actor.UserId)).UserName!;
        await cli.AuthenticateAsync(credential, user);
        await cli.RunAsync(credential, 0, "-p", "owned-r0", "permissions", "grant", "Update");
        using var cliLifetime = new CancellationTokenSource();
        var running = cli.RunAsync(credential, 0, TimeSpan.FromMinutes(215), cliLifetime.Token, "-p", "owned-r0", "jobs", "run", "T3Personal_Release",
            "--param", "vivarium.run-input=" + marker, "--wait", "--timeout", "210m", "--interval", "2s");
        JsonElement result;
        try
        {
            while (!running.IsCompleted)
            {
                await SaveProgressAsync(host, output, revision);
                await Task.WhenAny(running, Task.Delay(TimeSpan.FromSeconds(5)));
            }
            result = await running;
        }
        catch
        {
            // Preserve bounded public diagnostics before the owned installation is disposed.
            try { await SaveProgressAsync(host, output, revision); await RetainEvidenceAsync(host, actor.BuildClient, output, secret); }
            catch (Exception failure) { await File.WriteAllTextAsync(Path.Combine(output, "diagnostic-capture-failed.txt"), failure.GetType().Name); }
            throw;
        }
        finally
        {
            cliLifetime.Cancel();
            try { await running; } catch when (running.IsFaulted || running.IsCanceled) { }
        }
        var root = result.GetProperty("build").GetProperty("id").GetInt64();
        Assert.That(result.GetProperty("outcome").GetString(), Is.EqualTo("succeeded"));
        await SaveProgressAsync(host, output, revision);
        using var native = JsonDocument.Parse(await actor.BuildClient.GetStringAsync("/api/v1/builds/" + root));
        var version = native.RootElement.GetProperty("number").GetString()!;
        var artifacts = new List<(string Path, string Sha256, long Length)>();
        await using (var database = await DatabaseAsync(host))
        {
            var builds = await database.RuntimeBuilds.OrderBy(b => b.Id).ToArrayAsync(); Assert.That(builds, Has.Length.EqualTo(3));
            Assert.That(builds.All(b => b.State == "FINISHED" && b.Status == "SUCCESS" && b.Number == version), Is.True);
            Assert.That(await database.RuntimeBuildSourceSelections.AllAsync(s => s.Version == revision), Is.True);
            var receipts = await database.RuntimeBuildSourceReceipts.ToArrayAsync(); Assert.That(receipts, Has.Length.EqualTo(3));
            foreach (var receipt in receipts)
            {
                var consumed = Vivarium.Contracts.V1.VcsCheckoutReceipt.Parser.ParseFrom(receipt.Evidence);
                Assert.That(consumed.Succeeded, Is.True); Assert.That(consumed.Revision, Is.EqualTo(revision));
            }
            foreach (var execution in await database.RuntimeBuildExecutions.ToArrayAsync())
            {
                var type = await database.CatalogBuildTypes.SingleAsync(t => t.Id == builds.Single(b => b.Id == execution.BuildId).BuildTypeId);
                Assert.That(execution.AgentId, Is.EqualTo(type.ExternalId == "T3Personal_Windows" ? windowsId : linuxId));
                Assert.That(execution.Assignment.AsSpan().IndexOf(Encoding.UTF8.GetBytes(secret)), Is.EqualTo(-1));
            }
            foreach (var artifact in await database.RuntimeBuildArtifacts.Where(a => a.BuildId == root).OrderBy(a => a.Ordinal).ToArrayAsync())
                artifacts.Add((artifact.Path, artifact.Sha256, artifact.Size));
        }
        Assert.That(await runtime.RemainingAsync(), Is.Empty);
        await RetainEvidenceAsync(host, actor.BuildClient, output, secret);
        var files = Path.Combine(output, "published"); Directory.CreateDirectory(files);
        foreach (var artifact in artifacts.Where(a => a.Path.StartsWith("release/", StringComparison.Ordinal)))
        {
            var name = artifact.Path["release/".Length..]; Assert.That(name, Does.Not.Contain("/").And.Not.Contain("\\"));
            using var response = await actor.BuildClient.GetAsync("/app/rest/builds/id:" + root + "/artifacts/files/" + ArtifactPath(artifact.Path), HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode(); await using var stream = await response.Content.ReadAsStreamAsync();
            await using var saved = File.Create(Path.Combine(files, name)); await stream.CopyToAsync(saved); await saved.FlushAsync();
        }
        using var release = await JsonAsync(github, "repos/iXab3r/vivarium-us013-e2e/releases/tags/v" + version);
        Assert.That(release.RootElement.GetProperty("draft").GetBoolean(), Is.False);
        var remote = release.RootElement.GetProperty("assets").EnumerateArray().ToArray();
        var expected = artifacts.Where(a => a.Path.StartsWith("release/", StringComparison.Ordinal)).ToArray(); Assert.That(expected, Is.Not.Empty);
        Assert.That(remote.Select(a => a.GetProperty("name").GetString()), Is.EquivalentTo(expected.Select(a => a.Path[8..])));
        foreach (var asset in expected)
        {
            var name = asset.Path[8..]; var remoteAsset = remote.Single(a => a.GetProperty("name").GetString() == name);
            Assert.That(remoteAsset.GetProperty("digest").GetString(), Is.EqualTo("sha256:" + asset.Sha256));
            Assert.That(remoteAsset.GetProperty("size").GetInt64(), Is.EqualTo(asset.Length));
            await using var saved = File.OpenRead(Path.Combine(files, name)); Assert.That(Convert.ToHexStringLower(await SHA256.HashDataAsync(saved)), Is.EqualTo(asset.Sha256));
            using var request = new HttpRequestMessage(HttpMethod.Get, "repos/iXab3r/vivarium-us013-e2e/releases/assets/" + remoteAsset.GetProperty("id").GetInt64());
            request.Headers.Accept.ParseAdd("application/octet-stream");
            using var downloaded = await github.SendAsync(request, HttpCompletionOption.ResponseHeadersRead); downloaded.EnsureSuccessStatusCode();
            await using var remoteBytes = await downloaded.Content.ReadAsStreamAsync();
            Assert.That(Convert.ToHexStringLower(await SHA256.HashDataAsync(remoteBytes)), Is.EqualTo(asset.Sha256), "Actual GitHub downloaded bytes must match the build-owned artifact.");
        }
        var partial = artifacts.Single(a => a.Path == "publication-proof/partial.json");
        using var partialJson = JsonDocument.Parse(await actor.BuildClient.GetStringAsync("/app/rest/builds/id:" + root + "/artifacts/files/" + ArtifactPath(partial.Path)));
        Assert.That(partialJson.RootElement.GetProperty("draft").GetBoolean(), Is.True);
        Assert.That(partialJson.RootElement.GetProperty("assets").GetArrayLength(), Is.EqualTo(1));
        Assert.That(partialJson.RootElement.GetProperty("id").GetInt64(), Is.EqualTo(release.RootElement.GetProperty("id").GetInt64()), "The same actual draft is resumed.");
        var firstAsset = partialJson.RootElement.GetProperty("assets")[0];
        Assert.That(remote.Single(a => a.GetProperty("name").GetString() == firstAsset.GetProperty("name").GetString()).GetProperty("id").GetInt64(),
            Is.EqualTo(firstAsset.GetProperty("id").GetInt64()), "Recovery retains the first verified uploaded asset.");
        await host.RestartAsync();
        using var playwright = await Playwright.CreateAsync(); await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true, AcceptDownloads = true }); await actor.AddBrowserSessionAsync(context);
        var page = await context.NewPageAsync(); await page.GotoAsync(host.Origin + "builds/" + root);
        await page.GetByTestId("build-status").Filter(new() { HasText = "finished Â· SUCCESS" }).WaitForAsync();
        await page.GetByTestId("selected-revision").Filter(new() { HasText = revision }).WaitForAsync();
        var download = await page.RunAndWaitForDownloadAsync(() => page.GetByRole(AriaRole.Link, new() { Name = "release/manifest.json", Exact = true }).ClickAsync());
        var browserManifest = Path.Combine(output, "browser-manifest.json"); await download.SaveAsAsync(browserManifest);
        Assert.That(await File.ReadAllBytesAsync(browserManifest), Is.EqualTo(await File.ReadAllBytesAsync(Path.Combine(files, "manifest.json"))));
        await File.WriteAllTextAsync(Path.Combine(output, "receipt.json"), JsonSerializer.Serialize(new
        { kind = "ACTUAL_T3_BUILD_PACKAGE_AND_TEST_PUBLICATION", rootBuildId = root, version, revision, linuxId, windowsId,
            release = release.RootElement.Clone(), partialDraft = partialJson.RootElement.Clone(), assets = expected.Select(a => new { a.Path, a.Sha256, a.Length }),
            builtApplicationLaunched = false, unchangedCliEntry = true, nativeCompatibleAndBrowserReadbacks = true, hostRestartRetained = true }));
    }

    private static async Task SaveProgressAsync(R0AcceptanceInstallation.Case host, string directory, string revision)
    {
        await using var database = await DatabaseAsync(host);
        var states = await (from b in database.RuntimeBuilds join t in database.CatalogBuildTypes on b.BuildTypeId equals t.Id
            orderby b.Id select new { b.Id, configuration = t.ExternalId, b.Number, b.State, b.Status, b.StatusText, b.QueuedAt, b.StartedAt, b.FinishedAt }).ToArrayAsync();
        await File.WriteAllTextAsync(Path.Combine(directory, "progress.json"), JsonSerializer.Serialize(new { observedAt = DateTimeOffset.UtcNow, revision, builds = states }));
    }

    private static async Task RetainEvidenceAsync(R0AcceptanceInstallation.Case host, HttpClient actor, string output, string secret)
    {
        await using var database = await DatabaseAsync(host);
        var builds = await database.RuntimeBuilds.AsNoTracking().OrderBy(b => b.Id).ToArrayAsync();
        var executions = await database.RuntimeBuildExecutions.AsNoTracking().OrderBy(e => e.BuildId).ToArrayAsync();
        var results = await database.RuntimeBuildResults.AsNoTracking().OrderBy(r => r.BuildId).ToArrayAsync();
        var secretBytes = Encoding.UTF8.GetBytes(secret);
        foreach (var result in results) Assert.That(result.Evidence.AsSpan().IndexOf(secretBytes), Is.EqualTo(-1));
        var evidence = JsonSerializer.Serialize(new
        {
            builds = builds.Select(b => new { b.Id, b.BuildTypeId, b.Counter, b.Number, b.State, b.Status, b.StatusText, b.QueuedAt, b.StartedAt, b.FinishedAt }),
            executions = executions.Select(e => new { e.BuildId, e.AgentId, e.WorkloadId, e.LeaseFence, e.AssignedConnectionGeneration, e.CurrentConnectionGeneration,
                e.SelectionSnapshotJson, assignmentCipherSha256 = Convert.ToHexStringLower(SHA256.HashData(e.Assignment)) }),
            results = results.Select(r => new { r.BuildId, r.SemanticHash, evidenceBase64 = Convert.ToBase64String(r.Evidence) }),
            consumed = await database.RuntimeBuildConsumedArtifacts.AsNoTracking().ToArrayAsync(),
            source = await database.RuntimeBuildSourceSelections.AsNoTracking().ToArrayAsync()
        });
        Assert.That(evidence.Contains(secret, StringComparison.Ordinal), Is.False);
        await File.WriteAllTextAsync(Path.Combine(output, "execution-evidence.json"), evidence);
        foreach (var build in builds)
        {
            string? cursor = null; long retained = 0; var pageIndex = 0;
            await using var saved = File.Create(Path.Combine(output, "build-" + build.Id + ".log"));
            do
            {
                Assert.That(++pageIndex, Is.LessThanOrEqualTo(1024));
                using var page = JsonDocument.Parse(await actor.GetStringAsync($"/api/v1/builds/{build.Id}/output?limit=200" + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor))));
                foreach (var item in page.RootElement.GetProperty("items").EnumerateArray())
                {
                    if (!item.TryGetProperty("content", out var content)) continue;
                    var bytes = await actor.GetByteArrayAsync(content.GetProperty("href").GetString());
                    Assert.That(bytes.AsSpan().IndexOf(secretBytes), Is.EqualTo(-1));
                    retained += bytes.Length; Assert.That(retained, Is.LessThanOrEqualTo(64L * 1024 * 1024));
                    await saved.WriteAsync(bytes);
                }
                cursor = page.RootElement.GetProperty("page").GetProperty("nextCursor").GetString();
            } while (cursor is not null);
        }
    }

    private static async Task ConfigureStorageAsync(R0AcceptanceInstallation.Case host)
    {
        var settings = host.Services.GetRequiredService<NodeConfigurationService>();
        var storage = (await settings.GetAsync("vivarium-storage")).Value!; var document = storage.Config.DeepClone().AsObject();
        document["MaxObjectBytes"] = 2L * 1024 * 1024 * 1024;
        Assert.That((await settings.SaveAsync("vivarium-storage", Guid.NewGuid(), true, document, NodeConfigActor.System, storage.SavedRevision!.Id)).StatusCode, Is.EqualTo(200));
        await host.RestartAsync();
    }

    private static async Task ConfigureAsync(R0AcceptanceInstallation.Case host, HttpClient administrator, string csrf, JsonObject inputs,
        string sourceUrl, string linux, string windows)
    {
        static string S(string? value) => JsonSerializer.Serialize(value);
        static string A(IEnumerable<string> values) => "[" + string.Join(",", values) + "]";
        static string Text(JsonNode item, string key) => item[key]!.GetValue<string>();
        var configurations = new List<string>();
        foreach (var item in inputs["configurations"]!.AsArray().Select(c => c!))
        {
            var id = Text(item, "id");
            var steps = item["steps"]!.AsArray().Select(step => "new(" + S(Text(step!, "id")) + "," + S(Text(step!, "name")) + "," + S(Text(step!, "runner")) + "," +
                A(step!["properties"]!.AsArray().Select(p => "new(" + S(Text(p!, "name")) + "," + S(id.EndsWith("_Release", StringComparison.Ordinal) && Text(p!, "name") == "script.content" ? Publisher : Text(p!, "value")) + ",null)")) + ")");
            var requirements = A(["new(\"system.os.family\"," + S(id.EndsWith("_Windows", StringComparison.Ordinal) ? "windows" : "linux") + ")",
                "new(\"system.hostname\"," + S(id.EndsWith("_Windows", StringComparison.Ordinal) ? windows : linux) + ")"]);
            var snapshots = A(item["snapshots"]!.AsArray().Select(d => "new(" + S(Text(d!, "configuration")) + "," + S(Text(d!, "failure")) + "," + S(Text(d!, "cancel")) + ")"));
            var artifacts = A(item["artifacts"]!.AsArray().Select(d => "new(" + S(Text(d!, "configuration")) + "," + S(Text(d!, "selector")) + "," + S(Text(d!, "rules")) + ",true)"));
            var triggers = A(item["triggers"]!.AsArray().Select(t => "new(" + S(Text(t!, "type")) + "," + S(Text(t!, "branchFilter")) + ",60,\"USE_CUSTOM\"," + S(Text(t!, "rules")) + ")"));
            var parameters = A(item["parameters"]!.AsArray().Select(p => "new(" + S(Text(p!, "name")) + "," + S(Text(p!, "value")) + ",null)"));
            var name = id.EndsWith("_Windows", StringComparison.Ordinal) ? "Windows x64 / installer and static package checks" : Text(item, "name");
            configurations.Add("new(" + S(id) + "," + S(name) + "," + parameters + "," + A(steps) + "," + requirements + "," + snapshots + "," + artifacts + "," + item["timeoutMinutes"]!.GetValue<int>() + "," + triggers + ",\"ON_AGENT\")");
        }
        var source = inputs["source"]!;
        var declaration = "using Vivarium.Definitions; public static class Managed { public static Settings Declare() => new(\"P\",new(" + S(Text(source, "rootId")) + "," + S(sourceUrl) + "," + S(Text(source, "branch")) + "," + S(Text(source, "checkoutRules")) + "," + S(Text(source, "name")) + "),[]," + A(configurations) + "); }\n";
        await File.WriteAllTextAsync(Path.Combine(host.Definitions, ".vivarium/unbound/P/Settings.g.cs"), declaration);
        var control = Path.Combine(host.Definitions, ".vivarium/projects/P/Control.g.cs");
        await File.WriteAllTextAsync(control, (await File.ReadAllTextAsync(control)).Replace("Project P", "T3Personal (owned acceptance)", StringComparison.Ordinal));
        await R0AcceptanceCases.CommitCaseAsync(host, "fixture: actual T3 package-only three-member recipe with explicit owned Agent mapping");
        Assert.That((await VcsTriggerAcceptanceCases.ReconcileAsync(administrator, csrf)).GetProperty("state").GetString(), Is.EqualTo("applied"));
    }

    private static HttpClient GitHub(string secret)
    {
        var client = new HttpClient { BaseAddress = new Uri("https://api.github.com/"), Timeout = TimeSpan.FromMinutes(15) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Vivarium-US013-acceptance"); client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28"); return client;
    }
    private static async Task<JsonDocument> JsonAsync(HttpClient client, string path) => JsonDocument.Parse(await client.GetStringAsync(path));
    private static async Task CheckCredentialAsync(HttpClient github, string target, string output)
    {
        const string repo = "repos/iXab3r/vivarium-us013-e2e/";
        using var metadata = await JsonAsync(github, repo[..^1]); Assert.That(metadata.RootElement.GetProperty("full_name").GetString(), Is.EqualTo("iXab3r/vivarium-us013-e2e"));
        var name = "tags/vivarium-us013-preflight-" + Guid.NewGuid().ToString("N");
        using var bound = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            using var created = await github.PostAsJsonAsync(repo + "git/refs", new { @ref = "refs/" + name, sha = target }, bound.Token);
            Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.Created), "The dedicated credential must prove test-repository Contents write before the heavy build.");
        }
        finally
        {
            // A lost create response can still leave the owned ref. Inspect its exact identity first.
            using var inspect = await github.GetAsync(repo + "git/ref/" + name);
            if (inspect.StatusCode != HttpStatusCode.NotFound)
            {
                inspect.EnsureSuccessStatusCode(); using var actual = JsonDocument.Parse(await inspect.Content.ReadAsStringAsync());
                Assert.That(actual.RootElement.GetProperty("ref").GetString(), Is.EqualTo("refs/" + name));
                Assert.That(actual.RootElement.GetProperty("object").GetProperty("sha").GetString(), Is.EqualTo(target));
                using var deleted = await github.DeleteAsync(repo + "git/refs/" + name); Assert.That(deleted.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
            }
            using var absent = await github.GetAsync(repo + "git/ref/" + name); Assert.That(absent.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        }
        await File.WriteAllTextAsync(Path.Combine(output, "credential-preflight.json"), JsonSerializer.Serialize(new
        { repository = "iXab3r/vivarium-us013-e2e", ownedTemporaryRef = name, target, created = true, deleted = true, qualification = "Host fixture credential preparation, not Agent publication evidence" }));
    }
    private static async Task<JsonObject> InputsAsync() => JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(Repository(), "artifacts/us013-t3-recovery-v3/build-package-inputs.json")))!.AsObject();
    internal static string CreateWorkRoot()
    {
        var root = Path.Combine(Repository(), "artifacts", "w", Guid.NewGuid().ToString("N")[..8]);
        if (Directory.Exists(root)) throw new IOException("Owned workspace already exists.");
        Directory.CreateDirectory(root);
        return root;
    }
    private static string Repository()
    {
        var value = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (value is not null && !File.Exists(Path.Combine(value.FullName, "Vivarium.slnx"))) value = value.Parent;
        return value?.FullName ?? throw new InvalidDataException("Owned repository is absent.");
    }
    private static Task<VivariumDbContext> DatabaseAsync(R0AcceptanceInstallation.Case host) => host.Services.GetRequiredService<IDbContextFactory<VivariumDbContext>>().CreateDbContextAsync();
    private static string ArtifactPath(string path) => string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
    private const string Publisher = """
        #!/usr/bin/env bash
        set -euo pipefail
        node scripts/personal-release/run.mjs stage-release
        set +e
        VIVARIUM_US013_INTERRUPT_AFTER_FIRST_ASSET=true bash scripts/personal-release/publish.sh
        first=$?
        set -e
        [[ "$first" == 75 ]] || { echo 'The controlled partial-draft boundary was not reached.'; exit 76; }
        sleep 6
        bash scripts/personal-release/publish.sh
        bash scripts/personal-release/publish.sh
        """;
}
