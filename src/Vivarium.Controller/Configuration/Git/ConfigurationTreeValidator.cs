using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Vivarium.Controller.Security;

namespace Vivarium.Controller.Configuration.Git;

internal sealed partial class ConfigurationTreeValidator(string repositoryId)
{
    public const string RepositoryManifestPath = ".vivarium/repository.yaml";
    public const string ApiVersion = "vivarium.io/v1alpha1";
    public const string SchemaVersion = "1";

    private const int MaxDocumentBytes = 64 * 1024;
    public const int MaxTreeEntries = 4096;
    public const long MaxAggregateTreeBytes = 4L * 1024 * 1024;
    public const int MaxPathDepth = 8;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public ConfigurationDocumentNormalization NormalizeMutationDocument(
        string path,
        ReadOnlyMemory<byte> content)
    {
        var normalizedPath = NormalizePath(path);
        if (normalizedPath is null || !IsResourcePath(normalizedPath))
        {
            return ConfigurationDocumentNormalization.Rejected(
                content,
                Diagnostic(
                    "CONFIG_PATH_FORBIDDEN",
                    path,
                    null,
                    "This configuration path is not writable in the current repository schema."));
        }

        if (content.Length > MaxDocumentBytes)
        {
            return ConfigurationDocumentNormalization.Rejected(
                content,
                Diagnostic(
                    "CONFIG_DOCUMENT_TOO_LARGE",
                    normalizedPath,
                    null,
                    "The configuration document exceeds the 64 KiB limit."));
        }

        if (!TryDecode(content.Span, out var text))
        {
            return ConfigurationDocumentNormalization.Rejected(
                content,
                Diagnostic(
                    "CONFIG_DOCUMENT_ENCODING",
                    normalizedPath,
                    null,
                    "Configuration documents must be UTF-8 without a byte-order mark."));
        }

        var secretDiagnostic = FindForbiddenSecret(text, normalizedPath);
        if (secretDiagnostic is not null)
        {
            return ConfigurationDocumentNormalization.Rejected(content, secretDiagnostic);
        }

        if (!TryNormalizeResource(
                text,
                normalizedPath,
                out var canonical,
                out var diagnostic))
        {
            return ConfigurationDocumentNormalization.Rejected(content, diagnostic!);
        }

        return ConfigurationDocumentNormalization.Accepted(
            normalizedPath,
            canonical!,
            Hash(canonical!));
    }

