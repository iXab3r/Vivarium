using System.Text.RegularExpressions;
using Vivarium.Contracts.V1;

namespace Vivarium.Controller.Builds.Runtime;

internal static class RuntimeContainerReceipt
{
    internal static void Validate(StepResult result, Step assignment)
    {
        if (assignment.Container is null || result.Skipped)
        {
            if (result.Container is not null) throw new InvalidDataException("Unexpected container evidence.");
            return;
        }
        var value = result.Container ?? throw new InvalidDataException("Container execution evidence is absent.");
        if (!Regex.IsMatch(value.EngineId, "^[a-zA-Z0-9:_-]{1,128}$")) throw new InvalidDataException("Container Engine identity is invalid.");
        if (value.FailureCode == "image_unavailable" && value.ContainerId.Length == 0 && value.ImageId.Length == 0
            && value.ExecId.Length == 0 && !value.HasProcessExitCode && !value.Removed && result.ExitCode == 1 && !result.TimedOut) return;
        if (value.FailureCode.Length != 0 || !value.Removed || !value.HasProcessExitCode || value.ProcessExitCode != result.ExitCode
            || !Regex.IsMatch(value.ContainerId, "^[a-f0-9]{64}$") || !Regex.IsMatch(value.ImageId, "^sha256:[a-f0-9]{64}$")
            || !Regex.IsMatch(value.ExecId, "^[a-f0-9]{64}$")) throw new InvalidDataException("Container process/removal evidence is invalid.");
    }
}
