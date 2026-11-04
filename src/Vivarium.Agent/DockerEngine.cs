using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace Vivarium.Agent;

/// <summary>Private installation connection. Project definitions never select this authority.</summary>
public sealed record DockerRuntimeOptions(string Endpoint, string CertificateDirectory, string Scope);

internal sealed class DockerEngineException(string code, int? status = null) : IOException(code)
{
    internal string Code { get; } = code;
    internal int? Status { get; } = status;
}

/// <summary>Bounded Engine API transport; remote error bodies and environment values are never diagnostics.</summary>
internal sealed class DockerEngine : IDisposable
{
    private const string Api = "v1.45/";
    private readonly X509Certificate2 root;
    private readonly X509Certificate2 identity;
    private readonly HttpClient http;

    internal DockerEngine(DockerRuntimeOptions options)
    {
        if (!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme != "https"
            || endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0
            || endpoint.AbsolutePath != "/" || !Path.IsPathFullyQualified(options.CertificateDirectory)
            || !System.Text.RegularExpressions.Regex.IsMatch(options.Scope, "^[a-z0-9][a-z0-9-]{0,63}$"))
            throw new InvalidDataException("The installed Docker runtime connection is invalid.");
        root = X509Certificate2.CreateFromPem(File.ReadAllText(Path.Combine(options.CertificateDirectory, "ca.pem")));
        identity = X509Certificate2.CreateFromPemFile(Path.Combine(options.CertificateDirectory, "cert.pem"), Path.Combine(options.CertificateDirectory, "key.pem"));
        var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(10), MaxConnectionsPerServer = 8 };
        handler.SslOptions.ClientCertificates = new() { identity };
        handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
        {
            if (certificate is null || (errors & (SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable)) != 0) return false;
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(root);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            using var peer = new X509Certificate2(certificate);
            return chain.Build(peer);
        };
        http = new(handler) { BaseAddress = endpoint, Timeout = Timeout.InfiniteTimeSpan };
    }

    internal async Task<string> IdentityAsync(CancellationToken token)
    {
        using var document = await GetAsync("info", token) ?? throw new DockerEngineException("runtime_unavailable");
        return document.RootElement.GetProperty("ID").GetString() ?? throw new DockerEngineException("runtime_identity_unavailable");
    }

    internal async Task<JsonDocument?> GetAsync(string path, CancellationToken token, bool missingAllowed = false)
    {
        using var response = await SendAsync(HttpMethod.Get, path, null, token);
        if (missingAllowed && response.StatusCode == HttpStatusCode.NotFound) return null;
        Check(response);
        return await ReadJsonAsync(response, token);
    }

    internal async Task<JsonDocument> PostAsync(string path, object body, CancellationToken token)
    {
        using var response = await SendAsync(HttpMethod.Post, path, body, token);
        Check(response);
        return await ReadJsonAsync(response, token);
    }

    internal async Task CommandAsync(HttpMethod method, string path, CancellationToken token, bool missingAllowed = false)
    {
        using var response = await SendAsync(method, path, null, token);
        if (missingAllowed && response.StatusCode == HttpStatusCode.NotFound) return;
        Check(response);
    }

    // The caller owns the response until the multiplexed stream is drained or explicitly lost.
    internal async Task<HttpResponseMessage> StreamAsync(string path, object? body, CancellationToken token)
    {
        var response = await SendAsync(HttpMethod.Post, path, body, token);
        try { Check(response); return response; }
        catch { response.Dispose(); throw; }
    }

    internal async Task EnsureImageAsync(string image, string platform, CancellationToken token)
    {
        using var existing = await GetAsync("images/" + Uri.EscapeDataString(image) + "/json", token, missingAllowed: true);
        if (existing is not null) return;
        using var response = await StreamAsync("images/create?fromImage=" + Uri.EscapeDataString(image)
            + "&platform=" + Uri.EscapeDataString(platform), null, token);
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var line = new MemoryStream(); var buffer = new byte[8192]; int count;
        while ((count = await stream.ReadAsync(buffer, token)) != 0)
        {
            for (var index = 0; index < count; index++)
            {
                if (buffer[index] == (byte)'\n') { CheckLine(); continue; }
                if (line.Length == 65536) throw new DockerEngineException("image_pull_output_limit");
                line.WriteByte(buffer[index]);
            }
        }
        CheckLine();
        void CheckLine()
        {
            if (line.Length == 0) return;
            using var progress = JsonDocument.Parse(line.GetBuffer().AsMemory(0, checked((int)line.Length)));
            if (progress.RootElement.TryGetProperty("error", out _)) throw new DockerEngineException("image_pull_failed");
            line.SetLength(0);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, CancellationToken token)
    {
        using var request = new HttpRequestMessage(method, Api + path) { Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact };
        if (body is not null)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(body); // Engine field names are deliberately preserved.
            if (bytes.Length > 4 * 1024 * 1024) throw new DockerEngineException("runtime_request_limit");
            request.Content = new ByteArrayContent(bytes); request.Content.Headers.ContentType = new("application/json");
        }
        return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken token)
    {
        await using var source = await response.Content.ReadAsStreamAsync(token);
        using var bytes = new MemoryStream(); var buffer = new byte[8192]; int count;
        while ((count = await source.ReadAsync(buffer, token)) != 0)
        {
            if (bytes.Length + count > 4 * 1024 * 1024) throw new DockerEngineException("runtime_response_limit");
            bytes.Write(buffer, 0, count);
        }
        return JsonDocument.Parse(bytes.GetBuffer().AsMemory(0, checked((int)bytes.Length)));
    }

    private static void Check(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode) throw new DockerEngineException("runtime_request_failed", (int)response.StatusCode);
    }

    public void Dispose() { http.Dispose(); identity.Dispose(); root.Dispose(); }
}
