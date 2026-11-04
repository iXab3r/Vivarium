// Adapted from EyeAuras.Depot 20238384013326cddb3296963a6d101a0970efc0; consumer-owned fixture policy.
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CliWrap;
using EyeAuras.Automation;

namespace Vivarium.Acceptance.Fixtures;

/// <summary>Run-private Linux OpenSSL and Chromium NSS trust for real loopback HTTPS, never a validation bypass.</summary>
public sealed class RunTls
{
    public required string CertificatePath { get; init; }
    public required string CertificatePassword { get; init; }
    public required IReadOnlyDictionary<string, string?> Environment { get; init; }

    /// <summary>Create a private CA/loopback PFX and normal trust stores entirely inside the owned working directory.</summary>
    public static async Task<RunTls> CreateAsync(AutomationScope scope)
    {
        if (!OperatingSystem.IsLinux()) throw new InvalidOperationException("The issuer contour requires prepared Linux trust tools.");
        var run = await AutomationRun.ConnectAsync(scope.CancellationToken);
        var home = Path.GetFullPath(System.Environment.GetEnvironmentVariable("HOME")
            ?? throw new InvalidOperationException("Prepare a run-owned HOME before starting the Linux test process."));
        var workerHome = home.StartsWith(run.Directory + Path.DirectorySeparatorChar, StringComparison.Ordinal);
        var debugHome = Path.GetDirectoryName(run.Directory) == home && Guid.TryParseExact(Path.GetFileName(home), "N", out _);
        if (!workerHome && !debugHome)
            throw new InvalidOperationException("Prepare a run-owned HOME before starting the Linux test process.");
        Directory.CreateDirectory(home, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.SetUnixFileMode(home, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var root = Path.Combine(scope.WorkingDirectory, "tls");
        Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using var caKey = RSA.Create(2048);
        var caRequest = new CertificateRequest("CN=Vivarium run CA", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        using var ca = caRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=127.0.0.1", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(System.Net.IPAddress.Loopback);
        san.AddDnsName("localhost");
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        using var issued = request.Create(ca, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(12), RandomNumberGenerator.GetBytes(16));
        using var certificate = issued.CopyWithPrivateKey(key);
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var pfx = Path.Combine(root, "loopback.pfx");
        await File.WriteAllBytesAsync(pfx, certificate.Export(X509ContentType.Pfx, password), scope.CancellationToken);
        File.SetUnixFileMode(pfx, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var caPath = Path.Combine(root, "ca.pem");
        await File.WriteAllTextAsync(caPath, ca.ExportCertificatePem(), scope.CancellationToken);
        var nss = Path.Combine(home, ".pki/nssdb");
        var initializeNss = !File.Exists(Path.Combine(nss, "cert9.db"));
        Directory.CreateDirectory(nss);
        var nickname = "Vivarium run CA " + scope.Id;
        await scope.ExecuteAsync(Cli.Wrap("openssl").WithArguments(["rehash", root]), TimeSpan.FromSeconds(10));
        if (initializeNss)
            await scope.ExecuteAsync(Cli.Wrap("certutil").WithArguments(["-N", "-d", "sql:" + nss, "--empty-password"]), TimeSpan.FromSeconds(10));
        await scope.ExecuteAsync(Cli.Wrap("certutil").WithArguments(["-A", "-d", "sql:" + nss, "-n", nickname, "-t", "C,,", "-i", caPath]), TimeSpan.FromSeconds(10));
        var environment = new Dictionary<string, string?>
            { ["SSL_CERT_FILE"] = caPath, ["SSL_CERT_DIR"] = root, ["HOME"] = home,
                ["NODE_EXTRA_CA_CERTS"] = caPath };
        var previous = environment.Keys.ToDictionary(key => key, System.Environment.GetEnvironmentVariable);
        foreach (var pair in environment) System.Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        scope.BeforeStop("Restore run trust environment", (_, _) =>
        {
            foreach (var pair in previous) System.Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            return Task.CompletedTask;
        });
        // Import public certificate material only; no CA private key is persisted in the user trust store.
        var publicCa = X509CertificateLoader.LoadCertificate(ca.RawData);
        scope.BeforeStop("Remove the exact run trust anchor", async (_, token) =>
        {
            using var roots = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
            roots.Open(OpenFlags.ReadWrite);
            roots.Remove(publicCa);
            publicCa.Dispose();
            await PrivateCommand.ExecuteAsync(run, Cli.Wrap("certutil").WithArguments(["-D", "-d", "sql:" + nss, "-n", nickname]), null, token);
        });
        using (var roots = new X509Store(StoreName.Root, StoreLocation.CurrentUser))
        {
            roots.Open(OpenFlags.ReadWrite);
            roots.Add(publicCa);
        }
        if (!Directory.EnumerateFiles(home, "*.pfx", SearchOption.AllDirectories).Any(path =>
            string.Equals(Path.GetFileNameWithoutExtension(path), publicCa.Thumbprint, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("The runtime user trust store is outside the run-private HOME.");
        return new() { CertificatePath = pfx, CertificatePassword = password, Environment = environment };
    }
}
