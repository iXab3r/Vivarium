using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Vivarium.Contracts.V1;
using Vivarium.Contracts;
using Vivarium.Controller.Agents;
using Vivarium.Controller.Agents.Compatibility;
using Vivarium.Controller.Catalog;
using Vivarium.Controller.Persistence;
using Vivarium.Definitions;

namespace Vivarium.Controller.Builds.Runtime;

/// <summary>One PG-owned queue/lease lifecycle. Live sessions are delivery handles, never owners.</summary>
internal sealed class RuntimeBuildCoordinator(IDbContextFactory<VivariumDbContext> databases,
    RuntimeAgentSessions sessions, AgentRegistry registry, RuntimeStepCompiler compiler,
    RuntimeBuildOutputStore output, TimeProvider time, ILogger<RuntimeBuildCoordinator> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal async Task RunAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) when (error is System.Data.Common.DbException or DbUpdateException or IOException or TimeoutException
                || error is OperationCanceledException && !stoppingToken.IsCancellationRequested)
            {
                // Neither SQL diagnostics nor runner/parameter values belong in Host diagnostics.
                logger.LogWarning("Build coordination is unavailable; durable ownership is retained.");
            }
            await Task.Delay(TimeSpan.FromMilliseconds(250), time, stoppingToken);
        }
    }

    internal async Task TickAsync(CancellationToken token)
    {
        await sessions.ExpireSilentAsync(token);
        await using (var database = await databases.CreateDbContextAsync(token))
        {
            var active = await database.RuntimeBuildExecutions.AsNoTracking().Where(e => e.ReleasedAt == null)
                .OrderBy(e => e.BuildId).Select(e => e.BuildId).ToArrayAsync(token);
            foreach (var id in active)
            {
                await output.SealAsync(id, token);
                await AdvanceAsync(id, token);
            }
        }
        var claimed = await ClaimAsync(token);
        if (claimed is not null) await AdvanceAsync(claimed.Value, token);
    }

    private async Task<long?> ClaimAsync(CancellationToken token)
    {
        await using var database = await databases.CreateDbContextAsync(token);
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        await RuntimeBuildAccess.FenceQueueAsync(database, token);
        var queued = await database.RuntimeBuilds.Where(b => b.State == "QUEUED")
            .OrderBy(b => b.QueuedAt).ThenBy(b => b.Id).ToArrayAsync(token);
        foreach (var build in queued)
        {
            if (build.QueueDeadline <= time.GetUtcNow())
            {
                await FinishUnassignedAsync(database, build, "Queue wait timeout expired", token);
                continue;
            }
            var input = await database.RuntimeBuildInputs.AsNoTracking().SingleAsync(i => i.BuildId == build.Id, token);
            var definition = await database.CatalogBuildDefinitions.AsNoTracking()
                .SingleAsync(d => d.BuildTypeId == input.BuildTypeId && d.RevisionId == input.RevisionId, token);
            var config = ReadDefinition(definition);
            var candidates = await database.AgentRegistrations.AsNoTracking().Where(a => a.DeletedAt == null && a.Authorized
                && a.DesiredEnabled && a.DesiredRevision != null && a.Reconciled && !a.Quarantined
                && !a.MaintenanceDrain && a.CurrentWorkloadId == null && a.SessionAuthenticated)
                .OrderBy(a => a.TeamCityId).Select(a => a.AgentId).ToArrayAsync(token);
            foreach (var id in candidates)
            {
                await AgentCredentials.LockAgentAsync(database, id, token);
                var agent = await database.AgentRegistrations.SingleAsync(a => a.AgentId == id, token);
                if (!Eligible(agent) || !sessions.TryGetCurrent(id, out var connection)
                    || !Current(agent, connection!) || !connection!.Protocol.SupportsDurableBuildOutput
                    || !connection.Protocol.SupportsLiteralArtifactPublisher) continue;
                var reported = JsonSerializer.Deserialize<Dictionary<string, string>>(agent.ReportedParametersJson, Json)!;
                var custom = JsonSerializer.Deserialize<Dictionary<string, string>>(agent.CustomParametersJson, Json)!;
                var parameters = new Dictionary<string, string>(reported, StringComparer.Ordinal);
                foreach (var pair in custom) parameters[pair.Key] = pair.Value;
                if (config.Requirements.Any(r => !parameters.TryGetValue(r.Name, out var value) || value != r.Value)) continue;
                var now = time.GetUtcNow();
                var execution = new RuntimeBuildExecution
                {
                    BuildId = build.Id, WorkloadId = Guid.NewGuid().ToString("N"), AgentId = id,
                    LeaseFence = checked(++agent.WorkloadFence), CredentialGeneration = agent.CredentialGeneration,
                    AssignedSessionId = agent.CurrentSessionId!, AssignedConnectionGeneration = agent.ConnectionGeneration,
                    CurrentSessionId = agent.CurrentSessionId!, CurrentConnectionGeneration = agent.ConnectionGeneration,
                    PreparedAt = now, PreparationDeadline = now.AddMinutes(2), Assignment = [],
                    SelectionSnapshotJson = JsonSerializer.Serialize(new { agent.AgentId, agent.TeamCityId,
                        agent.ReportedName, agent.DesiredName, reportedParameters = reported, customParameters = custom,
                        agent.CredentialGeneration, agent.ConnectionGeneration, agent.CurrentSessionId,
                        negotiatedCapabilities = JsonSerializer.Deserialize<JsonElement>(agent.NegotiatedCapabilitiesJson),
                        agent.DesiredRevision, poolId = 0, trustClass = "operator-enrolled-physical",
                        cleanPolicy = "clean-workdir", input.RevisionId, definition.PlanHash }, Json),
                };
                database.RuntimeBuildExecutions.Add(execution); agent.CurrentWorkloadId = execution.WorkloadId;
                build.State = "RUNNING"; build.StartedAt = now; build.StatusText = "Preparing execution";
                await database.SaveChangesAsync(token); await transaction.CommitAsync(token);
                registry.Reconcile(connection.Handle, execution.WorkloadId);
                return build.Id;
            }
        }
        await database.SaveChangesAsync(token); await transaction.CommitAsync(token);
        return null;
    }

    private async Task AdvanceAsync(long id, CancellationToken token)
    {
        RuntimeBuildExecution snapshot;
        RuntimeBuild build;
        RuntimeBuildInput input;
        CatalogBuildDefinition definition;
        string os;
        await using (var database = await databases.CreateDbContextAsync(token))
        {
            snapshot = await database.RuntimeBuildExecutions.AsNoTracking().SingleAsync(e => e.BuildId == id, token);
            if (snapshot.ReleasedAt is not null) return;
            build = await database.RuntimeBuilds.AsNoTracking().SingleAsync(b => b.Id == id, token);
            input = await database.RuntimeBuildInputs.AsNoTracking().SingleAsync(i => i.BuildId == id, token);
            definition = await database.CatalogBuildDefinitions.AsNoTracking()
                .SingleAsync(d => d.BuildTypeId == input.BuildTypeId && d.RevisionId == input.RevisionId, token);
            using var selection = JsonDocument.Parse(snapshot.SelectionSnapshotJson);
            os = selection.RootElement.GetProperty("reportedParameters").GetProperty("system.os.family").GetString()!;
        }
        RuntimePreparedAssignment? prepared = null;
        if (snapshot.Phase == "PREPARING" && build.CancelRequestedAt is null && time.GetUtcNow() < snapshot.PreparationDeadline)
        {
            try { prepared = await compiler.PrepareAsync(build, input, definition, snapshot, os, token); }
            catch (Exception error) when (error is InvalidDataException or HttpRequestException or IOException or TimeoutException
                || error is OperationCanceledException && !token.IsCancellationRequested)
            { await FailAsync(id, "Execution preparation failed", safeUnsent: true, token); return; }
        }
        await using var state = await databases.CreateDbContextAsync(token);
        await using var transaction = await state.Database.BeginTransactionAsync(token);
        await AgentCredentials.LockAgentAsync(state, snapshot.AgentId, token);
        var owner = await state.RuntimeBuildExecutions.SingleAsync(e => e.BuildId == id, token);
        var currentBuild = await state.RuntimeBuilds.SingleAsync(b => b.Id == id, token);
        var currentAgent = await state.AgentRegistrations.SingleAsync(a => a.AgentId == owner.AgentId, token);
        if (owner.ReleasedAt is not null) return;
        var now = time.GetUtcNow();
        var connected = sessions.TryGetCurrent(owner.AgentId, out var connection) && Current(currentAgent, connection!)
            && Owner(currentAgent, owner, connection!);
        if (!connected) owner.ReconnectDeadline ??= (currentAgent.LastHeartbeatAt ?? owner.PreparedAt).AddSeconds(45);
        var expired = owner.Phase == "PREPARING" && now >= owner.PreparationDeadline
            || owner.Phase == "DISPATCHING" && owner.AcknowledgementDeadline <= now
            || owner.ReconnectDeadline <= now || currentBuild.StopDeadline <= now && owner.TerminalReceivedAt is null;
        if (expired)
        {
            await transaction.RollbackAsync(token);
            await FailAsync(id, "Execution ownership deadline expired", owner.SentAt is null, token); return;
        }
        if (owner.Phase == "ACCEPTED" && owner.ExecutionDeadline <= now && currentBuild.CancelRequestedAt is null)
        {
            currentBuild.CancelRequestedAt = now; currentBuild.CancelReason = "Execution timeout expired";
            currentBuild.CancelOperationId = Guid.NewGuid(); currentBuild.StopMode = "FORCE"; currentBuild.StopDeadline = now.AddSeconds(15);
        }
        if (currentBuild.CancelRequestedAt is not null && owner.SentAt is null)
        {
            await transaction.RollbackAsync(token); await FailAsync(id, "Cancelled before execution", safeUnsent: true, token); return;
        }
        if (prepared is not null && owner.Phase == "PREPARING" && connected && EligibleForDelivery(currentAgent))
        {
            var reported = JsonSerializer.Deserialize<Dictionary<string, string>>(currentAgent.ReportedParametersJson, Json)!;
            var custom = JsonSerializer.Deserialize<Dictionary<string, string>>(currentAgent.CustomParametersJson, Json)!;
            var effective = new Dictionary<string, string>(reported, StringComparer.Ordinal);
            foreach (var pair in custom) effective[pair.Key] = pair.Value;
            if (reported.GetValueOrDefault("system.os.family") != os || ReadDefinition(definition).Requirements
                .Any(r => !effective.TryGetValue(r.Name, out var value) || value != r.Value))
            {
                await transaction.RollbackAsync(token);
                await FailAsync(id, "Execution target changed before delivery", safeUnsent: true, token); return;
            }
            owner.Assignment = prepared.Assignment.ToByteArray(); owner.Phase = "DISPATCHING";
            owner.TargetSnapshotJson = JsonSerializer.Serialize(new { selection = JsonSerializer.Deserialize<JsonElement>(owner.SelectionSnapshotJson),
                currentAgent.AgentId, currentAgent.TeamCityId, currentAgent.ReportedName, currentAgent.DesiredName,
                reportedParameters = reported, customParameters = custom, currentAgent.CredentialGeneration,
                currentAgent.CurrentSessionId, currentAgent.ConnectionGeneration, currentAgent.DesiredRevision,
                negotiatedCapabilities = JsonSerializer.Deserialize<JsonElement>(currentAgent.NegotiatedCapabilitiesJson) }, Json);
            owner.AcknowledgementDeadline = now.AddSeconds(30);
            foreach (var payload in prepared.Payloads) state.RuntimeBuildPayloads.Add(payload);
            currentBuild.StatusText = "Starting execution";
        }
        ControllerMsg? message = null;
        if (connected && owner.Phase == "DISPATCHING" && EligibleForDelivery(currentAgent))
        {
            owner.SentAt ??= now;
            message = new() { Build = BuildAssignment.Parser.ParseFrom(owner.Assignment) };
        }
        else if (connected && currentBuild.CancelRequestedAt is not null && owner.TerminalReceivedAt is null)
            message = RuntimeBuildService.CancelMessage(currentBuild, owner);
        await state.SaveChangesAsync(token); await transaction.CommitAsync(token);
        if (message is not null) registry.TrySend(connection!.Handle, message);
    }

    internal async Task ReceiveAsync(RuntimeAgentConnection connection, AgentMsg message, CancellationToken token)
    {
        var (workload, session) = message.MsgCase switch
        {
            AgentMsg.MsgOneofCase.AssignmentAccepted => (message.AssignmentAccepted.BuildId, message.AssignmentAccepted.SessionId),
            AgentMsg.MsgOneofCase.Result => (message.Result.BuildId, message.Result.SessionId),
            AgentMsg.MsgOneofCase.BuildStopAcknowledged => (message.BuildStopAcknowledged.BuildId, message.BuildStopAcknowledged.SessionId),
            AgentMsg.MsgOneofCase.Status => (message.Status.BuildId, connection.Handle.SessionId),
            _ => throw new InvalidDataException("Unexpected workload message."),
        };
        if (session != connection.Handle.SessionId || workload.Length == 0) throw new InvalidDataException("Workload session differs.");
        await using var database = await databases.CreateDbContextAsync(token);
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        await AgentCredentials.LockAgentAsync(database, connection.Handle.AgentId, token);
        var agent = await database.AgentRegistrations.SingleAsync(a => a.AgentId == connection.Handle.AgentId, token);
        var execution = await database.RuntimeBuildExecutions.SingleOrDefaultAsync(e => e.WorkloadId == workload, token);
        if (execution is null || !Owner(agent, execution, connection) || execution.SentAt is null)
            throw new InvalidDataException("Workload ownership differs.");
        var build = await database.RuntimeBuilds.SingleAsync(b => b.Id == execution.BuildId, token);
        if (message.MsgCase is AgentMsg.MsgOneofCase.AssignmentAccepted or AgentMsg.MsgOneofCase.Result)
        {
            execution.AcceptedAt ??= time.GetUtcNow();
            if (execution.Phase == "DISPATCHING") execution.Phase = "ACCEPTED";
            var input = await database.RuntimeBuildInputs.SingleAsync(i => i.BuildId == build.Id, token);
            var definition = await database.CatalogBuildDefinitions.SingleAsync(d => d.BuildTypeId == input.BuildTypeId && d.RevisionId == input.RevisionId, token);
            var minutes = ReadDefinition(definition).TimeoutMinutes;
            if (minutes > 0) execution.ExecutionDeadline ??= execution.AcceptedAt.Value.AddMinutes(minutes);
        }
        ControllerMsg? acknowledgement = null;
        if (message.MsgCase == AgentMsg.MsgOneofCase.Result)
        {
            var result = message.Result;
            var assignment = BuildAssignment.Parser.ParseFrom(execution.Assignment);
            ValidateResult(result, assignment);
            var semantic = result.Clone(); semantic.SessionId = "";
            var hash = Convert.ToHexStringLower(SHA256.HashData(semantic.ToByteArray()));
            var existing = await database.RuntimeBuildResults.SingleOrDefaultAsync(r => r.BuildId == build.Id, token);
            if (existing is not null && existing.SemanticHash != hash) throw new InvalidDataException("Terminal evidence conflicts.");
            if (existing is null)
            {
                if (result.Artifacts.Count > 128 || result.Artifacts.Select(a => a.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != result.Artifacts.Count)
                    throw new InvalidDataException("Artifact manifest exceeds its bound or aliases paths.");
                for (var ordinal = 0; ordinal < result.Artifacts.Count; ordinal++)
                {
                    var artifact = result.Artifacts[ordinal]; LiteralArtifactPath.Validate(artifact.Path);
                    if (!System.Text.RegularExpressions.Regex.IsMatch(artifact.Sha256, "^[a-f0-9]{64}$") || artifact.Size is < 0 or > 65536)
                        throw new InvalidDataException("Artifact content metadata is invalid.");
                    var upload = await database.RuntimeArtifactUploads.SingleOrDefaultAsync(u => u.BuildId == build.Id && u.Sha256 == artifact.Sha256, token);
                    if (upload is null || artifact.Size != upload.Size) throw new InvalidDataException("Artifact content is not owned/verified.");
                    database.RuntimeBuildArtifacts.Add(new() { BuildId = build.Id, Ordinal = ordinal, Path = artifact.Path,
                        Sha256 = artifact.Sha256, Size = artifact.Size });
                }
                database.RuntimeBuildResults.Add(new() { BuildId = build.Id, Evidence = result.ToByteArray(), SemanticHash = hash,
                    OriginalOutcome = result.Outcome.ToString(), ReceivedAt = time.GetUtcNow() });
                execution.Phase = "TERMINAL"; execution.TerminalReceivedAt = time.GetUtcNow();
                execution.OutputDeadline = time.GetUtcNow().AddSeconds(30); build.State = "RELEASING";
                var output = await database.RuntimeBuildOutputs.SingleAsync(o => o.BuildId == build.Id, token); output.State = "SEALING";
                foreach (var watermark in result.OutputWatermarks)
                {
                    var stream = await database.RuntimeOutputStreams.SingleAsync(s => s.BuildId == build.Id
                        && s.StepIndex == watermark.StepIndex && s.Stream == (int)watermark.Stream, token);
                    if (stream.CommittedSequence > watermark.FinalSequenceExclusive || stream.CommittedBytes > watermark.ProducedBytes
                        || stream.CommittedSequence == watermark.FinalSequenceExclusive && stream.CommittedBytes != watermark.ProducedBytes)
                        throw new InvalidDataException("Output watermark regresses committed evidence.");
                    stream.FinalSequence = watermark.FinalSequenceExclusive; stream.FinalBytes = watermark.ProducedBytes;
                }
            }
            acknowledgement = new() { ResultAccepted = new() { BuildId = workload, SessionId = session } };
        }
        else if (message.MsgCase == AgentMsg.MsgOneofCase.BuildStopAcknowledged)
        {
            var acknowledged = message.BuildStopAcknowledged;
            if (acknowledged.OperationId == build.CancelOperationId?.ToString()
                && acknowledged.Mode == (build.StopMode == "FORCE" ? BuildStopMode.Force : BuildStopMode.Graceful))
                build.StopAcknowledgedAt ??= time.GetUtcNow();
        }
        await database.SaveChangesAsync(token); await transaction.CommitAsync(token);
        if (acknowledgement is not null) registry.TrySend(connection.Handle, acknowledgement);
    }

    private async Task FailAsync(long id, string reason, bool safeUnsent, CancellationToken token)
    {
        await using var database = await databases.CreateDbContextAsync(token);
        var key = await database.RuntimeBuildExecutions.AsNoTracking().Where(e => e.BuildId == id).Select(e => e.AgentId).SingleAsync(token);
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        await AgentCredentials.LockAgentAsync(database, key, token);
        var execution = await database.RuntimeBuildExecutions.SingleAsync(e => e.BuildId == id, token);
        if (execution.ReleasedAt is not null || execution.TerminalReceivedAt is not null) return;
        var build = await database.RuntimeBuilds.SingleAsync(b => b.Id == id, token);
        var agent = await database.AgentRegistrations.SingleAsync(a => a.AgentId == key, token);
        // A durable send intent always wins over a caller's earlier unsent observation.
        safeUnsent &= execution.SentAt is null;
        build.State = "FINISHED"; build.Status = "UNKNOWN"; build.StatusText = reason;
        build.FailedToStart = execution.AcceptedAt is null; build.FinishedAt = time.GetUtcNow();
        execution.Phase = "RELEASED"; execution.ReleasedAt = time.GetUtcNow();
        if (safeUnsent && agent.CurrentWorkloadId == execution.WorkloadId) agent.CurrentWorkloadId = null;
        else { agent.Quarantined = true; agent.Health = "UNHEALTHY"; agent.HealthReason = "workload_ownership_expired"; }
        var output = await database.RuntimeBuildOutputs.SingleAsync(o => o.BuildId == id, token);
        output.State = safeUnsent ? "COMPLETE" : "COMPLETE_WITH_GAPS";
        if (safeUnsent)
            foreach (var stream in await database.RuntimeOutputStreams.Where(s => s.BuildId == id).ToArrayAsync(token))
            { stream.FinalSequence = 0; stream.FinalBytes = 0; }
        else
            foreach (var stream in await database.RuntimeOutputStreams.Where(s => s.BuildId == id).OrderBy(s => s.StepIndex).ThenBy(s => s.Stream).ToArrayAsync(token))
            {
                foreach (var range in await database.RuntimeOutputRanges.Where(r => r.BuildId == id && r.StepIndex == stream.StepIndex
                    && r.Stream == stream.Stream && r.Position == null).OrderBy(r => r.Start).ToArrayAsync(token))
                    range.Position = output.NextPosition++;
                database.RuntimeOutputRanges.Add(new() { Id = Guid.NewGuid(), BuildId = id, StepIndex = stream.StepIndex, Stream = stream.Stream,
                    Start = stream.CommittedSequence, ByteStart = stream.CommittedBytes, Kind = "GAP", Reason = "producer-lost",
                    Position = output.NextPosition++, ReceivedAt = time.GetUtcNow() });
            }
        await database.SaveChangesAsync(token); await transaction.CommitAsync(token);
        if (!safeUnsent) registry.Quarantine(key, agent.HealthReason);
        else if (sessions.TryGetCurrent(key, out var connection)) registry.Reconcile(connection!.Handle, null);
    }

    private async Task FinishUnassignedAsync(VivariumDbContext database, RuntimeBuild build, string reason, CancellationToken token)
    {
        build.State = "FINISHED"; build.Status = "UNKNOWN"; build.StatusText = reason;
        build.FailedToStart = true; build.FinishedAt = time.GetUtcNow();
        var output = await database.RuntimeBuildOutputs.SingleAsync(o => o.BuildId == build.Id, token); output.State = "COMPLETE";
        foreach (var stream in await database.RuntimeOutputStreams.Where(s => s.BuildId == build.Id).ToArrayAsync(token))
        { stream.FinalSequence = 0; stream.FinalBytes = 0; }
    }

    internal static bool Current(AgentRegistration agent, RuntimeAgentConnection connection) => connection.Credential.BearerProven
        && agent.DeletedAt is null && agent.SessionAuthenticated && agent.CurrentSessionId == connection.Handle.SessionId
        && agent.ConnectionGeneration == connection.Handle.ConnectionGeneration
        && agent.CredentialGeneration == connection.Credential.Admission.CredentialGeneration
        && agent.SessionCredentialGeneration == agent.CredentialGeneration;
    internal static bool Owner(AgentRegistration agent, RuntimeBuildExecution execution, RuntimeAgentConnection connection) =>
        Current(agent, connection) && execution.AgentId == agent.AgentId && execution.CredentialGeneration == agent.CredentialGeneration
        && execution.CurrentSessionId == agent.CurrentSessionId && execution.CurrentConnectionGeneration == agent.ConnectionGeneration
        && execution.WorkloadId == agent.CurrentWorkloadId && execution.LeaseFence == agent.WorkloadFence && execution.ReleasedAt is null;
    private static bool EligibleForDelivery(AgentRegistration agent) => agent.DeletedAt is null && agent.Authorized && agent.DesiredEnabled
        && agent.DesiredRevision is not null && agent.Reconciled && !agent.Quarantined && !agent.MaintenanceDrain && agent.SessionAuthenticated;
    private static bool Eligible(AgentRegistration agent) => EligibleForDelivery(agent) && agent.CurrentWorkloadId is null;
    private static ResolvedConfiguration ReadDefinition(CatalogBuildDefinition definition) =>
        JsonSerializer.Deserialize<ResolvedConfiguration>(definition.PlanJson, DefinitionValidation.Json)
        ?? throw new InvalidDataException("The collected configuration is invalid.");

    internal static void ValidateResult(BuildResult result, BuildAssignment assignment)
    {
        if (result.Outcome is not (BuildOutcome.Succeeded or BuildOutcome.Failed or BuildOutcome.Cancelled or BuildOutcome.InfrastructureFailed)
            || result.StatusText.Length > 1024 || result.Steps.Count > assignment.Steps.Count
            || result.Steps.Any(s => s.StepIndex < 0 || s.StepIndex >= assignment.Steps.Count)
            || result.Steps.Select(s => s.StepIndex).Distinct().Count() != result.Steps.Count)
            throw new InvalidDataException("The terminal result is invalid.");
        var watermarks = result.OutputWatermarks;
        if (result.OutputProducerLost ? watermarks.Count != 0 : watermarks.Count != assignment.Steps.Count * 2)
            throw new InvalidDataException("Terminal output watermark coverage differs.");
        if (watermarks.Any(w => w.StepIndex < 0 || w.StepIndex >= assignment.Steps.Count || (int)w.Stream is < 0 or > 1
                || (w.FinalSequenceExclusive == 0) != (w.ProducedBytes == 0) || w.FinalSequenceExclusive > w.ProducedBytes)
            || watermarks.Select(w => (w.StepIndex, w.Stream)).Distinct().Count() != watermarks.Count)
            throw new InvalidDataException("Terminal output watermark identity differs.");
    }
}
