using System.Text.Json;
using Microsoft.Extensions.Primitives;
using Vivarium.Controller.Rest.Builds;
using Vivarium.Controller.Rest.Common;
using Vivarium.Controller.Security;

namespace Vivarium.Controller.Rest.Events;

public static class BuildEventEndpoints
{
    private const string LastEventIdHeader = "Last-Event-ID";
    private static readonly HashSet<string> SupportedQueryParameters = new(
        ["topic", "resourceId", "cursor"], StringComparer.OrdinalIgnoreCase);

    public static IServiceCollection AddVivariumBuildEventApi(
        this IServiceCollection services,
        BuildEventStreamOptions? options = null)
    {
        services.AddSingleton(options ?? new BuildEventStreamOptions());
        services.AddSingleton<BuildEventStore>();
        return services;
    }

    public static IEndpointRouteBuilder MapVivariumBuildEventApi(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/events", (Func<HttpContext, Task>)StreamAsync)
            .WithName("WatchBuildEvents")
            .WithTags("Events")
            .WithSummary("Resume a bounded stream of Build state changes")
            .WithDescription(
                "Streams durable Build event projections. Reconnect with Last-Event-ID or the " +
                "cursor query parameter and re-read the referenced Build for authoritative state.")
            .Produces(
                StatusCodes.Status200OK,
                responseType: null,
                contentType: "text/event-stream")
            .Produces<VivariumProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")
            .Produces<VivariumProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")
            .Produces<VivariumProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")
            .Produces<VivariumProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")
            .Produces<VivariumProblemDetails>(StatusCodes.Status410Gone, "application/problem+json");
        return endpoints;
    }

