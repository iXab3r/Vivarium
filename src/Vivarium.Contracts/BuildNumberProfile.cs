using System.Text;
using Vivarium.Contracts.V1;

namespace Vivarium.Contracts;

/// <summary>The selected literal stdout Build number profile, not the general service-message API.</summary>
public static class BuildNumberProfile
{
    public const string CapabilityId = "teamcity.build-number.literal.v1";
    public const int MaximumChanges = 128;
    public const int MaximumValueBytes = 4096;

    public static void Validate(BuildNumberChanged change, string workload)
    {
        if (change.BuildId != workload || change.Sequence is < 1 or > MaximumChanges || string.IsNullOrEmpty(change.Value)
            || change.Value.Contains('\0') || change.Value.Contains("{build.number}", StringComparison.Ordinal)
            || Encoding.UTF8.GetByteCount(change.Value) > MaximumValueBytes)
            throw new InvalidDataException("Build number metadata exceeds the selected literal profile.");
    }
}
