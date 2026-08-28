using Vivarium.Cli.Configuration;

namespace Vivarium.Tests;

[TestFixture]
public sealed class VivariumDefinitionParserTests
{
    [Test]
    public void ParsesAndResolvesStructuredConfiguration()
    {
        var root = Path.Combine(TestContext.CurrentContext.WorkDirectory, "definition-root");
        var result = VivariumDefinitionParser.Parse(ValidYaml, root, "integration");

        Assert.Multiple(() =>
        {
            Assert.That(result.Project, Is.EqualTo("myapp"));
            Assert.That(result.Configuration, Is.EqualTo("integration"));
            Assert.That(result.Cells, Has.Count.EqualTo(2));
        });

        var windows = result.Cells[0];
        Assert.Multiple(() =>
        {
            Assert.That(windows.Name, Is.EqualTo("windows"));
            Assert.That(windows.AgentRequirement, Is.EqualTo("os.family == windows"));
            Assert.That(windows.RuntimeIdentifier, Is.EqualTo("win-x64"));
            Assert.That(windows.Payload.RelativeDirectory, Is.EqualTo("out/win-x64"));
            Assert.That(windows.Payload.SourceDirectory, Is.EqualTo(Path.Combine(root, "out", "win-x64")));
            Assert.That(windows.QueueTimeout, Is.EqualTo(TimeSpan.FromMinutes(30)));
            Assert.That(windows.OnFail, Is.EqualTo(VivariumOnFail.Keep));
            Assert.That(windows.Collect, Is.EqualTo(new[] { "results/**", "logs/**" }));
        });

        var step = windows.Steps.Single();
        Assert.Multiple(() =>
        {
            Assert.That(step.Program, Is.EqualTo("IntegrationTests.exe"));
            Assert.That(step.Arguments, Is.EqualTo(new[] { "--target", "windows-x64", "--results", "results" }));
            Assert.That(step.Environment["TARGET_RID"], Is.EqualTo("win-x64"));
            Assert.That(step.Environment["WORK"], Is.EqualTo("."));
            Assert.That(step.WorkingDirectory, Is.EqualTo("."));
            Assert.That(step.Timeout, Is.EqualTo(TimeSpan.FromMinutes(2)));
            Assert.That(step.Policy, Is.EqualTo(VivariumStepPolicy.EvenIfFailed));
        });
    }

    [Test]
    public void OnlySelectionUsesCellNamesAndKeepsMatrixOrder()
    {
        var result = VivariumDefinitionParser.Parse(
            ValidYaml,
            TestContext.CurrentContext.WorkDirectory,
            "integration",
            ["linux", "windows"]);

        Assert.That(result.Cells.Select(cell => cell.Name), Is.EqualTo(new[] { "windows", "linux" }));
    }

    [TestCase("win-x64", "windows", "x64", ".exe")]
    [TestCase("linux-x64", "linux", "x64", "")]
    [TestCase("linux-arm64", "linux", "arm64", "")]
    [TestCase("osx-arm64", "macos", "arm64", "")]
    public void ResolvesTheStrictSupportedRidTable(string rid, string os, string arch, string exe)
    {
        var yaml = $$"""
            project: p
            configurations:
              test:
                matrix:
                  cell: { agent: "os.family == {{os}}", rid: {{rid}} }
                payload: publish/{rid}/**
                steps:
                  - program: app{exe}
                    args: ["{os}", "{arch}", "{rid}", "{results}", "{workdir}"]
                clean: none
            """;

        var cell = VivariumDefinitionParser.Parse(
            yaml,
            TestContext.CurrentContext.WorkDirectory,
            "test").Cells.Single();

        Assert.Multiple(() =>
        {
            Assert.That(cell.Payload.RelativeDirectory, Is.EqualTo($"publish/{rid}"));
            Assert.That(cell.Steps[0].Program, Is.EqualTo($"app{exe}"));
            Assert.That(cell.Steps[0].Arguments, Is.EqualTo(new[] { os, arch, rid, "results", "." }));
        });
    }

    [Test]
    public void PayloadRidTemplateRequiresCellRid()
    {
        var yaml = MinimalYaml(payload: "out/{rid}/**");

        var exception = Assert.Throws<VivariumConfigurationException>(() =>
            VivariumDefinitionParser.Parse(yaml, TestContext.CurrentContext.WorkDirectory, "test"));

        Assert.That(exception!.Message, Does.StartWith("configurations.test.payload:"));
        Assert.That(exception.Message, Does.Contain("requires the matrix cell to declare 'rid'"));
    }

    [Test]
    public void RejectsDuplicateCellNamesWithTheirPath()
    {
        const string yaml = """
            project: p
            configurations:
              test:
                matrix:
                  windows: { agent: "os.family == windows", rid: win-x64 }
                  windows: { agent: "name == other", rid: win-x64 }
                payload: out
                steps:
                  - program: app.exe
            """;

        var exception = Assert.Throws<VivariumConfigurationException>(() =>
            VivariumDefinitionParser.Parse(yaml, TestContext.CurrentContext.WorkDirectory, "test"));

        Assert.That(exception!.Message, Is.EqualTo("configurations.test.matrix.windows: duplicate key"));
    }

