using System.Security.Cryptography.X509Certificates;

namespace Vivarium.Controller.Identity;

/// <summary>Persistent framework credentials loaded once after Chiral restoration; missing files fail startup.</summary>
internal sealed class VivariumIdentityKeys : IDisposable
{
    public X509Certificate2 ServerSigning { get; }
    public X509Certificate2 ServerEncryption { get; }
    public X509Certificate2 ClientSigning { get; }
    public X509Certificate2 ClientEncryption { get; }

    public VivariumIdentityKeys(IConfiguration configuration)
    {
        var loaded = new List<X509Certificate2>();
        X509Certificate2 Read(string name)
        {
            var certificate = Load(configuration, name);
            loaded.Add(certificate);
            return certificate;
        }
        try
        {
            ServerSigning = Read("ServerSigningCertificate");
            ServerEncryption = Read("ServerEncryptionCertificate");
            ClientSigning = Read("ClientSigningCertificate");
            ClientEncryption = Read("ClientEncryptionCertificate");
            if (loaded.Select(certificate => certificate.Thumbprint).Distinct(StringComparer.Ordinal).Count() != 4)
                throw new InvalidOperationException("Identity requires four separate persistent certificates.");
        }
        catch
        {
            foreach (var certificate in loaded) certificate.Dispose();
            throw;
        }
    }

    private static X509Certificate2 Load(IConfiguration configuration, string name)
    {
        var path = configuration[$"Vivarium:Identity:{name}:Path"];
        if (path is null || !Path.IsPathFullyQualified(path))
            throw new InvalidOperationException("An absolute Identity certificate path is required.");
        var certificate = X509CertificateLoader.LoadPkcs12FromFile(path,
            configuration[$"Vivarium:Identity:{name}:Password"],
            X509KeyStorageFlags.EphemeralKeySet);
        if (!certificate.HasPrivateKey || certificate.NotBefore.ToUniversalTime() > DateTime.UtcNow
            || certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow)
        {
            certificate.Dispose();
            throw new InvalidOperationException("Identity requires valid certificates with private keys.");
        }
        return certificate;
    }

    public void Dispose()
    {
        ServerSigning.Dispose();
        ServerEncryption.Dispose();
        ClientSigning.Dispose();
        ClientEncryption.Dispose();
    }
}
