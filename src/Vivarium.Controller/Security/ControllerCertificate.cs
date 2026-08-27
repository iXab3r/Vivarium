using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Vivarium.Controller.Security;

/// <summary>
/// The controller's self-signed TLS identity (D4). Generated once, persisted in the data dir;
/// clients validate by pinned SHA-256 fingerprint with dates ignored, so long validity is fine.
/// </summary>
public sealed class ControllerCertificate
{
    public X509Certificate2 Certificate { get; }

    /// <summary>Uppercase hex SHA-256 of the DER-encoded certificate, no separators.</summary>
    public string FingerprintSha256 { get; }

    private ControllerCertificate(X509Certificate2 certificate)
    {
        Certificate = certificate;
        FingerprintSha256 = Convert.ToHexString(SHA256.HashData(certificate.RawData));
    }

    public static ControllerCertificate LoadOrCreate(string dataDir)
    {
        var path = Path.Combine(dataDir, "controller.pfx");
        if (!File.Exists(path))
        {
            File.WriteAllBytes(path, CreateSelfSigned());
        }

        var cert = X509CertificateLoader.LoadPkcs12FromFile(path, password: null, X509KeyStorageFlags.Exportable);
        return new ControllerCertificate(cert);
    }

    private static byte[] CreateSelfSigned()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=vivarium-controller", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddDnsName(Environment.MachineName);
        request.CertificateExtensions.Add(san.Build());

        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(20));
        return cert.Export(X509ContentType.Pfx);
    }
}
