using Google.Protobuf;
using Vivarium.Cli.Configuration;
using Vivarium.Contracts.V1;

namespace Vivarium.Cli;

internal static class BuildRequestMapper
{
    public static SubmitBuildRequest Create(
        ResolvedVivariumRun run,
        ReadOnlySpan<byte> definitionSnapshot,
        IReadOnlyDictionary<string, PayloadArchiveInfo> archives,
        string requestId)
    {
        var request = new SubmitBuildRequest
        {
            RequestId = requestId,
            Project = run.Project,
            Configuration = run.Configuration,
            DefinitionSnapshot = ByteString.CopyFrom(definitionSnapshot),
        };

        foreach (var cell in run.Cells)
        {
            if (!archives.TryGetValue(cell.Payload.SourceDirectory, out var archive))
            {
                throw new InvalidOperationException(
                    $"payload archive was not created for matrix cell '{cell.Name}'");
            }

            var assignment = new BuildAssignment
            {
                OnFail = cell.OnFail switch
                {
                    VivariumOnFail.Keep => OnFail.KeepMachine,
                    _ => OnFail.None,
                },
            };
            assignment.Payload.Add(new Blob
            {
                Sha256 = archive.Sha256,
                FileName = "payload.zip",
                Archive = true,
                UnpackTo = string.Empty,
            });
            assignment.Collect.Add(cell.Collect);
            assignment.Parameters["cell"] = cell.Name;
            assignment.Parameters["rid"] = cell.RuntimeIdentifier ?? string.Empty;

            foreach (var step in cell.Steps)
            {
                var mapped = new Step
                {
                    Program = step.Program,
                    Cwd = step.WorkingDirectory,
                    TimeoutSec = step.Timeout is null ? 0 : checked((int)step.Timeout.Value.TotalSeconds),
                    Policy = step.Policy switch
                    {
                        VivariumStepPolicy.EvenIfFailed => StepPolicy.EvenIfFailed,
                        VivariumStepPolicy.Always => StepPolicy.Always,
                        _ => StepPolicy.Default,
                    },
                };
                mapped.Args.Add(step.Arguments);
                foreach (var (name, value) in step.Environment)
                {
                    mapped.Env.Add(name, value);
                }
                assignment.Steps.Add(mapped);
            }

            request.Cells.Add(new MatrixBuildCell
            {
                Name = cell.Name,
                AgentExpression = cell.AgentRequirement,
                Rid = cell.RuntimeIdentifier ?? string.Empty,
                Assignment = assignment,
                QueueTimeoutSec = cell.QueueTimeout is null
                    ? 0
                    : checked((int)cell.QueueTimeout.Value.TotalSeconds),
            });
        }

        return request;
    }
}
