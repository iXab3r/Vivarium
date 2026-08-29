using System.Runtime.CompilerServices;
using System.Security.Claims;
using Vivarium.Controller.Auditing;
using Vivarium.Controller.Persistence;

[assembly: InternalsVisibleTo("Vivarium.Tests")]

namespace Vivarium.Controller.Security;

public enum ManagementPermission
{
    PanelAccess,
    BlobRead,
    BlobWrite,
    BlobDiscover,
    BuildSubmit,
    BuildWatch,
    BuildCancel,
    AgentList,
    AgentAuthorize,
    AgentManage,
    EnrollmentTokenCreate,
    ArtifactRead,
    AgentPackageRead,
    AgentPackageManage,
}

public sealed record ManagementPrincipal(
    string ActorType,
    string ActorId,
    string CredentialKind,
    BearerScope? LegacyScope,
    long? CredentialGeneration = null)
{
    public static ManagementPrincipal LegacyAdmin { get; } =
        new("user", "legacy-admin", "legacy-admin-token", BearerScope.Admin);

    public static ManagementPrincipal LegacySubmit { get; } =
        new("service", "legacy-submit", "legacy-submit-token", BearerScope.Submit);

    public static ManagementPrincipal System { get; } =
        new("system", "controller", "internal", LegacyScope: null);

    public static ManagementPrincipal Superuser { get; } =
        new("superuser", "break-glass", "recovery-session", LegacyScope: null);

    public static ManagementPrincipal Anonymous { get; } =
        new("anonymous", "anonymous", "none", LegacyScope: null);

    public static ManagementPrincipal Agent(string agentId) =>
        new("agent", agentId, "agent-token", BearerScope.Agent);
}

public sealed record ManagementRequestContext(
    ManagementPrincipal Principal,
    string CorrelationId,
    string? RequestId,
    string Source)
{
    public static ManagementRequestContext System(string source, string? requestId = null) =>
        new(
            ManagementPrincipal.System,
            ManagementIdentifiers.NewId(),
            NormalizeOptionalRequestId(requestId),
            source);

    public static ManagementRequestContext Anonymous(string source, string? correlationId = null) =>
        new(
            ManagementPrincipal.Anonymous,
            ManagementIdentifiers.NormalizeCorrelationId(correlationId),
            RequestId: null,
            source);

    public ManagementRequestContext WithRequestId(string? requestId) =>
        this with { RequestId = NormalizeOptionalRequestId(requestId) };

    private static string? NormalizeOptionalRequestId(string? requestId) =>
        string.IsNullOrWhiteSpace(requestId) ? null : requestId.Trim();
}

public sealed class ManagementAuthorizationException(
    ManagementPermission permission,
    string reasonCode = "permission_denied")
    : Exception($"management permission '{permission}' is required")
{
    public ManagementPermission Permission { get; } = permission;
    public string ReasonCode { get; } = reasonCode;
}

public sealed class ManagementAuthorizer(VivariumDatabase? database = null)
{
    public bool Allows(ManagementPrincipal principal, ManagementPermission permission)
        => Evaluate(principal, permission).Allowed;

    public bool Allows(
        ManagementPrincipal principal,
        ManagementPermission permission,
        AuthorizationResource resource)
        => Evaluate(principal, permission, resource).Allowed;

    public AuthorizationDecision Evaluate(
        ManagementPrincipal principal,
        ManagementPermission permission,
        AuthorizationResource? resource = null)
    {
        ArgumentNullException.ThrowIfNull(principal);
        resource ??= DefaultResource(permission);
        var permissionId = PermissionId(permission);
        if (principal == ManagementPrincipal.System)
        {
            return new AuthorizationDecision(
                true, permissionId, resource, "system_principal", null, null);
        }

        if (principal == ManagementPrincipal.Superuser)
        {
            return new AuthorizationDecision(
                true,
                permissionId,
                resource,
                "recovery_superuser_grant",
                AuthorizationRoleIds.SystemAdministrator,
                null);
        }

        var legacyAllowed = principal.LegacyScope switch
        {
            BearerScope.Admin => true,
            BearerScope.Submit => permission is
                ManagementPermission.BlobRead or
                ManagementPermission.BlobWrite or
                ManagementPermission.BlobDiscover or
                ManagementPermission.BuildSubmit or
                ManagementPermission.BuildWatch or
                ManagementPermission.BuildCancel,
            BearerScope.Agent => permission is
                ManagementPermission.BlobRead or
                ManagementPermission.BlobWrite or
                ManagementPermission.AgentPackageRead,
            _ => false,
        };
        if (principal.LegacyScope is not null)
        {
            return new AuthorizationDecision(
                legacyAllowed,
                permissionId,
                resource,
                legacyAllowed ? "legacy_scope_grant" : "permission_denied",
                principal.LegacyScope == BearerScope.Admin
                    ? AuthorizationRoleIds.SystemAdministrator
                    : null,
                null);
        }

        if (database is null || principal.ActorType is not ("user" or "service"))
        {
            return new AuthorizationDecision(
                false, permissionId, resource, "permission_denied", null, null);
        }

        return EvaluateDesiredPrincipal(principal, permission, permissionId, resource);
    }

