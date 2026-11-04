using System.Diagnostics;
using Vivarium.Agent;
using Vivarium.Contracts;
using System.Text.Json;
using System.Security.Cryptography;

namespace Vivarium.Tests;

public sealed class LiteralArtifactPublisherTests
{
    [Test]
    public void Negotiated_rules_publish_the_exact_paths_and_bytes_captured_from_the_pinned_reference()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory,
            "TeamCity/Reference/artifact-rules-observations.json")));
        var reference = document.RootElement;
        foreach (var key in new[] { "complete", "ownedProjectRemoved", "ownedResourcesRemoved", "permissionsRestored" })
            Assert.That(reference.GetProperty(key).GetBoolean(), Is.True);
        var repository = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "Vivarium.slnx"))) repository = repository.Parent;
        Assert.That(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(repository!.FullName,
            "tools/teamcity-reference/artifact-rules-capture.mjs")))), Is.EqualTo(reference.GetProperty("sourceSha256").GetString()));
        var scenario = reference.GetProperty("cases")[0];
        var expected = scenario.GetProperty("artifacts").EnumerateObject().Single(p => p.Name.EndsWith("_Prepare", StringComparison.Ordinal)).Value;
        var sources = new Dictionary<string, string> { ["prepare/a.tar.gz"] = "ci-out/a.tar.gz",
            ["prepare/manifest.json"] = "ci-out/manifest.json", ["linux/file.zip"] = "ci-out/linux/file.zip",
            ["tree/deep/file.txt"] = "ci-out/nested/deep/file.txt", ["binary/large.bin"] = "ci-out/large.bin" };
        var directory = Path.Combine(Path.GetTempPath(), "vivarium-artifact-rules-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            foreach (var file in expected.EnumerateArray())
            {
                var name = file.GetProperty("path").GetString()!;
                var exchange = scenario.GetProperty("exchanges").EnumerateArray().Single(e => e.GetProperty("request").GetProperty("path").GetString()!
                    .EndsWith("/artifacts/content/" + name, StringComparison.Ordinal));
                var response = exchange.GetProperty("response"); var bytes = Convert.FromBase64String(response.GetProperty("bodyBase64").GetString()!);
                Assert.That(Convert.ToHexStringLower(SHA256.HashData(bytes)), Is.EqualTo(response.GetProperty("bodySha256").GetString()));
                var source = Path.Combine(directory, sources[name]); Directory.CreateDirectory(Path.GetDirectoryName(source)!);
                File.WriteAllBytes(source, bytes);
            }
            var publisher = new LiteralArtifactPublisher(allowRules: true);
            foreach (var rule in new[] { "ci-out/*.tar.gz => prepare", "ci-out/manifest.json => prepare", "ci-out/linux/* => linux",
                "ci-out/nested/** => tree", "ci-out/large.bin => binary", "ci-out/empty/*.txt => unused" })
                publisher.Append(System.Text.Encoding.UTF8.GetBytes("##teamcity[publishArtifacts '" + rule + "']\n"));
            publisher.FinishStep();
            var actual = publisher.PublishedFiles(directory);
            Assert.That(actual.Select(f => f.Path), Is.EquivalentTo(expected.EnumerateArray().Select(f => f.GetProperty("path").GetString())));
            foreach (var file in actual)
                Assert.That(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(directory, file.Source)))),
                    Is.EqualTo(expected.EnumerateArray().Single(f => f.GetProperty("path").GetString() == file.Path).GetProperty("sha256").GetString()));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

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
