using System.Text.Json;
using System.Text.Json.Nodes;
using Chiral;
using Microsoft.EntityFrameworkCore;
using Vivarium.Controller.Persistence;

namespace Vivarium.Controller.Hosting;

internal static class VivariumConfigurationCommand
{
    public static async Task<int> ExecuteAsync(string[] args)
    {
        if (args.Length != 3 || args[0] is not ("init" or "nodes" or "migrate" or "run")
            || args[1] != "--settings-directory")
        {
            Console.Error.WriteLine("Usage: viv-server init|nodes|migrate|run --settings-directory PATH");
            return 2;
        }
        var directory = Path.GetFullPath(args[2]);
        if (args[0] == "run")
        {
            await using var app = VivariumRuntime.Build(directory);
            await app.RunAsync();
            return 0;
        }
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Services.AddVivariumNodes(builder.Configuration, directory);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        using var host = builder.Build();
        await host.StartAsync();
        try
        {
            var settings = host.Services.GetRequiredService<NodeConfigurationService>();
            var nodes = host.Services.GetServices<NodeDescriptor>().ToArray();
            if (args[0] == "migrate")
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                await VivariumRuntime.WaitForNodeAsync(settings, "vivarium-database", deadline.Token);
                // Privileged schema maintenance input is temporary, not another runtime setting.
                var input = await JsonSerializer.DeserializeAsync<MigrationInput>(Console.OpenStandardInput(),
                    cancellationToken: deadline.Token);
                if (string.IsNullOrWhiteSpace(input?.ConnectionString))
                    throw new InvalidDataException("Private maintenance connection input is required on stdin.");
                await using var database = new VivariumDbContext(new DbContextOptionsBuilder<VivariumDbContext>()
                    .UseNpgsql(input.ConnectionString).Options);
                await PostgresSchema.MigrateAsync(database, deadline.Token);
                Console.WriteLine("PostgreSQL migrations applied.");
                return 0;
            }
            if (args[0] == "init")
            {
                var documents = JsonNode.Parse(await Console.In.ReadToEndAsync())?.AsObject()
                    ?? throw new InvalidDataException("Private node configuration is required on stdin.");
                if (documents.Any(entry => nodes.All(node => node.NodeId != entry.Key)))
                    throw new InvalidDataException("Unknown node in initialization input.");
                foreach (var (nodeId, config) in documents)
                {
                    var before = await settings.GetAsync(nodeId);
                    var saved = await settings.SaveAsync(nodeId, Guid.NewGuid(), true,
                        config!.AsObject(), NodeConfigActor.System, before.Value?.SavedRevision?.Id);
                    if (saved.StatusCode == 200) continue;
                    Console.Error.WriteLine(JsonSerializer.Serialize(new { nodeId, saved.Errors }));
                    return 1;
                }
            }
            foreach (var node in nodes)
            {
                var result = await settings.GetAsync(node.NodeId);
                Console.WriteLine(JsonSerializer.Serialize(new { node.NodeId, node.NodeType, result.Value }));
            }
            return 0;
        }
        finally { await host.StopAsync(); }
    }

    private sealed record MigrationInput(string ConnectionString);
}
