using Microsoft.Data.Sqlite;
using Vivarium.Controller.Persistence;

namespace Vivarium.Tests;

[TestFixture]
public sealed class DatabaseMigrationTests
{
    private string rootDir = null!;

    [SetUp]
    public void SetUp()
    {
        rootDir = Path.Combine(Path.GetTempPath(), "vivarium-migration-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootDir);
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            Directory.Delete(rootDir, recursive: true);
        }
        catch
        {
            // Best effort: a failed database assertion must remain visible.
        }
    }

    [Test]
    public async Task Fresh_database_applies_the_complete_immutable_manifest()
    {
        await using var database = new VivariumDatabase(rootDir);

        var state = await database.ReadAsync(connection =>
        {
            using var migrations = connection.CreateCommand();
            migrations.CommandText = """
                SELECT migration_number, migration_name, length(checksum)
                FROM schema_migrations ORDER BY migration_number;
                """;
            using var reader = migrations.ExecuteReader();
            var rows = new List<(int Number, string Name, long ChecksumLength)>();
            while (reader.Read())
            {
                rows.Add((reader.GetInt32(0), reader.GetString(1), reader.GetInt64(2)));
            }

            using var metadata = connection.CreateCommand();
            metadata.CommandText = "SELECT current_version FROM schema_metadata WHERE metadata_id = 1;";
            return (Rows: rows, Version: Convert.ToInt32(metadata.ExecuteScalar()));
        });

        Assert.Multiple(() =>
        {
            Assert.That(state.Rows.Select(row => row.Number),
                Is.EqualTo(new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 }));
            Assert.That(state.Rows.Select(row => row.Name),
                Is.EqualTo(new[]
                {
                    "phase1-baseline",
                    "minimal-audit-journal",
                    "principal-matrix-idempotency",
                    "typed-agent-fact-observations",
                    "git-configuration-reconciliation",
                    "configuration-mutation-evidence",
                    "blob-access-and-build-events",
                    "durable-trx-projections",
                    "resumable-first-run-claim",
                    "git-backed-authorization-policy",
                    "private-user-credentials",
                    "agent-package-upgrade-operations",
                }));
            Assert.That(state.Rows.All(row => row.ChecksumLength == 64), Is.True);
            Assert.That(state.Version, Is.EqualTo(VivariumDatabase.CurrentSchemaVersion));
        });
    }

    [Test]
    public async Task Coordinated_agent_upgrade_without_its_exact_drain_fails_closed_on_restart()
    {
        await using (var database = new VivariumDatabase(rootDir))
        {
        }
        ExecuteRaw($"""
            INSERT INTO agents(agent_id, name, first_seen_unix_ms, last_seen_unix_ms)
            VALUES ('upgrade-agent', 'upgrade-agent', 1, 1);
            INSERT INTO agent_packages(
                package_id, version, rid, sha256, size, source,
                actor_type, actor_id, correlation_id, created_unix_ms)
            VALUES (
                '{new string('p', 32)}', '2.0.0', 'linux-x64', '{new string('a', 64)}', 1,
                'test', 'system', 'test', 'test-correlation', 1);
            INSERT INTO agent_upgrade_operations(
                operation_id, agent_id, package_id, state,
                actor_type, actor_id, credential_kind, request_id, request_hash,
                correlation_id, reason, maintenance_fence, prior_package_sha256,
                starting_connection_generation, created_unix_ms, updated_unix_ms,
                deadline_unix_ms)
            VALUES (
                '{new string('o', 32)}', 'upgrade-agent', '{new string('p', 32)}', 'DRAINING',
                'system', 'test', 'test', 'upgrade-request', '{new string('b', 64)}',
                'test-correlation', 'test', 1, '{new string('c', 64)}',
                1, 1, 1, 1000);
            """);

        var exception = Assert.Throws<InvalidDataException>(() => new VivariumDatabase(rootDir));
        Assert.That(exception!.Message, Does.Contain("operation/drain rows"));
    }

    [Test]
    public async Task Version_five_upgrade_adds_mutation_evidence_without_rewriting_operations()
    {
        await using (var database = new VivariumDatabase(rootDir))
        {
        }
        DowngradeWaveFourSchemaToVersionSix();
        ExecuteRaw("""
            INSERT INTO configuration_mutation_operations(
                operation_id, operation_kind, materialization_scope,
                actor_type, actor_id, credential_kind, request_id, correlation_id, request_source,
                repository_id, expected_base_commit, request_hash, state,
                created_unix_ms, updated_unix_ms)
            VALUES (
                'legacy-pending', 'agent.set-enabled', 'controller',
                'user', 'legacy-user', 'test', 'legacy-request', 'legacy-correlation', 'test',
                'controller', 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
                'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb',
                'PENDING', 1, 1);

            DROP TABLE configuration_repository_attempt_failures;
            DROP TABLE configuration_mutation_conflicts;
            DROP TABLE configuration_mutation_targets;
            DELETE FROM schema_migrations WHERE migration_number = 6;
            UPDATE schema_metadata SET current_version = 5 WHERE metadata_id = 1;
            """);

        await using var upgraded = new VivariumDatabase(rootDir);
        var state = await upgraded.ReadAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    (SELECT state FROM configuration_mutation_operations
                        WHERE operation_id = 'legacy-pending'),
                    (SELECT COUNT(*) FROM sqlite_master WHERE type = 'table'
                        AND name IN (
                            'configuration_mutation_targets',
                            'configuration_mutation_conflicts',
                            'configuration_repository_attempt_failures')),
                    (SELECT COUNT(*) FROM sqlite_master WHERE type = 'index'
                        AND name = 'configuration_repository_attempt_failures_by_operation'),
                    (SELECT current_version FROM schema_metadata WHERE metadata_id = 1);
                """;
            using var reader = command.ExecuteReader();
            Assert.That(reader.Read(), Is.True);
            return (
                OperationState: reader.GetString(0),
                EvidenceTableCount: reader.GetInt64(1),
                IndexCount: reader.GetInt64(2),
                Version: reader.GetInt32(3));
        });

        Assert.That(state, Is.EqualTo((
            "PENDING",
            3L,
            1L,
            VivariumDatabase.CurrentSchemaVersion)));
    }

    [Test]
    public async Task Version_six_upgrade_preserves_legacy_matrix_mapping_and_adds_wave_four_schema()
    {
        await using (var database = new VivariumDatabase(rootDir))
        {
        }
        DropAdministrationBootstrapSchema();
        ExecuteRaw("""
            DROP TABLE trx_test_occurrences;
            DROP TABLE trx_test_definitions;
            DROP TABLE trx_result_projections;
            DROP TABLE build_test_projection_states;

            INSERT INTO matrix_builds(
                matrix_build_id, request_id, request_hash, request_payload,
                project, configuration, definition_snapshot, definition_hash,
                created_unix_ms, updated_unix_ms)
            VALUES (
                'legacy-matrix-v6', 'legacy-storage-request-v6',
                'legacy-request-hash', X'01',
                'legacy-project', 'legacy-configuration', X'02', 'legacy-definition',
                1234, 1234);

            INSERT INTO matrix_build_idempotency(
                actor_type, actor_id, operation_kind, request_id, request_hash,
                matrix_build_id, created_unix_ms)
            VALUES (
                'service', 'legacy-principal', 'temporary-v7-shape', 'legacy-client-request',
                'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
                'legacy-matrix-v6', 1234);

            DROP TABLE build_events;
            DROP TABLE build_event_streams;
            DROP TABLE blob_build_artifact_references;
            DROP TABLE blob_build_artifact_sets;
            DROP TABLE blob_artifact_upload_receipts;
            DROP TABLE blob_artifact_upload_staging;
            DROP TABLE blob_build_payload_references;
            DROP TABLE blob_build_payload_sets;
            DROP TABLE blob_principal_project_grants;
            DROP TABLE blob_upload_receipts;
            DROP TABLE blob_upload_plan_items;
            DROP TABLE blob_upload_plans;

            CREATE TABLE matrix_build_idempotency_v6 (
                actor_type TEXT NOT NULL,
                actor_id TEXT NOT NULL,
                request_id TEXT NOT NULL,
                matrix_build_id TEXT NOT NULL UNIQUE
                    REFERENCES matrix_builds(matrix_build_id) ON DELETE CASCADE,
                PRIMARY KEY (actor_type, actor_id, request_id)
            );
            INSERT INTO matrix_build_idempotency_v6(
                actor_type, actor_id, request_id, matrix_build_id)
            SELECT actor_type, actor_id, request_id, matrix_build_id
            FROM matrix_build_idempotency;
            DROP TABLE matrix_build_idempotency;
            ALTER TABLE matrix_build_idempotency_v6 RENAME TO matrix_build_idempotency;

            DELETE FROM schema_migrations WHERE migration_number >= 7;
            UPDATE schema_metadata SET current_version = 6 WHERE metadata_id = 1;
            """);

        await using var upgraded = new VivariumDatabase(rootDir);
        var state = await upgraded.ReadAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT actor_type, actor_id, operation_kind, request_id, request_hash,
                    matrix_build_id, response_status, response_json, response_etag, created_unix_ms,
                    (SELECT COUNT(*) FROM sqlite_master
                        WHERE type = 'table' AND name IN (
                            'blob_upload_plans', 'blob_upload_plan_items',
                            'blob_upload_receipts', 'blob_principal_project_grants',
                            'blob_build_payload_sets', 'blob_build_payload_references',
                            'blob_artifact_upload_staging', 'blob_artifact_upload_receipts',
                            'blob_build_artifact_sets', 'blob_build_artifact_references',
                            'build_event_streams', 'build_events')),
                    (SELECT current_version FROM schema_metadata WHERE metadata_id = 1)
                FROM matrix_build_idempotency
                WHERE matrix_build_id = 'legacy-matrix-v6';
                """;
            using var reader = command.ExecuteReader();
            Assert.That(reader.Read(), Is.True);
            return (
                ActorType: reader.GetString(0),
                ActorId: reader.GetString(1),
                OperationKind: reader.GetString(2),
                RequestId: reader.GetString(3),
                RequestHash: reader.GetString(4),
                MatrixBuildId: reader.GetString(5),
                ResponseStatusIsNull: reader.IsDBNull(6),
                ResponseJsonIsNull: reader.IsDBNull(7),
                ResponseEtagIsNull: reader.IsDBNull(8),
                CreatedUnixMs: reader.GetInt64(9),
                WaveFourTableCount: reader.GetInt64(10),
                Version: reader.GetInt32(11));
        });

        Assert.Multiple(() =>
        {
            Assert.That(state.ActorType, Is.EqualTo("service"));
            Assert.That(state.ActorId, Is.EqualTo("legacy-principal"));
            Assert.That(state.OperationKind, Is.EqualTo("legacy-control-plane"));
            Assert.That(state.RequestId, Is.EqualTo("legacy-client-request"));
            Assert.That(state.RequestHash,
                Is.EqualTo("legacy-request-hash"));
            Assert.That(state.MatrixBuildId, Is.EqualTo("legacy-matrix-v6"));
            Assert.That(state.ResponseStatusIsNull, Is.True);
            Assert.That(state.ResponseJsonIsNull, Is.True);
            Assert.That(state.ResponseEtagIsNull, Is.True);
            Assert.That(state.CreatedUnixMs, Is.EqualTo(1234));
            Assert.That(state.WaveFourTableCount, Is.EqualTo(12));
            Assert.That(state.Version, Is.EqualTo(VivariumDatabase.CurrentSchemaVersion));
        });
    }

    [Test]
    public async Task Version_eight_upgrade_adds_resumable_first_run_claim_schema()
    {
        await using (var database = new VivariumDatabase(rootDir))
        {
        }
        DropAdministrationBootstrapSchema();
        ExecuteRaw("""
            DELETE FROM schema_migrations WHERE migration_number >= 9;
            UPDATE schema_metadata SET current_version = 8 WHERE metadata_id = 1;
            """);

        await using var upgraded = new VivariumDatabase(rootDir);
        var state = await upgraded.ReadAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    (SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN (
                        'administration_instances',
                        'administration_setup_operations',
                        'administration_token_generations',
                        'administration_setup_sessions',
                        'administration_setup_requests')),
                    (SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name IN (
                        'administration_token_generations_one_current',
                        'administration_token_generations_due',
                        'administration_setup_sessions_one_current',
                        'administration_setup_sessions_due')),
                    (SELECT migration_name FROM schema_migrations WHERE migration_number = 9),
                    (SELECT current_version FROM schema_metadata WHERE metadata_id = 1);
                """;
            using var reader = command.ExecuteReader();
            Assert.That(reader.Read(), Is.True);
            return (
                Tables: reader.GetInt64(0),
                Indexes: reader.GetInt64(1),
                Migration: reader.GetString(2),
                Version: reader.GetInt32(3));
        });

        Assert.That(state, Is.EqualTo((
            5L,
            4L,
            "resumable-first-run-claim",
            VivariumDatabase.CurrentSchemaVersion)));
    }

    [Test]
    public async Task Version_nine_upgrade_adds_git_backed_authorization_projection_schema()
    {
        await using (var database = new VivariumDatabase(rootDir))
        {
        }
        DropAuthorizationPolicySchema();
        ExecuteRaw("""
            DELETE FROM schema_migrations WHERE migration_number >= 10;
            UPDATE schema_metadata SET current_version = 9 WHERE metadata_id = 1;
            """);

        await using var upgraded = new VivariumDatabase(rootDir);
        var state = await upgraded.ReadAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    (SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN (
                        'authorization_desired_users',
                        'authorization_role_bindings')),
                    (SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name IN (
                        'authorization_role_bindings_by_principal',
                        'authorization_role_bindings_by_scope')),
                    (SELECT migration_name FROM schema_migrations WHERE migration_number = 10),
                    (SELECT current_version FROM schema_metadata WHERE metadata_id = 1);
                """;
            using var reader = command.ExecuteReader();
            Assert.That(reader.Read(), Is.True);
            return (
                Tables: reader.GetInt64(0),
                Indexes: reader.GetInt64(1),
                Migration: reader.GetString(2),
                Version: reader.GetInt32(3));
        });

        Assert.That(state, Is.EqualTo((
            2L,
            2L,
            "git-backed-authorization-policy",
            VivariumDatabase.CurrentSchemaVersion)));
    }

    [Test]
    public async Task Version_ten_upgrade_adds_private_user_credential_schema()
    {
        await using (var database = new VivariumDatabase(rootDir))
        {
        }
        DropUserCredentialSchema();
        ExecuteRaw("""
            DELETE FROM schema_migrations WHERE migration_number >= 11;
            UPDATE schema_metadata SET current_version = 10 WHERE metadata_id = 1;
            """);

        await using var upgraded = new VivariumDatabase(rootDir);
        var state = await upgraded.ReadAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    (SELECT COUNT(*) FROM sqlite_master WHERE type = 'table'
                        AND name = 'authorization_user_credentials'),
                    (SELECT migration_name FROM schema_migrations WHERE migration_number = 11),
                    (SELECT current_version FROM schema_metadata WHERE metadata_id = 1);
                """;
            using var reader = command.ExecuteReader();
            Assert.That(reader.Read(), Is.True);
            return (
                Tables: reader.GetInt64(0),
                Migration: reader.GetString(1),
                Version: reader.GetInt32(2));
        });

        Assert.That(state, Is.EqualTo((
            1L,
            "private-user-credentials",
            VivariumDatabase.CurrentSchemaVersion)));
    }

    [Test]
    public async Task Version_eleven_upgrade_adds_agent_package_and_upgrade_operation_schema()
    {
        await using (var database = new VivariumDatabase(rootDir))
        {
        }
        DropAgentPackageUpgradeSchema();
        ExecuteRaw("""
            DELETE FROM schema_migrations WHERE migration_number = 12;
            UPDATE schema_metadata SET current_version = 11 WHERE metadata_id = 1;
            """);

        await using var upgraded = new VivariumDatabase(rootDir);
        var state = await upgraded.ReadAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    (SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN (
                        'agent_packages', 'agent_package_publication_requests',
                        'agent_upgrade_operations', 'agent_upgrade_events',
                        'agent_maintenance_drains')),
                    (SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name IN (
                        'agent_packages_by_rid', 'agent_upgrade_operations_by_agent',
                        'agent_upgrade_operations_due', 'agent_upgrade_operations_one_active',
                        'agent_upgrade_events_by_operation')),
                    (SELECT migration_name FROM schema_migrations WHERE migration_number = 12),
                    (SELECT current_version FROM schema_metadata WHERE metadata_id = 1);
                """;
            using var reader = command.ExecuteReader();
            Assert.That(reader.Read(), Is.True);
            return (
                Tables: reader.GetInt64(0),
                Indexes: reader.GetInt64(1),
                Migration: reader.GetString(2),
                Version: reader.GetInt32(3));
        });

        Assert.That(state, Is.EqualTo((
            5L,
            5L,
            "agent-package-upgrade-operations",
            VivariumDatabase.CurrentSchemaVersion)));
    }

    [Test]
    public async Task Wave_four_blob_and_event_rows_survive_restart_with_fences_and_cursor_indexes()
    {
        await using (var database = new VivariumDatabase(rootDir))
        {
            await database.WriteAsync(connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO agents(
                        agent_id, name, first_seen_unix_ms, last_seen_unix_ms)
                    VALUES ('artifact-agent', 'Artifact Agent', 1, 1);

                    INSERT INTO builds(
                        build_id, agent_id, state, assignment, created_unix_ms, updated_unix_ms)
                    VALUES ('artifact-build', 'artifact-agent', 'FINISHED', X'01', 10, 20);

                    INSERT INTO matrix_builds(
                        matrix_build_id, request_id, request_hash, request_payload,
                        project, configuration, definition_snapshot, definition_hash,
                        created_unix_ms, updated_unix_ms)
                    VALUES (
                        'payload-matrix', 'payload-storage-request',
                        'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', X'02',
                        'project-one', 'configuration-one', X'03', 'definition-one', 10, 10);

                    INSERT INTO matrix_build_idempotency(
                        actor_type, actor_id, operation_kind, request_id, request_hash,
                        matrix_build_id, response_status, response_json, response_etag,
                        created_unix_ms)
                    VALUES (
                        'user', 'principal-one', 'rest:POST:/api/v1/builds', 'client-request',
                        'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
                        'payload-matrix', 202, '{}', '"response-etag"', 10);

                    INSERT INTO blob_upload_plans(
                        staging_id, actor_type, actor_id, project_id, operation_kind,
                        request_id, request_hash, created_unix_ms, expires_unix_ms)
                    VALUES (
                        'payload-stage', 'user', 'principal-one', 'project-one',
                        'rest:POST:/api/v1/builds', 'client-request',
                        'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
                        10, 20);
                    INSERT INTO blob_upload_plan_items(staging_id, sha256, declared_size)
                    VALUES (
                        'payload-stage',
                        'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 12);
                    INSERT INTO blob_upload_receipts(
                        staging_id, sha256, declared_size, received_unix_ms)
                    VALUES (
                        'payload-stage',
                        'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
                        12, 11);
                    INSERT INTO blob_principal_project_grants(
                        actor_type, actor_id, project_id, sha256, declared_size,
                        source_staging_id, granted_unix_ms)
                    VALUES (
                        'user', 'principal-one', 'project-one',
                        'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
                        12, 'payload-stage', 11);
                    INSERT INTO blob_build_payload_sets(
                        matrix_build_id, staging_id, actor_type, actor_id, project_id,
                        operation_kind, request_id, attached_unix_ms)
                    VALUES (
                        'payload-matrix', 'payload-stage', 'user', 'principal-one',
                        'project-one', 'rest:POST:/api/v1/builds', 'client-request', 12);
                    INSERT INTO blob_build_payload_references(
                        matrix_build_id, sha256, declared_size, source_staging_id)
                    VALUES (
                        'payload-matrix',
                        'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
                        12, 'payload-stage');

                    INSERT INTO blob_artifact_upload_staging(
                        build_id, sha256, declared_size, agent_id, owner_session_id,
                        connection_generation, created_unix_ms, expires_unix_ms)
                    VALUES (
                        'artifact-build',
                        'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb',
                        21, 'artifact-agent', 'session-one', 3, 20, 30);
                    INSERT INTO blob_artifact_upload_receipts(
                        build_id, sha256, declared_size, agent_id, owner_session_id,
                        connection_generation, received_unix_ms)
                    VALUES (
                        'artifact-build',
                        'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb',
                        21, 'artifact-agent', 'session-one', 3, 21);
                    INSERT INTO blob_build_artifact_sets(
                        build_id, agent_id, owner_session_id, connection_generation,
                        attached_unix_ms)
                    VALUES ('artifact-build', 'artifact-agent', 'session-one', 3, 22);
                    INSERT INTO blob_build_artifact_references(
                        build_id, artifact_id, relative_path, sha256, declared_size,
                        source_agent_id, source_session_id, source_connection_generation,
                        attached_unix_ms)
                    VALUES (
                        'artifact-build', 'test-report', 'results/report.xml',
                        'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb',
                        21, 'artifact-agent', 'session-one', 3, 22);

                    INSERT INTO build_events(
                        event_id, matrix_build_id, event_type, occurred_unix_ms,
                        correlation_id, actor_type, actor_id, runtime_revision, resource_url)
                    VALUES (
                        'bevt_0000000000000001_aaaaaaaaaaaa', 'payload-matrix',
                        'build.queued', 10, 'correlation-one', 'user', 'principal-one',
                        'runtime:1', '/api/v1/builds/payload-matrix');
                    INSERT INTO build_event_streams(
                        matrix_build_id, minimum_retained_sequence, latest_sequence,
                        updated_unix_ms)
                    VALUES ('payload-matrix', 1, 1, 10);
                    """;
                command.ExecuteNonQuery();
                return true;
            });
        }

        await using var reopened = new VivariumDatabase(rootDir);
        var state = await reopened.ReadAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    (SELECT COUNT(*) FROM blob_principal_project_grants),
                    (SELECT COUNT(*) FROM blob_build_payload_references),
                    (SELECT COUNT(*) FROM blob_build_artifact_references),
                    (SELECT minimum_retained_sequence FROM build_event_streams
                        WHERE matrix_build_id = 'payload-matrix'),
                    (SELECT latest_sequence FROM build_event_streams
                        WHERE matrix_build_id = 'payload-matrix'),
                    (SELECT event_id FROM build_events WHERE matrix_build_id = 'payload-matrix'),
                    (SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name IN (
                        'blob_upload_plans_due', 'blob_upload_plan_items_by_hash',
                        'blob_upload_receipts_by_hash',
                        'blob_principal_project_grants_by_hash',
                        'blob_build_payload_references_by_hash',
                        'blob_artifact_upload_staging_due',
                        'blob_artifact_upload_receipts_by_hash',
                        'blob_build_artifact_references_by_hash',
                        'ix_build_events_matrix_sequence')),
                    (SELECT COUNT(*) FROM pragma_foreign_key_check);
                """;
            using var reader = command.ExecuteReader();
            Assert.That(reader.Read(), Is.True);
            return (
                Grants: reader.GetInt64(0),
                PayloadReferences: reader.GetInt64(1),
                ArtifactReferences: reader.GetInt64(2),
                MinimumSequence: reader.GetInt64(3),
                LatestSequence: reader.GetInt64(4),
                EventId: reader.GetString(5),
                NamedIndexes: reader.GetInt64(6),
                ForeignKeyViolations: reader.GetInt64(7));
        });

        Assert.Multiple(() =>
        {
            Assert.That(state.Grants, Is.EqualTo(1));
            Assert.That(state.PayloadReferences, Is.EqualTo(1));
            Assert.That(state.ArtifactReferences, Is.EqualTo(1));
            Assert.That(state.MinimumSequence, Is.EqualTo(1));
            Assert.That(state.LatestSequence, Is.EqualTo(1));
            Assert.That(state.EventId, Is.EqualTo("bevt_0000000000000001_aaaaaaaaaaaa"));
            Assert.That(state.NamedIndexes, Is.EqualTo(9));
            Assert.That(state.ForeignKeyViolations, Is.Zero);
        });

        Assert.That(
            Assert.Throws<SqliteException>(() => ExecuteRaw("""
                PRAGMA foreign_keys = ON;
                DELETE FROM matrix_builds WHERE matrix_build_id = 'payload-matrix';
                """))!.SqliteErrorCode,
            Is.EqualTo(19));
        Assert.That(
            Assert.Throws<SqliteException>(() => ExecuteRaw("""
                PRAGMA foreign_keys = ON;
                DELETE FROM builds WHERE build_id = 'artifact-build';
                """))!.SqliteErrorCode,
            Is.EqualTo(19));
    }

    [Test]
    public async Task Wave_four_schema_rejects_invalid_hashes_orphans_partial_responses_and_cursors()
    {
        await using (var database = new VivariumDatabase(rootDir))
        {
        }
        ExecuteRaw("""
            INSERT INTO builds(
                build_id, state, assignment, created_unix_ms, updated_unix_ms)
            VALUES ('constraint-build', 'FINISHED', X'01', 1, 1);
            INSERT INTO matrix_builds(
                matrix_build_id, request_id, request_hash, request_payload,
                project, configuration, definition_snapshot, definition_hash,
                created_unix_ms, updated_unix_ms)
            VALUES (
                'constraint-matrix', 'constraint-storage-request',
                'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', X'01',
                'project', 'configuration', X'01', 'definition', 1, 1);
            INSERT INTO matrix_build_idempotency(
                actor_type, actor_id, operation_kind, request_id, request_hash,
                matrix_build_id, created_unix_ms)
            VALUES (
                'user', 'constraint-principal', 'rest:POST:/api/v1/builds',
                'constraint-client-request',
                'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
                'constraint-matrix', 1);
            INSERT INTO matrix_builds(
                matrix_build_id, request_id, request_hash, request_payload,
                project, configuration, definition_snapshot, definition_hash,
                created_unix_ms, updated_unix_ms)
            VALUES (
                'constraint-matrix-second', 'constraint-storage-request-second',
                'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb', X'02',
                'project', 'configuration', X'02', 'definition', 2, 2);
            INSERT INTO matrix_build_idempotency(
                actor_type, actor_id, operation_kind, request_id, request_hash,
                matrix_build_id, response_status, response_json, response_etag,
                created_unix_ms)
            VALUES (
                'user', 'constraint-principal', 'different-operation',
                'constraint-client-request',
                'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb',
                'constraint-matrix-second', 201, '{}', '"second"', 2);

            INSERT INTO blob_upload_plans(
                staging_id, actor_type, actor_id, project_id, operation_kind,
                request_id, request_hash, created_unix_ms, expires_unix_ms)
            VALUES (
                'size-stage', 'user', 'principal', 'project', 'operation', 'size-request',
                'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 1, 2);
            INSERT INTO blob_upload_plan_items(staging_id, sha256, declared_size)
            VALUES (
                'size-stage',
                'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 1);
            INSERT INTO blob_upload_receipts(
                staging_id, sha256, declared_size, received_unix_ms)
            VALUES (
                'size-stage',
                'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 1, 1);

            INSERT INTO blob_artifact_upload_staging(
                build_id, sha256, declared_size, agent_id, owner_session_id,
                connection_generation, created_unix_ms, expires_unix_ms)
            VALUES (
                'constraint-build',
                'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb',
                1, 'agent', 'session', 1, 1, 2);
            INSERT INTO blob_artifact_upload_receipts(
                build_id, sha256, declared_size, agent_id, owner_session_id,
                connection_generation, received_unix_ms)
            VALUES (
                'constraint-build',
                'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb',
                1, 'agent', 'session', 1, 1);
            INSERT INTO blob_build_artifact_sets(
                build_id, agent_id, owner_session_id, connection_generation, attached_unix_ms)
            VALUES ('constraint-build', 'agent', 'session', 1, 1);
            """);

        string[] rejectedStatements =
        [
            """
            INSERT INTO blob_upload_plans(
                staging_id, actor_type, actor_id, project_id, operation_kind,
                request_id, request_hash, created_unix_ms, expires_unix_ms)
            VALUES (
                'bad-hash', 'user', 'principal', 'project', 'operation', 'request',
                'AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA', 1, 2);
            """,
            """
            PRAGMA foreign_keys = ON;
            INSERT INTO blob_upload_plan_items(staging_id, sha256, declared_size)
            VALUES (
                'missing-stage',
                'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 1);
            """,
            """
            INSERT INTO blob_upload_plans(
                staging_id, actor_type, actor_id, project_id, operation_kind,
                request_id, request_hash, created_unix_ms, expires_unix_ms)
            VALUES (
                'bad-expiry', 'user', 'principal', 'project', 'operation', 'request',
                'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 2, 2);
            """,
            """
            UPDATE matrix_build_idempotency
            SET response_status = 202
            WHERE matrix_build_id = 'constraint-matrix';
            """,
            """
            UPDATE matrix_build_idempotency
            SET request_hash = 'not-a-sha256'
            WHERE matrix_build_id = 'constraint-matrix-second';
            """,
            """
            UPDATE matrix_build_idempotency
            SET operation_kind = 'legacy-control-plane',
                request_hash = 'unsafe' || char(10) || 'legacy-hash'
            WHERE matrix_build_id = 'constraint-matrix-second';
            """,
            """
            PRAGMA foreign_keys = ON;
            INSERT INTO blob_principal_project_grants(
                actor_type, actor_id, project_id, sha256, declared_size,
                source_staging_id, granted_unix_ms)
            VALUES (
                'user', 'principal', 'project',
                'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
                2, 'size-stage', 1);
            """,
            """
            PRAGMA foreign_keys = ON;
            INSERT INTO blob_build_artifact_references(
                build_id, artifact_id, relative_path, sha256, declared_size,
                source_agent_id, source_session_id, source_connection_generation,
                attached_unix_ms)
            VALUES (
                'constraint-build', 'wrong-size', 'wrong-size.txt',
                'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb',
                2, 'agent', 'session', 1, 1);
            """,
            """
            INSERT INTO build_events(
                event_id, matrix_build_id, event_type, occurred_unix_ms,
                correlation_id, actor_type, actor_id, runtime_revision, resource_url)
            VALUES (
                'not-a-cursor', 'constraint-matrix', 'build.queued', 1,
                'correlation', 'user', 'principal', 'runtime:1', '/api/v1/builds/x');
            """,
            """
            INSERT INTO build_event_streams(
                matrix_build_id, minimum_retained_sequence, latest_sequence, updated_unix_ms)
            VALUES ('constraint-matrix', 3, 1, 1);
            """,
            """
            INSERT INTO blob_artifact_upload_staging(
                build_id, sha256, declared_size, agent_id, owner_session_id,
                connection_generation, created_unix_ms, expires_unix_ms)
            VALUES (
                'constraint-build',
                'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb',
                1, 'agent', 'session', 0, 1, 2);
            """,
        ];

        foreach (var sql in rejectedStatements)
        {
            var exception = Assert.Throws<SqliteException>(() => ExecuteRaw(sql));
            Assert.That(exception!.SqliteErrorCode, Is.EqualTo(19), sql);
        }

        Assert.That(ReadRawString("""
            SELECT printf('%d:%d:%d',
                response_status IS NULL, response_json IS NULL, response_etag IS NULL)
            FROM matrix_build_idempotency
            WHERE matrix_build_id = 'constraint-matrix';
            """), Is.EqualTo("1:1:1"));
        Assert.That(ReadRawInt("""
            SELECT COUNT(*) FROM matrix_build_idempotency
            WHERE actor_type = 'user' AND actor_id = 'constraint-principal'
                AND request_id = 'constraint-client-request';
            """), Is.EqualTo(2));
    }

    [Test]
    public void Unknown_unversioned_schema_fails_without_claiming_it_as_a_baseline()
    {
        ExecuteRaw("CREATE TABLE mystery_state (id INTEGER PRIMARY KEY);");

        var exception = Assert.Throws<InvalidDataException>(() => _ = new VivariumDatabase(rootDir));

        Assert.That(exception!.Message, Does.Contain("unknown tables"));
        Assert.That(ReadRawInt(
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'schema_migrations';"),
            Is.Zero);
    }

    [Test]
    public async Task Altered_applied_migration_checksum_is_rejected()
    {
        await using (var database = new VivariumDatabase(rootDir))
        {
        }
        ExecuteRaw("UPDATE schema_migrations SET checksum = 'tampered' WHERE migration_number = 1;");

        var exception = Assert.Throws<InvalidDataException>(() => _ = new VivariumDatabase(rootDir));

        Assert.That(exception!.Message, Does.Contain("immutable manifest"));
    }

    [Test]
    public async Task Newer_schema_is_rejected_by_an_older_controller()
    {
        await using (var database = new VivariumDatabase(rootDir))
        {
        }
        ExecuteRaw("""
            INSERT INTO schema_migrations(
                migration_number, migration_name, checksum, controller_version, applied_unix_ms)
            VALUES (999, 'future', 'future', '999.0.0', 0);
            UPDATE schema_metadata SET current_version = 999 WHERE metadata_id = 1;
            """);

        var exception = Assert.Throws<InvalidDataException>(() => _ = new VivariumDatabase(rootDir));

        Assert.That(exception!.Message, Does.Contain("newer than this controller supports"));
    }

    [Test]
    public void Failed_legacy_upgrade_rolls_back_every_change_from_that_migration()
    {
        ExecuteRaw("""
            CREATE TABLE agents (
                agent_id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                authorized INTEGER NOT NULL,
                enabled INTEGER NOT NULL,
                first_seen_unix_ms INTEGER NOT NULL,
                last_seen_unix_ms INTEGER NOT NULL,
                unsupported_column TEXT NOT NULL
            );
            """);

        var exception = Assert.Throws<InvalidDataException>(() => _ = new VivariumDatabase(rootDir));

        Assert.Multiple(() =>
        {
            Assert.That(exception!.Message, Does.Contain("unsupported shape"));
            Assert.That(ReadRawInt(
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'schema_migrations';"),
                Is.Zero);
            Assert.That(ReadRawInt(
                "SELECT COUNT(*) FROM pragma_table_info('agents') WHERE name = 'custom_parameters_json';"),
                Is.Zero);
            Assert.That(ReadRawInt(
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'builds';"),
                Is.Zero);
        });
    }

    [Test]
    public async Task Versioned_database_with_unknown_table_is_rejected_as_drift()
    {
        await using (var database = new VivariumDatabase(rootDir))
        {
        }
        ExecuteRaw("CREATE TABLE hidden_authority (id INTEGER PRIMARY KEY);");

        var exception = Assert.Throws<InvalidDataException>(() => _ = new VivariumDatabase(rootDir));

        Assert.That(exception!.Message, Does.Contain("unknown tables"));
    }

    [Test]
    public async Task Interrupted_metadata_state_is_rejected_instead_of_repaired_silently()
    {
        await using (var database = new VivariumDatabase(rootDir))
        {
        }
        ExecuteRaw("UPDATE schema_metadata SET current_version = 1 WHERE metadata_id = 1;");

        var exception = Assert.Throws<InvalidDataException>(() => _ = new VivariumDatabase(rootDir));

        Assert.That(exception!.Message, Does.Contain("does not match its applied migration history"));
    }

    [Test]
    public async Task Populated_earliest_phase_one_database_upgrades_without_data_loss()
    {
        ExecuteRaw("""
            CREATE TABLE agents (
                agent_id TEXT PRIMARY KEY,
                name TEXT NOT NULL UNIQUE,
                authorized INTEGER NOT NULL DEFAULT 0,
                enabled INTEGER NOT NULL DEFAULT 1,
                auth_token_hash TEXT NULL,
                pending_auth_token TEXT NULL,
                enroll_token_hash TEXT NULL,
                first_seen_unix_ms INTEGER NOT NULL,
                last_seen_unix_ms INTEGER NOT NULL,
                parameters_json TEXT NOT NULL DEFAULT '{}',
                agent_version TEXT NOT NULL DEFAULT '',
                os_family TEXT NOT NULL DEFAULT '',
                os_version TEXT NOT NULL DEFAULT '',
                architecture TEXT NOT NULL DEFAULT '',
                interactive INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE builds (
                build_id TEXT PRIMARY KEY,
                agent_id TEXT NOT NULL,
                state TEXT NOT NULL CHECK (state IN ('RUNNING', 'CANCEL_REQUESTED', 'FINISHED')),
                assignment BLOB NOT NULL,
                result BLOB NULL,
                cancellation_reason TEXT NULL,
                created_unix_ms INTEGER NOT NULL,
                updated_unix_ms INTEGER NOT NULL
            );

            CREATE TABLE build_queue (
                queue_id INTEGER PRIMARY KEY AUTOINCREMENT,
                build_id TEXT NOT NULL UNIQUE REFERENCES builds(build_id) ON DELETE CASCADE,
                agent_expression TEXT NOT NULL,
                state TEXT NOT NULL CHECK (state IN ('QUEUED', 'CLAIMED', 'REMOVED')),
                claimed_agent_id TEXT NULL,
                enqueued_unix_ms INTEGER NOT NULL,
                claimed_unix_ms INTEGER NULL,
                removed_unix_ms INTEGER NULL,
                removal_reason TEXT NULL,
                CHECK (
                    (state = 'QUEUED' AND claimed_agent_id IS NULL AND claimed_unix_ms IS NULL
                        AND removed_unix_ms IS NULL)
                    OR (state = 'CLAIMED' AND claimed_agent_id IS NOT NULL AND claimed_unix_ms IS NOT NULL
                        AND removed_unix_ms IS NULL)
                    OR (state = 'REMOVED' AND removed_unix_ms IS NOT NULL)
                )
            );

            CREATE TABLE matrix_builds (
                matrix_build_id TEXT PRIMARY KEY,
                request_id TEXT NOT NULL UNIQUE,
                request_hash TEXT NOT NULL,
                request_payload BLOB NOT NULL,
                project TEXT NOT NULL,
                configuration TEXT NOT NULL,
                definition_snapshot BLOB NOT NULL,
                definition_hash TEXT NOT NULL,
                created_unix_ms INTEGER NOT NULL,
                updated_unix_ms INTEGER NOT NULL
            );

            CREATE TABLE matrix_build_cells (
                matrix_build_id TEXT NOT NULL
                    REFERENCES matrix_builds(matrix_build_id) ON DELETE CASCADE,
                cell_name TEXT NOT NULL,
                ordinal INTEGER NOT NULL,
                build_id TEXT NOT NULL UNIQUE REFERENCES builds(build_id),
                agent_expression TEXT NOT NULL,
                PRIMARY KEY (matrix_build_id, cell_name),
                UNIQUE (matrix_build_id, ordinal)
            );

            CREATE UNIQUE INDEX builds_one_active_per_agent
                ON builds(agent_id) WHERE state <> 'FINISHED';

            CREATE INDEX build_queue_pending_fifo ON build_queue(state, queue_id);
            CREATE UNIQUE INDEX build_queue_one_claim_per_agent
                ON build_queue(claimed_agent_id) WHERE state = 'CLAIMED';
            CREATE INDEX matrix_build_cells_build ON matrix_build_cells(build_id);

            INSERT INTO agents(
                agent_id, name, authorized, enabled, first_seen_unix_ms, last_seen_unix_ms,
                parameters_json, agent_version, os_family, os_version, architecture, interactive)
            VALUES (
                'legacy-agent', 'Legacy Agent', 1, 1, 10, 20,
                '{"system.os.family":"windows"}', '0.1', 'windows', '10.0', 'x64', 1);

            INSERT INTO builds(
                build_id, agent_id, state, assignment, result, cancellation_reason,
                created_unix_ms, updated_unix_ms)
            VALUES ('legacy-build', 'legacy-agent', 'RUNNING', X'0A00', NULL, NULL, 11, 12);

            INSERT INTO build_queue(
                build_id, agent_expression, state, enqueued_unix_ms,
                removed_unix_ms, removal_reason)
            VALUES ('legacy-build', 'system.os.family == windows', 'REMOVED', 11, 12, 'dispatched');

            INSERT INTO matrix_builds(
                matrix_build_id, request_id, request_hash, request_payload,
                project, configuration, definition_snapshot, definition_hash,
                created_unix_ms, updated_unix_ms)
            VALUES (
                'legacy-matrix', 'legacy-request', 'request-hash', X'00',
                'legacy-project', 'legacy-configuration', X'01', 'definition-hash', 11, 12);

            INSERT INTO matrix_build_cells(
                matrix_build_id, cell_name, ordinal, build_id, agent_expression)
            VALUES (
                'legacy-matrix', 'windows', 0, 'legacy-build',
                'system.os.family == windows');
            """);

        await using (var database = new VivariumDatabase(rootDir))
        {
        }

        await using var reopened = new VivariumDatabase(rootDir);
        var state = await reopened.ReadAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    a.name, a.custom_parameters_json,
                    b.agent_id, b.state, b.owner_session_id,
                    b.agent_name_snapshot, b.agent_parameters_snapshot_json,
                    q.state, q.dispatched_session_id, q.queue_deadline_unix_ms,
                    c.rid, i.actor_type, i.actor_id, i.request_id,
                    m.current_version
                FROM agents a
                JOIN builds b ON b.agent_id = a.agent_id
                JOIN build_queue q ON q.build_id = b.build_id
                JOIN matrix_build_cells c ON c.build_id = b.build_id
                JOIN matrix_build_idempotency i ON i.matrix_build_id = c.matrix_build_id
                CROSS JOIN schema_metadata m
                WHERE a.agent_id = 'legacy-agent' AND b.build_id = 'legacy-build';
                """;
            using var reader = command.ExecuteReader();
            Assert.That(reader.Read(), Is.True);
            return new
            {
                Name = reader.GetString(0),
                CustomParameters = reader.GetString(1),
                BuildAgentId = reader.GetString(2),
                BuildState = reader.GetString(3),
                OwnerIsNull = reader.IsDBNull(4),
                AgentNameSnapshot = reader.GetString(5),
                AgentParametersSnapshot = reader.GetString(6),
                QueueState = reader.GetString(7),
                DispatchedSessionIsNull = reader.IsDBNull(8),
                QueueDeadlineIsNull = reader.IsDBNull(9),
                Rid = reader.GetString(10),
                IdempotencyActorType = reader.GetString(11),
                IdempotencyActorId = reader.GetString(12),
                IdempotencyRequestId = reader.GetString(13),
                Version = reader.GetInt32(14),
            };
        });

        Assert.Multiple(() =>
        {
            Assert.That(state.Name, Is.EqualTo("Legacy Agent"));
            Assert.That(state.CustomParameters, Is.EqualTo("{}"));
            Assert.That(state.BuildAgentId, Is.EqualTo("legacy-agent"));
            Assert.That(state.BuildState, Is.EqualTo("RUNNING"));
            Assert.That(state.OwnerIsNull, Is.True);
            Assert.That(state.AgentNameSnapshot, Is.Empty);
            Assert.That(state.AgentParametersSnapshot, Is.EqualTo("{}"));
            Assert.That(state.QueueState, Is.EqualTo("REMOVED"));
            Assert.That(state.DispatchedSessionIsNull, Is.True);
            Assert.That(state.QueueDeadlineIsNull, Is.True);
            Assert.That(state.Rid, Is.Empty);
            Assert.That(state.IdempotencyActorType, Is.EqualTo("legacy"));
            Assert.That(state.IdempotencyActorId, Is.EqualTo("unattributed"));
            Assert.That(state.IdempotencyRequestId, Is.EqualTo("legacy-request"));
            Assert.That(state.Version, Is.EqualTo(VivariumDatabase.CurrentSchemaVersion));
        });
    }

    [Test]
    public void Orphaned_legacy_rows_are_rejected_before_adoption()
    {
        ExecuteRaw("""
            PRAGMA foreign_keys = OFF;
            CREATE TABLE builds (build_id TEXT PRIMARY KEY);
            CREATE TABLE build_queue (
                queue_id INTEGER PRIMARY KEY,
                build_id TEXT NOT NULL REFERENCES builds(build_id)
            );
            INSERT INTO build_queue(queue_id, build_id) VALUES (1, 'missing-build');
            """);

        var exception = Assert.Throws<InvalidDataException>(() => _ = new VivariumDatabase(rootDir));

        Assert.Multiple(() =>
        {
            Assert.That(exception!.Message, Does.Contain("foreign key check failed"));
            Assert.That(ReadRawInt(
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'schema_migrations';"),
                Is.Zero);
        });
    }

    [TestCase("not-a-version")]
    [TestCase("999.0.0")]
    public async Task Invalid_or_unsupported_minimum_controller_version_is_rejected(string minimumVersion)
    {
        await using (var database = new VivariumDatabase(rootDir))
        {
        }
        ExecuteRaw($"""
            UPDATE schema_metadata
            SET minimum_controller_version = '{minimumVersion}'
            WHERE metadata_id = 1;
            """);

        var exception = Assert.Throws<InvalidDataException>(() => _ = new VivariumDatabase(rootDir));

        Assert.That(exception!.Message, Does.Contain(
            minimumVersion == "not-a-version" ? "is invalid" : "requires controller"));
    }

    [Test]
    public async Task Same_named_index_with_different_columns_is_rejected()
    {
        await using (var database = new VivariumDatabase(rootDir))
        {
        }
        ExecuteRaw("""
            DROP INDEX build_queue_pending_fifo;
            CREATE INDEX build_queue_pending_fifo ON build_queue(queue_id);
            """);

        var exception = Assert.Throws<InvalidDataException>(() => _ = new VivariumDatabase(rootDir));

        Assert.That(exception!.Message, Does.Contain("unsupported definition"));
    }

    [Test]
    public async Task Same_named_trigger_with_different_body_is_rejected()
    {
        await using (var database = new VivariumDatabase(rootDir))
        {
        }
        ExecuteRaw("""
            DROP TRIGGER audit_events_no_replace;
            CREATE TRIGGER audit_events_no_replace
            BEFORE INSERT ON audit_events
            BEGIN
                SELECT 1;
            END;
            """);

        var exception = Assert.Throws<InvalidDataException>(() => _ = new VivariumDatabase(rootDir));

        Assert.That(exception!.Message, Does.Contain("unsupported definition"));
    }

    [Test]
    public async Task Same_named_columns_with_changed_nullability_are_rejected()
    {
        await using (var database = new VivariumDatabase(rootDir))
        {
        }
        ExecuteRaw("""
            DROP TABLE schema_metadata;
            CREATE TABLE schema_metadata (
                metadata_id INTEGER PRIMARY KEY CHECK (metadata_id = 1),
                current_version INTEGER NULL,
                minimum_controller_version TEXT NOT NULL
            );
            INSERT INTO schema_metadata(metadata_id, current_version, minimum_controller_version)
            VALUES (1, 3, '0.0.0');
            """);

        var exception = Assert.Throws<InvalidDataException>(() => _ = new VivariumDatabase(rootDir));

        Assert.That(exception!.Message, Does.Contain("unsupported shape"));
    }

    [Test]
    public async Task Extra_check_constraint_is_rejected_as_semantic_drift()
    {
        await using (var database = new VivariumDatabase(rootDir))
        {
        }
        ExecuteRaw("""
            DROP TABLE schema_metadata;
            CREATE TABLE schema_metadata (
                metadata_id INTEGER PRIMARY KEY CHECK (metadata_id = 1),
                current_version INTEGER NOT NULL CHECK (current_version >= 0),
                minimum_controller_version TEXT NOT NULL
            );
            INSERT INTO schema_metadata(metadata_id, current_version, minimum_controller_version)
            VALUES (1, 3, '0.0.0');
            """);

        var exception = Assert.Throws<InvalidDataException>(() => _ = new VivariumDatabase(rootDir));

        Assert.That(exception!.Message, Does.Contain("unsupported constraint definition"));
    }

    [Test]
    public async Task Unexpected_collation_is_rejected_as_semantic_drift()
    {
        await using (var database = new VivariumDatabase(rootDir))
        {
        }
        ExecuteRaw("""
            DROP TABLE enroll_tokens;
            CREATE TABLE enroll_tokens (
                token_hash TEXT COLLATE NOCASE PRIMARY KEY,
                expires_unix_ms INTEGER NOT NULL,
                claimed_agent_id TEXT NULL
            );
            """);

        var exception = Assert.Throws<InvalidDataException>(() => _ = new VivariumDatabase(rootDir));

        Assert.That(exception!.Message, Does.Contain("unsupported constraint definition"));
    }

    [Test]
    public async Task Audit_event_id_cannot_be_replaced_when_recursive_triggers_are_disabled()
    {
        await using (var database = new VivariumDatabase(rootDir))
        {
        }
        ExecuteRaw("""
            INSERT INTO audit_events(
                audit_event_id, received_unix_ms, actor_type, actor_id, credential_kind,
                correlation_id, action, target_type, target_id, outcome)
            VALUES (
                'event-1', 1, 'user', 'actor', 'test',
                'correlation-1', 'test.write', 'test', 'original', 'SUCCEEDED');
            """);

        var exception = Assert.Throws<SqliteException>(() => ExecuteRaw("""
            PRAGMA recursive_triggers = OFF;
            INSERT OR REPLACE INTO audit_events(
                audit_event_id, received_unix_ms, actor_type, actor_id, credential_kind,
                correlation_id, action, target_type, target_id, outcome)
            VALUES (
                'event-1', 2, 'user', 'actor', 'test',
                'correlation-2', 'test.write', 'test', 'replacement', 'SUCCEEDED');
            """));

        Assert.Multiple(() =>
        {
            Assert.That(exception!.SqliteErrorCode, Is.EqualTo(19));
            Assert.That(ReadRawString(
                "SELECT target_id FROM audit_events WHERE audit_event_id = 'event-1';"),
                Is.EqualTo("original"));
        });
    }

    [Test]
    public async Task Version_two_upgrade_backfills_legacy_matrix_identity_without_rewriting_the_matrix()
    {
        await using (var database = new VivariumDatabase(rootDir))
        {
        }
        DowngradeWaveFourSchemaToVersionSix();
        ExecuteRaw("""
            DROP TABLE configuration_repository_attempt_failures;
            DROP TABLE configuration_mutation_conflicts;
            DROP TABLE configuration_mutation_targets;
            DROP TABLE agent_desired_configuration;
            DROP TABLE configuration_mutation_operations;
            DROP TABLE configuration_materialization_scopes;
            DROP TABLE configuration_revision_members;
            DROP TABLE configuration_revision_sets;
            DROP TABLE agent_capabilities;
            DROP TABLE agent_fact_observations;
            ALTER TABLE agents DROP COLUMN connection_generation;
            ALTER TABLE agents DROP COLUMN credential_generation;
            DROP TRIGGER audit_events_no_replace;
            DROP TABLE matrix_build_idempotency;
            DELETE FROM schema_migrations WHERE migration_number >= 3;
            UPDATE schema_metadata SET current_version = 2 WHERE metadata_id = 1;

            INSERT INTO agents(
                agent_id, name, auth_token_hash, first_seen_unix_ms, last_seen_unix_ms)
            VALUES ('legacy-agent', 'Legacy Agent', 'legacy-credential-hash', 1, 1);

            INSERT INTO matrix_builds(
                matrix_build_id, request_id, request_hash, request_payload,
                project, configuration, definition_snapshot, definition_hash,
                created_unix_ms, updated_unix_ms)
            VALUES (
                'legacy-matrix', 'legacy-request', 'request-hash', X'00',
                'project', 'configuration', X'00', 'definition-hash', 1, 1);
            """);

        await using var upgraded = new VivariumDatabase(rootDir);
        var state = await upgraded.ReadAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT actor_type, actor_id, request_id, matrix_build_id
                FROM matrix_build_idempotency
                WHERE matrix_build_id = 'legacy-matrix';
                """;
            using var reader = command.ExecuteReader();
            Assert.That(reader.Read(), Is.True);
            var mapping = Enumerable.Range(0, 4).Select(reader.GetString).ToArray();
            reader.Close();
            using var generations = connection.CreateCommand();
            generations.CommandText = """
                SELECT credential_generation, connection_generation
                FROM agents WHERE agent_id = 'legacy-agent';
                """;
            using var generationReader = generations.ExecuteReader();
            Assert.That(generationReader.Read(), Is.True);
            return (
                Mapping: mapping,
                CredentialGeneration: generationReader.GetInt64(0),
                ConnectionGeneration: generationReader.GetInt64(1));
        });

        Assert.Multiple(() =>
        {
            Assert.That(state.Mapping, Is.EqualTo(new[]
            {
                "legacy", "unattributed", "legacy-request", "legacy-matrix",
            }));
            Assert.That(state.CredentialGeneration, Is.EqualTo(1));
            Assert.That(state.ConnectionGeneration, Is.Zero);
        });
    }

    private void ExecuteRaw(string sql)
    {
        using var connection = OpenRaw();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private void DowngradeWaveFourSchemaToVersionSix()
    {
        DropAdministrationBootstrapSchema();
        ExecuteRaw("""
            DROP TABLE trx_test_occurrences;
            DROP TABLE trx_test_definitions;
            DROP TABLE trx_result_projections;
            DROP TABLE build_test_projection_states;

            DROP TABLE build_events;
            DROP TABLE build_event_streams;
            DROP TABLE blob_build_artifact_references;
            DROP TABLE blob_build_artifact_sets;
            DROP TABLE blob_artifact_upload_receipts;
            DROP TABLE blob_artifact_upload_staging;
            DROP TABLE blob_build_payload_references;
            DROP TABLE blob_build_payload_sets;
            DROP TABLE blob_principal_project_grants;
            DROP TABLE blob_upload_receipts;
            DROP TABLE blob_upload_plan_items;
            DROP TABLE blob_upload_plans;

            CREATE TABLE matrix_build_idempotency_v6 (
                actor_type TEXT NOT NULL,
                actor_id TEXT NOT NULL,
                request_id TEXT NOT NULL,
                matrix_build_id TEXT NOT NULL UNIQUE
                    REFERENCES matrix_builds(matrix_build_id) ON DELETE CASCADE,
                PRIMARY KEY (actor_type, actor_id, request_id)
            );
            INSERT INTO matrix_build_idempotency_v6(
                actor_type, actor_id, request_id, matrix_build_id)
            SELECT actor_type, actor_id, request_id, matrix_build_id
            FROM matrix_build_idempotency;
            DROP TABLE matrix_build_idempotency;
            ALTER TABLE matrix_build_idempotency_v6 RENAME TO matrix_build_idempotency;

            DELETE FROM schema_migrations WHERE migration_number >= 7;
            UPDATE schema_metadata SET current_version = 6 WHERE metadata_id = 1;
            """);
    }

    private void DropAdministrationBootstrapSchema()
    {
        DropAuthorizationPolicySchema();
        ExecuteRaw("""
            DROP TABLE administration_setup_requests;
            DROP TABLE administration_setup_sessions;
            DROP TABLE administration_token_generations;
            DROP TABLE administration_instances;
            DROP TABLE administration_setup_operations;
            """);
    }

    private void DropAuthorizationPolicySchema()
    {
        DropUserCredentialSchema();
        ExecuteRaw("""
            DROP TABLE authorization_role_bindings;
            DROP TABLE authorization_desired_users;
            """);
    }

    private void DropUserCredentialSchema()
    {
        DropAgentPackageUpgradeSchema();
        ExecuteRaw("DROP TABLE authorization_user_credentials;");
    }

    private void DropAgentPackageUpgradeSchema()
    {
        ExecuteRaw("""
            DROP TABLE agent_maintenance_drains;
            DROP TABLE agent_upgrade_events;
            DROP TABLE agent_upgrade_operations;
            DROP TABLE agent_package_publication_requests;
            DROP TABLE agent_packages;
            """);
    }

    private int ReadRawInt(string sql)
    {
        using var connection = OpenRaw();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private string ReadRawString(string sql)
    {
        using var connection = OpenRaw();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar())!;
    }

    private SqliteConnection OpenRaw()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(rootDir, "vivarium.db"),
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());
        connection.Open();
        return connection;
    }
}
