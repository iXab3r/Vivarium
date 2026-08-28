using Grpc.Core;
using Vivarium.Contracts.V1;
using Vivarium.Controller.Agents;
using Vivarium.Controller.Blobs;
using Vivarium.Controller.Security;

namespace Vivarium.Controller.Management;

public sealed class ControlPlaneService : Vivarium.Contracts.V1.ControlPlane.ControlPlaneBase
{
    private static readonly TimeSpan WatchFallbackInterval = TimeSpan.FromSeconds(1);

    private readonly MatrixBuildSubmissionService submissions;
    private readonly MatrixBuildStore matrixBuilds;
    private readonly MatrixBuildCancellationService cancellations;
    private readonly DatabaseChangeNotifier changes;
    private readonly BlobStore blobs;
    private readonly AgentAdministration agents;
    private readonly ControlPlaneAuthorizer authorizer;

    public ControlPlaneService(
        MatrixBuildSubmissionService submissions,
        MatrixBuildStore matrixBuilds,
        MatrixBuildCancellationService cancellations,
        DatabaseChangeNotifier changes,
        BlobStore blobs,
        AgentAdministration agents,
        TokenStore tokens)
    {
        this.submissions = submissions;
        this.matrixBuilds = matrixBuilds;
        this.cancellations = cancellations;
        this.changes = changes;
        this.blobs = blobs;
        this.agents = agents;
        authorizer = new ControlPlaneAuthorizer(tokens);
    }

    public override async Task<BuildRef> SubmitBuild(
        SubmitBuildRequest request,
        ServerCallContext context)
    {
        await authorizer.DemandAsync(BearerScope.Submit, context);
        try
        {
            return await submissions.SubmitAsync(request);
        }
        catch (MatrixBuildValidationException exception)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, exception.Message));
        }
        catch (MatrixRequestConflictException exception)
        {
            throw new RpcException(new Status(StatusCode.AlreadyExists, exception.Message));
        }
    }

    public override async Task WatchBuild(
        BuildRef request,
        IServerStreamWriter<BuildSnapshot> responseStream,
        ServerCallContext context)
    {
        await authorizer.DemandAsync(BearerScope.Submit, context);
        if (string.IsNullOrWhiteSpace(request.BuildId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "build_id is required"));
        }

        BuildSnapshot? previous = null;
        while (true)
        {
            var observedVersion = changes.Version;
            var snapshot = await matrixBuilds.GetSnapshotAsync(request.BuildId)
                ?? throw new RpcException(new Status(
                    StatusCode.NotFound, $"unknown matrix build '{request.BuildId}'"));
            if (!snapshot.Equals(previous))
            {
                await responseStream.WriteAsync(snapshot);
                previous = snapshot;
            }

            if (snapshot.State == DurableBuildState.Finished)
            {
                return;
            }

            await changes.WaitForChangeAsync(
                observedVersion, WatchFallbackInterval, context.CancellationToken);
        }
    }

    public override async Task<BuildSnapshot> CancelBuild(
        CancelBuildRequest request,
        ServerCallContext context)
    {
        await authorizer.DemandAsync(BearerScope.Submit, context);
        if (string.IsNullOrWhiteSpace(request.BuildId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "build_id is required"));
        }

        return await cancellations.CancelAsync(request.BuildId, request.Reason)
            ?? throw new RpcException(new Status(
                StatusCode.NotFound, $"unknown matrix build '{request.BuildId}'"));
    }

    public override async Task<BlobHashes> MissingBlobs(
        BlobHashes request,
        ServerCallContext context)
    {
        await authorizer.DemandAsync(BearerScope.Submit, context);
        var result = new BlobHashes();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var hash in request.Sha256)
        {
            if (!BlobStore.IsSha256(hash) || hash.Any(character => character is >= 'A' and <= 'F'))
            {
                throw new RpcException(new Status(
                    StatusCode.InvalidArgument, $"malformed sha256 '{hash}'"));
            }

            if (seen.Add(hash) && !blobs.Contains(hash))
            {
                result.Sha256.Add(hash);
            }
        }

        return result;
    }

    public override async Task<AgentList> ListAgents(
        ListAgentsRequest request,
        ServerCallContext context)
    {
        await authorizer.DemandAsync(BearerScope.Admin, context);
        var result = new AgentList();
        foreach (var snapshot in await agents.ListAsync())
        {
            result.Agents.Add(ToInfo(snapshot));
        }

        return result;
    }

    public override async Task<AgentInfo> AuthorizeAgent(
        AgentRef request,
        ServerCallContext context)
    {
        await authorizer.DemandAsync(BearerScope.Admin, context);
        if (string.IsNullOrWhiteSpace(request.AgentId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "agent_id is required"));
        }

        try
        {
            await agents.AuthorizeAsync(request.AgentId);
        }
        catch (InvalidOperationException)
        {
            throw new RpcException(new Status(
                StatusCode.NotFound, $"unknown agent '{request.AgentId}'"));
        }

        var snapshot = (await agents.ListAsync())
            .FirstOrDefault(agent => agent.AgentId == request.AgentId)
            ?? throw new RpcException(new Status(
                StatusCode.NotFound, $"unknown agent '{request.AgentId}'"));
        return ToInfo(snapshot);
    }

    private static AgentInfo ToInfo(AgentSnapshot snapshot)
    {
        var info = new AgentInfo
        {
            AgentId = snapshot.AgentId,
            Name = snapshot.Name,
            Connected = snapshot.Connected,
            Reconciled = snapshot.Reconciled,
            Authorized = snapshot.Authorization == AgentAuth.Authorized,
            Enabled = snapshot.Enabled,
            Activity = snapshot.Activity.ToString().ToUpperInvariant(),
            CurrentBuildId = snapshot.CurrentBuildId ?? string.Empty,
            LastCommunicationUnixMs = snapshot.LastCommunication.ToUnixTimeMilliseconds(),
            AgentVersion = snapshot.AgentVersion,
            OsFamily = snapshot.OsFamily,
            OsVersion = snapshot.OsVersion,
            Architecture = snapshot.Architecture,
            Interactive = snapshot.Interactive,
        };
        info.Parameters.Add(snapshot.Parameters.ToDictionary());
        return info;
    }
}
