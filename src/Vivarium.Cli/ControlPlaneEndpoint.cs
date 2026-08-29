using Vivarium.Cli.Rest;
using Vivarium.Contracts.V1;

namespace Vivarium.Cli;

internal sealed record AgentUpgradeEventSnapshot(
    long Sequence,
    string Phase,
    string Code,
    long? ConnectionGeneration,
    string? PackageSha256,
    DateTimeOffset CreatedAt);

internal sealed record AgentUpgradeSnapshot(
    string OperationId,
    string AgentId,
    string PackageVersion,
    string State,
    int RestartAttempts,
    long? LastDispatchConnectionGeneration,
    DateTimeOffset? NextRestartAt,
    string? CancellationReason,
    string? FailureCode,
    string? ResultPackageSha256,
    bool DrainHeld,
    DateTimeOffset Deadline,
    IReadOnlyList<AgentUpgradeEventSnapshot> Events)
{
    public bool IsTerminal => State is "succeeded" or "rolled-back" or "failed" or "cancelled";
}

internal interface IControlPlaneEndpoint : IAsyncDisposable
{
    Task ValidateAsync(CancellationToken cancellationToken);
    Task<string> StageBlobsAsync(
        string projectId,
        IReadOnlyCollection<PayloadArchiveInfo> archives,
        string idempotencyKey,
        CancellationToken cancellationToken);
    Task<BuildRef> SubmitBuildAsync(
        SubmitBuildRequest request,
        string blobStagingId,
        CancellationToken cancellationToken);
    Task<BuildSnapshot> CancelBuildAsync(
        string buildId,
        string reason,
        CancellationToken cancellationToken);
    IAsyncEnumerable<BuildSnapshot> WatchBuildAsync(string buildId, CancellationToken cancellationToken);
    Task<AgentUpgradeSnapshot> CreateAgentUpgradeAsync(
        string agentId,
        string reason,
        int? timeoutSeconds,
        string idempotencyKey,
        CancellationToken cancellationToken) =>
        Task.FromException<AgentUpgradeSnapshot>(new NotSupportedException());
    Task<AgentUpgradeSnapshot> GetAgentUpgradeAsync(
        string operationId,
        CancellationToken cancellationToken) =>
        Task.FromException<AgentUpgradeSnapshot>(new NotSupportedException());
    Task<AgentUpgradeSnapshot> CancelAgentUpgradeAsync(
        string operationId,
        string reason,
        CancellationToken cancellationToken) =>
        Task.FromException<AgentUpgradeSnapshot>(new NotSupportedException());
}

internal interface IControlPlaneEndpointFactory
{
    IControlPlaneEndpoint Create(EndpointSettings settings);
}

internal sealed class ControlPlaneEndpointFactory : IControlPlaneEndpointFactory
{
    public IControlPlaneEndpoint Create(EndpointSettings settings) => new ControlPlaneEndpoint(settings);
}

internal sealed class ControlPlaneEndpoint : IControlPlaneEndpoint
{
    private readonly IVivariumRestApiClient client;
    private readonly Dictionary<string, string> lastBuildEventIds = new(StringComparer.Ordinal);

    public ControlPlaneEndpoint(EndpointSettings settings)
    {
        client = new VivariumRestApiClient(settings);
    }

    public Task ValidateAsync(CancellationToken cancellationToken) =>
        client.ValidateAsync(cancellationToken);

