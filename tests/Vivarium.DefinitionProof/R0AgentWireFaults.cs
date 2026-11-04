using Grpc.Core;
using Grpc.Core.Interceptors;
using Vivarium.Contracts.V1;

namespace Vivarium.DefinitionProof;

/// <summary>Bounded packet loss around the real AgentHub; application handlers remain unchanged.</summary>
internal sealed class R0AgentWireFaults : Interceptor
{
    private readonly object sync = new();
    private readonly List<BuildOutput> observed = [];
    private int droppedAcknowledgements;
    private readonly HashSet<string> observedSessions = [];
    internal bool DropOutputData { get; set; }
    internal bool OmitPlatform { get; set; }
    internal bool DropOutputAcknowledgements { get; set; }
    internal int DroppedAcknowledgements => Volatile.Read(ref droppedAcknowledgements);
    internal int ObservedSessionCount { get { lock (sync) return observedSessions.Count; } }
    internal BuildOutput[] Observed { get { lock (sync) return observed.Select(f => f.Clone()).ToArray(); } }

    public override Task DuplexStreamingServerHandler<TRequest, TResponse>(IAsyncStreamReader<TRequest> requestStream,
        IServerStreamWriter<TResponse> responseStream, ServerCallContext context,
        DuplexStreamingServerMethod<TRequest, TResponse> continuation)
        => continuation(new Reader<TRequest>(requestStream, this), new Writer<TResponse>(responseStream, this), context);

    private bool Observe(object? message)
    {
        if (OmitPlatform && message is AgentMsg { Hello: { } hello }) hello.Parameters.Remove("system.os.family");
        if (message is not AgentMsg { BuildOutput: { } output }) return true;
        lock (sync)
        {
            if (observedSessions.Add(output.SessionId) && observedSessions.Count > 4)
                throw new InvalidOperationException("Owned wire fault exceeded its session bound.");
            if (!observed.Any(f => f.BuildId == output.BuildId && f.StepIndex == output.StepIndex && f.Stream == output.Stream
                && f.SourceSequenceStart == output.SourceSequenceStart))
            {
                if (observed.Count == 16) throw new InvalidOperationException("Owned wire fault exceeded its frame bound.");
                observed.Add(output.Clone());
            }
        }
        return !DropOutputData;
    }

    private sealed class Reader<T>(IAsyncStreamReader<T> source, R0AgentWireFaults owner) : IAsyncStreamReader<T>
    {
        public T Current => source.Current;
        public async Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            while (await source.MoveNext(cancellationToken)) if (owner.Observe(source.Current)) return true;
            return false;
        }
    }

    private sealed class Writer<T>(IServerStreamWriter<T> destination, R0AgentWireFaults owner) : IServerStreamWriter<T>
    {
        public WriteOptions? WriteOptions { get => destination.WriteOptions; set => destination.WriteOptions = value; }
        public Task WriteAsync(T message)
        {
            if (owner.DropOutputAcknowledgements && message is ControllerMsg { BuildOutputAcknowledged: not null })
            { Interlocked.Increment(ref owner.droppedAcknowledgements); return Task.CompletedTask; }
            return destination.WriteAsync(message);
        }
    }
}
