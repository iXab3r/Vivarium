using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vivarium.Controller.Persistence;
using Vivarium.Controller.Catalog;

namespace Vivarium.DefinitionProof;

public sealed partial class R0AcceptanceCases
{
    [Test]
    public async Task Bound_source_handoff_recovers_the_journalled_evaluation_and_keeps_the_prior_owner_until_atomic_apply()
    {
        await using var host = await installation.CreateCaseAsync(catalogued: true);
        using var administrator = await host.AdministratorAsync(); var csrf = await host.AntiforgeryAsync(administrator);
        await host.StartWindowsAgentAsync(administrator, csrf);
        using var actor = await RestrictedActorAsync(host, administrator, csrf);
        var originalBuildId = await SubmitAsync(actor.BuildClient, "P_LocalSmoke"); await FinishedAsync(actor.BuildClient, originalBuildId);
        var oldBuild = await ReadBuildAsync(actor.BuildClient, originalBuildId);
        var product = Path.Combine(host.Root, "bound-settings"); Directory.CreateDirectory(product);
        await SourceCompiler.GitAsync(product, "init", "-b", "main");
        await SourceCompiler.GitAsync(product, "config", "core.autocrlf", "false");
        await SourceCompiler.GitAsync(product, "config", "user.name", "Owned source handoff");
        await SourceCompiler.GitAsync(product, "config", "user.email", "fixture@example.invalid");
        var settings = Path.Combine(product, ".vivarium/settings"); Directory.CreateDirectory(settings);
        await File.WriteAllTextAsync(Path.Combine(product, ".vivarium/repository.yaml"), DefinitionGitReader.Manifest("bound-r0", control: false) + "\n");
        await File.WriteAllTextAsync(Path.Combine(settings, "Settings.g.cs"),
            (await File.ReadAllTextAsync(Path.Combine(host.Definitions, ".vivarium/unbound/P/Settings.g.cs")))
            .Replace("\"Local smoke\"", "\"Bound smoke\"", StringComparison.Ordinal));
        await File.WriteAllBytesAsync(Path.Combine(settings, "UserCode.cs"), await File.ReadAllBytesAsync(Path.Combine(host.Definitions, ".vivarium/unbound/P/UserCode.cs")));
        await SourceCompiler.GitAsync(product, "add", "--all"); await SourceCompiler.GitAsync(product, "commit", "-m", "fixture: prepare destination before binding");
        var destination = (await SourceCompiler.GitAsync(product, "rev-parse", "HEAD")).Trim();
        await host.ConfigureSettingsRepositoryAsync("bound-r0", product, "owned-local");
        using (var stillLocal = JsonDocument.Parse(await actor.BuildClient.GetStringAsync("/api/v1/build-configurations/P_LocalSmoke")))
            Assert.That(stillLocal.RootElement.GetProperty("name").GetString(), Is.EqualTo("Local smoke"));
        await File.WriteAllTextAsync(Path.Combine(host.Definitions, ".vivarium/projects/P/Control.g.cs"), """
            using Vivarium.Definitions;
            public static class Control {
                public static Project Declare() => new("P", "_Root", "Project P", "",
                    new("bound-r0", ".vivarium/settings", "refs/heads/main", "owned-local", ""));
            }
            """);
        var control = await CommitCaseAsync(host, "fixture: accept the prepared source binding");
        const string createFault = """
            CREATE FUNCTION public.owned_handoff_audit_fault() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN IF NEW."Action"='configuration.activate' AND NEW."Outcome"='SUCCEEDED' THEN
              RAISE EXCEPTION 'Owned handoff audit fault' USING ERRCODE='23514'; END IF; RETURN NEW; END $$;
            CREATE TRIGGER "Owned_HandoffAuditFault" BEFORE INSERT ON "AuditEvents"
              FOR EACH ROW EXECUTE FUNCTION public.owned_handoff_audit_fault();
            """;
        const string removeFault = "DROP TRIGGER \"Owned_HandoffAuditFault\" ON \"AuditEvents\"; DROP FUNCTION public.owned_handoff_audit_fault();";
        await host.ExecuteDatabaseCommandAsync(createFault);
        var faultRemoved = false;
        Guid operationId;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/configuration-changes/reconcile");
            request.Headers.Add("RequestVerificationToken", csrf);
            using var accepted = await administrator.SendAsync(request); Assert.That(accepted.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
            using var admission = JsonDocument.Parse(await accepted.Content.ReadAsStringAsync()); operationId = admission.RootElement.GetProperty("id").GetGuid();
            await WaitAsync(async () =>
            {
                await using var scope = host.Services.CreateAsyncScope(); var database = scope.ServiceProvider.GetRequiredService<VivariumDbContext>();
                return await database.CatalogChanges.AnyAsync(c => c.Id == operationId && c.State == "EVALUATED");
            });
            await using var verification = host.Services.CreateAsyncScope(); var state = verification.ServiceProvider.GetRequiredService<VivariumDbContext>();
            var pending = await state.CatalogChanges.SingleAsync(c => c.Id == operationId);
            Assert.That(pending.CandidateCommit, Is.EqualTo(control));
            var selected = JsonSerializer.Deserialize<CatalogMember[]>(pending.SelectedRevisionsJson, DefinitionValidation.Json)!;
            Assert.That(selected.Single(s => s.RepositoryId == "bound-r0").Commit, Is.EqualTo(destination));
            Assert.That((await state.ActiveCatalogs.SingleAsync()).RevisionId, Is.EqualTo(oldBuild.GetProperty("definitionRevisionId").GetGuid()));
            Assert.That(await state.CatalogRevisions.AnyAsync(r => r.ChangeId == operationId), Is.False);
            await host.RestartAsync(async () => { await host.ExecuteDatabaseCommandAsync(removeFault); faultRemoved = true; });
        }
        finally { if (!faultRemoved) await host.ExecuteDatabaseCommandAsync(removeFault); }
        var applied = await WaitChangeAsync(administrator, "/api/v1/configuration-changes/" + operationId);
        Assert.That(applied.GetProperty("state").GetString(), Is.EqualTo("applied"));
        using var bound = JsonDocument.Parse(await actor.BuildClient.GetStringAsync("/api/v1/build-configurations/P_LocalSmoke"));
        Assert.That(bound.RootElement.GetProperty("name").GetString(), Is.EqualTo("Bound smoke"));
        var retained = await ReadBuildAsync(actor.BuildClient, originalBuildId);
        Assert.That(retained.GetProperty("definitionHash").GetString(), Is.EqualTo(oldBuild.GetProperty("definitionHash").GetString()));
        await using var final = host.Services.CreateAsyncScope(); var database = final.ServiceProvider.GetRequiredService<VivariumDbContext>();
        var revision = applied.GetProperty("appliedRevisionId").GetGuid();
        var definition = await database.CatalogBuildDefinitions.SingleAsync(d => d.RevisionId == revision && d.BuildTypeId ==
            database.CatalogBuildTypes.Where(t => t.ExternalId == "P_LocalSmoke").Select(t => t.Id).Single());
        Assert.That(definition.SettingsRepositoryId, Is.EqualTo("bound-r0")); Assert.That(definition.SettingsCommit, Is.EqualTo(destination));
        Assert.That(await database.CatalogEvaluations.CountAsync(e => e.ChangeId == operationId && e.ProjectId == "P"), Is.EqualTo(2),
            "Restart must reuse the two original control/settings evaluations.");
        Assert.That(await database.AuditEvents.CountAsync(e => e.Action == "configuration.activate" && e.Outcome == "SUCCEEDED"
            && e.CorrelationId == (database.CatalogChanges.Where(c => c.Id == operationId).Select(c => c.CorrelationId).Single())), Is.EqualTo(1));
    }

