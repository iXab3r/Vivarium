using System.Security.Cryptography;

namespace Vivarium.Controller.Blobs;

public enum BlobPutResult
{
    Created,
    AlreadyExists,
    DigestMismatch,
    SizeMismatch,
    SizeLimitExceeded,
}

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
        if (!IsSha256(sha256))
        {
            return null;
        }

        var path = PathFor(sha256);
        return File.Exists(path) ? path : null;
    }

    public bool Contains(string sha256) => GetPath(sha256) != null;

    /// <summary>Streams the body to a temp file while hashing; commits only on a hash match.</summary>
    public async Task<bool> PutAsync(string sha256, Stream body, CancellationToken ct) =>
        await PutWithDispositionAsync(sha256, body, ct) != BlobPutResult.DigestMismatch;

    public async Task<BlobPutResult> PutWithDispositionAsync(
        string sha256,
        Stream body,
        CancellationToken ct) =>
        await PutWithDispositionCoreAsync(
            sha256,
            body,
            expectedSize: null,
            maximumSize: long.MaxValue,
            ct);

    /// <summary>
    /// Bounded staged upload: verifies both the declared byte count and digest before commit.
    /// </summary>
    public Task<BlobPutResult> PutWithDispositionAsync(
        string sha256,
        Stream body,
        long expectedSize,
        long maximumSize,
        CancellationToken ct)
    {
        if (expectedSize < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedSize));
        }

        if (maximumSize < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumSize));
        }

        return PutWithDispositionCoreAsync(sha256, body, expectedSize, maximumSize, ct);
    }

    private async Task<BlobPutResult> PutWithDispositionCoreAsync(
        string sha256,
        Stream body,
        long? expectedSize,
        long maximumSize,
        CancellationToken ct)
    {
        if (!IsSha256(sha256))
        {
            return BlobPutResult.DigestMismatch;
        }

        var target = PathFor(sha256);
        if (File.Exists(target))
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            long length = 0;
            int read;
            while ((read = await body.ReadAsync(buffer, ct)) > 0)
            {
                length = checked(length + read);
                if (length > maximumSize)
                {
                    return BlobPutResult.SizeLimitExceeded;
                }

                hash.AppendData(buffer, 0, read);
            }

            var actual = Convert.ToHexString(hash.GetHashAndReset());
            if (expectedSize is not null && length != expectedSize.Value)
            {
                return BlobPutResult.SizeMismatch;
            }

            return actual.Equals(sha256, StringComparison.OrdinalIgnoreCase)
                ? BlobPutResult.AlreadyExists
                : BlobPutResult.DigestMismatch;
        }

        var temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                await using var file = File.Create(temp);
                var buffer = new byte[81920];
                long length = 0;
                int read;
                while ((read = await body.ReadAsync(buffer, ct)) > 0)
                {
                    length = checked(length + read);
                    if (length > maximumSize)
                    {
                        return BlobPutResult.SizeLimitExceeded;
                    }

                    hash.AppendData(buffer, 0, read);
                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                }

                if (expectedSize is not null && length != expectedSize.Value)
                {
                    return BlobPutResult.SizeMismatch;
                }

                var actual = Convert.ToHexString(hash.GetHashAndReset());
                if (!actual.Equals(sha256, StringComparison.OrdinalIgnoreCase))
                {
                    return BlobPutResult.DigestMismatch;
                }
            }

            var created = true;
            try
            {
                File.Move(temp, target);
            }
            catch (IOException) when (File.Exists(target))
            {
                // Lost a race to an identical blob — fine.
                created = false;
            }

            return created ? BlobPutResult.Created : BlobPutResult.AlreadyExists;
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    public static bool IsSha256(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);

    private string PathFor(string sha256) => Path.Combine(root, sha256.ToLowerInvariant());
}