    public void Demand(ManagementRequestContext context, ManagementPermission permission)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!Allows(context.Principal, permission))
        {
            throw new ManagementAuthorizationException(permission);
        }
    }

    public void Demand(
        ManagementRequestContext context,
        ManagementPermission permission,
        AuthorizationResource resource)
    {
        ArgumentNullException.ThrowIfNull(context);
        var decision = Evaluate(context.Principal, permission, resource);
        if (!decision.Allowed)
        {
            throw new ManagementAuthorizationException(permission, decision.ReasonCode);
        }
    }

    private AuthorizationDecision EvaluateDesiredPrincipal(
        ManagementPrincipal principal,
        ManagementPermission permission,
        string permissionId,
        AuthorizationResource resource) => database!.ReadAsync(connection =>
    {
        using (var user = connection.CreateCommand())
        {
            user.CommandText = """
                SELECT desired_active, source_revision_set_id
                FROM authorization_desired_users
                WHERE user_id = $principalId;
                """;
            user.Parameters.AddWithValue("$principalId", principal.ActorId);
            using var reader = user.ExecuteReader();
            if (!reader.Read() || reader.GetInt64(0) != 1)
            {
                return new AuthorizationDecision(
                    false, permissionId, resource, "principal_inactive", null, null);
            }
        }

        if (principal.CredentialGeneration is { } credentialGeneration)
        {
            using var credential = connection.CreateCommand();
            credential.CommandText = """
                SELECT 1 FROM authorization_user_credentials
                WHERE user_id = $principalId AND credential_state = 'ACTIVE'
                    AND credential_generation = $generation;
                """;
            credential.Parameters.AddWithValue("$principalId", principal.ActorId);
            credential.Parameters.AddWithValue("$generation", credentialGeneration);
            if (credential.ExecuteScalar() is null)
            {
                return new AuthorizationDecision(
                    false, permissionId, resource, "credential_generation_revoked", null, null);
            }
        }

        using var bindings = connection.CreateCommand();
        bindings.CommandText = """
            SELECT role_id, scope_kind, scope_id, source_revision_set_id
            FROM authorization_role_bindings
            WHERE principal_type = $principalType AND principal_id = $principalId
            ORDER BY binding_id COLLATE BINARY;
            """;
        bindings.Parameters.AddWithValue("$principalType", principal.ActorType);
        bindings.Parameters.AddWithValue("$principalId", principal.ActorId);
        using var bindingReader = bindings.ExecuteReader();
        while (bindingReader.Read())
        {
            var roleId = bindingReader.GetString(0);
            var bindingScope = ParseScope(bindingReader.GetString(1));
            var bindingScopeId = bindingReader.GetString(2);
            var grantsPermission = permission == ManagementPermission.PanelAccess
                ? AuthorizationBuiltInRoles.Contains(
                    roleId,
                    roleId == AuthorizationRoleIds.AgentManager
                        ? AuthorizationPermissionIds.FleetSummaryView
                        : AuthorizationPermissionIds.ProjectView)
                : ScopeApplies(bindingScope, bindingScopeId, resource) &&
                  AuthorizationBuiltInRoles.Contains(roleId, permissionId);
            if (!grantsPermission)
            {
                continue;
            }

            return new AuthorizationDecision(
                true,
                permissionId,
                resource,
                "role_binding_grant",
                roleId,
                bindingReader.GetString(3));
        }

        return new AuthorizationDecision(
            false, permissionId, resource, "permission_denied", null, null);
    }).GetAwaiter().GetResult();

    private static bool ScopeApplies(
        AuthorizationScopeKind bindingScope,
        string bindingScopeId,
        AuthorizationResource resource) => bindingScope switch
    {
        AuthorizationScopeKind.Global => true,
        AuthorizationScopeKind.Project =>
            resource.ScopeKind == AuthorizationScopeKind.Project &&
            string.Equals(bindingScopeId, resource.ScopeId, StringComparison.Ordinal),
        AuthorizationScopeKind.Fleet =>
            resource.ScopeKind is AuthorizationScopeKind.Fleet or AuthorizationScopeKind.Pool,
        AuthorizationScopeKind.Pool =>
            resource.ScopeKind == AuthorizationScopeKind.Pool &&
            string.Equals(bindingScopeId, resource.ScopeId, StringComparison.Ordinal),
        _ => false,
    };

    private static AuthorizationScopeKind ParseScope(string value) => value switch
    {
        "global" => AuthorizationScopeKind.Global,
        "project" => AuthorizationScopeKind.Project,
        "fleet" => AuthorizationScopeKind.Fleet,
        "pool" => AuthorizationScopeKind.Pool,
        _ => throw new InvalidDataException("authorization binding has an unknown scope kind"),
    };

    private static AuthorizationResource DefaultResource(ManagementPermission permission) => permission switch
    {
        ManagementPermission.AgentList or ManagementPermission.AgentAuthorize or
        ManagementPermission.AgentManage or ManagementPermission.EnrollmentTokenCreate or
        ManagementPermission.AgentPackageRead =>
            AuthorizationResource.FleetRoot,
        ManagementPermission.BuildSubmit or ManagementPermission.BuildWatch or
        ManagementPermission.BuildCancel or ManagementPermission.BlobRead or
        ManagementPermission.BlobWrite or ManagementPermission.BlobDiscover or
        ManagementPermission.ArtifactRead => AuthorizationResource.ProjectRoot,
        _ => AuthorizationResource.Global,
    };

    private static string PermissionId(ManagementPermission permission) => permission switch
    {
        ManagementPermission.PanelAccess => AuthorizationPermissionIds.ProjectView,
        ManagementPermission.BlobRead => AuthorizationPermissionIds.BuildArtifactView,
        ManagementPermission.BlobWrite or ManagementPermission.BlobDiscover or
        ManagementPermission.BuildSubmit => AuthorizationPermissionIds.BuildRun,
        ManagementPermission.BuildWatch => AuthorizationPermissionIds.ProjectView,
        ManagementPermission.BuildCancel => AuthorizationPermissionIds.BuildCancel,
        ManagementPermission.AgentList => AuthorizationPermissionIds.FleetSummaryView,
        ManagementPermission.AgentAuthorize or ManagementPermission.EnrollmentTokenCreate =>
            AuthorizationPermissionIds.FleetAgentAuthorize,
        ManagementPermission.AgentManage => AuthorizationPermissionIds.FleetAgentManage,
        ManagementPermission.ArtifactRead => AuthorizationPermissionIds.BuildArtifactView,
        ManagementPermission.AgentPackageRead => AuthorizationPermissionIds.FleetSummaryView,
        ManagementPermission.AgentPackageManage => AuthorizationPermissionIds.ServerManage,
        _ => throw new ArgumentOutOfRangeException(nameof(permission)),
    };
}

