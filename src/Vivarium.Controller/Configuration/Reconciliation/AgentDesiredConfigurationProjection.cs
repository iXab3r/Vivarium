using Microsoft.Data.Sqlite;
using Vivarium.Controller.Configuration.Git;

namespace Vivarium.Controller.Configuration.Reconciliation;

internal sealed class AgentDesiredConfigurationProjection : IConfigurationProjectionApplier
{
    public void Apply(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ValidatedConfigurationRevision revision,
        string revisionSetId,
        DateTimeOffset appliedAt)
    {
        var documents = revision.Documents.Where(document =>
                string.Equals(document.Kind, "Agent", StringComparison.Ordinal))
            .ToArray();
        var desiredAgentIds = documents.Select(document => document.Id)
            .ToHashSet(StringComparer.Ordinal);
        var removedAgentId = ReadMaterializedAgentIds(
                connection,
                transaction,
                revision.Descriptor.Revision.RepositoryId)
            .FirstOrDefault(agentId => !desiredAgentIds.Contains(agentId));
        if (removedAgentId is not null)
        {
            throw new ConfigurationProjectionException(
                "agent_document_removal_unsupported",
                $".vivarium/agents/{removedAgentId}.yaml",
                "id",
                "removing a materialized Agent document is not supported by this configuration schema");
        }

        foreach (var document in documents)
        {
            if (!document.ScalarFields.TryGetValue("spec.enabled", out var enabledText) ||
                !bool.TryParse(enabledText, out var enabled))
            {
                throw new ConfigurationProjectionException(
                    "agent_enabled_invalid",
                    document.Path,
                    "spec.enabled",
                    "the validated Agent document has no canonical enabled value");
            }

            if (!AgentExists(connection, transaction, document.Id))
            {
                throw new ConfigurationProjectionException(
                    "agent_registration_not_found",
                    document.Path,
                    "id",
                    "the desired Agent document does not match an enrolled registration");
            }

            using (var projection = connection.CreateCommand())
            {
                projection.Transaction = transaction;
                projection.CommandText = """
                    INSERT INTO agent_desired_configuration(
                        agent_id, enabled, source_repository_id, source_commit,
                        content_hash, source_revision_set_id, applied_unix_ms)
                    VALUES (
                        $agentId, $enabled, $repositoryId, $commit,
                        $contentHash, $revisionSetId, $appliedAt)
                    ON CONFLICT(agent_id) DO UPDATE SET
                        enabled = excluded.enabled,
                        source_repository_id = excluded.source_repository_id,
                        source_commit = excluded.source_commit,
                        content_hash = excluded.content_hash,
                        source_revision_set_id = excluded.source_revision_set_id,
                        applied_unix_ms = excluded.applied_unix_ms;
                    """;
                projection.Parameters.AddWithValue("$agentId", document.Id);
                projection.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
                projection.Parameters.AddWithValue(
                    "$repositoryId",
                    revision.Descriptor.Revision.RepositoryId);
                projection.Parameters.AddWithValue("$commit", revision.Descriptor.Revision.Commit);
                projection.Parameters.AddWithValue("$contentHash", document.ContentHash);
                projection.Parameters.AddWithValue("$revisionSetId", revisionSetId);
                projection.Parameters.AddWithValue("$appliedAt", appliedAt.ToUnixTimeMilliseconds());
                projection.ExecuteNonQuery();
            }

            using var runtime = connection.CreateCommand();
            runtime.Transaction = transaction;
            runtime.CommandText = """
                UPDATE agents SET enabled = $enabled WHERE agent_id = $agentId;
                """;
            runtime.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
            runtime.Parameters.AddWithValue("$agentId", document.Id);
            if (runtime.ExecuteNonQuery() != 1)
            {
                throw new ConfigurationProjectionException(
                    "agent_registration_not_found",
                    document.Path,
                    "id",
                    "the desired Agent document does not match an enrolled registration");
            }
        }
    }

    private static IReadOnlyList<string> ReadMaterializedAgentIds(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string repositoryId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT agent_id
            FROM agent_desired_configuration
            WHERE source_repository_id = $repositoryId
            ORDER BY agent_id COLLATE BINARY;
            """;
        command.Parameters.AddWithValue("$repositoryId", repositoryId);
        using var reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read())
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    private static bool AgentExists(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string agentId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM agents WHERE agent_id = $agentId;";
        command.Parameters.AddWithValue("$agentId", agentId);
        return command.ExecuteScalar() is not null;
    }
}
