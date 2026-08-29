using Cake.Frosting;
using System.Xml.Linq;

[TaskName("Default")]
[IsDependentOn(typeof(HelpTask))]
public sealed class DefaultTask : FrostingTask<BuildContext>;

[TaskName("Help")]
[TaskDescription("Lists the provider-neutral Vivarium build targets.")]
public sealed class HelpTask : FrostingTask<BuildContext>
{
    public override void Run(BuildContext context)
    {
        Console.WriteLine("Vivarium build targets:");
        Console.WriteLine("  Build                    Build Vivarium.slnx in Release configuration.");
        Console.WriteLine("  Test                     Run host-native tests and write deterministic TRX output.");
        Console.WriteLine("  CI                       Build and test the solution on the current host.");
        Console.WriteLine("  Compile                  Compile runnable binaries for --rid or the host RID.");
        Console.WriteLine("  CompileAll               Compile runnable binaries for every supported RID.");
        Console.WriteLine("  CompileSmoke             Run native product probes from one Compile output.");
        Console.WriteLine("  PayloadSmoke             Publish and run the NUnit payload for --rid or the host RID.");
        Console.WriteLine("  PayloadCrossMacPublish   Cross-publish the osx-arm64 payload for artifact transfer.");
        Console.WriteLine("  PayloadCrossMacRun       Run the transferred payload on a macOS host.");
        Console.WriteLine("  PayloadNextest           Archive and run the Rust payload with pinned cargo-nextest.");
        Console.WriteLine("  Release                  Package Compile outputs into deterministic release assets.");
        Console.WriteLine("  ReleaseVerify            Verify an existing release directory without rebuilding it.");
        Console.WriteLine("  ReleaseSmoke             Run controller/CLI/agent/bootstrap smokes from the final ZIP for --rid.");
        Console.WriteLine("  Publish                  Resume/create a GitHub draft, verify assets, then publish it.");
        Console.WriteLine("  Clean                    Remove only the repository out/ directory.");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  dotnet run --project build/Vivarium.Build.csproj -- --target Compile");
        Console.WriteLine("  dotnet run --project build/Vivarium.Build.csproj -- --target Compile --rid linux-arm64");
        Console.WriteLine("  dotnet run --project build/Vivarium.Build.csproj -- --target CompileAll");
        Console.WriteLine("  dotnet run --project build/Vivarium.Build.csproj -- --target Test");
    }
}

[TaskName("Clean")]
[TaskDescription("Removes only Vivarium's repository-local out directory.")]
public sealed class CleanTask : FrostingTask<BuildContext>
{
    public override void Run(BuildContext context)
    {
        if (!Directory.Exists(context.OutRoot)) return;
        var relative = Path.GetRelativePath(context.Root, context.OutRoot);
        if (!string.Equals(relative, "out", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Refusing to clean unexpected path: {context.OutRoot}");
        }

        Directory.Delete(context.OutRoot, recursive: true);
    }
}

[TaskName("Build")]
[TaskDescription("Builds the complete Vivarium solution.")]
public sealed class BuildTask : AsyncFrostingTask<BuildContext>
{
    public override async Task RunAsync(BuildContext context)
    {
        context.SetTeamCityBuildNumber();
        var arguments = new List<string>
        {
            "build",
            "Vivarium.slnx",
            "-c",
            context.BuildConfiguration,
            "--maxcpucount:1",
            "--nologo",
        };
        arguments.AddRange(context.ContinuousIntegrationProperties());
        await BuildProcess.RunAsync("dotnet", arguments, context.Root);
    }
}

[TaskName("Test")]
[TaskDescription("Runs the complete Vivarium test suite on the native host and writes TRX output.")]
[IsDependentOn(typeof(BuildTask))]
public sealed class TestTask : AsyncFrostingTask<BuildContext>
{
    public override async Task RunAsync(BuildContext context)
    {
        if (context.RequestedRid is not null &&
            !string.Equals(context.RequestedRid, context.HostRid, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Test can run only for the native host RID; requested {context.RequestedRid}, host {context.HostRid}.");
        }

        RecreateDirectory(context.TestResultsRoot);
        const string fileName = "vivarium-tests.trx";
        await BuildProcess.RunAsync(
            "dotnet",
            [
                "test",
                "Vivarium.slnx",
                "-c",
                context.BuildConfiguration,
                "--maxcpucount:1",
                "--no-build",
                "--nologo",
                "--logger",
                $"trx;LogFileName={fileName}",
                "--results-directory",
                context.TestResultsRoot,
            ],
            context.Root);

        VerifyTrx(Path.Combine(context.TestResultsRoot, fileName));
    }

    internal static void VerifyTrx(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Expected TRX result was not produced.", path);
        var results = XDocument.Load(path).Descendants()
            .Where(element => element.Name.LocalName == "UnitTestResult")
            .Select(element => element.Attribute("outcome")?.Value)
            .ToArray();
        if (results.Length == 0)
        {
            throw new InvalidDataException($"TRX file contains no test results: {path}");
        }

        var acceptedOutcomes = new HashSet<string>(StringComparer.Ordinal)
        {
            "Passed",
            "NotExecuted",
        };
        var failures = results.Where(result => result is null || !acceptedOutcomes.Contains(result)).ToArray();
        if (failures.Length != 0)
        {
            throw new InvalidDataException(
                $"TRX file contains {failures.Length} unsuccessful test results: {string.Join(", ", failures.Distinct())}");
        }
    }

    private static void RecreateDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        Directory.CreateDirectory(path);
    }
}

[TaskName("CI")]
[TaskDescription("Runs the normal host build and test gate.")]
[IsDependentOn(typeof(TestTask))]
public sealed class CiTask : FrostingTask<BuildContext>;
