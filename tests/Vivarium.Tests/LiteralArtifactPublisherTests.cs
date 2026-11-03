using System.Diagnostics;
using Vivarium.Agent;
using Vivarium.Contracts;

namespace Vivarium.Tests;

public sealed class LiteralArtifactPublisherTests
{
    [Test]
    public void Split_service_message_and_escaped_filename_publish_only_the_declared_literal_file()
    {
        var root = Path.Combine(Path.GetTempPath(), "vivarium-publisher-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "proof'file.txt"), "owned literal");
            File.WriteAllText(Path.Combine(root, "unpublished.txt"), "must not collect");
            var publisher = new LiteralArtifactPublisher();
            publisher.Append("ordinary output\n##teamcity[publishArt"u8);
            publisher.Append("ifacts 'proof|'file.txt']\r\n"u8);
            publisher.FinishStep();
            Assert.That(publisher.Files(root), Is.EqualTo(new[] { "proof'file.txt" }));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestCase("../outside.txt")]
    [TestCase("/absolute.txt")]
    [TestCase("C:/host-secret.txt")]
    [TestCase("dir/CON.txt")]
    [TestCase("dir/trailing.")]
    [TestCase("*.txt")]
    [TestCase("proof.txt => renamed.txt")]
    public void Literal_profile_rejects_escape_alias_and_rule_expressions(string path) =>
        Assert.Throws<InvalidDataException>(() => LiteralArtifactPath.Validate(path));

    [Test]
    public void Linked_publication_is_rejected_without_reading_its_target()
    {
        var root = Path.Combine(Path.GetTempPath(), "vivarium-publisher-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "target"));
            File.WriteAllText(Path.Combine(root, "target/proof.txt"), "target must not be collected");
            if (OperatingSystem.IsWindows())
            {
                var start = new ProcessStartInfo("cmd.exe") { WorkingDirectory = root, UseShellExecute = false,
                    CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var argument in new[] { "/d", "/c", "mklink", "/J", "link", Path.Combine(root, "target") }) start.ArgumentList.Add(argument);
                using var process = Process.Start(start)!;
                if (!process.WaitForExit(5000)) { process.Kill(entireProcessTree: true); Assert.Fail("Owned junction creation timed out."); }
                Assert.That(process.ExitCode, Is.Zero);
            }
            else Directory.CreateSymbolicLink(Path.Combine(root, "link"), Path.Combine(root, "target"));
            var publisher = new LiteralArtifactPublisher(); publisher.Append("##teamcity[publishArtifacts 'link/proof.txt']\n"u8);
            Assert.Throws<InvalidDataException>(() => publisher.Files(root));
        }
        finally
        {
            var link = Path.Combine(root, "link");
            if (Directory.Exists(link)) Directory.Delete(link); // Remove the link itself before recursive fixture cleanup.
            Directory.Delete(root, recursive: true);
        }
    }
}
