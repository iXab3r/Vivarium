using Vivarium.Controller.Configuration.Agents;
using Vivarium.Controller.Configuration.Git;
using Vivarium.Controller.Rest.Common;
using Vivarium.Controller.Security;

namespace Vivarium.Controller.Rest.Agents.Configuration;

public static class AgentDesiredConfigurationEndpoints
{
    public const string IdempotencyHeader = "Idempotency-Key";

    public static IServiceCollection AddAgentDesiredConfigurationRestApi(
        this IServiceCollection services)
    {
        services.AddAgentDesiredConfiguration();
        return services;
    }

    public static IEndpointRouteBuilder MapAgentDesiredConfigurationRestApi(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
                "/api/v1/agents/{agentId}/settings",
                (Func<HttpContext, string, AgentDesiredConfigurationService, Task<IResult>>)GetAsync)
            .WithName("getAgentSettings")
            .WithTags("Agents")
            .WithSummary("Read one Agent's desired settings")
            .WithDescription(
                "Returns Git-authoritative desired Agent settings, last-known-good applied state, " +
                "and a strong configuration ETag independent of live observations.")
            .Produces<AgentDesiredConfigurationResource>()
            .Produces(StatusCodes.Status304NotModified)
            .Produces<VivariumProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")
            .Produces<VivariumProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")
            .Produces<VivariumProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")
            .Produces<AgentConfigurationProblemDetails>(StatusCodes.Status503ServiceUnavailable, "application/problem+json");

