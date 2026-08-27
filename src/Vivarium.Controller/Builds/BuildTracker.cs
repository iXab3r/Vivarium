using System.Collections.Concurrent;
using System.Text;
using Vivarium.Contracts.V1;
using Vivarium.Controller.Agents;

namespace Vivarium.Controller.Builds;

/// <summary>
/// Phase 0 build bookkeeping: dispatch an assignment to a connected agent, collect its log and
/// result. Result handling is idempotent per build id (D4); SQLite persistence comes later.
/// </summary>
public sealed class BuildTracker
{
    private sealed class PendingBuild
    {
        public required string AgentId { get; init; }
        public TaskCompletionSource<BuildResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public StringBuilder Log { get; } = new();
    }

    private readonly AgentRegistry registry;
    private readonly ConcurrentDictionary<string, PendingBuild> builds = new();
    private readonly ConcurrentDictionary<string, BuildResult> completed = new();

    public BuildTracker(AgentRegistry registry) => this.registry = registry;

    public Task<BuildResult> RunBuildAsync(string agentId, BuildAssignment assignment, CancellationToken ct)
    {
        var agent = registry.Get(agentId)
            ?? throw new InvalidOperationException($"unknown agent '{agentId}'");
        if (agent.Auth != AgentAuth.Authorized)
        {
            throw new InvalidOperationException($"agent '{agentId}' is not authorized");
        }

        var pending = new PendingBuild { AgentId = agentId };
        if (!builds.TryAdd(assignment.BuildId, pending))
        {
            throw new InvalidOperationException($"build '{assignment.BuildId}' already exists");
        }

        if (!registry.TrySend(agentId, new ControllerMsg { Build = assignment }))
        {
            throw new InvalidOperationException($"agent '{agentId}' is not connected");
        }

        return pending.Completion.Task.WaitAsync(ct);
    }

    public string GetLog(string buildId) =>
        builds.TryGetValue(buildId, out var b) ? b.Log.ToString() : string.Empty;

    public void OnLog(LogChunk chunk)
    {
        if (builds.TryGetValue(chunk.BuildId, out var build))
        {
            build.Log.Append(chunk.Data.ToStringUtf8());
        }
    }

    public void OnStatus(StepStatus status)
    {
        // Phase 0: statuses only feed the log; the panel view comes later.
        if (builds.TryGetValue(status.BuildId, out var build))
        {
            build.Log.AppendLine($"[{status.Phase} step={status.StepIndex}]");
        }
    }

    public void OnResult(BuildResult result)
    {
        if (!completed.TryAdd(result.BuildId, result))
        {
            return; // duplicate submission — idempotent (D4)
        }

        if (builds.TryGetValue(result.BuildId, out var build))
        {
            build.Completion.TrySetResult(result);
        }
    }
}
