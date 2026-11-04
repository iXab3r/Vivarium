using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vivarium.Controller.Auditing;
using Vivarium.Controller.Configuration.Git;
using Vivarium.Controller.Identity;
using Vivarium.Controller.Persistence;
using Vivarium.Controller.Security;
using Vivarium.Definitions;
using Vivarium.Controller.Agents;
using Vivarium.Controller.Rest.Common;

namespace Vivarium.Controller.Catalog;

public sealed class CatalogBusyException() : Exception("The catalogue coordinator is busy.");
internal sealed class CatalogEvaluationException(string code) : Exception(code);

/// <summary>One accepted Git revision set becomes visible only through the final PG activation fence.</summary>
public sealed class CatalogService(IDbContextFactory<VivariumDbContext> databases, IConfiguration configuration,
    CatalogSources sources, TimeProvider time, AgentRegistry? agentRegistry = null)
{
    private const long CoordinationLock = 90695025115;

    public Task<CatalogChange> ReconcileAsync(ManagementRequestContext actor, CancellationToken token = default) => PrepareAsync(actor, complete: true, token);
    public Task<CatalogChange> SubmitAsync(ManagementRequestContext actor, CancellationToken token = default) => PrepareAsync(actor, complete: false, token);

    internal Task<CatalogChange> SetAgentEnabledAsync(ManagementRequestContext actor, string agentId, bool enabled,
        string comment, CancellationToken token) => WithLockAsync(async database =>
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(agentId, "^[a-z0-9](?:[a-z0-9.-]{0,125}[a-z0-9])?$"))
            throw new InvalidDataException("The Agent Git identity is unsupported.");
        if (System.Text.Encoding.UTF8.GetByteCount(comment) > 4096 || comment.Contains('\0'))
            throw new RestApiException(400, "invalid_request", "Invalid request", "The Agent enabled comment is invalid");
        var operation = new CatalogChange { Id = Guid.NewGuid(), ActorId = Guid.Parse(actor.Principal.ActorId),
            CredentialKind = actor.Principal.CredentialKind, CorrelationId = actor.CorrelationId, RequestId = actor.RequestId,
            Kind = "AGENT_ENABLEMENT", TargetAgentId = agentId, RepositoryId = sources.RepositoryId,
            CandidateCommit = "", SelectedRevisionsJson = "[]", CreatedAt = time.GetUtcNow() };
        if (!await CanActivateAsync(database, actor, token, operation))
        {
            new PostgresAuditStore(database).Stage(AuditEventDraft.Create(actor, time.GetUtcNow(), "agent.settings",
                "agent", agentId, AuditOutcome.Denied, "permission_denied"));
            await database.SaveChangesAsync(token); throw new AccessPermissionException();
        }
        await RecoverCoreAsync(database, token);
        if (!await database.AgentRegistrations.AnyAsync(a => a.AgentId == agentId && a.DeletedAt == null, token))
            throw new InvalidDataException("The Agent does not exist.");
        var active = await database.ActiveCatalogs.AsNoTracking().SingleOrDefaultAsync(a => a.RepositoryId == sources.RepositoryId, token)
            ?? throw new InvalidDataException("Activate the catalogue before changing Agent settings.");
        var prior = await database.CatalogRevisions.AsNoTracking().SingleAsync(r => r.Id == active.RevisionId, token);
        var reader = ControlReader();
        if (await reader.HeadAsync(token: token) != prior.ControlCommit)
            throw new CatalogBusyException(); // An unaccepted source change must not acquire this actor's authority.
        var setting = new CatalogAgentSetting(agentId, enabled, comment, DateTimeOffset.FromUnixTimeSeconds(time.GetUtcNow().ToUnixTimeSeconds()));
        var updates = new Dictionary<string, string>
        {
            [".vivarium/agents/" + agentId + ".yaml"] = CatalogAgentSettings.Render(setting),
            [".vivarium/repository.yaml"] = DefinitionGitReader.Manifest(sources.RepositoryId, control: true, agents: true) + "\n",
        };
        var writer = new DefinitionGitWriter(sources.Path);
        operation.CandidateCommit = await writer.PrepareAsync(prior.ControlCommit, updates, operation.Id,
            actor.Principal.ActorId, actor.CorrelationId, token);
        operation.BaseRevisionId = prior.Id;
        var materialized = await AgentDefinitionAsync(database, operation, token);
        operation.MaterializedJson = JsonSerializer.Serialize(materialized, DefinitionValidation.Json);
        operation.SelectedRevisionsJson = JsonSerializer.Serialize(materialized.Members, DefinitionValidation.Json);
        operation.State = "EVALUATED";
        database.CatalogChanges.Add(operation);
        new PostgresAuditStore(database).Stage(AuditEventDraft.Create(actor.WithRequestId(operation.Id.ToString()),
            time.GetUtcNow(), "configuration.prepare", "agent", agentId) with { ResultRevision = operation.CandidateCommit });
        await database.SaveChangesAsync(token);
        await using (var permissionFence = await database.Database.BeginTransactionAsync(token))
        {
            await database.Database.ExecuteSqlRawAsync("""
                LOCK TABLE "AspNetUsers", "UserRoleGrants", "RolePermissions", "IncludedRoles", "GroupRoleGrants" IN SHARE MODE
                """, token);
            if (!await CanActivateAsync(database, actor, token, operation))
            {
                await RejectAsync(database, operation, "permission_denied", AuditOutcome.Denied, token);
                await permissionFence.CommitAsync(token); return operation;
            }
            if (!await writer.AcceptAsync(prior.ControlCommit, operation.CandidateCommit, token))
            {
                await RejectAsync(database, operation, "accepted_ref_changed", AuditOutcome.Failed, token, conflict: true);
                await permissionFence.CommitAsync(token); return operation;
            }
            await permissionFence.CommitAsync(token);
        }
        await ContinueAsync(database, operation, token);
        return operation;
    }, token);

    private Task<CatalogChange> PrepareAsync(ManagementRequestContext actor, bool complete, CancellationToken token) =>
        WithLockAsync(async database =>
        {
            if (!await CanActivateAsync(database, actor, token))
            {
                new PostgresAuditStore(database).Stage(AuditEventDraft.Create(actor, time.GetUtcNow(),
                    "configuration.prepare", "repository", sources.RepositoryId, AuditOutcome.Denied, "permission_denied"));
                await database.SaveChangesAsync(token);
                throw new AccessPermissionException();
            }
            var reader = ControlReader();
            if (complete) await RecoverCoreAsync(database, token);
            var candidate = await reader.HeadAsync(token: token);
            var active = await database.ActiveCatalogs.AsNoTracking().SingleOrDefaultAsync(a => a.RepositoryId == sources.RepositoryId, token);
            var operation = new CatalogChange
            {
                Id = Guid.NewGuid(), ActorId = Guid.Parse(actor.Principal.ActorId),
                CorrelationId = actor.CorrelationId, CredentialKind = actor.Principal.CredentialKind,
                RequestId = actor.RequestId, RepositoryId = sources.RepositoryId, CandidateCommit = candidate,
                SelectedRevisionsJson = JsonSerializer.Serialize(new[] { new CatalogMember(sources.RepositoryId, "refs/heads/main", candidate) }, DefinitionValidation.Json),
                BaseRevisionId = active?.RevisionId, CreatedAt = time.GetUtcNow(),
            };
            database.CatalogChanges.Add(operation);
            new PostgresAuditStore(database).Stage(AuditEventDraft.Create(actor.WithRequestId(operation.Id.ToString()),
                time.GetUtcNow(), "configuration.prepare", "repository", sources.RepositoryId) with { ResultRevision = candidate });
            await database.SaveChangesAsync(token); // Invocation identity must exist before Docker mutation.
            if (complete) await ContinueAsync(database, operation, token);
            return operation;
        }, token);

    public Task<int> RecoverAsync(CancellationToken token = default) => WithLockAsync(database => RecoverCoreAsync(database, token), token);

    private async Task<int> RecoverCoreAsync(VivariumDbContext database, CancellationToken token)
    {
        if (!string.IsNullOrWhiteSpace(sources.Path)) await new DefinitionGitWriter(sources.Path).RecoverAsync(token);
        // The installation journal owns these identities independently of the current source
        // resource name. The shared PG coordination lock excludes a live peer coordinator.
        var unreconciled = await database.CatalogEvaluations.Where(e => !e.CleanupConfirmed).ToArrayAsync(token);
        foreach (var invocation in unreconciled)
        {
            await IsolatedDefinitionEvaluator.RemoveOwnedAsync(new IsolatedDefinitionEvaluator(invocation.Id).Name);
            invocation.CleanupConfirmed = true;
            if (invocation.State == "RUNNING") { invocation.State = "INTERRUPTED"; invocation.FailureCode = "evaluation_interrupted"; }
        }
        await database.SaveChangesAsync(token);
        var pending = await database.CatalogChanges.Where(c => c.State == "PENDING" || c.State == "EVALUATED")
            .OrderBy(c => c.CreatedAt).ToArrayAsync(token);
        foreach (var operation in pending)
        {
            if (operation.RepositoryId != sources.RepositoryId)
                await RejectAsync(database, operation, "source_configuration_changed", AuditOutcome.Failed, token);
            else if (await database.CatalogEvaluations.AnyAsync(e => e.ChangeId == operation.Id && e.State == "INTERRUPTED", token))
                await RejectAsync(database, operation, "evaluation_interrupted", AuditOutcome.Failed, token);
            else await ContinueAsync(database, operation, token);
        }
        return pending.Length;
    }

    private async Task ContinueAsync(VivariumDbContext database, CatalogChange operation, CancellationToken token)
    {
        var actor = Actor(operation);
        if (!await CanActivateAsync(database, actor, token, operation))
        { await RejectAsync(database, operation, "permission_denied", AuditOutcome.Denied, token); return; }
        if (await ControlReader().HeadAsync(token: token) != operation.CandidateCommit)
        { await RejectAsync(database, operation, "accepted_ref_changed", AuditOutcome.Failed, token, conflict: true); return; }
        try
        {
            if (operation.Kind == "AGENT_ENABLEMENT")
            {
                var verified = await AgentDefinitionAsync(database, operation, token);
                if (operation.State != "EVALUATED" || operation.MaterializedJson is null
                    || JsonSerializer.Serialize(verified, DefinitionValidation.Json) != JsonSerializer.Serialize(
                        JsonSerializer.Deserialize<CatalogDefinition>(operation.MaterializedJson, DefinitionValidation.Json), DefinitionValidation.Json))
                    throw new InvalidDataException("The retained Agent mutation differs.");
            }
            var selected = JsonSerializer.Deserialize<CatalogMember[]>(operation.SelectedRevisionsJson, DefinitionValidation.Json)
                ?? throw new InvalidDataException("Selected source revisions are missing.");
            if (!await RefsMatchAsync(selected, token))
            { await RejectAsync(database, operation, "accepted_ref_changed", AuditOutcome.Failed, token, conflict: true); return; }
            if (operation.State == "PENDING")
            {
                var definition = await CompileAsync(database, operation, token);
                operation.MaterializedJson = JsonSerializer.Serialize(definition, DefinitionValidation.Json);
                if (System.Text.Encoding.UTF8.GetByteCount(operation.MaterializedJson) > 1024 * 1024)
                    throw new InvalidDataException("The materialized catalogue exceeds its bound.");
                operation.State = "EVALUATED";
                await database.SaveChangesAsync(token); // Reuse this exact evaluation after a crash.
            }
            var materialized = JsonSerializer.Deserialize<CatalogDefinition>(operation.MaterializedJson!, DefinitionValidation.Json)
                ?? throw new InvalidDataException("Materialized catalogue is missing.");
            var canonical = JsonSerializer.Serialize(materialized, DefinitionValidation.Json);
            if (materialized.SchemaVersion != "1") throw new InvalidDataException("The retained catalogue schema is unsupported.");
            if (!await RefsMatchAsync(materialized.Members, token))
            { await RejectAsync(database, operation, "accepted_ref_changed", AuditOutcome.Failed, token, conflict: true); return; }
            await using var transaction = await database.Database.BeginTransactionAsync(token);
            await CatalogAgentSettings.LockProjectionAsync(database, token);
            // Keep the current grant/eligibility read valid through the short activation commit.
            // This fence takes no locks while Docker or source compilation is running.
            await database.Database.ExecuteSqlRawAsync("""
                LOCK TABLE "AspNetUsers", "UserRoleGrants", "RolePermissions", "IncludedRoles", "GroupRoleGrants" IN SHARE MODE
                """, token);
            if (!await CanActivateAsync(database, actor, token, operation))
            {
                await transaction.RollbackAsync(token);
                await RejectAsync(database, operation, "permission_denied", AuditOutcome.Denied, token);
                return;
            }
            var active = await database.ActiveCatalogs.SingleOrDefaultAsync(a => a.RepositoryId == sources.RepositoryId, token);
            if (active?.RevisionId != operation.BaseRevisionId)
            {
                await transaction.RollbackAsync(token);
                await RejectAsync(database, operation, "activation_changed", AuditOutcome.Failed, token, conflict: true);
                return;
            }
            if (await CatalogAgentSettings.ChangedAsync(database, active?.RevisionId, materialized, token)
                && !await new CurrentAccess(database, configuration).AllowsAsync(operation.ActorId, "enable_disable_agent", cancellationToken: token))
            {
                await transaction.RollbackAsync(token);
                await RejectAsync(database, operation, "permission_denied", AuditOutcome.Denied, token);
                return;
            }
            var revision = new CatalogRevision
            {
                Id = Guid.NewGuid(), ChangeId = operation.Id, RepositoryId = sources.RepositoryId,
                ControlCommit = operation.CandidateCommit, CompilerImage = materialized.CompilerImage,
                CompilerSdk = materialized.CompilerSdk,
                ContractHash = materialized.ContractHash,
                DefinitionJson = canonical,
                DefinitionHash = IsolatedDefinitionEvaluator.Hash(canonical), ActivatedAt = time.GetUtcNow(),
            };
            database.CatalogRevisions.Add(revision);
            await CatalogProjection.ApplyAsync(database, revision, materialized, token);
            await CatalogAgentSettings.ApplyAsync(database, revision, materialized, token);
            if (active is null)
            {
                active = new CatalogActive { RepositoryId = sources.RepositoryId };
                database.ActiveCatalogs.Add(active);
            }
            active.RevisionId = revision.Id;
            active.LastKnownGoodRevisionId = revision.Id;
            operation.State = "APPLIED"; operation.CompletedAt = time.GetUtcNow();
            new PostgresAuditStore(database).Stage(AuditEventDraft.Create(actor.WithRequestId(operation.Id.ToString()),
                time.GetUtcNow(), "configuration.activate", "repository", sources.RepositoryId,
                details: new Dictionary<string, string> { ["revisionId"] = revision.Id.ToString(), ["definitionHash"] = revision.DefinitionHash })
                with { ResultRevision = operation.CandidateCommit });
            await database.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            foreach (var agent in materialized.Agents ?? []) agentRegistry?.SetEnabled(agent.Id, agent.Enabled);
        }
        catch (Exception error) when (error is InvalidDataException or JsonException or ConfigurationRepositoryException or CatalogEvaluationException)
        {
            // A projection rejection may have rolled back earlier SaveChanges inside the activation
            // transaction. Discard its tracked mutations before recording only the durable outcome.
            database.ChangeTracker.Clear();
            database.Attach(operation);
            await database.Entry(operation).ReloadAsync(token);
            await RejectAsync(database, operation, error is CatalogEvaluationException ? error.Message : "definition_rejected",
                AuditOutcome.Failed, token);
        }
        // Database/daemon outages are not accepted source failures. The durable pending operation
        // survives for explicit restart/retry, retaining the previous active/LKG projection.
    }

    private async Task<CatalogDefinition> CompileAsync(VivariumDbContext database, CatalogChange operation, CancellationToken token)
    {
        var control = await ControlReader().ReadAsync(operation.CandidateCommit, control: true, token);
        var declarations = control.Sources.Keys.Where(p => p.StartsWith(".vivarium/projects/", StringComparison.Ordinal)
            && p.EndsWith("/Control.g.cs", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToArray();
        if (declarations.Length is < 1 or > 16) throw new InvalidDataException("The selected catalogue requires 1 to 16 projects.");
        var selectedMembers = JsonSerializer.Deserialize<CatalogMember[]>(operation.SelectedRevisionsJson, DefinitionValidation.Json)
            ?? throw new InvalidDataException("Selected source revisions are missing.");
        var members = selectedMembers.ToDictionary(m => m.RepositoryId + "@" + m.Branch, StringComparer.Ordinal);
        var projects = new List<CatalogProject>();
        foreach (var path in declarations)
        {
            var parts = path.Split('/');
            if (parts.Length != 4) throw new InvalidDataException("The Project control path is invalid.");
            var projectId = parts[2]; DefinitionValidation.Identity(projectId);
            var controlOutput = await EvaluateAsync(database, operation, projectId, "CONTROL",
                new Dictionary<string, string> { ["Control.g.cs"] = control.Sources[path] }, token);
            var project = DefinitionValidation.ParseControl(controlOutput, projectId);
            var selected = control;
            var settingsPath = ".vivarium/unbound/" + projectId;
            if (project.Binding is { } binding)
            {
                var repository = BoundRepository(binding.Repository);
                if (binding.TrustReference != repository.TrustReference || binding.CredentialReference != "")
                    throw new InvalidDataException("The local settings source trust/reference is not authorized.");
                settingsPath = binding.Path.TrimEnd('/');
                if (settingsPath != ".vivarium/settings") throw new InvalidDataException("The selected settings path is unsupported.");
                var reader = new DefinitionGitReader(repository.Path, repository.Id);
                var key = repository.Id + "@" + binding.Branch;
                if (!members.TryGetValue(key, out var member))
                {
                    member = new(repository.Id, binding.Branch, await reader.HeadAsync(binding.Branch, token));
                    members.Add(key, member);
                    operation.SelectedRevisionsJson = JsonSerializer.Serialize(members.Values.ToArray(), DefinitionValidation.Json);
                    await database.SaveChangesAsync(token); // Pin the selected member before evaluating any of its code.
                }
                selected = await reader.ReadAsync(member.Commit, control: false, token);
            }
            if (!selected.Sources.TryGetValue(settingsPath + "/Settings.g.cs", out var managed)
                || !selected.Sources.TryGetValue(settingsPath + "/UserCode.cs", out var user))
                throw new InvalidDataException("The selected committed settings are missing.");
            // Do not rerun a nondeterministic control declaration after it selected the source/context.
            var controlJson = JsonSerializer.Serialize(project, DefinitionValidation.Json);
            var frozenControl = "using Vivarium.Definitions; public static class Control { public static Project Declare() => "
                + "System.Text.Json.JsonSerializer.Deserialize<Project>(" + JsonSerializer.Serialize(controlJson)
                + ", new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!; }";
            var output = await EvaluateAsync(database, operation, projectId, "SETTINGS", new Dictionary<string, string>
            { ["Control.g.cs"] = frozenControl, ["Settings.g.cs"] = managed, ["UserCode.cs"] = user }, token);
            var evaluated = DefinitionValidation.Parse(output, projectId);
            if (JsonSerializer.Serialize(evaluated.Project, DefinitionValidation.Json) != controlJson)
                throw new InvalidDataException("The returned control ownership differs.");
            projects.Add(new(project, evaluated, selected.RepositoryId, selected.Commit, settingsPath));
        }
        ValidateGraph(projects);
        var agents = CatalogAgentSettings.Read(control);
        return new(members.Values.OrderBy(m => m.RepositoryId, StringComparer.Ordinal).ThenBy(m => m.Branch, StringComparer.Ordinal).ToArray(),
            projects.ToArray(), IsolatedDefinitionEvaluator.Image, IsolatedDefinitionEvaluator.Sdk, IsolatedDefinitionEvaluator.ContractHash(), "1",
            agents.Length == 0 ? null : agents);
    }

    private async Task<string> EvaluateAsync(VivariumDbContext database, CatalogChange operation, string project,
        string phase, Dictionary<string, string> input, CancellationToken token)
    {
        var identities = input.ToDictionary(p => p.Key, p => IsolatedDefinitionEvaluator.Hash(p.Value), StringComparer.Ordinal);
        identities["$compilerImage"] = IsolatedDefinitionEvaluator.Image;
        identities["$compilerSdk"] = IsolatedDefinitionEvaluator.Sdk;
        identities["$contract"] = IsolatedDefinitionEvaluator.ContractHash(phase == "CONTROL");
        var hashes = JsonSerializer.Serialize(identities.OrderBy(p => p.Key, StringComparer.Ordinal)
            .ToDictionary(p => p.Key, p => p.Value), DefinitionValidation.Json);
        var prior = await database.CatalogEvaluations.Where(e => e.ChangeId == operation.Id && e.ProjectId == project
            && e.Phase == phase && e.State == "SUCCEEDED" && e.CleanupConfirmed).ToArrayAsync(token);
        // JSONB may normalize whitespace/key order; compare values instead of serialized bytes.
        var retained = prior.SingleOrDefault(e => HashesEqual(e.InputHashesJson, hashes));
        if (retained is not null) return retained.OutputJson!;
        if (prior.Length > 0) throw new CatalogEvaluationException("evaluation_inputs_changed");
        var invocation = new CatalogEvaluation
        { Id = Guid.NewGuid(), ChangeId = operation.Id, ProjectId = project, Phase = phase, InputHashesJson = hashes };
        database.CatalogEvaluations.Add(invocation);
        await database.SaveChangesAsync(token);
        var evaluator = new IsolatedDefinitionEvaluator(invocation.Id);
        var result = phase == "CONTROL" ? await evaluator.EvaluateControlAsync(input["Control.g.cs"], token)
            : await evaluator.EvaluateAsync(input, token);
        invocation.CleanupConfirmed = true;
        var failure = result.OutputExceeded ? "evaluation_output_limit" : result.OomKilled ? "evaluation_memory_limit"
            : result.DeadlineExceeded ? "evaluation_cancelled_or_timed_out" : result.ExitCode != 0 ? "evaluation_failed" : null;
        if (failure is null)
        {
            try
            {
                if (phase == "CONTROL") DefinitionValidation.ParseControl(result.Output, project);
                else DefinitionValidation.Parse(result.Output, project);
                invocation.OutputJson = result.Output; invocation.OutputHash = IsolatedDefinitionEvaluator.Hash(result.Output);
                invocation.State = "SUCCEEDED";
            }
            catch (InvalidDataException) { failure = "evaluation_invalid_output"; }
        }
        if (failure is not null)
        {
            invocation.State = "FAILED"; invocation.FailureCode = failure;
            // Export only fixed source-file coordinates and compiler codes, never the authored
            // diagnostic message, runtime exception text, stdout or an arbitrary #line filename.
            var coordinates = System.Text.RegularExpressions.Regex.Matches(result.Error,
                @"\b(?:Control\.g|Settings\.g|UserCode|Program|Model)\.cs\(\d{1,6},\d{1,6}\): error CS\d{4}")
                .Select(match => match.Value).Distinct(StringComparer.Ordinal).Take(2).ToArray();
            if (coordinates.Length > 0) invocation.FailureCode += ":" + string.Join(";", coordinates);
        }
        await database.SaveChangesAsync(token);
        if (failure is not null) throw new CatalogEvaluationException(failure);
        return result.Output;
    }

    private static bool HashesEqual(string left, string right)
    {
        var a = JsonSerializer.Deserialize<Dictionary<string, string>>(left)!;
        var b = JsonSerializer.Deserialize<Dictionary<string, string>>(right)!;
        return a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out var hash) && hash == p.Value);
    }

    private static void ValidateGraph(List<CatalogProject> projects)
    {
        var byId = projects.ToDictionary(p => p.Control.Id, StringComparer.Ordinal);
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var project in projects)
        {
            if (project.Control.Id == "_Root") throw new InvalidDataException("Conflicting Project identity.");
            var seen = new HashSet<string>(StringComparer.Ordinal) { project.Control.Id };
            var parent = project.Control.Parent;
            while (parent != "_Root")
            {
                if (!seen.Add(parent) || !byId.TryGetValue(parent, out var ancestor)) throw new InvalidDataException("Invalid Project ancestry.");
                parent = ancestor.Control.Parent;
            }
            foreach (var id in project.Definition.Configurations.Select(c => c.Id)
                .Concat((project.Definition.Declared.Templates ?? []).Select(t => t.Id)))
                if (!identities.Add(id)) throw new InvalidDataException("Conflicting catalogue identity.");
        }
    }

    private async Task<bool> RefsMatchAsync(CatalogMember[] members, CancellationToken token)
    {
        foreach (var member in members)
        {
            var reader = member.RepositoryId == sources.RepositoryId ? ControlReader()
                : new DefinitionGitReader(BoundRepository(member.RepositoryId).Path, member.RepositoryId);
            if (await reader.HeadAsync(member.Branch, token) != member.Commit) return false;
        }
        return true;
    }

    private DefinitionGitReader ControlReader() => new(sources.Path, sources.RepositoryId);
    private DefinitionRepository BoundRepository(string id) => sources.SettingsRepositories.SingleOrDefault(r => r.Id == id && r.Id != sources.RepositoryId)
        ?? throw new InvalidDataException("The settings repository is not configured.");
    private async Task<bool> CanActivateAsync(VivariumDbContext database, ManagementRequestContext actor, CancellationToken token,
        CatalogChange? operation = null)
    {
        if (actor.Principal.ActorType != "user" || !Guid.TryParse(actor.Principal.ActorId, out var id)) return false;
        var access = new CurrentAccess(database, configuration);
        if (operation?.Kind == "AGENT_ENABLEMENT") return await access.AllowsAsync(id, "enable_disable_agent", cancellationToken: token);
        // This native operation reconciles the entire configured installation catalogue.
        return await access.AllowsAllAsync(id, ["edit_project", "create_sub_project"], cancellationToken: token);
    }

    private async Task<CatalogDefinition> AgentDefinitionAsync(VivariumDbContext database, CatalogChange operation, CancellationToken token)
    {
        if (operation.BaseRevisionId is null || operation.TargetAgentId is null) throw new InvalidDataException("The Agent mutation has no base.");
        var prior = await database.CatalogRevisions.AsNoTracking().SingleAsync(r => r.Id == operation.BaseRevisionId, token);
        var reader = ControlReader();
        var paths = await reader.ChangedPathsAsync(prior.ControlCommit, operation.CandidateCommit, token);
        if (paths.Length > 2 || paths.Any(p => p != ".vivarium/repository.yaml" && p != ".vivarium/agents/" + operation.TargetAgentId + ".yaml"))
            throw new InvalidDataException("The Agent mutation changes another resource.");
        var definition = JsonSerializer.Deserialize<CatalogDefinition>(prior.DefinitionJson, DefinitionValidation.Json)
            ?? throw new InvalidDataException("The active catalogue is invalid.");
        if (IsolatedDefinitionEvaluator.Hash(JsonSerializer.Serialize(definition, DefinitionValidation.Json)) != prior.DefinitionHash)
            throw new InvalidDataException("The active catalogue integrity differs.");
        var candidate = await reader.ReadAsync(operation.CandidateCommit, control: true, token);
        var agents = CatalogAgentSettings.Read(candidate);
        if (!agents.Any(a => a.Id == operation.TargetAgentId)
            || (definition.Agents ?? []).Any(a => a.Id != operation.TargetAgentId && !agents.Contains(a)))
            throw new InvalidDataException("The Agent mutation changes another declaration.");
        // Reuse the already validated C# output: all its source bytes are unchanged. Do not rerun
        // user computations merely because an independent fleet property changed.
        return definition with { Members = definition.Members.Select(m => m.RepositoryId == sources.RepositoryId && m.Branch == "refs/heads/main"
            ? m with { Commit = operation.CandidateCommit } : m).ToArray(), Agents = agents };
    }
    private static ManagementRequestContext Actor(CatalogChange operation) => new(
        new ManagementPrincipal("user", operation.ActorId.ToString(), operation.CredentialKind, null),
        operation.CorrelationId, operation.RequestId, "catalogue-reconcile");
    private async Task RejectAsync(VivariumDbContext database, CatalogChange operation, string code,
        AuditOutcome outcome, CancellationToken token, bool conflict = false)
    {
        operation.State = conflict ? "CONFLICT" : "REJECTED"; operation.FailureCode = code; operation.CompletedAt = time.GetUtcNow();
        new PostgresAuditStore(database).Stage(AuditEventDraft.Create(Actor(operation).WithRequestId(operation.Id.ToString()),
            time.GetUtcNow(), "configuration.activate", "repository", operation.RepositoryId, outcome, code)
            with { ResultRevision = operation.CandidateCommit });
        await database.SaveChangesAsync(token);
    }
    private async Task<T> WithLockAsync<T>(Func<VivariumDbContext, Task<T>> action, CancellationToken token)
    {
        await using var database = await databases.CreateDbContextAsync(token);
        await database.Database.OpenConnectionAsync(token);
        var connection = database.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT pg_try_advisory_lock({CoordinationLock})";
        if (await command.ExecuteScalarAsync(token) is not true) throw new CatalogBusyException();
        try { return await action(database); }
        finally
        {
            if (connection.State == ConnectionState.Open)
            {
                command.CommandText = $"SELECT pg_advisory_unlock({CoordinationLock})";
                await command.ExecuteScalarAsync(CancellationToken.None);
            }
        }
    }
}
