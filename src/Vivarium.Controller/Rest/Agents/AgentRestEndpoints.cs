using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Vivarium.Controller.Agents;
using Vivarium.Controller.Rest.Common;
using Vivarium.Controller.Security;

namespace Vivarium.Controller.Rest.Agents;

public static class AgentRestEndpoints
{
    private const string CursorResource = "agents";
    private static readonly HashSet<string> AllowedQueryParameters = new(
        [
            "search", "agentId", "connected", "reconciled", "authorized", "enabled",
            "activity", "hostname", "osFamily", "osVersion", "osBuild", "architecture",
            "agentVersion", "capability", "packageDigest", "sort", "limit", "cursor",
        ],
        StringComparer.OrdinalIgnoreCase);

    public static IServiceCollection AddAgentRestApi(this IServiceCollection services)
    {
        services.AddSingleton<AgentRestProjection>();
        return services;
    }

    public static IEndpointRouteBuilder MapAgentRestApi(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
                "/api/v1/agents",
                (Func<HttpContext, AgentRestProjection, RestCursorCodec, Task<IResult>>)ListAgentsAsync)
            .WithName("listAgents")
            .WithTags("Agents")
            .WithSummary("List visible Agents")
            .WithDescription(
                "Returns a bounded AgentExplorer collection without contacting Agents. " +
                "The legacy Phase-1 permission model limits this read to administrator principals.")
            .Produces<AgentRestCollection<AgentResource>>()
            .Produces<VivariumProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")
            .Produces<VivariumProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")
            .Produces<VivariumProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")
            .Produces<VivariumProblemDetails>(StatusCodes.Status410Gone, "application/problem+json")
            .Produces(StatusCodes.Status304NotModified);

        endpoints.MapGet(
                "/api/v1/agents/{agentId}",
                (Func<HttpContext, string, AgentRestProjection, Task<IResult>>)GetAgentAsync)
            .WithName("getAgent")
            .WithTags("Agents")
            .WithSummary("Read one Agent")
            .WithDescription(
                "Returns the stable Agent identity, TeamCity status axes, current activity, " +
                "basic persisted facts, and separately owned parameter maps.")
            .Produces<AgentResource>()
            .Produces<VivariumProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")
            .Produces<VivariumProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")
            .Produces<VivariumProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")
            .Produces(StatusCodes.Status304NotModified);

