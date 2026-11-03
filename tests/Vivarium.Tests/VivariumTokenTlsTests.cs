using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Configuration;
using OpenIddict.Client;
using Vivarium.Controller.Tokens;

namespace Vivarium.Tests;

public sealed class VivariumTokenTlsTests
{
    [Test]
    public void Native_client_trust_is_limited_to_the_configured_origin_and_certificate()
    {
        using var rsa = RSA.Create(2048);
        using var certificate = new CertificateRequest("CN=owned-host", rsa, HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1).CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var otherRsa = RSA.Create(2048);
        using var other = new CertificateRequest("CN=other", otherRsa, HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1).CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        var root = Path.Combine(Path.GetTempPath(), "vivarium-token-tls-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "host.pfx");
            File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx));
            var settings = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Vivarium:PublicOrigin"] = "https://127.0.0.1:23456/",
                ["Kestrel:Certificates:Default:Path"] = path
            }).Build();
            using var handler = new HttpClientHandler();
            VivariumTokenRegistration.ConfigureSelfClientTls(new OpenIddictClientRegistration
                { RegistrationId = VivariumTokenProfile.Client }, handler, settings);
            Assert.That(handler.AllowAutoRedirect, Is.False);
            var validate = handler.ServerCertificateCustomValidationCallback!;
            using var own = new HttpRequestMessage(HttpMethod.Get, "https://127.0.0.1:23456/.well-known/openid-configuration");
            using var foreign = new HttpRequestMessage(HttpMethod.Get, "https://127.0.0.1:23457/connect/token");
            Assert.Multiple(() =>
            {
                Assert.That(validate(own, certificate, null, SslPolicyErrors.RemoteCertificateChainErrors), Is.True);
                Assert.That(validate(own, other, null, SslPolicyErrors.RemoteCertificateChainErrors), Is.False);
                Assert.That(validate(own, null, null, SslPolicyErrors.RemoteCertificateNotAvailable), Is.False);
                Assert.That(validate(foreign, certificate, null, SslPolicyErrors.None), Is.False);
                Assert.That(validate(foreign, certificate, null, SslPolicyErrors.RemoteCertificateChainErrors), Is.False);
                // A CA-valid public reverse proxy is still ordinary HTTPS for this origin.
                Assert.That(validate(own, other, null, SslPolicyErrors.None), Is.True);
            });
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Test]
    public void External_registration_keeps_framework_TLS_validation()
    {
        using var handler = new HttpClientHandler();
        VivariumTokenRegistration.ConfigureSelfClientTls(new OpenIddictClientRegistration
            { RegistrationId = "external:owned" }, handler, new ConfigurationBuilder().Build());
        Assert.That(handler.ServerCertificateCustomValidationCallback, Is.Null);
    }
}
