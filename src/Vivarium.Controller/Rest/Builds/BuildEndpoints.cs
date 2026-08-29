using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Primitives;
using Vivarium.Contracts.V1;
using Vivarium.Controller.Builds;
using Vivarium.Controller.Management;
using Vivarium.Controller.Rest.Common;
using Vivarium.Controller.Security;

namespace Vivarium.Controller.Rest.Builds;

public static class BuildEndpoints
{
    private const string BuildResource = "/api/v1/builds";
    private const string BuildSort = "-createdAt,-id";
    private const string QueueResource = "/api/v1/queue";
    private const string QueueSort = "queueId";
    private static readonly HashSet<string> BuildQueryParameters = new(
        ["project", "configuration", "state", "outcome", "sort", "limit", "cursor"],
        StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> QueueQueryParameters = new(
        ["project", "configuration", "state", "claimedAgentId", "sort", "limit", "cursor"],
        StringComparer.OrdinalIgnoreCase);

    public static IServiceCollection AddVivariumBuildApi(this IServiceCollection services)
    {
        services.AddSingleton<BuildRestProjection>();
        return services;
    }

    public static IEndpointRouteBuilder MapVivariumBuildApi(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1").WithTags("Builds");
        group.MapGet("/builds", (Func<HttpContext, Task<IResult>>)ListBuildsAsync)
            .WithName("ListBuilds")
            .WithSummary("List matrix builds")
            .Produces<BuildRestCollection<BuildSummaryResource>>()
            .Produces<VivariumProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")
            .Produces<VivariumProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")
            .Produces<VivariumProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json");
        group.MapGet("/builds/{matrixBuildId}", GetBuildAsync)
            .WithName("GetBuild")
            .WithSummary("Get a matrix build and its immutable child results")
            .Produces<BuildResource>()
            .Produces(StatusCodes.Status304NotModified)
            .Produces<VivariumProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")
            .Produces<VivariumProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")
            .Produces<VivariumProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json");
        group.MapGet("/queue", (Func<HttpContext, Task<IResult>>)ListQueueAsync)
            .WithName("ListBuildQueue")
            .WithSummary("List active Build Queue entries in durable FIFO order")
            .Produces<BuildRestCollection<QueueEntryResource>>()
            .Produces<VivariumProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")
            .Produces<VivariumProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")
            .Produces<VivariumProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json");
        return endpoints;
    }

    private static async Task<IResult> ListBuildsAsync(HttpContext httpContext)
    {
        var authorization = await RestAuthentication.AuthorizeAsync(
            httpContext, ManagementPermission.BuildWatch, "rest-build-list");
        if (!authorization.IsAuthorized)
        {
            return authorization.Failure!;
        }

        RejectUnsupportedQueryParameters(httpContext.Request, BuildQueryParameters, "builds");
        RequireSort(httpContext.Request, BuildSort);
        var limit = RestPagination.ParseLimit(httpContext.Request);
        var project = OptionalQuery(httpContext.Request, "project");
        var configuration = OptionalQuery(httpContext.Request, "configuration");
        var state = ParseBuildState(OptionalQuery(httpContext.Request, "state"));
        var outcome = ParseBuildOutcome(OptionalQuery(httpContext.Request, "outcome"));
        var fingerprint = RestQueryFingerprint.Create(httpContext.Request);
        var cursorCodec = httpContext.RequestServices.GetRequiredService<RestCursorCodec>();
        var cursor = DecodeBuildCursor(
            httpContext.Request,
            cursorCodec,
            authorization.Context!.Principal,
            fingerprint);
        var projection = httpContext.RequestServices.GetRequiredService<BuildRestProjection>();
        var page = await projection.ListBuildsAsync(new MatrixBuildQuery(
            limit,
            project,
            configuration,
            state,
            outcome,
            cursor?.CreatedAt,
            cursor?.Id));
        var nextCursor = page.HasMore && page.NextCreatedAt is not null && page.NextId is not null
            ? cursorCodec.Encode(
                JsonSerializer.Serialize(
                    new BuildCursor(page.NextCreatedAt.Value, page.NextId),
                    RestJson.SerializerOptions),
                authorization.Context.Principal,
                BuildResource,
                fingerprint,
                BuildSort)
            : null;
        var response = new BuildRestCollection<BuildSummaryResource>(
            page.Items,
            new BuildRestPage(nextCursor, limit));
        var etag = RestEtags.FromValue(new
        {
            page.Items,
            page.HasMore,
            limit,
            fingerprint,
        });
        return RestEtags.ApplyConditionalGet(httpContext, etag, response);
    }

    private static async Task<IResult> GetBuildAsync(
        HttpContext httpContext,
        string matrixBuildId)
    {
        var target = new RestProblemTarget("build", matrixBuildId);
        var authorization = await RestAuthentication.AuthorizeAsync(
            httpContext,
            ManagementPermission.BuildWatch,
            "rest-build-detail",
            target);
        if (!authorization.IsAuthorized)
        {
            return authorization.Failure!;
        }

        var projection = httpContext.RequestServices.GetRequiredService<BuildRestProjection>();
        var build = await projection.GetBuildAsync(matrixBuildId);
        if (build is null)
        {
            return RestProblems.NotFound(httpContext, "build", matrixBuildId);
        }

        return RestEtags.ApplyConditionalGet(
            httpContext,
            RestEtags.FromRevision($"{build.Id}\n{build.RuntimeRevision}"),
            build);
    }

    private static async Task<IResult> ListQueueAsync(HttpContext httpContext)
    {
        var authorization = await RestAuthentication.AuthorizeAsync(
            httpContext, ManagementPermission.BuildWatch, "rest-build-queue");
        if (!authorization.IsAuthorized)
        {
            return authorization.Failure!;
        }

        RejectUnsupportedQueryParameters(httpContext.Request, QueueQueryParameters, "queue");
        RequireSort(httpContext.Request, QueueSort);
        var limit = RestPagination.ParseLimit(httpContext.Request);
        var project = OptionalQuery(httpContext.Request, "project");
        var configuration = OptionalQuery(httpContext.Request, "configuration");
        var state = ParseQueueState(OptionalQuery(httpContext.Request, "state"));
        var claimedAgentId = OptionalQuery(httpContext.Request, "claimedAgentId");
        var fingerprint = RestQueryFingerprint.Create(httpContext.Request);
        var cursorCodec = httpContext.RequestServices.GetRequiredService<RestCursorCodec>();
        var afterQueueId = DecodeQueueCursor(
            httpContext.Request,
            cursorCodec,
            authorization.Context!.Principal,
            fingerprint);
        var projection = httpContext.RequestServices.GetRequiredService<BuildRestProjection>();
        var page = await projection.ListQueueAsync(new BuildQueueQuery(
            limit,
            afterQueueId,
            state,
            project,
            configuration,
            claimedAgentId));
        var nextCursor = page.HasMore && page.NextQueueId is not null
            ? cursorCodec.Encode(
                page.NextQueueId.Value.ToString(CultureInfo.InvariantCulture),
                authorization.Context.Principal,
                QueueResource,
                fingerprint,
                QueueSort)
            : null;
        var response = new BuildRestCollection<QueueEntryResource>(
            page.Items,
            new BuildRestPage(nextCursor, limit));
        var etag = RestEtags.FromValue(new
        {
            page.Items,
            page.HasMore,
            limit,
            fingerprint,
        });
        return RestEtags.ApplyConditionalGet(httpContext, etag, response);
    }

    private static BuildCursor? DecodeBuildCursor(
        HttpRequest request,
        RestCursorCodec codec,
        ManagementPrincipal principal,
        string fingerprint)
    {
        var protectedCursor = OptionalQuery(request, "cursor");
        if (protectedCursor is null)
        {
            return null;
        }

        var position = codec.Decode(
            protectedCursor, principal, BuildResource, fingerprint, BuildSort);
        try
        {
            return JsonSerializer.Deserialize<BuildCursor>(position, RestJson.SerializerOptions)
                ?? throw InvalidCursor();
        }
        catch (Exception exception) when (exception is JsonException or FormatException)
        {
            throw InvalidCursor(exception);
        }
    }

    private static long? DecodeQueueCursor(
        HttpRequest request,
        RestCursorCodec codec,
        ManagementPrincipal principal,
        string fingerprint)
    {
        var protectedCursor = OptionalQuery(request, "cursor");
        if (protectedCursor is null)
        {
            return null;
        }

        var position = codec.Decode(
            protectedCursor, principal, QueueResource, fingerprint, QueueSort);
        if (!long.TryParse(position, NumberStyles.None, CultureInfo.InvariantCulture, out var queueId) ||
            queueId < 0)
        {
            throw InvalidCursor();
        }

        return queueId;
    }

    private static DurableBuildState? ParseBuildState(string? value) => value switch
    {
        null => null,
        "queued" => DurableBuildState.Queued,
        "running" => DurableBuildState.Running,
        "cancel-requested" => DurableBuildState.CancelRequested,
        "finished" => DurableBuildState.Finished,
        _ => throw InvalidFilter("state", "Use queued, running, cancel-requested, or finished."),
    };

    private static BuildOutcome? ParseBuildOutcome(string? value) => value switch
    {
        null => null,
        "succeeded" => BuildOutcome.Succeeded,
        "failed" => BuildOutcome.Failed,
        "cancelled" => BuildOutcome.Cancelled,
        "infrastructure-failed" => BuildOutcome.InfrastructureFailed,
        _ => throw InvalidFilter(
            "outcome", "Use succeeded, failed, cancelled, or infrastructure-failed."),
    };

    private static BuildQueueItemState? ParseQueueState(string? value) => value switch
    {
        null => null,
        "queued" => BuildQueueItemState.Queued,
        "claimed" => BuildQueueItemState.Claimed,
        _ => throw InvalidFilter("state", "Use queued or claimed."),
    };

    private static string? OptionalQuery(HttpRequest request, string name)
    {
        if (!request.Query.TryGetValue(name, out StringValues values))
        {
            return null;
        }

        if (values.Count != 1 || string.IsNullOrWhiteSpace(values[0]))
        {
            throw InvalidFilter(name, "Supply exactly one non-empty value.");
        }

        return values[0]!.Trim();
    }

    private static void RequireSort(HttpRequest request, string expected)
    {
        var supplied = OptionalQuery(request, "sort");
        if (supplied is not null && !string.Equals(supplied, expected, StringComparison.Ordinal))
        {
            throw InvalidFilter("sort", $"This collection supports only '{expected}'.");
        }
    }

    private static void RejectUnsupportedQueryParameters(
        HttpRequest request,
        IReadOnlySet<string> allowed,
        string resource)
    {
        var unsupported = request.Query.Keys
            .Where(key => !allowed.Contains(key))
            .Order(StringComparer.Ordinal)
            .FirstOrDefault();
        if (unsupported is not null)
        {
            throw InvalidFilter(
                unsupported,
                $"The '{unsupported}' query parameter is not supported for {resource}.");
        }
    }

    private static RestApiException InvalidFilter(string path, string message) => new(
        StatusCodes.Status400BadRequest,
        "invalid_filter",
        "A collection filter is invalid",
        message,
        errors: [new RestProblemError(path, "invalid", message)]);

    private static RestApiException InvalidCursor(Exception? innerException = null) => new(
        StatusCodes.Status400BadRequest,
        "invalid_cursor",
        "The pagination cursor is invalid",
        "Restart the collection request without a cursor.",
        innerException: innerException);

    private sealed record BuildCursor(DateTimeOffset CreatedAt, string Id);
}