    public async Task<string> StageBlobsAsync(
        string projectId,
        IReadOnlyCollection<PayloadArchiveInfo> archives,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var distinct = archives
            .GroupBy(archive => archive.Sha256, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToDictionary(archive => archive.Sha256, StringComparer.Ordinal);
        var plan = await client.CreateBlobUploadPlanAsync(
            new RestBlobUploadPlanRequest(
                projectId,
                distinct.Values
                    .OrderBy(archive => archive.Sha256, StringComparer.Ordinal)
                    .Select(archive => new RestBlobDescriptor(archive.Sha256, archive.Size))
                    .ToArray()),
            idempotencyKey,
            cancellationToken);
        foreach (var item in plan.Items.Where(item => item.UploadRequired))
        {
            if (!distinct.TryGetValue(item.Sha256, out var archive))
            {
                throw new InvalidDataException(
                    "controller requested an unknown payload archive during staging");
            }

            await client.UploadBlobAsync(
                item,
                plan.Id,
                archive.Path,
                cancellationToken);
        }

        return plan.Id;
    }

    public async Task<BuildRef> SubmitBuildAsync(
        SubmitBuildRequest request,
        string blobStagingId,
        CancellationToken cancellationToken)
    {
        var response = await client.SubmitBuildAsync(
            RestBuildRequestMapper.Create(request, blobStagingId),
            request.RequestId,
            cancellationToken);
        return new BuildRef { BuildId = response.Resource.Id };
    }

    public async Task<BuildSnapshot> CancelBuildAsync(
        string buildId,
        string reason,
        CancellationToken cancellationToken) => ToSnapshot((await client.CancelBuildAsync(
            buildId,
            reason,
            cancellationToken)).Resource);

    public async Task<AgentUpgradeSnapshot> CreateAgentUpgradeAsync(
        string agentId,
        string reason,
        int? timeoutSeconds,
        string idempotencyKey,
        CancellationToken cancellationToken) => ToUpgradeSnapshot(
            await client.CreateAgentUpgradeAsync(
                agentId,
                new RestAgentUpgradeRequest(reason, timeoutSeconds),
                idempotencyKey,
                cancellationToken));

    public async Task<AgentUpgradeSnapshot> GetAgentUpgradeAsync(
        string operationId,
        CancellationToken cancellationToken) => ToUpgradeSnapshot(
            await client.GetAgentUpgradeAsync(operationId, cancellationToken));

    public async Task<AgentUpgradeSnapshot> CancelAgentUpgradeAsync(
        string operationId,
        string reason,
        CancellationToken cancellationToken) => ToUpgradeSnapshot(
            await client.CancelAgentUpgradeAsync(operationId, reason, cancellationToken));

    public async IAsyncEnumerable<BuildSnapshot> WatchBuildAsync(
        string buildId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var current = ToSnapshot((await client.GetBuildAsync(buildId, cancellationToken)).Resource);
        yield return current;
        if (current.State == DurableBuildState.Finished)
        {
            yield break;
        }

        lastBuildEventIds.TryGetValue(buildId, out var lastEventId);
        await foreach (var update in client.WatchBuildAsync(
            buildId,
            lastEventId,
            cancellationToken))
        {
            lastBuildEventIds[buildId] = update.EventId;
            var snapshot = ToSnapshot(update.Build);
            yield return snapshot;
            if (snapshot.State == DurableBuildState.Finished)
            {
                yield break;
            }
        }
    }

    public ValueTask DisposeAsync() => client.DisposeAsync();

    private static BuildSnapshot ToSnapshot(RestBuildResource resource)
    {
        var snapshot = new BuildSnapshot
        {
            Build = new BuildRef { BuildId = resource.Id },
            Project = resource.Project,
            Configuration = resource.Configuration,
            State = ParseState(resource.State),
            Outcome = ParseOutcome(resource.Outcome),
            CreatedUnixMs = resource.CreatedAt.ToUnixTimeMilliseconds(),
            UpdatedUnixMs = resource.UpdatedAt.ToUnixTimeMilliseconds(),
        };
        foreach (var child in resource.Children)
        {
            var cell = new BuildCellSnapshot
            {
                Name = child.Name,
                BuildId = child.Id,
                AgentExpression = child.AgentExpression,
                State = ParseState(child.State),
                Outcome = ParseOutcome(child.Outcome),
                StatusText = child.StatusText ?? string.Empty,
                Rid = child.Rid,
                QueueDeadlineUnixMs = child.QueueDeadline?.ToUnixTimeMilliseconds() ?? 0,
            };
            if (child.AssignedAgent is not null)
            {
                cell.AgentId = child.AssignedAgent.Id;
                cell.AgentName = child.AssignedAgent.Name;
                foreach (var pair in child.AssignedAgent.ReportedParameters)
                {
                    cell.AgentParameters[pair.Key] = pair.Value;
                }

                foreach (var pair in child.AssignedAgent.CustomParameters)
                {
                    cell.AgentCustomParameters[pair.Key] = pair.Value;
                }
            }

            cell.Steps.Add(child.Steps.Select(step => new StepResult
            {
                StepIndex = step.Index,
                ExitCode = step.ExitCode,
                TimedOut = step.TimedOut,
                Skipped = step.Skipped,
            }));
            cell.Artifacts.Add(child.Artifacts.Select(artifact => new Artifact
            {
                Path = artifact.Path,
                Sha256 = artifact.Sha256,
                Size = artifact.Size,
            }));
            snapshot.Cells.Add(cell);
        }

        return snapshot;
    }

    private static AgentUpgradeSnapshot ToUpgradeSnapshot(
        RestAgentUpgradeOperationResource operation) => new(
        operation.OperationId,
        operation.AgentId,
        operation.Package.Version,
        operation.State,
        operation.RestartAttempts,
        operation.LastDispatchConnectionGeneration,
        operation.NextRestartAt,
        operation.CancellationReason,
        operation.FailureCode,
        operation.ResultPackageSha256,
        operation.DrainHeld,
        operation.Deadline,
        operation.Events.Select(value => new AgentUpgradeEventSnapshot(
            value.Sequence,
            value.Phase,
            value.Code,
            value.ConnectionGeneration,
            value.PackageSha256,
            value.CreatedAt)).ToArray());

    private static DurableBuildState ParseState(string value) => value switch
    {
        "queued" => DurableBuildState.Queued,
        "running" => DurableBuildState.Running,
        "cancel-requested" => DurableBuildState.CancelRequested,
        "finished" => DurableBuildState.Finished,
        _ => throw new InvalidDataException($"controller returned unknown build state '{value}'"),
    };

    private static BuildOutcome ParseOutcome(string? value) => value switch
    {
        null => BuildOutcome.Unspecified,
        "succeeded" => BuildOutcome.Succeeded,
        "failed" => BuildOutcome.Failed,
        "cancelled" => BuildOutcome.Cancelled,
        "infrastructure-failed" => BuildOutcome.InfrastructureFailed,
        _ => throw new InvalidDataException($"controller returned unknown build outcome '{value}'"),
    };
}
