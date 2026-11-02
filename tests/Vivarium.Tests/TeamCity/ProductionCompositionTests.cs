using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Vivarium.Contracts.V1;
using Vivarium.Controller.Agents;
using Vivarium.Controller.Builds;
using Vivarium.Controller.Persistence;
using Vivarium.Controller.Scheduling;

namespace Vivarium.Tests.TeamCity;

[TestFixture]
[NonParallelizable]
[Category("TeamCityCompositionControl")]
public sealed class ProductionCompositionTests
{
    [Test]
    public async Task Native_readiness_uses_real_auth_and_cleans_up_its_controller()
    {
        var timer = Stopwatch.StartNew();
        var controller = new CurrentController();
        await using (controller)
        {
            await controller.StartAsync();
            TestContext.Out.WriteLine($"Production Kestrel/Git/SQLite setup/readiness: {timer.ElapsedMilliseconds}ms; process working set {Environment.WorkingSet} bytes");
        }
        Assert.That(Directory.Exists(controller.Root), Is.False);
        TestContext.Out.WriteLine($"Including cleanup: {timer.ElapsedMilliseconds}ms");
    }

    [Test]
    public async Task Production_queue_monitor_uses_controlled_timers_and_isolated_databases()
    {
        var root = Path.Combine(Path.GetTempPath(), "vivarium-timer-composition", Guid.NewGuid().ToString("N"));
        var elapsed = Stopwatch.StartNew();
        Directory.CreateDirectory(Path.Combine(root, "first"));
        Directory.CreateDirectory(Path.Combine(root, "second"));
        try
        {
            await using var firstDatabase = new VivariumDatabase(Path.Combine(root, "first"));
            await using var secondDatabase = new VivariumDatabase(Path.Combine(root, "second"));
            var firstStore = new BuildQueueStore(firstDatabase);
            var secondStore = new BuildQueueStore(secondDatabase);
            var firstClock = new ObservedClock();
            var secondClock = new ObservedClock();
            // Pre-action native queue seed, not a claim about REST submission or TeamCity semantics.
            await firstStore.EnqueueAsync(new BuildAssignment { BuildId = "same-fixture-id" },
                "", firstClock.GetUtcNow(), TimeSpan.FromSeconds(10));
            await secondStore.EnqueueAsync(new BuildAssignment { BuildId = "same-fixture-id" },
                "", secondClock.GetUtcNow(), TimeSpan.FromSeconds(10));
            using var firstMonitor = Monitor(firstStore, firstClock);
            using var secondMonitor = Monitor(secondStore, secondClock);
            await firstMonitor.StartAsync(CancellationToken.None);
            await secondMonitor.StartAsync(CancellationToken.None);
            try
            {
                // Barrier proves timer registration before advancing virtual time. The watchdog is real time.
                await Task.WhenAll(firstClock.TimerRegistered.Task, secondClock.TimerRegistered.Task)
                    .WaitAsync(TimeSpan.FromSeconds(5));
                firstClock.Time.Advance(TimeSpan.FromSeconds(9));
                Assert.That((await firstStore.GetAsync("same-fixture-id"))!.State,
                    Is.EqualTo(BuildQueueItemState.Queued));
                firstClock.Time.Advance(TimeSpan.FromSeconds(1));
                await AwaitExpiredAsync(firstStore);
                Assert.That((await secondStore.GetAsync("same-fixture-id"))!.State,
                    Is.EqualTo(BuildQueueItemState.Queued), "the other case's timer/data must remain independent");
                secondClock.Time.Advance(TimeSpan.FromSeconds(10));
                await AwaitExpiredAsync(secondStore);
                var result = await new BuildStore(firstDatabase).GetAsync("same-fixture-id");
                Assert.That(result!.Result!.Outcome, Is.EqualTo(BuildOutcome.InfrastructureFailed));
            }
            finally
            {
                using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await Task.WhenAll(firstMonitor.StopAsync(watchdog.Token), secondMonitor.StopAsync(watchdog.Token));
            }
        }
        finally
        {
            CurrentController.DeleteDataDirectory(root);
        }
        Assert.That(Directory.Exists(root), Is.False);
        TestContext.Out.WriteLine($"Two isolated production SQLite monitors, 20 virtual seconds, setup/run/cleanup: {elapsed.ElapsedMilliseconds}ms");
    }

    private static BuildQueueTimeoutMonitor Monitor(BuildQueueStore store, TimeProvider clock) =>
        new(store, new BuildQueueService(store, new AgentRegistry(), clock, TimeSpan.FromSeconds(10)),
            clock, NullLogger<BuildQueueTimeoutMonitor>.Instance);

    private static async Task AwaitExpiredAsync(BuildQueueStore store)
    {
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while ((await store.GetAsync("same-fixture-id"))!.State != BuildQueueItemState.Removed)
        {
            // Wait for real async database work, never advance business time with a real-time sleep.
            await Task.Delay(10, watchdog.Token);
        }
    }

    private sealed class ObservedClock : TimeProvider
    {
        public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
        public TaskCompletionSource TimerRegistered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override DateTimeOffset GetUtcNow() => Time.GetUtcNow();
        public override long GetTimestamp() => Time.GetTimestamp();
        public override long TimestampFrequency => Time.TimestampFrequency;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = Time.CreateTimer(callback, state, dueTime, period);
            TimerRegistered.TrySetResult();
            return timer;
        }
    }
}
