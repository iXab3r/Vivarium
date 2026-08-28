using Vivarium.Controller.Persistence;

namespace Vivarium.Controller.Management;

/// <summary>Coalesced live wake-ups with periodic callers expected to re-read durable state.</summary>
public sealed class DatabaseChangeNotifier : IDisposable
{
    private readonly VivariumDatabase database;
    private readonly object gate = new();
    private TaskCompletionSource<bool> changed = NewSignal();
    private long version;

    public DatabaseChangeNotifier(VivariumDatabase database)
    {
        this.database = database;
        database.Changed += Notify;
    }

    public long Version
    {
        get
        {
            lock (gate)
            {
                return version;
            }
        }
    }

    public async Task<long> WaitForChangeAsync(
        long observedVersion,
        TimeSpan fallbackInterval,
        CancellationToken cancellationToken)
    {
        Task signal;
        lock (gate)
        {
            if (version != observedVersion)
            {
                return version;
            }

            signal = changed.Task;
        }

        try
        {
            await signal.WaitAsync(fallbackInterval, cancellationToken);
        }
        catch (TimeoutException)
        {
            // Periodic re-read is the correctness fallback for a best-effort notification.
        }

        return Version;
    }

    public void Dispose() => database.Changed -= Notify;

    private void Notify()
    {
        TaskCompletionSource<bool> previous;
        lock (gate)
        {
            version++;
            previous = changed;
            changed = NewSignal();
        }

        previous.TrySetResult(true);
    }

    private static TaskCompletionSource<bool> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
