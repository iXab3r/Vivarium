using Vivarium.Controller.Configuration.Git;

namespace Vivarium.Controller.Rest.Agents.Configuration;

public static class AgentConfigurationEtags
{
    private const string Prefix = "configuration:";

    public static string Create(ConfigurationRevision revision)
    {
        ArgumentNullException.ThrowIfNull(revision);
        return $"\"{Prefix}{revision.Canonical}\"";
    }

    public static bool TryParse(string value, out ConfigurationRevision? revision)
    {
        revision = null;
        if (string.IsNullOrWhiteSpace(value) ||
            value.StartsWith("W/", StringComparison.OrdinalIgnoreCase) ||
            value.Length < Prefix.Length + 4 ||
            value[0] != '"' || value[^1] != '"')
        {
            return false;
        }

        var unquoted = value[1..^1];
        if (!unquoted.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var canonical = unquoted[Prefix.Length..];
        var separator = canonical.IndexOf('@');
        if (separator <= 0 || separator != canonical.LastIndexOf('@'))
        {
            return false;
        }

        try
        {
            revision = new ConfigurationRevision(
                canonical[..separator],
                canonical[(separator + 1)..]);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