    public ConfigurationTreeValidation Validate(
        IReadOnlyList<ConfigurationTreeDocument> documents)
    {
        var diagnostics = new List<ConfigurationValidationDiagnostic>();
        var validated = new List<ValidatedConfigurationDocument>();
        var configurationDocuments = documents
            .OrderBy(document => document.Path, StringComparer.Ordinal)
            .ToArray();

        if (configurationDocuments.Length > MaxTreeEntries)
        {
            diagnostics.Add(Diagnostic(
                "CONFIG_TREE_ENTRY_LIMIT",
                null,
                null,
                "The configuration tree exceeds the 4096-entry limit."));
        }

        if (configurationDocuments.Sum(document => Math.Max(0, document.DeclaredSize)) >
            MaxAggregateTreeBytes)
        {
            diagnostics.Add(Diagnostic(
                "CONFIG_TREE_SIZE_LIMIT",
                null,
                null,
                "The configuration tree exceeds the 4 MiB aggregate limit."));
        }

        foreach (var collision in configurationDocuments
                     .GroupBy(document => document.Path, StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Select(document => document.Path)
                         .Distinct(StringComparer.Ordinal).Skip(1).Any()))
        {
            diagnostics.Add(Diagnostic(
                "CONFIG_PATH_CASE_COLLISION",
                collision.First().Path,
                null,
                "Configuration paths must not collide by case."));
        }

        foreach (var document in configurationDocuments)
        {
            if (!document.Path.StartsWith(".vivarium/", StringComparison.Ordinal))
            {
                diagnostics.Add(Diagnostic(
                    "CONFIG_PATH_FORBIDDEN",
                    document.Path,
                    null,
                    "Managed-local v1alpha1 repositories contain only the reserved .vivarium tree."));
                continue;
            }

            if (document.Path.Split('/').Length > MaxPathDepth)
            {
                diagnostics.Add(Diagnostic(
                    "CONFIG_PATH_DEPTH_LIMIT",
                    document.Path,
                    null,
                    "The configuration path exceeds the supported depth."));
                continue;
            }

            if (!string.Equals(document.Mode, "100644", StringComparison.Ordinal))
            {
                diagnostics.Add(Diagnostic(
                    "CONFIG_DOCUMENT_MODE",
                    document.Path,
                    null,
                    "Configuration documents must be ordinary files."));
                continue;
            }

            if (document.DeclaredSize > MaxDocumentBytes ||
                document.Utf8Bytes.Length > MaxDocumentBytes)
            {
                diagnostics.Add(Diagnostic(
                    "CONFIG_DOCUMENT_TOO_LARGE",
                    document.Path,
                    null,
                    "The configuration document exceeds the 64 KiB limit."));
                continue;
            }

            if (!TryDecode(document.Utf8Bytes.Span, out var text))
            {
                diagnostics.Add(Diagnostic(
                    "CONFIG_DOCUMENT_ENCODING",
                    document.Path,
                    null,
                    "Configuration documents must be UTF-8 without a byte-order mark."));
                continue;
            }

            var secretDiagnostic = FindForbiddenSecret(text, document.Path);
            if (secretDiagnostic is not null)
            {
                diagnostics.Add(secretDiagnostic);
                continue;
            }

            if (string.Equals(document.Path, RepositoryManifestPath, StringComparison.Ordinal))
            {
                var expected = RenderRepositoryManifest(repositoryId);
                if (!document.Utf8Bytes.Span.SequenceEqual(expected))
                {
                    diagnostics.Add(Diagnostic(
                        "CONFIG_REPOSITORY_MANIFEST_INVALID",
                        document.Path,
                        null,
                        "The repository manifest is missing required fields or is not canonical."));
                    continue;
                }

                validated.Add(new ValidatedConfigurationDocument(
                    document.Path,
                    ApiVersion,
                    "Repository",
                    repositoryId,
                    Hash(document.Utf8Bytes.Span),
                    document.Utf8Bytes.ToArray(),
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["spec.schemaVersion"] = SchemaVersion,
                        ["spec.documentKinds"] = "Agent,RoleBinding,User",
                    }));
                continue;
            }

            if (!IsResourcePath(document.Path))
            {
                diagnostics.Add(Diagnostic(
                    "CONFIG_PATH_FORBIDDEN",
                    document.Path,
                    null,
                    "This path is not part of the current configuration repository schema."));
                continue;
            }

            if (!TryValidateResource(
                    text,
                    document.Path,
                    document.Utf8Bytes,
                    out var validatedDocument,
                    out var diagnostic))
            {
                diagnostics.Add(diagnostic!);
                continue;
            }

            validated.Add(validatedDocument!);
        }

        if (!configurationDocuments.Any(document =>
                string.Equals(document.Path, RepositoryManifestPath, StringComparison.Ordinal)))
        {
            diagnostics.Add(Diagnostic(
                "CONFIG_REPOSITORY_MANIFEST_MISSING",
                RepositoryManifestPath,
                null,
                "The repository manifest is required."));
        }

        ValidateAuthorizationReferences(validated, diagnostics);

