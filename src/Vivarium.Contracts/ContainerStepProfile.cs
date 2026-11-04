using System.Text.RegularExpressions;
using Vivarium.Contracts.V1;

namespace Vivarium.Contracts;

/// <summary>The initial immutable-image Linux process profile, shared by both ends of AgentHub.</summary>
public static class ContainerStepProfile
{
    public const string CapabilityId = "teamcity.container-runner.v1";

    public static void Validate(ContainerStep value)
    {
        if (value.Image.Length > 512 || !Regex.IsMatch(value.Image,
                "^[a-zA-Z0-9][a-zA-Z0-9._:/-]*@sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant)
            || value.Platform != "linux/amd64" || !Regex.IsMatch(value.User, "^[0-9]{1,10}(:[0-9]{1,10})?$", RegexOptions.CultureInvariant)
            || value.Caches.Count > 8)
            throw new InvalidDataException("The container process profile is invalid.");
        var targets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cache in value.Caches)
        {
            if (!Regex.IsMatch(cache.Name, "^[a-z0-9][a-z0-9-]{0,63}$", RegexOptions.CultureInvariant)
                || cache.Target.Length > 256 || !cache.Target.StartsWith('/')
                || cache.Target.Split('/').Skip(1).Any(p => p is "" or "." or "..")
                || cache.Target.Any(c => char.IsControl(c) || c is ':' or ',')
                || !targets.Add(cache.Target)
                || Overlaps(cache.Target, "/workspace") || Overlaps(cache.Target, "/vivarium-inputs")
                || Overlaps(cache.Target, "/proc") || Overlaps(cache.Target, "/sys") || Overlaps(cache.Target, "/dev"))
                throw new InvalidDataException("The container cache profile is invalid.");
        }
        foreach (var target in targets)
            if (targets.Any(other => target != other && Overlaps(target, other)))
                throw new InvalidDataException("Container cache targets overlap.");
    }

    private static bool Overlaps(string left, string right) => left == right
        || left.StartsWith(right + '/', StringComparison.Ordinal) || right.StartsWith(left + '/', StringComparison.Ordinal);
}