        endpoints.MapPut(
                "/api/v1/agents/{agentId}/settings",
                (Func<HttpContext, string, AgentDesiredConfigurationUpdateRequest?,
                    AgentDesiredConfigurationService, Task<IResult>>)PutAsync)
            .WithName("putAgentSettings")
            .WithTags("Agents")
            .WithSummary("Commit and reconcile one Agent's desired settings")
            .WithDescription(
                "Requires a strong configuration If-Match and Idempotency-Key. The validated Git " +
                "commit becomes active before success is returned.")
            .Produces<AgentDesiredConfigurationChangeResource>()
            .Produces<VivariumProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")
            .Produces<VivariumProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")
            .Produces<VivariumProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")
            .Produces<VivariumProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")
            .Produces<VivariumProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")
            .Produces<AgentConfigurationProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")
            .Produces<AgentConfigurationProblemDetails>(StatusCodes.Status412PreconditionFailed, "application/problem+json")
            .Produces<VivariumProblemDetails>(StatusCodes.Status428PreconditionRequired, "application/problem+json")
            .Produces<AgentConfigurationProblemDetails>(StatusCodes.Status503ServiceUnavailable, "application/problem+json");
        return endpoints;
    }

    private static async Task<IResult> GetAsync(
        HttpContext context,
        string agentId,
        AgentDesiredConfigurationService service)
    {
        var authentication = await RestAuthentication.AuthorizeAsync(
            context,
            ManagementPermission.AgentList,
            "rest-agent-settings-read",
            new RestProblemTarget("agent", agentId));
        if (!authentication.IsAuthorized)
        {
            return authentication.Failure!;
        }

        try
        {
            var snapshot = await service.GetAsync(agentId, context.RequestAborted);
            if (snapshot is null)
            {
                return RestProblems.NotFound(context, "agent", agentId);
            }

            var etag = AgentConfigurationEtags.Create(snapshot.AuthoritativeRevision);
            context.Response.Headers.ETag = etag;
            context.Response.Headers.CacheControl = "private, no-cache";
            if (MatchesIfNoneMatch(context.Request, etag))
            {
                return Results.StatusCode(StatusCodes.Status304NotModified);
            }

            return Results.Json(ToResource(snapshot));
        }
        catch (AgentDesiredConfigurationValidationException)
        {
            return RestProblems.NotFound(context, "agent", agentId);
        }
        catch (ConfigurationRepositoryException exception)
        {
            return ConfigurationProblem(
                context,
                StatusCodes.Status503ServiceUnavailable,
                SafeCode(exception.Code),
                "The configuration repository is temporarily unavailable.",
                current: null,
                applied: null,
                Errors: []);
        }
    }

    private static async Task<IResult> PutAsync(
        HttpContext context,
        string agentId,
        AgentDesiredConfigurationUpdateRequest? request,
        AgentDesiredConfigurationService service)
    {
        var authentication = await RestAuthentication.AuthenticateManagementAsync(
            context,
            "rest-agent-settings-write");
        if (!authentication.IsAuthorized)
        {
            return authentication.Failure!;
        }

        var idempotencyKey = ParseIdempotencyKey(context);
        var expectedBase = ParseIfMatch(context);
        if (request?.Enabled is null)
        {
            throw new RestApiException(
                StatusCodes.Status422UnprocessableEntity,
                "agent_enabled_required",
                "The enabled setting is required",
                "Request property 'enabled' must be an explicit boolean.");
        }

        var commandContext = authentication.Context!.WithRequestId(idempotencyKey);
        try
        {
            var result = await service.SetEnabledAsync(
                commandContext,
                agentId,
                request.Enabled.Value,
                expectedBase,
                context.RequestAborted);
            var resource = ToResource(result, expectedBase);
            context.Response.Headers.ETag = AgentConfigurationEtags.Create(
                result.Settings.AuthoritativeRevision);
            context.Response.Headers.CacheControl = "private, no-cache";
            return Results.Json(resource);
        }
        catch (ManagementAuthorizationException)
        {
            return RestProblems.PermissionDenied(
                context,
                ManagementPermission.AgentManage.ToString(),
                new RestProblemTarget("agent", agentId));
        }
        catch (AgentDesiredConfigurationNotFoundException)
        {
            return RestProblems.NotFound(context, "agent", agentId);
        }
        catch (AgentDesiredConfigurationValidationException exception)
        {
            return ConfigurationProblem(
                context,
                StatusCodes.Status409Conflict,
                exception.Code,
                exception.Message,
                current: null,
                applied: null,
                exception.Diagnostics.Select(ToDiagnostic).ToArray());
        }
        catch (AgentDesiredConfigurationPreconditionException exception)
        {
            return ConfigurationProblem(
                context,
                StatusCodes.Status412PreconditionFailed,
                "configuration_revision_stale",
                exception.Message,
                exception.CurrentRevision,
                applied: null,
                ErrorsFromDiff(exception.Diff));
        }
        catch (AgentDesiredConfigurationConflictException exception)
        {
            return ConfigurationProblem(
                context,
                StatusCodes.Status409Conflict,
                SafeCode(exception.Code),
                exception.Message,
                exception.CurrentRevision,
                exception.AppliedRevision,
                exception.Diagnostics.Select(ToDiagnostic).ToArray());
        }
        catch (ConfigurationRepositoryException exception)
        {
            return ConfigurationProblem(
                context,
                StatusCodes.Status503ServiceUnavailable,
                SafeCode(exception.Code),
                "The configuration repository is temporarily unavailable.",
                current: null,
                applied: null,
                Errors: []);
        }
    }

    private static string ParseIdempotencyKey(HttpContext context)
    {
        var values = context.Request.Headers[IdempotencyHeader];
        if (values.Count != 1 || string.IsNullOrWhiteSpace(values[0]))
        {
            throw new RestApiException(
                StatusCodes.Status400BadRequest,
                "idempotency_key_required",
                "An Idempotency-Key is required",
                "Specify one Idempotency-Key for this Agent settings mutation.");
        }

        var value = values[0]!;
        if (value.Length > 256 || value.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or ':' or '@' or '/' or '-')))
        {
            throw new RestApiException(
                StatusCodes.Status400BadRequest,
                "idempotency_key_invalid",
                "The Idempotency-Key is invalid",
                "Idempotency-Key must contain 1-256 ASCII letters, digits, '.', '_', ':', '@', '/', or '-'.");
        }

        return value;
    }

    private static ConfigurationRevision ParseIfMatch(HttpContext context)
    {
        var values = context.Request.Headers.IfMatch;
        if (values.Count == 0)
        {
            throw new RestApiException(
                StatusCodes.Status428PreconditionRequired,
                "configuration_precondition_required",
                "A configuration precondition is required",
                "Specify the strong ETag returned by GET /api/v1/agents/{agentId}/settings.");
        }

        if (values.Count != 1 || values[0]!.Contains(',') ||
            !AgentConfigurationEtags.TryParse(values[0]!, out var revision))
        {
            throw new RestApiException(
                StatusCodes.Status400BadRequest,
                "configuration_precondition_invalid",
                "The configuration precondition is invalid",
                "If-Match must contain exactly one strong Agent configuration ETag.");
        }

        return revision!;
    }

    private static bool MatchesIfNoneMatch(HttpRequest request, string etag) =>
        request.Headers.IfNoneMatch.Any(header => header!
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(candidate => candidate == "*" || string.Equals(candidate, etag, StringComparison.Ordinal)));

    private static AgentDesiredConfigurationResource ToResource(
        AgentDesiredConfigurationSnapshot snapshot) => new(
        snapshot.AgentId,
        $"/api/v1/agents/{Uri.EscapeDataString(snapshot.AgentId)}/settings",
        snapshot.DesiredEnabled,
        snapshot.AppliedEnabled,
        ToRestRevision(snapshot.AuthoritativeRevision),
        snapshot.AppliedRevision is null ? null : ToRestRevision(snapshot.AppliedRevision),
        snapshot.State.ToString().ToLowerInvariant(),
        snapshot.Diagnostics.Select(ToDiagnostic).ToArray());

    private static AgentDesiredConfigurationChangeResource ToResource(
        AgentDesiredConfigurationMutationResult result,
        ConfigurationRevision expectedBase) => new(
        result.OperationId,
        result.Settings.State == AgentDesiredConfigurationState.Active ? "applied" : "committed",
        ToRestRevision(expectedBase),
        ToRestRevision(result.ResultRevision),
        result.Settings.AppliedRevision is null
            ? null
            : ToRestRevision(result.Settings.AppliedRevision),
        result.Diff.Select(diff => new AgentConfigurationDiffResource(
            diff.Path,
            diff.ChangeKind.ToString().ToLowerInvariant(),
            diff.PreviousContentHash,
            diff.ResultContentHash)).ToArray(),
        result.Replayed,
        ToResource(result.Settings));

    private static AgentConfigurationDiagnosticResource ToDiagnostic(
        ConfigurationValidationDiagnostic diagnostic) => new(
        SafeCode(diagnostic.Code),
        Bound(diagnostic.Path, 512),
        Bound(diagnostic.Field, 128),
        Bound(diagnostic.Summary, 512) ?? "Configuration validation failed.");

    private static IReadOnlyList<AgentConfigurationDiagnosticResource> ErrorsFromDiff(
        IReadOnlyList<ConfigurationPathDiff> diff) => diff.Select(item =>
        new AgentConfigurationDiagnosticResource(
            "changed",
            Bound(item.Path, 512),
            Field: null,
            "The configuration path changed after the supplied base revision."))
        .ToArray();

    private static IResult ConfigurationProblem(
        HttpContext context,
        int status,
        string code,
        string detail,
        ConfigurationRevision? current,
        ConfigurationRevision? applied,
        IReadOnlyList<AgentConfigurationDiagnosticResource> Errors)
    {
        var safeCode = SafeCode(code);
        return Results.Json(
            new AgentConfigurationProblemDetails
            {
                Type = $"https://vivarium.dev/problems/{safeCode.Replace('_', '-')}",
                Title = status switch
                {
                    StatusCodes.Status412PreconditionFailed => "The configuration revision is stale",
                    StatusCodes.Status503ServiceUnavailable => "The configuration repository is unavailable",
                    _ => "The Agent configuration change conflicts",
                },
                Status = status,
                Detail = Bound(detail, 512) ?? "The Agent configuration change failed.",
                Instance = context.Request.Path,
                Code = safeCode,
                CorrelationId = RestCorrelation.Get(context),
                Retryable = status == StatusCodes.Status503ServiceUnavailable,
                CurrentConfigurationRevision = current is null ? null : ToRestRevision(current),
                AppliedConfigurationRevision = applied is null ? null : ToRestRevision(applied),
                Errors = Errors.Count == 0 ? null : Errors.Take(64).ToArray(),
            },
            statusCode: status,
            contentType: "application/problem+json");
    }

    private static string ToRestRevision(ConfigurationRevision revision) => $"git:{revision.Commit}";

    private static string SafeCode(string value)
    {
        var normalized = new string(value.ToLowerInvariant().Take(128).Select(character =>
            char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character == '_'
                ? character
                : '_').ToArray());
        return string.IsNullOrWhiteSpace(normalized) ? "configuration_conflict" : normalized;
    }

    private static string? Bound(string? value, int maximum) => value is null
        ? null
        : new string(value.Take(maximum)
            .Select(character => char.IsControl(character) ? '?' : character)
            .ToArray());
}
