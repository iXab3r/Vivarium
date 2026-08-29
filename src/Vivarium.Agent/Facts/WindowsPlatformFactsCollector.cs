namespace Vivarium.Agent.Facts;

internal sealed class WindowsPlatformFactsCollector : PlatformFactsCollectorBase
{
    private const string CurrentVersionKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

    public WindowsPlatformFactsCollector(IPlatformFactSource source, TimeProvider? timeProvider = null)
        : base(source, timeProvider)
    {
        if (source.Family != PlatformFamily.Windows)
        {
            throw new ArgumentException("The source is not a Windows fact source.", nameof(source));
        }
    }

    public override async ValueTask<PlatformFactSnapshot> CollectAsync(
        AgentPackageIdentity package,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        var observedAt = TimeProvider.GetUtcNow();
        var builder = CreateBuilder(
            "windows",
            out var osArchitecture,
            out var processArchitecture,
            out var hostname);

        var productName = await ReadRequiredAsync(builder, "ProductName", "system.os.product.name", cancellationToken);
        var edition = await ReadOptionalAsync(builder, "EditionID", "system.os.windows.edition", cancellationToken);
        var displayVersion = await ReadOptionalAsync(
            builder,
            "DisplayVersion",
            "system.os.windows.display_version",
            cancellationToken);
        var installationType = await ReadOptionalAsync(
            builder,
            "InstallationType",
            "system.os.windows.installation_type",
            cancellationToken);

        var major = await ReadOptionalAsync(
            builder,
            "CurrentMajorVersionNumber",
            "system.os.version",
            cancellationToken);
        var minor = await ReadOptionalAsync(
            builder,
            "CurrentMinorVersionNumber",
            "system.os.version",
            cancellationToken);
        if (major is null || minor is null)
        {
            var legacyVersion = await ReadOptionalAsync(
                builder,
                "CurrentVersion",
                "system.os.version",
                cancellationToken);
            var parts = legacyVersion?.Split('.', 3, StringSplitOptions.TrimEntries);
            major ??= parts is { Length: >= 1 } ? parts[0] : null;
            minor ??= parts is { Length: >= 2 } ? parts[1] : null;
        }

        var productVersion = major is null || minor is null ? null : $"{major}.{minor}";
        if (productVersion is null)
        {
            builder.AddMissingIssue("system.os.version");
        }

        var buildNumber = await ReadOptionalAsync(builder, "CurrentBuildNumber", "system.os.build", cancellationToken)
            ?? await ReadRequiredAsync(builder, "CurrentBuild", "system.os.build", cancellationToken);
        var updateBuildRevision = await ReadRequiredAsync(builder, "UBR", "system.os.build", cancellationToken);
        var productBuild = buildNumber is null
            ? null
            : updateBuildRevision is null
                ? buildNumber
                : $"{buildNumber}.{updateBuildRevision}";
        var kernelVersion = productVersion is not null && productBuild is not null
            ? $"{productVersion}.{productBuild}"
            : Source.OperatingSystemVersion.ToString();

        builder.AddValue("system.os.product.name", productName);
        builder.AddValue("system.os.version", productVersion);
        builder.AddValue("system.os.build", productBuild);
        builder.AddValue("system.os.kernel.version", kernelVersion);
        builder.AddValue("system.os.windows.edition", edition);
        builder.AddValue("system.os.windows.display_version", displayVersion);
        builder.AddValue("system.os.windows.installation_type", installationType);

        return builder.Build(
            "windows",
            productName,
            productVersion,
            productBuild,
            kernelVersion,
            osArchitecture,
            processArchitecture,
            hostname,
            package,
            observedAt,
            SupportedCapabilities);
    }

    private async ValueTask<string?> ReadRequiredAsync(
        PlatformFactSnapshotBuilder builder,
        string registryName,
        string field,
        CancellationToken cancellationToken)
    {
        var read = await Source.ReadWindowsRegistryValueAsync(
            CurrentVersionKey,
            registryName,
            cancellationToken);
        return AddReadValue(builder, field, read, required: true);
    }

    private async ValueTask<string?> ReadOptionalAsync(
        PlatformFactSnapshotBuilder builder,
        string registryName,
        string field,
        CancellationToken cancellationToken)
    {
        var read = await Source.ReadWindowsRegistryValueAsync(
            CurrentVersionKey,
            registryName,
            cancellationToken);
        return AddReadValue(
            builder,
            field,
            read,
            required: false,
            includeFailure: read.Issue?.Code != PlatformFactIssueCodes.NotFound);
    }
}
