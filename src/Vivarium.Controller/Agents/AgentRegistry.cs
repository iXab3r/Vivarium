using System.Collections.Concurrent;
using System.Threading.Channels;
using Vivarium.Contracts.V1;

namespace Vivarium.Controller.Agents;

public enum AgentAuth
{
    Unauthorized,
    Authorized,
}

public enum AgentActivity
{
    Idle,
    Building,
    Upgrading,
}

public sealed record AgentSessionSnapshot(string AgentId, string SessionId, long ConnectionGeneration);

public sealed record AgentSessionLoss(string AgentId, string SessionId, string? CurrentBuildId);

public sealed class AgentConnectionHandle
{
    public required string AgentId { get; init; }
    public required string SessionId { get; init; }
    public required long ConnectionGeneration { get; init; }
    internal Channel<ControllerMsg> Outbox { get; init; } = null!;
}

public sealed record AgentSnapshot(
    string AgentId,
    string Name,
    bool Connected,
    bool Reconciled,
    AgentAuth Authorization,
    bool Enabled,
    AgentActivity Activity,
    string? CurrentBuildId,
    DateTimeOffset LastCommunication,
    long ConnectionGeneration,
    long ParameterGeneration,
    bool ParametersChanging,
    IReadOnlyDictionary<string, string> ReportedParameters,
    IReadOnlyDictionary<string, string> CustomParameters,
    IReadOnlyDictionary<string, string> Parameters,
    string AgentVersion,
    string OsFamily,
    string OsVersion,
    string Architecture,
    bool Interactive);

/// <summary>A known agent and, while connected, its live TeamCity-style state.</summary>
public sealed class ConnectedAgent
{
    internal object Gate { get; } = new();
    public required string AgentId { get; init; }
    public required Hello Hello { get; set; }
    public AgentAuth Auth { get; set; }
    public bool Enabled { get; set; }
    public bool Connected { get; set; }
    public bool Reconciled { get; set; }
    public AgentActivity Activity { get; set; } = AgentActivity.Idle;
    public string? CurrentBuildId { get; set; }
    public string SessionId { get; set; } = string.Empty;
    public long ConnectionGeneration { get; set; }
    public long ParameterGeneration { get; set; }
    public bool ParametersChanging { get; set; }
    public bool MaintenanceDrain { get; set; }
    public DateTimeOffset LastHeartbeat { get; set; }
    public CancellationTokenSource? SessionAbort { get; set; }
    public Channel<ControllerMsg> Outbox { get; set; } = NewOutbox();
    public TaskCompletionSource<long> SessionChanged { get; set; } = NewSessionSignal();

    internal static Channel<ControllerMsg> NewOutbox() => Channel.CreateBounded<ControllerMsg>(
        new BoundedChannelOptions(128)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });

    internal static TaskCompletionSource<long> NewSessionSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

public sealed class AgentRegistry
{
    private readonly ConcurrentDictionary<string, ConnectedAgent> agents = new();
    private readonly AgentStore? store;
    private readonly TimeProvider timeProvider;

    public AgentRegistry(AgentStore? store = null, TimeProvider? timeProvider = null)
    {
        this.store = store;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public event Action? Changed;

    public void NotifyChanged() => OnChanged();

    public IReadOnlyList<ConnectedAgent> All => agents.Values.ToArray();

    public ConnectedAgent? Get(string agentId) => agents.TryGetValue(agentId, out var agent) ? agent : null;

    public AgentConnectionHandle Register(
        Hello hello,
        AgentAuth auth,
        bool enabled,
        CancellationTokenSource sessionAbort) =>
        RegisterCore(hello, auth, enabled, maintenanceDrain: false, connectionGeneration: null, sessionAbort);

    public AgentConnectionHandle Register(
        Hello hello,
        AgentAuth auth,
        bool enabled,
        long connectionGeneration,
        CancellationTokenSource sessionAbort)
        => Register(hello, auth, enabled, maintenanceDrain: false, connectionGeneration, sessionAbort);

    public AgentConnectionHandle Register(
        Hello hello,
        AgentAuth auth,
        bool enabled,
        bool maintenanceDrain,
        long connectionGeneration,
        CancellationTokenSource sessionAbort)
    {
        if (connectionGeneration <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(connectionGeneration),
                "connection generation must be positive");
        }

        return RegisterCore(hello, auth, enabled, maintenanceDrain, connectionGeneration, sessionAbort);
    }

