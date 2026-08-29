using Vivarium.Controller.Deployment;
using Vivarium.Controller.Rest.Common;
using Vivarium.Controller.Security;

namespace Vivarium.Controller.Rest.Deployment;

public static class AgentDeploymentEndpoints
{
    private const string IdempotencyHeader = "Idempotency-Key";
    private const string DigestHeader = "X-Content-SHA256";

    public static IServiceCollection AddAgentDeploymentApi(this IServiceCollection services) => services;

    public static IEndpointRouteBuilder MapAgentDeploymentApi(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
                "/api/v1/agent-packages",
                (Func<HttpContext, AgentPackageStore, Task<IResult>>)ListPackagesAsync)
            .WithName("listAgentPackages")
            .WithTags("Agent deployment")
            .WithSummary("List immutable Agent packages")
            .Produces<AgentPackageCollectionResource>()
            .Produces<VivariumProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")
            .Produces<VivariumProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json");

        endpoints.MapGet(
                "/api/v1/agent-packages/{packageId}",
                (Func<HttpContext, string, AgentPackageStore, Task<IResult>>)GetPackageAsync)
            .WithName("getAgentPackage")
            .WithTags("Agent deployment")
            .WithSummary("Read one immutable Agent package")
            .Produces<AgentPackageResource>()
            .Produces<VivariumProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")
            .Produces<VivariumProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")
            .Produces<VivariumProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json");

        endpoints.MapPut(
                "/api/v1/agent-packages/{rid}/{version}",
                (Func<HttpContext, string, string, AgentPackageStore, ControllerOptions, Task<IResult>>)PublishPackageAsync)
            .WithName("publishAgentPackage")
            .WithTags("Agent deployment")
            .WithSummary("Publish one immutable per-RID Agent package")
            .WithDescription(
                "The raw request body is a bounded ZIP. X-Content-SHA256 and Idempotency-Key are required.")
            .Accepts<byte[]>("application/zip")
            .Produces<AgentPackageResource>(StatusCodes.Status201Created)
            .Produces<AgentPackageResource>(StatusCodes.Status200OK)
            .Produces<VivariumProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")
            .Produces<VivariumProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")
            .Produces<VivariumProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")
            .Produces<VivariumProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")
            .Produces<VivariumProblemDetails>(StatusCodes.Status413PayloadTooLarge, "application/problem+json")
            .Produces<VivariumProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")
            .ExcludeFromDescription();

        endpoints.MapPost(
                "/api/v1/agents/{agentId}/upgrade-operations",
                (Func<HttpContext, string, AgentUpgradeRequest?, AgentUpgradeService, Task<IResult>>)CreateUpgradeAsync)
            .WithName("createAgentUpgrade")
            .WithTags("Agent deployment")
            .WithSummary("Safely update one Agent to the running Server release")
            .Produces<AgentUpgradeOperationResource>(StatusCodes.Status202Accepted)
            .Produces<VivariumProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")
            .Produces<VivariumProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")
            .Produces<VivariumProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")
            .Produces<VivariumProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")
            .Produces<VivariumProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")
            .Produces<VivariumProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")
            .Produces<VivariumProblemDetails>(StatusCodes.Status503ServiceUnavailable, "application/problem+json");

        endpoints.MapGet(
                "/api/v1/agent-upgrade-operations",
                (Func<HttpContext, AgentUpgradeService, Task<IResult>>)ListUpgradesAsync)
            .WithName("listAgentUpgrades")
            .WithTags("Agent deployment")
            .WithSummary("List durable Agent upgrade operations")
            .Produces<AgentUpgradeOperationCollectionResource>();

        endpoints.MapGet(
                "/api/v1/agent-upgrade-operations/{operationId}",
                (Func<HttpContext, string, AgentUpgradeService, Task<IResult>>)GetUpgradeAsync)
            .WithName("getAgentUpgrade")
            .WithTags("Agent deployment")
            .WithSummary("Read one durable Agent upgrade operation")
            .Produces<AgentUpgradeOperationResource>()
            .Produces<VivariumProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json");

