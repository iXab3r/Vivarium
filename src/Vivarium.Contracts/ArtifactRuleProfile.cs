namespace Vivarium.Contracts;

/// <summary>The recipe's independently captured literal, leaf wildcard and recursive directory rules.</summary>
public sealed record ArtifactRuleProfile(string Pattern, string? Destination)
{
    public const string CapabilityId = "teamcity.artifact-publisher.rules.v1";

    public static ArtifactRuleProfile Parse(string text)
    {
        var parts = text.Split("=>", StringSplitOptions.None);
        if (parts.Length is < 1 or > 2) throw new InvalidDataException("An artifact rule is invalid.");
        var pattern = (parts.Length == 1 ? parts[0] : parts[0].Trim()).Replace('\\', '/');
        LiteralArtifactPath.Validate(pattern.Replace("*", "x", StringComparison.Ordinal));
        var leaf = pattern.Split('/')[^1];
        if (pattern.Contains('*') && (pattern[..(pattern.Length - leaf.Length)].Contains('*')
            || leaf != "**" && (leaf[0] != '*' || leaf[1..].Contains('*'))))
            throw new InvalidDataException("The selected wildcard profile is not implemented.");
        string? destination = null;
        if (parts.Length == 2)
        {
            destination = parts[1].Trim().Replace('\\', '/');
            LiteralArtifactPath.Validate(destination);
            // The captured target is a directory, not TeamCity's archive-creation profile.
            if (destination.Split('/')[^1].Contains('.')) throw new InvalidDataException("Archive artifact targets are not implemented.");
        }
        return new(pattern, destination);
    }

    public string? Map(string path, bool ignoreCase)
    {
        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var separator = Pattern.LastIndexOf('/');
        var directory = separator < 0 ? "" : Pattern[..(separator + 1)];
        var leaf = Pattern[(separator + 1)..];
        string tail;
        if (!Pattern.Contains('*'))
        {
            if (!string.Equals(path, Pattern, comparison)) return null;
            tail = path[(path.LastIndexOf('/') + 1)..];
        }
        else
        {
            if (!path.StartsWith(directory, comparison)) return null;
            tail = path[directory.Length..];
            if (tail.Length == 0 || leaf != "**" && (tail.Contains('/') || !tail.EndsWith(leaf[1..], comparison))) return null;
        }
        LiteralArtifactPath.Validate(path);
        var mapped = Destination is null ? path : Destination + '/' + tail;
        LiteralArtifactPath.Validate(mapped);
        return mapped;
    }
}
