using System.Security.Cryptography;

namespace Vivarium.Tests.TeamCity;

internal static class CapturedSourceBinding
{
    public static string OriginalCrlfSha256(ReadOnlySpan<byte> checkoutBytes)
    {
        // These two receipts hash UTF-8/no-BOM source captured with CRLF. Git checkouts may
        // use LF. Reconstruct only those line endings; every other byte remains significant.
        using var capturedBytes = new MemoryStream();
        for (var i = 0; i < checkoutBytes.Length; i++)
        {
            if (checkoutBytes[i] == (byte)'\n' && (i == 0 || checkoutBytes[i - 1] != (byte)'\r'))
                capturedBytes.WriteByte((byte)'\r');
            capturedBytes.WriteByte(checkoutBytes[i]);
        }
        return Convert.ToHexStringLower(SHA256.HashData(capturedBytes.GetBuffer().AsSpan(0, (int)capturedBytes.Length)));
    }
}
