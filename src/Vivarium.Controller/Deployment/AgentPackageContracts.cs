namespace Vivarium.Controller.Deployment;

public sealed record AgentPackage(
    string PackageId,
    string Version,
    string Rid,
    string Sha256,
    long Size,
    DateTimeOffset CreatedAt,
    string Source);

public sealed record AgentPackagePublication(
    AgentPackage Package,
    bool Replayed);

public sealed record AgentPackageCatalogDocument(
    int SchemaVersion,
    IReadOnlyList<AgentPackageCatalogEntry> Packages);

public sealed record AgentPackageCatalogEntry(
    string Version,
    string Rid,
    string File,
    string Sha256);

public sealed class AgentPackageException(
    string code,
    string message,
    int statusCode = StatusCodes.Status422UnprocessableEntity) : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}

public static class AgentPackageRids
{
    public static IReadOnlySet<string> Supported { get; } = new HashSet<string>(
        ["win-x64", "linux-x64", "linux-arm64", "osx-arm64"],
        StringComparer.Ordinal);

    public static string FromPlatform(string os, string architecture)
    {
        var family = os.Trim().ToLowerInvariant();
        var arch = architecture.Trim().ToLowerInvariant();
        if (arch is "amd64")
        {
            arch = "x64";
        }
        else if (arch is "aarch64")
        {
            arch = "arm64";
        }

        var rid = family switch
        {
            "windows" when arch == "x64" => "win-x64",
            "linux" when arch is "x64" or "arm64" => $"linux-{arch}",
            "macos" or "osx" when arch == "arm64" => "osx-arm64",
            _ => throw new AgentPackageException(
                "agent_package_rid_unsupported",
                $"Agent platform '{family}-{arch}' is not a supported package RID."),
        };
        return rid;
    }
}
