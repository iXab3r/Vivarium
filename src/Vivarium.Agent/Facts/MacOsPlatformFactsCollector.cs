namespace Vivarium.Agent.Facts;

internal sealed class MacOsPlatformFactsCollector : PlatformFactsCollectorBase
{
    public MacOsPlatformFactsCollector(IPlatformFactSource source, TimeProvider? timeProvider = null)
        : base(source, timeProvider)
    {
        if (source.Family != PlatformFamily.MacOS)
        {
            throw new ArgumentException("The source is not a macOS fact source.", nameof(source));
        }
    }

    public override async ValueTask<PlatformFactSnapshot> CollectAsync(
        AgentPackageIdentity package,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        var observedAt = TimeProvider.GetUtcNow();
        var builder = CreateBuilder(
            "macos",
            out var osArchitecture,
            out var processArchitecture,
            out var hostname);

        var productName = AddReadValue(
            builder,
            "system.os.product.name",
            await Source.RunCommandAsync(
                "/usr/bin/sw_vers",
                ["-productName"],
                cancellationToken),
            required: true);
        var productVersion = AddReadValue(
            builder,
            "system.os.version",
            await Source.RunCommandAsync(
                "/usr/bin/sw_vers",
                ["-productVersion"],
                cancellationToken),
            required: true);
        var productBuild = AddReadValue(
            builder,
            "system.os.build",
            await Source.RunCommandAsync(
                "/usr/bin/sw_vers",
                ["-buildVersion"],
                cancellationToken),
            required: true);
        var kernelVersion = AddReadValue(
            builder,
            "system.os.kernel.version",
            await Source.RunCommandAsync(
                "/usr/bin/uname",
                ["-r"],
                cancellationToken),
            required: true);

        builder.AddValue("system.os.product.name", productName);
        builder.AddValue("system.os.version", productVersion);
        builder.AddValue("system.os.build", productBuild);
        builder.AddValue("system.os.kernel.version", kernelVersion);

        return builder.Build(
            "macos",
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
}