/// <summary>
/// Enforces caller permissions at the application-command boundary and records a denied command
/// against its addressed domain target. Transport adapters authenticate and translate errors; they
/// do not own this authorization decision.
/// </summary>
public sealed class ManagementCommandAuthorizer(
    ManagementAuthorizer authorizer,
    AuditEventStore audits,
    TimeProvider timeProvider)
{
    public async Task DemandAsync(
        ManagementRequestContext context,
        ManagementPermission permission,
        string action,
        string targetType,
        string targetId)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            authorizer.Demand(context, permission);
        }
        catch (ManagementAuthorizationException exception)
        {
            await audits.AppendAsync(AuditEventDraft.Create(
                context,
                timeProvider.GetUtcNow(),
                action,
                targetType,
                NormalizeTargetId(targetId),
                AuditOutcome.Denied,
                exception.ReasonCode,
                new Dictionary<string, string>
                {
                    ["permission"] = permission.ToString(),
                }));
            throw;
        }
    }

    private static string NormalizeTargetId(string targetId)
    {
        var normalized = string.IsNullOrWhiteSpace(targetId)
            ? "(unspecified)"
            : targetId.Trim();
        return normalized.Length <= 256 ? normalized : normalized[..256];
    }
}

public sealed class ManagementRequestContextFactory(TokenStore tokens)
{
    public const string CorrelationHeader = "X-Correlation-ID";
    public const string ActorIdClaim = "vivarium:actor_id";
    public const string ActorTypeClaim = "vivarium:actor_type";
    public const string CredentialKindClaim = "vivarium:credential_kind";
    public const string LegacyScopeClaim = "vivarium:legacy_scope";
    public const string CredentialGenerationClaim = "vivarium:credential_generation";

