namespace Vivarium.Controller.Rest.Common;

public sealed class RestApiException : Exception
{
    public RestApiException(
        int status,
        string code,
        string title,
        string detail,
        bool retryable = false,
        RestProblemTarget? target = null,
        IReadOnlyList<RestProblemError>? errors = null,
        Exception? innerException = null)
        : base(detail, innerException)
    {
        Status = status;
        Code = code;
        Title = title;
        Retryable = retryable;
        Target = target;
        Errors = errors;
    }

    public int Status { get; }
    public string Code { get; }
    public string Title { get; }
    public bool Retryable { get; }
    public RestProblemTarget? Target { get; }
    public IReadOnlyList<RestProblemError>? Errors { get; }
}

public static class RestProblems
{
    public static IResult AuthenticationRequired(HttpContext context) =>
        Create(
            context,
            StatusCodes.Status401Unauthorized,
            "authentication_required",
            "Authentication is required",
            "Use a valid Vivarium management cookie or bearer credential.");

    public static IResult PermissionDenied(
        HttpContext context,
        string permission,
        RestProblemTarget? target = null) =>
        Create(
            context,
            StatusCodes.Status403Forbidden,
            "permission_denied",
            "Permission is denied",
            $"The authenticated principal does not have the '{permission}' permission.",
            target: target);

    public static IResult NotFound(
        HttpContext context,
        string resourceType,
        string resourceId) =>
        Create(
            context,
            StatusCodes.Status404NotFound,
            "resource_not_found",
            "The resource was not found",
            "The requested resource does not exist or is not visible to the caller.",
            target: new RestProblemTarget(resourceType, resourceId));

    public static IResult Create(HttpContext context, RestApiException exception) =>
        Create(
            context,
            exception.Status,
            exception.Code,
            exception.Title,
            exception.Message,
            exception.Retryable,
            exception.Target,
            exception.Errors);

    public static IResult Create(
        HttpContext context,
        int status,
        string code,
        string title,
        string detail,
        bool retryable = false,
        RestProblemTarget? target = null,
        IReadOnlyList<RestProblemError>? errors = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        var problem = new VivariumProblemDetails
        {
            Type = $"https://vivarium.dev/problems/{code.Replace('_', '-')}",
            Title = title,
            Status = status,
            Detail = detail,
            Instance = context.Request.Path,
            Code = code,
            CorrelationId = RestCorrelation.Get(context),
            Retryable = retryable,
            Target = target,
            Errors = errors,
        };
        return Results.Json(
            problem,
            statusCode: status,
            contentType: "application/problem+json");
    }
}