    private static async Task StreamAsync(HttpContext context)
    {
        var resourceId = RequiredQuery(context.Request, "resourceId", 256);
        var target = new RestProblemTarget("build", resourceId);
        var authorization = await RestAuthentication.AuthorizeAsync(
            context,
            ManagementPermission.BuildWatch,
            "rest-build-events",
            target);
        if (!authorization.IsAuthorized)
        {
            await authorization.Failure!.ExecuteAsync(context);
            return;
        }

        RejectUnsupportedQueryParameters(context.Request);
        var topic = RequiredQuery(context.Request, "topic", 32);
        if (!string.Equals(topic, "build", StringComparison.Ordinal))
        {
            await Problem(
                context,
                StatusCodes.Status400BadRequest,
                "unsupported_event_topic",
                "The event topic is not supported",
                "This endpoint currently supports only topic=build.",
                target).ExecuteAsync(context);
            return;
        }

        var projection = context.RequestServices.GetRequiredService<BuildRestProjection>();
        if (await projection.GetBuildAsync(resourceId) is null)
        {
            await RestProblems.NotFound(context, "build", resourceId).ExecuteAsync(context);
            return;
        }

        string? cursor;
        try
        {
            cursor = ParseResumeCursor(context.Request);
        }
        catch (RestApiException exception)
        {
            await RestProblems.Create(context, exception).ExecuteAsync(context);
            return;
        }

        var events = context.RequestServices.GetRequiredService<BuildEventStore>();
        var options = context.RequestServices.GetRequiredService<BuildEventStreamOptions>();
        ValidateOptions(options);
        BuildEventPage initial;
        try
        {
            initial = await events.ReadAfterAsync(resourceId, cursor, options.BatchSize);
        }
        catch (BuildEventCursorException exception)
        {
            await CursorProblem(context, resourceId, exception).ExecuteAsync(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["X-Accel-Buffering"] = "no";
        await context.Response.StartAsync(context.RequestAborted);

        var page = initial;
        var lastWrite = TimeProvider.System.GetUtcNow();
        while (!context.RequestAborted.IsCancellationRequested)
        {
            if (page.Items.Count != 0)
            {
                foreach (var item in page.Items)
                {
                    var currentAuthorization = await RestAuthentication.AuthorizeAsync(
                        context,
                        ManagementPermission.BuildWatch,
                        "rest-build-events-emit",
                        target);
                    if (!currentAuthorization.IsAuthorized)
                    {
                        return;
                    }

                    var envelope = ToEnvelope(item);
                    var json = JsonSerializer.Serialize(envelope, RestJson.SerializerOptions);
                    if (!await TryWriteAsync(
                            context,
                            $"id: {item.Id}\nevent: build\ndata: {json}\n\n",
                            options.WriteTimeout))
                    {
                        return;
                    }

                    cursor = item.Id;
                    lastWrite = TimeProvider.System.GetUtcNow();
                }

                try
                {
                    page = await events.ReadAfterAsync(resourceId, cursor, options.BatchSize);
                }
                catch (BuildEventCursorException)
                {
                    // Retention can advance while a connection is caught up. The client resumes via
                    // an authoritative GET after the server closes this now-gapped stream.
                    return;
                }

                continue;
            }

            var now = TimeProvider.System.GetUtcNow();
            if (now - lastWrite >= options.KeepaliveInterval)
            {
                var stillAuthorized = await RestAuthentication.AuthorizeAsync(
                    context,
                    ManagementPermission.BuildWatch,
                    "rest-build-events-keepalive",
                    target);
                if (!stillAuthorized.IsAuthorized ||
                    !await TryWriteAsync(context, ": keepalive\n\n", options.WriteTimeout))
                {
                    return;
                }

                lastWrite = now;
            }

            try
            {
                await Task.Delay(options.PollInterval, context.RequestAborted);
                page = await events.ReadAfterAsync(resourceId, cursor, options.BatchSize);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                return;
            }
            catch (BuildEventCursorException)
            {
                return;
            }
        }
    }

    private static RestEventEnvelope ToEnvelope(StoredBuildEvent item)
    {
        var data = JsonSerializer.SerializeToElement(
            new BuildEventData(item.ResourceUrl, AuthoritativeGetRequired: true),
            RestJson.SerializerOptions);
        return new RestEventEnvelope(
            item.Id,
            item.Sequence,
            item.OccurredAt,
            item.Type,
            new EventResourceReference("build", item.MatrixBuildId, item.ResourceUrl),
            item.CorrelationId,
            data,
            ConfigurationRevision: null,
            ObservationRevision: null,
            item.RuntimeRevision);
    }

    private static async Task<bool> TryWriteAsync(
        HttpContext context,
        string value,
        TimeSpan timeout)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(
            context.RequestAborted);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await context.Response.WriteAsync(value, timeoutSource.Token);
            await context.Response.Body.FlushAsync(timeoutSource.Token);
            return true;
        }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        {
            context.Abort();
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static string? ParseResumeCursor(HttpRequest request)
    {
        var header = SingleOptional(request.Headers[LastEventIdHeader], LastEventIdHeader, 128);
        var query = SingleOptional(request.Query["cursor"], "cursor", 128);
        if (header is not null && query is not null &&
            !string.Equals(header, query, StringComparison.Ordinal))
        {
            throw InvalidCursor(
                "Last-Event-ID and cursor must identify the same event when both are supplied.");
        }

        return header ?? query;
    }

    private static string RequiredQuery(HttpRequest request, string name, int maximumLength) =>
        SingleOptional(request.Query[name], name, maximumLength) ?? throw new RestApiException(
            StatusCodes.Status400BadRequest,
            "event_filter_required",
            "An event stream filter is required",
            $"Supply exactly one non-empty {name} query parameter.",
            errors: [new RestProblemError(name, "required", $"{name} is required.")]);

    private static string? SingleOptional(
        StringValues values,
        string name,
        int maximumLength)
    {
        if (values.Count == 0)
        {
            return null;
        }

        if (values.Count != 1 || string.IsNullOrWhiteSpace(values[0]) ||
            values[0]!.Length > maximumLength ||
            values[0]!.Any(character => character is '\r' or '\n' or '\0'))
        {
            throw new RestApiException(
                StatusCodes.Status400BadRequest,
                "invalid_event_filter",
                "An event stream filter is invalid",
                $"Supply exactly one safe {name} value of at most {maximumLength} characters.",
                errors: [new RestProblemError(name, "invalid", $"{name} is invalid.")]);
        }

        return values[0]!.Trim();
    }

    private static void RejectUnsupportedQueryParameters(HttpRequest request)
    {
        var unsupported = request.Query.Keys
            .Where(key => !SupportedQueryParameters.Contains(key))
            .Order(StringComparer.Ordinal)
            .FirstOrDefault();
        if (unsupported is not null)
        {
            throw new RestApiException(
                StatusCodes.Status400BadRequest,
                "unsupported_event_filter",
                "An event stream filter is not supported",
                $"The '{unsupported}' query parameter is not supported.",
                errors: [new RestProblemError(unsupported, "unsupported", "Remove this filter.")]);
        }
    }

    private static RestApiException InvalidCursor(string detail) => new(
        StatusCodes.Status400BadRequest,
        "invalid_event_cursor",
        "The event cursor is invalid",
        detail);

    private static IResult CursorProblem(
        HttpContext context,
        string matrixBuildId,
        BuildEventCursorException exception) => exception.Expired
        ? Problem(
            context,
            StatusCodes.Status410Gone,
            "event_cursor_expired",
            "The event cursor has expired",
            $"Recover current state with GET /api/v1/builds/{Uri.EscapeDataString(matrixBuildId)}.",
            new RestProblemTarget("build", matrixBuildId))
        : Problem(
            context,
            StatusCodes.Status400BadRequest,
            "invalid_event_cursor",
            "The event cursor is invalid",
            exception.Message,
            new RestProblemTarget("build", matrixBuildId));

    private static IResult Problem(
        HttpContext context,
        int status,
        string code,
        string title,
        string detail,
        RestProblemTarget? target = null) =>
        RestProblems.Create(context, status, code, title, detail, target: target);

    private static void ValidateOptions(BuildEventStreamOptions options)
    {
        if (options.BatchSize is < 1 or > BuildEventStore.MaximumBatchSize ||
            options.PollInterval <= TimeSpan.Zero ||
            options.KeepaliveInterval <= TimeSpan.Zero ||
            options.WriteTimeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("Build event stream options are invalid");
        }
    }
}
