using Vivarium.Controller.Rest.Common;

namespace Vivarium.Controller.Rest.System;

public sealed record SystemResource(
    string Id,
    string Url,
    string ApiVersion,
    string Status,
    string ControllerVersion,
    SystemLimits Limits,
    SystemLinks Links);

public sealed record SystemLimits(
    int DefaultPageLimit,
    int MaximumPageLimit,
    string CursorLifetime);

public sealed record SystemLinks(
    string OpenApi);

internal static class SystemResourceFactory
{
    public static SystemResource Create() =>
        new(
            Id: "system",
            Url: "/api/v1/system",
            ApiVersion: "v1",
            Status: "ready",
            ControllerVersion: typeof(SystemResourceFactory).Assembly
                .GetName().Version?.ToString() ?? "0.0.0.0",
            new SystemLimits(
                RestPagination.DefaultLimit,
                RestPagination.MaxLimit,
                global::System.Xml.XmlConvert.ToString(RestCursorCodec.Lifetime)),
            new SystemLinks("/openapi/v1.json"));
}
