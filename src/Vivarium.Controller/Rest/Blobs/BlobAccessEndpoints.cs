using System.Globalization;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vivarium.Controller.Auditing;
using Vivarium.Controller.Blobs;
using Vivarium.Controller.Blobs.Access;
using Vivarium.Controller.Rest.Common;
using Vivarium.Controller.Security;

namespace Vivarium.Controller.Rest.Blobs;

public static class BlobAccessEndpoints
{
    public const string IdempotencyHeader = "Idempotency-Key";
    public const string StagingIdHeader = "X-Vivarium-Blob-Staging-Id";
    public const string BuildIdHeader = "X-Vivarium-Build-Id";
    public const string SessionIdHeader = "X-Vivarium-Session-Id";
    public const string DeclaredSizeHeader = "X-Vivarium-Blob-Declared-Size";

    public static IServiceCollection AddBlobAccessApi(this IServiceCollection services)
    {
        services.TryAddSingleton<BlobAccessStore>();
        services.TryAddSingleton<BlobAccessService>();
        services.TryAddSingleton<IBlobObjectAccess>(services =>
            services.GetRequiredService<BlobAccessService>());
        services.TryAddSingleton<IBlobBuildAttachmentParticipant>(services =>
            services.GetRequiredService<BlobAccessStore>());
        services.TryAddSingleton<IBlobArtifactAttachmentParticipant>(services =>
            services.GetRequiredService<BlobAccessStore>());
        return services;
    }

    public static IEndpointRouteBuilder MapBlobAccessApi(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(
                "/api/v1/blob-upload-plans",
                (Func<HttpContext, BlobUploadPlanCreateRequest?, BlobAccessService, Task<IResult>>)
                CreatePlanAsync)
            .WithName("createBlobUploadPlan")
            .WithTags("Blobs")
            .WithSummary("Create an object-scoped blob upload plan")
            .WithDescription(
                "Creates one expiring principal/project-owned staging resource. Physical content " +
                "deduplication never discloses another owner's blob presence.")
            .Produces<BlobUploadPlanResource>(StatusCodes.Status201Created)
            .Produces<VivariumProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")
            .Produces<VivariumProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")
            .Produces<VivariumProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")
            .Produces<VivariumProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")
            .Produces<VivariumProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json");
        return endpoints;
    }

    /// <summary>
    /// Maps object-scoped Agent payload reads and staged/Agent artifact writes. Root composition
    /// must replace the transitional broad handlers before calling this method; mapping both sets
    /// of handlers to the same route is invalid.
    /// </summary>
    public static IEndpointRouteBuilder MapObjectScopedBlobDataPlane(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPut(
                "/blobs/{sha256}",
                (Func<HttpContext, string, BlobAccessService, Task<IResult>>)PutBlobAsync)
            .ExcludeFromDescription();
        endpoints.MapGet(
                "/blobs/{sha256}",
                (Func<HttpContext, string, BlobAccessService, BlobStore, Task<IResult>>)GetBlobAsync)
            .ExcludeFromDescription();
        return endpoints;
    }

    private static async Task<IResult> GetBlobAsync(
        HttpContext context,
        string sha256,
        BlobAccessService service,
        BlobStore blobs)
    {
        var authentication = await AuthenticateBearerAsync(context, "blob-assignment-get");
        if (!authentication.IsAuthenticated || !IsAgent(authentication.Context.Principal))
        {
            return BearerAuthenticationFailure(context, authentication, "agent-blob.access");
        }

        try
        {
            var buildId = ParseSingleHeader(
                context,
                BuildIdHeader,
                256,
                "blob_build_id_required",
                "Specify exactly one X-Vivarium-Build-Id header.");
            var sessionId = ParseSingleHeader(
                context,
                SessionIdHeader,
                256,
                "blob_session_id_required",
                "Specify exactly one X-Vivarium-Session-Id header.");
            var request = new BlobAssignmentReadRequest(
                authentication.Context.Principal.ActorId,
                sessionId,
                buildId,
                sha256,
                context.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow());
            if (!await service.CanReadAssignmentAsync(
                    authentication.Context,
                    request,
                    context.RequestAborted))
            {
                return RestProblems.NotFound(context, "build-blob", Bound(sha256, 64));
            }

            var path = blobs.GetPath(sha256);
            if (path is null)
            {
                return RestProblems.Create(
                    context,
                    StatusCodes.Status503ServiceUnavailable,
                    "blob_content_unavailable",
                    "The assigned blob content is unavailable",
                    "The logical assignment exists but its content is not currently available.",
                    retryable: true);
            }

            context.Response.Headers.CacheControl = "private, no-store";
            return Results.File(path, "application/octet-stream", enableRangeProcessing: false);
        }
        catch (ManagementAuthorizationException)
        {
            return RestProblems.PermissionDenied(context, ManagementPermission.BlobRead.ToString());
        }
        catch (RestApiException exception)
        {
            return RestProblems.Create(context, exception);
        }
        catch (BlobAccessException exception)
        {
            return BlobProblem(context, exception);
        }
    }

