using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;

namespace Vivarium.Controller.Identity;

/// <summary>Same-origin Workbench entry, with presentation/CSRF bootstrap and server-side authority.</summary>
internal static class WorkbenchShell
{
    public static void MapWorkbench(this WebApplication app)
    {
        app.MapGet("/", RenderAsync);
        app.MapGet("/access", RenderAsync);
        app.MapGet("/access/users/{userId:guid}", RenderAsync);
        app.MapGet("/builds", RenderAsync);
        app.MapGet("/builds/{buildId:long}", RenderAsync);
        app.MapGet("/projects/{projectId}", RenderAsync);
        app.MapGet("/build-configurations/{configurationId}", RenderAsync);
    }

    private static async Task<IResult> RenderAsync(HttpContext http, RuntimeIdentity identity,
        CurrentAccess access, RuntimeRequestContext requestContext, AccessQueries reads,
        IAntiforgery antiforgery, IWebHostEnvironment environment)
    {
        http.Response.Headers.CacheControl = "no-store";
        var user = await identity.CurrentAsync(http);
        if (user is null && http.Request.Headers.ContainsKey("Authorization"))
            return Rest.Common.RestProblems.AuthenticationRequired(http);
        var read = false; var grant = false; var change = false;
        if (user is not null)
        {
            var actor = requestContext.ForUser(user.Id, "identity-cookie", "workbench");
            read = await reads.CanReadProfilesAsync(actor, http.RequestAborted);
            change = await access.AllowsAsync(user.Id, "change_user", cancellationToken: http.RequestAborted);
            grant = change || await access.AllowsAsync(user.Id, "manage_roles", cancellationToken: http.RequestAborted);
        }
        var session = JsonSerializer.Serialize(new
        {
            localDevelopment = LocalDevelopment.Root(http.RequestServices.GetRequiredService<IConfiguration>()) is not null,
            user = user is null ? null : new
            {
                user.Id, displayName = user.DisplayName ?? user.UserName ?? "Account",
                isInstallation = user.Id == VivariumIdentityConstants.SuperuserId
            },
            canReadProfiles = read, canGrantRoles = grant, canChangeUsers = change,
            requestVerificationToken = user is null ? "" : antiforgery.GetAndStoreTokens(http).RequestToken
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        // The stock JSON encoder escapes '<' in untrusted account presentation.
        var development = LocalDevelopment.Root(http.RequestServices.GetRequiredService<IConfiguration>());
        var html = development is null
            ? await File.ReadAllTextAsync(Path.Combine(environment.WebRootPath, "workbench/index.html"), http.RequestAborted)
            : "<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>Vivarium local development</title></head><body><div id=\"app\"></div><script type=\"module\" src=\"/workbench/@vite/client\"></script><script type=\"module\" src=\"/workbench/src/main.ts\"></script></body></html>";
        return Results.Content(html.Replace("</head>", "<script id=\"vivarium-session\" type=\"application/json\">"
            + session + "</script></head>", StringComparison.Ordinal), "text/html; charset=utf-8");
    }
}
