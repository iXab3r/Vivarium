using Vivarium.Contracts.V1;

namespace Vivarium.Agent;

/// <summary>
/// One monotonic Build stop signal. Graceful may be escalated to force, but a duplicate request can
/// never weaken the mode, replace the first reason, or extend an already accepted deadline (D31).
/// </summary>
internal sealed class BuildStopController : IDisposable
{
    private static readonly TimeSpan LegacyGrace = TimeSpan.FromSeconds(10);
    private readonly object gate = new();
    private readonly CancellationTokenSource graceful = new();
    private readonly CancellationTokenSource force = new();
    private readonly CancellationTokenRegistration shutdownRegistration;
    private BuildStopMode mode;
    private string? reason;
    private DateTimeOffset? gracefulDeadline;
    private DateTimeOffset? forceDeadline;

    public BuildStopController(CancellationToken shutdownToken = default)
    {
        shutdownRegistration = shutdownToken.Register(static state =>
            ((BuildStopController)state!).Request(
                BuildStopMode.Force,
                "Agent process is stopping",
                DateTimeOffset.UtcNow.AddSeconds(10)), this);
    }

    public CancellationToken GracefulToken => graceful.Token;
    public CancellationToken ForceToken => force.Token;

    public BuildStopMode Mode
    {
        get
        {
            lock (gate)
            {
                return mode;
            }
        }
    }

    public string? Reason
    {
        get
        {
            lock (gate)
            {
                return reason;
            }
        }
    }

    public DateTimeOffset Deadline
    {
        get
        {
            lock (gate)
            {
                return (mode == BuildStopMode.Force ? forceDeadline : gracefulDeadline) ??
                    DateTimeOffset.UtcNow + LegacyGrace;
            }
        }
    }

    public BuildStopMode Request(
        BuildStopMode requestedMode,
        string? requestedReason,
        DateTimeOffset? requestedDeadline)
    {
        requestedMode = requestedMode == BuildStopMode.Unspecified
            ? BuildStopMode.Force
            : requestedMode;
        var cancelGraceful = false;
        var cancelForce = false;
        lock (gate)
        {
            if (reason is null && !string.IsNullOrWhiteSpace(requestedReason))
            {
                reason = requestedReason.Trim();
            }

            var candidateDeadline = requestedDeadline ?? DateTimeOffset.UtcNow + LegacyGrace;
            if (requestedMode == BuildStopMode.Force)
            {
                forceDeadline = forceDeadline is null || candidateDeadline < forceDeadline
                    ? candidateDeadline
                    : forceDeadline;
                if (mode != BuildStopMode.Force)
                {
                    mode = BuildStopMode.Force;
                    cancelGraceful = true;
                    cancelForce = true;
                }
            }
            else
            {
                gracefulDeadline = gracefulDeadline is null || candidateDeadline < gracefulDeadline
                    ? candidateDeadline
                    : gracefulDeadline;
                if (mode == BuildStopMode.Unspecified)
                {
                    mode = BuildStopMode.Graceful;
                    cancelGraceful = true;
                }
            }
        }

        if (cancelGraceful)
        {
            graceful.Cancel();
        }
        if (cancelForce)
        {
            force.Cancel();
        }
        return Mode;
    }

    public void Dispose()
    {
        shutdownRegistration.Dispose();
        graceful.Dispose();
        force.Dispose();
    }
}

internal sealed class WorkloadTerminationException(string message, Exception? inner = null)
    : Exception(message, inner);
