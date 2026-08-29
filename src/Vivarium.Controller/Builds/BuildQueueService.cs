using System.Threading.Channels;
using Vivarium.Contracts.V1;
using Vivarium.Controller.Agents;
using Vivarium.Controller.Auditing;
using Vivarium.Controller.Scheduling;
using Vivarium.Controller.Security;

namespace Vivarium.Controller.Builds;

/// <summary>Submission and operator-facing Build Queue operations.</summary>
public sealed class BuildQueueService
{
    private readonly BuildQueueStore store;
    private readonly AgentRegistry agents;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan defaultQueueWaitTimeout;
    private readonly ManagementCommandAuthorizer? authorization;
    private readonly Channel<bool> wakeups = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
        SingleWriter = false,
    });

    public BuildQueueService(
        BuildQueueStore store,
        AgentRegistry agents,
        TimeProvider? timeProvider = null,
        TimeSpan? defaultQueueWaitTimeout = null,
        ManagementCommandAuthorizer? authorization = null)
    {
        this.store = store;
        this.agents = agents;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.defaultQueueWaitTimeout =
            defaultQueueWaitTimeout ?? ControllerOptions.DefaultBuildQueueWaitTimeout;
        this.authorization = authorization;
        if (this.defaultQueueWaitTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(defaultQueueWaitTimeout), "queue wait timeout must be positive");
        }
    }

    public event Action? Changed;

    internal async Task<BuildQueueItem> EnqueueFromControllerAsync(
        BuildAssignment assignment,
        string agentExpression)
    {
        BuildAdmission.EnsureSupported(assignment);
        if (!AgentCompatibilityMatcher.TryParse(agentExpression, out _, out var parseError))
        {
            throw new ArgumentException(parseError, nameof(agentExpression));
        }

        var knownAgents = await agents.GetSnapshotsAsync();
        var compatible = knownAgents.Where(agent =>
            AgentCompatibilityMatcher.Match(
                agentExpression, agent.Name, agent.Parameters).Compatible).ToArray();
        if (compatible.Length == 0)
        {
            throw new InvalidOperationException(
                $"no known agent is compatible with '{agentExpression}'");
        }

        var item = await store.EnqueueAsync(
            assignment,
            agentExpression,
            timeProvider.GetUtcNow(),
            defaultQueueWaitTimeout);
        NotifyChanged();
        return item;
    }

    public Task<IReadOnlyList<BuildQueueItem>> ListPendingAsync() => store.ListPendingAsync();

    public Task<BuildQueueItem?> GetAsync(string buildId) => store.GetAsync(buildId);

    public async Task<bool> RemoveAsync(
        ManagementRequestContext context,
        string buildId,
        string reason)
    {
        ArgumentNullException.ThrowIfNull(context);
        await (authorization ?? throw new InvalidOperationException(
                "application command authorization is not configured"))
            .DemandAsync(
                context,
                ManagementPermission.BuildCancel,
                "build.cancel",
                "build",
                buildId);
        var removed = await store.TryRemoveAsync(
            buildId,
            reason,
            AuditEventDraft.Create(
                context,
                timeProvider.GetUtcNow(),
                "build.cancel",
                "build",
                buildId));
        if (removed)
        {
            NotifyChanged();
        }

        return removed;
    }

    internal ValueTask<bool> WaitForWorkAsync(CancellationToken cancellationToken) =>
        wakeups.Reader.ReadAsync(cancellationToken);

    internal void NotifyChanged()
    {
        wakeups.Writer.TryWrite(true);
        Changed?.Invoke();
    }
}