        return new ConfigurationTreeValidation(
            diagnostics.Count == 0
                ? validated.OrderBy(document => document.Path, StringComparer.Ordinal).ToArray()
                : [],
            diagnostics.Take(64).ToArray());
    }

    public static byte[] RenderRepositoryManifest(string id) =>
        StrictUtf8.GetBytes($"""
            apiVersion: {ApiVersion}
            kind: Repository
            id: {id}
            spec:
              schemaVersion: "{SchemaVersion}"
              documentKinds:
                - Agent
                - RoleBinding
                - User

            """);

    internal static byte[] RenderAgent(string id, bool enabled) =>
        StrictUtf8.GetBytes($"""
            apiVersion: {ApiVersion}
            kind: Agent
            id: {id}
            spec:
              enabled: {enabled.ToString().ToLowerInvariant()}

            """);

    internal static byte[] RenderUser(
        string id,
        string login,
        string displayName,
        bool active) => StrictUtf8.GetBytes($"""
            apiVersion: {ApiVersion}
            kind: User
            id: {id}
            spec:
              login: {login}
              displayName: {JsonSerializer.Serialize(displayName)}
              active: {active.ToString().ToLowerInvariant()}

            """);

    internal static byte[] RenderRoleBinding(
        string id,
        string principalType,
        string principalId,
        string roleId,
        string scopeType,
        string scopeId) => StrictUtf8.GetBytes($"""
            apiVersion: {ApiVersion}
            kind: RoleBinding
            id: {id}
            spec:
              principalType: {principalType}
              principalId: {principalId}
              roleId: {roleId}
              scopeType: {scopeType}
              scopeId: {scopeId}

            """);

    private static bool TryNormalizeResource(
        string text,
        string path,
        out byte[]? canonical,
        out ConfigurationValidationDiagnostic? diagnostic)
    {
        if (!TryParseResource(text, path, out var parsed, out diagnostic))
        {
            canonical = null;
            return false;
        }

        canonical = parsed!.CanonicalBytes;
        return true;
    }

    private static bool TryValidateResource(
        string text,
        string path,
        ReadOnlyMemory<byte> original,
        out ValidatedConfigurationDocument? document,
        out ConfigurationValidationDiagnostic? diagnostic)
    {
        document = null;
        if (!TryParseResource(text, path, out var parsed, out diagnostic))
        {
            return false;
        }

        if (!original.Span.SequenceEqual(parsed!.CanonicalBytes))
        {
            diagnostic = Diagnostic(
                "CONFIG_DOCUMENT_NOT_CANONICAL",
                path,
                null,
                $"The {parsed.Kind} document must use canonical UTF-8, LF line endings, field order, and spacing.");
            return false;
        }

        document = new ValidatedConfigurationDocument(
            path,
            ApiVersion,
            parsed.Kind,
            parsed.Id,
            Hash(parsed.CanonicalBytes),
            parsed.CanonicalBytes,
            parsed.ScalarFields);
        return true;
    }

    private static bool TryParseResource(
        string text,
        string path,
        out ParsedResource? resource,
        out ConfigurationValidationDiagnostic? diagnostic)
    {
        resource = null;
        if (AgentPathRegex().IsMatch(path))
        {
            if (!TryParseAgent(text, path, out var agent, out diagnostic))
            {
                return false;
            }

            var canonical = RenderAgent(agent!.Id, agent.Enabled);
            resource = new ParsedResource(
                "Agent",
                agent.Id,
                canonical,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["spec.enabled"] = agent.Enabled ? "true" : "false",
                });
            return true;
        }

        if (UserPathRegex().IsMatch(path))
        {
            if (!TryParseUser(text, path, out var user, out diagnostic))
            {
                return false;
            }

            var canonical = RenderUser(user!.Id, user.Login, user.DisplayName, user.Active);
            resource = new ParsedResource(
                "User",
                user.Id,
                canonical,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["spec.login"] = user.Login,
                    ["spec.displayName"] = user.DisplayName,
                    ["spec.active"] = user.Active ? "true" : "false",
                });
            return true;
        }

        if (RoleBindingPathRegex().IsMatch(path))
        {
            if (!TryParseRoleBinding(text, path, out var binding, out diagnostic))
            {
                return false;
            }

            var canonical = RenderRoleBinding(
                binding!.Id,
                binding.PrincipalType,
                binding.PrincipalId,
                binding.RoleId,
                binding.ScopeType,
                binding.ScopeId);
            resource = new ParsedResource(
                "RoleBinding",
                binding.Id,
                canonical,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["spec.principalType"] = binding.PrincipalType,
                    ["spec.principalId"] = binding.PrincipalId,
                    ["spec.roleId"] = binding.RoleId,
                    ["spec.scopeType"] = binding.ScopeType,
                    ["spec.scopeId"] = binding.ScopeId,
                });
            return true;
        }

        diagnostic = Diagnostic(
            "CONFIG_PATH_FORBIDDEN",
            path,
            null,
            "This path is not part of the current configuration repository schema.");
        return false;
    }

    private static bool TryParseAgent(
        string text,
        string path,
        out ParsedAgent? parsed,
        out ConfigurationValidationDiagnostic? diagnostic)
    {
        parsed = null;
        diagnostic = null;
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .TrimEnd('\n');
        var lines = normalized.Split('\n');
        if (lines.Length != 5 ||
            !string.Equals(lines[0], $"apiVersion: {ApiVersion}", StringComparison.Ordinal) ||
            !string.Equals(lines[1], "kind: Agent", StringComparison.Ordinal) ||
            !lines[2].StartsWith("id: ", StringComparison.Ordinal) ||
            !string.Equals(lines[3], "spec:", StringComparison.Ordinal) ||
            !lines[4].StartsWith("  enabled: ", StringComparison.Ordinal))
        {
            diagnostic = Diagnostic(
                "CONFIG_AGENT_SCHEMA_INVALID",
                path,
                null,
                "The Agent document must contain only apiVersion, kind, id, and spec.enabled in schema order.");
            return false;
        }

        var id = lines[2][4..];
        var enabledText = lines[4][11..];
        if (!AgentIdRegex().IsMatch(id))
        {
            diagnostic = Diagnostic(
                "CONFIG_AGENT_ID_INVALID",
                path,
                "id",
                "Agent IDs must be lowercase stable ASCII identifiers.");
            return false;
        }

        var expectedPath = $".vivarium/agents/{id}.yaml";
        if (!string.Equals(path, expectedPath, StringComparison.Ordinal))
        {
            diagnostic = Diagnostic(
                "CONFIG_AGENT_ID_PATH_MISMATCH",
                path,
                "id",
                "The Agent document ID must match its canonical path.");
            return false;
        }

        if (enabledText is not ("true" or "false"))
        {
            diagnostic = Diagnostic(
                "CONFIG_AGENT_ENABLED_INVALID",
                path,
                "spec.enabled",
                "Agent spec.enabled must be an explicit boolean.");
            return false;
        }

        parsed = new ParsedAgent(id, enabledText == "true");
        return true;
    }

    private static bool TryParseUser(
        string text,
        string path,
        out ParsedUser? parsed,
        out ConfigurationValidationDiagnostic? diagnostic)
    {
        parsed = null;
        diagnostic = null;
        var lines = NormalizeDocumentLines(text);
        if (lines.Length != 7 ||
            !string.Equals(lines[0], $"apiVersion: {ApiVersion}", StringComparison.Ordinal) ||
            !string.Equals(lines[1], "kind: User", StringComparison.Ordinal) ||
            !lines[2].StartsWith("id: ", StringComparison.Ordinal) ||
            !string.Equals(lines[3], "spec:", StringComparison.Ordinal) ||
            !lines[4].StartsWith("  login: ", StringComparison.Ordinal) ||
            !lines[5].StartsWith("  displayName: ", StringComparison.Ordinal) ||
            !lines[6].StartsWith("  active: ", StringComparison.Ordinal))
        {
            diagnostic = Diagnostic(
                "CONFIG_USER_SCHEMA_INVALID",
                path,
                null,
                "The User document must contain only id, login, displayName, and active in schema order.");
            return false;
        }

        var id = lines[2][4..];
        var login = lines[4][9..];
        var activeText = lines[6][10..];
        if (!ResourceIdRegex().IsMatch(id) ||
            !string.Equals(path, $".vivarium/rbac/users/{id}.yaml", StringComparison.Ordinal))
        {
            diagnostic = Diagnostic(
                "CONFIG_USER_ID_INVALID",
                path,
                "id",
                "User IDs must be lowercase stable ASCII identifiers matching their canonical path.");
            return false;
        }

        if (!LoginRegex().IsMatch(login))
        {
            diagnostic = Diagnostic(
                "CONFIG_USER_LOGIN_INVALID",
                path,
                "spec.login",
                "User login must contain 1-128 safe ASCII characters.");
            return false;
        }

        string? displayName;
        try
        {
            displayName = JsonSerializer.Deserialize<string>(lines[5][15..]);
        }
        catch (JsonException)
        {
            displayName = null;
        }

        if (string.IsNullOrWhiteSpace(displayName) || displayName.Length > 256 ||
            displayName.Any(char.IsControl))
        {
            diagnostic = Diagnostic(
                "CONFIG_USER_DISPLAY_NAME_INVALID",
                path,
                "spec.displayName",
                "User display name must be a quoted printable string of 1-256 characters.");
            return false;
        }

        if (activeText is not ("true" or "false"))
        {
            diagnostic = Diagnostic(
                "CONFIG_USER_ACTIVE_INVALID",
                path,
                "spec.active",
                "User spec.active must be an explicit boolean.");
            return false;
        }

        parsed = new ParsedUser(id, login, displayName, activeText == "true");
        return true;
    }

    private static bool TryParseRoleBinding(
        string text,
        string path,
        out ParsedRoleBinding? parsed,
        out ConfigurationValidationDiagnostic? diagnostic)
    {
        parsed = null;
        diagnostic = null;
        var lines = NormalizeDocumentLines(text);
        string[] prefixes =
        [
            "  principalType: ",
            "  principalId: ",
            "  roleId: ",
            "  scopeType: ",
            "  scopeId: ",
        ];
        if (lines.Length != 9 ||
            !string.Equals(lines[0], $"apiVersion: {ApiVersion}", StringComparison.Ordinal) ||
            !string.Equals(lines[1], "kind: RoleBinding", StringComparison.Ordinal) ||
            !lines[2].StartsWith("id: ", StringComparison.Ordinal) ||
            !string.Equals(lines[3], "spec:", StringComparison.Ordinal) ||
            prefixes.Where((prefix, index) =>
                    !lines[index + 4].StartsWith(prefix, StringComparison.Ordinal))
                .Any())
        {
            diagnostic = Diagnostic(
                "CONFIG_ROLE_BINDING_SCHEMA_INVALID",
                path,
                null,
                "The RoleBinding document must contain only principal, role, and scope fields in schema order.");
            return false;
        }

        var id = lines[2][4..];
        var principalType = lines[4][prefixes[0].Length..];
        var principalId = lines[5][prefixes[1].Length..];
        var roleId = lines[6][prefixes[2].Length..];
        var scopeType = lines[7][prefixes[3].Length..];
        var scopeId = lines[8][prefixes[4].Length..];
        if (!ResourceIdRegex().IsMatch(id) ||
            !string.Equals(path, $".vivarium/rbac/bindings/{id}.yaml", StringComparison.Ordinal))
        {
            diagnostic = Diagnostic(
                "CONFIG_ROLE_BINDING_ID_INVALID",
                path,
                "id",
                "RoleBinding IDs must be lowercase stable ASCII identifiers matching their canonical path.");
            return false;
        }

        if (principalType != "user" || !ResourceIdRegex().IsMatch(principalId))
        {
            diagnostic = Diagnostic(
                "CONFIG_ROLE_BINDING_PRINCIPAL_INVALID",
                path,
                "spec.principalId",
                "The initial RoleBinding schema supports one stable user principal reference.");
            return false;
        }

        if (!AuthorizationRoleIds.IsBuiltIn(roleId))
        {
            diagnostic = Diagnostic(
                "CONFIG_ROLE_BINDING_ROLE_INVALID",
                path,
                "spec.roleId",
                "RoleBinding roleId must name one product-defined built-in role.");
            return false;
        }

        var scopeKind = TryParseScope(scopeType);
        if (scopeKind is null ||
            !AuthorizationRoleIds.IsLegalScope(roleId, scopeKind.Value) ||
            !ValidScopeId(scopeKind.Value, scopeId))
        {
            diagnostic = Diagnostic(
                "CONFIG_ROLE_BINDING_SCOPE_INVALID",
                path,
                "spec.scopeType",
                "The role cannot be assigned at the requested scope.");
            return false;
        }

        parsed = new ParsedRoleBinding(
            id, principalType, principalId, roleId, scopeType, scopeId);
        return true;
    }

    private static void ValidateAuthorizationReferences(
        IReadOnlyList<ValidatedConfigurationDocument> documents,
        ICollection<ConfigurationValidationDiagnostic> diagnostics)
    {
        var users = documents.Where(document => document.Kind == "User")
            .ToDictionary(document => document.Id, StringComparer.Ordinal);
        foreach (var duplicate in users.Values
                     .GroupBy(document => document.ScalarFields["spec.login"], StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Skip(1).Any()))
        {
            diagnostics.Add(Diagnostic(
                "CONFIG_USER_LOGIN_DUPLICATE",
                duplicate.First().Path,
                "spec.login",
                "User logins must be unique without regard to ASCII case."));
        }

        foreach (var binding in documents.Where(document => document.Kind == "RoleBinding"))
        {
            var principalId = binding.ScalarFields["spec.principalId"];
            if (!users.ContainsKey(principalId))
            {
                diagnostics.Add(Diagnostic(
                    "CONFIG_ROLE_BINDING_PRINCIPAL_NOT_FOUND",
                    binding.Path,
                    "spec.principalId",
                    "The RoleBinding references a user absent from this configuration revision."));
            }
        }
    }

    private static string[] NormalizeDocumentLines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .TrimEnd('\n')
            .Split('\n');

    private static AuthorizationScopeKind? TryParseScope(string value) => value switch
    {
        "global" => AuthorizationScopeKind.Global,
        "project" => AuthorizationScopeKind.Project,
        "fleet" => AuthorizationScopeKind.Fleet,
        "pool" => AuthorizationScopeKind.Pool,
        _ => null,
    };

    private static bool ValidScopeId(AuthorizationScopeKind scope, string scopeId) => scope switch
    {
        AuthorizationScopeKind.Global => scopeId == "global",
        AuthorizationScopeKind.Fleet => scopeId == "fleet-root",
        AuthorizationScopeKind.Project or AuthorizationScopeKind.Pool =>
            ResourceIdRegex().IsMatch(scopeId),
        _ => false,
    };

    private static ConfigurationValidationDiagnostic? FindForbiddenSecret(string text, string path)
    {
        if (PrivateKeyRegex().IsMatch(text) ||
            BearerRegex().IsMatch(text) ||
            CredentialUrlRegex().IsMatch(text))
        {
            return Diagnostic(
                "CONFIG_SECRET_VALUE_FORBIDDEN",
                path,
                null,
                "Configuration must contain secret references only; credential material is forbidden.");
        }

        foreach (Match match in YamlKeyRegex().Matches(text))
        {
            var key = match.Groups[1].Value;
            if (SecretKeyRegex().IsMatch(key) && !key.EndsWith("Ref", StringComparison.OrdinalIgnoreCase))
            {
                return Diagnostic(
                    "CONFIG_SECRET_FIELD_FORBIDDEN",
                    path,
                    key,
                    "Secret-bearing fields must use a typed reference and cannot contain plaintext values.");
            }
        }

        return null;
    }

    private static bool TryDecode(ReadOnlySpan<byte> content, out string text)
    {
        text = string.Empty;
        if (content.StartsWith(Encoding.UTF8.Preamble))
        {
            return false;
        }

        try
        {
            text = StrictUtf8.GetString(content);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static string? NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            path.Length > 512 ||
            path.Contains('\\') ||
            path.StartsWith('/') ||
            path.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            return null;
        }

        return path;
    }

    private static bool IsResourcePath(string path) =>
        AgentPathRegex().IsMatch(path) ||
        UserPathRegex().IsMatch(path) ||
        RoleBindingPathRegex().IsMatch(path);

    private static string Hash(ReadOnlySpan<byte> content) =>
        Convert.ToHexStringLower(SHA256.HashData(content));

    private static ConfigurationValidationDiagnostic Diagnostic(
        string code,
        string? path,
        string? field,
        string summary) =>
        new(
            code,
            BoundSafe(path, 512),
            BoundSafe(field, 128),
            BoundSafe(summary, 512) ?? "Configuration validation failed.");

    private static string? BoundSafe(string? value, int maxLength)
    {
        if (value is null)
        {
            return null;
        }

        var safe = new string(value
            .Take(maxLength)
            .Select(character => char.IsControl(character) ? '?' : character)
            .ToArray());
        return safe;
    }

    private sealed record ParsedResource(
        string Kind,
        string Id,
        byte[] CanonicalBytes,
        IReadOnlyDictionary<string, string> ScalarFields);

    private sealed record ParsedAgent(string Id, bool Enabled);

    private sealed record ParsedUser(
        string Id,
        string Login,
        string DisplayName,
        bool Active);

    private sealed record ParsedRoleBinding(
        string Id,
        string PrincipalType,
        string PrincipalId,
        string RoleId,
        string ScopeType,
        string ScopeId);

    [GeneratedRegex("^\\.vivarium/agents/[a-z0-9](?:[a-z0-9.-]{0,125}[a-z0-9])?\\.yaml$", RegexOptions.CultureInvariant)]
    private static partial Regex AgentPathRegex();

    [GeneratedRegex("^\\.vivarium/rbac/users/[a-z0-9](?:[a-z0-9.-]{0,125}[a-z0-9])?\\.yaml$", RegexOptions.CultureInvariant)]
    private static partial Regex UserPathRegex();

    [GeneratedRegex("^\\.vivarium/rbac/bindings/[a-z0-9](?:[a-z0-9.-]{0,125}[a-z0-9])?\\.yaml$", RegexOptions.CultureInvariant)]
    private static partial Regex RoleBindingPathRegex();

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9.-]{0,125}[a-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex AgentIdRegex();

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9.-]{0,125}[a-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex ResourceIdRegex();

    [GeneratedRegex("^[A-Za-z0-9](?:[A-Za-z0-9._-]{0,127})$", RegexOptions.CultureInvariant)]
    private static partial Regex LoginRegex();

    [GeneratedRegex("-----BEGIN(?: [A-Z0-9]+)? PRIVATE KEY-----", RegexOptions.CultureInvariant)]
    private static partial Regex PrivateKeyRegex();

    [GeneratedRegex("(?i)\\bbearer[ \\t]+[A-Za-z0-9._~+/=-]{8,}", RegexOptions.CultureInvariant)]
    private static partial Regex BearerRegex();

    [GeneratedRegex("(?i)\\b(?:https?|ssh)://[^/\\s:@]+:[^/\\s@]+@", RegexOptions.CultureInvariant)]
    private static partial Regex CredentialUrlRegex();

    [GeneratedRegex("(?m)^\\s*([A-Za-z][A-Za-z0-9_-]*)\\s*:", RegexOptions.CultureInvariant)]
    private static partial Regex YamlKeyRegex();

    [GeneratedRegex("(?i)(?:password|passwd|token|secret|private[_-]?key|credential|api[_-]?key)", RegexOptions.CultureInvariant)]
    private static partial Regex SecretKeyRegex();
}

