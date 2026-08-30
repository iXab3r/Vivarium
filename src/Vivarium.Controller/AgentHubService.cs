using Grpc.Core;
using Vivarium.Contracts;
using Vivarium.Contracts.V1;
using Vivarium.Controller.Agents;
using Vivarium.Controller.Agents.Compatibility;
using Vivarium.Controller.Auditing;
using Vivarium.Controller.Builds;
using Vivarium.Controller.Deployment;
using Vivarium.Controller.Security;

namespace Vivarium.Controller;

public sealed class AgentHubService : AgentHub.AgentHubBase
{
    private readonly AgentRegistry registry;
    private readonly AgentStore store;
    private readonly AgentOperationalStore operationalStore;
    private readonly TokenStore tokens;
    private readonly AgentLifecycleCoordinator lifecycle;
    private readonly BuildTracker builds;
    private readonly TimeProvider timeProvider;
    private readonly AgentUpgradeService upgrades;
    private readonly AgentRestartService restarts;
    private readonly ILogger<AgentHubService> log;

    public AgentHubService(
        AgentRegistry registry,
        AgentStore store,
        AgentOperationalStore operationalStore,
        TokenStore tokens,
        AgentLifecycleCoordinator lifecycle,
        BuildTracker builds,
        AgentUpgradeService upgrades,
        AgentRestartService restarts,
        TimeProvider timeProvider,
        ILogger<AgentHubService> log)
    {
        this.registry = registry;
        this.store = store;
        this.operationalStore = operationalStore;
        this.tokens = tokens;
        this.lifecycle = lifecycle;
        this.builds = builds;
        this.upgrades = upgrades;
        this.restarts = restarts;
        this.timeProvider = timeProvider;
        this.log = log;
    }

