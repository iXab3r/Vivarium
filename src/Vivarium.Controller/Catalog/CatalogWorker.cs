using System.Data.Common;
using Vivarium.Controller.Configuration.Git;

namespace Vivarium.Controller.Catalog;

/// <summary>Resume journalled operations; a Git push alone never invents a managed actor.</summary>
internal sealed class CatalogWorker(IServiceProvider services,
    ILogger<CatalogWorker> logger) : BackgroundService
{
    internal static bool Configured(IConfiguration configuration) =>
        !string.IsNullOrWhiteSpace(configuration["Vivarium:Definitions:Path"]);

    internal static CatalogSources Sources(IConfiguration configuration)
    {
        var section = configuration.GetSection("Vivarium:Definitions");
        // Recovery still owns journalled containers when the optional source resource was removed.
        if (!Configured(configuration)) return new("", "", []);
        return new(section["RepositoryId"] ?? throw new InvalidDataException("Configure the definition repository identity."),
            section["Path"] ?? throw new InvalidDataException("Configure the definition repository path."),
            section.GetSection("SettingsRepositories").Get<DefinitionRepository[]>() ?? []);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await services.GetRequiredService<CatalogService>().RecoverAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (CatalogBusyException) { }
            catch (Exception error) when (error is IOException or DbException or ConfigurationRepositoryException
                or Microsoft.EntityFrameworkCore.DbUpdateException)
            {
                // Credentials, Git diagnostics and author output are never copied into Host logs.
                logger.LogWarning("Catalogue recovery is unavailable; the prior active revision is retained.");
            }
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }
}