    [Test]
    public async Task Invalid_and_newly_unauthorized_candidates_keep_the_active_plan_and_never_activate_after_restart()
    {
        await using var host = await installation.CreateCaseAsync(catalogued: true);
        using var administrator = await host.AdministratorAsync(); var csrf = await host.AntiforgeryAsync(administrator);
        await host.StartWindowsAgentAsync(administrator, csrf);
        using var runner = await RestrictedActorAsync(host, administrator, csrf);
        using var editor = new ScopedKeyScenario(host.Origin, host.Thumbprint, host.Root, host.Certificate);
        await editor.InitializeUserAsync(host.Services, "r0-editor-" + Guid.NewGuid().ToString("N")[..12]);
        var role = $"/api/v1/users/{editor.UserId}/roles/SYSTEM_ADMIN/g";
        await R0AcceptanceInstallation.Case.MutateAsync(administrator, csrf, HttpMethod.Put, role, null, HttpStatusCode.NoContent);
        await editor.CreateKeyAsync();
        var baselineId = await SubmitAsync(runner.BuildClient, "P_LocalSmoke"); await FinishedAsync(runner.BuildClient, baselineId);
        var baseline = await ReadBuildAsync(runner.BuildClient, baselineId);
        var source = Path.Combine(host.Definitions, ".vivarium/unbound/P/Settings.g.cs");
        var original = await File.ReadAllTextAsync(source);
        foreach (var candidate in new[] { "invalid owned CSharp", original.Replace("return new(\"P\", null", "return new(\"Q\", null", StringComparison.Ordinal) })
        {
            Assert.That(candidate, Is.Not.EqualTo(original));
            await File.WriteAllTextAsync(source, candidate); await CommitCaseAsync(host, "fixture: rejected CSharp candidate");
            var rejected = await ReconcileCaseAsync(editor.BuildClient);
            Assert.That(rejected.GetProperty("state").GetString(), Is.EqualTo("rejected"));
            Assert.That(rejected.GetProperty("failureCode").GetString(), Is.Not.Null.And.Not.Empty);
            using var current = JsonDocument.Parse(await runner.BuildClient.GetStringAsync("/api/v1/build-configurations/P_LocalSmoke"));
            Assert.That(current.RootElement.GetProperty("activeRevisionId").GetGuid(), Is.EqualTo(baseline.GetProperty("definitionRevisionId").GetGuid()));
        }
        await File.WriteAllTextAsync(source, original.Replace("return new(\"P\", null",
            "System.Threading.Thread.Sleep(10000); return new(\"P\", null", StringComparison.Ordinal));
        var commit = await CommitCaseAsync(host, "fixture: revoke managed proposal authority during real evaluation");
        using var proposed = await editor.BuildClient.PostAsync("/api/v1/configuration-changes/reconcile", null);
        Assert.That(proposed.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        using var operation = JsonDocument.Parse(await proposed.Content.ReadAsStringAsync());
        var operationId = operation.RootElement.GetProperty("id").GetGuid();
        await WaitAsync(async () =>
        {
            await using var scope = host.Services.CreateAsyncScope(); var database = scope.ServiceProvider.GetRequiredService<VivariumDbContext>();
            return await database.CatalogEvaluations.AnyAsync(e => e.ChangeId == operationId && e.ProjectId == "P" && e.Phase == "SETTINGS" && e.State == "RUNNING");
        });
        await R0AcceptanceInstallation.Case.MutateAsync(administrator, csrf, HttpMethod.Delete, role, null, HttpStatusCode.NoContent);
        var withdrawn = await WaitChangeAsync(administrator, proposed.Headers.Location!.OriginalString);
        Assert.That(withdrawn.GetProperty("state").GetString(), Is.EqualTo("rejected"));
        Assert.That(withdrawn.GetProperty("failureCode").GetString(), Is.EqualTo("permission_denied"));
        await host.RestartAsync();
        var next = await SubmitAsync(runner.BuildClient, "P_LocalSmoke"); await FinishedAsync(runner.BuildClient, next);
        var currentBuild = await ReadBuildAsync(runner.BuildClient, next);
        Assert.That(currentBuild.GetProperty("definitionRevisionId").GetGuid(), Is.EqualTo(baseline.GetProperty("definitionRevisionId").GetGuid()));
        Assert.That(currentBuild.GetProperty("definitionHash").GetString(), Is.EqualTo(baseline.GetProperty("definitionHash").GetString()));
        await using var verification = host.Services.CreateAsyncScope(); var state = verification.ServiceProvider.GetRequiredService<VivariumDbContext>();
        var change = await state.CatalogChanges.SingleAsync(c => c.Id == operationId);
        Assert.That(change.CandidateCommit, Is.EqualTo(commit)); Assert.That(change.Origin, Is.EqualTo("MANAGED"));
        Assert.That(await state.CatalogRevisions.AnyAsync(r => r.ChangeId == operationId), Is.False);
        var active = await state.ActiveCatalogs.SingleAsync();
        Assert.That(active.RevisionId, Is.EqualTo(baseline.GetProperty("definitionRevisionId").GetGuid()));
        Assert.That(active.LastKnownGoodRevisionId, Is.EqualTo(active.RevisionId));
    }

    [Test]
    public async Task Committed_unbound_CSharp_activates_one_real_catalogue_and_actual_builds_pin_its_evaluated_inputs()
    {
        await using var host = await installation.CreateCaseAsync();
        using var administrator = await host.AdministratorAsync(); var csrf = await host.AntiforgeryAsync(administrator);
        using (var unavailable = await administrator.GetAsync("/api/v1/projects"))
            Assert.That(unavailable.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        var commit = (await SourceCompiler.GitAsync(host.Definitions, "rev-parse", "HEAD")).Trim();
        var applied = await ReconcileCaseAsync(administrator, csrf);
        Assert.That(applied.GetProperty("state").GetString(), Is.EqualTo("applied"));
        var revision = applied.GetProperty("appliedRevisionId").GetGuid();
        using var project = JsonDocument.Parse(await administrator.GetStringAsync("/api/v1/projects/P"));
        Assert.That(project.RootElement.GetProperty("id").GetString(), Is.EqualTo("P"));
        Assert.That(project.RootElement.GetProperty("name").GetString(), Is.EqualTo("Project P"));
        Assert.That(project.RootElement.GetProperty("activeRevisionId").GetGuid(), Is.EqualTo(revision));
        await host.StartWindowsAgentAsync(administrator, csrf);
        using var actor = await RestrictedActorAsync(host, administrator, csrf);
        var id = await SubmitAsync(actor.BuildClient, "P_LocalSmoke"); await FinishedAsync(actor.BuildClient, id);
        var build = await ReadBuildAsync(actor.BuildClient, id);
        // Enabling the Agent is a later Git revision with identical source inputs and plans.
        await using var scope = host.Services.CreateAsyncScope(); var database = scope.ServiceProvider.GetRequiredService<VivariumDbContext>();
        var original = await database.CatalogRevisions.SingleAsync(r => r.Id == revision);
        Assert.That(original.ControlCommit, Is.EqualTo(commit));
        var pinned = await database.RuntimeBuildInputs.SingleAsync(i => i.BuildId == id);
        var definition = await database.CatalogBuildDefinitions.SingleAsync(d => d.BuildTypeId == pinned.BuildTypeId && d.RevisionId == pinned.RevisionId);
        Assert.That(definition.RevisionId, Is.EqualTo(build.GetProperty("definitionRevisionId").GetGuid()));
        Assert.That(definition.PlanHash, Is.EqualTo(build.GetProperty("definitionHash").GetString()));
        var active = await database.ActiveCatalogs.SingleAsync();
        Assert.That(active.LastKnownGoodRevisionId, Is.EqualTo(active.RevisionId));
        Assert.That(await database.AuditEvents.CountAsync(e => e.Action == "configuration.activate" && e.Outcome == "SUCCEEDED"), Is.EqualTo(2));
        actor.BuildClient.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        using var compatible = JsonDocument.Parse(await actor.BuildClient.GetStringAsync("/app/rest/buildTypes/id:P_LocalSmoke?fields=id,name,projectId"));
        Assert.That(compatible.RootElement.GetProperty("id").GetString(), Is.EqualTo("P_LocalSmoke"));
        Assert.That(compatible.RootElement.GetProperty("name").GetString(), Is.EqualTo("Local smoke"));
        Assert.That(compatible.RootElement.GetProperty("projectId").GetString(), Is.EqualTo("P"));
    }

    [Test]
    public async Task A_compiled_Git_name_edit_preserves_user_code_and_historical_build_inputs_across_restart()
    {
        await using var host = await installation.CreateCaseAsync(catalogued: true);
        using var administrator = await host.AdministratorAsync(); var csrf = await host.AntiforgeryAsync(administrator);
        await host.StartWindowsAgentAsync(administrator, csrf);
        using var actor = await RestrictedActorAsync(host, administrator, csrf);
        var id = await SubmitAsync(actor.BuildClient, "P_LocalSmoke"); await FinishedAsync(actor.BuildClient, id);
        var oldBuild = await ReadBuildAsync(actor.BuildClient, id);
        var userPath = Path.Combine(host.Definitions, ".vivarium/unbound/P/UserCode.cs");
        var userCode = await File.ReadAllBytesAsync(userPath);
        var path = Path.Combine(host.Definitions, ".vivarium/unbound/P/Settings.g.cs");
        var original = await File.ReadAllTextAsync(path);
        var edited = original.Replace("\"Local smoke\"", "\"Renamed smoke\"", StringComparison.Ordinal);
        Assert.That(edited, Is.Not.EqualTo(original)); await File.WriteAllTextAsync(path, edited);
        var commit = await CommitCaseAsync(host, "fixture: typed CSharp configuration name edit");
        var applied = await ReconcileCaseAsync(administrator, csrf);
        Assert.That(applied.GetProperty("state").GetString(), Is.EqualTo("applied"));
        Assert.That(await File.ReadAllBytesAsync(userPath), Is.EqualTo(userCode));
        await host.RestartAsync();
        using var current = JsonDocument.Parse(await actor.BuildClient.GetStringAsync("/api/v1/build-configurations/P_LocalSmoke"));
        Assert.That(current.RootElement.GetProperty("name").GetString(), Is.EqualTo("Renamed smoke"));
        var retained = await ReadBuildAsync(actor.BuildClient, id);
        foreach (var field in new[] { "id", "definitionRevisionId", "definitionHash", "state", "status", "agentId" })
            Assert.That(retained.GetProperty(field).GetRawText(), Is.EqualTo(oldBuild.GetProperty(field).GetRawText()));
        var next = await SubmitAsync(actor.BuildClient, "P_LocalSmoke"); await FinishedAsync(actor.BuildClient, next);
        var nextBuild = await ReadBuildAsync(actor.BuildClient, next);
        Assert.That(nextBuild.GetProperty("definitionRevisionId").GetGuid(), Is.Not.EqualTo(oldBuild.GetProperty("definitionRevisionId").GetGuid()));
        await using var scope = host.Services.CreateAsyncScope(); var database = scope.ServiceProvider.GetRequiredService<VivariumDbContext>();
        var revision = await database.CatalogRevisions.SingleAsync(r => r.Id == nextBuild.GetProperty("definitionRevisionId").GetGuid());
        Assert.That(revision.ControlCommit, Is.EqualTo(commit));
        var oldInput = await database.RuntimeBuildInputs.SingleAsync(i => i.BuildId == id);
        var oldDefinition = await database.CatalogBuildDefinitions.SingleAsync(d => d.BuildTypeId == oldInput.BuildTypeId && d.RevisionId == oldInput.RevisionId);
        Assert.That(oldDefinition.PlanHash, Is.EqualTo(oldBuild.GetProperty("definitionHash").GetString()));
        Assert.That(oldDefinition.PlanJson, Does.Contain("Local smoke").And.Not.Contain("Renamed smoke"));
    }

    private static async Task<string> CommitCaseAsync(R0AcceptanceInstallation.Case host, string message)
    {
        await SourceCompiler.GitAsync(host.Definitions, "add", "--all");
        await SourceCompiler.GitAsync(host.Definitions, "commit", "-m", message);
        return (await SourceCompiler.GitAsync(host.Definitions, "rev-parse", "HEAD")).Trim();
    }
    private static async Task<JsonElement> ReconcileCaseAsync(HttpClient client, string? csrf = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/configuration-changes/reconcile");
        if (csrf is not null) request.Headers.Add("RequestVerificationToken", csrf);
        using var accepted = await client.SendAsync(request); Assert.That(accepted.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        return await WaitChangeAsync(client, accepted.Headers.Location!.OriginalString);
    }
    private static async Task<JsonElement> WaitChangeAsync(HttpClient client, string path)
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        while (true)
        {
            using var response = JsonDocument.Parse(await client.GetStringAsync(path, bound.Token));
            var state = response.RootElement.GetProperty("state").GetString();
            if (state is not ("pending" or "evaluated")) return response.RootElement.Clone();
            await Task.Delay(50, bound.Token);
        }
    }
}