    private AgentConnectionHandle RegisterCore(
        Hello hello,
        AgentAuth auth,
        bool enabled,
        bool maintenanceDrain,
        long? connectionGeneration,
        CancellationTokenSource sessionAbort)
    {

        CancellationTokenSource? previousAbort;
        Channel<ControllerMsg> previousOutbox;
        TaskCompletionSource<long> previousSignal;
        AgentConnectionHandle connection;

        var agent = agents.GetOrAdd(hello.AgentId, _ => new ConnectedAgent
        {
            AgentId = hello.AgentId,
            Hello = hello,
            Auth = auth,
            Enabled = enabled,
        });

        lock (agent.Gate)
        {
            var acceptedGeneration = connectionGeneration ?? checked(agent.ConnectionGeneration + 1);
            if (acceptedGeneration <= agent.ConnectionGeneration)
            {
                throw new InvalidOperationException(
                    $"connection generation {acceptedGeneration} does not advance agent " +
                    $"'{hello.AgentId}' from {agent.ConnectionGeneration}");
            }

            previousAbort = agent.SessionAbort;
            previousOutbox = agent.Outbox;
            previousSignal = agent.SessionChanged;

            agent.Hello = hello;
            agent.Auth = auth;
            agent.Enabled = enabled;
            agent.MaintenanceDrain = maintenanceDrain;
            agent.Connected = true;
            agent.Reconciled = false;
            agent.SessionId = hello.SessionId;
            agent.ConnectionGeneration = acceptedGeneration;
            agent.ParameterGeneration++;
            agent.LastHeartbeat = timeProvider.GetUtcNow();
            agent.SessionAbort = sessionAbort;
            agent.Outbox = ConnectedAgent.NewOutbox();
            agent.SessionChanged = ConnectedAgent.NewSessionSignal();
            connection = new AgentConnectionHandle
            {
                AgentId = agent.AgentId,
                SessionId = agent.SessionId,
                ConnectionGeneration = agent.ConnectionGeneration,
                Outbox = agent.Outbox,
            };
        }

        previousOutbox.Writer.TryComplete();
        previousAbort?.Cancel();
        previousSignal.TrySetResult(agent.ConnectionGeneration);
        OnChanged();
        return connection;
    }

    public AgentSessionLoss? Disconnect(AgentConnectionHandle connection)
    {
        var agent = Get(connection.AgentId);
        if (agent == null)
        {
            return null;
        }

        AgentSessionLoss loss;
        lock (agent.Gate)
        {
            if (!agent.Connected ||
                agent.SessionId != connection.SessionId ||
                agent.ConnectionGeneration != connection.ConnectionGeneration)
            {
                return null; // expiry or a newer reconnect already owns the runtime record
            }

            loss = new AgentSessionLoss(agent.AgentId, agent.SessionId, agent.CurrentBuildId);
            agent.Connected = false;
            agent.Reconciled = false;
            agent.SessionAbort = null;
        }

        OnChanged();
        return loss;
    }

    public void Heartbeat(AgentConnectionHandle connection)
    {
        var agent = Get(connection.AgentId);
        if (agent == null)
        {
            return;
        }

        lock (agent.Gate)
        {
            if (agent.SessionId == connection.SessionId &&
                agent.ConnectionGeneration == connection.ConnectionGeneration)
            {
                agent.LastHeartbeat = timeProvider.GetUtcNow();
            }
        }
    }

    public bool IsCurrent(AgentConnectionHandle connection)
    {
        var agent = Get(connection.AgentId);
        if (agent == null)
        {
            return false;
        }

        lock (agent.Gate)
        {
            return agent.Connected &&
                agent.SessionId == connection.SessionId &&
                agent.ConnectionGeneration == connection.ConnectionGeneration;
        }
    }

    /// <summary>
    /// Publishes durable-build reconciliation for one immutable connection. Until this succeeds,
    /// the session is connected but cannot receive new work or satisfy provider readiness.
    /// </summary>
    public bool Reconcile(AgentConnectionHandle connection, string? currentBuildId)
    {
        var agent = Get(connection.AgentId);
        if (agent == null)
        {
            return false;
        }

        TaskCompletionSource<long> previousSignal;
        lock (agent.Gate)
        {
            if (!agent.Connected ||
                agent.SessionId != connection.SessionId ||
                agent.ConnectionGeneration != connection.ConnectionGeneration)
            {
                return false;
            }

            agent.CurrentBuildId = currentBuildId;
            agent.Activity = currentBuildId != null
                ? AgentActivity.Building
                : agent.MaintenanceDrain ? AgentActivity.Upgrading : AgentActivity.Idle;
            agent.Reconciled = true;
            previousSignal = agent.SessionChanged;
            agent.SessionChanged = ConnectedAgent.NewSessionSignal();
        }

        previousSignal.TrySetResult(connection.ConnectionGeneration);
        OnChanged();
        return true;
    }

