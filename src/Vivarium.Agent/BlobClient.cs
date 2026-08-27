using System.Security.Cryptography;

namespace Vivarium.Agent;

/// <summary>Pull payloads / push artifacts against the controller's blob store (D4).</summary>
public sealed class BlobClient : IDisposable
{
    private readonly HttpClient http;

    public string? BearerToken { get; set; }

    public BlobClient(string controllerUrl, string fingerprintSha256)
    {
        http = new HttpClient(PinnedTls.CreateHandler(fingerprintSha256))
        {
            BaseAddress = new Uri(controllerUrl),
        };
    }

    public async Task DownloadAsync(string sha256, string targetPath, CancellationToken ct)
    {
        using var request = NewRequest(HttpMethod.Get, sha256);
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

    public async Task<string> UploadAsync(string filePath, CancellationToken ct)
    {
        string sha256;
        await using (var stream = File.OpenRead(filePath))
        {
            sha256 = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
        }

        using var request = NewRequest(HttpMethod.Put, sha256);
        await using var content = File.OpenRead(filePath);
        request.Content = new StreamContent(content);
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return sha256;
    }

    private HttpRequestMessage NewRequest(HttpMethod method, string sha256)
    {
        var request = new HttpRequestMessage(method, $"/blobs/{sha256}");
        if (BearerToken is { Length: > 0 })
        {
            request.Headers.Authorization = new("Bearer", BearerToken);
        }

        return request;
    }

    public void Dispose() => http.Dispose();
}
