using System.Security.Cryptography;

namespace Vivarium.Agent;

/// <summary>
/// Pinned-fingerprint TLS validation (D4): the certificate is trusted iff its SHA-256 matches the
/// pinned value. Chain and validity dates are deliberately ignored — a guest waking from a memory
/// checkpoint has a clock in the past.
/// </summary>
public static class PinnedTls
{
    public static SocketsHttpHandler CreateHandler(string fingerprintSha256, bool keepAlive = false)
    {
        var handler = new SocketsHttpHandler();
        handler.SslOptions.RemoteCertificateValidationCallback = (_, cert, _, _) =>
            cert != null &&
            Convert.ToHexString(SHA256.HashData(cert.GetRawCertData()))
                .Equals(fingerprintSha256, StringComparison.OrdinalIgnoreCase);

        if (keepAlive)
        {
            // Detect a connection killed by a checkpoint restore in seconds, not TCP-timeout minutes (D4).
            handler.KeepAlivePingDelay = TimeSpan.FromSeconds(10);
            handler.KeepAlivePingTimeout = TimeSpan.FromSeconds(5);
            handler.KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always;
        }

        return handler;
    }
}
