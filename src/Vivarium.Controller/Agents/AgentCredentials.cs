using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Vivarium.Contracts.V1;
using Vivarium.Controller.Auditing;
using Vivarium.Controller.Identity;
using Vivarium.Controller.Persistence;
using Vivarium.Controller.Security;
using Vivarium.Controller.Catalog;

namespace Vivarium.Controller.Agents;

public sealed record IssuedAgentEnrollment(Guid Id, string Token, DateTimeOffset ExpiresAt)
{
    public override string ToString() => "Agent enrollment " + Id;
}
public sealed record AgentCredentialGrant(AccessMutationResult Outcome, string? Token, long Generation)
{
    public override string ToString() => "Agent credential grant " + Outcome;
}
public sealed record AgentCredentialAdmission(AgentAdmission Admission, bool BearerProven, string? EnrollmentBinding)
{
    public override string ToString() => "Agent admission " + Admission.Authorization;
}

/// <summary>The Agent credential lane retains D7 delivery/reconnect semantics, independently of human OIDC.</summary>
public sealed class AgentCredentials(IDbContextFactory<VivariumDbContext> databases, IConfiguration configuration,
    IDataProtectionProvider protection, TimeProvider time)
{
    public async Task<IssuedAgentEnrollment> IssueEnrollmentAsync(ManagementRequestContext actor,
        CancellationToken token = default)
    {
        await using var database = await databases.CreateDbContextAsync(token);
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        await PermissionFenceAsync(database, token);
        if (!await CanAuthorizeAsync(database, actor, token))
        {
            Audit(database, actor, "agent.enrollment.issue", "enrollment", "new", AuditOutcome.Denied, "permission_denied");
            await database.SaveChangesAsync(token); await transaction.CommitAsync(token);
            throw new AccessPermissionException();
        }
        var secret = TokenStore.NewToken();
        var enrollment = new AgentEnrollment { Id = Guid.NewGuid(), TokenHash = Hash(secret),
            CreatedBy = Guid.Parse(actor.Principal.ActorId), ExpiresAt = time.GetUtcNow().AddHours(24) };
        database.AgentEnrollments.Add(enrollment);
        Audit(database, actor, "agent.enrollment.issue", "enrollment", enrollment.Id.ToString());
        await database.SaveChangesAsync(token); await transaction.CommitAsync(token);
        return new(enrollment.Id, secret, enrollment.ExpiresAt);
    }

    public async Task<AgentCredentialAdmission?> AdmitAsync(Hello hello, CancellationToken token = default)
    {
        AgentSessionIdentity.Validate(hello);
        await using var database = await databases.CreateDbContextAsync(token);
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        await CatalogAgentSettings.LockProjectionAsync(database, token);
        await LockAgentAsync(database, hello.AgentId, token);
        var current = await database.AgentRegistrations.SingleOrDefaultAsync(a => a.AgentId == hello.AgentId, token);
        var actor = new ManagementRequestContext(new("agent", hello.AgentId, "enrollment-token", null),
            Guid.NewGuid().ToString(), null, "agent-hub");
        if (current is { DeletedAt: null } && hello.AuthToken.Length > 0 && Equal(current.AuthTokenHash, Hash(hello.AuthToken)))
        {
            if (current.ProtectedPendingAuthToken is not null)
            {
                var enrollment = await database.AgentEnrollments.SingleOrDefaultAsync(e => e.TokenHash == current.EnrollmentTokenHash, token);
                if (enrollment is not null) database.AgentEnrollments.Remove(enrollment);
                current.ProtectedPendingAuthToken = null; current.EnrollmentTokenHash = null;
                await database.SaveChangesAsync(token);
            }
            await transaction.CommitAsync(token);
            return new(Admission(current), true, null);
        }
        var enrollHash = hello.EnrollToken.Length == 0 ? null : Hash(hello.EnrollToken);
        if (current is { DeletedAt: null } && enrollHash is not null && Equal(current.EnrollmentTokenHash, enrollHash))
        {
            var delivery = current.Authorized && current.ProtectedPendingAuthToken is not null
                ? Protector(current).Unprotect(current.ProtectedPendingAuthToken) : null;
            await transaction.CommitAsync(token);
            // Enrollment proof only delivers the credential. The Agent persists it and reconnects
            // with a real bearer before becoming eligible for work, matching the existing Agent.
            return new(new(AgentAuth.Unauthorized, current.DesiredEnabled, delivery, current.CredentialGeneration, false,
                current.MaintenanceDrain), false, enrollHash);
        }
        AgentEnrollment? ticket = null;
        if (enrollHash is not null)
            ticket = await database.AgentEnrollments.FromSqlInterpolated($"""
                SELECT * FROM "AgentEnrollments" WHERE "TokenHash" = {enrollHash} FOR UPDATE
                """).SingleOrDefaultAsync(token);
        if (ticket is null || ticket.ExpiresAt < time.GetUtcNow()
            || ticket.ClaimedAgentId is not null && ticket.ClaimedAgentId != hello.AgentId)
        {
            Audit(database, actor, "agent.enroll", "agent", hello.AgentId, AuditOutcome.Denied, "invalid_enrollment_proof");
            await database.SaveChangesAsync(token); await transaction.CommitAsync(token);
            return null;
        }
        if (current?.CurrentWorkloadId is not null)
        {
            Audit(database, actor, "agent.reenroll", "agent", hello.AgentId, AuditOutcome.Denied, "active_workload_owns_identity");
            await database.SaveChangesAsync(token); await transaction.CommitAsync(token);
            return null;
        }
        var replaced = current is not null;
        if (current is null)
        {
            var requestedName = hello.Parameters.TryGetValue("hostname", out var hostname) && !string.IsNullOrEmpty(hostname)
                ? hostname : "agent-" + hello.AgentId[..Math.Min(8, hello.AgentId.Length)];
            current = new AgentRegistration { AgentId = hello.AgentId, ReportedName = requestedName,
                FirstSeenAt = time.GetUtcNow(), LastSeenAt = time.GetUtcNow() };
            await CatalogAgentSettings.InitializeAsync(database, configuration, current, token);
            database.AgentRegistrations.Add(current);
        }
        else
        {
            current.CredentialGeneration = checked(current.CredentialGeneration + 1);
            current.Authorized = false; current.AuthTokenHash = null; current.ProtectedPendingAuthToken = null;
            current.DeletedAt = null; current.Reconciled = false;
        }
        current.EnrollmentTokenHash = enrollHash;
        ticket.ClaimedAgentId = hello.AgentId;
        Audit(database, actor, replaced ? "agent.reenroll" : "agent.enroll", "agent", hello.AgentId);
        await database.SaveChangesAsync(token); await transaction.CommitAsync(token);
        return new(new(AgentAuth.Unauthorized, current.DesiredEnabled, null, current.CredentialGeneration, replaced,
            current.MaintenanceDrain), false, enrollHash);
    }

    public async Task<AgentCredentialGrant> SetAuthorizedAsync(ManagementRequestContext actor, string agentId,
        bool authorized, CancellationToken token = default)
    {
        await using var database = await databases.CreateDbContextAsync(token);
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        await PermissionFenceAsync(database, token);
        await LockAgentAsync(database, agentId, token);
        if (!await CanAuthorizeAsync(database, actor, token))
        {
            Audit(database, actor, "agent.authorize", "agent", agentId, AuditOutcome.Denied, "permission_denied");
            await database.SaveChangesAsync(token); await transaction.CommitAsync(token);
            return new(AccessMutationResult.Denied, null, 0);
        }
        var row = await database.AgentRegistrations.SingleOrDefaultAsync(a => a.AgentId == agentId && a.DeletedAt == null, token);
        if (row is null)
        {
            Audit(database, actor, "agent.authorize", "agent", agentId, AuditOutcome.Failed, "resource_not_found");
            await database.SaveChangesAsync(token); await transaction.CommitAsync(token);
            return new(AccessMutationResult.NotFound, null, 0);
        }
        var changed = row.Authorized != authorized;
        string? delivery = null;
        if (authorized && row.AuthTokenHash is null)
        {
            delivery = TokenStore.NewToken();
            if (row.CredentialGeneration == 0) row.CredentialGeneration = 1;
            row.AuthTokenHash = Hash(delivery);
            row.ProtectedPendingAuthToken = Protector(row).Protect(delivery);
        }
        else if (authorized && row.ProtectedPendingAuthToken is not null)
            delivery = Protector(row).Unprotect(row.ProtectedPendingAuthToken);
        row.Authorized = authorized;
        Audit(database, actor, "agent.authorize", "agent", agentId,
            changed ? AuditOutcome.Succeeded : AuditOutcome.NoChange,
            details: new Dictionary<string, string> { ["authorized"] = authorized ? "true" : "false" });
        await database.SaveChangesAsync(token); await transaction.CommitAsync(token);
        return new(changed ? AccessMutationResult.Applied : AccessMutationResult.Unchanged, delivery, row.CredentialGeneration);
    }

    public async Task<AgentRegistration?> ResolveAsync(string secret, CancellationToken token = default)
    {
        if (string.IsNullOrEmpty(secret) || secret.Length > 4096) return null;
        var hash = Hash(secret);
        await using var database = await databases.CreateDbContextAsync(token);
        return await database.AgentRegistrations.AsNoTracking().SingleOrDefaultAsync(a => a.AuthTokenHash == hash && a.DeletedAt == null, token);
    }

    internal static Task LockAgentAsync(VivariumDbContext database, string agentId, CancellationToken token) =>
        database.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({agentId}, 90695025116))", token);
    private static Task PermissionFenceAsync(VivariumDbContext database, CancellationToken token) =>
        database.Database.ExecuteSqlRawAsync("""
            LOCK TABLE "AspNetUsers", "UserRoleGrants", "RolePermissions", "IncludedRoles", "GroupRoleGrants" IN SHARE MODE
            """, token);
    private Task<bool> CanAuthorizeAsync(VivariumDbContext database, ManagementRequestContext actor, CancellationToken token) =>
        actor.Principal.ActorType == "user" && Guid.TryParse(actor.Principal.ActorId, out var user)
            ? new CurrentAccess(database, configuration).AllowsAsync(user, "authorize_agent", cancellationToken: token)
            : Task.FromResult(false);
    private IDataProtector Protector(AgentRegistration agent) => protection.CreateProtector("Vivarium.AgentCredentials.v1",
        agent.AgentId, agent.CredentialGeneration.ToString(CultureInfo.InvariantCulture));
    private static AgentAdmission Admission(AgentRegistration row) => new(row.Authorized ? AgentAuth.Authorized : AgentAuth.Unauthorized,
        row.DesiredEnabled, null, row.CredentialGeneration, false, row.MaintenanceDrain);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static bool Equal(string? left, string right) => left is not null && left.Length == right.Length
        && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(left), Encoding.ASCII.GetBytes(right));
    private void Audit(VivariumDbContext database, ManagementRequestContext actor, string action, string type, string id,
        AuditOutcome outcome = AuditOutcome.Succeeded, string reason = "", IReadOnlyDictionary<string, string>? details = null) =>
        new PostgresAuditStore(database).Stage(AuditEventDraft.Create(actor, time.GetUtcNow(), action, type, id, outcome, reason, details));
}
