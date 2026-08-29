using Vivarium.Controller.Blobs.Access;
using Vivarium.Controller.Management;
using Vivarium.Controller.Rest.Common;
using Vivarium.Controller.Security;

namespace Vivarium.Controller.Rest.Builds.Mutations;

public static class BuildMutationEndpoints
{
    public const string IdempotencyHeader = "Idempotency-Key";

    public static IServiceCollection AddVivariumBuildMutationApi(this IServiceCollection services)
    {
        services.AddSingleton<BuildMutationService>();
        return services;
    }

    public static IEndpointRouteBuilder MapVivariumBuildMutationApi(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(
                "/api/v1/builds",
                (Func<HttpContext, BuildSubmissionRequest?, BuildMutationService, Task<IResult>>)SubmitAsync)
            .WithName("SubmitBuild")
            .WithTags("Builds")
            .WithSummary("Submit one durable matrix Build")
            .WithDescription(
                "Consumes an attached blob staging plan and creates the matrix and all child queue " +
                "entries atomically. Idempotency is scoped to principal, method, path, and key.")
            .Produces<BuildResource>(StatusCodes.Status201Created)
            .Produces<VivariumProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")
            .Produces<VivariumProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")
            .Produces<VivariumProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")
            .Produces<VivariumProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")
            .Produces<VivariumProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")
            .Produces<VivariumProblemDetails>(StatusCodes.Status410Gone, "application/problem+json")
            .Produces<VivariumProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json");

        endpoints.MapPut(
                "/api/v1/builds/{matrixBuildId}/cancellation",
                (Func<HttpContext, string, BuildCancellationRequest?, BuildMutationService,
                    Task<IResult>>)CancelAsync)
            .WithName("CancelBuild")
            .WithTags("Builds")
            .WithSummary("Request convergent cancellation of a matrix Build")
            .WithDescription(
                "Durably preserves the first accepted reason and returns current authoritative " +
                "Build state. No If-Match is required for this convergent compatibility mutation.")
            .Produces<BuildResource>()
            .Produces<VivariumProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")
            .Produces<VivariumProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")
            .Produces<VivariumProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")
            .Produces<VivariumProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")
            .Produces<VivariumProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json");
        return endpoints;
    }

    private static async Task<IResult> SubmitAsync(
        HttpContext context,
        BuildSubmissionRequest? request,
        BuildMutationService service)
    {
        var authentication = await RestAuthentication.AuthenticateManagementAsync(
            context,
            "rest-build-submit");
        if (!authentication.IsAuthorized)
        {
            return authentication.Failure!;
        }

        var idempotencyKey = ParseIdempotencyKey(context.Request);
        if (request is null)
        {
            return ValidationProblem(
                context,
                "build_request_required",
                "A Build submission body is required",
                "Supply the documented Build submission JSON object.");
        }

        try
        {
            var response = await service.SubmitAsync(
                authentication.Context!.WithRequestId(idempotencyKey),
                request,
                idempotencyKey,
                context.RequestAborted);
            context.Response.Headers.Location = response.Location;
            context.Response.Headers.ETag = response.ETag;
            context.Response.Headers.CacheControl = "private, no-cache";
            return Results.Text(
                response.Json,
                "application/json",
                statusCode: response.Status);
        }
        catch (ManagementAuthorizationException)
        {
            return RestProblems.PermissionDenied(
                context,
                ManagementPermission.BuildSubmit.ToString());
        }
        catch (MatrixRequestConflictException)
        {
            return Problem(
                context,
                StatusCodes.Status409Conflict,
                "idempotency_key_reused",
                "The Idempotency-Key was already used",
                "Use the same key only for the exact same effective Build submission.");
        }
        catch (MatrixBuildValidationException exception)
        {
            return ValidationProblem(
                context,
                "build_submission_invalid",
                "The Build submission is invalid",
                SafeDetail(exception.Message));
        }
        catch (BlobAccessException exception)
        {
            return BlobProblem(context, exception);
        }
    }