internal sealed record ConfigurationTreeDocument(
    string Path,
    string Mode,
    ReadOnlyMemory<byte> Utf8Bytes,
    long DeclaredSize);

internal sealed record ConfigurationTreeValidation(
    IReadOnlyList<ValidatedConfigurationDocument> Documents,
    IReadOnlyList<ConfigurationValidationDiagnostic> Diagnostics)
{
    public bool IsValid => Diagnostics.Count == 0;
}

internal sealed record ConfigurationDocumentNormalization(
    string? Path,
    ReadOnlyMemory<byte> CanonicalBytes,
    string ContentHash,
    IReadOnlyList<ConfigurationValidationDiagnostic> Diagnostics)
{
    public bool IsValid => Diagnostics.Count == 0;

    public static ConfigurationDocumentNormalization Accepted(
        string path,
        ReadOnlyMemory<byte> canonicalBytes,
        string contentHash) =>
        new(path, canonicalBytes, contentHash, []);

    public static ConfigurationDocumentNormalization Rejected(
        ReadOnlyMemory<byte> originalBytes,
        ConfigurationValidationDiagnostic diagnostic) =>
        new(
            null,
            ReadOnlyMemory<byte>.Empty,
            Convert.ToHexStringLower(SHA256.HashData(originalBytes.Span)),
            [diagnostic]);
}
