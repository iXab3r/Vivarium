using System.Collections.ObjectModel;
using Vivarium.Contracts.V1;

namespace Vivarium.Cli.Rest;

internal static class RestBuildRequestMapper
{
    public static RestBuildSubmissionRequest Create(
        SubmitBuildRequest request,
        string blobStagingId)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(blobStagingId);
        if (request.Cells.Any(cell => !string.IsNullOrWhiteSpace(cell.Assignment?.BuildId)))
        {
            throw new InvalidOperationException(
                "REST build submission cannot contain controller-owned child build IDs");
        }

        return new RestBuildSubmissionRequest(
            request.Project,
            request.Configuration,
            request.DefinitionSnapshot.ToByteArray(),
            blobStagingId,
            request.Cells.Select(MapCell).ToArray());
    }

    private static RestBuildCellRequest MapCell(MatrixBuildCell cell)
    {
        var assignment = cell.Assignment
            ?? throw new InvalidOperationException("REST build cell has no assignment");
        return new RestBuildCellRequest(
            cell.Name,
            cell.AgentExpression,
            cell.Rid,
            cell.QueueTimeoutSec,
            new RestBuildAssignmentRequest(
                assignment.Payload.Select(payload => new RestBuildPayloadRequest(
                    payload.Sha256,
                    payload.FileName,
                    payload.Archive,
                    payload.UnpackTo)).ToArray(),
                assignment.Steps.Select(step => new RestBuildStepRequest(
                    step.Program,
                    step.Args.ToArray(),
                    Ordered(step.Env),
                    step.Cwd == "." ? string.Empty : step.Cwd,
                    step.TimeoutSec,
                    StepPolicyValue(step.Policy),
                    step.ExpectedReboot)).ToArray(),
                assignment.Collect.ToArray(),
                OnFailValue(assignment.OnFail),
                Ordered(assignment.Parameters)));
    }

    private static IReadOnlyDictionary<string, string> Ordered(
        IEnumerable<KeyValuePair<string, string>> values) =>
        new ReadOnlyDictionary<string, string>(values
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));

    private static string StepPolicyValue(StepPolicy policy) => policy switch
    {
        StepPolicy.Default => "default",
        StepPolicy.EvenIfFailed => "even-if-failed",
        StepPolicy.Always => "always",
        _ => throw new InvalidOperationException($"unsupported REST build step policy {policy}"),
    };

    private static string OnFailValue(OnFail onFail) => onFail switch
    {
        OnFail.None => "none",
        OnFail.KeepMachine => "keep-machine",
        OnFail.SnapshotMachine => "snapshot-machine",
        _ => throw new InvalidOperationException($"unsupported REST build on-fail policy {onFail}"),
    };
}
