using Vivarium.Controller.Builds;

namespace Vivarium.Controller.Scheduling;

/// <summary>Expires builds whose queue-wait deadline elapsed before execution began.</summary>
public sealed class BuildQueueTimeoutMonitor : BackgroundService
{
    private readonly BuildQueueStore store;
    private readonly BuildQueueService queue;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<BuildQueueTimeoutMonitor> log;

    public BuildQueueTimeoutMonitor(
        BuildQueueStore store,
        BuildQueueService queue,
        TimeProvider timeProvider,
        ILogger<BuildQueueTimeoutMonitor> log)
    {
        this.store = store;
        this.queue = queue;
        this.timeProvider = timeProvider;
        this.log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await SweepOnceAsync(timeProvider.GetUtcNow());
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await SweepOnceAsync(timeProvider.GetUtcNow());
        }
    }

    public async Task<IReadOnlyList<string>> SweepOnceAsync(DateTimeOffset now)
    {
        var expired = await store.ExpireDueAsync(now);
        if (expired.Count != 0)
        {
            queue.NotifyChanged();
            log.LogWarning(
                "expired {BuildCount} builds after their queue-wait deadline",
                expired.Count);
        }

        return expired;
    }
}
