using Grpc.Core;
using Grpc.Core.Interceptors;
using Vivarium.Contracts.V1;

namespace Vivarium.DefinitionProof;

/// <summary>Bounded transport barriers around the real Agent's acceptance, execution, collection and result.</summary>
internal sealed class R0AgentMessageGate : Interceptor
{
    internal volatile bool Enabled = true;
    internal bool GateCompletions { get; init; }
    internal AgentMsg? CompletionMessage { get; private set; }
    internal AgentMsg? ResultMessage { get; private set; }
    internal Barrier Acceptance { get; } = new();
    internal Barrier Step { get; } = new();
    internal Barrier Collection { get; } = new();
    internal Barrier Completion { get; } = new();
    internal Barrier Result { get; } = new();

    public override Task DuplexStreamingServerHandler<TRequest, TResponse>(IAsyncStreamReader<TRequest> requestStream,
        IServerStreamWriter<TResponse> responseStream, ServerCallContext context,
        DuplexStreamingServerMethod<TRequest, TResponse> continuation)
        => continuation(new Reader<TRequest>(requestStream, this), responseStream, context);

    internal sealed class Barrier
    {
        private readonly TaskCompletionSource arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task Arrived => arrived.Task;
        internal void Release() => released.TrySetResult();
        internal async Task WaitAsync(CancellationToken token)
        {
            arrived.TrySetResult();
            await released.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
        }
    }

    private sealed class Reader<T>(IAsyncStreamReader<T> source, R0AgentMessageGate owner) : IAsyncStreamReader<T>
    {
        public T Current => source.Current;
        public async Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            if (!await source.MoveNext(cancellationToken)) return false;
            if (!owner.Enabled) return true;
            if (source.Current is AgentMsg { AssignmentAccepted: not null }) await owner.Acceptance.WaitAsync(cancellationToken);
            if (source.Current is AgentMsg { Status.Completed: not null } completed)
            {
                owner.CompletionMessage = completed.Clone();
                if (owner.GateCompletions) await owner.Completion.WaitAsync(cancellationToken);
            }
            else if (source.Current is AgentMsg { Status.Phase: "RUNNING" }) await owner.Step.WaitAsync(cancellationToken);
            if (source.Current is AgentMsg { Status.Phase: "COLLECTING" }) await owner.Collection.WaitAsync(cancellationToken);
            if (source.Current is AgentMsg { Result: not null } result)
            {
                owner.ResultMessage = result.Clone(); await owner.Result.WaitAsync(cancellationToken);
            }
            return true;
        }
    }
}
