using Vivarium.Controller.Administration;
using Vivarium.Controller.Configuration.Git;
using Vivarium.Controller.Rest.Common;

namespace Vivarium.Controller.Rest.Administration;

public static class AdministrationSetupEndpoints
{
    private const string SetupAuthorizationScheme = "Vivarium-Setup";
    private const string IdempotencyHeader = "Idempotency-Key";

    public static IServiceCollection AddAdministrationSetupApi(this IServiceCollection services) =>
        services;

    public static IEndpointRouteBuilder MapAdministrationSetupApi(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
                "/api/v1/setup/status",
                (Func<AdministrationBootstrapService, Task<IResult>>)GetStatusAsync)
            .WithName("getSetupStatus")
            .WithTags("Administration setup")
            .WithSummary("Read the non-sensitive controller setup state")
            .WithDescription(
                "Anonymous readiness resource. It never returns token values, user identity, " +
                "filesystem paths, or repository credentials.")
            .Produces<SetupStatusResource>();

        endpoints.MapPost(
                "/api/v1/setup/claims",
                (Func<HttpContext, SetupClaimRequest?, AdministrationBootstrapService, Task<IResult>>)ClaimAsync)
            .WithName("claimSetup")
            .WithTags("Administration setup")
            .WithSummary("Exchange one local first-run proof")
            .WithDescription(
                "Consumes a purpose-bound bootstrap or operation-resume value and returns one bounded " +
                "setup-only session. The value is never accepted by normal management APIs.")
            .Produces<SetupClaimResource>(StatusCodes.Status201Created)
            .Produces<VivariumProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")
            .Produces<VivariumProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json");

        endpoints.MapPost(
                "/api/v1/recovery/claims",
                (Func<HttpContext, RecoveryClaimRequest?, AdministrationBootstrapService, Task<IResult>>)ClaimRecoveryAsync)
            .WithName("claimRecovery")
            .WithTags("Administration recovery")
            .WithSummary("Exchange one host-issued recovery proof")
            .WithDescription(
                "Consumes a purpose-bound recovery value and returns one bounded recovery session. " +
                "The unexchanged value is never accepted by normal management APIs.")
            .Produces<RecoveryClaimResource>()
            .Produces<VivariumProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")
            .Produces<VivariumProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json");

        endpoints.MapGet(
                "/api/v1/setup/operations/{operationId}",
                (Func<HttpContext, string, AdministrationBootstrapService, Task<IResult>>)GetOperationAsync)
            .WithName("getSetupOperation")
            .WithTags("Administration setup")
            .WithSummary("Read one durable first-run setup operation")
            .Produces<SetupOperationResource>()
            .Produces<VivariumProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")
            .Produces<VivariumProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json");

        endpoints.MapPut(
                "/api/v1/setup/administrator",
                (Func<HttpContext, SetupAdministratorRequest?, AdministrationBootstrapService, Task<IResult>>)PutAdministratorAsync)
            .WithName("putSetupAdministrator")
            .WithTags("Administration setup")
            .WithSummary("Reserve the first durable administrator identity")
            .WithDescription(
                "Stores only a password verifier in private runtime state. The request requires a " +
                "setup-only session, operation state version, and Idempotency-Key.")
            .Produces<SetupAdministratorResource>()
            .Produces<VivariumProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")
            .Produces<VivariumProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")
            .Produces<VivariumProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")
            .Produces<VivariumProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json");

        endpoints.MapPut(
                "/api/v1/setup/config-repository",
                (Func<HttpContext, SetupRepositoryRequest?, AdministrationBootstrapService, Task<IResult>>)PutRepositoryAsync)
            .WithName("putSetupConfigRepository")
            .WithTags("Administration setup")
            .WithSummary("Validate and bind the managed-local setup repository")
            .Produces<SetupRepositoryChangeResource>()
            .Produces<VivariumProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")
            .Produces<VivariumProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")
            .Produces<VivariumProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")
            .Produces<VivariumProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json");

        endpoints.MapGet(
                "/api/v1/setup/changes",
                (Func<HttpContext, AdministrationBootstrapService, IConfigurationRepository, Task<IResult>>)GetChangesAsync)
            .WithName("getSetupChanges")
            .WithTags("Administration setup")
            .WithSummary("Inspect the setup repository baseline validation")
            .Produces<SetupChangesResource>()
            .Produces<VivariumProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")
            .Produces<VivariumProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json");

