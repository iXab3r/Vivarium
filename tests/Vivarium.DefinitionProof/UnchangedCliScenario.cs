using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace Vivarium.DefinitionProof;

/// <summary>Execute the published package as a separate process; no fetch/client substitutions.</summary>
internal sealed class UnchangedCliScenario(string root, Uri origin, string certificatePath)
{
    private readonly List<object> invocations = [];
    private readonly List<object> facts = [];
    private object? packageIdentity;
    private string? installed;
    private string? lastErrorCode;

    internal async Task AuthenticateAsync(string credential, string expectedUserName = "owned-scoped-user")
    {
        var repository = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "Vivarium.slnx"))) repository = repository.Parent;
        if (repository is null) throw new InvalidDataException("The proof repository is absent.");
        installed = Environment.GetEnvironmentVariable("VIVARIUM_PROOF_TEAMCITY_CLI")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm/node_modules/@eyeauras/teamcity-cli");
        var clientFiles = VerifyPackage(Path.Combine(repository.FullName, "artifacts/us013-cli056-20261102/eyeauras-teamcity-cli-0.5.6.tgz"),
            "7dfd8df5d3a5ac31dd7a389b7499551ef2512373bde2c9ac5d574b8dde863122", installed);
        var factoryFiles = VerifyPackage(Path.Combine(repository.FullName, "artifacts/us013-cli056-20261102/eyeauras-cli-factory-0.5.6.tgz"),
            "e2df5f9e99729d7ec5d6244bafbc404fffecb9c2de80f79dc910ca5cfb153a05", Path.GetFullPath(Path.Combine(installed, "../cli-factory")));
        packageIdentity = new { version = "0.5.6", clientFiles, factoryFiles,
            clientArchiveSha256 = "7dfd8df5d3a5ac31dd7a389b7499551ef2512373bde2c9ac5d574b8dde863122",
            factoryArchiveSha256 = "e2df5f9e99729d7ec5d6244bafbc404fffecb9c2de80f79dc910ca5cfb153a05" };
        using var certificate = X509CertificateLoader.LoadPkcs12FromFile(certificatePath, password: null);
        await File.WriteAllTextAsync(Path.Combine(root, "cli-ca.pem"), certificate.ExportCertificatePem());
        await RunAsync(credential, 0, "profile", "create", "owned-r0", "--url", origin.AbsoluteUri.TrimEnd('/'));
        await RunAsync(credential, 0, "-p", "owned-r0", "auth", "login");
        var status = await RunAsync(credential, 0, "-p", "owned-r0", "auth", "status");
        Assert.That(status.GetProperty("authenticated").GetBoolean(), Is.True);
        Assert.That(status.GetProperty("identity").ValueKind, Is.EqualTo(JsonValueKind.Object));
        Assert.That(status.GetProperty("identity").GetProperty("username").GetString(), Is.EqualTo(expectedUserName));
    }

    internal async Task RunBuildAsync(string credential, HttpClient native)
    {
        var server = await RunAsync(credential, 0, "-p", "owned-r0", "server", "status");
        Assert.That(server.GetProperty("version").GetString(), Is.EqualTo(Vivarium.Contracts.VivariumProductVersion.FromAssembly(typeof(Vivarium.Controller.Hosting.VivariumRuntime).Assembly)));
        Assert.That(server.GetProperty("webUrl").GetString(), Is.EqualTo(origin.AbsoluteUri.TrimEnd('/')));
        var project = await RunAsync(credential, 0, "-p", "owned-r0", "projects", "show", "P");
        Assert.That(project.GetProperty("id").GetString(), Is.EqualTo("P"));
        var configuration = await RunAsync(credential, 0, "-p", "owned-r0", "jobs", "show", "P_LocalSmoke");
        Assert.That(configuration.GetProperty("projectId").GetString(), Is.EqualTo("P"));
        await RunAsync(credential, 0, "-p", "owned-r0", "permissions", "grant", "Update");
        var run = await RunAsync(credential, 0, "-p", "owned-r0", "jobs", "run", "P_LocalSmoke", "--wait", "--timeout", "2m", "--interval", "1s");
        Assert.That(run.GetProperty("accepted").GetBoolean(), Is.True);
        Assert.That(run.GetProperty("outcome").GetString(), Is.EqualTo("succeeded"));
        var build = run.GetProperty("build");
        var id = build.GetProperty("id").GetInt64().ToString(CultureInfo.InvariantCulture);
        Assert.That(build.GetProperty("state").GetString(), Is.EqualTo("finished"));
        Assert.That(build.GetProperty("status").GetString(), Is.EqualTo("SUCCESS"));
        using var nativeBuild = JsonDocument.Parse(await native.GetStringAsync("/api/v1/builds/" + id));
        Assert.That(nativeBuild.RootElement.GetProperty("id").GetInt64(), Is.EqualTo(build.GetProperty("id").GetInt64()));
        Assert.That(nativeBuild.RootElement.GetProperty("number").GetString(), Is.EqualTo(build.GetProperty("number").GetString()));
        var files = await RunAsync(credential, 0, "-p", "owned-r0", "builds", "artifacts", "list", id, "--limit", "10");
        var artifact = files.GetProperty("items")[0];
        Assert.That(artifact.GetProperty("name").GetString(), Is.EqualTo("proof.txt"));
        Assert.That(artifact.GetProperty("modificationTime").GetString(), Does.Match("^[0-9]{8}T[0-9]{6}[+]0000$"));
        var download = await RunAsync(credential, 0, "-p", "owned-r0", "builds", "artifacts", "download", id, "proof.txt", "--output", "proof.txt");
        var saved = Path.GetFullPath(download.GetProperty("path").GetString()!);
        Assert.That(saved.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), Is.True);
        var actual = await File.ReadAllBytesAsync(saved);
        Assert.That(Encoding.UTF8.GetString(actual).TrimEnd('\r', '\n'), Is.EqualTo("owned-smoke-artifact"));
        Assert.That(Convert.ToHexStringLower(SHA256.HashData(actual)), Is.EqualTo(download.GetProperty("sha256").GetString()));
        using var manifest = JsonDocument.Parse(await native.GetStringAsync("/api/v1/builds/" + id + "/artifacts"));
        var metadata = manifest.RootElement.GetProperty("items")[0];
        Assert.That(metadata.GetProperty("sha256").GetString(), Is.EqualTo(download.GetProperty("sha256").GetString()));
        Assert.That(await native.GetByteArrayAsync(metadata.GetProperty("href").GetString()), Is.EqualTo(actual));
        var failed = await RunAsync(credential, 1, "-p", "owned-r0", "jobs", "run", "P_LocalSmoke", "--param", "env.MODE=fail", "--wait", "--timeout", "2m", "--interval", "1s");
        Assert.That(lastErrorCode, Is.EqualTo("build.failed"));
        Assert.That(failed.GetProperty("outcome").GetString(), Is.EqualTo("failed"));
        var failedBuild = failed.GetProperty("build");
        Assert.That(failedBuild.GetProperty("state").GetString(), Is.EqualTo("finished"));
        Assert.That(failedBuild.GetProperty("status").GetString(), Is.EqualTo("FAILURE"));
        Assert.That(failedBuild.GetProperty("statusText").GetString(), Is.EqualTo("Exit code 7 (Step: Echo (Command Line)) (new)"));
        using var nativeFailed = JsonDocument.Parse(await native.GetStringAsync("/api/v1/builds/" + failedBuild.GetProperty("id").GetInt64()));
        Assert.That(nativeFailed.RootElement.GetProperty("status").GetString(), Is.EqualTo("FAILURE"));
        foreach (var (mode, exit, text) in new[]
        {
            ("fail", 1, "Exit code 7 (Step: Echo (Command Line))"),
            ("success", 0, "Success"),
            ("fail", 1, "Exit code 7 (Step: Echo (Command Line)) (new)"),
            ("fail8", 1, "Exit code 8 (Step: Echo (Command Line)) (new)"),
            ("fail8", 1, "Exit code 8 (Step: Echo (Command Line))"),
        })
        {
            var next = await RunAsync(credential, exit, "-p", "owned-r0", "jobs", "run", "P_LocalSmoke", "--param", "env.MODE=" + mode,
                "--wait", "--timeout", "2m", "--interval", "1s");
            Assert.That(lastErrorCode, Is.EqualTo(exit == 0 ? null : "build.failed"), "A sequential run must reach its actual terminal outcome.");
            Assert.That(next.GetProperty("build").GetProperty("statusText").GetString(), Is.EqualTo(text));
        }
        facts.Add(new { kind = "SUCCESS_FAILURE_ARTIFACT", successBuildId = build.GetProperty("id").GetInt64(),
            failedBuildId = failedBuild.GetProperty("id").GetInt64(), artifactSha256 = download.GetProperty("sha256").GetString(),
            nativeAndCompatibleIdsAndBytesMatch = true });
        await SaveAsync();
    }

    internal async Task<long> RunWholeChainAsync(string credential, string? runInput = null)
    {
        await RunAsync(credential, 0, "-p", "owned-r0", "permissions", "grant", "Update");
        var arguments = new List<string> { "-p", "owned-r0", "jobs", "run", "P_Release",
            "--wait", "--timeout", "2m", "--interval", "1s" };
        if (runInput is not null) arguments.AddRange(["--param", "vivarium.run-input=" + runInput]);
        var result = await RunAsync(credential, 0, arguments.ToArray());
        Assert.That(result.GetProperty("accepted").GetBoolean(), Is.True);
        Assert.That(result.GetProperty("outcome").GetString(), Is.EqualTo("succeeded"));
        var build = result.GetProperty("build");
        Assert.That(build.GetProperty("buildTypeId").GetString(), Is.EqualTo("P_Release"));
        Assert.That(build.GetProperty("state").GetString(), Is.EqualTo("finished"));
        var id = build.GetProperty("id").GetInt64();
        var downloaded = await RunAsync(credential, 0, "-p", "owned-r0", "builds", "artifacts", "download",
            id.ToString(CultureInfo.InvariantCulture), "release.txt", "--output", "chain-release.txt");
        var saved = Path.GetFullPath(downloaded.GetProperty("path").GetString()!);
        Assert.That(saved.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), Is.True);
        Assert.That(await File.ReadAllBytesAsync(saved), Is.EqualTo("coherent-reference-version\n"u8.ToArray()));
        Assert.That(downloaded.GetProperty("sha256").GetString(), Is.EqualTo("97dc31e36da90b592250f4e30e01d0364a3abcafa36d57fccaccbaedbd6629c8"));
        return id;
    }

    internal async Task<long> CancelQueuedAsync(string credential, HttpClient native)
    {
        var run = await RunAsync(credential, 0, "-p", "owned-r0", "jobs", "run", "P_LocalSmoke");
        var build = run.GetProperty("build"); var id = build.GetProperty("id").GetInt64();
        Assert.That(build.GetProperty("state").GetString(), Is.EqualTo("queued"));
        var cancelled = await RunAsync(credential, 0, "-p", "owned-r0", "queue", "cancel", id.ToString(CultureInfo.InvariantCulture));
        Assert.That(cancelled.GetProperty("id").GetInt64(), Is.EqualTo(id));
        Assert.That(cancelled.GetProperty("state").GetString(), Is.EqualTo("finished"));
        Assert.That(cancelled.GetProperty("status").GetString(), Is.EqualTo("UNKNOWN"));
        Assert.That(cancelled.GetProperty("number").GetString(), Is.EqualTo("N/A"));
        Assert.That(cancelled.GetProperty("statusText").GetString(), Is.EqualTo("Canceled"));
        using var retained = JsonDocument.Parse(await native.GetStringAsync("/api/v1/builds/" + id));
        Assert.That(retained.RootElement.GetProperty("agentId").ValueKind, Is.EqualTo(JsonValueKind.Null));
        facts.Add(new { kind = "QUEUED_CANCELLED_WITHOUT_ASSIGNMENT", buildId = id }); await SaveAsync();
        return id;
    }

    internal Task<JsonElement> RunAsync(string credential, int expectedExit, params string[] arguments) =>
        RunAsync(credential, expectedExit, TimeSpan.FromMinutes(3), arguments);

    internal Task<JsonElement> RunAsync(string credential, int expectedExit, TimeSpan processDeadline, params string[] arguments) =>
        RunAsync(credential, expectedExit, processDeadline, CancellationToken.None, arguments);

    internal async Task<JsonElement> RunAsync(string credential, int expectedExit, TimeSpan processDeadline, CancellationToken cancellationToken, params string[] arguments)
    {
        var environment = new Dictionary<string, string>
        {
            ["APPDATA"] = Path.Combine(root, "cli-state/roaming"), ["LOCALAPPDATA"] = Path.Combine(root, "cli-state/local"),
            ["XDG_CONFIG_HOME"] = Path.Combine(root, "cli-state/config"), ["XDG_DATA_HOME"] = Path.Combine(root, "cli-state/data"),
            ["TEAMCITY_TOKEN"] = credential, ["NODE_EXTRA_CA_CERTS"] = Path.Combine(root, "cli-ca.pem"),
        };
        var result = await ProofProcess.RunAsync("node", new[] { Path.Combine(installed!, "dist/src/bin.js"), "--json" }.Concat(arguments),
            root, environment, allowFailure: true, deadline: processDeadline, cancellationToken: cancellationToken);
        Assert.That(Encoding.UTF8.GetString(result.Output).Contains(credential, StringComparison.Ordinal), Is.False);
        Assert.That(Encoding.UTF8.GetString(result.Error).Contains(credential, StringComparison.Ordinal), Is.False);
        using var document = JsonDocument.Parse(result.Output.Length == 0 ? result.Error : result.Output);
        var value = document.RootElement;
        lastErrorCode = null;
        if (result.ExitCode != 0)
        {
            using var failure = JsonDocument.Parse(result.Error);
            lastErrorCode = failure.RootElement.GetProperty("error").GetProperty("code").GetString();
        }
        var buildResponse = value.TryGetProperty("build", out var returnedBuild) ? returnedBuild
            : arguments.Contains("builds") || arguments.Contains("queue") ? value : default;
        long? returnedBuildId = buildResponse.ValueKind == JsonValueKind.Object && buildResponse.TryGetProperty("id", out var returnedId)
            && returnedId.ValueKind == JsonValueKind.Number && returnedId.TryGetInt64(out var numericId) ? numericId : null;
        string? BuildString(string name) => buildResponse.ValueKind == JsonValueKind.Object && buildResponse.TryGetProperty(name, out var field)
            && field.ValueKind == JsonValueKind.String ? field.GetString() : null;
        invocations.Add(new { arguments, exitCode = result.ExitCode, returnedBuildId, errorCode = lastErrorCode,
            buildState = BuildString("state"), buildStatus = BuildString("status"), buildStatusText = BuildString("statusText"),
            stdoutSha256 = Convert.ToHexStringLower(SHA256.HashData(result.Output)),
            stderrSha256 = Convert.ToHexStringLower(SHA256.HashData(result.Error)) });
        if (result.ExitCode != expectedExit)
            TestContext.Progress.WriteLine("Published CLI failure: " + Encoding.UTF8.GetString(result.Error));
        await SaveAsync();
        Assert.That(result.ExitCode, Is.EqualTo(expectedExit), "Published CLI command: " + string.Join(' ', arguments));
        return value.Clone();
    }

    private async Task SaveAsync()
    {
        var evidence = Path.Combine(TestContext.CurrentContext.WorkDirectory, "artifacts/r0-host/cli-process-" + Path.GetFileName(root) + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(evidence)!);
        await File.WriteAllTextAsync(evidence, JsonSerializer.Serialize(new { kind = "UNCHANGED_PUBLISHED_CLI_PROCESS_INVOCATIONS", packageIdentity, invocations, facts }));
    }

    private static int VerifyPackage(string archive, string expectedHash, string installed)
    {
        Assert.That(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(archive))), Is.EqualTo(expectedHash));
        using var compressed = File.OpenRead(archive);
        using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);
        var count = 0;
        while (reader.GetNextEntry() is { } entry)
        {
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)) continue;
            if (!entry.Name.StartsWith("package/", StringComparison.Ordinal) || entry.Name.Split('/').Contains(".."))
                throw new InvalidDataException("The pinned package entry is invalid.");
            using var content = new MemoryStream(); entry.DataStream!.CopyTo(content);
            Assert.That(SHA256.HashData(File.ReadAllBytes(Path.Combine(installed, entry.Name[8..]))),
                Is.EqualTo(SHA256.HashData(content.ToArray())), "Published installed package differs: " + entry.Name);
            count++;
        }
        Assert.That(count, Is.GreaterThan(100));
        return count;
    }
}
