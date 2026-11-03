using System.Security.Cryptography;
using EyeAuras.Storage.Contracts;
using EyeAuras.Storage;

namespace Vivarium.Controller.Builds.Runtime;

/// <summary>Internal Storage bytes; signed URLs never cross the public management boundary.</summary>
internal sealed class RuntimeBuildBytes(IObjectStorage storage) : IDisposable
{
    private readonly HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };

    internal async Task PutAsync(string key, ReadOnlyMemory<byte> bytes, CancellationToken token)
    {
        var upload = await storage.UploadAsync(new GetUploadUrl(key, "application/octet-stream")).WaitAsync(TimeSpan.FromSeconds(15), token);
        using var content = new ByteArrayContent(bytes.ToArray()); content.Headers.ContentType = new("application/octet-stream");
        using var result = await http.PutAsync(upload.Url, content, token); result.EnsureSuccessStatusCode();
    }

    internal async Task<byte[]> ReadAsync(string key, long expectedLength, string expectedHash, CancellationToken token)
    {
        if (expectedLength is < 0 or > 64 * 1024) throw new InvalidDataException("Inline output bytes exceed their bound.");
        var download = await storage.DownloadAsync(new GetDownloadUrl(key, "output.bin")).WaitAsync(TimeSpan.FromSeconds(15), token);
        using var response = await http.GetAsync(download.Url, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(token);
        using var result = new MemoryStream(); var buffer = new byte[8192]; int read;
        while ((read = await source.ReadAsync(buffer, token)) > 0)
        {
            if (result.Length + read > expectedLength) throw new InvalidDataException("Stored output length differs.");
            result.Write(buffer, 0, read);
        }
        var bytes = result.ToArray();
        if (bytes.LongLength != expectedLength || Convert.ToHexStringLower(SHA256.HashData(bytes)) != expectedHash)
            throw new InvalidDataException("Stored output integrity differs.");
        return bytes;
    }

    internal Task DeleteAsync(string key, CancellationToken token) => storage.DeleteAsync(key, token);
    public void Dispose() => http.Dispose();
}
