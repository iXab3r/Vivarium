using Cake.Core;
using Cake.Frosting;
using System.Globalization;
using System.Runtime.InteropServices;

public sealed class BuildContext : FrostingContext
{
    public BuildContext(ICakeContext context)
        : base(context)
    {
        Root = FindRepositoryRoot();
        BuildConfiguration = context.Arguments.GetArgument("configuration") ?? "Release";
        RequestedRid = context.Arguments.GetArgument("rid")?.Trim().ToLowerInvariant();
        SourceSha = context.Arguments.GetArgument("source-sha")?.Trim().ToLowerInvariant();
        BuildCounter = context.Arguments.GetArgument("build-counter")?.Trim();
        VersionOverride = NormalizeVersion(context.Arguments.GetArgument("build-version"));
        GitHubRepository = context.Arguments.GetArgument("github-repository")?.Trim();
        PayloadDirectory = ResolvePath(
            context.Arguments.GetArgument("payload-directory") ?? Path.Combine("out", "payload-cross-macos"));
    }

    public string Root { get; }

    public string BuildConfiguration { get; }

    public string? RequestedRid { get; }

    public string? SourceSha { get; }

    public string? BuildCounter { get; }

    public string? VersionOverride { get; }

    public string? GitHubRepository { get; }

    public string PayloadDirectory { get; }

    public string OutRoot => Path.Combine(Root, "out");

    public string ProductVersion
    {
        get
        {
            if (VersionOverride is not null)
            {
                return RequireSemanticVersion(VersionOverride, "--build-version must be SemVer.");
            }

            if (string.IsNullOrWhiteSpace(BuildCounter))
            {
                return ReadVersionBase() + ".0";
            }

            if (!BuildCounter.All(char.IsAsciiDigit))
            {
                throw new InvalidOperationException("--build-counter must contain only decimal digits.");
            }

            return ReadVersionBase() + "." + BuildCounter;
        }
    }

    public string TestResultsRoot => Path.Combine(OutRoot, "test-results", HostId);

    public string HostRid => DetectHostRid();

    public string TargetRid => RequestedRid ?? HostRid;

    public string HostId => OperatingSystem.IsWindows()
        ? "windows"
        : OperatingSystem.IsMacOS()
            ? "macos"
            : OperatingSystem.IsLinux()
                ? "linux"
                : "unknown";

    public string ResolvePath(string path) => Path.GetFullPath(
        Path.IsPathRooted(path) ? path : Path.Combine(Root, path));

    public IReadOnlyList<string> ContinuousIntegrationProperties()
    {
        var properties = new List<string> { "-p:ContinuousIntegrationBuild=true" };
        if (!string.IsNullOrWhiteSpace(SourceSha))
        {
            properties.Add($"-p:RepositoryCommit={SourceSha}");
            properties.Add($"-p:SourceRevisionId={SourceSha}");
        }

        return properties;
    }

    public string BuildNumber()
    {
        return ProductVersion;
    }

    public string RequireSourceSha()
    {
        if (string.IsNullOrWhiteSpace(SourceSha) || SourceSha.Length != 40 ||
            !SourceSha.All(Uri.IsHexDigit))
        {
            throw new InvalidOperationException("Release targets require --source-sha with the full 40-character commit SHA.");
        }

        return SourceSha;
    }

    public string RequireGitHubRepository()
    {
        if (string.IsNullOrWhiteSpace(GitHubRepository) ||
            !System.Text.RegularExpressions.Regex.IsMatch(
                GitHubRepository,
                @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant))
        {
            throw new InvalidOperationException(
                "Publish requires --github-repository <owner/repository>.");
        }

        return GitHubRepository;
    }

    public void SetTeamCityBuildNumber()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TEAMCITY_VERSION")))
        {
            return;
        }

        Console.WriteLine($"##teamcity[buildNumber '{EscapeTeamCity(BuildNumber())}']");
    }

    private string ReadVersionBase()
    {
        var props = File.ReadAllText(Path.Combine(Root, "Directory.Build.props"));
        const string start = "<VivariumVersionBase>";
        const string end = "</VivariumVersionBase>";
        var startIndex = props.IndexOf(start, StringComparison.Ordinal);
        var endIndex = props.IndexOf(end, StringComparison.Ordinal);
        if (startIndex < 0 || endIndex <= startIndex)
        {
            throw new InvalidDataException("Directory.Build.props does not contain VivariumVersionBase.");
        }

        var versionBase = props[(startIndex + start.Length)..endIndex].Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(
                versionBase,
                @"^[0-9]+\.[0-9]+$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant))
        {
            throw new InvalidDataException("VivariumVersionBase must contain exactly major.minor.");
        }

        return versionBase;
    }

    private static string EscapeTeamCity(string value) => value
        .Replace("|", "||", StringComparison.Ordinal)
        .Replace("'", "|'", StringComparison.Ordinal)
        .Replace("\n", "|n", StringComparison.Ordinal)
        .Replace("\r", "|r", StringComparison.Ordinal)
        .Replace("[", "|[", StringComparison.Ordinal)
        .Replace("]", "|]", StringComparison.Ordinal);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(System.Environment.CurrentDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Vivarium.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the Vivarium repository root.");
    }

    private static string DetectHostRid()
    {
        var architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            var other => throw new PlatformNotSupportedException(
                $"Unsupported host architecture: {other.ToString().ToLower(CultureInfo.InvariantCulture)}"),
        };

        if (OperatingSystem.IsWindows()) return "win-" + architecture;
        if (OperatingSystem.IsLinux()) return "linux-" + architecture;
        if (OperatingSystem.IsMacOS()) return "osx-" + architecture;
        throw new PlatformNotSupportedException("Unsupported build host operating system.");
    }

    private static string? NormalizeVersion(string? value)
    {
        var normalized = value?.Trim();
        const string tagPrefix = "refs/tags/";
        if (normalized?.StartsWith(tagPrefix, StringComparison.Ordinal) == true)
        {
            normalized = normalized[tagPrefix.Length..];
        }
        if (normalized?.StartsWith('v') == true)
        {
            normalized = normalized[1..];
        }
        return normalized;
    }

    private static string RequireSemanticVersion(string version, string error)
    {
        if (!IsSemanticVersion(version))
        {
            throw new InvalidOperationException(error);
        }

        return version;
    }

    private static bool IsSemanticVersion(string version) =>
        System.Text.RegularExpressions.Regex.IsMatch(
            version,
            @"^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
}

internal static class BuildDirectory
{
    public static void Recreate(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        Directory.CreateDirectory(path);
    }
}
