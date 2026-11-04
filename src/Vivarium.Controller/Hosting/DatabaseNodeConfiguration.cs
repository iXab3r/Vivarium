using Chiral;

namespace Vivarium.Controller.Hosting;

public sealed class DatabaseNodeConfiguration(IConfiguration configuration,
    ManagedNodeConfigurationProvider provider, NodeWorkloads workloads)
    : MappedNodeConfigurationModule("vivarium-database", typeof(DatabaseNodeConfiguration).Assembly,
        "Vivarium.Controller.Hosting.Schemas.Database.json", configuration, provider, workloads,
        new Dictionary<string, string> { ["ConnectionString"] = "ConnectionStrings:Vivarium" })
{
    public override bool ApplyOnRestart => true;
}
