using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vivarium.Contracts.V1;
using Vivarium.Controller.Agents.Compatibility;
using Vivarium.Controller.Persistence;
using Vivarium.Controller.Security;
using Vivarium.Controller.Builds.Runtime;

namespace Vivarium.Controller.Agents;

internal sealed record RuntimeAgentConnection(AgentConnectionHandle Handle, AgentCredentialAdmission Credential,
    AgentProtocolNegotiation Protocol, CancellationTokenSource Lifetime);

/// <summary>Fresh PG session ownership with the existing live-session/fencing kernel.</summary>
internal sealed class RuntimeAgentSessions(IDbContextFactory<VivariumDbContext> databases, AgentCredentials credentials,
    AgentRegistry registry, AgentLifecycleCoordinator lifecycle, TimeProvider time)
{
    private readonly ConcurrentDictionary<string, RuntimeAgentConnection> connections = new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal async Task<RuntimeAgentConnection?> ConnectAsync(Hello hello, CancellationToken token)
    {
        AgentSessionIdentity.Validate(hello);
        var protocol = AgentProtocolCompatibility.Negotiate(hello, enableDurableBuildOutput: true,
            enableLiteralArtifactPublisher: true); // Reject before any enrollment side effect.
        await using var lease = await lifecycle.AcquireAsync(hello.AgentId, token);
        var credential = await credentials.AdmitAsync(hello, token);
        if (credential is null) return null;
        if (credential.Admission.CredentialReplaced) registry.SetAuthorized(hello.AgentId, false);
        await using var database = await databases.CreateDbContextAsync(token);
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        await AgentCredentials.LockAgentAsync(database, hello.AgentId, token);
        var row = await database.AgentRegistrations.SingleAsync(a => a.AgentId == hello.AgentId, token);
        if (row.CredentialGeneration != credential.Admission.CredentialGeneration || row.DeletedAt is not null) return null;
        credential = credential with { Admission = credential.Admission with
        {
            Authorization = row.Authorized ? AgentAuth.Authorized : AgentAuth.Unauthorized,
            Enabled = row.DesiredEnabled, MaintenanceDrain = row.MaintenanceDrain,
        } };
        row.ConnectionGeneration = checked(row.ConnectionGeneration + 1);
        row.CurrentSessionId = hello.SessionId;
        row.SessionCredentialGeneration = row.CredentialGeneration;
        row.SessionAuthenticated = credential.BearerProven;
        row.ProcessInstanceId = hello.ProcessInstanceId;
        row.LastHeartbeatSequence = 0; row.LastHeartbeatAt = time.GetUtcNow(); row.LastSeenAt = time.GetUtcNow();
        row.ReportedParametersJson = JsonSerializer.Serialize(AgentParameterMaps.Normalize(hello.Parameters), Json);
        row.AdvertisedCapabilitiesJson = JsonSerializer.Serialize(protocol.AdvertisedCapabilities.Select(c =>
            new AgentCapabilitySupport(c.CapabilityId, checked((int)c.ContractMajor))), Json);
        row.NegotiatedCapabilitiesJson = JsonSerializer.Serialize(protocol.NegotiatedCapabilities.Select(c =>
            new AgentCapabilitySupport(c.CapabilityId, checked((int)c.ContractMajor))), Json);
        var observation = AgentProtocolCompatibility.CreateStaticObservation(hello, protocol, time.GetUtcNow(),
            row.CredentialGeneration, row.ConnectionGeneration);
        row.StaticObservationJson = observation is null ? null : JsonSerializer.Serialize(observation, Json);
        if (hello.WorkloadRecoveryOutcome == WorkloadRecoveryOutcome.Failed)
        { row.Quarantined = true; row.HealthReason = "workload_recovery_failed"; }
        // Rebind delivery only for the already persisted owner. Immutable assigned provenance stays
        // unchanged; a Hello can neither invent a lease nor move one to another Agent.
        var owned = row.CurrentWorkloadId is null ? null : await database.RuntimeBuildExecutions
            .SingleOrDefaultAsync(e => e.WorkloadId == row.CurrentWorkloadId && e.AgentId == row.AgentId, token);
        var known = credential.BearerProven && owned is not null && owned.ReleasedAt is null
            && owned.LeaseFence == row.WorkloadFence && owned.CredentialGeneration == row.CredentialGeneration
            && (owned.ReconnectDeadline is null || owned.ReconnectDeadline > time.GetUtcNow());
        var matches = known && (hello.RunningBuildId == owned!.WorkloadId
            || hello.RunningBuildId.Length == 0 && (owned!.AcceptedAt is null || owned.TerminalReceivedAt is not null));
        if (matches)
        {
            owned!.CurrentSessionId = row.CurrentSessionId; owned.CurrentConnectionGeneration = row.ConnectionGeneration;
            owned.ReconnectDeadline = null;
            if (hello.RunningBuildId.Length == 0) await ReleaseIfCompleteAsync(database, row, owned, token);
        }
        else if (known && hello.RunningBuildId.Length == 0 && await ReleaseIfCompleteAsync(database, row, owned!, token)) { }
        else if (hello.RunningBuildId.Length > 0 || row.CurrentWorkloadId is not null)
        { row.Quarantined = true; row.HealthReason = "unreconciled_workload"; }
        row.Reconciled = credential.BearerProven && !row.Quarantined && protocol.SupportsBuildRunner;
        row.Health = row.Quarantined ? "UNHEALTHY" : row.Reconciled ? "HEALTHY" : "UNKNOWN";
        if (!row.Quarantined) row.HealthReason = row.Reconciled ? "reconciled" : "session_not_reconciled";
        await database.SaveChangesAsync(token); await transaction.CommitAsync(token);
        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        try
        {
            var handle = registry.Register(hello, credential.BearerProven ? credential.Admission.Authorization : AgentAuth.Unauthorized,
                row.DesiredEnabled && row.DesiredRevision is not null, row.MaintenanceDrain, row.ConnectionGeneration, lifetime);
            registry.ApplyStoredOperationalState(handle, new(row.Health switch
            { "HEALTHY" => AgentOperationalHealth.Healthy, "UNHEALTHY" => AgentOperationalHealth.Unhealthy, _ => AgentOperationalHealth.Unknown },
                row.Quarantined, row.HealthReason, time.GetUtcNow()));
            if (row.Reconciled) registry.Reconcile(handle, row.CurrentWorkloadId);
            var connection = new RuntimeAgentConnection(handle, credential, protocol, lifetime);
            connections[hello.AgentId] = connection;
            return connection;
        }
        catch { lifetime.Dispose(); throw; }
    }

    internal async Task<bool> HeartbeatAsync(RuntimeAgentConnection connection, Heartbeat heartbeat, CancellationToken token)
    {
        if (!registry.IsCurrent(connection.Handle)) return false;
        await using var database = await databases.CreateDbContextAsync(token);
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        await AgentCredentials.LockAgentAsync(database, connection.Handle.AgentId, token);
        var row = await database.AgentRegistrations.SingleAsync(a => a.AgentId == connection.Handle.AgentId, token);
        if (!Current(row, connection)) return false;
        var regressed = heartbeat.Sequence != 0 && (decimal)heartbeat.Sequence <= row.LastHeartbeatSequence;
        var execution = row.CurrentWorkloadId is null ? null : await database.RuntimeBuildExecutions
            .SingleOrDefaultAsync(e => e.WorkloadId == row.CurrentWorkloadId && e.AgentId == row.AgentId, token);
        var mismatch = !string.Equals(row.CurrentWorkloadId ?? "", heartbeat.RunningBuildId, StringComparison.Ordinal);
        if (!regressed && mismatch && heartbeat.RunningBuildId.Length == 0 && execution is not null
            && RuntimeBuildCoordinator.Owner(row, execution, connection))
        {
            // Empty heartbeats before acceptance are not proof that a durable assignment vanished.
            if (execution.AcceptedAt is null && execution.TerminalReceivedAt is null) mismatch = false;
            else if (execution.TerminalReceivedAt is not null)
            { await ReleaseIfCompleteAsync(database, row, execution, token); mismatch = false; }
        }
        if (regressed || mismatch)
        {
            row.Quarantined = true; row.Health = "UNHEALTHY";
            row.HealthReason = regressed ? "heartbeat_sequence_regressed" : "heartbeat_workload_mismatch";
            registry.Quarantine(row.AgentId, row.HealthReason);
        }
        else
        {
            row.LastHeartbeatAt = time.GetUtcNow(); row.LastSeenAt = time.GetUtcNow();
            if (heartbeat.Sequence != 0) row.LastHeartbeatSequence = heartbeat.Sequence;
            registry.Heartbeat(connection.Handle, heartbeat);
        }
        await database.SaveChangesAsync(token); await transaction.CommitAsync(token);
        if (!regressed && !mismatch) registry.Reconcile(connection.Handle, row.CurrentWorkloadId);
        return true;
    }

    internal async Task<AgentCredentialGrant> SetAuthorizedAsync(ManagementRequestContext actor, string agentId,
        bool authorized, CancellationToken token)
    {
        await using var lease = await lifecycle.AcquireAsync(agentId, token);
        var grant = await credentials.SetAuthorizedAsync(actor, agentId, authorized, token);
        if (grant.Outcome is not (Identity.AccessMutationResult.Applied or Identity.AccessMutationResult.Unchanged)) return grant;
        if (!authorized) registry.SetAuthorized(agentId, false);
        if (grant.Token is not null && connections.TryGetValue(agentId, out var connection) && registry.IsCurrent(connection.Handle)
            && !connection.Credential.BearerProven)
        {
            await using var database = await databases.CreateDbContextAsync(token);
            var row = await database.AgentRegistrations.AsNoTracking().SingleAsync(a => a.AgentId == agentId, token);
            if (Current(row, connection) && row.EnrollmentTokenHash == connection.Credential.EnrollmentBinding)
                registry.TrySend(connection.Handle, new ControllerMsg { Authorized = new()
                { AuthToken = grant.Token, CredentialGeneration = checked((ulong)grant.Generation) } });
        }
        else if (authorized && connections.TryGetValue(agentId, out var proven) && proven.Credential.BearerProven)
            registry.SetAuthorized(agentId, true);
        return grant;
    }

    internal bool Connected(string agentId, string? sessionId, long generation) => connections.TryGetValue(agentId, out var connection)
        && connection.Handle.SessionId == sessionId && connection.Handle.ConnectionGeneration == generation && registry.IsCurrent(connection.Handle);

    internal bool TryGetCurrent(string agentId, out RuntimeAgentConnection? connection)
    {
        if (connections.TryGetValue(agentId, out var value) && registry.IsCurrent(value.Handle))
        { connection = value; return true; }
        connection = null; return false;
    }

    internal async Task ExpireSilentAsync(CancellationToken token)
    {
        foreach (var connection in connections.Values)
        {
            if (!registry.IsCurrent(connection.Handle)) continue;
            await using var database = await databases.CreateDbContextAsync(token);
            await using var transaction = await database.Database.BeginTransactionAsync(token);
            await AgentCredentials.LockAgentAsync(database, connection.Handle.AgentId, token);
            var row = await database.AgentRegistrations.SingleAsync(a => a.AgentId == connection.Handle.AgentId, token);
            if (!Current(row, connection) || row.LastHeartbeatAt is null || row.LastHeartbeatAt.Value.AddSeconds(15) > time.GetUtcNow()) continue;
            row.Reconciled = false;
            if (row.CurrentWorkloadId is not null)
            {
                var execution = await database.RuntimeBuildExecutions.SingleOrDefaultAsync(e => e.WorkloadId == row.CurrentWorkloadId, token);
                if (execution is { ReleasedAt: null }) execution.ReconnectDeadline ??= row.LastHeartbeatAt.Value.AddSeconds(45);
            }
            await database.SaveChangesAsync(token); await transaction.CommitAsync(token);
            try { await connection.Lifetime.CancelAsync(); }
            catch (ObjectDisposedException) { } // The exact stream finished while its expiry committed.
        }
    }

    internal async Task DisconnectAsync(RuntimeAgentConnection connection)
    {
        var loss = registry.Disconnect(connection.Handle);
        if (connections.TryGetValue(connection.Handle.AgentId, out var current) && ReferenceEquals(current, connection))
            connections.TryRemove(new KeyValuePair<string, RuntimeAgentConnection>(connection.Handle.AgentId, connection));
        if (loss is null) return;
        await using var database = await databases.CreateDbContextAsync();
        await using var transaction = await database.Database.BeginTransactionAsync();
        await AgentCredentials.LockAgentAsync(database, connection.Handle.AgentId, CancellationToken.None);
        var row = await database.AgentRegistrations.SingleAsync(a => a.AgentId == connection.Handle.AgentId);
        if (Current(row, connection))
        {
            row.Reconciled = false;
            if (row.CurrentWorkloadId is not null)
            {
                var execution = await database.RuntimeBuildExecutions.SingleOrDefaultAsync(e => e.WorkloadId == row.CurrentWorkloadId);
                if (execution is { ReleasedAt: null }) execution.ReconnectDeadline ??= time.GetUtcNow().AddSeconds(30);
            }
            await database.SaveChangesAsync();
        }
        await transaction.CommitAsync();
    }

    private async Task<bool> ReleaseIfCompleteAsync(VivariumDbContext database, AgentRegistration row,
        RuntimeBuildExecution execution, CancellationToken token)
    {
        if (execution.TerminalReceivedAt is null || execution.ReleasedAt is not null) return false;
        var output = await database.RuntimeBuildOutputs.SingleAsync(o => o.BuildId == execution.BuildId, token);
        if (output.State is not ("COMPLETE" or "COMPLETE_WITH_GAPS")) return false;
        execution.Phase = "RELEASED"; execution.ReleasedAt = time.GetUtcNow(); row.CurrentWorkloadId = null;
        return true;
    }

    private static bool Current(AgentRegistration row, RuntimeAgentConnection connection) => row.DeletedAt is null
        && row.CurrentSessionId == connection.Handle.SessionId && row.ConnectionGeneration == connection.Handle.ConnectionGeneration
        && row.SessionCredentialGeneration == connection.Credential.Admission.CredentialGeneration;
}
