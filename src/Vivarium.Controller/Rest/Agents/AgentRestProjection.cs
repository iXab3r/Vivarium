using Vivarium.Controller.Agents;
using Vivarium.Controller.Management;

namespace Vivarium.Controller.Rest.Agents;

internal sealed record AgentReadQuery(
    AgentStoreQuery StoreQuery,
    bool? Connected,
    bool? Reconciled,
    IReadOnlySet<AgentActivity>? Activities,
    AgentStoreCursor? After,
    int Limit);

internal sealed record AgentReadPageProjection(
    IReadOnlyList<AgentResource> Items,
    AgentStoreCursor? NextCursor);

internal sealed class AgentRestProjection(
    AgentStore store,
    AgentOperationalStore operationalStore,
    AgentRegistry registry,
    MatrixBuildStore matrixBuilds,
    ControllerOptions options,
    TimeProvider timeProvider)
{
    private const int CandidatePageSize = 200;

    public async Task<AgentReadPageProjection> ListAgentsAsync(AgentReadQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var items = new List<(AgentResource Resource, AgentStoreCursor Cursor)>(query.Limit);
        var scanCursor = query.After;

        while (true)
        {
            var page = await store.QueryPageAsync(query.StoreQuery, scanCursor, CandidatePageSize);
            foreach (var candidate in page.Items)
            {
                var resource = await ToResourceAsync(candidate.Projection);
                if (!MatchesRuntimeFilters(resource, query))
                {
                    continue;
                }

                if (items.Count == query.Limit)
                {
                    return new AgentReadPageProjection(
                        items.Select(item => item.Resource).ToArray(),
                        items[^1].Cursor);
                }

                items.Add((resource, candidate.Cursor));
            }

            if (page.NextCursor is null)
            {
                return new AgentReadPageProjection(
                    items.Select(item => item.Resource).ToArray(),
                    NextCursor: null);
            }

            scanCursor = page.NextCursor;
        }
    }

    public async Task<AgentResource?> GetAgentAsync(string agentId)
    {
        var agent = await store.GetProjectionAsync(agentId);
        return agent is null ? null : await ToResourceAsync(agent);
    }

    public async Task<AgentFactsResource?> GetAgentFactsAsync(string agentId)
    {
        var stored = await store.GetProjectionAsync(agentId);
        if (stored is null)
        {
            return null;
        }

        var runtime = await RuntimeForAsync(stored.Agent);
        return ToFactsResource(stored, runtime);
    }

    private async Task<AgentResource> ToResourceAsync(StoredAgentProjection projection)
    {
        var stored = projection.Agent;
        var runtime = await RuntimeForAsync(stored);
        var now = timeProvider.GetUtcNow();
        var age = now > runtime.LastCommunication
            ? now - runtime.LastCommunication
            : TimeSpan.Zero;
        var freshness = runtime.Connected && age <= options.AgentHeartbeatTimeout
            ? "current"
            : "stale";
        AgentCurrentBuildResource? currentBuild = null;
        if (!string.IsNullOrWhiteSpace(runtime.CurrentBuildId))
        {
            var matrixBuildId = await matrixBuilds.FindMatrixBuildIdForChildAsync(runtime.CurrentBuildId);
            currentBuild = new AgentCurrentBuildResource(
                runtime.CurrentBuildId,
                matrixBuildId,
                matrixBuildId is null
                    ? null
                    : $"/api/v1/builds/{Uri.EscapeDataString(matrixBuildId)}");
        }
        var facts = projection.Observation?.Facts;
        var hostname = NullIfEmpty(facts?.Hostname) ?? FirstParameter(
            stored.ReportedParameters,
            "system.hostname",
            "hostname");
        var factFreshness = FactFreshness(projection, runtime);
        var observationRevision = $"observation:{projection.Observation?.Revision ?? 0}";
        var runtimeRevision = $"runtime:{runtime.ConnectionGeneration}:{runtime.ParameterGeneration}:" +
            $"{(runtime.Connected ? 1 : 0)}:{(runtime.Reconciled ? 1 : 0)}:" +
            $"{HealthValue(runtime.OperationalHealth)}:{(runtime.Quarantined ? 1 : 0)}:" +
            $"{runtime.OperationalReason}:" +
            $"{(runtime.Authorized ? 1 : 0)}:{(runtime.Enabled ? 1 : 0)}:" +
            $"{ActivityValue(runtime.Activity)}:{runtime.CurrentBuildId}:{freshness}:" +
            runtime.LastCommunication.ToUnixTimeMilliseconds();

        return new AgentResource(
            stored.AgentId,
            $"/api/v1/agents/{Uri.EscapeDataString(stored.AgentId)}",
            stored.Name,
            hostname,
            new AgentStatusResource(
                runtime.Connected,
                runtime.Reconciled,
                HealthValue(runtime.OperationalHealth),
                runtime.Quarantined,
                runtime.OperationalReason,
                runtime.Authorized,
                runtime.Enabled,
                ActivityValue(runtime.Activity)),
            currentBuild,
            stored.FirstSeen,
            runtime.LastCommunication,
            freshness,
            new AgentSoftwareResource(
                NullIfEmpty(facts?.AgentVersion) ?? stored.AgentVersion,
                NullIfEmpty(facts?.PackageVersion),
                projection.Observation?.PackageDigestSha256,
                NullIfEmpty(facts?.CollectorVersion)),
            new AgentOperatingSystemResource(
                NullIfEmpty(facts?.OsFamily) ?? stored.OsFamily,
                NullIfEmpty(facts?.ProductVersion) ?? stored.OsVersion,
                NullIfEmpty(facts?.OsArchitecture) ?? stored.Architecture,
                NullIfEmpty(facts?.ProductName),
                NullIfEmpty(facts?.OsBuild),
                NullIfEmpty(facts?.KernelVersion),
                NullIfEmpty(facts?.ProcessArchitecture)),
            facts?.Interactive ?? stored.Interactive,
            new AgentParametersResource(
                AgentRestDictionaries.Ordered(stored.ReportedParameters),
                AgentRestDictionaries.Ordered(stored.CustomParameters),
                AgentRestDictionaries.Ordered(stored.Parameters)),
            $"/api/v1/agents/{Uri.EscapeDataString(stored.AgentId)}/facts",
            new AgentFactObservationSummaryResource(
                projection.Observation?.Revision ?? 0,
                projection.Observation?.Quality ?? "unknown",
                factFreshness,
                projection.Observation?.ObservedAt,
                projection.Observation?.ReceivedAt,
                projection.Observation?.Issues.Count ?? 0,
                ObservationGenerations(projection.Observation)),
            Capabilities(projection.Capabilities),
            new AgentGenerationResource(
                stored.CredentialGeneration,
                stored.ConnectionGeneration),
            observationRevision,
            runtimeRevision);
    }

    private AgentFactsResource ToFactsResource(
        StoredAgentProjection projection,
        RuntimeProjection runtime)
    {
        var stored = projection.Agent;
        var observation = projection.Observation;
        if (observation is null)
        {
            return new AgentFactsResource(
                stored.AgentId,
                $"/api/v1/agents/{Uri.EscapeDataString(stored.AgentId)}",
                ObservationRevision: 0,
                Quality: "unknown",
                CollectorOutcome: null,
                Complete: false,
                Freshness: "unknown",
                ObservedAt: null,
                ReceivedAt: null,
                new AgentOperatingSystemResource("", "", "", null, null, null, null),
                Hostname: null,
                new AgentSoftwareResource("", null, null, null),
                Interactive: null,
                Capabilities: Capabilities(projection.Capabilities),
                Issues: [],
                ExtensionFacts: AgentRestDictionaries.Ordered([]),
                new AgentGenerationResource(
                    stored.CredentialGeneration,
                    stored.ConnectionGeneration),
                ObservationGenerations: null);
        }

        return new AgentFactsResource(
            stored.AgentId,
            $"/api/v1/agents/{Uri.EscapeDataString(stored.AgentId)}",
            observation.Revision,
            observation.Quality,
            OutcomeValue(observation.CollectorOutcome),
            observation.Complete,
            FactFreshness(projection, runtime),
            observation.ObservedAt,
            observation.ReceivedAt,
            new AgentOperatingSystemResource(
                observation.Facts.OsFamily,
                observation.Facts.ProductVersion,
                observation.Facts.OsArchitecture,
                NullIfEmpty(observation.Facts.ProductName),
                NullIfEmpty(observation.Facts.OsBuild),
                NullIfEmpty(observation.Facts.KernelVersion),
                NullIfEmpty(observation.Facts.ProcessArchitecture)),
            NullIfEmpty(observation.Facts.Hostname),
            new AgentSoftwareResource(
                observation.Facts.AgentVersion,
                NullIfEmpty(observation.Facts.PackageVersion),
                observation.PackageDigestSha256,
                NullIfEmpty(observation.Facts.CollectorVersion)),
            observation.Facts.Interactive,
            Capabilities(projection.Capabilities),
            observation.Issues.Select(issue => new AgentFactIssueResource(
                    issue.Code,
                    issue.Field,
                    NullIfEmpty(issue.NativeCode)))
                .ToArray(),
            AgentRestDictionaries.Ordered(observation.Facts.Extensions),
            new AgentGenerationResource(
                stored.CredentialGeneration,
                stored.ConnectionGeneration),
            ObservationGenerations(observation));
    }

    private string FactFreshness(StoredAgentProjection projection, RuntimeProjection runtime)
    {
        var observation = projection.Observation;
        if (observation is null)
        {
            return "unknown";
        }

        var sameConnection =
            observation.ConnectionGeneration == projection.Agent.ConnectionGeneration;
        var initialAuthorizationOnSameSession =
            sameConnection &&
            observation.CredentialGeneration == 0 &&
            projection.Agent.CredentialGeneration == 1 &&
            projection.Agent.Authorized;
        if (!sameConnection ||
            (observation.CredentialGeneration != projection.Agent.CredentialGeneration &&
             !initialAuthorizationOnSameSession))
        {
            return "superseded";
        }

        var now = timeProvider.GetUtcNow();
        var age = now > observation.ReceivedAt
            ? now - observation.ReceivedAt
            : TimeSpan.Zero;
        return runtime.Connected && runtime.ConnectionGeneration == observation.ConnectionGeneration &&
            age <= options.AgentHeartbeatTimeout
            ? "current"
            : "stale";
    }

    private static IReadOnlyList<AgentCapabilityResource> Capabilities(
        IReadOnlyList<AgentCapabilitySupport> capabilities) => capabilities
            .Select(capability => new AgentCapabilityResource(
                capability.CapabilityId,
                capability.ContractMajor))
            .ToArray();

    private static AgentGenerationResource? ObservationGenerations(
        StoredAgentObservation? observation) => observation is null
        ? null
        : new AgentGenerationResource(
            observation.CredentialGeneration,
            observation.ConnectionGeneration);

    private static string OutcomeValue(AgentFactCollectorOutcome outcome) => outcome switch
    {
        AgentFactCollectorOutcome.Succeeded => "succeeded",
        AgentFactCollectorOutcome.Partial => "partial",
        AgentFactCollectorOutcome.Degraded => "degraded",
        AgentFactCollectorOutcome.PermissionDenied => "permission_denied",
        AgentFactCollectorOutcome.TemporarilyUnavailable => "temporarily_unavailable",
        AgentFactCollectorOutcome.Failed => "failed",
        _ => "unknown",
    };

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrEmpty(value) ? null : value;

    private async Task<RuntimeProjection> RuntimeForAsync(StoredAgent stored)
    {
        var live = registry.Get(stored.AgentId);
        if (live is null)
        {
            var operational = await operationalStore.GetAsync(stored.AgentId);
            return new RuntimeProjection(
                Connected: false,
                Reconciled: false,
                operational?.Health ?? AgentOperationalHealth.Unknown,
                operational?.Quarantined ?? false,
                operational?.Reason ?? "disconnected",
                stored.Authorized,
                stored.Enabled,
                AgentActivity.Idle,
                CurrentBuildId: null,
                stored.LastSeen,
                ConnectionGeneration: stored.ConnectionGeneration,
                ParameterGeneration: 0);
        }

        lock (live.Gate)
        {
            return new RuntimeProjection(
                live.Connected,
                live.Reconciled,
                live.OperationalHealth,
                live.Quarantined,
                live.OperationalReason,
                live.Auth == AgentAuth.Authorized,
                live.Enabled,
                live.Activity,
                live.CurrentBuildId,
                live.LastHeartbeat,
                live.ConnectionGeneration,
                live.ParameterGeneration);
        }
    }

    private static bool MatchesRuntimeFilters(AgentResource resource, AgentReadQuery query)
    {
        if (query.Connected is { } connected && resource.Status.Connected != connected)
        {
            return false;
        }

        if (query.Reconciled is { } reconciled && resource.Status.Reconciled != reconciled)
        {
            return false;
        }

        return query.Activities is null || query.Activities.Contains(ParseActivity(resource.Status.Activity));
    }

    private static string? FirstParameter(
        IReadOnlyDictionary<string, string> parameters,
        params string[] keys)
    {
        foreach (var key in keys)
        {
            if (parameters.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    internal static string ActivityValue(AgentActivity activity) => activity switch
    {
        AgentActivity.Idle => "idle",
        AgentActivity.Building => "building",
        AgentActivity.Upgrading => "upgrading",
        _ => "unknown",
    };

    internal static AgentActivity ParseActivity(string value) => value switch
    {
        "idle" => AgentActivity.Idle,
        "building" => AgentActivity.Building,
        "upgrading" => AgentActivity.Upgrading,
        _ => throw new ArgumentException($"unknown agent activity '{value}'", nameof(value)),
    };

    private static string HealthValue(AgentOperationalHealth health) => health switch
    {
        AgentOperationalHealth.Unknown => "unknown",
        AgentOperationalHealth.Healthy => "healthy",
        AgentOperationalHealth.Unhealthy => "unhealthy",
        _ => "unknown",
    };

    private sealed record RuntimeProjection(
        bool Connected,
        bool Reconciled,
        AgentOperationalHealth OperationalHealth,
        bool Quarantined,
        string OperationalReason,
        bool Authorized,
        bool Enabled,
        AgentActivity Activity,
        string? CurrentBuildId,
        DateTimeOffset LastCommunication,
        long ConnectionGeneration,
        long ParameterGeneration);
}