    private static async Task<IResult> CreatePlanAsync(
        HttpContext context,
        BlobUploadPlanCreateRequest? request,
        BlobAccessService service)
    {
        var authentication = await RestAuthentication.AuthenticateManagementAsync(
            context,
            "rest-blob-upload-plan-create");
        if (!authentication.IsAuthorized)
        {
            return authentication.Failure!;
        }

        var idempotencyKey = ParseSingleHeader(
            context,
            IdempotencyHeader,
            256,
            "idempotency_key_required",
            "Specify one bounded Idempotency-Key for this upload plan.");
        if (request?.ProjectId is null || request.Blobs is null)
        {
            throw Validation(
                "blob_plan_body_invalid",
                "Request properties 'projectId' and 'blobs' are required.");
        }

        var items = new List<BlobDescriptor>(request.Blobs.Count);
        foreach (var item in request.Blobs)
        {
            if (item.Sha256 is null || item.Size is null)
            {
                throw Validation(
                    "blob_plan_item_invalid",
                    "Every blob item requires 'sha256' and 'size'.");
            }

            items.Add(new BlobDescriptor(item.Sha256, item.Size.Value));
        }

        try
        {
            var plan = await service.CreateUploadPlanAsync(
                authentication.Context!.WithRequestId(idempotencyKey),
                request.ProjectId,
                items,
                context.RequestAborted);
            context.Response.Headers.CacheControl = "private, no-store";
            return Results.Json(
                ToResource(plan),
                statusCode: StatusCodes.Status201Created);
        }
        catch (ManagementAuthorizationException)
        {
            return RestProblems.PermissionDenied(
                context,
                ManagementPermission.BlobDiscover.ToString(),
                new RestProblemTarget("project", Bound(request.ProjectId, 256)));
        }
        catch (BlobAccessException exception)
        {
            return BlobProblem(context, exception);
        }
    }

    private static async Task<IResult> PutBlobAsync(
        HttpContext context,
        string sha256,
        BlobAccessService service)
    {
        var stagingValues = context.Request.Headers[StagingIdHeader];
        if (stagingValues.Count > 0)
        {
            const string action = "blob-staging.upload";
            var target = HeaderAuditTarget(context, StagingIdHeader, 64);
            var authentication = await AuthenticateBearerAsync(context, "blob-staging-put");
            if (!authentication.IsAuthenticated)
            {
                await AuditAuthenticationFailureAsync(
                    service,
                    authentication,
                    action,
                    "blob-upload-plan",
                    target);
                return BearerAuthenticationFailure(
                    context,
                    authentication,
                    "management-api.access");
            }

            var authorizer = context.RequestServices.GetRequiredService<ManagementAuthorizer>();
            if (!authorizer.Allows(authentication.Context.Principal, ManagementPermission.PanelAccess) &&
                !authorizer.Allows(authentication.Context.Principal, ManagementPermission.BuildWatch))
            {
                await service.AuditDataPlaneMutationAsync(
                    authentication.Context,
                    action,
                    "blob-upload-plan",
                    target,
                    AuditOutcome.Denied,
                    "permission_denied");
                return RestProblems.PermissionDenied(context, "management-api.access");
            }

            string stagingId;
            try
            {
                stagingId = ParseSingleHeader(
                    context,
                    StagingIdHeader,
                    64,
                    "blob_staging_id_invalid",
                    "Specify exactly one bounded X-Vivarium-Blob-Staging-Id header.");
                _ = await service.UploadStagedAsync(
                    authentication.Context,
                    stagingId,
                    sha256,
                    context.Request.Body,
                    context.RequestAborted);
                return Results.NoContent();
            }
            catch (ManagementAuthorizationException)
            {
                return RestProblems.PermissionDenied(context, ManagementPermission.BlobWrite.ToString());
            }
            catch (RestApiException exception)
            {
                await service.AuditDataPlaneMutationAsync(
                    authentication.Context,
                    action,
                    "blob-upload-plan",
                    target,
                    AuditOutcome.Failed,
                    exception.Code);
                return RestProblems.Create(context, exception);
            }
            catch (BlobAccessException exception)
            {
                await service.AuditDataPlaneMutationAsync(
                    authentication.Context,
                    action,
                    "blob-upload-plan",
                    target,
                    exception.Failure == BlobAccessFailure.NotFound
                        ? AuditOutcome.Denied
                        : AuditOutcome.Failed,
                    exception.Code);
                return BlobProblem(context, exception);
            }
            catch (Exception) when (!context.RequestAborted.IsCancellationRequested)
            {
                await service.AuditDataPlaneMutationAsync(
                    authentication.Context,
                    action,
                    "blob-upload-plan",
                    target,
                    AuditOutcome.Failed,
                    "write_failed");
                return InternalBlobFailure(context);
            }
        }

        return await PutArtifactAsync(context, sha256, service);
    }