    public async Task<ManagementRequestContext?> FromBearerAsync(
        string authorizationHeader,
        string? suppliedCorrelationId,
        string? requestId,
        string source)
    {
        const string prefix = "Bearer ";
        if (!authorizationHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var token = authorizationHeader[prefix.Length..].Trim();
        if (token.Length == 0)
        {
            return null;
        }

        var principal = await tokens.ResolveBearerPrincipalAsync(token);
        return principal is null
            ? null
            : new ManagementRequestContext(
                principal,
                ManagementIdentifiers.NormalizeCorrelationId(suppliedCorrelationId),
                string.IsNullOrWhiteSpace(requestId) ? null : requestId.Trim(),
                source);
    }

    public ManagementRequestContext FromClaims(
        ClaimsPrincipal claims,
        string? suppliedCorrelationId,
        string? requestId,
        string source)
    {
        ArgumentNullException.ThrowIfNull(claims);
        if (claims.Identity?.IsAuthenticated != true)
        {
            throw new ManagementAuthorizationException(
                ManagementPermission.PanelAccess,
                "authentication_required");
        }

        var actorId = claims.FindFirstValue(ActorIdClaim);
        var actorType = claims.FindFirstValue(ActorTypeClaim);
        var credentialKind = claims.FindFirstValue(CredentialKindClaim);
        var scopeValue = claims.FindFirstValue(LegacyScopeClaim);
        var generationValue = claims.FindFirstValue(CredentialGenerationClaim);
        if (string.IsNullOrWhiteSpace(actorId) ||
            string.IsNullOrWhiteSpace(actorType) ||
            string.IsNullOrWhiteSpace(credentialKind))
        {
            throw new ManagementAuthorizationException(
                ManagementPermission.PanelAccess,
                "invalid_session_principal");
        }

        BearerScope? scope = null;
        if (!string.IsNullOrWhiteSpace(scopeValue))
        {
            if (!Enum.TryParse<BearerScope>(scopeValue, ignoreCase: true, out var parsedScope))
            {
                throw new ManagementAuthorizationException(
                    ManagementPermission.PanelAccess,
                    "invalid_session_principal");
            }

            scope = parsedScope;
        }

        long? generation = null;
        if (!string.IsNullOrWhiteSpace(generationValue))
        {
            if (!long.TryParse(generationValue, out var parsedGeneration) || parsedGeneration <= 0)
            {
                throw new ManagementAuthorizationException(
                    ManagementPermission.PanelAccess,
                    "invalid_session_principal");
            }

            generation = parsedGeneration;
        }

        return new ManagementRequestContext(
            new ManagementPrincipal(actorType, actorId, credentialKind, scope, generation),
            ManagementIdentifiers.NormalizeCorrelationId(suppliedCorrelationId),
            string.IsNullOrWhiteSpace(requestId) ? null : requestId.Trim(),
            source);
    }

    public static ClaimsPrincipal CreateClaimsPrincipal(ManagementPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.Name, principal.ActorId),
            new Claim(ClaimTypes.NameIdentifier, principal.ActorId),
            new Claim(ActorIdClaim, principal.ActorId),
            new Claim(ActorTypeClaim, principal.ActorType),
            new Claim(CredentialKindClaim, principal.CredentialKind),
        };
        if (principal.LegacyScope is { } scope)
        {
            claims.Add(new Claim(LegacyScopeClaim, scope.ToString()));
        }

        if (principal.CredentialGeneration is { } generation)
        {
            claims.Add(new Claim(
                CredentialGenerationClaim,
                generation.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        var identity = new ClaimsIdentity(claims, "Cookies");
        return new ClaimsPrincipal(identity);
    }
}

public static class ManagementIdentifiers
{
    public static string NewId() => Guid.CreateVersion7().ToString("N");

    public static string NormalizeCorrelationId(string? supplied)
    {
        if (string.IsNullOrWhiteSpace(supplied))
        {
            return NewId();
        }

        var value = supplied.Trim();
        if (value.Length is < 8 or > 128 ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_' and not '.' and not ':'))
        {
            throw new ArgumentException("correlation ID must be 8-128 ASCII letters, digits, '.', ':', '_' or '-'");
        }

        return value;
    }
}
