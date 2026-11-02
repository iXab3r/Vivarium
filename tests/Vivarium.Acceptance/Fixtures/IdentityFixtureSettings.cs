// Adapted from EyeAuras.Depot 20238384013326cddb3296963a6d101a0970efc0; consumer-owned fixture policy.
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using EyeAuras.Automation;

namespace Vivarium.Acceptance.Fixtures;

/// <summary>Minimal complete production Identity node profile with four distinct persistent run-owned keys.</summary>
public static class IdentityFixtureSettings
{
    /// <summary>Create ordinary provider-neutral settings; optional published issuer adds the one explicit test registration.</summary>
    public static async Task<Dictionary<string, object>> CreateAsync(AutomationScope scope, IssuerFixture? issuer = null)
    {
        var settings = new Dictionary<string, object>
        {
            ["SuperuserEnabled"] = false, ["TokenLifetimeDays"] = 1,
            ["DataProtectionDirectory"] = Path.Combine(scope.WorkingDirectory, "data-protection"),
            ["Providers"] = issuer is null ? Array.Empty<object>() : [new { Key = "eyeauras", issuer.Issuer, issuer.ClientId, issuer.ClientSecret,
                Scopes = new[] { "openid", "profile", "email" } }],
            ["TokenCreationPermitLimit"] = 10, ["TokenCreationWindowSeconds"] = 60,
            ["TokenCreationConcurrencyLimit"] = 2, ["TokenCreationQueueLimit"] = 0,
            ["TokenRedemptionConcurrencyLimit"] = 8
        };
        foreach (var name in new[] { "ServerSigningCertificate", "ServerEncryptionCertificate", "ClientSigningCertificate", "ClientEncryptionCertificate" })
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=Vivarium run " + name, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
            var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            var path = Path.Combine(scope.WorkingDirectory, name + ".pfx");
            await File.WriteAllBytesAsync(path, certificate.Export(X509ContentType.Pfx, password), scope.CancellationToken);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            settings[name] = new { Path = path, Password = password };
        }
        return settings;
    }
}