    private static async Task<IResult> PutArtifactAsync(
        HttpContext context,
        string sha256,
        BlobAccessService service)
    {
        const string action = "blob-artifact.upload";
        var target = HeaderAuditTarget(context, BuildIdHeader, 256);
        var authentication = await AuthenticateBearerAsync(context, "blob-artifact-put");
        if (!authentication.IsAuthenticated || !IsAgent(authentication.Context.Principal))
        {
            await AuditAuthenticationFailureAsync(
                service,
                authentication,
                action,
                "build",
                target,
                authentication.IsAuthenticated ? "permission_denied" : null);
            return BearerAuthenticationFailure(context, authentication, "agent-blob.access");
        }

        try
        {
            var buildId = ParseSingleHeader(
                context,
                BuildIdHeader,
                256,
                "blob_build_id_required",
                "Specify exactly one X-Vivarium-Build-Id header.");
            var sessionId = ParseSingleHeader(
                context,
                SessionIdHeader,
                256,
                "blob_session_id_required",
                "Specify exactly one X-Vivarium-Session-Id header.");
            var sizeText = ParseSingleHeader(
                context,
                DeclaredSizeHeader,
                32,
                "blob_declared_size_required",
                "Specify exactly one X-Vivarium-Blob-Declared-Size header.");
            if (!long.TryParse(sizeText, NumberStyles.None, CultureInfo.InvariantCulture, out var size) ||
                size < 0 || context.Request.ContentLength != size)
            {
                throw Validation(
                    "blob_declared_size_invalid",
                    "The declared size must equal the non-negative Content-Length.");
            }

            var upload = new BlobArtifactUploadRequest(
                authentication.Context.Principal.ActorId,
                sessionId,
                buildId,
                sha256,
                size,
                context.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow());
            _ = await service.UploadArtifactAsync(
                authentication.Context,
                upload,
                context.Request.Body,
                context.RequestAborted);
            return Results.NoContent();
        }
        catch (ManagementAuthorizationException)
        {
            return RestProblems.PermissionDenied(context, ManagementPermission.BlobWrite.ToString());
        }
        catch (RestApiException exception)
        {
            await service.AuditDataPlaneMutationAsync(
                authentication.Context,
                action,
                "build",
                target,
                AuditOutcome.Failed,
                exception.Code);
            return RestProblems.Create(context, exception);
        }
        catch (BlobAccessException exception)
        {
            await service.AuditDataPlaneMutationAsync(
                authentication.Context,
                action,
                "build",
                target,
                exception.Failure == BlobAccessFailure.NotFound
                    ? AuditOutcome.Denied
                    : AuditOutcome.Failed,
                exception.Code);
            return BlobProblem(context, exception);
        }
        catch (Exception) when (!context.RequestAborted.IsCancellationRequested)
        {
            await service.AuditDataPlaneMutationAsync(
                authentication.Context,
                action,
                "build",
                target,
                AuditOutcome.Failed,
                "write_failed");
            return InternalBlobFailure(context);
        }
    }

    private static async Task<BearerAuthentication> AuthenticateBearerAsync(
        HttpContext context,
        string source)
    {
        string correlationId;
        try
        {
            correlationId = ManagementIdentifiers.NormalizeCorrelationId(
                context.Request.Headers[ManagementRequestContextFactory.CorrelationHeader].ToString());
        }
        catch (ArgumentException)
        {
            var invalid = ManagementRequestContext.Anonymous(source);
            SetCorrelation(context, invalid.CorrelationId);
            return new BearerAuthentication(invalid, IsAuthenticated: false, InvalidCorrelation: true);
        }

        var contexts = context.RequestServices.GetRequiredService<ManagementRequestContextFactory>();
        var requestContext = await contexts.FromBearerAsync(
                context.Request.Headers.Authorization.ToString(),
                correlationId,
                requestId: null,
                source);
        requestContext ??= ManagementRequestContext.Anonymous(source, correlationId);
        SetCorrelation(context, requestContext.CorrelationId);
        return new BearerAuthentication(
            requestContext,
            requestContext.Principal != ManagementPrincipal.Anonymous,
            InvalidCorrelation: false);
    }

