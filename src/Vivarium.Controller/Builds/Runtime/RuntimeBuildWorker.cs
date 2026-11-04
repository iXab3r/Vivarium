namespace Vivarium.Controller.Builds.Runtime;

/// <summary>Resolve PG/Storage owners after the managed Chiral startup barrier has completed.</summary>
internal sealed class RuntimeBuildWorker(IServiceProvider services) : BackgroundService
{
    private Task[] loops = [];

    internal void EnsureHealthy()
    {
        if (ExecuteTask is { IsCompleted: true } execution)
            throw new InvalidOperationException("Runtime worker stopped: " +
                (execution.Exception?.GetBaseException().GetType().Name ?? execution.Status.ToString()));
        foreach (var loop in loops)
            if (loop.IsCompleted)
                throw new InvalidOperationException("Runtime worker loop stopped: " +
                    (loop.Exception?.GetBaseException().GetType().Name ?? loop.Status.ToString()));
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        loops = [
        services.GetRequiredService<RuntimeBuildCoordinator>().RunAsync(stoppingToken),
        services.GetRequiredService<RuntimeBuildOutputStore>().RunAsync(stoppingToken),
        services.GetRequiredService<RuntimeOutputRetention>().RunAsync(stoppingToken),
        services.GetRequiredService<RuntimeBuildRunInputs>().RunAsync(stoppingToken),
        services.GetRequiredService<RuntimeVcsTriggers>().RunAsync(stoppingToken)];
        return Task.WhenAll(loops);
    }
}
