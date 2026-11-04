using Chiral;
using EyeAuras.Storage;

namespace Vivarium.Controller.Hosting;

/// <summary>Host-local Chiral control; durable application state belongs to PostgreSQL.</summary>
public static class VivariumNodes
{
    public static void AddVivariumNodes(this IServiceCollection services,
        ConfigurationManager configuration, string settingsDirectory)
    {
        configuration.Sources.Clear();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Nodes:ConfigurationDirectory"] = Path.GetFullPath(settingsDirectory),
            ["Chiral:Mode"] = "InMemory",
        });
        services.AddNodeFileStorage();
        services.AddManagedNodeConfiguration(configuration);
        services.AddChiral(configuration);
        services.AddNode(new NodeDescriptor("vivarium-host", "Host", "Vivarium Host") { Required = true });
        services.AddNode(new NodeDescriptor("vivarium-database", "Database", "PostgreSQL") { Required = true });
        services.AddNode(new NodeDescriptor("vivarium-identity", "Identity", "Sign-in and credentials") { Required = true });
        services.AddNode(new NodeDescriptor("vivarium-storage", "Storage", "Build bytes") { Required = true });
        services.AddSingleton<INodeConfigurationModule, HostNodeConfiguration>();
        services.AddSingleton<INodeConfigurationModule, DatabaseNodeConfiguration>();
        services.AddSingleton<INodeConfigurationModule, IdentityNodeConfiguration>();
        services.AddObjectStorage(configuration, new StorageNodeOptions("vivarium-storage", ApplyOnRestart: true));
    }
}
