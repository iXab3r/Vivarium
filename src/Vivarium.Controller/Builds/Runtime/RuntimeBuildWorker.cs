namespace Vivarium.Controller.Builds.Runtime;

/// <summary>Resolve PG/Storage owners after the managed Chiral startup barrier has completed.</summary>
internal sealed class RuntimeBuildWorker(IServiceProvider services) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.WhenAll(
        services.GetRequiredService<RuntimeBuildCoordinator>().RunAsync(stoppingToken),
        services.GetRequiredService<RuntimeBuildOutputStore>().RunAsync(stoppingToken),
        services.GetRequiredService<RuntimeOutputRetention>().RunAsync(stoppingToken),
        services.GetRequiredService<RuntimeBuildRunInputs>().RunAsync(stoppingToken));
}
