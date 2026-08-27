using Grpc.Core;
using Vivarium.Contracts.V1;
using Vivarium.Controller.Agents;
using Vivarium.Controller.Builds;
using Vivarium.Controller.Security;

namespace Vivarium.Controller;

public sealed class AgentHubService : AgentHub.AgentHubBase
{
    private readonly AgentRegistry registry;
    private readonly TokenStore tokens;
    private readonly BuildTracker builds;
    private readonly ILogger<AgentHubService> log;

    public AgentHubService(AgentRegistry registry, TokenStore tokens, BuildTracker builds, ILogger<AgentHubService> log)
    {
        this.registry = registry;
        this.tokens = tokens;
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
        var auth = Authenticate(hello);
        var agent = registry.Register(hello, auth);
        agent.Connected = true;
        agent.LastHeartbeat = DateTimeOffset.UtcNow;
        log.LogInformation("agent {AgentId} connected ({Auth}, session {SessionId})",
            agent.AgentId, agent.Auth, hello.SessionId);

        await responseStream.WriteAsync(new ControllerMsg
        {
            Welcome = new Welcome
            {
                ServerTimeUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Authorized = agent.Auth == AgentAuth.Authorized,
                ServerVersion = ServerVersion,
            },
        });

        var outbox = agent.Outbox;
        var writer = Task.Run(async () =>
        {
            try
            {
                await foreach (var msg in outbox.Reader.ReadAllAsync(context.CancellationToken))
                {
                    await responseStream.WriteAsync(msg);
                }
            }
            catch (OperationCanceledException)
            {
            }
        });

        try
        {
            while (await requestStream.MoveNext(context.CancellationToken))
            {
                var msg = requestStream.Current;
                switch (msg.MsgCase)
                {
                    case AgentMsg.MsgOneofCase.Heartbeat:
                        agent.LastHeartbeat = DateTimeOffset.UtcNow;
                        break;
                    case AgentMsg.MsgOneofCase.Log:
                        builds.OnLog(msg.Log);
                        break;
                    case AgentMsg.MsgOneofCase.Status:
                        builds.OnStatus(msg.Status);
                        break;
                    case AgentMsg.MsgOneofCase.Result:
                        builds.OnResult(msg.Result);
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
            agent.Connected = false;
            outbox.Writer.TryComplete();
            await writer;
            log.LogInformation("agent {AgentId} disconnected", agent.AgentId);
        }
    }

    private AgentAuth Authenticate(Hello hello)
    {
        if (hello.AuthToken.Length > 0 &&
            tokens.TryGetAgentByToken(hello.AuthToken, out var byToken) &&
            byToken == hello.AgentId)
        {
            return AgentAuth.Authorized;
        }

        if (hello.EnrollToken.Length > 0 && !tokens.ConsumeEnrollToken(hello.EnrollToken))
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, "invalid enroll token"));
        }

        // Known-but-unauthorized is the TeamCity model: visible, never scheduled (D8).
        return AgentAuth.Unauthorized;
    }

    internal static readonly string ServerVersion =
        typeof(AgentHubService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}
