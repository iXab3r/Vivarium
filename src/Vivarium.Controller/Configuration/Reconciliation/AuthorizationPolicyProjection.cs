using Microsoft.Data.Sqlite;
using Vivarium.Controller.Configuration.Git;

namespace Vivarium.Controller.Configuration.Reconciliation;

internal sealed class AuthorizationPolicyProjection : IConfigurationProjectionApplier
{
    public void Apply(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ValidatedConfigurationRevision revision,
        string revisionSetId,
        DateTimeOffset appliedAt)
    {
        var repositoryId = revision.Descriptor.Revision.RepositoryId;
        DeletePriorProjection(connection, transaction, repositoryId);

        foreach (var document in revision.Documents
                     .Where(document => document.Kind == "User")
                     .OrderBy(document => document.Id, StringComparer.Ordinal))
        {
            var login = Required(document, "spec.login");
            var displayName = Required(document, "spec.displayName");
            if (!bool.TryParse(Required(document, "spec.active"), out var active))
            {
                throw ProjectionError(
                    "authorization_user_active_invalid",
                    document,
                    "spec.active",
                    "the validated User document has no canonical active value");
            }

            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO authorization_desired_users(
                    user_id, login, display_name, desired_active,
                    source_repository_id, source_commit, content_hash,
                    source_revision_set_id, applied_unix_ms)
                VALUES (
                    $userId, $login, $displayName, $active,
                    $repositoryId, $commit, $contentHash,
                    $revisionSetId, $appliedAt);
                """;
            insert.Parameters.AddWithValue("$userId", document.Id);
            insert.Parameters.AddWithValue("$login", login);
            insert.Parameters.AddWithValue("$displayName", displayName);
            insert.Parameters.AddWithValue("$active", active ? 1 : 0);
            AddProvenanceParameters(insert, revision, document, revisionSetId, appliedAt);
            insert.ExecuteNonQuery();
        }

        foreach (var document in revision.Documents
                     .Where(document => document.Kind == "RoleBinding")
                     .OrderBy(document => document.Id, StringComparer.Ordinal))
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO authorization_role_bindings(
                    binding_id, principal_type, principal_id, role_id, scope_kind, scope_id,
                    source_repository_id, source_commit, content_hash,
                    source_revision_set_id, applied_unix_ms)
                VALUES (
                    $bindingId, $principalType, $principalId, $roleId, $scopeKind, $scopeId,
                    $repositoryId, $commit, $contentHash,
                    $revisionSetId, $appliedAt);
                """;
            insert.Parameters.AddWithValue("$bindingId", document.Id);
            insert.Parameters.AddWithValue(
                "$principalType", Required(document, "spec.principalType"));
            insert.Parameters.AddWithValue(
                "$principalId", Required(document, "spec.principalId"));
            insert.Parameters.AddWithValue("$roleId", Required(document, "spec.roleId"));
            insert.Parameters.AddWithValue("$scopeKind", Required(document, "spec.scopeType"));
            insert.Parameters.AddWithValue("$scopeId", Required(document, "spec.scopeId"));
            AddProvenanceParameters(insert, revision, document, revisionSetId, appliedAt);
            insert.ExecuteNonQuery();
        }
    }

    private static void DeletePriorProjection(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string repositoryId)
    {
        using (var bindings = connection.CreateCommand())
        {
            bindings.Transaction = transaction;
            bindings.CommandText = """
                DELETE FROM authorization_role_bindings
                WHERE source_repository_id = $repositoryId;
                """;
            bindings.Parameters.AddWithValue("$repositoryId", repositoryId);
            bindings.ExecuteNonQuery();
        }

        using var users = connection.CreateCommand();
        users.Transaction = transaction;
        users.CommandText = """
            DELETE FROM authorization_desired_users
            WHERE source_repository_id = $repositoryId;
            """;
        users.Parameters.AddWithValue("$repositoryId", repositoryId);
        users.ExecuteNonQuery();
    }

    private static void AddProvenanceParameters(
        SqliteCommand command,
        ValidatedConfigurationRevision revision,
        ValidatedConfigurationDocument document,
        string revisionSetId,
        DateTimeOffset appliedAt)
    {
        command.Parameters.AddWithValue(
            "$repositoryId", revision.Descriptor.Revision.RepositoryId);
        command.Parameters.AddWithValue("$commit", revision.Descriptor.Revision.Commit);
        command.Parameters.AddWithValue("$contentHash", document.ContentHash);
        command.Parameters.AddWithValue("$revisionSetId", revisionSetId);
        command.Parameters.AddWithValue("$appliedAt", appliedAt.ToUnixTimeMilliseconds());
    }

    private static string Required(ValidatedConfigurationDocument document, string field)
    {
        if (!document.ScalarFields.TryGetValue(field, out var value) ||
            string.IsNullOrWhiteSpace(value))
        {
            throw ProjectionError(
                "authorization_field_missing",
                document,
                field,
                "the validated authorization document is missing a canonical field");
        }

        return value;
    }

    private static ConfigurationProjectionException ProjectionError(
        string code,
        ValidatedConfigurationDocument document,
        string field,
        string summary) => new(code, document.Path, field, summary);
}
