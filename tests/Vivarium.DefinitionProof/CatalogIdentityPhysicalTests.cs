using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;

namespace Vivarium.DefinitionProof;

// D41 structural feasibility only: real committed C# -> owning catalogue rows -> immutable Build input.
// No scheduler, HTTP facade, authorization, activation engine or claimed TeamCity matrix behavior.
[NonParallelizable]
public sealed class CatalogIdentityPhysicalTests
{
    [Test]
    public async Task Builds_require_real_catalogue_and_committed_definition_without_a_legacy_payload_alias()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Vivarium.slnx"))) directory = directory.Parent;
        var root = directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
        var run = Path.Combine(root, "artifacts/us013-catalogue-identity-20261102", "physical-run-" + Guid.NewGuid().ToString("N"));
        var control = Path.Combine(run, "control");
        Directory.CreateDirectory(control);
        await SourceCompiler.GitAsync(control, "init", "-b", "main");
        await SourceCompiler.GitAsync(control, "config", "core.autocrlf", "false");
        await SourceCompiler.GitAsync(control, "config", "user.name", "Vivarium P0 proof");
        await SourceCompiler.GitAsync(control, "config", "user.email", "proof@example.invalid");
        var source = Path.Combine(root, "tools/definition-proof");
        File.Copy(Path.Combine(source, "Control.g.cs"), Path.Combine(control, "Control.g.cs"));
        var settings = Path.Combine(control, ".vivarium/unbound/T3Personal/Settings.g.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
        File.Copy(Path.Combine(source, "Settings.g.cs"), settings);
        File.Copy(Path.Combine(source, "UserCode.cs"), Path.Combine(Path.GetDirectoryName(settings)!, "UserCode.cs"));
        async Task<string> Commit(string message)
        {
            await SourceCompiler.GitAsync(control, "add", "--all");
            await SourceCompiler.GitAsync(control, "commit", "-m", "proof: " + message);
            return await SourceCompiler.GitAsync(control, "rev-parse", "HEAD");
        }
        var inputs = Directory.GetFiles(source).Where(path => Path.GetExtension(path) is ".cs" or ".py" or ".csproj")
            .Concat(Directory.GetFiles(Path.Combine(root, "tests/Vivarium.DefinitionProof"), "*.cs"))
            .Append(Path.Combine(root, "tests/Vivarium.DefinitionProof/Vivarium.DefinitionProof.csproj"))
            .Append(Path.Combine(root, "global.json")).Append(Path.Combine(root, "Directory.Build.props"))
            .ToDictionary(path => Path.GetRelativePath(root, path), Hash);
        var originalCommit = await Commit("unbound catalogue source");
        var compilerRuns = new List<object>();
        async Task<CompiledRevisionPair> CompileAsync(string commit)
        {
            // This fixture has one explicitly unbound owner. Read exact committed bytes;
            // never compile or evaluate its C# in a Host child before the container.
            var sources = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var name in new[] { "Model.cs", "Program.cs" })
                sources[name] = await File.ReadAllTextAsync(Path.Combine(source, name));
            foreach (var (path, name) in new[]
            {
                ("Control.g.cs", "Control.g.cs"),
                (".vivarium/unbound/T3Personal/Settings.g.cs", "Settings.g.cs"),
                (".vivarium/unbound/T3Personal/UserCode.cs", "UserCode.cs"),
            })
            {
                var committed = await ProofProcess.RunAsync("git", ["-C", control, "show", commit + ":" + path]);
                sources[name] = new UTF8Encoding(false, true).GetString(committed.Output);
            }
            var compiler = new IsolatedCompilerStudy();
            var evaluated = await compiler.EvaluateAsync(sources);
            Assert.Multiple(() =>
            {
                Assert.That(evaluated.ExitCode, Is.Zero, evaluated.Error);
                Assert.That(evaluated.DeadlineExceeded || evaluated.OutputExceeded || evaluated.OomKilled, Is.False);
            });
            var hash = IsolatedCompilerStudy.Hash(evaluated.Output);
            await File.WriteAllTextAsync(Path.Combine(run, commit + ".container.json"), evaluated.Container.GetRawText());
            compilerRuns.Add(new { commit, container = compiler.Name, planHash = hash,
                image = IsolatedCompilerStudy.Image, sdk = IsolatedCompilerStudy.Sdk, removed = true });
            return new(commit, "control-proof", commit, hash, evaluated.Output);
        }
        var original = await CompileAsync(originalCommit);
        using var plan = JsonDocument.Parse(original.Plan);
        var configurations = plan.RootElement.GetProperty("configurations").EnumerateArray().ToArray();
        Assert.That(plan.RootElement.GetProperty("project").GetProperty("id").GetString(), Is.EqualTo("T3Personal"));
        Assert.That(configurations.Select(c => c.GetProperty("id").GetString()), Is.EqualTo(new[]
            { "T3Personal_Prepare", "T3Personal_Windows", "T3Personal_Release" }));
        var postgres = new OwnedPostgres(); var passed = false;
        var observations = new List<object>();
        try
        {
            await postgres.StartAsync();
            async Task<object?> Query(string sql, params object[] values)
            {
                await using var connection = new NpgsqlConnection(postgres.ConnectionString);
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand(sql, connection);
                foreach (var value in values) command.Parameters.AddWithValue(value);
                return await command.ExecuteScalarAsync();
            }
            await Query("""
                CREATE TABLE study_catalog_project(
                    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                    external_id text NOT NULL UNIQUE);
                CREATE TABLE study_catalog_configuration(
                    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                    project_id bigint NOT NULL REFERENCES study_catalog_project(id),
                    external_id text NOT NULL UNIQUE, name text NOT NULL);
                CREATE TABLE study_catalog_definition(
                    configuration_id bigint NOT NULL REFERENCES study_catalog_configuration(id),
                    plan_hash text NOT NULL CHECK(plan_hash ~ '^[0-9a-f]{64}$'),
                    control_revision text NOT NULL CHECK(control_revision ~ '^[0-9a-f]{40}$'),
                    settings_revision text NOT NULL CHECK(settings_revision ~ '^[0-9a-f]{40}$'),
                    plan text NOT NULL, PRIMARY KEY(configuration_id,plan_hash));
                CREATE TABLE study_catalog_active(
                    configuration_id bigint PRIMARY KEY REFERENCES study_catalog_configuration(id), plan_hash text NOT NULL,
                    FOREIGN KEY(configuration_id,plan_hash) REFERENCES study_catalog_definition(configuration_id,plan_hash));
                CREATE TABLE study_catalog_build(
                    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY CHECK(id>0),
                    configuration_id bigint NOT NULL, plan_hash text NOT NULL,
                    display_number text NOT NULL, agent_workload_id text UNIQUE,
                    FOREIGN KEY(configuration_id,plan_hash) REFERENCES study_catalog_definition(configuration_id,plan_hash));
                CREATE FUNCTION immutable_study_catalog_definition() RETURNS trigger LANGUAGE plpgsql AS $$
                    BEGIN RAISE EXCEPTION 'Resolved definition is immutable' USING ERRCODE='23514'; END $$;
                CREATE TRIGGER immutable_study_catalog_definition BEFORE UPDATE ON study_catalog_definition
                    FOR EACH ROW EXECUTE FUNCTION immutable_study_catalog_definition();
                """);
            var projectId = (long)(await Query("INSERT INTO study_catalog_project(external_id) VALUES ($1) RETURNING id", "T3Personal"))!;
            var ids = new List<long>();
            foreach (var configuration in configurations)
            {
                var id = (long)(await Query("INSERT INTO study_catalog_configuration(project_id,external_id,name) VALUES ($1,$2,$3) RETURNING id",
                    projectId, configuration.GetProperty("id").GetString()!, configuration.GetProperty("name").GetString()!))!;
                ids.Add(id);
                await Query("INSERT INTO study_catalog_definition VALUES ($1,$2,$3,$4,$5)", id, original.PlanHash, original.ControlCommit, original.SettingsCommit, original.Plan);
                await Query("INSERT INTO study_catalog_active VALUES ($1,$2)", id, original.PlanHash);
            }
            async Task<long> Admit(long id, string number) => (long)(await Query("""
                INSERT INTO study_catalog_build(configuration_id,plan_hash,display_number)
                SELECT configuration_id,plan_hash,$2 FROM study_catalog_active WHERE configuration_id=$1 RETURNING id
                """, id, number))!;
            var builds = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => Admit(ids[0], "owned-" + i)));
            Assert.That(builds.Distinct().Count(), Is.EqualTo(4));
            Assert.That(builds.All(id => id > 0 && id <= 9007199254740991L), Is.True, "Small study allocations fit the pinned CLI, not an int64-wide client claim.");
            var oldBuild = builds[0]; var workload = "owned-opaque-workload-" + Guid.NewGuid().ToString("N");
            await Query("UPDATE study_catalog_build SET agent_workload_id=$2,display_number='owned-display' WHERE id=$1", oldBuild, workload);
            Assert.That(await Query("SELECT id FROM study_catalog_build WHERE agent_workload_id=$1", workload), Is.EqualTo(oldBuild));
            var absentConfiguration = Assert.ThrowsAsync<PostgresException>(async () => await Query(
                "INSERT INTO study_catalog_build(configuration_id,plan_hash,display_number) VALUES (999999,$1,'invalid')", original.PlanHash));
            Assert.That(absentConfiguration!.SqlState, Is.EqualTo(PostgresErrorCodes.ForeignKeyViolation));
            var absentProject = Assert.ThrowsAsync<PostgresException>(async () => await Query(
                "INSERT INTO study_catalog_configuration(project_id,external_id,name) VALUES (999999,'Invalid','Invalid')"));
            Assert.That(absentProject!.SqlState, Is.EqualTo(PostgresErrorCodes.ForeignKeyViolation));
            var missingDefinition = Assert.ThrowsAsync<PostgresException>(async () => await Query(
                "INSERT INTO study_catalog_build(configuration_id,plan_hash,display_number) VALUES ($1,$2,'invalid')", ids[0], new string('a', 64)));
            Assert.That(missingDefinition!.SqlState, Is.EqualTo(PostgresErrorCodes.ForeignKeyViolation));
            var missingOwner = Assert.ThrowsAsync<PostgresException>(async () => await Query(
                "INSERT INTO study_catalog_build(plan_hash,display_number) VALUES ($1,'invalid')", original.PlanHash));
            Assert.That(missingOwner!.SqlState, Is.EqualTo(PostgresErrorCodes.NotNullViolation));
            var originalSource = await File.ReadAllTextAsync(settings);
            var changedSource = originalSource.Replace("\"Prepare\", [", "\"Prepare v2\", [", StringComparison.Ordinal);
            Assert.That(changedSource, Is.Not.EqualTo(originalSource));
            await File.WriteAllTextAsync(settings, changedSource);
            var changedCommit = await Commit("edit real configuration name");
            var changed = await CompileAsync(changedCommit);
            Assert.That(changed.PlanHash, Is.Not.EqualTo(original.PlanHash));
            foreach (var id in ids)
            {
                await Query("INSERT INTO study_catalog_definition VALUES ($1,$2,$3,$4,$5)", id, changed.PlanHash, changed.ControlCommit, changed.SettingsCommit, changed.Plan);
                await Query("UPDATE study_catalog_active SET plan_hash=$2 WHERE configuration_id=$1", id, changed.PlanHash);
            }
            await Query("UPDATE study_catalog_configuration SET name='Prepare v2' WHERE id=$1", ids[0]);
            var freshBuild = await Admit(ids[0], "new-revision");
            Assert.That(await Query("SELECT plan_hash FROM study_catalog_build WHERE id=$1", freshBuild), Is.EqualTo(changed.PlanHash));
            Assert.That(await Query("SELECT plan_hash FROM study_catalog_build WHERE id=$1", oldBuild), Is.EqualTo(original.PlanHash));
            var rewrite = Assert.ThrowsAsync<PostgresException>(async () => await Query(
                "UPDATE study_catalog_definition SET plan='rewritten' WHERE configuration_id=$1 AND plan_hash=$2", ids[0], original.PlanHash));
            Assert.That(rewrite!.SqlState, Is.EqualTo(PostgresErrorCodes.CheckViolation));
            await postgres.RestartAsync();
            Assert.That(await Query("SELECT plan FROM study_catalog_definition WHERE configuration_id=$1 AND plan_hash=$2", ids[0], original.PlanHash), Is.EqualTo(original.Plan));
            Assert.That(await Query("SELECT id FROM study_catalog_build WHERE agent_workload_id=$1", workload), Is.EqualTo(oldBuild));
            Assert.That(await Query("SELECT count(*) FROM study_catalog_build b JOIN study_catalog_configuration c ON c.id=b.configuration_id JOIN study_catalog_project p ON p.id=c.project_id"), Is.EqualTo(5L));
            observations.Add(new { projectId, configurationIds = ids, builds, oldBuild, freshBuild, original.PlanHash, changedPlanHash = changed.PlanHash,
                retainedOldRevision = original.SettingsCommit, currentRevision = changed.SettingsCommit, postgresVersion = postgres.ServerVersion });
            passed = true;
        }
        finally
        {
            await postgres.DisposeAsync();
            foreach (var input in inputs) Assert.That(Hash(Path.Combine(root, input.Key)), Is.EqualTo(input.Value), input.Key);
            await File.WriteAllTextAsync(Path.Combine(run, "receipt.json"), JsonSerializer.Serialize(new
            {
                result = passed ? "PASS" : "FAIL", inputs, observations, compilerRuns,
                source = new { original.ControlCommit, original.SettingsRepository, original.SettingsCommit, original.PlanHash },
                limits = "Real committed Git -> D43 isolated Linux CSharp compilation/evaluation -> PG catalogue/immutable Build-input structural study only. No product Host/catalogue activation, authorization, source-timing/concurrent-activation, matrix execution, public adapter or Agent protocol behavior is proved."
            }, new JsonSerializerOptions { WriteIndented = true }));
            TestContext.Out.WriteLine("Catalogue identity receipt: " + Path.Combine(run, "receipt.json"));
        }
    }

    private static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
}
