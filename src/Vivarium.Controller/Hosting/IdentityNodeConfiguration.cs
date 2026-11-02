using Chiral;
using System.Text.Json.Nodes;

namespace Vivarium.Controller.Hosting;

/// <summary>Identity settings share Chiral persistence; user memberships remain database business data.</summary>
public sealed class IdentityNodeConfiguration(IConfiguration configuration, ManagedNodeConfigurationProvider provider,
    NodeWorkloads workloads) : MappedNodeConfigurationModule("vivarium-identity", typeof(IdentityNodeConfiguration).Assembly,
    "Vivarium.Controller.Hosting.Schemas.Identity.json", configuration, provider, workloads,
    new Dictionary<string, string>
    {
        ["SuperuserEnabled"] = "Vivarium:Identity:SuperuserEnabled",
        ["TokenLifetimeDays"] = "Vivarium:Identity:TokenLifetimeDays",
        ["DataProtectionDirectory"] = "Vivarium:Identity:DataProtectionDirectory",
        ["ServerSigningCertificate"] = "Vivarium:Identity:ServerSigningCertificate",
        ["ServerEncryptionCertificate"] = "Vivarium:Identity:ServerEncryptionCertificate",
        ["ClientSigningCertificate"] = "Vivarium:Identity:ClientSigningCertificate",
        ["ClientEncryptionCertificate"] = "Vivarium:Identity:ClientEncryptionCertificate",
        ["Providers"] = "Vivarium:Identity:Providers",
        ["TokenCreationPermitLimit"] = "Vivarium:Identity:TokenCreationPermitLimit",
        ["TokenCreationWindowSeconds"] = "Vivarium:Identity:TokenCreationWindowSeconds",
        ["TokenCreationConcurrencyLimit"] = "Vivarium:Identity:TokenCreationConcurrencyLimit",
        ["TokenCreationQueueLimit"] = "Vivarium:Identity:TokenCreationQueueLimit",
        ["TokenRedemptionConcurrencyLimit"] = "Vivarium:Identity:TokenRedemptionConcurrencyLimit"
    })
{
    public override bool ApplyOnRestart => true;

    public override IReadOnlyDictionary<string, string[]> Validate(JsonObject config, bool enabled)
    {
        var errors = base.Validate(config, enabled).ToDictionary(entry => entry.Key, entry => entry.Value);
        if (config["Providers"] is not JsonArray providers) return errors;
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (provider, index) in providers.Select((value, index) => (value, index)))
        {
            if (provider is not JsonObject item) continue;
            if (!keys.Add(item["Key"]?.ToString() ?? "")) errors[$"/Providers/{index}/Key"] = ["validation.unique"];
            if (!Uri.TryCreate(item["Issuer"]?.ToString(), UriKind.Absolute, out var issuer)
                || issuer.Scheme != Uri.UriSchemeHttps || issuer.UserInfo.Length != 0
                || issuer.Query.Length != 0 || issuer.Fragment.Length != 0)
                errors[$"/Providers/{index}/Issuer"] = ["validation.httpsIssuer"];
        }
        return errors;
    }
}
