using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Npgsql;

namespace Vivarium.DefinitionProof;

[NonParallelizable]
public sealed class RevisionSetPhysicalTests
{
    private readonly OwnedPostgres postgres = new();
    private string root = "", run = "", fixture = "", dotnet = "";
    private JsonDocument receipt = null!;
    private readonly List<object> observations = [];
    private readonly List<object> outcomes = [];
    private Dictionary<string, string> inputs = [];

    [OneTimeSetUp]
    public async Task StartAsync()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Vivarium.slnx"))) directory = directory.Parent;
        root = directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
        dotnet = Path.Combine(root, "artifacts/p0-rest-first/tooling/dotnet/dotnet.exe");
        run = Path.Combine(root, "artifacts/us013-p0", "physical-run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(run);
        inputs = Directory.GetFiles(Path.Combine(root, "tests/Vivarium.DefinitionProof"), "*.cs")
            .Concat(Directory.GetFiles(Path.Combine(root, "tools/definition-proof"), "*", SearchOption.TopDirectoryOnly))
            .Where(p => Path.GetExtension(p) is ".cs" or ".py" or ".csproj")
            .Append(Path.Combine(root, "tests/Vivarium.DefinitionProof/Vivarium.DefinitionProof.csproj"))
            .Append(Path.Combine(root, "global.json")).Append(Path.Combine(root, "Directory.Build.props"))
            .ToDictionary(p => Path.GetRelativePath(root, p), Hash);
        try
        {
            var source = await ProofProcess.RunAsync("python", ["tools/definition-proof/probe.py"], root,
                deadline: TimeSpan.FromMinutes(3));
            await File.WriteAllBytesAsync(Path.Combine(run, "source-probe-output.json"), source.Output);
            using var result = JsonDocument.Parse(source.Output);
            var receiptPath = result.RootElement.GetProperty("receipt").GetString()!;
            fixture = Path.GetDirectoryName(receiptPath)!;
            receipt = JsonDocument.Parse(await File.ReadAllTextAsync(receiptPath));
            await postgres.StartAsync();
            await new RevisionSetStudy(postgres.ConnectionString, new(root, "", "", "")).CreateSchemaAsync();
        }
        catch { await postgres.DisposeAsync(); throw; }
    }

    private static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    private JsonElement Event(string name) => receipt.RootElement.GetProperty("events").EnumerateArray().Single(e => e.GetProperty("name").GetString() == name);
    private string Control(string name) => Event(name).GetProperty("control").GetString()!;
    private string Product(string name) => Event(name).GetProperty("settings").GetString()!;