        endpoints.MapPut(
                "/api/v1/agent-upgrade-operations/{operationId}/cancellation",
                (Func<HttpContext, string, AgentUpgradeCancellationRequest?, AgentUpgradeService, Task<IResult>>)CancelUpgradeAsync)
            .WithName("cancelAgentUpgrade")
            .WithTags("Agent deployment")
            .WithSummary("Cancel before handoff or request a safe rollback after handoff")
            .Produces<AgentUpgradeOperationResource>();

        endpoints.MapGet(
                "/bootstrap/manifest",
                (Func<HttpContext, AgentUpgradeService, Task<IResult>>)GetBootstrapManifestAsync)
            .ExcludeFromDescription();
        endpoints.MapGet(
                "/bootstrap/packages/{sha256}",
                (Func<HttpContext, string, AgentUpgradeService, AgentPackageStore, Task<IResult>>)GetBootstrapPackageAsync)
            .ExcludeFromDescription();
        endpoints.MapPost(
                "/bootstrap/upgrade-failure",
                (Func<HttpContext, AgentUpgradeService, Task<IResult>>)ReportBootstrapFailureAsync)
            .ExcludeFromDescription();
        return endpoints;
    }

    private static async Task<IResult> ListPackagesAsync(
        HttpContext context,
        AgentPackageStore packages)
    {
        var authentication = await RestAuthentication.AuthorizeAsync(
            context, ManagementPermission.AgentPackageManage, "rest-agent-package-list");
        if (!authentication.IsAuthorized)
        {
            return authentication.Failure!;
        }

        return Results.Json(new AgentPackageCollectionResource(
            (await packages.ListAsync()).Select(ToResource).ToArray()));
    }

    private static async Task<IResult> GetPackageAsync(
        HttpContext context,
        string packageId,
        AgentPackageStore packages)
    {
        var authentication = await RestAuthentication.AuthorizeAsync(
            context, ManagementPermission.AgentPackageManage, "rest-agent-package-read");
        if (!authentication.IsAuthorized)
        {
            return authentication.Failure!;
        }

        var package = await packages.FindAsync(packageId);
        return package is null
            ? RestProblems.NotFound(context, "agent-package", packageId)
            : Results.Json(ToResource(package));
    }

    private static async Task<IResult> PublishPackageAsync(
        HttpContext context,
        string rid,
        string version,
        AgentPackageStore packages,
        ControllerOptions options)
    {
        if (!options.EnableDevelopmentAgentPackageApi)
        {
            return Results.NotFound();
        }

        var authentication = await RestAuthentication.AuthorizeAsync(
            context, ManagementPermission.AgentPackageManage, "rest-agent-package-publish");
        if (!authentication.IsAuthorized)
        {
            return authentication.Failure!;
        }

        var requestId = RequiredHeader(context, IdempotencyHeader);
        var digest = RequiredHeader(context, DigestHeader);
        if (requestId is null || digest is null)
        {
            return RestProblems.Create(
                context,
                StatusCodes.Status400BadRequest,
                "agent_package_headers_required",
                "Package publication headers are required",
                $"Supply both {IdempotencyHeader} and {DigestHeader}.");
        }

        if (context.Request.ContentLength is > AgentPackageStore.MaximumPackageSize)
        {
            return RestProblems.Create(
                context,
                StatusCodes.Status413PayloadTooLarge,
                "agent_package_too_large",
                "The Agent package is too large",
                $"Agent packages are limited to {AgentPackageStore.MaximumPackageSize} bytes.");
        }

        try
        {
            var publication = await packages.PublishDevelopmentAsync(
                authentication.Context!.WithRequestId(requestId),
                version,
                rid,
                context.Request.Body,
                digest,
                context.RequestAborted);
            context.Response.Headers.Location =
                $"/api/v1/agent-packages/{publication.Package.PackageId}";
            return Results.Json(
                ToResource(publication.Package),
                statusCode: publication.Replayed
                    ? StatusCodes.Status200OK
                    : StatusCodes.Status201Created);
        }
        catch (AgentPackageException exception)
        {
            return PackageProblem(context, exception);
        }
    }

    private static async Task<IResult> CreateUpgradeAsync(
        HttpContext context,
        string agentId,
        AgentUpgradeRequest? request,
        AgentUpgradeService upgrades)
    {
        var authentication = await RestAuthentication.AuthenticateManagementAsync(
            context, "rest-agent-upgrade-create");
        if (!authentication.IsAuthorized)
        {
            return authentication.Failure!;
        }

        var requestId = RequiredHeader(context, IdempotencyHeader);
        if (requestId is null || request?.Reason is null)
        {
            return RestProblems.Create(
                context,
                StatusCodes.Status422UnprocessableEntity,
                "agent_upgrade_request_invalid",
                "The Agent upgrade request is incomplete",
                "Supply Idempotency-Key and reason.");
        }

        try
        {
            var creation = await upgrades.CreateAsync(
                authentication.Context!,
                agentId,
                requestId,
                request.Reason,
                request.TimeoutSeconds is null
                    ? null
                    : TimeSpan.FromSeconds(request.TimeoutSeconds.Value),
                context.RequestAborted);
            context.Response.Headers.Location =
                $"/api/v1/agent-upgrade-operations/{creation.Operation.OperationId}";
            return Results.Json(
                ToResource(creation.Operation),
                statusCode: creation.Replayed
                    ? StatusCodes.Status200OK
                    : StatusCodes.Status202Accepted);
        }
        catch (ManagementAuthorizationException)
        {
            return RestProblems.PermissionDenied(
                context,
                ManagementPermission.AgentManage.ToString(),
                new RestProblemTarget("agent", agentId));
        }
        catch (AgentUpgradeException exception)
        {
            return UpgradeProblem(context, exception);
        }
    }

    private static async Task<IResult> ListUpgradesAsync(
        HttpContext context,
        AgentUpgradeService upgrades)
    {
        var authentication = await RestAuthentication.AuthorizeAsync(
            context, ManagementPermission.AgentList, "rest-agent-upgrade-list");
        if (!authentication.IsAuthorized)
        {
            return authentication.Failure!;
        }

        var agentId = context.Request.Query["agentId"].ToString();
        return Results.Json(new AgentUpgradeOperationCollectionResource(
            (await upgrades.ListAsync(string.IsNullOrWhiteSpace(agentId) ? null : agentId.Trim()))
            .Select(operation => ToResource(operation)).ToArray()));
    }

    private static async Task<IResult> GetUpgradeAsync(
        HttpContext context,
        string operationId,
        AgentUpgradeService upgrades)
    {
        var authentication = await RestAuthentication.AuthorizeAsync(
            context, ManagementPermission.AgentList, "rest-agent-upgrade-read");
        if (!authentication.IsAuthorized)
        {
            return authentication.Failure!;
        }

        var operation = await upgrades.FindAsync(operationId);
        return operation is null
            ? RestProblems.NotFound(context, "agent-upgrade", operationId)
            : Results.Json(ToResource(operation, await upgrades.ListEventsAsync(operationId)));
    }

    private static async Task<IResult> CancelUpgradeAsync(
        HttpContext context,
        string operationId,
        AgentUpgradeCancellationRequest? request,
        AgentUpgradeService upgrades)
    {
        var authentication = await RestAuthentication.AuthenticateManagementAsync(
            context, "rest-agent-upgrade-cancel");
        if (!authentication.IsAuthorized)
        {
            return authentication.Failure!;
        }

        try
        {
            await upgrades.CancelAndReleaseAsync(
                authentication.Context!,
                operationId,
                request?.Reason ?? "cancelled-by-operator",
                context.RequestAborted);
            var operation = await upgrades.FindAsync(operationId);
            return operation is null
                ? RestProblems.NotFound(context, "agent-upgrade", operationId)
                : Results.Json(ToResource(
                    operation,
                    await upgrades.ListEventsAsync(operationId)));
        }
        catch (ManagementAuthorizationException)
        {
            return RestProblems.PermissionDenied(context, ManagementPermission.AgentManage.ToString());
        }
        catch (AgentUpgradeException exception)
        {
            return UpgradeProblem(context, exception);
        }
    }

    private static async Task<IResult> GetBootstrapManifestAsync(
        HttpContext context,
        AgentUpgradeService upgrades)
    {
        var authentication = await RestAuthentication.AuthorizeAsync(
            context, ManagementPermission.AgentPackageRead, "bootstrap-agent-manifest");
        if (!authentication.IsAuthorized ||
            authentication.Context!.Principal.ActorType != "agent")
        {
            return Results.Unauthorized();
        }

        try
        {
            var requestedRid = AgentPackageRids.FromPlatform(
                context.Request.Query["os"].ToString(),
                context.Request.Query["arch"].ToString());
            var operation = await upgrades.FindActiveForAgentAsync(
                authentication.Context.Principal.ActorId);
            if (operation is null || operation.State == AgentUpgradeState.Draining)
            {
                return Results.NoContent();
            }

            if (upgrades.UtcNow >= operation.Deadline &&
                operation.State is not (
                    AgentUpgradeState.RollbackRequested or
                    AgentUpgradeState.Failed or
                    AgentUpgradeState.Succeeded or
                    AgentUpgradeState.RolledBack or
                    AgentUpgradeState.Cancelled))
            {
                await upgrades.TryAdvanceAsync(operation.OperationId, context.RequestAborted);
                operation = await upgrades.FindAsync(operation.OperationId);
                if (operation is null)
                {
                    return Results.NoContent();
                }
            }

            if (!string.Equals(operation.Package.Rid, requestedRid, StringComparison.Ordinal))
            {
                return Results.StatusCode(StatusCodes.Status409Conflict);
            }

            context.Response.Headers.CacheControl = "no-store";
            var rollback = operation.State == AgentUpgradeState.RollbackRequested;
            if (!rollback && operation.State is not (
                    AgentUpgradeState.HandoffReady or
                    AgentUpgradeState.AwaitingHealth or
                    AgentUpgradeState.CommitPending or
                    AgentUpgradeState.Finalizing))
            {
                return Results.NoContent();
            }

            var remainingSeconds = Math.Clamp(
                (int)Math.Floor((operation.Deadline - upgrades.UtcNow).TotalSeconds),
                30,
                120);
            return Results.Json(new BootstrapAgentManifest(
                2,
                rollback ? "rollback" : "activate",
                operation.OperationId,
                operation.Package.Version,
                operation.Package.Rid,
                operation.Package.Sha256,
                operation.PriorPackageSha256 ?? "",
                operation.Package.Size,
                rollback ? "" : $"/bootstrap/packages/{operation.Package.Sha256}",
                remainingSeconds,
                operation.Deadline.ToUnixTimeMilliseconds()));
        }
        catch (AgentPackageException)
        {
            return Results.BadRequest();
        }
    }

    private static async Task<IResult> GetBootstrapPackageAsync(
        HttpContext context,
        string sha256,
        AgentUpgradeService upgrades,
        AgentPackageStore packages)
    {
        var authentication = await RestAuthentication.AuthorizeAsync(
            context, ManagementPermission.AgentPackageRead, "bootstrap-agent-package");
        if (!authentication.IsAuthorized ||
            authentication.Context!.Principal.ActorType != "agent")
        {
            return Results.Unauthorized();
        }

        var operation = await upgrades.FindActiveForAgentAsync(
            authentication.Context.Principal.ActorId);
        if (operation is null ||
            operation.State is not (
                AgentUpgradeState.HandoffReady or
                AgentUpgradeState.AwaitingHealth or
                AgentUpgradeState.CommitPending or
                AgentUpgradeState.Finalizing) ||
            !string.Equals(operation.Package.Sha256, sha256, StringComparison.Ordinal))
        {
            return Results.NotFound();
        }

        var path = packages.ResolveContentPath(operation.Package);
        if (path is null)
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        context.Response.Headers.CacheControl = "private, no-store";
        return Results.File(
            path,
            "application/zip",
            enableRangeProcessing: false,
            lastModified: operation.Package.CreatedAt,
            entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue(
                $"\"sha256:{operation.Package.Sha256}\""));
    }

    private static async Task<IResult> ReportBootstrapFailureAsync(
        HttpContext context,
        AgentUpgradeService upgrades)
    {
        var authentication = await RestAuthentication.AuthorizeAsync(
            context, ManagementPermission.AgentPackageRead, "bootstrap-upgrade-failure");
        if (!authentication.IsAuthorized ||
            authentication.Context!.Principal.ActorType != "agent")
        {
            return Results.Unauthorized();
        }

        if (context.Request.ContentLength is < 1 or > 1024)
        {
            return Results.BadRequest();
        }
        var bytes = new byte[1025];
        var length = 0;
        while (length < bytes.Length)
        {
            var read = await context.Request.Body.ReadAsync(
                bytes.AsMemory(length), context.RequestAborted);
            if (read == 0)
            {
                break;
            }
            length += read;
        }
        if (length is 0 or > 1024)
        {
            return Results.BadRequest();
        }
        BootstrapUpgradeFailureReport? report;
        try
        {
            report = global::System.Text.Json.JsonSerializer.Deserialize<BootstrapUpgradeFailureReport>(
                bytes.AsSpan(0, length), RestJson.SerializerOptions);
        }
        catch (global::System.Text.Json.JsonException)
        {
            return Results.BadRequest();
        }
        if (report is null || report.SchemaVersion != 1 ||
            report.OperationId is not { Length: 32 } ||
            report.OperationId.Any(character => !char.IsAsciiLetterOrDigit(character)) ||
            report.FailureCode != "child_termination_failed")
        {
            return Results.BadRequest();
        }

        return await upgrades.ReportBootstrapFailureAsync(
                authentication.Context.Principal.ActorId,
                report.OperationId,
                report.FailureCode,
                context.RequestAborted)
            ? Results.Accepted()
            : Results.Conflict();
    }

    private static AgentPackageResource ToResource(AgentPackage package) => new(
        package.PackageId,
        package.Version,
        package.Rid,
        package.Sha256,
        package.Size,
        package.CreatedAt,
        package.Source);

    private static AgentUpgradeOperationResource ToResource(
        AgentUpgradeOperation operation,
        IReadOnlyList<AgentUpgradeEvent>? events = null) => new(
        operation.OperationId,
        operation.AgentId,
        ToResource(operation.Package),
        ToRestState(operation.State),
        operation.Reason,
        operation.MaintenanceFence,
        operation.PriorPackageSha256,
        operation.StartingConnectionGeneration,
        operation.ObservedConnectionGeneration,
        operation.RestartAttempts,
        operation.LastDispatchConnectionGeneration,
        operation.NextRestartAt,
        operation.CancellationReason,
        operation.FailureCode,
        operation.ResultPackageSha256,
        operation.DrainHeld,
        operation.CreatedAt,
        operation.UpdatedAt,
        operation.Deadline,
        operation.CompletedAt,
        (events ?? []).Select(value => new AgentUpgradeEventResource(
            value.Sequence,
            value.Phase,
            value.Code,
            value.ConnectionGeneration,
            value.PackageSha256,
            value.CreatedAt)).ToArray());

    private static string ToRestState(AgentUpgradeState state) => state switch
    {
        AgentUpgradeState.Draining => "draining",
        AgentUpgradeState.HandoffReady => "handoff-ready",
        AgentUpgradeState.AwaitingHealth => "awaiting-health",
        AgentUpgradeState.CommitPending => "commit-pending",
        AgentUpgradeState.Finalizing => "finalizing",
        AgentUpgradeState.RollbackRequested => "rollback-requested",
        AgentUpgradeState.Succeeded => "succeeded",
        AgentUpgradeState.RolledBack => "rolled-back",
        AgentUpgradeState.Failed => "failed",
        AgentUpgradeState.Cancelled => "cancelled",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    private static string? RequiredHeader(HttpContext context, string name)
    {
        var value = context.Request.Headers[name].ToString().Trim();
        return value.Length is >= 1 and <= 256 &&
               !value.Any(character => character is '\r' or '\n' or '\0')
            ? value
            : null;
    }

    private static IResult PackageProblem(HttpContext context, AgentPackageException exception) =>
        RestProblems.Create(
            context,
            exception.StatusCode,
            exception.Code,
            "The Agent package could not be published",
            exception.Message);

    private static IResult UpgradeProblem(HttpContext context, AgentUpgradeException exception) =>
        RestProblems.Create(
            context,
            exception.StatusCode,
            exception.Code,
            "The Agent upgrade request could not be completed",
            exception.Message);
}