    private static async Task<IResult> CancelAsync(
        HttpContext context,
        string matrixBuildId,
        BuildCancellationRequest? request,
        BuildMutationService service)
    {
        var authentication = await RestAuthentication.AuthenticateManagementAsync(
            context,
            "rest-build-cancel");
        if (!authentication.IsAuthorized)
        {
            return authentication.Failure!;
        }

        if (request is null || string.IsNullOrWhiteSpace(request.Reason) ||
            request.Reason.Length > 1024 || request.Reason.Any(character =>
                character is '\r' or '\n' or '\0'))
        {
            return ValidationProblem(
                context,
                "cancellation_reason_invalid",
                "The cancellation reason is invalid",
                "reason must contain 1-1024 safe characters.");
        }

        try
        {
            var build = await service.CancelAsync(
                authentication.Context!,
                matrixBuildId,
                request.Reason.Trim(),
                context.RequestAborted);
            if (build is null)
            {
                return RestProblems.NotFound(context, "build", matrixBuildId);
            }

            context.Response.Headers.ETag = RestEtags.FromRevision(
                $"{build.Id}\n{build.RuntimeRevision}");
            context.Response.Headers.CacheControl = "private, no-cache";
            return Results.Json(build);
        }
        catch (ManagementAuthorizationException)
        {
            return RestProblems.PermissionDenied(
                context,
                ManagementPermission.BuildCancel.ToString(),
                new RestProblemTarget("build", matrixBuildId));
        }
    }

    private static string ParseIdempotencyKey(HttpRequest request)
    {
        var values = request.Headers[IdempotencyHeader];
        if (values.Count != 1 || string.IsNullOrWhiteSpace(values[0]))
        {
            throw new RestApiException(
                StatusCodes.Status400BadRequest,
                "idempotency_key_required",
                "An Idempotency-Key is required",
                "Specify exactly one Idempotency-Key for this Build submission.");
        }

        var value = values[0]!;
        if (value.Length > 256 || value.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) ||
                    character is '.' or '_' or ':' or '@' or '/' or '-')))
        {
            throw new RestApiException(
                StatusCodes.Status400BadRequest,
                "idempotency_key_invalid",
                "The Idempotency-Key is invalid",
                "Idempotency-Key must contain 1-256 ASCII letters, digits, '.', '_', ':', '@', '/', or '-'.");
        }

        return value;
    }

    private static IResult BlobProblem(HttpContext context, BlobAccessException exception)
    {
        var (status, title) = exception.Failure switch
        {
            BlobAccessFailure.Validation => (
                StatusCodes.Status422UnprocessableEntity, "The blob staging plan is invalid"),
            BlobAccessFailure.NotFound => (
                StatusCodes.Status404NotFound, "The blob staging plan was not found"),
            BlobAccessFailure.Expired => (
                StatusCodes.Status410Gone, "The blob staging plan has expired"),
            _ => (StatusCodes.Status409Conflict, "The blob staging plan conflicts"),
        };
        return Problem(
            context,
            status,
            SafeCode(exception.Code),
            title,
            SafeDetail(exception.Message));
    }

    private static IResult ValidationProblem(
        HttpContext context,
        string code,
        string title,
        string detail) => Problem(
            context,
            StatusCodes.Status422UnprocessableEntity,
            code,
            title,
            detail);

    private static IResult Problem(
        HttpContext context,
        int status,
        string code,
        string title,
        string detail) =>
        RestProblems.Create(context, status, code, title, detail);

    private static string SafeCode(string value)
    {
        var safe = new string(value.ToLowerInvariant().Select(character =>
            char.IsAsciiLetterOrDigit(character) || character is '_' or '-'
                ? character
                : '_').Take(128).ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "blob_staging_conflict" : safe;
    }

    private static string SafeDetail(string value)
    {
        var bounded = new string(value.Where(character =>
            character is not '\r' and not '\n' and not '\0').Take(1024).ToArray());
        return string.IsNullOrWhiteSpace(bounded) ? "The request could not be accepted." : bounded;
    }
}