    public void SetAuthorized(string agentId, bool authorized)
    {
        var agent = Get(agentId);
        if (agent != null)
        {
            lock (agent.Gate)
            {
                agent.Auth = authorized ? AgentAuth.Authorized : AgentAuth.Unauthorized;
            }
        }

        OnChanged();
    }

    internal void SetEnabled(string agentId, bool enabled)
    {
        var agent = Get(agentId);
        if (agent != null)
        {
            lock (agent.Gate)
            {
                agent.Enabled = enabled;
            }
        }

        OnChanged();
    }

    public void SetMaintenanceDrain(string agentId, bool drained)
    {
        var agent = Get(agentId);
        if (agent != null)
        {
            lock (agent.Gate)
            {
                agent.MaintenanceDrain = drained;
                if (agent.CurrentBuildId is null)
                {
                    agent.Activity = drained ? AgentActivity.Upgrading : AgentActivity.Idle;
                }
            }
        }

        OnChanged();
    }

    public bool TryBeginBuild(string agentId, string buildId, out string? reason)
    {
        return TryBeginBuild(agentId, buildId, out _, out reason);
    }

    /// <summary>
    /// Reserves capacity and returns the immutable session that won the reservation. Callers must
    /// use that handle for the wire send so a reconnect cannot redirect an assignment to a newer,
    /// unreconciled stream.
    /// </summary>
    public bool TryBeginBuild(
        string agentId,
        string buildId,
        out AgentConnectionHandle? connection,
        out string? reason)
    {
        return TryBeginBuildCore(
            agentId, buildId, expectedParameterGeneration: null, out connection, out reason);
    }

    public bool TryBeginBuild(
        string agentId,
        string buildId,
        long expectedParameterGeneration,
        out AgentConnectionHandle? connection,
        out string? reason)
    {
        return TryBeginBuildCore(
            agentId, buildId, expectedParameterGeneration, out connection, out reason);
    }

    private bool TryBeginBuildCore(
        string agentId,
        string buildId,
        long? expectedParameterGeneration,
        out AgentConnectionHandle? connection,
        out string? reason)
    {
        var agent = Get(agentId);
        if (agent == null)
        {
            connection = null;
            reason = $"unknown agent '{agentId}'";
            return false;
        }

        lock (agent.Gate)
        {
            if (expectedParameterGeneration != null &&
                agent.ParameterGeneration != expectedParameterGeneration)
            {
                connection = null;
                reason = $"agent '{agentId}' parameters changed while it was being matched";
                return false;
            }

            if (agent.ParametersChanging)
            {
                connection = null;
                reason = $"agent '{agentId}' parameters are being edited";
                return false;
            }

            if (!agent.Connected)
            {
                connection = null;
                reason = $"agent '{agentId}' is disconnected";
                return false;
            }

            if (!agent.Reconciled)
            {
                connection = null;
                reason = $"agent '{agentId}' is still reconciling its session";
                return false;
            }

            if (agent.Auth != AgentAuth.Authorized)
            {
                connection = null;
                reason = $"agent '{agentId}' is not authorized";
                return false;
            }

            if (!agent.Enabled)
            {
                connection = null;
                reason = $"agent '{agentId}' is disabled";
                return false;
            }

            if (agent.MaintenanceDrain)
            {
                connection = null;
                reason = $"agent '{agentId}' is drained for maintenance";
                return false;
            }

            if (agent.Activity != AgentActivity.Idle)
            {
                connection = null;
                reason = $"agent '{agentId}' is already building '{agent.CurrentBuildId}'";
                return false;
            }

            agent.Activity = AgentActivity.Building;
            agent.CurrentBuildId = buildId;
            connection = ConnectionFor(agent);
        }

        reason = null;
        OnChanged();
        return true;
    }