        endpoints.MapGet(
                "/api/v1/agents/{agentId}/facts",
                (Func<HttpContext, string, AgentRestProjection, Task<IResult>>)GetAgentFactsAsync)
            .WithName("getAgentFacts")
            .WithTags("Agents")
            .WithSummary("Read one Agent's bounded typed fact observation")
            .WithDescription(
                "Returns the latest persisted static fact observation and stable capabilities. " +
                "Legacy Agents are represented explicitly with unknown quality and empty typed facts.")
            .Produces<AgentFactsResource>()
            .Produces<VivariumProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")
            .Produces<VivariumProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")
            .Produces<VivariumProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")
            .Produces(StatusCodes.Status304NotModified);
        return endpoints;
    }

    private static async Task<IResult> ListAgentsAsync(
        HttpContext context,
        AgentRestProjection projection,
        RestCursorCodec cursors)
    {
        var authorization = await RestAuthentication.AuthorizeAsync(
            context,
            ManagementPermission.AgentList,
            "rest-agent-list");
        if (!authorization.IsAuthorized)
        {
            return authorization.Failure!;
        }

        RejectUnsupportedQueryParameters(context.Request);
        var limit = RestPagination.ParseLimit(context.Request);
        var parsedSort = ParseSort(context.Request.Query["sort"]);
        var fingerprint = RestQueryFingerprint.Create(context.Request);
        var after = DecodeCursor(
            context.Request.Query["cursor"],
            cursors,
            authorization.Context!.Principal,
            fingerprint,
            parsedSort.Canonical);
        var activities = ParseActivities(context.Request.Query["activity"]);
        var query = new AgentReadQuery(
            new AgentStoreQuery(
                Search: ParseOptionalText(context.Request.Query["search"], "search", 256),
                AgentIds: ParseTextValues(context.Request.Query["agentId"], "agentId", 256),
                Authorized: ParseOptionalBoolean(context.Request.Query["authorized"], "authorized"),
                Enabled: ParseOptionalBoolean(context.Request.Query["enabled"], "enabled"),
                OsFamilies: ParseTextValues(context.Request.Query["osFamily"], "osFamily", 128),
                OsVersions: ParseTextValues(context.Request.Query["osVersion"], "osVersion", 128),
                OsBuilds: ParseTextValues(context.Request.Query["osBuild"], "osBuild", 128),
                Architectures: ParseTextValues(context.Request.Query["architecture"], "architecture", 128),
                AgentVersions: ParseTextValues(context.Request.Query["agentVersion"], "agentVersion", 128),
                Hostnames: ParseTextValues(context.Request.Query["hostname"], "hostname", 255),
                Capabilities: ParseTextValues(context.Request.Query["capability"], "capability", 128),
                PackageDigests: ParsePackageDigests(context.Request.Query["packageDigest"]),
                Sort: parsedSort.StoreSort),
            Connected: ParseOptionalBoolean(context.Request.Query["connected"], "connected"),
            Reconciled: ParseOptionalBoolean(context.Request.Query["reconciled"], "reconciled"),
            Activities: activities,
            After: after,
            Limit: limit);
        var page = await projection.ListAgentsAsync(query);
        var nextCursor = page.NextCursor is null
            ? null
            : cursors.Encode(
                JsonSerializer.Serialize(page.NextCursor, RestJson.SerializerOptions),
                authorization.Context.Principal,
                CursorResource,
                fingerprint,
                parsedSort.Canonical);
        var resource = new AgentRestCollection<AgentResource>(
            page.Items,
            new AgentRestPage(nextCursor, limit));
        var etagState = new
        {
            page.Items,
            HasNextPage = page.NextCursor is not null,
            Limit = limit,
        };
        return RestEtags.ApplyConditionalGet(
            context,
            RestEtags.FromValue(etagState),
            resource);
    }

    private static async Task<IResult> GetAgentAsync(
        HttpContext context,
        string agentId,
        AgentRestProjection projection)
    {
        var authorization = await RestAuthentication.AuthorizeAsync(
            context,
            ManagementPermission.AgentList,
            "rest-agent-detail",
            new RestProblemTarget("agent", agentId));
        if (!authorization.IsAuthorized)
        {
            return authorization.Failure!;
        }

        var resource = await projection.GetAgentAsync(agentId);
        if (resource is null)
        {
            return RestProblems.NotFound(context, "agent", agentId);
        }

        return RestEtags.ApplyConditionalGet(
            context,
            RestEtags.FromValue(resource),
            resource);
    }

    private static async Task<IResult> GetAgentFactsAsync(
        HttpContext context,
        string agentId,
        AgentRestProjection projection)
    {
        var authorization = await RestAuthentication.AuthorizeAsync(
            context,
            ManagementPermission.AgentList,
            "rest-agent-facts",
            new RestProblemTarget("agent", agentId));
        if (!authorization.IsAuthorized)
        {
            return authorization.Failure!;
        }

        var resource = await projection.GetAgentFactsAsync(agentId);
        if (resource is null)
        {
            return RestProblems.NotFound(context, "agent", agentId);
        }

        return RestEtags.ApplyConditionalGet(
            context,
            RestEtags.FromValue(resource),
            resource);
    }

    private static AgentStoreCursor? DecodeCursor(
        Microsoft.Extensions.Primitives.StringValues supplied,
        RestCursorCodec cursors,
        ManagementPrincipal principal,
        string fingerprint,
        string sort)
    {
        if (supplied.Count == 0)
        {
            return null;
        }

        if (supplied.Count != 1 || string.IsNullOrWhiteSpace(supplied[0]))
        {
            throw InvalidCursor();
        }

        try
        {
            var position = cursors.Decode(supplied[0]!, principal, CursorResource, fingerprint, sort);
            return JsonSerializer.Deserialize<AgentStoreCursor>(position, RestJson.SerializerOptions)
                ?? throw InvalidCursor();
        }
        catch (JsonException exception)
        {
            throw InvalidCursor(exception);
        }
    }

    private static (AgentStoreSort StoreSort, string Canonical) ParseSort(
        Microsoft.Extensions.Primitives.StringValues supplied)
    {
        if (supplied.Count == 0 || supplied is ["name"] or ["name,agentId"])
        {
            return (AgentStoreSort.NameAscending, "name,agentId");
        }

        if (supplied.Count != 1)
        {
            throw InvalidQuery("sort", "unsupported_sort", "Specify one supported Agent sort order.");
        }

        return supplied[0] switch
        {
            "-name" or "-name,-agentId" => (AgentStoreSort.NameDescending, "-name,-agentId"),
            "agentId" => (AgentStoreSort.AgentIdAscending, "agentId"),
            "-agentId" => (AgentStoreSort.AgentIdDescending, "-agentId"),
            _ => throw InvalidQuery(
                "sort",
                "unsupported_sort",
                "Supported Agent sorts are name and agentId, optionally descending."),
        };
    }

    private static IReadOnlySet<AgentActivity>? ParseActivities(
        Microsoft.Extensions.Primitives.StringValues supplied)
    {
        var values = ParseTextValues(supplied, "activity", 32);
        if (values is null)
        {
            return null;
        }

        try
        {
            return values.Select(AgentRestProjection.ParseActivity).ToHashSet();
        }
        catch (ArgumentException)
        {
            throw InvalidQuery(
                "activity",
                "invalid_filter",
                "Agent activity must be idle, building, or upgrading.");
        }
    }

    private static bool? ParseOptionalBoolean(
        Microsoft.Extensions.Primitives.StringValues supplied,
        string name)
    {
        if (supplied.Count == 0)
        {
            return null;
        }

        if (supplied.Count != 1 || !bool.TryParse(supplied[0], out var value))
        {
            throw InvalidQuery(name, "invalid_filter", $"The {name} filter must be true or false.");
        }

        return value;
    }

    private static string? ParseOptionalText(
        Microsoft.Extensions.Primitives.StringValues supplied,
        string name,
        int maximumLength)
    {
        if (supplied.Count == 0)
        {
            return null;
        }

        if (supplied.Count != 1 || string.IsNullOrWhiteSpace(supplied[0]) || supplied[0]!.Length > maximumLength)
        {
            throw InvalidQuery(
                name,
                "invalid_filter",
                $"The {name} filter must contain 1-{maximumLength} characters.");
        }

        return supplied[0]!.Trim();
    }

    private static IReadOnlyList<string>? ParseTextValues(
        Microsoft.Extensions.Primitives.StringValues supplied,
        string name,
        int maximumLength)
    {
        if (supplied.Count == 0)
        {
            return null;
        }

        if (supplied.Count > 50 || supplied.Any(value =>
                string.IsNullOrWhiteSpace(value) || value!.Length > maximumLength))
        {
            throw InvalidQuery(
                name,
                "invalid_filter",
                $"The {name} filter accepts at most 50 values of 1-{maximumLength} characters.");
        }

        return supplied.Select(value => value!.Trim()).Distinct(StringComparer.Ordinal).ToArray();
    }

    private static IReadOnlyList<string>? ParsePackageDigests(
        Microsoft.Extensions.Primitives.StringValues supplied)
    {
        var values = ParseTextValues(supplied, "packageDigest", 64);
        if (values is null)
        {
            return null;
        }

        if (values.Any(value => value.Length != 64 || value.Any(character =>
                !(char.IsAsciiDigit(character) || character is >= 'a' and <= 'f'))))
        {
            throw InvalidQuery(
                "packageDigest",
                "invalid_filter",
                "Package digests must be lowercase 64-character SHA-256 values.");
        }

        return values;
    }

    private static void RejectUnsupportedQueryParameters(HttpRequest request)
    {
        var unsupported = request.Query.Keys
            .Where(key => !AllowedQueryParameters.Contains(key))
            .Order(StringComparer.Ordinal)
            .FirstOrDefault();
        if (unsupported is not null)
        {
            throw InvalidQuery(
                unsupported,
                "unsupported_filter",
                $"The '{unsupported}' query parameter is not supported for Agents.");
        }
    }

    private static RestApiException InvalidCursor(Exception? innerException = null) => new(
        StatusCodes.Status400BadRequest,
        "invalid_cursor",
        "The pagination cursor is invalid",
        "Restart the Agent collection request without a cursor.",
        innerException: innerException);

    private static RestApiException InvalidQuery(string path, string code, string message) => new(
        StatusCodes.Status400BadRequest,
        code,
        "The Agent query is invalid",
        message,
        errors: [new RestProblemError(path, code, message)]);
}