    private static IResult BearerAuthenticationFailure(
        HttpContext context,
        BearerAuthentication authentication,
        string permission) =>
        authentication.InvalidCorrelation
            ? RestProblems.Create(
                context,
                StatusCodes.Status400BadRequest,
                "invalid_correlation_id",
                "The correlation ID is invalid",
                "X-Correlation-ID is malformed.")
            : !authentication.IsAuthenticated
                ? RestProblems.AuthenticationRequired(context)
                : RestProblems.PermissionDenied(context, permission);

    private static Task AuditAuthenticationFailureAsync(
        BlobAccessService service,
        BearerAuthentication authentication,
        string action,
        string targetType,
        string targetId,
        string? authenticatedReason = null) =>
        service.AuditDataPlaneMutationAsync(
            authentication.Context,
            action,
            targetType,
            targetId,
            authentication.IsAuthenticated
                ? AuditOutcome.Denied
                : authentication.InvalidCorrelation
                    ? AuditOutcome.Failed
                    : AuditOutcome.Denied,
            authenticatedReason ?? (authentication.InvalidCorrelation
                ? "invalid_correlation_id"
                : "authentication_required"));

    private static bool IsAgent(ManagementPrincipal principal) =>
        principal.LegacyScope == BearerScope.Agent &&
        string.Equals(principal.ActorType, "agent", StringComparison.Ordinal);

    private static void SetCorrelation(HttpContext context, string correlationId)
    {
        RestCorrelation.Set(context, correlationId);
        context.Response.Headers[ManagementRequestContextFactory.CorrelationHeader] = correlationId;
    }

    private static string HeaderAuditTarget(HttpContext context, string name, int maximumLength)
    {
        var values = context.Request.Headers[name];
        return values.Count == 1 &&
               !string.IsNullOrWhiteSpace(values[0]) &&
               values[0]!.Length <= maximumLength &&
               !values[0]!.Any(char.IsControl)
            ? values[0]!
            : "(invalid)";
    }

    private static IResult InternalBlobFailure(HttpContext context) =>
        RestProblems.Create(
            context,
            StatusCodes.Status500InternalServerError,
            "blob_write_failed",
            "The blob write failed",
            "The controller could not complete the blob write.",
            retryable: true);

    private sealed record BearerAuthentication(
        ManagementRequestContext Context,
        bool IsAuthenticated,
        bool InvalidCorrelation);

    private static string ParseSingleHeader(
        HttpContext context,
        string name,
        int maximumLength,
        string code,
        string detail)
    {
        var values = context.Request.Headers[name];
        if (values.Count != 1 || string.IsNullOrWhiteSpace(values[0]) ||
            values[0]!.Length > maximumLength || values[0]!.Any(char.IsControl))
        {
            throw new RestApiException(
                StatusCodes.Status400BadRequest,
                code,
                "A required request header is invalid",
                detail);
        }

        return values[0]!;
    }

    private static BlobUploadPlanResource ToResource(BlobUploadPlan plan) =>
        new(
            plan.Id,
            plan.ProjectId,
            plan.ExpiresAt,
            plan.Items.Select(item => new BlobUploadPlanItemResource(
                item.Sha256,
                item.Size,
                item.UploadRequired,
                item.UploadUrl)).ToArray());

    private static IResult BlobProblem(HttpContext context, BlobAccessException exception) =>
        RestProblems.Create(
            context,
            exception.Failure switch
            {
                BlobAccessFailure.Validation => StatusCodes.Status422UnprocessableEntity,
                BlobAccessFailure.NotFound => StatusCodes.Status404NotFound,
                BlobAccessFailure.Expired => StatusCodes.Status410Gone,
                BlobAccessFailure.Conflict => StatusCodes.Status409Conflict,
                _ => StatusCodes.Status400BadRequest,
            },
            exception.Code,
            exception.Failure switch
            {
                BlobAccessFailure.NotFound => "The blob staging resource was not found",
                BlobAccessFailure.Expired => "The blob staging resource has expired",
                BlobAccessFailure.Conflict => "The blob request conflicts with existing state",
                _ => "The blob request is invalid",
            },
            exception.Message,
            retryable: false);

    private static RestApiException Validation(string code, string detail) =>
        new(
            StatusCodes.Status422UnprocessableEntity,
            code,
            "The blob request is invalid",
            detail);

    private static string Bound(string value, int maximum) =>
        value.Length <= maximum ? value : value[..maximum];
}
