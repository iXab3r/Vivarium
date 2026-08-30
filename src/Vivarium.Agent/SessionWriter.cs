using Grpc.Core;
using System.Threading.Channels;
using Vivarium.Contracts.V1;

namespace Vivarium.Agent;

/// <summary>
/// Serializes writes to the gRPC request stream — IClientStreamWriter allows one writer at a time,
/// and heartbeats, logs, statuses and results all race for it.
/// </summary>
public sealed class SessionWriter
{
    private const long MaximumQueuedLogBytes = 1024 * 1024;
    private const int MaximumControlMessages = 64;
    private readonly IClientStreamWriter<AgentMsg> stream;
    private readonly Channel<PendingWrite> control = Channel.CreateBounded<PendingWrite>(
        new BoundedChannelOptions(MaximumControlMessages)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
    private readonly Channel<AgentMsg> output = Channel.CreateUnbounded<AgentMsg>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private long queuedLogBytes;
    private long droppedLogBytes;

    public SessionWriter(IClientStreamWriter<AgentMsg> stream) => this.stream = stream;

    public long DroppedLogBytes => Interlocked.Read(ref droppedLogBytes);

    public Task SendAsync(AgentMsg msg, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(msg);
        if (msg.MsgCase == AgentMsg.MsgOneofCase.Log)
        {
            var bytes = msg.Log.Data.Length;
            if (!TryReserveLogBytes(bytes))
            {
                if (bytes > 0)
                {
                    Interlocked.Add(ref droppedLogBytes, bytes);
                }
                return Task.CompletedTask;
            }
            if (!output.Writer.TryWrite(msg.Clone()))
            {
                Interlocked.Add(ref queuedLogBytes, -bytes);
                Interlocked.Add(ref droppedLogBytes, bytes);
            }
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new PendingWrite(msg.Clone(), completion);
        if (!control.Writer.TryWrite(pending))
        {
            throw new IOException("Agent control-message queue is full");
        }
        return completion.Task.WaitAsync(ct);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        Exception? failure = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (control.Reader.TryRead(out var urgent))
                {
                    await WriteControlAsync(urgent, cancellationToken);
                    continue;
                }
                if (output.Reader.TryRead(out var log))
                {
                    try
                    {
                        await stream.WriteAsync(log, cancellationToken);
                    }
                    finally
                    {
                        Interlocked.Add(ref queuedLogBytes, -log.Log.Data.Length);
                    }
                    continue;
                }

                if (control.Reader.Completion.IsCompleted)
                {
                    if (!await output.Reader.WaitToReadAsync(cancellationToken))
                    {
                        break;
                    }
                    continue;
                }
                if (output.Reader.Completion.IsCompleted)
                {
                    if (!await control.Reader.WaitToReadAsync(cancellationToken))
                    {
                        break;
                    }
                    continue;
                }

                var controlReady = control.Reader.WaitToReadAsync(cancellationToken).AsTask();
                var outputReady = output.Reader.WaitToReadAsync(cancellationToken).AsTask();
                await Task.WhenAny(controlReady, outputReady);
            }
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            control.Writer.TryComplete(failure);
            output.Writer.TryComplete(failure);
            while (control.Reader.TryRead(out var pending))
            {
                pending.Completion.TrySetException(
                    failure ?? new IOException("Agent session writer stopped"));
            }
            while (output.Reader.TryRead(out var log))
            {
                Interlocked.Add(ref queuedLogBytes, -log.Log.Data.Length);
            }
        }
    }

    public void Complete()
    {
        control.Writer.TryComplete();
        output.Writer.TryComplete();
    }

    private bool TryReserveLogBytes(int bytes)
    {
        if (bytes <= 0 || bytes > MaximumQueuedLogBytes)
        {
            return false;
        }
        while (true)
        {
            var observed = Interlocked.Read(ref queuedLogBytes);
            if (observed > MaximumQueuedLogBytes - bytes)
            {
                return false;
            }
            if (Interlocked.CompareExchange(ref queuedLogBytes, observed + bytes, observed) == observed)
            {
                return true;
            }
        }
    }

    private async Task WriteControlAsync(PendingWrite pending, CancellationToken cancellationToken)
    {
        try
        {
            await stream.WriteAsync(pending.Message, cancellationToken);
            pending.Completion.TrySetResult(true);
        }
        catch (Exception exception)
        {
            pending.Completion.TrySetException(exception);
            throw;
        }
    }

    private sealed record PendingWrite(AgentMsg Message, TaskCompletionSource<bool> Completion);
}
