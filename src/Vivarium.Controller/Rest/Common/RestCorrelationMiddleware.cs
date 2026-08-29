using System.Text.Json;
using Vivarium.Controller.Security;

namespace Vivarium.Controller.Rest.Common;

public static class RestCorrelation
{
    private const string ItemKey = "Vivarium.Rest.CorrelationId";

    public static string Get(HttpContext context) =>
        context.Items.TryGetValue(ItemKey, out var value) && value is string correlationId
            ? correlationId
            : ManagementIdentifiers.NewId();

    internal static void Set(HttpContext context, string correlationId) =>
        context.Items[ItemKey] = correlationId;
}

internal sealed class RestCorrelationMiddleware(
    RequestDelegate next,
    ILogger<RestCorrelationMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/api/v1"))
        {
            await next(context);
            return;
        }

        string correlationId;
        try
        {
            correlationId = ManagementIdentifiers.NormalizeCorrelationId(
                context.Request.Headers[ManagementRequestContextFactory.CorrelationHeader].ToString());
        }
        catch (ArgumentException)
        {
            correlationId = ManagementIdentifiers.NewId();
            RestCorrelation.Set(context, correlationId);
            context.Response.Headers[ManagementRequestContextFactory.CorrelationHeader] = correlationId;
            await RestProblems.Create(
                context,
                StatusCodes.Status400BadRequest,
                "invalid_correlation_id",
                "The correlation ID is invalid",
                "X-Correlation-ID must be 8-128 ASCII letters, digits, '.', ':', '_' or '-'.")
                .ExecuteAsync(context);
            return;
        }

        RestCorrelation.Set(context, correlationId);
        context.Response.Headers[ManagementRequestContextFactory.CorrelationHeader] = correlationId;
        try
        {
            await next(context);
        }
        catch (RestApiException exception) when (!context.Response.HasStarted)
        {
            context.Response.Clear();
            context.Response.Headers[ManagementRequestContextFactory.CorrelationHeader] = correlationId;
            await RestProblems.Create(context, exception).ExecuteAsync(context);
        }
        catch (BadHttpRequestException exception) when (!context.Response.HasStarted)
        {
            context.Response.Clear();
            context.Response.Headers[ManagementRequestContextFactory.CorrelationHeader] = correlationId;
            await RestProblems.Create(
                context,
                StatusCodes.Status400BadRequest,
                "malformed_request",
                "The request is malformed",
                "The HTTP request could not be parsed.")
                .ExecuteAsync(context);
            logger.LogDebug(exception, "Rejected malformed REST request {CorrelationId}", correlationId);
        }
        catch (JsonException exception) when (!context.Response.HasStarted)
        {
            context.Response.Clear();
            context.Response.Headers[ManagementRequestContextFactory.CorrelationHeader] = correlationId;
            await RestProblems.Create(
                context,
                StatusCodes.Status400BadRequest,
                "malformed_json",
                "The JSON body is malformed",
                "The request body is not valid JSON.")
                .ExecuteAsync(context);
            logger.LogDebug(exception, "Rejected malformed REST JSON {CorrelationId}", correlationId);
        }
        catch (Exception exception) when (!context.Response.HasStarted)
        {
            logger.LogError(exception, "Unhandled REST request failure {CorrelationId}", correlationId);
            context.Response.Clear();
            context.Response.Headers[ManagementRequestContextFactory.CorrelationHeader] = correlationId;
            await RestProblems.Create(
                context,
                StatusCodes.Status500InternalServerError,
                "internal_error",
                "The request failed",
                "The controller could not complete the request.",
                retryable: true)
                .ExecuteAsync(context);
        }
    }
}