    [TestCase("image: win-11", "configurations.test.image")]
    [TestCase("scenarios: {}", "configurations.test.scenarios")]
    [TestCase("axes: {}", "configurations.test.axes")]
    [TestCase("repeat: 3", "configurations.test.repeat")]
    [TestCase("surprise: true", "configurations.test.surprise")]
    public void RejectsDeferredAndUnknownConfigurationKeys(string line, string expectedPath)
    {
        var yaml = MinimalYaml(extra: line);

        var exception = Assert.Throws<VivariumConfigurationException>(() =>
            VivariumDefinitionParser.Parse(yaml, TestContext.CurrentContext.WorkDirectory, "test"));

        Assert.That(exception!.Message, Does.StartWith(expectedPath + ":"));
    }

    [TestCase("reboot")]
    [TestCase("pristine")]
    [TestCase("clean-workdir")]
    public void RejectsDeferredCleanPolicies(string clean)
    {
        var yaml = MinimalYaml(extra: $"clean: {clean}");

        var exception = Assert.Throws<VivariumConfigurationException>(() =>
            VivariumDefinitionParser.Parse(yaml, TestContext.CurrentContext.WorkDirectory, "test"));

        Assert.That(exception!.Message, Does.StartWith("configurations.test.clean:"));
    }

    [TestCase("0s")]
    [TestCase("1.5m")]
    [TestCase("30")]
    [TestCase("999999999999999999999d")]
    public void RejectsInvalidDurations(string duration)
    {
        var yaml = MinimalYaml(extra: $"queue_timeout: {duration}");

        var exception = Assert.Throws<VivariumConfigurationException>(() =>
            VivariumDefinitionParser.Parse(yaml, TestContext.CurrentContext.WorkDirectory, "test"));

        Assert.That(exception!.Message, Does.StartWith("configurations.test.queue_timeout:"));
    }

    [TestCase("../secrets")]
    [TestCase("out/../../secrets")]
    [TestCase("C:/secrets")]
    [TestCase("/var/secrets")]
    [TestCase(".")]
    [TestCase("out/*.dll")]
    public void RejectsUnsafeOrUnsupportedPayloadRoots(string payload)
    {
        var yaml = MinimalYaml(payload: payload);

        var exception = Assert.Throws<VivariumConfigurationException>(() =>
            VivariumDefinitionParser.Parse(yaml, TestContext.CurrentContext.WorkDirectory, "test"));

        Assert.That(exception!.Message, Does.StartWith("configurations.test.payload:"));
    }

    [Test]
    public void RejectsPayloadRootSymbolicLinkResolvingOutsideConfigurationRoot()
    {
        var sandbox = CreateSandbox();
        var configurationRoot = Path.Combine(sandbox, "repo");
        var outside = Path.Combine(sandbox, "outside");
        var payloadLink = Path.Combine(configurationRoot, "out");
        Directory.CreateDirectory(configurationRoot);
        Directory.CreateDirectory(outside);

        try
        {
            CreateDirectorySymbolicLinkOrIgnore(payloadLink, outside);

            var exception = Assert.Throws<VivariumConfigurationException>(() =>
                VivariumDefinitionParser.Parse(MinimalYaml(), configurationRoot, "test"));

            Assert.That(exception!.Message, Is.EqualTo(
                "configurations.test.payload: payload directory path must not contain symbolic links or reparse points"));
        }
        finally
        {
            DeleteSandbox(sandbox, payloadLink);
        }
    }

    [Test]
    public void RejectsExistingSymbolicLinkComponentInPayloadPath()
    {
        var sandbox = CreateSandbox();
        var configurationRoot = Path.Combine(sandbox, "repo");
        var outside = Path.Combine(sandbox, "outside");
        var linkedComponent = Path.Combine(configurationRoot, "linked");
        Directory.CreateDirectory(configurationRoot);
        Directory.CreateDirectory(Path.Combine(outside, "payload"));

        try
        {
            CreateDirectorySymbolicLinkOrIgnore(linkedComponent, outside);

            var exception = Assert.Throws<VivariumConfigurationException>(() =>
                VivariumDefinitionParser.Parse(
                    MinimalYaml(payload: "linked/payload"),
                    configurationRoot,
                    "test"));

            Assert.That(exception!.Message, Is.EqualTo(
                "configurations.test.payload: payload directory path must not contain symbolic links or reparse points"));
        }
        finally
        {
            DeleteSandbox(sandbox, linkedComponent);
        }
    }

