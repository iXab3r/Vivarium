using System.Security.Cryptography;
using Vivarium.Controller.Security;

namespace Vivarium.Controller.Builds.Runtime;

/// <summary>Private transient transfer bytes; Storage and PostgreSQL retain the durable authority.</summary>
internal static class VerifiedArtifactFile
{
    internal const long MaximumBytes = 2L * 1024 * 1024 * 1024;
    internal static readonly TimeSpan TransferTimeout = TimeSpan.FromMinutes(15);
    private static readonly string DirectoryPath = Path.Combine(Path.GetTempPath(), "vivarium-private-transfers-" + Guid.NewGuid().ToString("N"));

    internal static async Task<FileStream> ReceiveAsync(Stream source, long length, string hash, CancellationToken token)
    {
        if (length is < 0 or > MaximumBytes) throw new InvalidDataException("Artifact transfer exceeds its selected bound.");
        PrivateStorage.EnsureDirectory(DirectoryPath);
        var options = new FileStreamOptions { Access = FileAccess.ReadWrite, Mode = FileMode.CreateNew,
            Share = FileShare.None, Options = FileOptions.Asynchronous | FileOptions.DeleteOnClose | FileOptions.SequentialScan };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        var file = new FileStream(Path.Combine(DirectoryPath, Guid.NewGuid().ToString("N") + ".bin"), options);
        try
        {
            // Keep the descriptor without a Unix directory entry, including across process death.
            if (!OperatingSystem.IsWindows()) File.Delete(file.Name);
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024]; long received = 0; int count;
            while ((count = await source.ReadAsync(buffer, token)) != 0)
            {
                if (count > length - received) throw new InvalidDataException("Artifact length differs.");
                digest.AppendData(buffer.AsSpan(0, count));
                await file.WriteAsync(buffer.AsMemory(0, count), token); received += count;
            }
            if (received != length || Convert.ToHexStringLower(digest.GetHashAndReset()) != hash)
                throw new InvalidDataException("Artifact integrity differs.");
            await file.FlushAsync(token); file.Position = 0;
            return file;
        }
        catch { await file.DisposeAsync(); throw; }
    }
}
