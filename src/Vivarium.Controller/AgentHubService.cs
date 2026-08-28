using Grpc.Core;
using Vivarium.Contracts.V1;
using Vivarium.Controller.Agents;
using Vivarium.Controller.Builds;
using Vivarium.Controller.Security;

namespace Vivarium.Controller;

public sealed class AgentHubService : AgentHub.AgentHubBase
{
    private readonly AgentRegistry registry;
    private readonly AgentStore store;
    private readonly TokenStore tokens;
    private readonly AgentLifecycleCoordinator lifecycle;
    private readonly BuildTracker builds;
    private readonly ILogger<AgentHubService> log;

    public AgentHubService(
        AgentRegistry registry,
        AgentStore store,
        TokenStore tokens,
        AgentLifecycleCoordinator lifecycle,
        BuildTracker builds,
        ILogger<AgentHubService> log)
    {
        this.registry = registry;
        this.store = store;
        this.tokens = tokens;
        this.lifecycle = lifecycle;
        this.builds = builds;
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
        if (string.IsNullOrWhiteSpace(hello.AgentId) || string.IsNullOrWhiteSpace(hello.SessionId))
        {
            throw new RpcException(new Status(
                StatusCode.InvalidArgument, "Hello requires non-empty agent_id and session_id"));
        }

        AgentAdmission admission;
        CancellationTokenSource session;
        AgentConnectionHandle connection;
        await using (await lifecycle.AcquireAsync(hello.AgentId, context.CancellationToken))
        {
            admission = await tokens.AdmitAgentAsync(hello)
                ?? throw new RpcException(new Status(
                    StatusCode.PermissionDenied, "valid agent or enrollment token required"));
            await store.ObserveHelloAsync(hello);
            session = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
            connection = registry.Register(hello, admission.Authorization, admission.Enabled, session);
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

        await responseStream.WriteAsync(new ControllerMsg
        {
            Welcome = new Welcome
            {
                ServerTimeUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Authorized = admission.Authorization == AgentAuth.Authorized,
                ServerVersion = ServerVersion,
            },
        });

        if (admission.AuthTokenToDeliver != null)
        {
            await responseStream.WriteAsync(new ControllerMsg
            {
                Authorized = new AuthorizationGranted { AuthToken = admission.AuthTokenToDeliver },
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
        if (registry.IsCurrent(connection))
        {
            await builds.OnAgentReconnectedAsync(connection, hello.RunningBuildId);
        }

        try
        {
            while (await requestStream.MoveNext(session.Token))
            {
                var msg = requestStream.Current;
                switch (msg.MsgCase)
                {
                    case AgentMsg.MsgOneofCase.Heartbeat:
                        registry.Heartbeat(connection);
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
        typeof(AgentHubService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}
