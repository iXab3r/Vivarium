using System.Text.RegularExpressions;
using Vivarium.Contracts.V1;

namespace Vivarium.Contracts;

/// <summary>The first anonymous Git checkout contract, shared by both sides of the Agent seam.</summary>
public static class VcsCheckoutProfile
{
    public const string CapabilityId = "teamcity.vcs-checkout.v1";

    public static void Validate(VcsCheckout value, bool requireRevision = true)
    {
        if (value.Url.Length > 2048 || value.Url.Any(char.IsControl)
            || !Uri.TryCreate(value.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http" or "git")
            || uri.Host.Length == 0 || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || value.Branch.Length > 256 || !value.Branch.StartsWith("refs/heads/", StringComparison.Ordinal)
            || value.Branch.EndsWith('/') || value.Branch.Contains("..", StringComparison.Ordinal)
            || value.Branch.Contains("@{", StringComparison.Ordinal)
            || value.Branch.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || "~^:?*[\\".Contains(c))
            || requireRevision && !Regex.IsMatch(value.Revision, "^[a-f0-9]{40}$", RegexOptions.CultureInvariant)
            || value.ExcludedDirectories.Count > 32 || value.ExcludedDirectories.Distinct(StringComparer.OrdinalIgnoreCase).Count() != value.ExcludedDirectories.Count
            || value.ExcludedDirectories.Any(d => d.Length is 0 or > 128 || d is "." or ".." || d.Equals(".git", StringComparison.OrdinalIgnoreCase)
                || d.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_'))))
            throw new InvalidDataException("The anonymous Git checkout profile is invalid or unavailable.");
    }

    public static void IsolateEnvironment(IDictionary<string, string?> environment)
    {
        foreach (var key in environment.Keys.Where(k => k.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)
            || k.EndsWith("_PROXY", StringComparison.OrdinalIgnoreCase) || k.Equals("ALL_PROXY", StringComparison.OrdinalIgnoreCase)).ToArray())
            environment.Remove(key);
        environment["GIT_CONFIG_NOSYSTEM"] = "1";
        environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        environment["GIT_TERMINAL_PROMPT"] = "0";
        environment["GCM_INTERACTIVE"] = "never";
        environment["GIT_ALLOW_PROTOCOL"] = "https:http:git";
    }

    public static string[] ConfigurationArguments => ["-c", "credential.helper=", "-c", "core.hooksPath=" + (OperatingSystem.IsWindows() ? "NUL" : "/dev/null"),
        "-c", "core.fsmonitor=false", "-c", "protocol.allow=never", "-c", "protocol.https.allow=always", "-c", "protocol.http.allow=always", "-c", "protocol.git.allow=always"];
}