        endpoints.MapPost(
                "/api/v1/setup/completion",
                (Func<HttpContext, SetupCompletionRequest?, AdministrationBootstrapService, Task<IResult>>)CompleteAsync)
            .WithName("completeSetup")
            .WithTags("Administration setup")
            .WithSummary("Commit and activate the first administrator")
            .WithDescription(
                "Atomically commits the User and System Administrator binding to Git, reconciles " +
                "that exact revision, then activates the private credential.")
            .Produces<SetupCompletionResource>()
            .Produces<VivariumProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")
            .Produces<VivariumProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")
            .Produces<VivariumProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")
            .Produces<VivariumProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json");
        return endpoints;
    }

    private static async Task<IResult> GetStatusAsync(AdministrationBootstrapService service)
    {
        var status = await service.GetStatusAsync();
        return Results.Json(new SetupStatusResource(
            "/api/v1/setup/status",
            status.InstanceId,
            State(status.State),
            status.StateVersion,
            status.SetupOperationId is null
                ? null
                : OperationUrl(status.SetupOperationId),
            status.TokenDeliveryHint,
            status.UpdatedAt));
    }

    private static async Task<IResult> ClaimAsync(
        HttpContext context,
        SetupClaimRequest? request,
        AdministrationBootstrapService service)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (string.IsNullOrWhiteSpace(request?.Token))
        {
            return RestProblems.Create(
                context,
                StatusCodes.Status401Unauthorized,
                "setup_claim_invalid",
                "The setup claim is invalid",
                "The setup claim is invalid or expired.");
        }

        try
        {
            var result = await service.ClaimAsync(
                request.Token,
                RestCorrelation.Get(context),
                "rest-setup-claim",
                context.RequestAborted);
            var resource = new SetupClaimResource(
                result.OperationId,
                OperationUrl(result.OperationId),
                result.SessionToken,
                result.SessionExpiresAt,
                result.StateVersion,
                result.Resumed);
            return Results.Json(
                resource,
                statusCode: result.Resumed
                    ? StatusCodes.Status200OK
                    : StatusCodes.Status201Created);
        }
        catch (AdministrationBootstrapException exception)
        {
            return SetupProblem(context, exception);
        }
    }

    private static async Task<IResult> ClaimRecoveryAsync(
        HttpContext context,
        RecoveryClaimRequest? request,
        AdministrationBootstrapService service)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (string.IsNullOrWhiteSpace(request?.Token))
        {
            return RestProblems.Create(
                context,
                StatusCodes.Status401Unauthorized,
                "recovery_claim_invalid",
                "The recovery claim is invalid",
                "The recovery claim is invalid or expired.");
        }

        try
        {
            var result = await service.ExchangeRecoveryAsync(
                request.Token,
                RestCorrelation.Get(context),
                "rest-recovery-claim",
                context.RequestAborted);
            return Results.Json(new RecoveryClaimResource(
                result.OperationId,
                result.SessionToken,
                result.SessionExpiresAt));
        }
        catch (AdministrationBootstrapException exception)
        {
            return SetupProblem(context, exception);
        }
    }

    private static async Task<IResult> GetOperationAsync(
        HttpContext context,
        string operationId,
        AdministrationBootstrapService service)
    {
        var session = await AuthenticateAsync(context, service);
        if (session is null)
        {
            return SetupAuthenticationRequired(context);
        }

        if (!string.Equals(session.OperationId, operationId, StringComparison.Ordinal))
        {
            return RestProblems.NotFound(context, "setup-operation", operationId);
        }

        return Results.Json(ToResource(session.Operation));
    }

    private static async Task<IResult> PutAdministratorAsync(
        HttpContext context,
        SetupAdministratorRequest? request,
        AdministrationBootstrapService service)
    {
        context.Response.Headers.CacheControl = "no-store";
        var session = await AuthenticateAsync(context, service);
        if (session is null)
        {
            return SetupAuthenticationRequired(context);
        }

        if (request?.StateVersion is null || string.IsNullOrWhiteSpace(request.Login) ||
            string.IsNullOrWhiteSpace(request.DisplayName) || request.Password is null)
        {
            return RestProblems.Create(
                context,
                StatusCodes.Status422UnprocessableEntity,
                "setup_administrator_required",
                "Administrator details are required",
                "Specify stateVersion, login, displayName, and password.");
        }

        try
        {
            var result = await service.ReserveAdministratorAsync(
                session,
                IdempotencyKey(context),
                request.StateVersion.Value,
                request.Login,
                request.DisplayName,
                request.Password,
                RestCorrelation.Get(context),
                context.RequestAborted);
            return Results.Json(new SetupAdministratorResource(
                result.OperationId,
                result.UserId,
                result.Login,
                result.DisplayName,
                result.StateVersion,
                result.Replayed));
        }
        catch (AdministrationBootstrapException exception)
        {
            return SetupProblem(context, exception);
        }
    }

    private static async Task<IResult> PutRepositoryAsync(
        HttpContext context,
        SetupRepositoryRequest? request,
        AdministrationBootstrapService service)
    {
        var session = await AuthenticateAsync(context, service);
        if (session is null)
        {
            return SetupAuthenticationRequired(context);
        }

        if (request?.StateVersion is null ||
            !string.Equals(request.Mode, "managed-local", StringComparison.Ordinal))
        {
            return RestProblems.Create(
                context,
                StatusCodes.Status422UnprocessableEntity,
                "setup_repository_mode_invalid",
                "The setup repository mode is invalid",
                "The current setup slice supports only the explicit 'managed-local' mode.");
        }

        try
        {
            var result = await service.ConfigureManagedLocalRepositoryAsync(
                session,
                IdempotencyKey(context),
                request.StateVersion.Value,
                RestCorrelation.Get(context),
                context.RequestAborted);
            return Results.Json(new SetupRepositoryChangeResource(
                result.OperationId,
                new SetupRepositoryResource(
                    result.RepositoryMode,
                    result.RepositoryId,
                    result.ExpectedBaseCommit),
                result.StateVersion,
                result.Replayed));
        }
        catch (AdministrationBootstrapException exception)
        {
            return SetupProblem(context, exception);
        }
    }

    private static async Task<IResult> GetChangesAsync(
        HttpContext context,
        AdministrationBootstrapService service,
        IConfigurationRepository repository)
    {
        var session = await AuthenticateAsync(context, service);
        if (session is null)
        {
            return SetupAuthenticationRequired(context);
        }

        if (session.Operation.RepositoryId is null ||
            session.Operation.ExpectedBaseCommit is null ||
            session.Operation.RepositoryMode is null)
        {
            return RestProblems.Create(
                context,
                StatusCodes.Status409Conflict,
                "setup_repository_required",
                "A setup repository is required",
                "Configure the setup repository before inspecting its baseline.");
        }

        var revision = new ConfigurationRevision(
            session.Operation.RepositoryId,
            session.Operation.ExpectedBaseCommit);
        var validation = await repository.ValidateRevisionAsync(revision, context.RequestAborted);
        return Results.Json(new SetupChangesResource(
            session.OperationId,
            new SetupRepositoryResource(
                session.Operation.RepositoryMode,
                session.Operation.RepositoryId,
                session.Operation.ExpectedBaseCommit),
            validation.IsValid,
            validation.Diagnostics.Select(diagnostic => new SetupValidationErrorResource(
                Safe(diagnostic.Code, 128),
                SafeNullable(diagnostic.Path, 512),
                SafeNullable(diagnostic.Field, 128),
                Safe(diagnostic.Summary, 512))).ToArray()));
    }

    private static async Task<IResult> CompleteAsync(
        HttpContext context,
        SetupCompletionRequest? request,
        AdministrationBootstrapService service)
    {
        context.Response.Headers.CacheControl = "no-store";
        var session = await AuthenticateAsync(context, service);
        if (session is null)
        {
            return SetupAuthenticationRequired(context);
        }

        if (request?.StateVersion is null)
        {
            return RestProblems.Create(
                context,
                StatusCodes.Status422UnprocessableEntity,
                "setup_state_version_required",
                "The setup state version is required",
                "Refresh the setup operation and provide its stateVersion.");
        }

        try
        {
            var result = await service.CompleteSetupAsync(
                session,
                IdempotencyKey(context),
                request.StateVersion.Value,
                RestCorrelation.Get(context),
                context.RequestAborted);
            return Results.Json(new SetupCompletionResource(
                result.OperationId,
                result.UserId,
                result.RepositoryId,
                result.Commit,
                result.StateVersion,
                result.Active));
        }
        catch (AdministrationBootstrapException exception)
        {
            return SetupProblem(context, exception);
        }
    }

    private static async Task<SetupSessionAuthentication?> AuthenticateAsync(
        HttpContext context,
        AdministrationBootstrapService service)
    {
        var value = context.Request.Headers.Authorization.ToString();
        var prefix = SetupAuthorizationScheme + " ";
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            return await service.AuthenticateSetupSessionAsync(
                value[prefix.Length..].Trim(),
                context.RequestAborted);
        }
        catch (AdministrationBootstrapException)
        {
            return null;
        }
    }

    private static IResult SetupAuthenticationRequired(HttpContext context)
    {
        context.Response.Headers.WWWAuthenticate = SetupAuthorizationScheme;
        return RestProblems.Create(
            context,
            StatusCodes.Status401Unauthorized,
            "setup_authentication_required",
            "Setup authentication is required",
            "Use the bounded setup session returned by the claim exchange.");
    }

    private static IResult SetupProblem(
        HttpContext context,
        AdministrationBootstrapException exception)
    {
        var status = exception.Code switch
        {
            "setup_claim_invalid" or "setup_session_invalid" or "recovery_claim_invalid" =>
                StatusCodes.Status401Unauthorized,
            "setup_login_invalid" or "setup_display_name_invalid" or "setup_password_invalid" =>
                StatusCodes.Status422UnprocessableEntity,
            "idempotency_key_invalid" or "setup_request_invalid" => StatusCodes.Status400BadRequest,
            "setup_operation_not_found" => StatusCodes.Status404NotFound,
            _ => StatusCodes.Status409Conflict,
        };
        return RestProblems.Create(
            context,
            status,
            Safe(exception.Code, 128),
            "The setup request could not be completed",
            Safe(exception.Message, 512));
    }

    private static string IdempotencyKey(HttpContext context)
    {
        var values = context.Request.Headers[IdempotencyHeader];
        if (values.Count != 1 || string.IsNullOrWhiteSpace(values[0]))
        {
            throw new AdministrationBootstrapException(
                "idempotency_key_invalid",
                "Specify exactly one Idempotency-Key.");
        }

        return values[0]!;
    }

    private static SetupOperationResource ToResource(SetupOperationSnapshot operation) => new(
        operation.OperationId,
        OperationUrl(operation.OperationId),
        State(operation.State),
        operation.StateVersion,
        operation.PendingUserId is null
            ? null
            : new SetupPendingAdministratorResource(
                operation.PendingUserId,
                operation.PendingLogin!,
                operation.PendingDisplayName!),
        operation.RepositoryMode is null
            ? null
            : new SetupRepositoryResource(
                operation.RepositoryMode,
                operation.RepositoryId!,
                operation.ExpectedBaseCommit!),
        operation.CandidateCommit,
        string.IsNullOrWhiteSpace(operation.LastFailureCode)
            ? null
            : operation.LastFailureCode,
        operation.CreatedAt,
        operation.UpdatedAt);

    private static string OperationUrl(string operationId) =>
        $"/api/v1/setup/operations/{Uri.EscapeDataString(operationId)}";

    private static string State(AdministrationState state) => state switch
    {
        AdministrationState.Unclaimed => "unclaimed",
        AdministrationState.SetupInProgress => "setup-in-progress",
        AdministrationState.SetupWaitingForGit => "setup-waiting-for-git",
        AdministrationState.SetupActivating => "setup-activating",
        AdministrationState.Active => "active",
        AdministrationState.RecoveryAvailable => "recovery-available",
        AdministrationState.RecoveryInProgress => "recovery-in-progress",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    private static string State(SetupOperationState state) => state switch
    {
        SetupOperationState.InProgress => "in-progress",
        SetupOperationState.WaitingForGit => "waiting-for-git",
        SetupOperationState.Activating => "activating",
        SetupOperationState.Completed => "completed",
        SetupOperationState.Abandoned => "abandoned",
        SetupOperationState.Blocked => "blocked",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    private static string Safe(string value, int maximum) =>
        SafeNullable(value, maximum) ?? "setup request failed";

    private static string? SafeNullable(string? value, int maximum)
    {
        if (value is null)
        {
            return null;
        }

        return new string(value.Take(maximum)
            .Select(character => char.IsControl(character) ? '?' : character)
            .ToArray());
    }
}
