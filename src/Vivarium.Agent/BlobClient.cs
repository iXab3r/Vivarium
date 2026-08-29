using System.Security.Cryptography;

namespace Vivarium.Agent;

/// <summary>Pull payloads / push artifacts against the controller's blob store (D4).</summary>
public sealed class BlobClient : IDisposable
{
    public const string BuildIdHeader = "X-Vivarium-Build-Id";
    public const string SessionIdHeader = "X-Vivarium-Session-Id";
    public const string DeclaredSizeHeader = "X-Vivarium-Blob-Declared-Size";
    private readonly HttpClient http;

    public string? BearerToken { get; set; }

    public BlobClient(string controllerUrl, string fingerprintSha256)
        : this(new Uri(controllerUrl), PinnedTls.CreateHandler(fingerprintSha256))
    {
    }

    public BlobClient(Uri controllerUrl, HttpMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(controllerUrl);
        ArgumentNullException.ThrowIfNull(handler);
        http = new HttpClient(handler)
        {
            BaseAddress = controllerUrl,
        };
    }

    public async Task DownloadAsync(
        string sha256,
        string targetPath,
        string buildId,
        string sessionId,
        CancellationToken ct)
    {
        using var request = NewRequest(HttpMethod.Get, sha256, buildId, sessionId);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var file = File.Create(targetPath))
        await using (var body = await response.Content.ReadAsStreamAsync(ct))
        {
            var buffer = new byte[81920];
            int read;
            while ((read = await body.ReadAsync(buffer, ct)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                await file.WriteAsync(buffer.AsMemory(0, read), ct);
            }
        }

        var actual = Convert.ToHexString(hash.GetHashAndReset());
        if (!actual.Equals(sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(targetPath);
            throw new InvalidOperationException($"blob {sha256} failed hash verification");
        }
    }

    public async Task<string> UploadAsync(
        string filePath,
        string buildId,
        string sessionId,
        CancellationToken ct)
    {
        string sha256;
        await using (var stream = File.OpenRead(filePath))
        {
            sha256 = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
        }

        var declaredSize = new FileInfo(filePath).Length;
        using var request = NewRequest(HttpMethod.Put, sha256, buildId, sessionId);
        request.Headers.TryAddWithoutValidation(
            DeclaredSizeHeader,
            declaredSize.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await using var content = File.OpenRead(filePath);
        request.Content = new StreamContent(content);
        request.Content.Headers.ContentLength = declaredSize;
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return sha256;
    }

    private HttpRequestMessage NewRequest(
        HttpMethod method,
        string sha256,
        string buildId,
        string sessionId)
    {
        RequireHeaderIdentity(buildId, nameof(buildId));
        RequireHeaderIdentity(sessionId, nameof(sessionId));
        var request = new HttpRequestMessage(method, $"/blobs/{sha256}");
        request.Headers.TryAddWithoutValidation(BuildIdHeader, buildId);
        request.Headers.TryAddWithoutValidation(SessionIdHeader, sessionId);
        if (BearerToken is { Length: > 0 })
        {
            request.Headers.Authorization = new("Bearer", BearerToken);
        }

        return request;
    }

    private static void RequireHeaderIdentity(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl))
        {
            throw new ArgumentException(
                "blob transfer identity must contain 1-256 non-control characters",
                parameterName);
        }
    }

    public void Dispose() => http.Dispose();
}
