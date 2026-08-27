using Grpc.Core;
using Vivarium.Contracts.V1;

namespace Vivarium.Agent;

/// <summary>
/// Serializes writes to the gRPC request stream — IClientStreamWriter allows one writer at a time,
/// and heartbeats, logs, statuses and results all race for it.
/// </summary>
public sealed class SessionWriter
{
    private readonly IClientStreamWriter<AgentMsg> stream;
    private readonly SemaphoreSlim gate = new(1, 1);

    public SessionWriter(IClientStreamWriter<AgentMsg> stream) => this.stream = stream;

    public async Task SendAsync(AgentMsg msg, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            await stream.WriteAsync(msg, ct);
        }
        finally
        {
            gate.Release();
        }
    }
}