    public override async Task Session(
        IAsyncStreamReader<AgentMsg> requestStream,
        IServerStreamWriter<ControllerMsg> responseStream,
        ServerCallContext context)
    {
        if (!await requestStream.MoveNext(context.CancellationToken) ||
            requestStream.Current.MsgCase != AgentMsg.MsgOneofCase.Hello)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "first message must be Hello"));
        }

        var hello = requestStream.Current.Hello;
        try
        {
            AgentSessionIdentity.Validate(hello);
        }
        catch (ArgumentException exception)
        {
            throw new RpcException(new Status(
                StatusCode.InvalidArgument, exception.Message));
        }

        AgentProtocolNegotiation negotiation;
        try
        {
            negotiation = AgentProtocolCompatibility.Negotiate(hello);
        }
        catch (AgentProtocolException exception)
        {
            // Compatibility is evaluated before token admission or Agent observation. An
            // incompatible or malformed Hello therefore cannot claim an enrollment token or
            // mutate the stable Agent projection.
            throw new RpcException(new Status(exception.StatusCode, exception.Message));
        }

        var correlationId = ManagementIdentifiers.NewId();
        var receivedAt = timeProvider.GetUtcNow();
        var enrollmentContext = new ManagementRequestContext(
            new ManagementPrincipal(
                "agent",
                hello.AgentId,
                "enrollment-token",
                LegacyScope: null),
            correlationId,
            RequestId: null,
            Source: "agent-hub");
        var enrollmentAudit = AuditEventDraft.Create(
            enrollmentContext,
            receivedAt,
            "agent.enroll",
            "agent",
            hello.AgentId,
            details: new Dictionary<string, string> { ["session_id"] = hello.SessionId });
        var deniedEnrollmentAudit = AuditEventDraft.Create(
            new ManagementRequestContext(
                ManagementPrincipal.Anonymous,
                correlationId,
                RequestId: null,
                Source: "agent-hub"),
            receivedAt,
            "agent.enroll",
            "agent",
            hello.AgentId,
            AuditOutcome.Denied,
            "invalid_enrollment_proof",
            new Dictionary<string, string> { ["session_id"] = hello.SessionId });

        AgentAdmission admission;
        AgentGenerationState generations;
        CancellationTokenSource session;
        AgentConnectionHandle connection;
        StoredAgentOperationalState? operationalState;
        await using (await lifecycle.AcquireAsync(hello.AgentId, context.CancellationToken))
        {
            admission = await tokens.AdmitAgentAsync(
                    hello,
                    enrollmentAudit,
                    deniedEnrollmentAudit)
                ?? throw new RpcException(new Status(
                    StatusCode.PermissionDenied, "valid agent or enrollment token required"));
            if (admission.CredentialReplaced)
            {
                // Credential revocation committed with admission. Fence the old live session from
                // new assignments before any later observation/session-acceptance await can fail.
                // Existing owned work retains its authenticated completion lane until replacement.
                registry.SetAuthorized(hello.AgentId, authorized: false);
            }

            await store.ObserveHelloAsync(hello);
            operationalState = await operationalStore.GetAsync(hello.AgentId);
            var restartDrain = await restarts.HasActiveAsync(hello.AgentId);
            generations = await store.AcceptSessionAsync(
                hello.AgentId,
                admission.CredentialGeneration);
            session = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
            try
            {
                connection = registry.Register(
                    hello,
                    admission.Authorization,
                    admission.Enabled,
                    admission.MaintenanceDrain || restartDrain,
                    generations.ConnectionGeneration,
                    session);
                registry.ApplyStoredOperationalState(connection, operationalState);
                await registry.PersistOperationalStateAsync(hello.AgentId);
            }
            catch
            {
                session.Dispose();
                throw;
            }

            try
            {
                await store.ObserveCapabilitiesAsync(
                    hello.AgentId,
                    generations.CredentialGeneration,
                    generations.ConnectionGeneration,
                    AgentProtocolCompatibility.CreateCapabilityObservation(negotiation));
                var observation = AgentProtocolCompatibility.CreateStaticObservation(
                    hello,
                    negotiation,
                    receivedAt,
                    generations.CredentialGeneration,
                    generations.ConnectionGeneration);
                if (observation is not null)
                {
                    await store.ObserveStaticFactsAsync(observation);
                }
            }
            catch
            {
                // The accepted generation is intentionally never reused. If the typed observation
                // cannot commit, tear down this runtime registration before exposing Welcome; the
                // next authenticated reconnect advances to a fresh durable generation.
                registry.Disconnect(connection);
                session.Cancel();
                session.Dispose();
                throw;
            }
        }

        using var sessionLifetime = session;
        log.LogInformation("agent {AgentId} connected ({Auth}, session {SessionId})",
            connection.AgentId, admission.Authorization, hello.SessionId);
        if (hello.RunningBuildId.Length > 0)
        {
            // Re-hello mid-build: the build is re-adopted, not double-scheduled (D4).
            log.LogInformation("agent {AgentId} re-adopted with running build {BuildId}",
                connection.AgentId, hello.RunningBuildId);
        }

        var welcome = new Welcome
        {
            ServerTimeUnixMs = timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            Authorized = admission.Authorization == AgentAuth.Authorized,
            ServerVersion = ServerVersion,
            ProtocolMode = negotiation.Mode,
            SelectedProtocolVersion = negotiation.SelectedVersion,
            MinimumProtocolVersion = AgentProtocolCompatibility.MinimumSupportedVersion,
            CurrentProtocolVersion = AgentProtocolCompatibility.CurrentVersion,
            CredentialGeneration = checked((ulong)generations.CredentialGeneration),
            ConnectionGeneration = checked((ulong)connection.ConnectionGeneration),
        };
        welcome.NegotiatedCapabilities.Add(negotiation.NegotiatedCapabilities);
        await responseStream.WriteAsync(new ControllerMsg { Welcome = welcome });

        if (admission.AuthTokenToDeliver != null)
        {
            await responseStream.WriteAsync(new ControllerMsg
            {
                Authorized = new AuthorizationGranted
                {
                    AuthToken = admission.AuthTokenToDeliver,
                    CredentialGeneration = checked((ulong)generations.CredentialGeneration),
                },
            });
        }

        var outbox = connection.Outbox;
        var writer = Task.Run(async () =>
        {
            try
            {
                await foreach (var msg in outbox.Reader.ReadAllAsync(session.Token))
                {
                    await responseStream.WriteAsync(msg);
                }
            }
            catch (OperationCanceledException)
            {
            }
        });
        if (registry.IsCurrent(connection) &&
            (negotiation.SupportsBuildRunner ||
             negotiation.IsLegacy && hello.RunningBuildId.Length > 0))
        {
            await builds.OnAgentReconnectedAsync(connection, hello.RunningBuildId);
            var restartConfirmed = await restarts.OnAgentConnectedAsync(connection);
            if (restartConfirmed &&
                hello.WorkloadRecoveryOutcome != WorkloadRecoveryOutcome.Failed)
            {
                registry.TryClearQuarantineAfterRestart(
                    connection,
                    hello.RunningBuildId,
                    "restart_reconciled");
            }
            if (restartConfirmed)
            {
                registry.SetMaintenanceDrain(connection.AgentId, admission.MaintenanceDrain);
            }
            await registry.PersistOperationalStateAsync(connection.AgentId);
            var healthAcceptance = await upgrades.OnAgentReconciledAsync(connection, session.Token);
            if (healthAcceptance is not null)
            {
                registry.TrySend(connection, new ControllerMsg
                {
                    UpgradeHealthAccepted = healthAcceptance,
                });
            }
        }

        try
        {
            while (await requestStream.MoveNext(session.Token))
            {
                var msg = requestStream.Current;
                switch (msg.MsgCase)
                {
                    case AgentMsg.MsgOneofCase.Heartbeat:
                        await builds.OnHeartbeatAsync(msg.Heartbeat, connection);
                        break;
                    case AgentMsg.MsgOneofCase.Log:
                        builds.OnLog(msg.Log, connection);
                        break;
                    case AgentMsg.MsgOneofCase.Status:
                        builds.OnStatus(msg.Status, connection);
                        break;
                    case AgentMsg.MsgOneofCase.Result:
                        await builds.OnResultAsync(msg.Result, connection);
                        break;
                    case AgentMsg.MsgOneofCase.AssignmentAccepted:
                        await builds.OnAssignmentAcceptedAsync(
                            msg.AssignmentAccepted, connection);
                        break;
                    case AgentMsg.MsgOneofCase.BuildStopAcknowledged:
                        await builds.OnBuildStopAcknowledgedAsync(
                            msg.BuildStopAcknowledged, connection);
                        break;
                    case AgentMsg.MsgOneofCase.AgentRestartAcknowledged:
                        await restarts.OnAcknowledgedAsync(
                            msg.AgentRestartAcknowledged, connection);
                        break;
                    case AgentMsg.MsgOneofCase.UpgradeHealthConfirmed:
                        await upgrades.ConfirmHealthAsync(
                            connection, msg.UpgradeHealthConfirmed, session.Token);
                        break;
                    case AgentMsg.MsgOneofCase.UpgradeCommitConfirmed:
                        await upgrades.ConfirmCommitAsync(
                            connection, msg.UpgradeCommitConfirmed, session.Token);
                        break;
                    case AgentMsg.MsgOneofCase.UpgradeFinalizationConfirmed:
                        await upgrades.ConfirmFinalizationAsync(
                            connection, msg.UpgradeFinalizationConfirmed, session.Token);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
            // connection dropped — reconnect is the agent's job (D4)
        }
        finally
        {
            var loss = registry.Disconnect(connection);
            if (loss != null)
            {
                await builds.OnSessionLostAsync(loss);
            }

            outbox.Writer.TryComplete();
            await writer;
            log.LogInformation("agent {AgentId} disconnected", connection.AgentId);
        }
    }

    internal static readonly string ServerVersion =
        VivariumProductVersion.FromAssembly(typeof(AgentHubService).Assembly);
}
