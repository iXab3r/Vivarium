using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Vivarium.Controller.Catalog;
using Vivarium.Controller.Identity;
using Vivarium.Controller.Persistence;
using Vivarium.Controller.Security;

namespace Vivarium.DefinitionProof;

[NonParallelizable]
public sealed class ProductionCatalogPhysicalTests
{
    [Test]
    public async Task Committed_CSharp_activates_atomically_and_rejected_or_revoked_changes_keep_the_prior_plan()
    {
        await using var postgres = new OwnedPostgres();
        await postgres.StartAsync();
        var options = new DbContextOptionsBuilder<VivariumDbContext>().UseNpgsql(postgres.ConnectionString).Options;
        var factory = new PooledDbContextFactory<VivariumDbContext>(options);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var actorId = Guid.NewGuid();
        var actor = new ManagementRequestContext(new("user", actorId.ToString(), "physical-framework-test", null),
            Guid.NewGuid().ToString(), null, "owned-catalogue-acceptance");
        await using (var database = await factory.CreateDbContextAsync())
        {
            await PostgresSchema.MigrateAsync(database);
            database.Users.Add(new VivariumUser { Id = actorId, UserName = "catalogue-operator", IsEnabled = true });
            database.UserRoleGrants.Add(new UserRoleGrant { UserId = actorId, RoleId = "SYSTEM_ADMIN", Scope = "g" });
            await database.SaveChangesAsync();
        }
        var root = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.WorkDirectory,
            "artifacts/r0-catalogue/production-" + Guid.NewGuid().ToString("N")));
        var repository = Path.Combine(root, "control");
        Directory.CreateDirectory(repository);
        await SourceCompiler.GitAsync(repository, "init", "-b", "main");
        await SourceCompiler.GitAsync(repository, "config", "core.autocrlf", "false");
        await SourceCompiler.GitAsync(repository, "config", "user.name", "Owned Vivarium catalogue fixture");
        await SourceCompiler.GitAsync(repository, "config", "user.email", "fixture@example.invalid");
        var source = ProductionEvaluatorPhysicalTests.Sources();
        async Task WriteAsync(string relative, string value)
        {
            var path = Path.Combine(repository, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, value);
        }
        await WriteAsync(".vivarium/repository.yaml", """
            apiVersion: vivarium.io/v1alpha1
            kind: Repository
            id: local
            spec:
              schemaVersion: "2"
              documentKinds:
                - Project
            """);
        await WriteAsync(".vivarium/projects/P/Control.g.cs", source["Control.g.cs"]);
        await WriteAsync(".vivarium/unbound/P/Settings.g.cs", source["Settings.g.cs"]);
        await WriteAsync(".vivarium/unbound/P/UserCode.cs", source["UserCode.cs"]);
        async Task<string> CommitAsync(string message)
        {
            await SourceCompiler.GitAsync(repository, "add", "--all");
            await SourceCompiler.GitAsync(repository, "commit", "-m", message);
            return await SourceCompiler.GitAsync(repository, "rev-parse", "HEAD");
        }
        var firstCommit = await CommitAsync("fixture: first real Git definition");
        var service = new CatalogService(factory, configuration, new("local", repository, []), TimeProvider.System);
        var first = await service.ReconcileAsync(actor);
        Assert.That(first.State, Is.EqualTo("APPLIED"), first.FailureCode);
        Guid firstRevision;
        long projectIdentity, buildTypeIdentity;
        string original;
        await using (var database = await factory.CreateDbContextAsync())
        {
            var active = await database.ActiveCatalogs.SingleAsync();
            firstRevision = active.RevisionId;
            Assert.That(active.LastKnownGoodRevisionId, Is.EqualTo(firstRevision));
            var revision = await database.CatalogRevisions.SingleAsync();
            Assert.That(revision.ControlCommit, Is.EqualTo(firstCommit));
            original = revision.DefinitionJson;
            var owner = await database.CatalogProjects.SingleAsync(p => p.ExternalId == "P");
            var type = await database.CatalogBuildTypes.SingleAsync();
            projectIdentity = owner.Id; buildTypeIdentity = type.Id;
            Assert.That(projectIdentity, Is.Positive);
            Assert.That(buildTypeIdentity, Is.Positive);
            Assert.That(type.ProjectId, Is.EqualTo(projectIdentity));
            var pinned = await database.CatalogBuildDefinitions.SingleAsync();
            Assert.That(pinned.BuildTypeId, Is.EqualTo(buildTypeIdentity));
            Assert.That(pinned.ProjectId, Is.EqualTo(projectIdentity));
            Assert.That(pinned.RevisionId, Is.EqualTo(firstRevision));
            using var parsed = JsonDocument.Parse(original);
            var project = parsed.RootElement.GetProperty("projects")[0];
            Assert.That(project.GetProperty("control").GetProperty("id").GetString(), Is.EqualTo("P"));
            Assert.That(project.GetProperty("definition").GetProperty("configurations")[0].GetProperty("steps")[0]
                .GetProperty("properties")[0].GetProperty("value").GetString(), Is.EqualTo("echo from CSharp Git"));
            Assert.That(await database.CatalogEvaluations.CountAsync(e => e.CleanupConfirmed && e.State == "SUCCEEDED"), Is.EqualTo(2));
        }
        // A rejection after intermediate projection writes must roll back all catalogue indexes.
        await WriteAsync(".vivarium/projects/P/Control.g.cs", source["Control.g.cs"].Replace("Project P", "Should not be applied"));
        await WriteAsync(".vivarium/unbound/P/Settings.g.cs", """
            using Vivarium.Definitions;
            public static class Managed {
                public static Settings Declare() => new("P", null, [], [],
                    [new Template("P_LocalSmoke", "Unsupported kind conversion", [])]);
            }
            """);
        await CommitAsync("fixture: reject a retained BuildType kind conversion");
        var conversion = await service.ReconcileAsync(actor);
        Assert.That(conversion.State, Is.EqualTo("REJECTED"));
        await using (var database = await factory.CreateDbContextAsync())
        {
            Assert.That((await database.CatalogProjects.SingleAsync(p => p.Id == projectIdentity)).Name, Is.EqualTo("Project P"));
            Assert.That((await database.CatalogBuildTypes.SingleAsync()).IsActive, Is.True);
            Assert.That(await database.CatalogRevisions.CountAsync(), Is.EqualTo(1));
            Assert.That(await database.CatalogBuildDefinitions.CountAsync(), Is.EqualTo(1));
        }
        await WriteAsync(".vivarium/unbound/P/Settings.g.cs", "invalid CSharp candidate");
        await CommitAsync("fixture: compiler rejection");
        var invalid = await service.ReconcileAsync(actor);
        Assert.That(invalid.State, Is.EqualTo("REJECTED"));
        Assert.That(invalid.FailureCode, Is.EqualTo("evaluation_failed"));
        await using (var database = await factory.CreateDbContextAsync())
            Assert.That((await database.ActiveCatalogs.SingleAsync()).RevisionId, Is.EqualTo(firstRevision));

        await WriteAsync(".vivarium/projects/P/Control.g.cs", source["Control.g.cs"]);
        await WriteAsync(".vivarium/unbound/P/Settings.g.cs", source["Settings.g.cs"].Replace("echo from CSharp Git", "echo repaired candidate")
            .Replace("Local smoke", "Renamed local smoke"));
        var repairedCommit = await CommitAsync("fixture: repaired candidate");
        const string activationFault = """
                CREATE FUNCTION fixture_activation_failure() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
                  IF NEW."Action" = 'configuration.activate' AND NEW."Outcome" = 'SUCCEEDED' THEN
                    RAISE EXCEPTION 'owned activation audit fault';
                  END IF;
                  RETURN NEW;
                END $$;
                CREATE TRIGGER fixture_activation_failure BEFORE INSERT ON "AuditEvents"
                  FOR EACH ROW EXECUTE FUNCTION fixture_activation_failure();
                """;
        await using (var database = await factory.CreateDbContextAsync())
            await database.Database.ExecuteSqlRawAsync(activationFault);
        Assert.ThrowsAsync<DbUpdateException>(() => service.ReconcileAsync(actor));
        Guid pendingId;
        await using (var database = await factory.CreateDbContextAsync())
        {
            var pending = await database.CatalogChanges.SingleAsync(c => c.State == "EVALUATED");
            pendingId = pending.Id;
            Assert.That(pending.CandidateCommit, Is.EqualTo(repairedCommit));
            Assert.That(pending.MaterializedJson, Is.Not.Null);
            Assert.That((await database.ActiveCatalogs.SingleAsync()).RevisionId, Is.EqualTo(firstRevision));
            database.UserRoleGrants.Remove(await database.UserRoleGrants.SingleAsync(g => g.UserId == actorId));
            await database.SaveChangesAsync();
        }
        Assert.That(await service.RecoverAsync(), Is.EqualTo(1));
        await using (var database = await factory.CreateDbContextAsync())
        {
            var pending = await database.CatalogChanges.FindAsync(pendingId);
            Assert.That(pending!.State, Is.EqualTo("REJECTED"));
            Assert.That(pending.FailureCode, Is.EqualTo("permission_denied"));
            Assert.That(pending.Origin, Is.EqualTo("MANAGED"));
            Assert.That((await database.ActiveCatalogs.SingleAsync()).RevisionId, Is.EqualTo(firstRevision));
            Assert.That((await database.CatalogRevisions.SingleAsync()).DefinitionJson, Is.EqualTo(original));
            Assert.That(await database.CatalogEvaluations.CountAsync(e => e.ChangeId == pendingId), Is.EqualTo(2));
            await database.Database.ExecuteSqlRawAsync("DROP TRIGGER fixture_activation_failure ON \"AuditEvents\"; DROP FUNCTION fixture_activation_failure();");
            database.UserRoleGrants.Add(new UserRoleGrant { UserId = actorId, RoleId = "SYSTEM_ADMIN", Scope = "g" });
            await database.SaveChangesAsync();
        }
        var repaired = await service.ReconcileAsync(actor);
        Assert.That(repaired.State, Is.EqualTo("APPLIED"), repaired.FailureCode);
        await using (var database = await factory.CreateDbContextAsync())
        {
            Assert.That(await database.CatalogRevisions.CountAsync(), Is.EqualTo(2));
            Assert.That((await database.CatalogProjects.SingleAsync(p => p.ExternalId == "P")).Id, Is.EqualTo(projectIdentity));
            Assert.That((await database.CatalogBuildTypes.SingleAsync()).Id, Is.EqualTo(buildTypeIdentity));
            Assert.That((await database.CatalogBuildTypes.SingleAsync()).Name, Is.EqualTo("Renamed local smoke"));
            Assert.That((await database.CatalogRevisions.FindAsync(firstRevision))!.DefinitionJson, Is.EqualTo(original));
            Assert.That(await database.AuditEvents.AnyAsync(a => a.Action == "configuration.activate"
                && a.ActorId == actorId.ToString() && a.Outcome == "DENIED"), Is.True);
        }
        // A cold coordinator must retain the actual evaluated result, including nondeterminism.
        await WriteAsync(".vivarium/unbound/P/Settings.g.cs", source["Settings.g.cs"].Replace(
            "\"echo from CSharp Git\"", "\"echo \" + Guid.NewGuid().ToString(\"N\")"));
        await CommitAsync("fixture: retained nondeterministic evaluation");
        await using (var database = await factory.CreateDbContextAsync())
            await database.Database.ExecuteSqlRawAsync(activationFault);
        Assert.ThrowsAsync<DbUpdateException>(() => service.ReconcileAsync(actor));
        string retained;
        await using (var database = await factory.CreateDbContextAsync())
        {
            var pending = await database.CatalogChanges.SingleAsync(c => c.State == "EVALUATED");
            pendingId = pending.Id; retained = pending.MaterializedJson!;
            await database.Database.ExecuteSqlRawAsync("DROP TRIGGER fixture_activation_failure ON \"AuditEvents\"; DROP FUNCTION fixture_activation_failure();");
        }
        var cold = new CatalogService(factory, configuration, new("local", repository, []), TimeProvider.System);
        Assert.That(await cold.RecoverAsync(), Is.EqualTo(1));
        await using (var database = await factory.CreateDbContextAsync())
        {
            var revision = await database.CatalogRevisions.SingleAsync(r => r.ChangeId == pendingId);
            Assert.That(revision.DefinitionJson, Is.EqualTo(retained));
            Assert.That(await database.CatalogEvaluations.CountAsync(e => e.ChangeId == pendingId), Is.EqualTo(2),
                "Recovery must not execute nondeterministic CSharp again.");
            Assert.That((await database.CatalogChanges.FindAsync(pendingId))!.State, Is.EqualTo("APPLIED"));
            Assert.That((await database.CatalogRevisions.FindAsync(firstRevision))!.DefinitionJson, Is.EqualTo(original));
            var rejectedRewrite = Assert.ThrowsAsync<PostgresException>(() => database.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"CatalogRevisions\" SET \"DefinitionHash\" = 'rewritten' WHERE \"Id\" = {firstRevision}"));
            Assert.That(rejectedRewrite!.SqlState, Is.EqualTo(PostgresErrorCodes.CheckViolation));
            var rejectedDefinition = Assert.ThrowsAsync<PostgresException>(() => database.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"CatalogBuildDefinitions\" SET \"PlanHash\" = {new string('0', 64)} WHERE \"BuildTypeId\" = {buildTypeIdentity}"));
            Assert.That(rejectedDefinition!.SqlState, Is.EqualTo(PostgresErrorCodes.CheckViolation));
        }
        // Prepare a destination first, then select it through committed control ownership.
        var product = Path.Combine(root, "product"); Directory.CreateDirectory(product);
        await SourceCompiler.GitAsync(product, "init", "-b", "main");
        await SourceCompiler.GitAsync(product, "config", "core.autocrlf", "false");
        await SourceCompiler.GitAsync(product, "config", "user.name", "Owned settings fixture");
        await SourceCompiler.GitAsync(product, "config", "user.email", "fixture@example.invalid");
        var settingsDirectory = Path.Combine(product, ".vivarium/settings"); Directory.CreateDirectory(settingsDirectory);
        await File.WriteAllTextAsync(Path.Combine(product, ".vivarium/repository.yaml"), """
            apiVersion: vivarium.io/v1alpha1
            kind: Repository
            id: product
            spec:
              schemaVersion: "2"
              documentKinds:
                - Settings
            """);
        await File.WriteAllTextAsync(Path.Combine(settingsDirectory, "Settings.g.cs"),
            source["Settings.g.cs"].Replace("echo from CSharp Git", "echo prepared destination"));
        await File.WriteAllTextAsync(Path.Combine(settingsDirectory, "UserCode.cs"), source["UserCode.cs"]);
        await SourceCompiler.GitAsync(product, "add", "--all");
        await SourceCompiler.GitAsync(product, "commit", "-m", "fixture: prepared source without activation");
        var productCommit = await SourceCompiler.GitAsync(product, "rev-parse", "HEAD");
        var handoff = new CatalogService(factory, configuration,
            new("local", repository, [new("product", product, "owned-local")]), TimeProvider.System);
        await WriteAsync(".vivarium/projects/P/Control.g.cs", """
            using Vivarium.Definitions;
            public static class Control {
                public static Project Declare() => new("P", "_Root", "Project P", "",
                    new("product", ".vivarium/settings", "refs/heads/main", "owned-local", ""));
            }
            """);
        await CommitAsync("fixture: accepted source handoff");
        var bound = await handoff.ReconcileAsync(actor);
        Assert.That(bound.State, Is.EqualTo("APPLIED"), bound.FailureCode);
        Guid boundRevision;
        await using (var database = await factory.CreateDbContextAsync())
        {
            boundRevision = (await database.ActiveCatalogs.SingleAsync()).RevisionId;
            var pinned = await database.CatalogBuildDefinitions.SingleAsync(d => d.RevisionId == boundRevision);
            Assert.That(pinned.BuildTypeId, Is.EqualTo(buildTypeIdentity));
            Assert.That(pinned.SettingsRepositoryId, Is.EqualTo("product"));
            Assert.That(pinned.SettingsCommit, Is.EqualTo(productCommit));
            Assert.That(pinned.PlanJson, Does.Contain("echo prepared destination"));
            Assert.That((await database.CatalogRevisions.FindAsync(firstRevision))!.DefinitionJson, Is.EqualTo(original));
        }
        await File.WriteAllTextAsync(Path.Combine(settingsDirectory, "Settings.g.cs"),
            source["Settings.g.cs"].Replace("new(\"P\", null", "new(\"Q\", null"));
        await SourceCompiler.GitAsync(product, "add", "--all");
        await SourceCompiler.GitAsync(product, "commit", "-m", "fixture: reject foreign settings ownership");
        var foreign = await handoff.ReconcileAsync(actor);
        Assert.That(foreign.State, Is.EqualTo("REJECTED"));
        await using (var database = await factory.CreateDbContextAsync())
            Assert.That((await database.ActiveCatalogs.SingleAsync()).RevisionId, Is.EqualTo(boundRevision));

        var abandoned = await handoff.SubmitAsync(actor);
        var invocation = Guid.NewGuid();
        var evaluator = new IsolatedDefinitionEvaluator(invocation);
        await using (var database = await factory.CreateDbContextAsync())
        {
            // Arrange the durable boundary left by a dead coordinator; code still executes through
            // the real production evaluator, never a fake Docker or alternate compilation path.
            database.CatalogEvaluations.Add(new CatalogEvaluation
            { Id = invocation, ChangeId = abandoned.Id, ProjectId = "P", Phase = "SETTINGS", InputHashesJson = "{}" });
            await database.SaveChangesAsync();
        }
        try
        {
            await ProductionEvaluatorPhysicalTests.KillCoordinatorAfterAuthorStartsAsync(invocation);
            var removedSource = new CatalogService(factory, configuration, new("", "", []), TimeProvider.System);
            Assert.That(await removedSource.RecoverAsync(), Is.EqualTo(1));
            await using var database = await factory.CreateDbContextAsync();
            var rejected = (await database.CatalogChanges.FindAsync(abandoned.Id))!;
            Assert.That(rejected.State, Is.EqualTo("REJECTED"));
            Assert.That(rejected.FailureCode, Is.EqualTo("source_configuration_changed"));
            Assert.That(rejected.Origin, Is.EqualTo("MANAGED"));
            Assert.That((await database.CatalogEvaluations.FindAsync(invocation))!.CleanupConfirmed, Is.True);
            Assert.That((await database.ActiveCatalogs.SingleAsync()).RevisionId, Is.EqualTo(boundRevision));
            Assert.That(await database.AuditEvents.AnyAsync(a => a.RequestId == abandoned.Id.ToString()
                && a.Action == "configuration.activate" && a.TargetId == "local" && a.Outcome == "FAILED"), Is.True);
            var remaining = await ProofProcess.RunAsync("docker", ["ps", "-a", "--filter", "name=^/" + evaluator.Name + "$", "--format", "{{.ID}}"]);
            Assert.That(remaining.Text, Is.Empty);
        }
        finally { await IsolatedDefinitionEvaluator.RemoveOwnedAsync(evaluator.Name); }
    }
}
