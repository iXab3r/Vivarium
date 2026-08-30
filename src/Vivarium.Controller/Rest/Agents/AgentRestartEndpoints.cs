using Vivarium.Contracts.V1;
using Vivarium.Controller.Agents;
using Vivarium.Controller.Rest.Common;
using Vivarium.Controller.Security;

namespace Vivarium.Controller.Rest.Agents;

public sealed record AgentRestartRequest(
    string? Mode,
    string? Reason,
    int? TimeoutSeconds = null);

public sealed record AgentRestartOperationResource(
    string Id,
    string Url,
    string AgentId,
    string AgentUrl,
    string State,
    string Mode,
    string Reason,
    long RequestedConnectionGeneration,
    long? AcknowledgedConnectionGeneration,
    long? ObservedConnectionGeneration,
    DateTimeOffset Deadline,
    string? FailureCode,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt);

public static class AgentRestartEndpoints
{
    private const string IdempotencyHeader = "Idempotency-Key";

    public static IServiceCollection AddAgentRestartApi(this IServiceCollection services) => services;

    public static IEndpointRouteBuilder MapAgentRestartApi(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(
                "/api/v1/agents/{agentId}/restart-operations",
                (Func<HttpContext, string, AgentRestartRequest?, AgentRestartService, Task<IResult>>)
                    CreateAsync)
            .WithName("createAgentRestart")
            .WithTags("Agents")
            .WithSummary("Request a durable remote Agent restart")
            .WithDescription(
                "The operation succeeds only after a newer authenticated Agent connection " +
                "generation and a different Bootstrap child process are observed. " +
                "Idempotency-Key is required.")
            .Produces<AgentRestartOperationResource>(StatusCodes.Status202Accepted)
            .Produces<VivariumProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")
            .Produces<VivariumProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")
            .Produces<VivariumProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")
            .Produces<VivariumProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")
            .Produces<VivariumProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json");

        endpoints.MapGet(
                "/api/v1/agent-restart-operations/{operationId}",
                (Func<HttpContext, string, AgentRestartService, Task<IResult>>)GetAsync)
            .WithName("getAgentRestart")
            .WithTags("Agents")
            .WithSummary("Read one durable Agent restart operation")
            .Produces<AgentRestartOperationResource>()
            .Produces<VivariumProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json");
        return endpoints;
    }

    private static async Task<IResult> CreateAsync(
        HttpContext context,
        string agentId,
        AgentRestartRequest? request,
        AgentRestartService restarts)
    {
        var authentication = await RestAuthentication.AuthenticateManagementAsync(
            context, "rest-agent-restart-create");
        if (!authentication.IsAuthorized)
        {
            return authentication.Failure!;
        }

        var requestId = context.Request.Headers[IdempotencyHeader].ToString().Trim();
        var mode = ParseMode(request?.Mode);
        if (request is null || requestId.Length is < 1 or > 256 ||
            mode == AgentRestartMode.Unspecified ||
            string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 512 ||
            request.Reason.Any(character => character is '\r' or '\n' or '\0') ||
            request.TimeoutSeconds is < 5 or > 3600)
        {
            return RestProblems.Create(
                context,
                StatusCodes.Status422UnprocessableEntity,
                "agent_restart_request_invalid",
                "The Agent restart request is invalid",
                "Supply Idempotency-Key, mode (after-current-work, cancel-then-restart, or force), " +
                "a 1-512 character reason, and optional timeoutSeconds between 5 and 3600.");
        }

        try
        {
            var operation = await restarts.CreateAsync(
                authentication.Context!.WithRequestId(requestId),
                agentId,
                mode,
                request.Reason.Trim(),
                TimeSpan.FromSeconds(request.TimeoutSeconds ?? 120));
            context.Response.Headers.Location =
                $"/api/v1/agent-restart-operations/{operation.OperationId}";
            return Results.Json(ToResource(operation), statusCode: StatusCodes.Status202Accepted);
        }
        catch (ManagementAuthorizationException)
        {
            return RestProblems.PermissionDenied(
                context,
                ManagementPermission.AgentManage.ToString(),
                new RestProblemTarget("agent", agentId));
        }
        catch (AgentRestartUnavailableException exception)
        {
            return RestProblems.Create(
                context,
                StatusCodes.Status409Conflict,
                exception.Reason,
                "The Agent cannot currently accept a restart",
                "The Agent must have a live authenticated control session and a supervised " +
                "process-instance identity.");
        }
        catch (AgentRestartAlreadyActiveException)
        {
            return RestProblems.Create(
                context,
                StatusCodes.Status409Conflict,
                "agent_restart_already_active",
                "An Agent restart is already active",
                "Wait for the current operation to finish before creating another.");
        }
        catch (AgentRestartRequestConflictException)
        {
            return RestProblems.Create(
                context,
                StatusCodes.Status409Conflict,
                "idempotency_key_reused",
                "The Idempotency-Key was already used",
                "Use the same key only for the exact same restart request.");
        }
    }

    private static async Task<IResult> GetAsync(
        HttpContext context,
        string operationId,
        AgentRestartService restarts)
    {
        var authentication = await RestAuthentication.AuthorizeAsync(
            context, ManagementPermission.AgentList, "rest-agent-restart-read");
        if (!authentication.IsAuthorized)
        {
            return authentication.Failure!;
        }

        var operation = await restarts.FindAsync(operationId);
        return operation is null
            ? RestProblems.NotFound(context, "agent-restart-operation", operationId)
            : Results.Json(ToResource(operation));
    }

    private static AgentRestartMode ParseMode(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "after-current-work" => AgentRestartMode.AfterCurrentWork,
        "cancel-then-restart" => AgentRestartMode.CancelThenRestart,
        "force" => AgentRestartMode.Force,
        _ => AgentRestartMode.Unspecified,
    };

    private static AgentRestartOperationResource ToResource(AgentRestartOperation operation) => new(
        operation.OperationId,
        $"/api/v1/agent-restart-operations/{operation.OperationId}",
        operation.AgentId,
        $"/api/v1/agents/{Uri.EscapeDataString(operation.AgentId)}",
        operation.State.ToString().ToLowerInvariant(),
        AgentRestartStore.ModeValue(operation.Mode).ToLowerInvariant().Replace('_', '-'),
        operation.Reason,
        operation.RequestedConnectionGeneration,
        operation.AcknowledgedConnectionGeneration,
        operation.ObservedConnectionGeneration,
        operation.Deadline,
        string.IsNullOrEmpty(operation.FailureCode) ? null : operation.FailureCode,
        operation.CreatedAt,
        operation.UpdatedAt,
        operation.CompletedAt);
}
