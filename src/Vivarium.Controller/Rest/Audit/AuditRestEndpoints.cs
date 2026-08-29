using System.Globalization;
using System.Text.Json;
using Vivarium.Controller.Auditing;
using Vivarium.Controller.Rest.Common;
using Vivarium.Controller.Security;

namespace Vivarium.Controller.Rest.Audit;

public static class AuditRestEndpoints
{
    private const string CursorResource = "audit-events";
    private const string CanonicalSort = "-receivedAt,-auditEventId";
    private static readonly HashSet<string> AllowedQueryParameters = new(
        [
            "actorId", "actorType", "action", "targetType", "targetId", "outcome",
            "from", "to", "sort", "limit", "cursor",
        ],
        StringComparer.OrdinalIgnoreCase);

    public static IServiceCollection AddAuditRestApi(this IServiceCollection services)
    {
        services.AddSingleton<AuditRestProjection>();
        return services;
    }

    public static IEndpointRouteBuilder MapAuditRestApi(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
                "/api/v1/audit-events",
                (Func<HttpContext, AuditRestProjection, RestCursorCodec, Task<IResult>>)ListAuditEventsAsync)
            .WithName("listAuditEvents")
            .WithTags("Audit")
            .WithSummary("List security audit events")
            .WithDescription(
                "Returns the bounded append-only audit projection. The current legacy permission " +
                "mapping uses AgentManage and therefore permits administrator principals only.")
            .Produces<AuditRestCollection<AuditEventResource>>()
            .Produces<VivariumProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")
            .Produces<VivariumProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")
            .Produces<VivariumProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")
            .Produces<VivariumProblemDetails>(StatusCodes.Status410Gone, "application/problem+json")
            .Produces(StatusCodes.Status304NotModified);
        return endpoints;
    }

    private static async Task<IResult> ListAuditEventsAsync(
        HttpContext context,
        AuditRestProjection projection,
        RestCursorCodec cursors)
    {
        var authorization = await RestAuthentication.AuthorizeAsync(
            context,
            ManagementPermission.AgentManage,
            "rest-audit-list");
        if (!authorization.IsAuthorized)
        {
            return authorization.Failure!;
        }

        RejectUnsupportedQueryParameters(context.Request);
        ParseSort(context.Request.Query["sort"]);
        var limit = RestPagination.ParseLimit(context.Request);
        var fingerprint = RestQueryFingerprint.Create(context.Request);
        var after = DecodeCursor(
            context.Request.Query["cursor"],
            cursors,
            authorization.Context!.Principal,
            fingerprint);
        var query = new AuditEventQuery(
            ActorIds: ParseTextValues(context.Request.Query["actorId"], "actorId", 256),
            ActorTypes: ParseTextValues(context.Request.Query["actorType"], "actorType", 32),
            Actions: ParseTextValues(context.Request.Query["action"], "action", 128),
            TargetTypes: ParseTextValues(context.Request.Query["targetType"], "targetType", 64),
            TargetIds: ParseTextValues(context.Request.Query["targetId"], "targetId", 256),
            Outcomes: ParseOutcomes(context.Request.Query["outcome"]),
            From: ParseTimestamp(context.Request.Query["from"], "from"),
            To: ParseTimestamp(context.Request.Query["to"], "to"));
        if (query.From > query.To)
        {
            throw InvalidQuery("from", "invalid_time_range", "The 'from' timestamp must not be later than 'to'.");
        }

        var page = await projection.ListAuditEventsAsync(query, after, limit);
        var nextCursor = page.NextCursor is null
            ? null
            : cursors.Encode(
                JsonSerializer.Serialize(page.NextCursor, RestJson.SerializerOptions),
                authorization.Context.Principal,
                CursorResource,
                fingerprint,
                CanonicalSort);
        var resource = new AuditRestCollection<AuditEventResource>(
            page.Items,
            new AuditRestPage(nextCursor, limit));
        var etagState = new
        {
            EventIds = page.Items.Select(item => item.Id),
            HasNextPage = page.NextCursor is not null,
            Limit = limit,
        };
        return RestEtags.ApplyConditionalGet(
            context,
            RestEtags.FromValue(etagState),
            resource);
    }

    private static AuditEventCursor? DecodeCursor(
        Microsoft.Extensions.Primitives.StringValues supplied,
        RestCursorCodec cursors,
        ManagementPrincipal principal,
        string fingerprint)
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
            var position = cursors.Decode(
                supplied[0]!,
                principal,
                CursorResource,
                fingerprint,
                CanonicalSort);
            return JsonSerializer.Deserialize<AuditEventCursor>(position, RestJson.SerializerOptions)
                ?? throw InvalidCursor();
        }
        catch (JsonException exception)
        {
            throw InvalidCursor(exception);
        }
    }

    private static void ParseSort(Microsoft.Extensions.Primitives.StringValues supplied)
    {
        if (supplied.Count == 0 || supplied is ["-receivedAt"] or [CanonicalSort])
        {
            return;
        }

        throw InvalidQuery(
            "sort",
            "unsupported_sort",
            "Audit events are sorted by descending receipt time and descending audit event ID.");
    }

    private static IReadOnlyList<AuditOutcome>? ParseOutcomes(
        Microsoft.Extensions.Primitives.StringValues supplied)
    {
        var values = ParseTextValues(supplied, "outcome", 32);
        if (values is null)
        {
            return null;
        }

        var outcomes = new List<AuditOutcome>(values.Count);
        foreach (var value in values)
        {
            outcomes.Add(value switch
            {
                "succeeded" => AuditOutcome.Succeeded,
                "denied" => AuditOutcome.Denied,
                "failed" => AuditOutcome.Failed,
                "no-change" => AuditOutcome.NoChange,
                _ => throw InvalidQuery(
                    "outcome",
                    "invalid_filter",
                    "Audit outcome must be succeeded, denied, failed, or no-change."),
            });
        }

        return outcomes.Distinct().ToArray();
    }

    private static DateTimeOffset? ParseTimestamp(
        Microsoft.Extensions.Primitives.StringValues supplied,
        string name)
    {
        if (supplied.Count == 0)
        {
            return null;
        }

        if (supplied.Count != 1 ||
            !supplied[0]!.EndsWith('Z') ||
            !DateTimeOffset.TryParseExact(
                supplied[0],
                ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            throw InvalidQuery(
                name,
                "invalid_filter",
                $"The {name} filter must be an RFC 3339 UTC timestamp ending in 'Z'.");
        }

        return parsed;
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
                $"The '{unsupported}' query parameter is not supported for audit events.");
        }
    }

    private static RestApiException InvalidCursor(Exception? innerException = null) => new(
        StatusCodes.Status400BadRequest,
        "invalid_cursor",
        "The pagination cursor is invalid",
        "Restart the audit-event collection request without a cursor.",
        innerException: innerException);

    private static RestApiException InvalidQuery(string path, string code, string message) => new(
        StatusCodes.Status400BadRequest,
        code,
        "The audit-event query is invalid",
        message,
        errors: [new RestProblemError(path, code, message)]);
}