    private async Task<StudyResources> RepositoriesAsync(bool bound = true)
    {
        var path = Path.Combine(run, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        foreach (var name in new[] { "control", "product" })
            await ProofProcess.RunAsync("git", ["clone", "--bare", "--no-hardlinks", Path.Combine(fixture, name), Path.Combine(path, name + ".git")]);
        var resources = new StudyResources(root, Path.Combine(path, "control.git"), Path.Combine(path, "product.git"), Path.Combine(path, "evaluation"));
        await SourceCompiler.GitAsync(resources.Control, "update-ref", "refs/heads/main", Control(bound ? "bound-three" : "three-unbound"));
        var product = bound ? Product("bound-three") : await SourceCompiler.GitAsync(resources.Product, "rev-list", "--max-parents=0", "main");
        await SourceCompiler.GitAsync(resources.Product, "update-ref", "refs/heads/main", product);
        await File.WriteAllTextAsync(Path.Combine(path, "resources.json"), JsonSerializer.Serialize(resources));
        return resources;
    }

    private (Process Child, Task<ProofProcessResult> Result) StartWorker(StudyResources resources, Guid operation)
    {
        var child = ProofProcess.Start(dotnet, [typeof(RevisionSetPhysicalTests).Assembly.Location, "--worker",
            Path.Combine(Path.GetDirectoryName(resources.Control)!, "resources.json"), operation.ToString()], root,
            new Dictionary<string, string> { ["VIVARIUM_PROOF_PG"] = postgres.ConnectionString });
        return (child, ProofProcess.ObserveAsync(child, allowFailure: true, deadline: TimeSpan.FromMinutes(3)));
    }

    private async Task ApplyAsync(StudyResources resources, StudyOperation operation, string expected = "APPLIED")
    {
        var worker = StartWorker(resources, operation.Id);
        using var child = worker.Child;
        var result = await worker.Result;
        Assert.That(result.ExitCode, Is.Zero, System.Text.Encoding.UTF8.GetString(result.Error));
        Assert.That(result.Text, Is.EqualTo(expected));
    }

    private async Task<(RevisionSetStudy Study, string Scope, ActiveStudyRevision Active)> SeedAsync(StudyResources resources)
    {
        var study = new RevisionSetStudy(postgres.ConnectionString, resources);
        var scope = Guid.NewGuid().ToString("N");
        var initial = await study.PrepareAsync(scope, await SourceCompiler.GitAsync(resources.Control, "rev-parse", "main"),
            await SourceCompiler.GitAsync(resources.Product, "rev-parse", "main"));
        await ApplyAsync(resources, initial);
        return (study, scope, (await study.ReadActiveAsync(scope))!);
    }

    private async Task<long> CountAsync(string sql, object value)
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(value);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task UntilAsync(Func<Task<bool>> predicate)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        while (!await predicate()) await Task.Delay(50, deadline.Token);
    }

    [Test]
    public async Task Source_edits_reset_and_database_restart_preserve_exact_revision_members()
    {
        var resources = await RepositoriesAsync();
        var (study, scope, _) = await SeedAsync(resources);
        foreach (var name in new[] { "overrides", "reset" })
        {
            var operation = await study.PrepareAsync(scope, Control(name), Product(name));
            await ApplyAsync(resources, operation);
            await ApplyAsync(resources, operation); // Lost successful response: cold retry, not a second activation.
            var active = (await study.ReadActiveAsync(scope))!;
            Assert.That(active.Control, Is.EqualTo(Control(name)));
            Assert.That(active.Settings, Is.EqualTo(Product(name)));
            Assert.That(active.SettingsRepository, Is.EqualTo("product-proof"));
            Assert.That(active.PlanHash, Is.EqualTo(Event(name).GetProperty("effectiveSha256").GetString()));
            Assert.That(await CountAsync("SELECT count(*) FROM study_audit WHERE operation_id=$1 AND event='APPLIED'", operation.Id), Is.EqualTo(1));
        }
        var before = await study.ReadActiveAsync(scope);
        await postgres.RestartAsync();
        Assert.That(await new RevisionSetStudy(postgres.ConnectionString, resources).ReadActiveAsync(scope), Is.EqualTo(before));
        Assert.That(await CountAsync("SELECT count(*) FROM study_history WHERE scope=$1", scope), Is.EqualTo(3));
        observations.Add(new { scenario = "source-reset-cold-retry-pg-restart", active = before });
    }

    [Test]
    public async Task Binding_switch_changes_the_owner_without_changing_configuration_identity()
    {
        var resources = await RepositoriesAsync(bound: false);
        var (study, scope, before) = await SeedAsync(resources);
        Assert.That(before.SettingsRepository, Is.EqualTo("control-proof"));
        var preparation = await study.PrepareAsync(scope, Control("three-unbound"), Product("bound-three"));
        await ApplyAsync(resources, preparation);
        Assert.That((await study.ReadActiveAsync(scope))!.PlanHash, Is.EqualTo(before.PlanHash));
        var binding = await study.PrepareAsync(scope, Control("bound-three"), Product("bound-three"));
        await ApplyAsync(resources, binding);
        var after = (await study.ReadActiveAsync(scope))!;
        Assert.That(after.SettingsRepository, Is.EqualTo("product-proof"));
        using var oldPlan = JsonDocument.Parse(before.Plan);
        using var newPlan = JsonDocument.Parse(after.Plan);
        Assert.That(newPlan.RootElement.GetProperty("configurations").GetRawText(),
            Is.EqualTo(oldPlan.RootElement.GetProperty("configurations").GetRawText()));
        var tree = await SourceCompiler.GitAsync(resources.Control, "ls-tree", "-r", "--name-only", "main");
        Assert.That(tree, Does.Not.Contain("Settings.g.cs"));
        observations.Add(new { scenario = "prepared-then-bound", before, after });
    }

