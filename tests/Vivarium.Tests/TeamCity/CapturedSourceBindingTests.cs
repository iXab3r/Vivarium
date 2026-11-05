using System.Security.Cryptography;
using System.Text;

namespace Vivarium.Tests.TeamCity;

public sealed class CapturedSourceBindingTests
{
    [Test]
    public void Checkout_line_endings_reconstruct_original_bytes_without_hiding_source_or_encoding_changes()
    {
        var captured = "// café\r\nconst value = 1;\r\n"u8.ToArray();
        var expected = Convert.ToHexStringLower(SHA256.HashData(captured));
        var lf = "// café\nconst value = 1;\n"u8.ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(CapturedSourceBinding.OriginalCrlfSha256(captured), Is.EqualTo(expected));
            Assert.That(CapturedSourceBinding.OriginalCrlfSha256(lf), Is.EqualTo(expected));
            foreach (var changed in new[]
            {
                "// café\nconst value = 2;\n"u8.ToArray(), // Content change.
                "// café\nconst value = 1; \n"u8.ToArray(), // Whitespace is still source.
                "// café\nconst value = 1;"u8.ToArray(), // Missing final newline.
                "// café\rconst value = 1;\r"u8.ToArray(), // Lone CR is not a checkout LF.
                Encoding.UTF8.GetPreamble().Concat(lf).ToArray(),
                Encoding.Unicode.GetBytes("// café\nconst value = 1;\n"),
                new byte[] { 0xff }.Concat(lf).ToArray() // Invalid UTF-8 is not repaired.
            })
                Assert.That(CapturedSourceBinding.OriginalCrlfSha256(changed), Is.Not.EqualTo(expected));
        });
    }
}