    /// <summary>Gets the exact current session only when it still owns the expected build.</summary>
    public bool TryGetBuildConnection(
        string agentId,
        string buildId,
        out AgentConnectionHandle? connection)
    {
        var agent = Get(agentId);
        if (agent == null)
        {
            connection = null;
            return false;
        }

        lock (agent.Gate)
        {
            if (!agent.Connected || !agent.Reconciled || agent.CurrentBuildId != buildId)
            {
                connection = null;
                return false;
            }

            connection = ConnectionFor(agent);
            return true;
        }
    }

    public void EndBuild(string agentId, string buildId)
    {
        var agent = Get(agentId);
        if (agent == null)
        {
            return;
        }

        lock (agent.Gate)
        {
            if (agent.CurrentBuildId != buildId)
            {
                return;
            }

            agent.CurrentBuildId = null;
            agent.Activity = agent.MaintenanceDrain ? AgentActivity.Upgrading : AgentActivity.Idle;
        }

        OnChanged();
    }

    public void EndBuild(AgentConnectionHandle connection, string buildId)
    {
        var agent = Get(connection.AgentId);
        if (agent == null)
        {
            return;
        }

        var changed = false;
        lock (agent.Gate)
        {
            if (!IsCurrentLocked(agent, connection) || agent.CurrentBuildId != buildId)
            {
                return;
            }

            agent.CurrentBuildId = null;
            agent.Activity = agent.MaintenanceDrain ? AgentActivity.Upgrading : AgentActivity.Idle;
            changed = true;
        }

        if (changed)
        {
            OnChanged();
        }
    }

    public bool TrySend(string agentId, ControllerMsg message)
    {
        var agent = Get(agentId);
        if (agent == null)
        {
            return false;
        }

        CancellationTokenSource? overflowAbort = null;
        lock (agent.Gate)
        {
            if (!agent.Connected)
            {
                return false;
            }
            if (agent.Outbox.Writer.TryWrite(message))
            {
                return true;
            }
            agent.Outbox.Writer.TryComplete();
            overflowAbort = agent.SessionAbort;
        }
        overflowAbort?.Cancel();
        OnChanged();
        return false;
    }

    public bool TrySend(AgentConnectionHandle connection, ControllerMsg message)
    {
        var agent = Get(connection.AgentId);
        if (agent == null)
        {
            return false;
        }

        CancellationTokenSource? overflowAbort = null;
        lock (agent.Gate)
        {
            if (!IsCurrentLocked(agent, connection))
            {
                return false;
            }
            if (connection.Outbox.Writer.TryWrite(message))
            {
                return true;
            }
            connection.Outbox.Writer.TryComplete();
            overflowAbort = agent.SessionAbort;
        }
        overflowAbort?.Cancel();
        OnChanged();
        return false;
    }

    public bool TryGetMaintenanceConnection(
        string agentId,
        out AgentConnectionHandle? connection,
        out Hello? hello,
        out string? reason)
    {
        var agent = Get(agentId);
        if (agent == null)
        {
            connection = null;
            hello = null;
            reason = "agent_unknown";
            return false;
        }

        lock (agent.Gate)
        {
            if (!agent.Connected)
            {
                connection = null;
                hello = null;
                reason = "agent_disconnected";
                return false;
            }

            if (!agent.Reconciled)
            {
                connection = null;
                hello = null;
                reason = "agent_reconciling";
                return false;
            }

            if (agent.Auth != AgentAuth.Authorized)
            {
                connection = null;
                hello = null;
                reason = "agent_unauthorized";
                return false;
            }

            if (agent.CurrentBuildId is not null)
            {
                connection = null;
                hello = agent.Hello.Clone();
                reason = "agent_build_active";
                return false;
            }

            connection = ConnectionFor(agent);
            hello = agent.Hello.Clone();
            reason = null;
            return true;
        }
    }

    public IReadOnlyList<AgentSessionLoss> ExpireStaleConnections(
        DateTimeOffset now,
        TimeSpan timeout)
    {
        var expired = new List<AgentSessionLoss>();
        foreach (var agent in agents.Values)
        {
            CancellationTokenSource? abort = null;
            lock (agent.Gate)
            {
                if (agent.Connected && now - agent.LastHeartbeat > timeout)
                {
                    expired.Add(new AgentSessionLoss(
                        agent.AgentId, agent.SessionId, agent.CurrentBuildId));
                    agent.Connected = false;
                    agent.Reconciled = false;
                    abort = agent.SessionAbort;
                    agent.SessionAbort = null;
                }
            }

            abort?.Cancel();
        }

        if (expired.Count > 0)
        {
            OnChanged();
        }

        return expired;
    }

