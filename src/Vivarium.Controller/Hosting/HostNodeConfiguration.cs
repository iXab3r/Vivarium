using Chiral;

namespace Vivarium.Controller.Hosting;

public sealed class HostNodeConfiguration(IConfiguration configuration,
    ManagedNodeConfigurationProvider provider, NodeWorkloads workloads)
    : MappedNodeConfigurationModule("vivarium-host", typeof(HostNodeConfiguration).Assembly,
        "Vivarium.Controller.Hosting.Schemas.Host.json", configuration, provider, workloads,
        new Dictionary<string, string>
        {
            ["PublicOrigin"] = "Vivarium:PublicOrigin",
            ["ListenUrl"] = "Kestrel:Endpoints:Public:Url",
            ["StorageListenUrl"] = "Kestrel:Endpoints:Storage:Url",
            ["CertificatePath"] = "Kestrel:Certificates:Default:Path",
            ["CertificatePassword"] = "Kestrel:Certificates:Default:Password",
        })
{
    public override bool ApplyOnRestart => true;
}
