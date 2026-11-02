using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Antiforgery;
using Vivarium.Controller.Rest.Common;
using Vivarium.Controller.Security;

namespace Vivarium.Controller.Identity;

/// <summary>Native Access projections; compatible representations retain their own wire adapter.</summary>
internal static class AccessEndpoints
{
    public static void MapAccess(this WebApplication app)
    {
        app.MapGet("/api/v1/users", (HttpContext http, AccessQueries reads) => ExecuteAsync(http, async actor =>
        {
            if (!Page(http, out var offset, out var limit)) return InvalidPage(http);
            return Results.Json(await reads.UsersAsync(actor, offset, limit, http.RequestAborted));
        }));
        app.MapGet("/api/v1/roles", (HttpContext http, AccessQueries reads) => ExecuteAsync(http, async actor =>
        {
            if (!Page(http, out var offset, out var limit)) return InvalidPage(http);
            return Results.Json(await reads.RolesAsync(actor, offset, limit, http.RequestAborted));
        }));
        app.MapGet("/api/v1/users/{userId:guid}", (HttpContext http, Guid userId, AccessQueries reads) =>
            ExecuteAsync(http, async actor =>
            {
                var user = await reads.UserAsync(actor, userId, http.RequestAborted);
                return user is null ? RestProblems.NotFound(http, "user", userId.ToString()) : Results.Json(user);
            }));
        app.MapGet("/api/v1/users/{userId:guid}/roles", (HttpContext http, Guid userId, AccessQueries reads) =>
            ExecuteAsync(http, async actor =>
            {
                if (!Page(http, out var offset, out var limit)) return InvalidPage(http);
                var scopes = http.Request.Query["scope"];
                var roleIds = http.Request.Query["roleId"].Select(value => value ?? "").ToArray();
                if (scopes.Count > 1 || roleIds.Length > 200 || roleIds.Any(string.IsNullOrEmpty)) return InvalidInput(http);
                var grants = await reads.UserRolesAsync(actor, userId, offset, limit, scopes.Count == 0 ? null : scopes.ToString(),
                    roleIds, http.RequestAborted);
                return grants is null ? RestProblems.NotFound(http, "user", userId.ToString())
                    : Results.Json(grants);
            }));
        app.MapPut("/api/v1/users/{userId:guid}/roles/{roleId}/g",
            (HttpContext http, Guid userId, string roleId, TeamCityAccessService changes) => ExecuteAsync(http, async actor =>
                Result(http, await changes.SetGlobalUserRoleAsync(actor, userId, roleId, true, http.RequestAborted), userId), mutation: true));
        app.MapDelete("/api/v1/users/{userId:guid}/roles/{roleId}/g",
            (HttpContext http, Guid userId, string roleId, TeamCityAccessService changes) => ExecuteAsync(http, async actor =>
                Result(http, await changes.SetGlobalUserRoleAsync(actor, userId, roleId, false, http.RequestAborted), userId), mutation: true));
        app.MapPut("/api/v1/users/{userId:guid}/enabled",
            (HttpContext http, Guid userId, TeamCityAccessService changes) => ExecuteAsync(http, async actor =>
            {
                if (!http.Request.HasJsonContentType()) return RestProblems.Create(http, 415, "unsupported_media_type",
                    "JSON is required", "Use application/json for this mutation.");
                var input = await http.Request.ReadFromJsonAsync<EnabledInput>(http.RequestAborted);
                if (input is null) return InvalidInput(http);
                return Result(http, await changes.SetOrdinaryUserEnabledAsync(actor, userId, input.Enabled, http.RequestAborted), userId);
            }, mutation: true));
    }

    private static async Task<IResult> ExecuteAsync(HttpContext http, Func<ManagementRequestContext, Task<IResult>> action,
        bool mutation = false)
    {
        var user = await http.RequestServices.GetRequiredService<RuntimeIdentity>().CurrentAsync(http);
        if (user is null)
        {
            http.Response.Headers.WWWAuthenticate = "Bearer";
            return RestProblems.AuthenticationRequired(http);
        }
        try
        {
            var bearer = http.Request.Headers.ContainsKey("Authorization");
            if (mutation && !bearer)
                await http.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(http);
            var actor = http.RequestServices.GetRequiredService<RuntimeRequestContext>()
                .ForUser(user.Id, bearer ? "openid-bearer" : "identity-cookie", "access");
            return await action(actor);
        }
        catch (AccessPermissionException)
        {
            return RestProblems.Create(http, 403, "permission_denied", "Permission is denied",
                "Global profile-read or user-management permission is required.");
        }
        catch (AntiforgeryValidationException)
        {
            return RestProblems.Create(http, 400, "invalid_antiforgery_token", "Request verification failed",
                "Use the current browser request-verification token.");
        }
        catch (JsonException) { return InvalidInput(http); }
        catch (BadHttpRequestException) { return InvalidInput(http); }
    }

    private static IResult Result(HttpContext http, AccessMutationResult result, Guid userId) => result switch
    {
        AccessMutationResult.Applied or AccessMutationResult.Unchanged => Results.NoContent(),
        AccessMutationResult.NotFound => RestProblems.NotFound(http, "access-target", userId.ToString()),
        AccessMutationResult.Denied => RestProblems.Create(http, 403, "permission_denied", "Permission is denied",
            "Current permissions do not allow this change."),
        _ => throw new InvalidOperationException("Unknown access mutation outcome."),
    };

    private static bool Page(HttpContext http, out int offset, out int limit)
    {
        offset = 0; limit = 50;
        return (!http.Request.Query.ContainsKey("offset") || int.TryParse(http.Request.Query["offset"], out offset))
            && (!http.Request.Query.ContainsKey("limit") || int.TryParse(http.Request.Query["limit"], out limit))
            && offset >= 0 && limit is >= 1 and <= 200;
    }

    private static IResult InvalidPage(HttpContext http) => RestProblems.Create(http, 400,
        "invalid_pagination", "Invalid pagination", "Offset must be nonnegative and limit must be between 1 and 200.");
    private static IResult InvalidInput(HttpContext http) => RestProblems.Create(http, 400,
        "invalid_request", "Invalid request", "Supply the documented JSON request shape.");

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed class EnabledInput { public required bool Enabled { get; init; } }
}