    /// <summary>
    /// Provider seam for restore-own-checkpoint (D5): after recording the current generation and
    /// restoring the VM, wait for a newer, idle agent session before marking the machine READY.
    /// </summary>
    public async Task<AgentSessionSnapshot> WaitForFreshIdleSessionAsync(
        string agentId,
        long afterGeneration,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var agent = Get(agentId)
                ?? throw new InvalidOperationException($"unknown agent '{agentId}'");
            Task<long> changed;
            lock (agent.Gate)
            {
                if (agent.Connected &&
                    agent.Reconciled &&
                    agent.ConnectionGeneration > afterGeneration &&
                    agent.Activity == AgentActivity.Idle &&
                    agent.Hello.RunningBuildId.Length == 0)
                {
                    return new AgentSessionSnapshot(agent.AgentId, agent.SessionId, agent.ConnectionGeneration);
                }

                changed = agent.SessionChanged.Task;
            }

            await changed.WaitAsync(cancellationToken);
        }
    }

    public async Task<IReadOnlyList<AgentSnapshot>> GetSnapshotsAsync()
    {
        if (store == null)
        {
            return agents.Values.Select(ToRuntimeSnapshot).ToArray();
        }

        var persisted = await store.ListAsync();
        return persisted.Select(record =>
        {
            var live = Get(record.AgentId);
            if (live == null)
            {
                return new AgentSnapshot(
                    record.AgentId, record.Name, false, false,
                    record.Authorized ? AgentAuth.Authorized : AgentAuth.Unauthorized,
                    record.Enabled, AgentActivity.Idle, null, record.LastSeen, 0, 0, false,
                    record.ReportedParameters, record.CustomParameters, record.Parameters,
                    record.AgentVersion, record.OsFamily, record.OsVersion,
                    record.Architecture, record.Interactive);
            }

            lock (live.Gate)
            {
                return new AgentSnapshot(
                    record.AgentId, record.Name, live.Connected, live.Reconciled,
                    live.Auth, live.Enabled,
                    live.Activity, live.CurrentBuildId, live.LastHeartbeat, live.ConnectionGeneration,
                    live.ParameterGeneration, live.ParametersChanging,
                    record.ReportedParameters, record.CustomParameters,
                    record.Parameters, record.AgentVersion, record.OsFamily, record.OsVersion,
                    record.Architecture, record.Interactive);
            }
        }).ToArray();
    }

    public void Remove(string agentId)
    {
        if (!agents.TryRemove(agentId, out var agent))
        {
            OnChanged();
            return;
        }

        CancellationTokenSource? abort;
        lock (agent.Gate)
        {
            abort = agent.SessionAbort;
            agent.SessionAbort = null;
            agent.Connected = false;
            agent.Reconciled = false;
            agent.Outbox.Writer.TryComplete();
        }

        abort?.Cancel();
        OnChanged();
    }

    private static AgentSnapshot ToRuntimeSnapshot(ConnectedAgent agent)
    {
        lock (agent.Gate)
        {
            return new AgentSnapshot(
                agent.AgentId, agent.AgentId, agent.Connected, agent.Reconciled,
                agent.Auth, agent.Enabled,
                agent.Activity, agent.CurrentBuildId, agent.LastHeartbeat, agent.ConnectionGeneration,
                agent.ParameterGeneration, agent.ParametersChanging,
                AgentParameterMaps.Normalize(agent.Hello.Parameters),
                AgentParameterMaps.Normalize([]),
                AgentParameterMaps.Normalize(agent.Hello.Parameters),
                agent.Hello.AgentVersion, agent.Hello.Os?.Family ?? string.Empty,
                agent.Hello.Os?.Version ?? string.Empty, agent.Hello.Os?.Arch ?? string.Empty,
                agent.Hello.Interactive);
        }
    }

    private static AgentConnectionHandle ConnectionFor(ConnectedAgent agent) => new()
    {
        AgentId = agent.AgentId,
        SessionId = agent.SessionId,
        ConnectionGeneration = agent.ConnectionGeneration,
        Outbox = agent.Outbox,
    };

    private static bool IsCurrentLocked(ConnectedAgent agent, AgentConnectionHandle connection) =>
        agent.Connected &&
        agent.SessionId == connection.SessionId &&
        agent.ConnectionGeneration == connection.ConnectionGeneration &&
        ReferenceEquals(agent.Outbox, connection.Outbox);

    private void OnChanged() => Changed?.Invoke();
}
