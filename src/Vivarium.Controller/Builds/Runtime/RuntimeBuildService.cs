using System.Data;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Vivarium.Controller.Agents;
using Vivarium.Controller.Auditing;
using Vivarium.Controller.Catalog;
using Vivarium.Controller.Identity;
using Vivarium.Controller.Persistence;
using Vivarium.Controller.Rest.Common;
using Vivarium.Controller.Security;
using Vivarium.Definitions;

namespace Vivarium.Controller.Builds.Runtime;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record RuntimeRunRequest(string ConfigurationId, Dictionary<string, string>? Parameters = null,
    Dictionary<string, string>? SecretEnvironment = null);
internal sealed record RuntimeBuildView(long Id, string? Number, string State, string Status, string StatusText,
    string ConfigurationId, string ProjectId, Guid DefinitionRevisionId, string DefinitionHash,
    DateTimeOffset QueuedAt, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt,
    DateTimeOffset? CancelRequestedAt, string? CancelReason, string? ExecutionPhase, string? AgentId, bool FailedToStart)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public RuntimeBuildSelectionView[]? SnapshotDependencies { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public RuntimeBuildSelectionView[]? ArtifactDependencies { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public RuntimeBuildSourceView? Source { get; init; }
}
internal sealed record RuntimeBuildSourceView(string RootId, long RootInstanceId, string Branch, string? SelectedRevision, string? ConsumedRevision, string? CheckoutFailure);
internal sealed record RuntimeBuildSelectionView(long Id, string ConfigurationId, string State, string Status);
internal sealed record RuntimeBuildDependencyHandle
{
    public required string Kind { get; init; }
    public long BuildTypeId { get; init; }
    public long Id { get; init; }
}

internal sealed class RuntimeBuildService(IDbContextFactory<VivariumDbContext> databases, IConfiguration configuration,
    AgentRegistry registry, TimeProvider time, RuntimeBuildSecrets secrets, RuntimeBuildRunInputs runInputs)
{
    internal async Task<RuntimeBuildView> SubmitAsync(ManagementRequestContext actor, RuntimeRunRequest request, CancellationToken token)
    {
        try { DefinitionValidation.Identity(request.ConfigurationId); }
        catch (InvalidDataException) { throw Invalid("Supply a valid configuration identity."); }
        var parameters = request.Parameters is null ? [] : new Dictionary<string, string>(request.Parameters, StringComparer.Ordinal);
        var environment = request.SecretEnvironment ?? [];
        try { RuntimeBuildSecrets.Validate(environment); }
        catch (InvalidDataException) { throw Invalid("Run secret environment is invalid or exceeds its bound."); }
        if (parameters.Count > 256 || parameters.Any(p => string.IsNullOrWhiteSpace(p.Key) || p.Key.Length > 128 || p.Value is null)
            || Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(parameters)) > 256 * 1024)
            throw Invalid("Build parameter input exceeds its bound.");
        var runInputId = RuntimeBuildRunInputs.Extract(parameters);
        if (runInputId is not null && environment.Count != 0)
            throw Invalid("Use either direct run secrets or one prepared run input.");
        await using var database = await databases.CreateDbContextAsync(token);
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        await RuntimeBuildAccess.FencePermissionsAsync(database, token);
        await RuntimeBuildAccess.FenceQueueAsync(database, token);
        var queries = new CatalogQueries(database, configuration, new CurrentAccess(database, configuration));
        var snapshot = await queries.ActiveAsync(token) ?? throw new RestApiException(503, "catalogue_unavailable",
            "The catalogue is unavailable", "Activate a valid C#/Git catalogue before submitting a Build.");
        var selected = snapshot.Definition.Projects.SelectMany(p => p.Definition.Configurations.Select(c => (Project: p, Configuration: c)))
            .SingleOrDefault(p => p.Configuration.Id == request.ConfigurationId);
        if (selected.Configuration is null || !await queries.VisibleAsync(actor, snapshot, selected.Project.Control.Id, token))
        {
            StageAudit(database, actor, "build.submit", "build-configuration", request.ConfigurationId, AuditOutcome.Denied);
            await database.SaveChangesAsync(token); await transaction.CommitAsync(token); throw Missing();
        }
        var ancestry = snapshot.Ancestry(selected.Project.Control.Id);
        var permissions = parameters.Count == 0 && environment.Count == 0 && runInputId is null ? new[] { "run_build" } : ["run_build", "customize_build_parameters"];
        if (!await RuntimeBuildAccess.AllowsAsync(database, configuration, actor, permissions, ancestry, token))
        {
            StageAudit(database, actor, "build.submit", "build-configuration", request.ConfigurationId, AuditOutcome.Denied);
            await database.SaveChangesAsync(token); await transaction.CommitAsync(token); throw new AccessPermissionException();
        }
        // R0 collects the selected source-free/server settings now, matching its independent queued
        // edit observation. This is not a universal VCS/settings freeze rule or a YAML payload entry.
        if (selected.Configuration.Steps.Length > 128)
        {
            StageAudit(database, actor, "build.submit", "build-configuration", request.ConfigurationId, AuditOutcome.Failed);
            await database.SaveChangesAsync(token); await transaction.CommitAsync(token);
            throw new RestApiException(422, "execution_subset_unavailable", "This execution profile is unavailable",
                "The current runner accepts ordinary configurations with at most 128 steps.");
        }
        var source = selected.Project.Definition.Source;
        if (source is not null)
        {
            try { RuntimeBuildSources.Profile(source); }
            catch (InvalidDataException) { throw new RestApiException(422, "execution_subset_unavailable", "Checkout profile unavailable",
                "This source profile requires anonymous Git, a canonical branch and root checkout with literal top-level exclusions."); }
        }
        var now = time.GetUtcNow();
        var configurations = selected.Project.Definition.Configurations.ToDictionary(c => c.Id, StringComparer.Ordinal);
        RuntimeBuildRunInput? preparedInput = null;
        Dictionary<string, Dictionary<string, string>> preparedEnvironments = [];
        if (runInputId is { } inputId)
        {
            try
            {
                (preparedInput, preparedEnvironments) = await runInputs.ReadForAdmissionAsync(database, actor, inputId,
                    snapshot.BuildTypeIds[request.ConfigurationId], snapshot.Revision.Id, token);
            }
            catch (RestApiException error)
            {
                StageAudit(database, actor, "build.submit", "build-configuration", request.ConfigurationId,
                    error.Status == 404 ? AuditOutcome.Denied : AuditOutcome.Failed);
                await database.SaveChangesAsync(token); await transaction.CommitAsync(token); throw;
            }
        }
        long? rootInstance = source is null ? null : await RuntimeBuildSources.InstanceAsync(database, snapshot.ProjectIds[selected.Project.Control.Id], source, token);
        var collected = new Dictionary<string, RuntimeBuild>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        async Task<RuntimeBuild> AdmitAsync(string configurationId)
        {
            if (collected.TryGetValue(configurationId, out var existing)) return existing;
            if (!visiting.Add(configurationId) || visiting.Count + collected.Count > 128)
                throw Invalid("The Build dependency graph is cyclic or exceeds the current bound.");
            if (!configurations.TryGetValue(configurationId, out var config) || config.Steps.Length > 128)
                throw Invalid("A Build dependency definition is unavailable.");
            if (source is not null && config.CheckoutMode != "ON_AGENT")
                throw new RestApiException(422, "execution_subset_unavailable", "Checkout profile unavailable",
                    "This source cohort requires explicit ON_AGENT checkout.");
            if (config.Snapshots.Any(d => d.Failure != "CANCEL" || d.Cancel != "CANCEL" || d.ReuseBuilds && !d.SuccessfulOnly))
                throw new RestApiException(422, "execution_subset_unavailable", "Dependency policy unavailable",
                    "This cohort requires CANCEL/CANCEL edges and successful-only selection when reuse is enabled.");
            foreach (var dependency in config.Snapshots) await AdmitAsync(dependency.Configuration);
            var member = new RuntimeBuild { BuildTypeId = snapshot.BuildTypeIds[configurationId],
                ProjectId = snapshot.ProjectIds[selected.Project.Control.Id], ActorId = Guid.Parse(actor.Principal.ActorId),
                CredentialKind = actor.Principal.CredentialKind, CorrelationId = actor.CorrelationId, QueuedAt = now };
            database.RuntimeBuilds.Add(member); await database.SaveChangesAsync(token);
            var isRoot = configurationId == request.ConfigurationId;
            database.RuntimeBuildInputs.Add(new() { BuildId = member.Id, BuildTypeId = member.BuildTypeId, ProjectId = member.ProjectId,
                RevisionId = snapshot.Revision.Id, CollectedAt = now,
                VcsRootInstanceId = rootInstance, SourceJson = source is null ? null : JsonSerializer.Serialize(source, DefinitionValidation.Json),
                ProtectedEnvironment = secrets.ProtectEnvironment(member.Id,
                    preparedEnvironments.TryGetValue(configurationId, out var preparedEnvironment) ? preparedEnvironment : isRoot ? environment : []),
                OverridesJson = JsonSerializer.Serialize(isRoot ? new SortedDictionary<string, string>(parameters, StringComparer.Ordinal) : []),
                AdmissionJson = JsonSerializer.Serialize(new { actorId = member.ActorId, permissions, projectAndAncestors = ancestry,
                    catalogueRevisionId = snapshot.Revision.Id, permittedPoolIds = new[] { 0 }, trustClass = "operator-enrolled-physical" }) });
            database.RuntimeBuildOutputs.Add(new() { BuildId = member.Id });
            for (var step = 0; step < config.Steps.Length; step++)
                foreach (var stream in new[] { 0, 1 }) database.RuntimeOutputStreams.Add(new() { BuildId = member.Id, StepIndex = step, Stream = stream });
            StageAudit(database, actor, isRoot ? "build.submit" : "build.dependency.admit", "build",
                member.Id.ToString(CultureInfo.InvariantCulture), AuditOutcome.Succeeded, snapshot.Revision.ControlCommit);
            collected.Add(configurationId, member); visiting.Remove(configurationId); return member;
        }
        var build = await AdmitAsync(request.ConfigurationId);
        if (preparedEnvironments.Keys.Any(k => !collected.ContainsKey(k))) throw Invalid("A run input target is outside this dependency graph.");
        foreach (var (configurationId, member) in collected)
        {
            var config = configurations[configurationId];
            foreach (var dependency in config.Snapshots)
                database.RuntimeBuildDependencies.Add(new() { BuildId = member.Id, Kind = "SNAPSHOT",
                    UpstreamBuildTypeId = collected[dependency.Configuration].BuildTypeId,
                    UpstreamBuildId = collected[dependency.Configuration].Id, SelectedAt = now });
            foreach (var dependency in config.Artifacts)
            {
                if (dependency.Selector != "sameChainOrLastFinished" || !collected.TryGetValue(dependency.Configuration, out var producer))
                    throw new RestApiException(422, "execution_subset_unavailable", "Artifact selection unavailable",
                        "This cohort requires an artifact producer selected in the same snapshot chain.");
                database.RuntimeBuildDependencies.Add(new() { BuildId = member.Id, Kind = "ARTIFACT",
                    UpstreamBuildTypeId = producer.BuildTypeId, UpstreamBuildId = producer.Id, SelectedAt = now });
            }
        }
        var definition = await database.CatalogBuildDefinitions.AsNoTracking().SingleAsync(d => d.BuildTypeId == build.BuildTypeId && d.RevisionId == snapshot.Revision.Id, token);
        if (preparedInput is not null) runInputs.Consume(database, actor, preparedInput, build.Id, snapshot.Revision.ControlCommit);
        await database.SaveChangesAsync(token); await transaction.CommitAsync(token);
        return new(build.Id, build.Number, "queued", build.Status, build.StatusText, request.ConfigurationId,
            selected.Project.Control.Id, snapshot.Revision.Id, definition.PlanHash, now, null, null, null, null, null, null, false);
    }

    internal async Task<RuntimeBuildView?> GetAsync(ManagementRequestContext actor, long id, CancellationToken token)
    {
        await using var database = await databases.CreateDbContextAsync(token);
        await using var snapshot = await database.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        var build = await VisibleAsync(database, actor, id, token);
        if (build is null) return null;
        id = build.Id;
        var input = await database.RuntimeBuildInputs.AsNoTracking().SingleAsync(i => i.BuildId == id, token);
        var definition = await database.CatalogBuildDefinitions.AsNoTracking().SingleAsync(d => d.BuildTypeId == input.BuildTypeId && d.RevisionId == input.RevisionId, token);
        var type = await database.CatalogBuildTypes.AsNoTracking().SingleAsync(t => t.Id == build.BuildTypeId, token);
        var project = await database.CatalogProjects.AsNoTracking().SingleAsync(p => p.Id == build.ProjectId, token);
        var execution = await database.RuntimeBuildExecutions.AsNoTracking().SingleOrDefaultAsync(e => e.BuildId == id, token);
        var publicState = build.State switch
        {
            "RELEASING" => "running",
            _ => build.State.ToLowerInvariant()
        };
        var selections = await SelectionViewsAsync(database, id, token);
        RuntimeBuildSourceView? sourceView = null;
        if (input.SourceJson is not null)
        {
            var source = JsonSerializer.Deserialize<Source>(input.SourceJson, DefinitionValidation.Json)!;
            var selection = await database.RuntimeBuildSourceSelections.AsNoTracking().SingleOrDefaultAsync(s => s.BuildId == id, token);
            var receipt = await database.RuntimeBuildSourceReceipts.AsNoTracking().SingleOrDefaultAsync(s => s.BuildId == id, token);
            var checkout = receipt is null ? null : Vivarium.Contracts.V1.VcsCheckoutReceipt.Parser.ParseFrom(receipt.Evidence);
            sourceView = new(source.RootId, input.VcsRootInstanceId!.Value, source.Branch, selection?.Version,
                checkout is { Succeeded: true } ? checkout.Revision : null, checkout is { Succeeded: false } ? checkout.FailureCode : null);
        }
        return new(build.Id, build.Number, publicState, build.Status, build.StatusText, type.ExternalId,
            project.ExternalId, input.RevisionId, definition.PlanHash, build.QueuedAt, build.StartedAt, build.FinishedAt,
            build.CancelRequestedAt, build.CancelReason, execution?.Phase.ToLowerInvariant(), execution?.AgentId, build.FailedToStart)
        {
            Source = sourceView,
            SnapshotDependencies = selections.Any(d => d.Kind == "SNAPSHOT") ? selections.Where(d => d.Kind == "SNAPSHOT").Select(d => d.Build).ToArray() : null,
            ArtifactDependencies = selections.Any(d => d.Kind == "ARTIFACT") ? selections.Where(d => d.Kind == "ARTIFACT").Select(d => d.Build).ToArray() : null,
        };
    }

    internal static async Task<(string Kind, RuntimeBuildSelectionView Build)[]> SelectionViewsAsync(VivariumDbContext database, long id, CancellationToken token)
    {
        var rows = await DependencyHandles(database, id)
            .Join(database.RuntimeBuilds.AsNoTracking(), d => d.Id, upstream => upstream.Id, (d, upstream) => new { d.Kind, Build = upstream })
            .Join(database.CatalogBuildTypes.AsNoTracking(), d => d.Build.BuildTypeId, t => t.Id,
                (d, t) => new { d.Kind, d.Build.Id, d.Build.State, d.Build.Status, t.ExternalId })
            .OrderBy(d => d.Id).ToArrayAsync(token);
        return rows.Select(d => (d.Kind, new RuntimeBuildSelectionView(d.Id, d.ExternalId,
            d.State == "RELEASING" ? "running" : d.State.ToLowerInvariant(), d.Status))).ToArray();
    }

    internal static IQueryable<RuntimeBuildDependencyHandle> DependencyHandles(VivariumDbContext database, long id) =>
        from dependency in database.RuntimeBuildDependencies.AsNoTracking()
        where dependency.BuildId == id
        join resolution in database.RuntimeBuildResolutions.AsNoTracking() on dependency.UpstreamBuildId equals resolution.RequestedBuildId into matches
        from resolution in matches.DefaultIfEmpty()
        select new RuntimeBuildDependencyHandle { Kind = dependency.Kind, BuildTypeId = dependency.UpstreamBuildTypeId,
            Id = resolution == null ? dependency.UpstreamBuildId : resolution.ResolvedBuildId };

    internal async Task<long> CancelAsync(ManagementRequestContext actor, long id, string reason, string mode, CancellationToken token)
    {
        if (reason.Length > 1024 || mode is not ("GRACEFUL" or "FORCE")) throw Invalid("Cancellation input is invalid.");
        await using var database = await databases.CreateDbContextAsync(token);
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        await RuntimeBuildAccess.FencePermissionsAsync(database, token); await RuntimeBuildAccess.FenceQueueAsync(database, token);
        var build = await VisibleAsync(database, actor, id, token);
        if (build is null)
        {
            StageAudit(database, actor, "build.cancel", "build", id.ToString(CultureInfo.InvariantCulture), AuditOutcome.Denied);
            await database.SaveChangesAsync(token); await transaction.CommitAsync(token); throw Missing();
        }
        id = build.Id;
        var permissions = mode == "FORCE" ? new[] { "cancel_build", AuthorizationPermissionIds.BuildForceStop } : ["cancel_build"];
        if (!await RuntimeBuildAccess.AllowsAsync(database, configuration, actor, permissions,
            await RuntimeBuildAccess.AncestryAsync(database, build.ProjectId, token), token))
        {
            StageAudit(database, actor, "build.cancel", "build", id.ToString(CultureInfo.InvariantCulture), AuditOutcome.Denied);
            await database.SaveChangesAsync(token); await transaction.CommitAsync(token); throw new AccessPermissionException();
        }
        var execution = await database.RuntimeBuildExecutions.SingleOrDefaultAsync(e => e.BuildId == id, token);
        if (execution is not null)
        {
            await AgentCredentials.LockAgentAsync(database, execution.AgentId, token);
            await database.Entry(build).ReloadAsync(token); await database.Entry(execution).ReloadAsync(token);
        }
        if (build.State == "FINISHED")
        {
            StageAudit(database, actor, "build.cancel", "build", id.ToString(CultureInfo.InvariantCulture), AuditOutcome.Failed);
            await database.SaveChangesAsync(token); await transaction.CommitAsync(token);
            throw new RestApiException(409, "build_already_finished", "The Build has finished", "A finished Build cannot be cancelled.");
        }
        var changed = build.CancelRequestedAt is null || build.StopMode == "GRACEFUL" && mode == "FORCE";
        if (build.CancelRequestedAt is null)
        {
            build.CancelRequestedAt = time.GetUtcNow(); build.CancelReason = reason; build.CancelOperationId = Guid.NewGuid();
            build.CancelActorId = Guid.Parse(actor.Principal.ActorId);
        }
        if (changed) { build.StopMode = mode; build.StopDeadline = time.GetUtcNow().AddSeconds(mode == "FORCE" ? 15 : 30); }
        build.Status = "UNKNOWN";
        if (!build.StatusText.StartsWith("Canceled", StringComparison.Ordinal))
            build.StatusText = build.StatusText.StartsWith("Exit code ", StringComparison.Ordinal) ? "Canceled (" + build.StatusText + ")" : "Canceled";
        if (build.State == "QUEUED")
        {
            build.Number = "N/A";
            build.State = "FINISHED"; build.Status = "UNKNOWN"; build.StatusText = "Cancelled before execution"; build.FinishedAt = time.GetUtcNow();
            var output = await database.RuntimeBuildOutputs.SingleAsync(o => o.BuildId == id, token); output.State = "COMPLETE";
            foreach (var stream in await database.RuntimeOutputStreams.Where(s => s.BuildId == id).ToArrayAsync(token))
            { stream.FinalSequence = 0; stream.FinalBytes = 0; }
        }
        StageAudit(database, actor, mode == "FORCE" ? "build.force-stop" : "build.cancel", "build", id.ToString(CultureInfo.InvariantCulture),
            changed ? AuditOutcome.Succeeded : AuditOutcome.NoChange);
        await database.SaveChangesAsync(token); await transaction.CommitAsync(token);
        if (execution is not null && registry.TryGetBuildConnection(execution.AgentId, execution.WorkloadId, out var connection))
            registry.TrySend(connection!, CancelMessage(build, execution));
        return id;
    }

    internal async Task<RuntimeBuild?> VisibleAsync(VivariumDbContext database, ManagementRequestContext actor, long id, CancellationToken token,
        bool discloseDenied = false)
    {
        id = await RuntimeBuildAccess.ResolveAsync(database, id, token);
        var build = await database.RuntimeBuilds.SingleOrDefaultAsync(b => b.Id == id, token);
        if (build is null) return null;
        if (await RuntimeBuildAccess.AllowsAsync(database, configuration, actor, ["view_project"],
            await RuntimeBuildAccess.AncestryAsync(database, build.ProjectId, token), token)) return build;
        if (discloseDenied) throw new AccessPermissionException();
        return null;
    }

    internal static Vivarium.Contracts.V1.ControllerMsg CancelMessage(RuntimeBuild build, RuntimeBuildExecution execution) => new()
    {
        Cancel = new() { BuildId = execution.WorkloadId, Reason = build.CancelReason ?? "",
            OperationId = build.CancelOperationId?.ToString() ?? "", DeadlineUnixMs = build.StopDeadline?.ToUnixTimeMilliseconds() ?? 0,
            Mode = build.StopMode == "FORCE" ? Vivarium.Contracts.V1.BuildStopMode.Force : Vivarium.Contracts.V1.BuildStopMode.Graceful },
    };
    private void StageAudit(VivariumDbContext database, ManagementRequestContext actor, string action, string type,
        string id, AuditOutcome outcome, string? revision = null) => new PostgresAuditStore(database).Stage(
            AuditEventDraft.Create(actor, time.GetUtcNow(), action, type, id, outcome) with { ResultRevision = revision });
    internal static RestApiException Missing() => new(404, "resource_not_found", "Build is not found", "The Build does not exist or is not visible.");
    private static RestApiException Invalid(string detail) => new(400, "invalid_request", "Invalid Build request", detail);
}
