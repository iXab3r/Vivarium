using Grpc.Core;
using Vivarium.Contracts;
using Vivarium.Contracts.V1;
using Vivarium.Controller.Agents.Compatibility;

namespace Vivarium.Controller.Agents;

/// <summary>Private AgentHub adapter for the fresh PG services; Bootstrap and field tags are unchanged.</summary>
internal sealed class RuntimeAgentHub(RuntimeAgentSessions sessions, TimeProvider time) : AgentHub.AgentHubBase
{
    public override async Task Session(IAsyncStreamReader<AgentMsg> requestStream,
        IServerStreamWriter<ControllerMsg> responseStream, ServerCallContext context)
    {
        if (!await requestStream.MoveNext(context.CancellationToken) || requestStream.Current.MsgCase != AgentMsg.MsgOneofCase.Hello)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The first message must be Hello."));
        RuntimeAgentConnection? connection;
        try { connection = await sessions.ConnectAsync(requestStream.Current.Hello, context.CancellationToken); }
        catch (AgentProtocolException error) { throw new RpcException(new Status(error.StatusCode, "Agent protocol negotiation failed.")); }
        catch (ArgumentException) { throw new RpcException(new Status(StatusCode.InvalidArgument, "Agent session identity is invalid.")); }
        if (connection is null) throw new RpcException(new Status(StatusCode.PermissionDenied, "Valid Agent or enrollment proof is required."));
        using var lifetime = connection.Lifetime;
        var writer = Task.CompletedTask;
        try
        {
            var welcome = new Welcome
            {
                ServerTimeUnixMs = time.GetUtcNow().ToUnixTimeMilliseconds(), Authorized = connection.Credential.Admission.Authorization == AgentAuth.Authorized,
                ServerVersion = VivariumProductVersion.FromAssembly(typeof(RuntimeAgentHub).Assembly),
                SelectedProtocolVersion = connection.Protocol.SelectedVersion,
                MinimumProtocolVersion = AgentProtocolCompatibility.MinimumSupportedVersion, CurrentProtocolVersion = AgentProtocolCompatibility.CurrentVersion,
                ProtocolMode = connection.Protocol.Mode, CredentialGeneration = checked((ulong)connection.Credential.Admission.CredentialGeneration),
                ConnectionGeneration = checked((ulong)connection.Handle.ConnectionGeneration),
            };
            welcome.NegotiatedCapabilities.Add(connection.Protocol.NegotiatedCapabilities);
            await responseStream.WriteAsync(new ControllerMsg { Welcome = welcome });
            if (connection.Credential.Admission.AuthTokenToDeliver is { } delivered)
                await responseStream.WriteAsync(new ControllerMsg { Authorized = new()
                { AuthToken = delivered, CredentialGeneration = welcome.CredentialGeneration } });
            writer = Task.Run(async () =>
            {
                try
                {
                    await foreach (var message in connection.Handle.Outbox.Reader.ReadAllAsync(lifetime.Token))
                        await responseStream.WriteAsync(message);
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            });
            while (await requestStream.MoveNext(lifetime.Token))
            {
                var message = requestStream.Current;
                if (message.MsgCase == AgentMsg.MsgOneofCase.Heartbeat)
                {
                    if (!await sessions.HeartbeatAsync(connection, message.Heartbeat, lifetime.Token))
                        throw new RpcException(new Status(StatusCode.Aborted, "The Agent session was superseded."));
                }
                else throw new RpcException(new Status(StatusCode.FailedPrecondition, "No owning workload accepts this message."));
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (IOException) { }
        finally
        {
            await lifetime.CancelAsync(); connection.Handle.Outbox.Writer.TryComplete();
            try { await writer; }
            finally { await sessions.DisconnectAsync(connection); }
        }
    }
}