    [Test]
    public async Task Invalid_external_source_and_duplicate_identity_keep_the_last_working_projection()
    {
        var resources = await RepositoriesAsync();
        var (study, scope, before) = await SeedAsync(resources);
        foreach (var field in new[] { "invalidRevision", "collisionRevision" })
        {
            var revision = receipt.RootElement.GetProperty(field).GetString()!;
            // External Git has already advanced. Reconciliation must not pretend the bad head is active.
            await SourceCompiler.GitAsync(resources.Product, "update-ref", "refs/heads/main", revision);
            var operation = await study.PrepareAsync(scope, before.Control, revision);
            await ApplyAsync(resources, operation, "REJECTED");
            Assert.That(await study.ReadActiveAsync(scope), Is.EqualTo(before));
            Assert.That(await SourceCompiler.GitAsync(resources.Product, "rev-parse", "main"), Is.EqualTo(revision));
            Assert.That(await CountAsync("SELECT count(*) FROM study_audit WHERE operation_id=$1 AND event='APPLIED'", operation.Id), Is.Zero);
        }
        observations.Add(new { scenario = "invalid-external-heads", active = before });
    }

    [TestCase("before-product-ref")]
    [TestCase("between-refs")]
    [TestCase("before-projection")]
    [TestCase("uncommitted-projection")]
    public async Task Killed_worker_resumes_one_durable_operation(string boundary)
    {
        var resources = await RepositoriesAsync(bound: false);
        var (study, scope, before) = await SeedAsync(resources);
        var operation = await study.PrepareAsync(scope, Control("bound-three"), Product("bound-three"));
        string? lockPath = null;
        FileStream? gitLock = null;
        await using var blocker = new NpgsqlConnection(postgres.ConnectionString);
        await blocker.OpenAsync();
        NpgsqlTransaction? transaction = null;
        if (boundary is "before-product-ref" or "between-refs")
        {
            lockPath = Path.Combine(boundary == "before-product-ref" ? resources.Product : resources.Control, "refs/heads/main.lock");
            gitLock = new FileStream(lockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        }
        else
        {
            transaction = await blocker.BeginTransactionAsync();
            await using var command = new NpgsqlCommand(boundary == "before-projection"
                ? "SELECT version FROM study_scopes WHERE scope=$1 FOR UPDATE"
                : "LOCK TABLE study_audit IN ACCESS EXCLUSIVE MODE", blocker, transaction);
            if (boundary == "before-projection") command.Parameters.AddWithValue(scope);
            await command.ExecuteNonQueryAsync();
        }
        var worker = StartWorker(resources, operation.Id);
        using var child = worker.Child;
        try
        {
            if (boundary == "before-product-ref")
                await UntilAsync(async () => (await study.ReadOperationAsync(operation.Id)).State == "VALIDATED" &&
                    await SourceCompiler.GitAsync(resources.Product, "rev-parse", "main") == operation.BaseProduct);
            else if (boundary == "between-refs")
                await UntilAsync(async () => (await study.ReadOperationAsync(operation.Id)).State == "PRODUCT_REF" &&
                    await SourceCompiler.GitAsync(resources.Product, "rev-parse", "main") == operation.TargetProduct);
            else
                await UntilAsync(async () => (await study.ReadOperationAsync(operation.Id)).State == "REFS" &&
                    await CountAsync("SELECT count(*) FROM pg_stat_activity WHERE application_name=$1 AND wait_event_type='Lock'",
                        "definition-study-" + operation.Id) == 1);
            child.Kill(entireProcessTree: true);
            var killed = await worker.Result;
            Assert.That(killed.ExitCode, Is.Not.Zero);
        }
        finally
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            await worker.Result;
            gitLock?.Dispose();
            if (lockPath is not null) File.Delete(lockPath); // Only the lock created by this test.
            if (transaction is not null) { await transaction.RollbackAsync(); await transaction.DisposeAsync(); }
        }
        Assert.That(await study.ReadActiveAsync(scope), Is.EqualTo(before));
        Assert.That(await CountAsync("SELECT count(*) FROM study_history WHERE scope=$1", scope), Is.EqualTo(1));
        await ApplyAsync(resources, operation);
        await ApplyAsync(resources, operation);
        var active = (await study.ReadActiveAsync(scope))!;
        Assert.That(active.Version, Is.EqualTo(before.Version + 1));
        Assert.That(active.Control, Is.EqualTo(operation.TargetControl));
        Assert.That(active.Settings, Is.EqualTo(operation.TargetProduct));
        Assert.That(await CountAsync("SELECT count(*) FROM study_history WHERE scope=$1", scope), Is.EqualTo(2));
        Assert.That(await CountAsync("SELECT count(*) FROM study_audit WHERE operation_id=$1 AND event='APPLIED'", operation.Id), Is.EqualTo(1));
        observations.Add(new { scenario = boundary, operation = operation.Id, before, active });
    }

