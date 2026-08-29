using Vivarium.Controller.Rest.Common;

namespace Vivarium.Controller.Rest.System;

public static class SystemEndpoints
{
    public static IEndpointRouteBuilder MapVivariumSystemApi(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
                "/api/v1/system",
                (Func<HttpContext, Task<IResult>>)GetSystemAsync)
            .WithName("getSystem")
            .WithTags("System")
            .WithSummary("Read controller API metadata and operational limits")
            .WithDescription(
                "Returns the stable API version and bounded pagination conventions for this controller.")
            .Produces<SystemResource>()
            .Produces<VivariumProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")
            .Produces<VivariumProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")
            .Produces(StatusCodes.Status304NotModified);
        return endpoints;
    }

    private static async Task<IResult> GetSystemAsync(HttpContext context)
    {
        var authorization = await RestAuthentication.AuthenticateManagementAsync(
            context, "rest-system-read");
        if (!authorization.IsAuthorized)
        {
            return authorization.Failure!;
        }

        var resource = SystemResourceFactory.Create();
        return RestEtags.ApplyConditionalGet(context, RestEtags.FromValue(resource), resource);
    }
}
