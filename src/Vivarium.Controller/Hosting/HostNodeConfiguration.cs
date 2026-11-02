using Chiral;
using System.Text.Json.Nodes;

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
            ["DefinitionSources"] = "Vivarium:Definitions",
        })
{
    public override bool ApplyOnRestart => true;

    public override IReadOnlyDictionary<string, string[]> Validate(JsonObject config, bool enabled)
    {
        var errors = base.Validate(config, enabled).ToDictionary(p => p.Key, p => p.Value);
        if (config["DefinitionSources"] is not JsonObject sources) return errors;
        if (!Path.IsPathFullyQualified(sources["Path"]?.ToString() ?? ""))
            errors["/DefinitionSources/Path"] = ["validation.absolutePath"];
        var ids = new HashSet<string>(StringComparer.Ordinal) { sources["RepositoryId"]?.ToString() ?? "" };
        if (sources["SettingsRepositories"] is JsonArray repositories)
            foreach (var (value, index) in repositories.Select((item, index) => (item, index)))
            {
                if (value is not JsonObject repository) continue;
                if (!ids.Add(repository["Id"]?.ToString() ?? ""))
                    errors[$"/DefinitionSources/SettingsRepositories/{index}/Id"] = ["validation.unique"];
                if (!Path.IsPathFullyQualified(repository["Path"]?.ToString() ?? ""))
                    errors[$"/DefinitionSources/SettingsRepositories/{index}/Path"] = ["validation.absolutePath"];
            }
        return errors;
    }
}
