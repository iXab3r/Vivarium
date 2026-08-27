using System.Security.Cryptography;

namespace Vivarium.Controller.Blobs;

/// <summary>
/// Content-addressed blob store (D4): idempotent PUT, server-side hash verification —
/// a body that does not hash to its name is rejected, never stored.
/// </summary>
public sealed class BlobStore
{
    private readonly string root;

    public BlobStore(string root)
    {
        this.root = root;
        Directory.CreateDirectory(root);
    }

    public string? GetPath(string sha256)
    {
        var path = PathFor(sha256);
        return File.Exists(path) ? path : null;
    }

    public bool Contains(string sha256) => GetPath(sha256) != null;

    /// <summary>Streams the body to a temp file while hashing; commits only on a hash match.</summary>
    public async Task<bool> PutAsync(string sha256, Stream body, CancellationToken ct)
    {
        var target = PathFor(sha256);
        if (File.Exists(target))
        {
            // Idempotent: content-addressing means an existing blob is by definition correct.
            await body.CopyToAsync(Stream.Null, ct);
            return true;
        }

        var temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                await using var file = File.Create(temp);
                var buffer = new byte[81920];
                int read;
                while ((read = await body.ReadAsync(buffer, ct)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                }

                var actual = Convert.ToHexString(hash.GetHashAndReset());
                if (!actual.Equals(sha256, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            try
            {
                File.Move(temp, target);
            }
            catch (IOException) when (File.Exists(target))
            {
                // Lost a race to an identical blob — fine.
            }

            return true;
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    private string PathFor(string sha256) => Path.Combine(root, sha256.ToLowerInvariant());
}
