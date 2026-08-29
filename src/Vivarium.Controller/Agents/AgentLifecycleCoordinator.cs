using System.Collections.Concurrent;

namespace Vivarium.Controller.Agents;

/// <summary>
/// Serializes lifecycle-sensitive work for one Agent: identity admission/deletion, desired-state
/// activation, and the scheduler's claim/reservation/preparation boundary.
/// </summary>
public sealed class AgentLifecycleCoordinator
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> guards = new();

    public async ValueTask<IAsyncDisposable> AcquireAsync(
        string agentId,
        CancellationToken cancellationToken = default)
    {
        var guard = guards.GetOrAdd(agentId, static _ => new SemaphoreSlim(1, 1));
        await guard.WaitAsync(cancellationToken);
        return new Lease(guard);
    }

    private sealed class Lease : IAsyncDisposable
    {
        private SemaphoreSlim? guard;

        public Lease(SemaphoreSlim guard) => this.guard = guard;

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref guard, null)?.Release();
            return ValueTask.CompletedTask;
        }
    }
}
