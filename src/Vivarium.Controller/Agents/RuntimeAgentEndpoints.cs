using System.Data;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Vivarium.Controller.Identity;
using Vivarium.Controller.Persistence;
using Vivarium.Controller.Rest.Common;
using Vivarium.Controller.Security;

namespace Vivarium.Controller.Agents;

internal static class RuntimeAgentEndpoints
{
    internal static void MapRuntimeAgents(this WebApplication app)
    {
        app.MapPost("/api/v1/enrollment-tokens", (HttpContext http, AgentCredentials credentials) =>
            RuntimeManagement.ExecuteAsync(http, async actor =>
            {
                if (http.Request.ContentLength is > 0 || http.Request.Headers.ContainsKey("Transfer-Encoding"))
                    return RuntimeManagement.InvalidInput(http);
                var issued = await credentials.IssueEnrollmentAsync(actor, http.RequestAborted);
                // The enrollment proof is disclosed once, only after issuance and audit commit.
                return Results.Json(new { issued.Id, token = issued.Token, issued.ExpiresAt }, statusCode: 201);
            }, mutation: true, source: "agents"));
        app.MapPut("/api/v1/agents/{agentId}/authorization", (HttpContext http, string agentId, RuntimeAgentSessions sessions) =>
            RuntimeManagement.ExecuteAsync(http, async actor =>
            {
                if (agentId.Length > AgentSessionIdentity.MaximumLength || agentId.Any(char.IsControl))
                    return RuntimeManagement.InvalidInput(http);
                if (!http.Request.HasJsonContentType()) return RestProblems.Create(http, 415, "unsupported_media_type",
                    "JSON is required", "Use application/json for this mutation.");
                var input = await http.Request.ReadFromJsonAsync<AuthorizationInput>(http.RequestAborted);
                if (input is null) return RuntimeManagement.InvalidInput(http);
                var result = await sessions.SetAuthorizedAsync(actor, agentId, input.Authorized, http.RequestAborted);
                return result.Outcome switch
                {
                    AccessMutationResult.Applied or AccessMutationResult.Unchanged => Results.NoContent(),
                    AccessMutationResult.Denied => RestProblems.Create(http, 403, "permission_denied", "Permission is denied",
                        "Current Agent authorization permission is required."),
                    _ => RestProblems.NotFound(http, "agent", agentId),
                };
            }, mutation: true, source: "agents"));
        app.MapGet("/api/v1/agents", (HttpContext http, VivariumDbContext database, CurrentAccess access, RuntimeAgentSessions sessions) =>
            RuntimeManagement.ExecuteAsync(http, async actor =>
            {
                await using var snapshot = await database.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, http.RequestAborted);
                if (!await CanReadAsync(actor, access, http.RequestAborted)) throw new AccessPermissionException();
                if (!Page(http, out var offset, out var limit)) return InvalidPage(http);
                var query = database.AgentRegistrations.AsNoTracking().Where(a => a.DeletedAt == null);
                var total = await query.LongCountAsync(http.RequestAborted);
                var rows = await query.OrderBy(a => a.TeamCityId).Skip(offset).Take(limit).ToArrayAsync(http.RequestAborted);
                return Results.Json(new AccessPage<object>(rows.Select(row => View(row, sessions)).ToArray(), total, offset, limit));
            }, source: "agents"));
        app.MapGet("/api/v1/agents/{agentId}", (HttpContext http, string agentId, VivariumDbContext database,
            CurrentAccess access, RuntimeAgentSessions sessions) => RuntimeManagement.ExecuteAsync(http, async actor =>
        {
            await using var snapshot = await database.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, http.RequestAborted);
            if (!await CanReadAsync(actor, access, http.RequestAborted)) throw new AccessPermissionException();
            var row = await database.AgentRegistrations.AsNoTracking().SingleOrDefaultAsync(a => a.AgentId == agentId && a.DeletedAt == null,
                http.RequestAborted);
            return row is null ? RestProblems.NotFound(http, "agent", agentId) : Results.Json(View(row, sessions));
        }, source: "agents"));
    }

    private static object View(AgentRegistration row, RuntimeAgentSessions sessions)
    {
        var connected = sessions.Connected(row.AgentId, row.CurrentSessionId, row.ConnectionGeneration);
        return new
        {
            id = row.AgentId, row.TeamCityId, name = row.DesiredName ?? row.ReportedName,
            authorized = row.Authorized, enabled = row.DesiredEnabled, connected, row.Reconciled,
            row.Health, row.Quarantined, row.HealthReason, row.CredentialGeneration, row.ConnectionGeneration,
            row.LastSeenAt, row.LastHeartbeatAt, appliedConfigurationRevision = row.DesiredRevision,
            ready = connected && row.SessionAuthenticated && row.Authorized && row.DesiredEnabled && row.DesiredRevision is not null
                && row.Reconciled && row.Health == "HEALTHY" && !row.Quarantined && !row.MaintenanceDrain,
        };
    }
    private static Task<bool> CanReadAsync(ManagementRequestContext actor, CurrentAccess access, CancellationToken token) =>
        Guid.TryParse(actor.Principal.ActorId, out var id) ? access.AllowsAsync(id, "view_agent_details", cancellationToken: token) : Task.FromResult(false);
    private static bool Page(HttpContext http, out int offset, out int limit)
    {
        offset = 0; limit = 50;
        return (!http.Request.Query.ContainsKey("offset") || int.TryParse(http.Request.Query["offset"], out offset))
            && (!http.Request.Query.ContainsKey("limit") || int.TryParse(http.Request.Query["limit"], out limit))
            && offset >= 0 && limit is >= 1 and <= 200;
    }
    private static IResult InvalidPage(HttpContext http) => RestProblems.Create(http, 400, "invalid_pagination",
        "Invalid pagination", "Offset must be nonnegative and limit between 1 and 200.");
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed class AuthorizationInput { public required bool Authorized { get; init; } }
}
