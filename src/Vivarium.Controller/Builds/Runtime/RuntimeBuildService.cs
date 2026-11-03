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
internal sealed record RuntimeRunRequest(string ConfigurationId, Dictionary<string, string>? Parameters = null);
internal sealed record RuntimeBuildView(long Id, string Number, string State, string Status, string StatusText,
    string ConfigurationId, string ProjectId, Guid DefinitionRevisionId, string DefinitionHash,
    DateTimeOffset QueuedAt, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt,
    DateTimeOffset? CancelRequestedAt, string? CancelReason, string? ExecutionPhase, string? AgentId, bool FailedToStart);

internal sealed class RuntimeBuildService(IDbContextFactory<VivariumDbContext> databases, IConfiguration configuration,
    AgentRegistry registry, TimeProvider time)
{
    internal async Task<RuntimeBuildView> SubmitAsync(ManagementRequestContext actor, RuntimeRunRequest request, CancellationToken token)
    {
        try { DefinitionValidation.Identity(request.ConfigurationId); }
        catch (InvalidDataException) { throw Invalid("Supply a valid configuration identity."); }
        var parameters = request.Parameters ?? [];
        if (parameters.Count > 256 || parameters.Any(p => string.IsNullOrWhiteSpace(p.Key) || p.Key.Length > 128 || p.Value is null)
            || Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(parameters)) > 256 * 1024)
            throw Invalid("Build parameter input exceeds its bound.");
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
        var permissions = parameters.Count == 0 ? new[] { "run_build" } : ["run_build", "customize_build_parameters"];
        if (!await RuntimeBuildAccess.AllowsAsync(database, configuration, actor, permissions, ancestry, token))
        {
            StageAudit(database, actor, "build.submit", "build-configuration", request.ConfigurationId, AuditOutcome.Denied);
            await database.SaveChangesAsync(token); await transaction.CommitAsync(token); throw new AccessPermissionException();
        }
        // R0 collects the selected source-free/server settings now, matching its independent queued
        // edit observation. This is not a universal VCS/settings freeze rule or a YAML payload entry.
        if (selected.Project.Definition.Source is not null || selected.Configuration.Snapshots.Length != 0
            || selected.Configuration.Artifacts.Length != 0 || selected.Configuration.Steps.Length > 128)
        {
            StageAudit(database, actor, "build.submit", "build-configuration", request.ConfigurationId, AuditOutcome.Failed);
            await database.SaveChangesAsync(token); await transaction.CommitAsync(token);
            throw new RestApiException(422, "execution_subset_unavailable", "This execution profile is unavailable",
                "The selected R0 runner currently accepts source-free ordinary configurations with at most 128 steps.");
        }
        var type = snapshot.BuildTypeIds[request.ConfigurationId];
        var counter = await database.RuntimeBuildCounters.SingleOrDefaultAsync(c => c.BuildTypeId == type, token);
        if (counter is null) { counter = new() { BuildTypeId = type }; database.RuntimeBuildCounters.Add(counter); }
        var value = counter.NextValue; counter.NextValue = checked(value + 1);
        var now = time.GetUtcNow();
        var build = new RuntimeBuild { BuildTypeId = type, ProjectId = snapshot.ProjectIds[selected.Project.Control.Id],
            Counter = value, Number = value.ToString(CultureInfo.InvariantCulture), ActorId = Guid.Parse(actor.Principal.ActorId),
            CredentialKind = actor.Principal.CredentialKind, CorrelationId = actor.CorrelationId,
            QueuedAt = now };
        database.RuntimeBuilds.Add(build); await database.SaveChangesAsync(token);
        database.RuntimeBuildInputs.Add(new() { BuildId = build.Id, BuildTypeId = build.BuildTypeId, ProjectId = build.ProjectId,
            RevisionId = snapshot.Revision.Id, CollectedAt = now,
            OverridesJson = JsonSerializer.Serialize(new SortedDictionary<string, string>(parameters, StringComparer.Ordinal)),
            AdmissionJson = JsonSerializer.Serialize(new { actorId = build.ActorId, permissions, projectAndAncestors = ancestry,
                catalogueRevisionId = snapshot.Revision.Id, permittedPoolIds = new[] { 0 }, trustClass = "operator-enrolled-physical" }) });
        database.RuntimeBuildOutputs.Add(new() { BuildId = build.Id });
        for (var step = 0; step < selected.Configuration.Steps.Length; step++)
            foreach (var stream in new[] { 0, 1 }) database.RuntimeOutputStreams.Add(new() { BuildId = build.Id, StepIndex = step, Stream = stream });
        StageAudit(database, actor, "build.submit", "build", build.Id.ToString(CultureInfo.InvariantCulture), AuditOutcome.Succeeded,
            snapshot.Revision.ControlCommit);
        var definition = await database.CatalogBuildDefinitions.AsNoTracking().SingleAsync(d => d.BuildTypeId == type && d.RevisionId == snapshot.Revision.Id, token);
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
        var input = await database.RuntimeBuildInputs.AsNoTracking().SingleAsync(i => i.BuildId == id, token);
        var definition = await database.CatalogBuildDefinitions.AsNoTracking().SingleAsync(d => d.BuildTypeId == input.BuildTypeId && d.RevisionId == input.RevisionId, token);
        var type = await database.CatalogBuildTypes.AsNoTracking().SingleAsync(t => t.Id == build.BuildTypeId, token);
        var project = await database.CatalogProjects.AsNoTracking().SingleAsync(p => p.Id == build.ProjectId, token);
        var execution = await database.RuntimeBuildExecutions.AsNoTracking().SingleOrDefaultAsync(e => e.BuildId == id, token);
        return new(build.Id, build.Number, build.State.ToLowerInvariant(), build.Status, build.StatusText, type.ExternalId,
            project.ExternalId, input.RevisionId, definition.PlanHash, build.QueuedAt, build.StartedAt, build.FinishedAt,
            build.CancelRequestedAt, build.CancelReason, execution?.Phase.ToLowerInvariant(), execution?.AgentId, build.FailedToStart);
    }

    internal async Task CancelAsync(ManagementRequestContext actor, long id, string reason, string mode, CancellationToken token)
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
        }
        if (changed) { build.StopMode = mode; build.StopDeadline = time.GetUtcNow().AddSeconds(mode == "FORCE" ? 15 : 30); }
        if (build.State == "QUEUED")
        {
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
    }

    internal async Task<RuntimeBuild?> VisibleAsync(VivariumDbContext database, ManagementRequestContext actor, long id, CancellationToken token)
    {
        var build = await database.RuntimeBuilds.SingleOrDefaultAsync(b => b.Id == id, token);
        return build is not null && await RuntimeBuildAccess.AllowsAsync(database, configuration, actor, ["view_project"],
            await RuntimeBuildAccess.AncestryAsync(database, build.ProjectId, token), token) ? build : null;
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
