using Vivarium.Contracts.V1;

namespace Vivarium.Controller.Builds;

internal static class BuildAdmission
{
    public static void EnsureSupported(BuildAssignment assignment)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        if (assignment.Steps.Any(step => step.ExpectedReboot))
        {
            throw new NotSupportedException(
                "provisioning reboot/resume (expected_reboot) is not implemented in Phase 1");
        }
    }
}
