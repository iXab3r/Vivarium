using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

namespace Vivarium.Cli;

internal interface IServerCertificateProbe
{
    Task<string> GetFingerprintAsync(string controllerUrl, CancellationToken cancellationToken);
}

internal sealed class ServerCertificateProbe : IServerCertificateProbe
{
    public async Task<string> GetFingerprintAsync(string controllerUrl, CancellationToken cancellationToken)
    {
        var normalized = PinnedTls.NormalizeControllerUrl(controllerUrl);
        var uri = new Uri(normalized, UriKind.Absolute);
        var port = uri.IsDefaultPort ? 443 : uri.Port;
        using var client = new TcpClient();
        await client.ConnectAsync(uri.IdnHost, port, cancellationToken);

        byte[]? certificateBytes = null;
        using var tls = new SslStream(
            client.GetStream(),
            leaveInnerStreamOpen: false,
            (_, certificate, _, _) =>
            {
                certificateBytes = certificate?.GetRawCertData();
                return true; // Discovery only; every subsequent connection is exactly pinned.
            });
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = uri.IdnHost,
            EnabledSslProtocols = SslProtocols.None,
        }, cancellationToken);

        if (certificateBytes is null)
        {
            throw new AuthenticationException("controller did not present a TLS certificate");
        }

        return PinnedTls.FormatFingerprint(SHA256.HashData(certificateBytes));
    }
}

internal static partial class PinnedTls
{
    public static string NormalizeControllerUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidOperationException("controller URL must be an absolute https URL without credentials, query, or fragment");
        }

        var builder = new UriBuilder(uri)
        {
            Scheme = Uri.UriSchemeHttps,
            Host = uri.IdnHost,
            Path = uri.AbsolutePath.TrimEnd('/'),
            Query = string.Empty,
            Fragment = string.Empty,
        };
        var normalized = builder.Uri.AbsoluteUri.TrimEnd('/');
        return normalized;
    }

    public static string NormalizeFingerprint(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var candidate = value.Trim();
        if (candidate.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase))
        {
            candidate = candidate[7..];
        }

        candidate = candidate.Replace(":", string.Empty, StringComparison.Ordinal);
        if (!FingerprintPattern().IsMatch(candidate))
        {
            throw new InvalidOperationException(
                "certificate fingerprint must be SHA256 followed by exactly 64 hexadecimal digits");
        }

        return "SHA256:" + candidate.ToUpperInvariant();
    }

    public static bool Matches(string expected, X509Certificate? certificate)
    {
        if (certificate is null)
        {
            return false;
        }

        var expectedBytes = Convert.FromHexString(NormalizeFingerprint(expected)[7..]);
        var actualBytes = SHA256.HashData(certificate.GetRawCertData());
        return CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }

    public static HttpClientHandler CreateHandler(string fingerprint) => new()
    {
        ServerCertificateCustomValidationCallback = (_, certificate, _, _) => Matches(fingerprint, certificate),
    };

    public static string FormatFingerprint(ReadOnlySpan<byte> hash) =>
        "SHA256:" + Convert.ToHexString(hash);

    [GeneratedRegex("^[0-9A-Fa-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex FingerprintPattern();
}