    [Test]
    public void RejectsUnknownOnlyCell()
    {
        var exception = Assert.Throws<VivariumConfigurationException>(() =>
            VivariumDefinitionParser.Parse(
                ValidYaml,
                TestContext.CurrentContext.WorkDirectory,
                "integration",
                ["missing"]));

        Assert.That(exception!.Message, Is.EqualTo("--only: matrix cell 'missing' does not exist"));
    }

    [Test]
    public void RejectsUnknownTemplatesWithTheValuePath()
    {
        var yaml = MinimalYaml(argument: "{future}");

        var exception = Assert.Throws<VivariumConfigurationException>(() =>
            VivariumDefinitionParser.Parse(yaml, TestContext.CurrentContext.WorkDirectory, "test"));

        Assert.That(exception!.Message, Is.EqualTo("configurations.test.steps[0].args[0]: unknown template '{future}'"));
    }

    [TestCase("{param.suite}", "unknown template '{param.suite}'")]
    [TestCase("{future-name}", "unknown template '{future-name}'")]
    [TestCase("{RID}", "unknown template '{RID}'")]
    [TestCase("value{", "unmatched or malformed template braces")]
    [TestCase("value}", "unmatched or malformed template braces")]
    [TestCase("{{rid}}", "unmatched or malformed template braces")]
    public void RejectsEveryUnsupportedOrMalformedBraceTemplate(string argument, string expectedMessage)
    {
        var yaml = MinimalYaml(argument: argument);

        var exception = Assert.Throws<VivariumConfigurationException>(() =>
            VivariumDefinitionParser.Parse(yaml, TestContext.CurrentContext.WorkDirectory, "test"));

        Assert.That(exception!.Message, Is.EqualTo($"configurations.test.steps[0].args[0]: {expectedMessage}"));
    }

    [Test]
    public void RejectsProgramThatBecomesEmptyAfterTemplateExpansion()
    {
        const string yaml = """
            project: p
            configurations:
              test:
                matrix:
                  linux: { agent: "os.family == linux", rid: linux-x64 }
                payload: out
                steps:
                  - program: "{exe}"
            """;

        var exception = Assert.Throws<VivariumConfigurationException>(() =>
            VivariumDefinitionParser.Parse(yaml, TestContext.CurrentContext.WorkDirectory, "test"));

        Assert.That(exception!.Message, Is.EqualTo("configurations.test.steps[0].program: value must not be empty"));
    }

    private const string ValidYaml = """
        project: myapp
        configurations:
          integration:
            matrix:
              windows: { agent: "os.family == windows", rid: win-x64 }
              linux: { agent: "os.family == linux", rid: linux-arm64 }
            payload: out/{rid}/**
            steps:
              - program: IntegrationTests{exe}
                args: ["--target", "{os}-{arch}", "--results", "{results}"]
                env:
                  TARGET_RID: "{rid}"
                  WORK: "{workdir}"
                cwd: "{workdir}"
                timeout: 2m
                policy: even-if-failed
            collect: ["{results}/**", "logs/**"]
            queue_timeout: 30m
            clean: none
            on_fail: keep
        """;

    private static string MinimalYaml(string? extra = null, string payload = "out", string? argument = null)
    {
        var lines = new List<string>
        {
            "project: p",
            "configurations:",
            "  test:",
            "    matrix:",
            "      cell: { agent: \"os.family == windows\" }",
            $"    payload: '{payload.Replace("'", "''", StringComparison.Ordinal)}'",
            "    steps:",
            "      - program: app",
        };
        if (argument is not null)
        {
            lines.Add($"        args: [\"{argument}\"]");
        }

        if (extra is not null)
        {
            lines.AddRange(extra.Split('\n').Select(line => "    " + line.TrimEnd('\r')));
        }

        return string.Join('\n', lines);
    }

    private static string CreateSandbox() =>
        Path.Combine(Path.GetTempPath(), "vivarium-definition-tests", Guid.NewGuid().ToString("N"));

    private static void CreateDirectorySymbolicLinkOrIgnore(string path, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(path, target);
        }
        catch (PlatformNotSupportedException)
        {
            Assert.Ignore("Symbolic-link creation is not supported on this platform.");
        }
        catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows())
        {
            Assert.Ignore("Windows symbolic-link creation requires Developer Mode or the symbolic-link privilege.");
        }
        catch (IOException exception) when (
            OperatingSystem.IsWindows() && exception.HResult == unchecked((int)0x80070522))
        {
            Assert.Ignore("Windows symbolic-link creation requires Developer Mode or the symbolic-link privilege.");
        }
    }

    private static void DeleteSandbox(string sandbox, string link)
    {
        try
        {
            if (Directory.Exists(link))
            {
                Directory.Delete(link);
            }

            if (Directory.Exists(sandbox))
            {
                Directory.Delete(sandbox, recursive: true);
            }
        }
        catch
        {
            // Best effort: cleanup must not hide the parser assertion.
        }
    }
}
