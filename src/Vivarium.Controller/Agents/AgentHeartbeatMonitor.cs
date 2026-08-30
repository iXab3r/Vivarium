using Vivarium.Controller.Builds;

namespace Vivarium.Controller.Agents;

public sealed class AgentHeartbeatMonitor : BackgroundService
{
    private readonly AgentRegistry registry;
    private readonly BuildTracker builds;
    private readonly ControllerOptions options;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<AgentHeartbeatMonitor> log;

    public AgentHeartbeatMonitor(
        AgentRegistry registry,
        BuildTracker builds,
        ControllerOptions options,
        TimeProvider timeProvider,
        ILogger<AgentHeartbeatMonitor> log)
    {
        this.registry = registry;
        this.builds = builds;
        this.options = options;
        this.timeProvider = timeProvider;
        this.log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await SweepOnceAsync(timeProvider.GetUtcNow());
        }
    }

    public async Task SweepOnceAsync(DateTimeOffset now)
    {
        foreach (var loss in registry.ExpireStaleConnections(
                     now, options.AgentHeartbeatTimeout))
        {
            await builds.OnSessionLostAsync(loss);
            log.LogWarning("agent {AgentId} missed its heartbeat deadline", loss.AgentId);
        }

        await builds.SweepExpiredLeasesAsync(now);
        await builds.SweepDueAssignmentAttemptsAsync(now);
        await builds.SweepDueStopsAsync(now);
    }
}