    [Test]
    public async Task Interfering_git_writer_is_not_overwritten()
    {
        var resources = await RepositoriesAsync();
        var (study, scope, before) = await SeedAsync(resources);
        var operation = await study.PrepareAsync(scope, before.Control, Product("overrides"));
        await SourceCompiler.GitAsync(resources.Product, "update-ref", "refs/heads/main", Product("reset"));
        await ApplyAsync(resources, operation, "CONFLICT");
        Assert.That(await study.ReadActiveAsync(scope), Is.EqualTo(before));
        Assert.That(await SourceCompiler.GitAsync(resources.Product, "rev-parse", "main"), Is.EqualTo(Product("reset")));
        observations.Add(new { scenario = "external-ref-conflict", active = before });
    }

    [OneTimeTearDown]
    public async Task StopAsync()
    {
        try
        {
            if (run.Length != 0)
            {
                var stable = inputs.All(p => File.Exists(Path.Combine(root, p.Key)) && Hash(Path.Combine(root, p.Key)) == p.Value);
                await File.WriteAllTextAsync(Path.Combine(run, "receipt.json"), JsonSerializer.Serialize(new
                {
                    status = "PHYSICAL_FEASIBILITY_ONLY", postgres = postgres.ServerVersion,
                    sourceReceipt = fixture.Length == 0 ? null : Path.Combine(fixture, "receipt.json"),
                    inputsStable = stable, inputs, observations, outcomes,
                    limits = "Trusted fixtures; bare Git CAS and isolated PostgreSQL study schema. No production Host, HTTP, authorization, Chiral, worktree recovery or complete concurrent-writer fencing proof."
                }, new JsonSerializerOptions { WriteIndented = true }));
                TestContext.Progress.WriteLine("Physical study receipt: " + Path.Combine(run, "receipt.json"));
                Assert.That(stable, Is.True, "Proof inputs changed during execution.");
            }
        }
        finally { receipt?.Dispose(); await postgres.DisposeAsync(); }
    }

    [TearDown]
    public void RecordOutcome() => outcomes.Add(new
    {
        test = TestContext.CurrentContext.Test.Name,
        outcome = TestContext.CurrentContext.Result.Outcome.Status.ToString(),
    });
}
