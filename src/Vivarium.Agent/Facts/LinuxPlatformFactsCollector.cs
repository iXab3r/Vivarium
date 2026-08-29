namespace Vivarium.Agent.Facts;

internal sealed class LinuxPlatformFactsCollector : PlatformFactsCollectorBase
{
    private const string PrimaryOsReleasePath = "/etc/os-release";
    private const string FallbackOsReleasePath = "/usr/lib/os-release";

    public LinuxPlatformFactsCollector(IPlatformFactSource source, TimeProvider? timeProvider = null)
        : base(source, timeProvider)
    {
        if (source.Family != PlatformFamily.Linux)
        {
            throw new ArgumentException("The source is not a Linux fact source.", nameof(source));
        }
    }

    public override async ValueTask<PlatformFactSnapshot> CollectAsync(
        AgentPackageIdentity package,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        var observedAt = TimeProvider.GetUtcNow();
        var builder = CreateBuilder(
            "linux",
            out var osArchitecture,
            out var processArchitecture,
            out var hostname);

        var releaseRead = await Source.ReadTextFileAsync(PrimaryOsReleasePath, cancellationToken);
        if (releaseRead.Value is null && releaseRead.Issue?.Code == PlatformFactIssueCodes.NotFound)
        {
            releaseRead = await Source.ReadTextFileAsync(FallbackOsReleasePath, cancellationToken);
        }

        IReadOnlyDictionary<string, string> release = new Dictionary<string, string>();
        if (releaseRead.Value is not null)
        {
            release = OsReleaseParser.Parse(releaseRead.Value);
        }
        else if (releaseRead.Issue is not null)
        {
            builder.AddIssue(releaseRead.Issue with { Field = "system.os.version" });
        }

        var distributionId = Value(release, "ID");
        var productVersion = Value(release, "VERSION_ID");
        var prettyName = Value(release, "PRETTY_NAME");
        var productName = prettyName ?? Value(release, "NAME") ?? distributionId;
        var variantId = Value(release, "VARIANT_ID");

        Require(builder, distributionId, "system.os.linux.distribution_id");
        Require(builder, productName, "system.os.product.name");
        Require(builder, productVersion, "system.os.version");

        var kernelRead = await Source.RunCommandAsync(
            "/usr/bin/uname",
            ["-r"],
            cancellationToken);
        var kernelVersion = AddReadValue(
            builder,
            "system.os.kernel.version",
            kernelRead,
            required: true);

        builder.AddValue("system.os.product.name", productName);
        builder.AddValue("system.os.version", productVersion);
        builder.AddValue("system.os.kernel.version", kernelVersion);
        builder.AddValue("system.os.linux.distribution_id", distributionId);
        builder.AddValue("system.os.linux.pretty_name", prettyName);
        builder.AddValue("system.os.linux.variant_id", variantId);

        return builder.Build(
            "linux",
            productName,
            productVersion,
            productBuild: null,
            kernelVersion,
            osArchitecture,
            processArchitecture,
            hostname,
            package,
            observedAt,
            SupportedCapabilities);
    }

    private static string? Value(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;

    private static void Require(
        PlatformFactSnapshotBuilder builder,
        string? value,
        string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            builder.AddMissingIssue(field);
        }
    }
}

internal static class OsReleaseParser
{
    public static IReadOnlyDictionary<string, string> Parse(string content)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            if (key.Any(static c => !(char.IsAsciiLetterOrDigit(c) || c == '_')))
            {
                continue;
            }

            var value = line[(separator + 1)..].Trim();
            result[key] = Unquote(value);
        }

        return result;
    }

    private static string Unquote(string value)
    {
        if (value.Length < 2 ||
            (value[0] != '"' || value[^1] != '"') &&
            (value[0] != '\'' || value[^1] != '\''))
        {
            return value;
        }

        var quote = value[0];
        var body = value[1..^1];
        if (quote == '\'')
        {
            return body;
        }

        return body
            .Replace("\\\"", "\"", StringComparison.Ordinal)
            .Replace("\\\\", "\\", StringComparison.Ordinal)
            .Replace("\\$", "$", StringComparison.Ordinal)
            .Replace("\\`", "`", StringComparison.Ordinal);
    }
}
