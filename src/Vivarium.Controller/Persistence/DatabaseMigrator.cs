using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Vivarium.Controller.Persistence;

internal static class DatabaseMigrator
{
    internal const int CurrentVersion = 12;
    private const string MinimumControllerVersion = "0.0.0";
    private const string PhaseOneFingerprint =
        "explicit Phase-1 legacy adoption and column backfills v1\n" +
        AgentSchemaSql + BuildSchemaSql + BuildIndexesSql + QueueSchemaSql + QueueIndexesSql +
        MatrixSchemaSql;

    private static readonly Migration[] Migrations =
    [
        new(
            1,
            "phase1-baseline",
            PhaseOneFingerprint,
            ApplyPhaseOneBaseline),
        new(
            2,
            "minimal-audit-journal",
            AuditSchemaSql,
            static (connection, transaction) => Execute(connection, transaction, AuditSchemaSql)),
        new(
            3,
            "principal-matrix-idempotency",
            PrincipalMatrixIdempotencySchemaSql,
            static (connection, transaction) =>
                Execute(connection, transaction, PrincipalMatrixIdempotencySchemaSql)),
        new(
            4,
            "typed-agent-fact-observations",
            AgentFactObservationSchemaSql,
            static (connection, transaction) =>
                Execute(connection, transaction, AgentFactObservationSchemaSql)),
        new(
            5,
            "git-configuration-reconciliation",
            ConfigurationReconciliationSchemaSql,
            static (connection, transaction) =>
                Execute(connection, transaction, ConfigurationReconciliationSchemaSql)),
        new(
            6,
            "configuration-mutation-evidence",
            ConfigurationMutationEvidenceSchemaSql,
            static (connection, transaction) =>
                Execute(connection, transaction, ConfigurationMutationEvidenceSchemaSql)),
        new(
            7,
            "blob-access-and-build-events",
            BlobAccessAndBuildEventsSchemaSql,
            static (connection, transaction) =>
                Execute(connection, transaction, BlobAccessAndBuildEventsSchemaSql)),
        new(
            8,
            "durable-trx-projections",
            TrxProjectionSchemaSql,
            static (connection, transaction) =>
                Execute(connection, transaction, TrxProjectionSchemaSql)),
        new(
            9,
            "resumable-first-run-claim",
            AdministrationBootstrapSchemaSql,
            static (connection, transaction) =>
                Execute(connection, transaction, AdministrationBootstrapSchemaSql)),
        new(
            10,
            "git-backed-authorization-policy",
            AuthorizationPolicySchemaSql,
            static (connection, transaction) =>
                Execute(connection, transaction, AuthorizationPolicySchemaSql)),
        new(
            11,
            "private-user-credentials",
            UserCredentialSchemaSql,
            static (connection, transaction) =>
                Execute(connection, transaction, UserCredentialSchemaSql)),
        new(
            12,
            "agent-package-upgrade-operations",
            AgentPackageUpgradeSchemaSql,
            static (connection, transaction) =>
                Execute(connection, transaction, AgentPackageUpgradeSchemaSql)),
    ];

    private static readonly HashSet<string> KnownUnversionedTables =
    [
        "agents",
        "enroll_tokens",
        "builds",
        "build_queue",
        "matrix_builds",
        "matrix_build_cells",
    ];

    private static readonly HashSet<string> VersionFourTables =
    [
        "agents",
        "enroll_tokens",
        "builds",
        "build_queue",
        "matrix_builds",
        "matrix_build_cells",
        "schema_migrations",
        "schema_metadata",
        "audit_events",
        "matrix_build_idempotency",
        "agent_fact_observations",
        "agent_capabilities",
    ];

    private static readonly HashSet<string> VersionFiveTables =
        new(VersionFourTables.Concat([
            "configuration_revision_sets",
            "configuration_revision_members",
            "configuration_materialization_scopes",
            "configuration_mutation_operations",
            "agent_desired_configuration",
        ]), StringComparer.Ordinal);

    private static readonly HashSet<string> VersionSixTables =
        new(VersionFiveTables.Concat([
            "configuration_mutation_targets",
            "configuration_mutation_conflicts",
            "configuration_repository_attempt_failures",
        ]), StringComparer.Ordinal);

    private static readonly HashSet<string> VersionSevenTables =
        new(VersionSixTables.Concat([
            "blob_upload_plans",
            "blob_upload_plan_items",
            "blob_upload_receipts",
            "blob_principal_project_grants",
            "blob_build_payload_sets",
            "blob_build_payload_references",
            "blob_artifact_upload_staging",
            "blob_artifact_upload_receipts",
            "blob_build_artifact_sets",
            "blob_build_artifact_references",
            "build_event_streams",
            "build_events",
        ]), StringComparer.Ordinal);

    private static readonly HashSet<string> VersionEightTables =
        new(VersionSevenTables.Concat([
            "build_test_projection_states",
            "trx_result_projections",
            "trx_test_definitions",
            "trx_test_occurrences",
        ]), StringComparer.Ordinal);

    private static readonly HashSet<string> VersionNineTables =
        new(VersionEightTables.Concat([
            "administration_instances",
            "administration_token_generations",
            "administration_setup_operations",
            "administration_setup_sessions",
            "administration_setup_requests",
        ]), StringComparer.Ordinal);

    private static readonly HashSet<string> VersionTenTables =
        new(VersionNineTables.Concat([
            "authorization_desired_users",
            "authorization_role_bindings",
        ]), StringComparer.Ordinal);

    private static readonly HashSet<string> VersionElevenTables =
        new(VersionTenTables.Concat([
            "authorization_user_credentials",
        ]), StringComparer.Ordinal);

    private static readonly HashSet<string> KnownVersionedTables =
        new(VersionElevenTables.Concat([
            "agent_packages",
            "agent_package_publication_requests",
            "agent_upgrade_operations",
            "agent_upgrade_events",
            "agent_maintenance_drains",
        ]), StringComparer.Ordinal);

    private static readonly HashSet<string> VersionTwoTables =
        new(KnownUnversionedTables.Concat(["schema_migrations", "schema_metadata", "audit_events"]),
            StringComparer.Ordinal);

    private static readonly HashSet<string> VersionThreeTables =
        new(VersionTwoTables.Concat(["matrix_build_idempotency"]), StringComparer.Ordinal);

    public static void Migrate(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        ValidateMigrationManifest();
        Execute(connection, transaction: null, "PRAGMA foreign_keys = ON;");
        VerifyDatabaseIntegrity(connection, "before schema adoption or migration");

        var existingTables = ReadUserTables(connection);
        ValidateLedgerState(existingTables);
        var hasLedger = existingTables.Contains("schema_migrations");
        Dictionary<int, AppliedMigration>? applied = null;

        if (hasLedger)
        {
            VerifyLedgerSchema(connection);
            applied = ReadAppliedMigrations(connection);
            ValidateAppliedMigrations(applied);
            var appliedVersion = applied.Count == 0 ? 0 : applied.Keys.Max();
            ValidateMetadata(connection, appliedVersion);
            VerifySchemaAtVersion(connection, appliedVersion);
        }
        else
        {
            VerifyLegacySchema(connection, existingTables);
        }

        Execute(connection, transaction: null, "PRAGMA journal_mode = WAL;");

        if (!hasLedger)
        {
            using var transaction = connection.BeginTransaction(deferred: false);
            Execute(connection, transaction, MigrationLedgerSql);
            transaction.Commit();
        }

        applied ??= ReadAppliedMigrations(connection);

        foreach (var migration in Migrations.Where(migration => !applied.ContainsKey(migration.Version)))
        {
            using var transaction = connection.BeginTransaction(deferred: false);
            migration.Apply(connection, transaction);
            InsertAppliedMigration(connection, transaction, migration);
            UpdateMetadata(connection, transaction, migration.Version);
            transaction.Commit();
        }

        var finalApplied = ReadAppliedMigrations(connection);
        ValidateAppliedMigrations(finalApplied);
        if (finalApplied.Count != Migrations.Length || finalApplied.Keys.Max() != CurrentVersion)
        {
            throw new InvalidDataException(
                $"database schema migration history is incomplete; expected version {CurrentVersion}");
        }

        ValidateMetadata(connection, CurrentVersion);
        VerifyDatabaseIntegrity(connection, "after schema migration");
        VerifyCurrentSchema(connection);
    }

    private static void ValidateMigrationManifest()
    {
        if (Migrations.Length != CurrentVersion)
        {
            throw new InvalidDataException(
                $"database migration manifest ends at {Migrations.Length}, but the controller declares {CurrentVersion}");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        var checksums = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < Migrations.Length; index++)
        {
            var migration = Migrations[index];
            var expectedVersion = index + 1;
            if (migration.Version != expectedVersion)
            {
                throw new InvalidDataException(
                    $"database migration manifest is not contiguous at version {expectedVersion}");
            }

            if (string.IsNullOrWhiteSpace(migration.Name) ||
                string.IsNullOrWhiteSpace(migration.Fingerprint) ||
                !names.Add(migration.Name))
            {
                throw new InvalidDataException(
                    $"database migration manifest has an invalid or duplicate entry at version {expectedVersion}");
            }

            if (migration.Checksum.Length != 64 ||
                migration.Checksum.Any(character => !char.IsAsciiHexDigit(character) || char.IsUpper(character)) ||
                !checksums.Add(migration.Checksum))
            {
                throw new InvalidDataException(
                    $"database migration manifest has an invalid checksum at version {expectedVersion}");
            }
        }
    }

    private static void ValidateLedgerState(IReadOnlySet<string> tables)
    {
        var hasLedger = tables.Contains("schema_migrations");
        var hasMetadata = tables.Contains("schema_metadata");
        if (hasLedger != hasMetadata)
        {
            throw new InvalidDataException(
                "database schema metadata is incomplete; restore from backup or repair explicitly");
        }

        if (hasLedger)
        {
            var unknownVersioned = tables
                .Where(table => !KnownVersionedTables.Contains(table))
                .Order()
                .ToArray();
            if (unknownVersioned.Length > 0)
            {
                throw new InvalidDataException(
                    $"versioned database contains unknown tables: {string.Join(", ", unknownVersioned)}");
            }

            return;
        }

        var unknown = tables.Where(table => !KnownUnversionedTables.Contains(table)).Order().ToArray();
        if (unknown.Length > 0)
        {
            throw new InvalidDataException(
                $"unversioned database contains unknown tables: {string.Join(", ", unknown)}");
        }
    }

    private static Dictionary<int, AppliedMigration> ReadAppliedMigrations(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT migration_number, migration_name, checksum
            FROM schema_migrations
            ORDER BY migration_number;
            """;
        using var reader = command.ExecuteReader();
        var result = new Dictionary<int, AppliedMigration>();
        while (reader.Read())
        {
            var migration = new AppliedMigration(reader.GetInt32(0), reader.GetString(1), reader.GetString(2));
            if (!result.TryAdd(migration.Version, migration))
            {
                throw new InvalidDataException($"duplicate schema migration {migration.Version}");
            }
        }

        return result;
    }

    private static void ValidateAppliedMigrations(IReadOnlyDictionary<int, AppliedMigration> applied)
    {
        if (applied.Keys.Any(version => version > CurrentVersion))
        {
            throw new InvalidDataException(
                $"database schema version {applied.Keys.Max()} is newer than this controller supports ({CurrentVersion})");
        }

        for (var index = 0; index < applied.Count; index++)
        {
            var expectedVersion = index + 1;
            if (!applied.TryGetValue(expectedVersion, out var actual))
            {
                throw new InvalidDataException($"database schema migration history has a gap at {expectedVersion}");
            }

            var expected = Migrations.Single(migration => migration.Version == expectedVersion);
            if (!string.Equals(actual.Name, expected.Name, StringComparison.Ordinal) ||
                !string.Equals(actual.Checksum, expected.Checksum, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"database schema migration {expectedVersion} does not match this controller's immutable manifest");
            }
        }
    }

    private static void InsertAppliedMigration(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Migration migration)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO schema_migrations(
                migration_number, migration_name, checksum, controller_version, applied_unix_ms)
            VALUES ($number, $name, $checksum, $controllerVersion, $appliedAt);
            """;
        command.Parameters.AddWithValue("$number", migration.Version);
        command.Parameters.AddWithValue("$name", migration.Name);
        command.Parameters.AddWithValue("$checksum", migration.Checksum);
        command.Parameters.AddWithValue("$controllerVersion", ControllerVersion());
        command.Parameters.AddWithValue("$appliedAt", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        command.ExecuteNonQuery();
    }

    private static void UpdateMetadata(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int version)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE schema_metadata SET
                current_version = $version,
                minimum_controller_version = $minimumControllerVersion
            WHERE metadata_id = 1;
            """;
        command.Parameters.AddWithValue("$version", version);
        command.Parameters.AddWithValue("$minimumControllerVersion", MinimumControllerVersion);
        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvalidDataException("database schema metadata row is missing");
        }
    }

    private static void ApplyPhaseOneBaseline(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        Execute(connection, transaction, AgentSchemaSql);
        AddLegacyColumnIfMissing(
            connection,
            transaction,
            "agents",
            "custom_parameters_json",
            "TEXT NOT NULL DEFAULT '{}'");

        EnsureBuildSchema(connection, transaction);
        AddLegacyColumnIfMissing(connection, transaction, "builds", "owner_session_id", "TEXT NULL");
        AddLegacyColumnIfMissing(
            connection,
            transaction,
            "builds",
            "reconnect_deadline_unix_ms",
            "INTEGER NULL");
        AddLegacyColumnIfMissing(
            connection,
            transaction,
            "builds",
            "agent_name_snapshot",
            "TEXT NOT NULL DEFAULT ''");
        AddLegacyColumnIfMissing(
            connection,
            transaction,
            "builds",
            "agent_parameters_snapshot_json",
            "TEXT NOT NULL DEFAULT '{}'");
        AddLegacyColumnIfMissing(
            connection,
            transaction,
            "builds",
            "agent_custom_parameters_snapshot_json",
            "TEXT NOT NULL DEFAULT '{}'");

        Execute(connection, transaction, BuildIndexesSql);
        Execute(connection, transaction, QueueSchemaSql);
        AddLegacyColumnIfMissing(
            connection,
            transaction,
            "build_queue",
            "dispatched_session_id",
            "TEXT NULL");
        AddLegacyColumnIfMissing(
            connection,
            transaction,
            "build_queue",
            "queue_deadline_unix_ms",
            "INTEGER NULL");
        Execute(connection, transaction, QueueIndexesSql);

        Execute(connection, transaction, MatrixSchemaSql);
        AddLegacyColumnIfMissing(
            connection,
            transaction,
            "matrix_build_cells",
            "rid",
            "TEXT NOT NULL DEFAULT ''");

        RestoreCapturedTable(connection, transaction, "build_queue");
        RestoreCapturedTable(connection, transaction, "matrix_build_cells");

        VerifyPhaseOneSchema(connection, transaction, includesAgentGenerations: false);
    }

    private static void EnsureBuildSchema(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        using var inspect = connection.CreateCommand();
        inspect.Transaction = transaction;
        inspect.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'builds';";
        var existingSql = inspect.ExecuteScalar() as string;
        if (existingSql == null)
        {
            Execute(connection, transaction, BuildSchemaSql);
            return;
        }

        if (existingSql.Contains("'QUEUED'", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        CaptureDependentTable(connection, transaction, "matrix_build_cells");
        CaptureDependentTable(connection, transaction, "build_queue");
        var expectedRows = CountRows(connection, transaction, "builds");
        Execute(connection, transaction, $"""
            DROP INDEX IF EXISTS builds_one_active_per_agent;
            ALTER TABLE builds RENAME TO builds_before_queue;
            {BuildSchemaSql}
            INSERT INTO builds(
                build_id, agent_id, state, assignment, result, cancellation_reason,
                created_unix_ms, updated_unix_ms)
            SELECT
                build_id, agent_id, state, assignment, result, cancellation_reason,
                created_unix_ms, updated_unix_ms
            FROM builds_before_queue;
            DROP TABLE builds_before_queue;
            """);
        if (CountRows(connection, transaction, "builds") != expectedRows)
        {
            throw new InvalidDataException("legacy builds migration did not preserve every row");
        }
    }

    private static void CaptureDependentTable(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table)
    {
        if (!TableExists(connection, transaction, table))
        {
            return;
        }

        var temporaryTable = CapturedTableName(table);
        Execute(connection, transaction, $"""
            CREATE TEMP TABLE {temporaryTable} AS SELECT * FROM {table};
            DROP TABLE {table};
            """);
    }

    private static void RestoreCapturedTable(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table)
    {
        var temporaryTable = CapturedTableName(table);
        if (!TemporaryTableExists(connection, transaction, temporaryTable))
        {
            return;
        }

        var capturedColumns = ReadColumns(connection, transaction, temporaryTable)
            .Select(column => column.Name)
            .ToHashSet(StringComparer.Ordinal);
        var destinationColumns = ReadColumns(connection, transaction, table)
            .Select(column => column.Name)
            .Where(capturedColumns.Contains)
            .ToArray();
        var columnList = string.Join(", ", destinationColumns);
        var expectedRows = CountRows(connection, transaction, temporaryTable);
        Execute(connection, transaction, $"""
            INSERT INTO {table}({columnList}) SELECT {columnList} FROM {temporaryTable};
            DROP TABLE {temporaryTable};
            """);
        if (CountRows(connection, transaction, table) != expectedRows)
        {
            throw new InvalidDataException($"legacy {table} migration did not preserve every row");
        }
    }

    private static string CapturedTableName(string table) => $"vivarium_migration_capture_{table}";

    private static void AddLegacyColumnIfMissing(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string column,
        string definition)
    {
        if (ReadColumns(connection, transaction, table).Any(actual => actual.Name == column))
        {
            return;
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
        command.ExecuteNonQuery();
    }

    private static void VerifyCurrentSchema(SqliteConnection connection)
    {
        VerifyExactTableSet(connection, KnownVersionedTables);
        VerifyPhaseOneSchema(connection, transaction: null, includesAgentGenerations: true);
        VerifyLedgerSchema(connection);
        VerifyTable(
            connection,
            transaction: null,
            "audit_events",
            AuditColumns);
        VerifyTableSqlContains(connection, null, "audit_events", AuditOutcomeConstraintSql);
        VerifyTableConstraintSurface(connection, null, "audit_events", AuditSchemaSql);
        VerifyPrincipalMatrixIdempotencySchemaV7(connection, transaction: null);
        VerifyAgentFactObservationSchema(connection, transaction: null);
        VerifyConfigurationReconciliationSchema(connection, transaction: null);
        VerifyConfigurationMutationEvidenceSchema(connection, transaction: null);
        VerifyBlobAccessAndBuildEventSchema(connection, transaction: null);
        VerifyTrxProjectionSchema(connection, transaction: null);
        VerifyAdministrationBootstrapSchema(connection, transaction: null);
        VerifyAuthorizationPolicySchema(connection, transaction: null);
        VerifyUserCredentialSchema(connection, transaction: null);
        VerifyAgentPackageUpgradeSchema(connection, transaction: null);

        VerifyExactNamedObjects(connection, CurrentNamedObjects);
    }

    private static void VerifySchemaAtVersion(SqliteConnection connection, int version)
    {
        var expectedTables = version switch
        {
            0 => new HashSet<string>(["schema_migrations", "schema_metadata"], StringComparer.Ordinal),
            1 => new HashSet<string>(KnownUnversionedTables.Concat(["schema_migrations", "schema_metadata"]),
                StringComparer.Ordinal),
            2 => VersionTwoTables,
            3 => VersionThreeTables,
            4 => VersionFourTables,
            5 => VersionFiveTables,
            6 => VersionSixTables,
            7 => VersionSevenTables,
            8 => VersionEightTables,
            9 => VersionNineTables,
            10 => VersionTenTables,
            11 => VersionElevenTables,
            12 => KnownVersionedTables,
            _ => throw new InvalidDataException($"unsupported applied schema version {version}"),
        };
        VerifyExactTableSet(connection, expectedTables);
        VerifyLedgerSchema(connection);
        if (version >= 1)
        {
            VerifyPhaseOneSchema(
                connection,
                transaction: null,
                includesAgentGenerations: version >= 4);
        }

        if (version >= 2)
        {
            VerifyTable(
                connection,
                transaction: null,
                "audit_events",
                AuditColumns);
            VerifyTableSqlContains(connection, null, "audit_events", AuditOutcomeConstraintSql);
            VerifyTableConstraintSurface(connection, null, "audit_events", AuditSchemaSql);
        }

        if (version is >= 3 and < 7)
        {
            VerifyPrincipalMatrixIdempotencySchema(connection, transaction: null);
        }

        if (version >= 7)
        {
            VerifyPrincipalMatrixIdempotencySchemaV7(connection, transaction: null);
        }

        if (version >= 4)
        {
            VerifyAgentFactObservationSchema(connection, transaction: null);
        }

        if (version >= 5)
        {
            VerifyConfigurationReconciliationSchema(connection, transaction: null);
        }

        if (version >= 6)
        {
            VerifyConfigurationMutationEvidenceSchema(connection, transaction: null);
        }

        if (version >= 7)
        {
            VerifyBlobAccessAndBuildEventSchema(connection, transaction: null);
        }

        if (version >= 8)
        {
            VerifyTrxProjectionSchema(connection, transaction: null);
        }

        if (version >= 9)
        {
            VerifyAdministrationBootstrapSchema(connection, transaction: null);
        }

        if (version >= 10)
        {
            VerifyAuthorizationPolicySchema(connection, transaction: null);
        }

        if (version >= 11)
        {
            VerifyUserCredentialSchema(connection, transaction: null);
        }

        if (version >= 12)
        {
            VerifyAgentPackageUpgradeSchema(connection, transaction: null);
        }

        var expectedObjects = version switch
        {
            0 => EmptyNamedObjects,
            1 => PhaseOneNamedObjects,
            2 => AuditNamedObjects,
            3 => VersionThreeNamedObjects,
            4 => VersionFourNamedObjects,
            5 => VersionFiveNamedObjects,
            6 => VersionSixNamedObjects,
            7 => VersionSevenNamedObjects,
            8 => VersionEightNamedObjects,
            9 => VersionNineNamedObjects,
            10 => VersionTenNamedObjects,
            11 => VersionElevenNamedObjects,
            12 => CurrentNamedObjects,
            _ => throw new InvalidDataException($"unsupported applied schema version {version}"),
        };
        VerifyExactNamedObjects(connection, expectedObjects);
    }

    private static void VerifyLedgerSchema(SqliteConnection connection)
    {
        VerifyTable(
            connection,
            transaction: null,
            "schema_migrations",
            MigrationColumns,
            uniqueConstraints: [["migration_name"]]);
        VerifyTable(
            connection,
            transaction: null,
            "schema_metadata",
            MetadataColumns);
        VerifyTableSqlContains(connection, null, "schema_metadata", MetadataIdConstraintSql);
        VerifyTableConstraintSurface(connection, null, "schema_migrations", MigrationLedgerSql);
        VerifyTableConstraintSurface(connection, null, "schema_metadata", MigrationLedgerSql);
    }

    private static void ValidateMetadata(SqliteConnection connection, int expectedVersion)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT current_version, minimum_controller_version
            FROM schema_metadata WHERE metadata_id = 1;
            """;
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.GetInt32(0) != expectedVersion)
        {
            throw new InvalidDataException(
                "database schema metadata does not match its applied migration history");
        }

        var minimum = ParseVersion(reader.GetString(1), "database minimum controller version");
        if (reader.Read())
        {
            throw new InvalidDataException("database schema metadata contains more than one row");
        }

        var controller = typeof(DatabaseMigrator).Assembly.GetName().Version
            ?? throw new InvalidDataException("controller assembly has no compatibility version");
        if (controller < minimum)
        {
            throw new InvalidDataException(
                $"database requires controller {minimum} or newer; this controller is {controller}");
        }
    }

    private static Version ParseVersion(string value, string field)
    {
        var separator = value.IndexOfAny(['-', '+']);
        var numeric = separator < 0 ? value : value[..separator];
        if (!Version.TryParse(numeric, out var version))
        {
            throw new InvalidDataException($"{field} '{value}' is invalid");
        }

        return version;
    }

    private static void VerifyPhaseOneSchema(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        bool includesAgentGenerations)
    {
        VerifyTable(
            connection,
            transaction,
            "agents",
            includesAgentGenerations ? AgentColumns : PhaseOneAgentColumns,
            uniqueConstraints: [["name"]]);
        VerifyTable(
            connection,
            transaction,
            "enroll_tokens",
            EnrollTokenColumns);
        VerifyTable(
            connection,
            transaction,
            "builds",
            BuildColumns);
        VerifyTable(
            connection,
            transaction,
            "build_queue",
            QueueColumns,
            QueueForeignKeys,
            [["build_id"]]);
        VerifyTable(
            connection,
            transaction,
            "matrix_builds",
            MatrixBuildColumns,
            uniqueConstraints: [["request_id"]]);
        VerifyTable(
            connection,
            transaction,
            "matrix_build_cells",
            MatrixCellColumns,
            MatrixCellForeignKeys,
            [["build_id"], ["matrix_build_id", "ordinal"]]);
        VerifyTableSqlContains(connection, transaction, "builds", BuildStateConstraintSql);
        VerifyTableSqlContains(connection, transaction, "build_queue", QueueStateConstraintSql);
        VerifyTableSqlContains(connection, transaction, "build_queue", QueueShapeConstraintSql);
        VerifyTableConstraintSurface(
            connection,
            transaction,
            "agents",
            includesAgentGenerations ? AgentSchemaV4Sql : AgentSchemaSql);
        VerifyTableConstraintSurface(connection, transaction, "enroll_tokens", AgentSchemaSql);
        VerifyTableConstraintSurface(connection, transaction, "builds", BuildSchemaSql);
        VerifyTableConstraintSurface(connection, transaction, "build_queue", QueueSchemaSql);
        VerifyTableConstraintSurface(connection, transaction, "matrix_builds", MatrixSchemaSql);
        VerifyTableConstraintSurface(connection, transaction, "matrix_build_cells", MatrixSchemaSql);
    }

    private static void VerifyPrincipalMatrixIdempotencySchema(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        VerifyTable(
            connection,
            transaction,
            "matrix_build_idempotency",
            MatrixBuildIdempotencyColumns,
            MatrixBuildIdempotencyForeignKeys,
            [["matrix_build_id"]]);
        VerifyTableConstraintSurface(
            connection,
            transaction,
            "matrix_build_idempotency",
            PrincipalMatrixIdempotencySchemaSql);
    }

    private static void VerifyPrincipalMatrixIdempotencySchemaV7(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        VerifyTable(
            connection,
            transaction,
            "matrix_build_idempotency",
            MatrixBuildIdempotencyV7Columns,
            MatrixBuildIdempotencyForeignKeys,
            [["matrix_build_id"]]);
        VerifyTableConstraintSurface(
            connection,
            transaction,
            "matrix_build_idempotency",
            MatrixBuildIdempotencyV7DefinitionSql);
        VerifyRenamedTableDefinitionBody(
            connection,
            transaction,
            "matrix_build_idempotency",
            MatrixBuildIdempotencyV7DefinitionSql);
    }

    private static void VerifyAgentFactObservationSchema(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        VerifyTable(
            connection,
            transaction,
            "agent_fact_observations",
            AgentFactObservationColumns,
            AgentFactObservationForeignKeys);
        VerifyTableConstraintSurface(
            connection,
            transaction,
            "agent_fact_observations",
            AgentFactObservationSchemaSql);
        VerifyTable(
            connection,
            transaction,
            "agent_capabilities",
            AgentCapabilityColumns,
            AgentCapabilityForeignKeys);
        VerifyTableConstraintSurface(
            connection,
            transaction,
            "agent_capabilities",
            AgentFactObservationSchemaSql);
    }

    private static void VerifyConfigurationReconciliationSchema(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        VerifyTable(
            connection,
            transaction,
            "configuration_revision_sets",
            ConfigurationRevisionSetColumns,
            ConfigurationRevisionSetForeignKeys);
        VerifyTableConstraintSurface(
            connection,
            transaction,
            "configuration_revision_sets",
            ConfigurationReconciliationSchemaSql);
        VerifyTable(
            connection,
            transaction,
            "configuration_revision_members",
            ConfigurationRevisionMemberColumns,
            ConfigurationRevisionMemberForeignKeys);
        VerifyTableConstraintSurface(
            connection,
            transaction,
            "configuration_revision_members",
            ConfigurationReconciliationSchemaSql);
        VerifyTable(
            connection,
            transaction,
            "configuration_materialization_scopes",
            ConfigurationMaterializationScopeColumns,
            ConfigurationMaterializationScopeForeignKeys);
        VerifyTableConstraintSurface(
            connection,
            transaction,
            "configuration_materialization_scopes",
            ConfigurationReconciliationSchemaSql);
        VerifyTable(
            connection,
            transaction,
            "configuration_mutation_operations",
            ConfigurationMutationOperationColumns,
            ConfigurationMutationOperationForeignKeys,
            [["actor_type", "actor_id", "operation_kind", "request_id"]]);
        VerifyTableConstraintSurface(
            connection,
            transaction,
            "configuration_mutation_operations",
            ConfigurationReconciliationSchemaSql);
        VerifyTable(
            connection,
            transaction,
            "agent_desired_configuration",
            AgentDesiredConfigurationColumns,
            AgentDesiredConfigurationForeignKeys);
        VerifyTableConstraintSurface(
            connection,
            transaction,
            "agent_desired_configuration",
            ConfigurationReconciliationSchemaSql);
        VerifyConfigurationReconciliationRows(connection, transaction);
    }

    private static void VerifyConfigurationMutationEvidenceSchema(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        VerifyTable(
            connection,
            transaction,
            "configuration_mutation_targets",
            ConfigurationMutationTargetColumns,
            ConfigurationMutationTargetForeignKeys);
        VerifyTableConstraintSurface(
            connection,
            transaction,
            "configuration_mutation_targets",
            ConfigurationMutationEvidenceSchemaSql);
        VerifyTable(
            connection,
            transaction,
            "configuration_mutation_conflicts",
            ConfigurationMutationConflictColumns,
            ConfigurationMutationConflictForeignKeys);
        VerifyTableConstraintSurface(
            connection,
            transaction,
            "configuration_mutation_conflicts",
            ConfigurationMutationEvidenceSchemaSql);
        VerifyTable(
            connection,
            transaction,
            "configuration_repository_attempt_failures",
            ConfigurationRepositoryAttemptFailureColumns,
            ConfigurationRepositoryAttemptFailureForeignKeys);
        VerifyTableConstraintSurface(
            connection,
            transaction,
            "configuration_repository_attempt_failures",
            ConfigurationMutationEvidenceSchemaSql);

        using var conflicts = connection.CreateCommand();
        conflicts.Transaction = transaction;
        conflicts.CommandText = """
            SELECT conflicts.operation_id
            FROM configuration_mutation_conflicts conflicts
            JOIN configuration_mutation_operations operations
                ON operations.operation_id = conflicts.operation_id
            WHERE operations.state <> 'CONFLICT'
                OR operations.repository_id <> conflicts.current_repository_id
            LIMIT 1;
            """;
        if (conflicts.ExecuteScalar() is not null)
        {
            throw new InvalidDataException(
                "configuration mutation conflict evidence does not match its terminal operation");
        }
    }

    private static void VerifyBlobAccessAndBuildEventSchema(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        string[] tables =
        [
            "blob_upload_plans",
            "blob_upload_plan_items",
            "blob_upload_receipts",
            "blob_principal_project_grants",
            "blob_build_payload_sets",
            "blob_build_payload_references",
            "blob_artifact_upload_staging",
            "blob_artifact_upload_receipts",
            "blob_build_artifact_sets",
            "blob_build_artifact_references",
            "build_event_streams",
            "build_events",
        ];

        foreach (var table in tables)
        {
            VerifyExactTableDefinition(
                connection,
                transaction,
                table,
                BlobAccessAndBuildEventsSchemaSql);
        }
    }

    private static void VerifyTrxProjectionSchema(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        string[] tables =
        [
            "build_test_projection_states",
            "trx_result_projections",
            "trx_test_definitions",
            "trx_test_occurrences",
        ];

        foreach (var table in tables)
        {
            VerifyExactTableDefinition(connection, transaction, table, TrxProjectionSchemaSql);
        }
    }

    private static void VerifyAdministrationBootstrapSchema(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        string[] tables =
        [
            "administration_instances",
            "administration_token_generations",
            "administration_setup_operations",
            "administration_setup_sessions",
            "administration_setup_requests",
        ];

        foreach (var table in tables)
        {
            VerifyExactTableDefinition(
                connection,
                transaction,
                table,
                AdministrationBootstrapSchemaSql);
        }
    }

    private static void VerifyAuthorizationPolicySchema(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        string[] tables =
        [
            "authorization_desired_users",
            "authorization_role_bindings",
        ];

        foreach (var table in tables)
        {
            VerifyExactTableDefinition(
                connection,
                transaction,
                table,
                AuthorizationPolicySchemaSql);
        }
    }

    private static void VerifyUserCredentialSchema(
        SqliteConnection connection,
        SqliteTransaction? transaction) =>
        VerifyExactTableDefinition(
            connection,
            transaction,
            "authorization_user_credentials",
            UserCredentialSchemaSql);

    private static void VerifyAgentPackageUpgradeSchema(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        string[] tables =
        [
            "agent_packages",
            "agent_package_publication_requests",
            "agent_upgrade_operations",
            "agent_upgrade_events",
            "agent_maintenance_drains",
        ];
        foreach (var table in tables)
        {
            VerifyExactTableDefinition(connection, transaction, table, AgentPackageUpgradeSchemaSql);
        }
        VerifyAgentPackageUpgradeRows(connection, transaction);
    }

    private static void VerifyAgentPackageUpgradeRows(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT operations.operation_id
            FROM agent_upgrade_operations AS operations
            LEFT JOIN agent_maintenance_drains AS drains
                ON drains.operation_id = operations.operation_id
            WHERE (
                operations.state IN (
                    'DRAINING', 'HANDOFF_READY', 'AWAITING_HEALTH', 'COMMIT_PENDING',
                    'FINALIZING', 'ROLLBACK_REQUESTED')
                AND (drains.operation_id IS NULL
                    OR drains.agent_id <> operations.agent_id
                    OR drains.fence <> operations.maintenance_fence)
            ) OR (
                drains.operation_id IS NOT NULL
                AND (drains.agent_id <> operations.agent_id
                    OR drains.fence <> operations.maintenance_fence)
            ) OR (
                drains.operation_id IS NOT NULL
                AND operations.state NOT IN (
                    'DRAINING', 'HANDOFF_READY', 'AWAITING_HEALTH', 'COMMIT_PENDING',
                    'FINALIZING', 'ROLLBACK_REQUESTED', 'FAILED')
            )
            LIMIT 1;
            """;
        if (command.ExecuteScalar() is not null)
        {
            throw new InvalidDataException(
                "agent upgrade operation/drain rows violate the durable coordination invariant");
        }
    }

    private static void VerifyConfigurationReconciliationRows(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        using (var pointers = connection.CreateCommand())
        {
            pointers.Transaction = transaction;
            pointers.CommandText = """
                SELECT scopes.materialization_scope
                FROM configuration_materialization_scopes scopes
                LEFT JOIN configuration_revision_sets latest
                    ON latest.revision_set_id = scopes.latest_attempt_revision_set_id
                LEFT JOIN configuration_revision_sets active
                    ON active.revision_set_id = scopes.active_revision_set_id
                WHERE latest.materialization_scope <> scopes.materialization_scope
                    OR (scopes.active_revision_set_id IS NOT NULL AND (
                        active.materialization_scope <> scopes.materialization_scope
                        OR active.state <> 'ACTIVE'))
                LIMIT 1;
                """;
            if (pointers.ExecuteScalar() is not null)
            {
                throw new InvalidDataException(
                    "configuration materialization scope has a cross-scope or non-active pointer");
            }
        }

        using var members = connection.CreateCommand();
        members.Transaction = transaction;
        members.CommandText = """
            SELECT sets.revision_set_id
            FROM configuration_revision_sets sets
            LEFT JOIN configuration_revision_members members
                ON members.revision_set_id = sets.revision_set_id
                    AND members.repository_role = 'CONTROL'
            LEFT JOIN configuration_revision_sets base
                ON base.revision_set_id = sets.base_revision_set_id
            GROUP BY sets.revision_set_id
            HAVING COUNT(members.repository_id) <> 1
                OR (sets.base_revision_set_id IS NOT NULL
                    AND base.materialization_scope <> sets.materialization_scope)
            LIMIT 1;
            """;
        if (members.ExecuteScalar() is not null)
        {
            throw new InvalidDataException(
                "configuration revision set does not contain exactly one control member");
        }
    }

    private static void VerifyTable(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string table,
        IReadOnlyCollection<ColumnSchema> expectedColumns,
        IReadOnlyCollection<ForeignKeySchema>? expectedForeignKeys = null,
        IReadOnlyCollection<string[]>? uniqueConstraints = null)
    {
        var actual = ReadColumns(connection, transaction, table);
        if (actual.Count != expectedColumns.Count || !actual.ToHashSet().SetEquals(expectedColumns))
        {
            throw new InvalidDataException(
                $"database table '{table}' has an unsupported shape; expected " +
                $"[{string.Join(", ", expectedColumns.Select(DescribeColumn))}], found " +
                $"[{string.Join(", ", actual.Select(DescribeColumn))}]");
        }

        VerifyForeignKeys(connection, transaction, table, expectedForeignKeys ?? []);
        VerifyUniqueConstraints(connection, transaction, table, uniqueConstraints ?? []);
    }

    private static string DescribeColumn(ColumnSchema column) =>
        $"{column.Name}:{column.Type}:not-null={column.NotNull}:default={column.DefaultValue ?? "NULL"}:pk={column.PrimaryKeyOrdinal}";

    private static List<ColumnSchema> ReadColumns(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string table)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA table_xinfo({QuoteIdentifier(table)});";
        using var reader = command.ExecuteReader();
        var columns = new List<ColumnSchema>();
        while (reader.Read())
        {
            if (reader.GetInt32(6) != 0)
            {
                throw new InvalidDataException($"database table '{table}' contains a hidden or generated column");
            }

            columns.Add(new ColumnSchema(
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3) != 0,
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetInt32(5)));
        }

        return columns;
    }

    private static void VerifyLegacySchema(
        SqliteConnection connection,
        IReadOnlySet<string> tables)
    {
        foreach (var table in tables)
        {
            switch (table)
            {
                case "agents":
                    VerifyLegacyColumns(
                        connection,
                        table,
                        PhaseOneAgentColumns,
                        PhaseOneAgentColumns.Where(column => column.Name != "custom_parameters_json")
                            .Select(column => column.Name));
                    VerifyForeignKeys(connection, null, table, []);
                    VerifyUniqueConstraints(connection, null, table, [["name"]]);
                    break;
                case "enroll_tokens":
                    VerifyTable(connection, null, table, EnrollTokenColumns);
                    break;
                case "builds":
                    VerifyLegacyBuildTable(connection);
                    break;
                case "build_queue":
                    VerifyLegacyColumns(
                        connection,
                        table,
                        QueueColumns,
                        QueueColumns
                            .Where(column => column.Name is not "dispatched_session_id" and not "queue_deadline_unix_ms")
                            .Select(column => column.Name));
                    VerifyForeignKeys(connection, null, table, QueueForeignKeys);
                    VerifyUniqueConstraints(connection, null, table, [["build_id"]]);
                    VerifyTableSqlContains(connection, null, table, QueueStateConstraintSql);
                    VerifyTableSqlContains(connection, null, table, QueueShapeConstraintSql);
                    break;
                case "matrix_builds":
                    VerifyTable(
                        connection,
                        null,
                        table,
                        MatrixBuildColumns,
                        uniqueConstraints: [["request_id"]]);
                    break;
                case "matrix_build_cells":
                    VerifyLegacyColumns(
                        connection,
                        table,
                        MatrixCellColumns,
                        MatrixCellColumns.Where(column => column.Name != "rid").Select(column => column.Name));
                    VerifyForeignKeys(connection, null, table, MatrixCellForeignKeys);
                    VerifyUniqueConstraints(
                        connection,
                        null,
                        table,
                        [["build_id"], ["matrix_build_id", "ordinal"]]);
                    break;
            }
        }

        VerifyLegacyNamedObjects(connection, tables.Contains("builds") && !BuildsTableSupportsQueue(connection));
    }

    private static void VerifyLegacyBuildTable(SqliteConnection connection)
    {
        if (!BuildsTableSupportsQueue(connection))
        {
            VerifyTable(connection, null, "builds", LegacyBuildColumns);
            VerifyTableSql(connection, "builds", LegacyBuildTableSql);
            return;
        }

        VerifyLegacyColumns(
            connection,
            "builds",
            BuildColumns,
            LegacyBuildColumns.Select(column => column.Name));
        VerifyForeignKeys(connection, null, "builds", []);
        VerifyUniqueConstraints(connection, null, "builds", []);
        VerifyTableSqlContains(connection, null, "builds", BuildStateConstraintSql);
    }

    private static bool BuildsTableSupportsQueue(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'builds';";
        return (command.ExecuteScalar() as string)?.Contains("'QUEUED'", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static void VerifyLegacyColumns(
        SqliteConnection connection,
        string table,
        IReadOnlyCollection<ColumnSchema> allowedColumns,
        IEnumerable<string> requiredNames)
    {
        var allowed = allowedColumns.ToDictionary(column => column.Name, StringComparer.Ordinal);
        var actual = ReadColumns(connection, null, table);
        var actualNames = actual.Select(column => column.Name).ToHashSet(StringComparer.Ordinal);
        var required = requiredNames.ToHashSet(StringComparer.Ordinal);
        if (!required.IsSubsetOf(actualNames) ||
            actual.Any(column => !allowed.TryGetValue(column.Name, out var expected) || expected != column))
        {
            throw new InvalidDataException($"legacy database table '{table}' has an unsupported shape");
        }
    }

    private static void VerifyForeignKeys(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string table,
        IReadOnlyCollection<ForeignKeySchema> expected)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA foreign_key_list({QuoteIdentifier(table)});";
        using var reader = command.ExecuteReader();
        var actual = new HashSet<ForeignKeySchema>();
        while (reader.Read())
        {
            if (reader.GetInt32(1) != 0)
            {
                throw new InvalidDataException($"database table '{table}' contains an unsupported composite foreign key");
            }

            actual.Add(new ForeignKeySchema(
                reader.GetString(3),
                reader.GetString(2),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7)));
        }

        if (!actual.SetEquals(expected))
        {
            throw new InvalidDataException($"database table '{table}' has unsupported foreign keys");
        }
    }

    private static void VerifyUniqueConstraints(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string table,
        IReadOnlyCollection<string[]> expected)
    {
        using var indexes = connection.CreateCommand();
        indexes.Transaction = transaction;
        indexes.CommandText = "SELECT name FROM pragma_index_list($table) WHERE origin = 'u';";
        indexes.Parameters.AddWithValue("$table", table);
        using var reader = indexes.ExecuteReader();
        var indexNames = new List<string>();
        while (reader.Read())
        {
            indexNames.Add(reader.GetString(0));
        }
        reader.Close();

        var actual = new HashSet<string>(StringComparer.Ordinal);
        foreach (var indexName in indexNames)
        {
            using var columns = connection.CreateCommand();
            columns.Transaction = transaction;
            columns.CommandText = "SELECT name FROM pragma_index_info($index) ORDER BY seqno;";
            columns.Parameters.AddWithValue("$index", indexName);
            using var columnReader = columns.ExecuteReader();
            var names = new List<string>();
            while (columnReader.Read())
            {
                if (columnReader.IsDBNull(0))
                {
                    throw new InvalidDataException($"database table '{table}' has an expression-based unique constraint");
                }

                names.Add(columnReader.GetString(0));
            }

            actual.Add(string.Join('\u001f', names));
        }

        var expectedSignatures = expected
            .Select(columns => string.Join('\u001f', columns))
            .ToHashSet(StringComparer.Ordinal);
        if (!actual.SetEquals(expectedSignatures))
        {
            throw new InvalidDataException($"database table '{table}' has unsupported unique constraints");
        }
    }

    private static void VerifyExactNamedObjects(
        SqliteConnection connection,
        IReadOnlyDictionary<string, NamedSchemaObject> expected)
    {
        var actual = ReadNamedObjects(connection);
        if (!actual.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(expected.Keys))
        {
            throw new InvalidDataException(
                $"database contains an unsupported index/trigger set; expected [{string.Join(", ", expected.Keys.Order())}], " +
                $"found [{string.Join(", ", actual.Keys.Order())}]");
        }

        foreach (var pair in expected)
        {
            VerifyNamedObject(pair.Key, pair.Value, actual[pair.Key]);
        }
    }

    private static void VerifyLegacyNamedObjects(SqliteConnection connection, bool earliestBuildSchema)
    {
        var actual = ReadNamedObjects(connection);
        foreach (var pair in actual)
        {
            if (!PhaseOneNamedObjects.TryGetValue(pair.Key, out var expected))
            {
                throw new InvalidDataException($"legacy database contains unsupported index or trigger '{pair.Key}'");
            }

            if (earliestBuildSchema && pair.Key == "builds_one_active_per_agent")
            {
                expected = LegacyBuildActiveIndex;
            }

            VerifyNamedObject(pair.Key, expected, pair.Value);
        }
    }

    private static void VerifyNamedObject(
        string name,
        NamedSchemaObject expected,
        NamedSchemaObject actual)
    {
        if (!string.Equals(actual.Type, expected.Type, StringComparison.Ordinal) ||
            !string.Equals(NormalizeSql(actual.Sql), NormalizeSql(expected.Sql), StringComparison.Ordinal))
        {
            throw new InvalidDataException($"database {expected.Type} '{name}' has an unsupported definition");
        }
    }

    private static Dictionary<string, NamedSchemaObject> ReadNamedObjects(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT type, name, sql FROM sqlite_master
            WHERE (type = 'index' AND sql IS NOT NULL) OR type IN ('trigger', 'view');
            """;
        using var reader = command.ExecuteReader();
        var result = new Dictionary<string, NamedSchemaObject>(StringComparer.Ordinal);
        while (reader.Read())
        {
            result.Add(reader.GetString(1), new NamedSchemaObject(reader.GetString(0), reader.GetString(2)));
        }

        return result;
    }

    private static void VerifyTableSql(SqliteConnection connection, string table, string expected)
    {
        var actual = ReadTableSql(connection, null, table);
        if (!string.Equals(NormalizeSql(actual), NormalizeSql(expected), StringComparison.Ordinal))
        {
            throw new InvalidDataException($"database table '{table}' has an unsupported definition");
        }
    }

    private static void VerifyExactTableDefinition(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string table,
        string expectedSchemaSql)
    {
        var actual = ReadTableSql(connection, transaction, table);
        var expected = ExtractTableDefinition(expectedSchemaSql, table);
        if (!string.Equals(NormalizeSql(actual), NormalizeSql(expected), StringComparison.Ordinal))
        {
            throw new InvalidDataException($"database table '{table}' has an unsupported definition");
        }
    }

    private static void VerifyRenamedTableDefinitionBody(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string table,
        string expectedSchemaSql)
    {
        var actual = NormalizeSql(ReadTableSql(connection, transaction, table));
        var expected = NormalizeSql(ExtractTableDefinition(expectedSchemaSql, table));
        var actualBody = actual.IndexOf('(');
        var expectedBody = expected.IndexOf('(');
        if (actualBody < 0 || expectedBody < 0 ||
            !string.Equals(
                actual[actualBody..],
                expected[expectedBody..],
                StringComparison.Ordinal))
        {
            throw new InvalidDataException($"database table '{table}' has an unsupported definition");
        }
    }

    private static void VerifyTableSqlContains(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string table,
        string expectedFragment)
    {
        var actual = NormalizeSql(ReadTableSql(connection, transaction, table));
        if (!actual.Contains(NormalizeSql(expectedFragment), StringComparison.Ordinal))
        {
            throw new InvalidDataException($"database table '{table}' has an unsupported constraint definition");
        }
    }

    private static void VerifyTableConstraintSurface(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string table,
        string expectedSchemaSql)
    {
        var actual = NormalizeSql(ReadTableSql(connection, transaction, table));
        var expected = NormalizeSql(ExtractTableDefinition(expectedSchemaSql, table));
        string[] semanticTokens =
        [
            "check (",
            " collate ",
            " on conflict ",
            " deferrable",
            " initially ",
            " without rowid",
            " strict",
        ];
        if (semanticTokens.Any(token =>
                CountOccurrences(actual, token) != CountOccurrences(expected, token)))
        {
            throw new InvalidDataException(
                $"database table '{table}' has an unsupported constraint definition");
        }
    }

    private static string ExtractTableDefinition(string schemaSql, string table)
    {
        var marker = $"CREATE TABLE {table}";
        var start = schemaSql.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            marker = $"CREATE TABLE IF NOT EXISTS {table}";
            start = schemaSql.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        }

        if (start < 0)
        {
            throw new InvalidDataException($"migration manifest is missing table definition '{table}'");
        }

        var opening = schemaSql.IndexOf('(', start);
        if (opening < 0)
        {
            throw new InvalidDataException($"migration manifest has an invalid table definition '{table}'");
        }

        var depth = 0;
        var quote = '\0';
        for (var index = opening; index < schemaSql.Length; index++)
        {
            var character = schemaSql[index];
            if (quote != '\0')
            {
                if (character == quote)
                {
                    if (index + 1 < schemaSql.Length && schemaSql[index + 1] == quote)
                    {
                        index++;
                    }
                    else
                    {
                        quote = '\0';
                    }
                }

                continue;
            }

            if (character is '\'' or '"' or '`')
            {
                quote = character;
            }
            else if (character == '(')
            {
                depth++;
            }
            else if (character == ')' && --depth == 0)
            {
                var terminator = schemaSql.IndexOf(';', index + 1);
                return terminator < 0
                    ? schemaSql[start..].Trim()
                    : schemaSql[start..terminator];
            }
        }

        throw new InvalidDataException($"migration manifest has an unterminated table definition '{table}'");
    }

    private static int CountOccurrences(string value, string token)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(token, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += token.Length;
        }

        return count;
    }

    private static string ReadTableSql(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string table)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", table);
        return command.ExecuteScalar() as string
            ?? throw new InvalidDataException($"database is missing table '{table}'");
    }

    private static string NormalizeSql(string sql)
    {
        var normalized = new StringBuilder(sql.Length);
        var quote = '\0';
        var pendingSpace = false;
        for (var index = 0; index < sql.Length; index++)
        {
            var character = sql[index];
            if (quote != '\0')
            {
                normalized.Append(character);
                if (character == quote)
                {
                    if (index + 1 < sql.Length && sql[index + 1] == quote)
                    {
                        normalized.Append(sql[++index]);
                    }
                    else
                    {
                        quote = '\0';
                    }
                }

                continue;
            }

            if (character is '\'' or '"' or '`')
            {
                if (pendingSpace && normalized.Length > 0)
                {
                    normalized.Append(' ');
                }

                pendingSpace = false;
                quote = character;
                normalized.Append(character);
            }
            else if (char.IsWhiteSpace(character))
            {
                pendingSpace = true;
            }
            else
            {
                if (pendingSpace && normalized.Length > 0)
                {
                    normalized.Append(' ');
                }

                pendingSpace = false;
                normalized.Append(char.ToLowerInvariant(character));
            }
        }

        return normalized.ToString().Trim().TrimEnd(';');
    }

    private static void VerifyExactTableSet(SqliteConnection connection, IReadOnlySet<string> expected)
    {
        var actual = ReadUserTables(connection);
        if (!actual.SetEquals(expected))
        {
            throw new InvalidDataException(
                $"database contains an unsupported table set; expected [{string.Join(", ", expected.Order())}], " +
                $"found [{string.Join(", ", actual.Order())}]");
        }
    }

    private static void VerifyDatabaseIntegrity(SqliteConnection connection, string stage)
    {
        using (var quickCheck = connection.CreateCommand())
        {
            quickCheck.CommandText = "PRAGMA quick_check(1);";
            var result = quickCheck.ExecuteScalar() as string;
            if (!string.Equals(result, "ok", StringComparison.Ordinal))
            {
                throw new InvalidDataException($"database quick check failed {stage}: {result ?? "no result"}");
            }
        }

        using var foreignKeyCheck = connection.CreateCommand();
        foreignKeyCheck.CommandText = "SELECT * FROM pragma_foreign_key_check LIMIT 1;";
        using var reader = foreignKeyCheck.ExecuteReader();
        if (reader.Read())
        {
            throw new InvalidDataException(
                $"database foreign key check failed {stage}: table '{reader.GetString(0)}', row {reader.GetValue(1)}");
        }
    }

    private static bool TableExists(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string table)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", table);
        return command.ExecuteScalar() is not null;
    }

    private static bool TemporaryTableExists(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM sqlite_temp_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", table);
        return command.ExecuteScalar() is not null;
    }

    private static long CountRows(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT COUNT(*) FROM {QuoteIdentifier(table)};";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static string QuoteIdentifier(string value) => $"\"{value.Replace("\"", "\"\"")}\"";

    private static HashSet<string> ReadUserTables(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT name FROM sqlite_master
            WHERE type = 'table' AND name NOT LIKE 'sqlite_%';
            """;
        using var reader = command.ExecuteReader();
        var tables = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            tables.Add(reader.GetString(0));
        }

        return tables;
    }

    private static void Execute(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string ControllerVersion() =>
        typeof(DatabaseMigrator).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(DatabaseMigrator).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    private static string Checksum(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed record Migration(
        int Version,
        string Name,
        string Fingerprint,
        Action<SqliteConnection, SqliteTransaction> Apply)
    {
        public string Checksum { get; } = DatabaseMigrator.Checksum($"{Version}\n{Name}\n{Fingerprint}");
    }

    private sealed record AppliedMigration(int Version, string Name, string Checksum);

    private sealed record ColumnSchema(
        string Name,
        string Type,
        bool NotNull,
        string? DefaultValue = null,
        int PrimaryKeyOrdinal = 0);

    private sealed record ForeignKeySchema(
        string From,
        string ReferencedTable,
        string To,
        string OnUpdate,
        string OnDelete,
        string Match);

    private sealed record NamedSchemaObject(string Type, string Sql);

    private static ColumnSchema Column(
        string name,
        string type,
        bool notNull = false,
        string? defaultValue = null,
        int primaryKeyOrdinal = 0) =>
        new(name, type, notNull, defaultValue, primaryKeyOrdinal);

    private static readonly ColumnSchema[] PhaseOneAgentColumns =
    [
        Column("agent_id", "TEXT", primaryKeyOrdinal: 1),
        Column("name", "TEXT", notNull: true),
        Column("authorized", "INTEGER", notNull: true, defaultValue: "0"),
        Column("enabled", "INTEGER", notNull: true, defaultValue: "1"),
        Column("auth_token_hash", "TEXT"),
        Column("pending_auth_token", "TEXT"),
        Column("enroll_token_hash", "TEXT"),
        Column("first_seen_unix_ms", "INTEGER", notNull: true),
        Column("last_seen_unix_ms", "INTEGER", notNull: true),
        Column("parameters_json", "TEXT", notNull: true, defaultValue: "'{}'"),
        Column("custom_parameters_json", "TEXT", notNull: true, defaultValue: "'{}'"),
        Column("agent_version", "TEXT", notNull: true, defaultValue: "''"),
        Column("os_family", "TEXT", notNull: true, defaultValue: "''"),
        Column("os_version", "TEXT", notNull: true, defaultValue: "''"),
        Column("architecture", "TEXT", notNull: true, defaultValue: "''"),
        Column("interactive", "INTEGER", notNull: true, defaultValue: "0"),
    ];

    private static readonly ColumnSchema[] AgentColumns =
    [
        .. PhaseOneAgentColumns,
        Column("credential_generation", "INTEGER", notNull: true, defaultValue: "0"),
        Column("connection_generation", "INTEGER", notNull: true, defaultValue: "0"),
    ];

    private static readonly ColumnSchema[] EnrollTokenColumns =
    [
        Column("token_hash", "TEXT", primaryKeyOrdinal: 1),
        Column("expires_unix_ms", "INTEGER", notNull: true),
        Column("claimed_agent_id", "TEXT"),
    ];

    private static readonly ColumnSchema[] BuildColumns =
    [
        Column("build_id", "TEXT", primaryKeyOrdinal: 1),
        Column("agent_id", "TEXT"),
        Column("state", "TEXT", notNull: true),
        Column("assignment", "BLOB", notNull: true),
        Column("result", "BLOB"),
        Column("cancellation_reason", "TEXT"),
        Column("owner_session_id", "TEXT"),
        Column("reconnect_deadline_unix_ms", "INTEGER"),
        Column("agent_name_snapshot", "TEXT", notNull: true, defaultValue: "''"),
        Column("agent_parameters_snapshot_json", "TEXT", notNull: true, defaultValue: "'{}'"),
        Column("agent_custom_parameters_snapshot_json", "TEXT", notNull: true, defaultValue: "'{}'"),
        Column("created_unix_ms", "INTEGER", notNull: true),
        Column("updated_unix_ms", "INTEGER", notNull: true),
    ];

    private static readonly ColumnSchema[] LegacyBuildColumns =
    [
        Column("build_id", "TEXT", primaryKeyOrdinal: 1),
        Column("agent_id", "TEXT", notNull: true),
        Column("state", "TEXT", notNull: true),
        Column("assignment", "BLOB", notNull: true),
        Column("result", "BLOB"),
        Column("cancellation_reason", "TEXT"),
        Column("created_unix_ms", "INTEGER", notNull: true),
        Column("updated_unix_ms", "INTEGER", notNull: true),
    ];

    private static readonly ColumnSchema[] QueueColumns =
    [
        Column("queue_id", "INTEGER", primaryKeyOrdinal: 1),
        Column("build_id", "TEXT", notNull: true),
        Column("agent_expression", "TEXT", notNull: true),
        Column("state", "TEXT", notNull: true),
        Column("claimed_agent_id", "TEXT"),
        Column("dispatched_session_id", "TEXT"),
        Column("enqueued_unix_ms", "INTEGER", notNull: true),
        Column("queue_deadline_unix_ms", "INTEGER"),
        Column("claimed_unix_ms", "INTEGER"),
        Column("removed_unix_ms", "INTEGER"),
        Column("removal_reason", "TEXT"),
    ];

    private static readonly ColumnSchema[] MatrixBuildColumns =
    [
        Column("matrix_build_id", "TEXT", primaryKeyOrdinal: 1),
        Column("request_id", "TEXT", notNull: true),
        Column("request_hash", "TEXT", notNull: true),
        Column("request_payload", "BLOB", notNull: true),
        Column("project", "TEXT", notNull: true),
        Column("configuration", "TEXT", notNull: true),
        Column("definition_snapshot", "BLOB", notNull: true),
        Column("definition_hash", "TEXT", notNull: true),
        Column("created_unix_ms", "INTEGER", notNull: true),
        Column("updated_unix_ms", "INTEGER", notNull: true),
    ];

    private static readonly ColumnSchema[] MatrixCellColumns =
    [
        Column("matrix_build_id", "TEXT", notNull: true, primaryKeyOrdinal: 1),
        Column("cell_name", "TEXT", notNull: true, primaryKeyOrdinal: 2),
        Column("ordinal", "INTEGER", notNull: true),
        Column("build_id", "TEXT", notNull: true),
        Column("agent_expression", "TEXT", notNull: true),
        Column("rid", "TEXT", notNull: true, defaultValue: "''"),
    ];

    private static readonly ColumnSchema[] MigrationColumns =
    [
        Column("migration_number", "INTEGER", primaryKeyOrdinal: 1),
        Column("migration_name", "TEXT", notNull: true),
        Column("checksum", "TEXT", notNull: true),
        Column("controller_version", "TEXT", notNull: true),
        Column("applied_unix_ms", "INTEGER", notNull: true),
    ];

    private static readonly ColumnSchema[] MetadataColumns =
    [
        Column("metadata_id", "INTEGER", primaryKeyOrdinal: 1),
        Column("current_version", "INTEGER", notNull: true),
        Column("minimum_controller_version", "TEXT", notNull: true),
    ];

    private static readonly ColumnSchema[] AuditColumns =
    [
        Column("audit_event_id", "TEXT", primaryKeyOrdinal: 1),
        Column("received_unix_ms", "INTEGER", notNull: true),
        Column("actor_type", "TEXT", notNull: true),
        Column("actor_id", "TEXT", notNull: true),
        Column("credential_kind", "TEXT", notNull: true),
        Column("correlation_id", "TEXT", notNull: true),
        Column("request_id", "TEXT"),
        Column("action", "TEXT", notNull: true),
        Column("target_type", "TEXT", notNull: true),
        Column("target_id", "TEXT", notNull: true),
        Column("outcome", "TEXT", notNull: true),
        Column("reason_code", "TEXT", notNull: true, defaultValue: "''"),
        Column("details_json", "TEXT", notNull: true, defaultValue: "'{}'"),
        Column("base_revision", "TEXT"),
        Column("result_revision", "TEXT"),
    ];

    private static readonly ColumnSchema[] MatrixBuildIdempotencyColumns =
    [
        Column("actor_type", "TEXT", notNull: true, primaryKeyOrdinal: 1),
        Column("actor_id", "TEXT", notNull: true, primaryKeyOrdinal: 2),
        Column("request_id", "TEXT", notNull: true, primaryKeyOrdinal: 3),
        Column("matrix_build_id", "TEXT", notNull: true),
    ];

    private static readonly ColumnSchema[] MatrixBuildIdempotencyV7Columns =
    [
        Column("actor_type", "TEXT", notNull: true, primaryKeyOrdinal: 1),
        Column("actor_id", "TEXT", notNull: true, primaryKeyOrdinal: 2),
        Column("operation_kind", "TEXT", notNull: true, primaryKeyOrdinal: 3),
        Column("request_id", "TEXT", notNull: true, primaryKeyOrdinal: 4),
        Column("request_hash", "TEXT", notNull: true),
        Column("matrix_build_id", "TEXT", notNull: true),
        Column("response_status", "INTEGER"),
        Column("response_json", "TEXT"),
        Column("response_etag", "TEXT"),
        Column("created_unix_ms", "INTEGER", notNull: true),
    ];

    private static readonly ColumnSchema[] AgentFactObservationColumns =
    [
        Column("agent_id", "TEXT", notNull: true, primaryKeyOrdinal: 1),
        Column("observation_revision", "INTEGER", notNull: true),
        Column("observed_unix_ms", "INTEGER"),
        Column("received_unix_ms", "INTEGER", notNull: true),
        Column("quality", "TEXT", notNull: true),
        Column("collector_outcome", "TEXT", notNull: true),
        Column("complete", "INTEGER", notNull: true),
        Column("issues_json", "TEXT", notNull: true, defaultValue: "'[]'"),
        Column("credential_generation", "INTEGER", notNull: true),
        Column("connection_generation", "INTEGER", notNull: true),
        Column("package_digest_sha256", "TEXT", notNull: true, defaultValue: "''"),
        Column("hostname", "TEXT", notNull: true, defaultValue: "''"),
        Column("os_family", "TEXT", notNull: true, defaultValue: "''"),
        Column("product_name", "TEXT", notNull: true, defaultValue: "''"),
        Column("product_version", "TEXT", notNull: true, defaultValue: "''"),
        Column("os_build", "TEXT", notNull: true, defaultValue: "''"),
        Column("kernel_version", "TEXT", notNull: true, defaultValue: "''"),
        Column("os_architecture", "TEXT", notNull: true, defaultValue: "''"),
        Column("process_architecture", "TEXT", notNull: true, defaultValue: "''"),
        Column("agent_version", "TEXT", notNull: true, defaultValue: "''"),
        Column("package_version", "TEXT", notNull: true, defaultValue: "''"),
        Column("collector_version", "TEXT", notNull: true, defaultValue: "''"),
        Column("interactive", "INTEGER", notNull: true, defaultValue: "0"),
        Column("extension_facts_json", "TEXT", notNull: true, defaultValue: "'{}'"),
    ];

    private static readonly ColumnSchema[] AgentCapabilityColumns =
    [
        Column("agent_id", "TEXT", notNull: true, primaryKeyOrdinal: 1),
        Column("capability_id", "TEXT", notNull: true, primaryKeyOrdinal: 2),
        Column("contract_major", "INTEGER", notNull: true),
    ];

    private static readonly ColumnSchema[] ConfigurationRevisionSetColumns =
    [
        Column("revision_set_id", "TEXT", primaryKeyOrdinal: 1),
        Column("materialization_scope", "TEXT", notNull: true),
        Column("base_revision_set_id", "TEXT"),
        Column("state", "TEXT", notNull: true),
        Column("operation_id", "TEXT", notNull: true),
        Column("requested_unix_ms", "INTEGER", notNull: true),
        Column("validated_unix_ms", "INTEGER", notNull: true),
        Column("applied_unix_ms", "INTEGER"),
        Column("actor_type", "TEXT", notNull: true),
        Column("actor_id", "TEXT", notNull: true),
        Column("correlation_id", "TEXT", notNull: true),
        Column("request_id", "TEXT"),
        Column("diagnostics_json", "TEXT", notNull: true, defaultValue: "'[]'"),
    ];

    private static readonly ColumnSchema[] ConfigurationRevisionMemberColumns =
    [
        Column("revision_set_id", "TEXT", notNull: true, primaryKeyOrdinal: 1),
        Column("repository_id", "TEXT", notNull: true, primaryKeyOrdinal: 2),
        Column("repository_role", "TEXT", notNull: true),
        Column("commit_sha", "TEXT", notNull: true),
        Column("tree_hash", "TEXT", notNull: true),
        Column("content_hash", "TEXT"),
        Column("schema_version", "TEXT"),
        Column("project_binding", "TEXT"),
    ];

    private static readonly ColumnSchema[] ConfigurationMaterializationScopeColumns =
    [
        Column("materialization_scope", "TEXT", primaryKeyOrdinal: 1),
        Column("active_revision_set_id", "TEXT"),
        Column("last_known_good_revision_set_id", "TEXT"),
        Column("latest_attempt_revision_set_id", "TEXT", notNull: true),
        Column("updated_unix_ms", "INTEGER", notNull: true),
    ];

    private static readonly ColumnSchema[] ConfigurationMutationOperationColumns =
    [
        Column("operation_id", "TEXT", primaryKeyOrdinal: 1),
        Column("operation_kind", "TEXT", notNull: true),
        Column("materialization_scope", "TEXT", notNull: true),
        Column("actor_type", "TEXT", notNull: true),
        Column("actor_id", "TEXT", notNull: true),
        Column("credential_kind", "TEXT", notNull: true),
        Column("request_id", "TEXT", notNull: true),
        Column("correlation_id", "TEXT", notNull: true),
        Column("request_source", "TEXT", notNull: true),
        Column("repository_id", "TEXT", notNull: true),
        Column("expected_base_commit", "TEXT", notNull: true),
        Column("request_hash", "TEXT", notNull: true),
        Column("state", "TEXT", notNull: true),
        Column("result_commit", "TEXT"),
        Column("candidate_content_hash", "TEXT"),
        Column("failure_code", "TEXT", notNull: true, defaultValue: "''"),
        Column("failure_summary", "TEXT", notNull: true, defaultValue: "''"),
        Column("revision_set_id", "TEXT"),
        Column("created_unix_ms", "INTEGER", notNull: true),
        Column("updated_unix_ms", "INTEGER", notNull: true),
    ];

    private static readonly ColumnSchema[] AgentDesiredConfigurationColumns =
    [
        Column("agent_id", "TEXT", primaryKeyOrdinal: 1),
        Column("enabled", "INTEGER", notNull: true),
        Column("source_repository_id", "TEXT", notNull: true),
        Column("source_commit", "TEXT", notNull: true),
        Column("content_hash", "TEXT", notNull: true),
        Column("source_revision_set_id", "TEXT", notNull: true),
        Column("applied_unix_ms", "INTEGER", notNull: true),
    ];

    private static readonly ColumnSchema[] ConfigurationMutationTargetColumns =
    [
        Column("operation_id", "TEXT", notNull: true, primaryKeyOrdinal: 1),
        Column("ordinal", "INTEGER", notNull: true, primaryKeyOrdinal: 2),
        Column("target_type", "TEXT", notNull: true),
        Column("target_id", "TEXT", notNull: true),
        Column("path", "TEXT", notNull: true),
    ];

    private static readonly ColumnSchema[] ConfigurationMutationConflictColumns =
    [
        Column("operation_id", "TEXT", primaryKeyOrdinal: 1),
        Column("current_repository_id", "TEXT", notNull: true),
        Column("current_commit", "TEXT", notNull: true),
        Column("diff_json", "TEXT", notNull: true),
    ];

    private static readonly ColumnSchema[] ConfigurationRepositoryAttemptFailureColumns =
    [
        Column("attempt_id", "TEXT", primaryKeyOrdinal: 1),
        Column("operation_id", "TEXT", notNull: true),
        Column("failure_code", "TEXT", notNull: true),
        Column("failure_summary", "TEXT", notNull: true),
        Column("attempted_unix_ms", "INTEGER", notNull: true),
    ];

    private static readonly ForeignKeySchema[] QueueForeignKeys =
    [
        new("build_id", "builds", "build_id", "NO ACTION", "CASCADE", "NONE"),
    ];

    private static readonly ForeignKeySchema[] MatrixCellForeignKeys =
    [
        new("matrix_build_id", "matrix_builds", "matrix_build_id", "NO ACTION", "CASCADE", "NONE"),
        new("build_id", "builds", "build_id", "NO ACTION", "NO ACTION", "NONE"),
    ];

    private static readonly ForeignKeySchema[] MatrixBuildIdempotencyForeignKeys =
    [
        new("matrix_build_id", "matrix_builds", "matrix_build_id", "NO ACTION", "CASCADE", "NONE"),
    ];

    private static readonly ForeignKeySchema[] AgentFactObservationForeignKeys =
    [
        new("agent_id", "agents", "agent_id", "NO ACTION", "CASCADE", "NONE"),
    ];

    private static readonly ForeignKeySchema[] AgentCapabilityForeignKeys =
    [
        new("agent_id", "agents", "agent_id", "NO ACTION", "CASCADE", "NONE"),
    ];

    private static readonly ForeignKeySchema[] ConfigurationRevisionSetForeignKeys =
    [
        new(
            "base_revision_set_id",
            "configuration_revision_sets",
            "revision_set_id",
            "NO ACTION",
            "NO ACTION",
            "NONE"),
    ];

    private static readonly ForeignKeySchema[] ConfigurationRevisionMemberForeignKeys =
    [
        new(
            "revision_set_id",
            "configuration_revision_sets",
            "revision_set_id",
            "NO ACTION",
            "CASCADE",
            "NONE"),
    ];

    private static readonly ForeignKeySchema[] ConfigurationMaterializationScopeForeignKeys =
    [
        new(
            "active_revision_set_id",
            "configuration_revision_sets",
            "revision_set_id",
            "NO ACTION",
            "NO ACTION",
            "NONE"),
        new(
            "last_known_good_revision_set_id",
            "configuration_revision_sets",
            "revision_set_id",
            "NO ACTION",
            "NO ACTION",
            "NONE"),
        new(
            "latest_attempt_revision_set_id",
            "configuration_revision_sets",
            "revision_set_id",
            "NO ACTION",
            "NO ACTION",
            "NONE"),
    ];

    private static readonly ForeignKeySchema[] ConfigurationMutationOperationForeignKeys =
    [
        new(
            "revision_set_id",
            "configuration_revision_sets",
            "revision_set_id",
            "NO ACTION",
            "NO ACTION",
            "NONE"),
    ];

    private static readonly ForeignKeySchema[] AgentDesiredConfigurationForeignKeys =
    [
        new("agent_id", "agents", "agent_id", "NO ACTION", "CASCADE", "NONE"),
        new(
            "source_revision_set_id",
            "configuration_revision_sets",
            "revision_set_id",
            "NO ACTION",
            "NO ACTION",
            "NONE"),
    ];

    private static readonly ForeignKeySchema[] ConfigurationMutationTargetForeignKeys =
    [
        new(
            "operation_id",
            "configuration_mutation_operations",
            "operation_id",
            "NO ACTION",
            "CASCADE",
            "NONE"),
    ];

    private static readonly ForeignKeySchema[] ConfigurationMutationConflictForeignKeys =
    [
        new(
            "operation_id",
            "configuration_mutation_operations",
            "operation_id",
            "NO ACTION",
            "CASCADE",
            "NONE"),
    ];

    private static readonly ForeignKeySchema[] ConfigurationRepositoryAttemptFailureForeignKeys =
    [
        new(
            "operation_id",
            "configuration_mutation_operations",
            "operation_id",
            "NO ACTION",
            "CASCADE",
            "NONE"),
    ];

    private static readonly IReadOnlyDictionary<string, NamedSchemaObject> EmptyNamedObjects =
        new Dictionary<string, NamedSchemaObject>(StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, NamedSchemaObject> PhaseOneNamedObjects =
        new Dictionary<string, NamedSchemaObject>(StringComparer.Ordinal)
        {
            ["builds_one_active_per_agent"] = new("index", BuildActiveIndexDefinitionSql),
            ["builds_due_reconnect"] = new("index", BuildReconnectIndexDefinitionSql),
            ["build_queue_pending_fifo"] = new("index", QueueFifoIndexDefinitionSql),
            ["build_queue_one_claim_per_agent"] = new("index", QueueClaimIndexDefinitionSql),
            ["build_queue_due"] = new("index", QueueDeadlineIndexDefinitionSql),
            ["matrix_build_cells_build"] = new("index", MatrixCellBuildIndexDefinitionSql),
        };

    private static readonly IReadOnlyDictionary<string, NamedSchemaObject> AuditNamedObjects =
        CreateAuditNamedObjects();

    private static readonly IReadOnlyDictionary<string, NamedSchemaObject> VersionThreeNamedObjects =
        CreateVersionThreeNamedObjects();

    private static readonly IReadOnlyDictionary<string, NamedSchemaObject> VersionFourNamedObjects =
        CreateVersionFourNamedObjects();

    private static readonly IReadOnlyDictionary<string, NamedSchemaObject> VersionFiveNamedObjects =
        CreateVersionFiveNamedObjects();

    private static readonly IReadOnlyDictionary<string, NamedSchemaObject> VersionSixNamedObjects =
        CreateCurrentNamedObjects();

    private static readonly IReadOnlyDictionary<string, NamedSchemaObject> VersionSevenNamedObjects =
        CreateVersionSevenNamedObjects();

    private static readonly IReadOnlyDictionary<string, NamedSchemaObject> VersionEightNamedObjects =
        CreateVersionEightNamedObjects();

    private static readonly IReadOnlyDictionary<string, NamedSchemaObject> VersionNineNamedObjects =
        CreateVersionNineNamedObjects();

    private static readonly IReadOnlyDictionary<string, NamedSchemaObject> VersionTenNamedObjects =
        CreateVersionTenNamedObjects();

    private static readonly IReadOnlyDictionary<string, NamedSchemaObject> VersionElevenNamedObjects =
        VersionTenNamedObjects;

    private static readonly IReadOnlyDictionary<string, NamedSchemaObject> CurrentNamedObjects =
        CreateVersionTwelveNamedObjects();

    private static IReadOnlyDictionary<string, NamedSchemaObject> CreateAuditNamedObjects()
    {
        var result = new Dictionary<string, NamedSchemaObject>(StringComparer.Ordinal);
        foreach (var pair in PhaseOneNamedObjects)
        {
            result.Add(pair.Key, pair.Value);
        }

        result.Add("audit_events_by_time", new("index", AuditTimeIndexDefinitionSql));
        result.Add("audit_events_by_actor", new("index", AuditActorIndexDefinitionSql));
        result.Add("audit_events_by_target", new("index", AuditTargetIndexDefinitionSql));
        result.Add("audit_events_no_update", new("trigger", AuditNoUpdateTriggerDefinitionSql));
        result.Add("audit_events_no_delete", new("trigger", AuditNoDeleteTriggerDefinitionSql));
        return result;
    }

    private static IReadOnlyDictionary<string, NamedSchemaObject> CreateVersionThreeNamedObjects()
    {
        var result = new Dictionary<string, NamedSchemaObject>(AuditNamedObjects, StringComparer.Ordinal)
        {
            ["audit_events_no_replace"] = new("trigger", AuditNoReplaceTriggerDefinitionSql),
        };
        return result;
    }

    private static IReadOnlyDictionary<string, NamedSchemaObject> CreateVersionFourNamedObjects()
    {
        var result = new Dictionary<string, NamedSchemaObject>(VersionThreeNamedObjects, StringComparer.Ordinal)
        {
            ["agent_fact_observations_by_hostname"] = new("index", AgentFactsHostnameIndexDefinitionSql),
            ["agent_fact_observations_by_os_family"] = new("index", AgentFactsOsFamilyIndexDefinitionSql),
            ["agent_fact_observations_by_product_version"] = new("index", AgentFactsProductVersionIndexDefinitionSql),
            ["agent_fact_observations_by_os_build"] = new("index", AgentFactsOsBuildIndexDefinitionSql),
            ["agent_fact_observations_by_architecture"] = new("index", AgentFactsArchitectureIndexDefinitionSql),
            ["agent_fact_observations_by_agent_version"] = new("index", AgentFactsAgentVersionIndexDefinitionSql),
            ["agent_fact_observations_by_package_digest"] = new("index", AgentFactsPackageDigestIndexDefinitionSql),
            ["agent_capabilities_by_capability"] = new("index", AgentCapabilitiesCapabilityIndexDefinitionSql),
        };
        return result;
    }

    private static IReadOnlyDictionary<string, NamedSchemaObject> CreateVersionFiveNamedObjects()
    {
        var result = new Dictionary<string, NamedSchemaObject>(VersionFourNamedObjects, StringComparer.Ordinal)
        {
            ["configuration_revision_sets_one_active"] =
                new("index", ConfigurationRevisionSetsOneActiveIndexDefinitionSql),
            ["configuration_revision_sets_by_scope"] =
                new("index", ConfigurationRevisionSetsByScopeIndexDefinitionSql),
            ["configuration_revision_members_by_commit"] =
                new("index", ConfigurationRevisionMembersByCommitIndexDefinitionSql),
            ["configuration_revision_members_one_control"] =
                new("index", ConfigurationRevisionMembersOneControlIndexDefinitionSql),
            ["configuration_mutation_operations_by_state"] =
                new("index", ConfigurationMutationOperationsByStateIndexDefinitionSql),
            ["agent_desired_configuration_by_source"] =
                new("index", AgentDesiredConfigurationBySourceIndexDefinitionSql),
        };
        return result;
    }

    private static IReadOnlyDictionary<string, NamedSchemaObject> CreateCurrentNamedObjects()
    {
        var result = new Dictionary<string, NamedSchemaObject>(VersionFiveNamedObjects, StringComparer.Ordinal)
        {
            ["configuration_repository_attempt_failures_by_operation"] =
                new("index", ConfigurationRepositoryAttemptFailuresByOperationIndexDefinitionSql),
        };
        return result;
    }

    private static IReadOnlyDictionary<string, NamedSchemaObject> CreateVersionSevenNamedObjects()
    {
        var result = new Dictionary<string, NamedSchemaObject>(VersionSixNamedObjects, StringComparer.Ordinal)
        {
            ["blob_upload_plans_due"] = new("index", BlobUploadPlansDueIndexDefinitionSql),
            ["blob_upload_plan_items_by_hash"] =
                new("index", BlobUploadPlanItemsByHashIndexDefinitionSql),
            ["blob_upload_receipts_by_hash"] =
                new("index", BlobUploadReceiptsByHashIndexDefinitionSql),
            ["blob_principal_project_grants_by_hash"] =
                new("index", BlobPrincipalProjectGrantsByHashIndexDefinitionSql),
            ["blob_build_payload_references_by_hash"] =
                new("index", BlobBuildPayloadReferencesByHashIndexDefinitionSql),
            ["blob_artifact_upload_staging_due"] =
                new("index", BlobArtifactUploadStagingDueIndexDefinitionSql),
            ["blob_artifact_upload_receipts_by_hash"] =
                new("index", BlobArtifactUploadReceiptsByHashIndexDefinitionSql),
            ["blob_build_artifact_references_by_hash"] =
                new("index", BlobBuildArtifactReferencesByHashIndexDefinitionSql),
            ["ix_build_events_matrix_sequence"] =
                new("index", BuildEventsMatrixSequenceIndexDefinitionSql),
        };
        return result;
    }

    private static IReadOnlyDictionary<string, NamedSchemaObject> CreateVersionEightNamedObjects()
    {
        var result = new Dictionary<string, NamedSchemaObject>(VersionSevenNamedObjects, StringComparer.Ordinal)
        {
            ["trx_result_projections_by_build"] =
                new("index", TrxResultProjectionsByBuildIndexDefinitionSql),
            ["trx_test_definitions_by_test"] =
                new("index", TrxTestDefinitionsByTestIndexDefinitionSql),
            ["trx_test_occurrences_by_test_outcome"] =
                new("index", TrxTestOccurrencesByTestOutcomeIndexDefinitionSql),
        };
        return result;
    }

    private static IReadOnlyDictionary<string, NamedSchemaObject> CreateVersionNineNamedObjects()
    {
        var result = new Dictionary<string, NamedSchemaObject>(VersionEightNamedObjects, StringComparer.Ordinal)
        {
            ["administration_token_generations_one_current"] =
                new("index", AdministrationTokenGenerationsOneCurrentIndexDefinitionSql),
            ["administration_token_generations_due"] =
                new("index", AdministrationTokenGenerationsDueIndexDefinitionSql),
            ["administration_setup_sessions_one_current"] =
                new("index", AdministrationSetupSessionsOneCurrentIndexDefinitionSql),
            ["administration_setup_sessions_due"] =
                new("index", AdministrationSetupSessionsDueIndexDefinitionSql),
        };
        return result;
    }

    private static IReadOnlyDictionary<string, NamedSchemaObject> CreateVersionTenNamedObjects()
    {
        var result = new Dictionary<string, NamedSchemaObject>(VersionNineNamedObjects, StringComparer.Ordinal)
        {
            ["authorization_role_bindings_by_principal"] =
                new("index", AuthorizationRoleBindingsByPrincipalIndexDefinitionSql),
            ["authorization_role_bindings_by_scope"] =
                new("index", AuthorizationRoleBindingsByScopeIndexDefinitionSql),
        };
        return result;
    }

    private static IReadOnlyDictionary<string, NamedSchemaObject> CreateVersionTwelveNamedObjects()
    {
        var result = new Dictionary<string, NamedSchemaObject>(VersionElevenNamedObjects, StringComparer.Ordinal)
        {
            ["agent_packages_by_rid"] =
                new("index", AgentPackagesByRidIndexDefinitionSql),
            ["agent_upgrade_operations_by_agent"] =
                new("index", AgentUpgradeOperationsByAgentIndexDefinitionSql),
            ["agent_upgrade_operations_due"] =
                new("index", AgentUpgradeOperationsDueIndexDefinitionSql),
            ["agent_upgrade_operations_one_active"] =
                new("index", AgentUpgradeOperationsOneActiveIndexDefinitionSql),
            ["agent_upgrade_events_by_operation"] =
                new("index", AgentUpgradeEventsByOperationIndexDefinitionSql),
        };
        return result;
    }

    private static readonly NamedSchemaObject LegacyBuildActiveIndex =
        new("index", LegacyBuildActiveIndexDefinitionSql);

    private const string LegacyBuildTableSql = """
        CREATE TABLE builds (
            build_id TEXT PRIMARY KEY,
            agent_id TEXT NOT NULL,
            state TEXT NOT NULL CHECK (state IN ('RUNNING', 'CANCEL_REQUESTED', 'FINISHED')),
            assignment BLOB NOT NULL,
            result BLOB NULL,
            cancellation_reason TEXT NULL,
            created_unix_ms INTEGER NOT NULL,
            updated_unix_ms INTEGER NOT NULL
        )
        """;

    private const string BuildStateConstraintSql =
        "CHECK (state IN ('QUEUED', 'RUNNING', 'CANCEL_REQUESTED', 'FINISHED'))";

    private const string QueueStateConstraintSql =
        "CHECK (state IN ('QUEUED', 'CLAIMED', 'REMOVED'))";

    private const string QueueShapeConstraintSql = """
        CHECK (
            (state = 'QUEUED' AND claimed_agent_id IS NULL AND claimed_unix_ms IS NULL
                AND removed_unix_ms IS NULL)
            OR (state = 'CLAIMED' AND claimed_agent_id IS NOT NULL AND claimed_unix_ms IS NOT NULL
                AND removed_unix_ms IS NULL)
            OR (state = 'REMOVED' AND removed_unix_ms IS NOT NULL)
        )
        """;

    private const string MetadataIdConstraintSql = "CHECK (metadata_id = 1)";

    private const string AuditOutcomeConstraintSql =
        "CHECK (outcome IN ('SUCCEEDED', 'DENIED', 'FAILED', 'NO_CHANGE'))";

    private const string BuildActiveIndexDefinitionSql = """
        CREATE UNIQUE INDEX builds_one_active_per_agent
        ON builds(agent_id)
        WHERE state IN ('RUNNING', 'CANCEL_REQUESTED')
        """;

    private const string LegacyBuildActiveIndexDefinitionSql = """
        CREATE UNIQUE INDEX builds_one_active_per_agent
        ON builds(agent_id) WHERE state <> 'FINISHED'
        """;

    private const string BuildReconnectIndexDefinitionSql = """
        CREATE INDEX builds_due_reconnect
        ON builds(reconnect_deadline_unix_ms)
        WHERE state IN ('RUNNING', 'CANCEL_REQUESTED')
            AND reconnect_deadline_unix_ms IS NOT NULL
        """;

    private const string QueueFifoIndexDefinitionSql = """
        CREATE INDEX build_queue_pending_fifo
        ON build_queue(state, queue_id)
        """;

    private const string QueueClaimIndexDefinitionSql = """
        CREATE UNIQUE INDEX build_queue_one_claim_per_agent
        ON build_queue(claimed_agent_id)
        WHERE state = 'CLAIMED'
        """;

    private const string QueueDeadlineIndexDefinitionSql = """
        CREATE INDEX build_queue_due
        ON build_queue(queue_deadline_unix_ms)
        WHERE state IN ('QUEUED', 'CLAIMED')
            AND queue_deadline_unix_ms IS NOT NULL
        """;

    private const string MatrixCellBuildIndexDefinitionSql = """
        CREATE INDEX matrix_build_cells_build
        ON matrix_build_cells(build_id)
        """;

    private const string AuditTimeIndexDefinitionSql = """
        CREATE INDEX audit_events_by_time
        ON audit_events(received_unix_ms DESC, audit_event_id DESC)
        """;

    private const string AuditActorIndexDefinitionSql = """
        CREATE INDEX audit_events_by_actor
        ON audit_events(actor_id, received_unix_ms DESC, audit_event_id DESC)
        """;

    private const string AuditTargetIndexDefinitionSql = """
        CREATE INDEX audit_events_by_target
        ON audit_events(target_type, target_id, received_unix_ms DESC, audit_event_id DESC)
        """;

    private const string AuditNoUpdateTriggerDefinitionSql = """
        CREATE TRIGGER audit_events_no_update
        BEFORE UPDATE ON audit_events
        BEGIN
            SELECT RAISE(ABORT, 'audit_events is append-only');
        END
        """;

    private const string AuditNoDeleteTriggerDefinitionSql = """
        CREATE TRIGGER audit_events_no_delete
        BEFORE DELETE ON audit_events
        BEGIN
            SELECT RAISE(ABORT, 'audit_events is append-only');
        END
        """;

    private const string AuditNoReplaceTriggerDefinitionSql = """
        CREATE TRIGGER audit_events_no_replace
        BEFORE INSERT ON audit_events
        WHEN EXISTS (
            SELECT 1 FROM audit_events
            WHERE audit_event_id = NEW.audit_event_id
        )
        BEGIN
            SELECT RAISE(ABORT, 'audit_events is append-only');
        END
        """;

    private const string AgentFactsHostnameIndexDefinitionSql = """
        CREATE INDEX agent_fact_observations_by_hostname
        ON agent_fact_observations(hostname COLLATE NOCASE, agent_id)
        """;

    private const string AgentFactsOsFamilyIndexDefinitionSql = """
        CREATE INDEX agent_fact_observations_by_os_family
        ON agent_fact_observations(os_family COLLATE NOCASE, agent_id)
        """;

    private const string AgentFactsProductVersionIndexDefinitionSql = """
        CREATE INDEX agent_fact_observations_by_product_version
        ON agent_fact_observations(product_version COLLATE NOCASE, agent_id)
        """;

    private const string AgentFactsOsBuildIndexDefinitionSql = """
        CREATE INDEX agent_fact_observations_by_os_build
        ON agent_fact_observations(os_build COLLATE NOCASE, agent_id)
        """;

    private const string AgentFactsArchitectureIndexDefinitionSql = """
        CREATE INDEX agent_fact_observations_by_architecture
        ON agent_fact_observations(os_architecture COLLATE NOCASE, agent_id)
        """;

    private const string AgentFactsAgentVersionIndexDefinitionSql = """
        CREATE INDEX agent_fact_observations_by_agent_version
        ON agent_fact_observations(agent_version COLLATE BINARY, agent_id)
        """;

    private const string AgentFactsPackageDigestIndexDefinitionSql = """
        CREATE INDEX agent_fact_observations_by_package_digest
        ON agent_fact_observations(package_digest_sha256 COLLATE BINARY, agent_id)
        """;

    private const string AgentCapabilitiesCapabilityIndexDefinitionSql = """
        CREATE INDEX agent_capabilities_by_capability
        ON agent_capabilities(capability_id COLLATE BINARY, contract_major, agent_id)
        """;

    private const string ConfigurationRevisionSetsOneActiveIndexDefinitionSql = """
        CREATE UNIQUE INDEX configuration_revision_sets_one_active
        ON configuration_revision_sets(materialization_scope)
        WHERE state = 'ACTIVE'
        """;

    private const string ConfigurationRevisionSetsByScopeIndexDefinitionSql = """
        CREATE INDEX configuration_revision_sets_by_scope
        ON configuration_revision_sets(
            materialization_scope, validated_unix_ms DESC, revision_set_id DESC)
        """;

    private const string ConfigurationRevisionMembersByCommitIndexDefinitionSql = """
        CREATE INDEX configuration_revision_members_by_commit
        ON configuration_revision_members(repository_id, commit_sha, revision_set_id)
        """;

    private const string ConfigurationRevisionMembersOneControlIndexDefinitionSql = """
        CREATE UNIQUE INDEX configuration_revision_members_one_control
        ON configuration_revision_members(revision_set_id)
        WHERE repository_role = 'CONTROL'
        """;

    private const string ConfigurationMutationOperationsByStateIndexDefinitionSql = """
        CREATE INDEX configuration_mutation_operations_by_state
        ON configuration_mutation_operations(state, updated_unix_ms, operation_id)
        """;

    private const string AgentDesiredConfigurationBySourceIndexDefinitionSql = """
        CREATE INDEX agent_desired_configuration_by_source
        ON agent_desired_configuration(source_repository_id, source_commit, agent_id)
        """;

    private const string ConfigurationRepositoryAttemptFailuresByOperationIndexDefinitionSql = """
        CREATE INDEX configuration_repository_attempt_failures_by_operation
        ON configuration_repository_attempt_failures(
            operation_id, attempted_unix_ms, attempt_id)
        """;

    private const string BlobUploadPlansDueIndexDefinitionSql = """
        CREATE INDEX blob_upload_plans_due
        ON blob_upload_plans(expires_unix_ms, staging_id)
        """;

    private const string BlobUploadPlanItemsByHashIndexDefinitionSql = """
        CREATE INDEX blob_upload_plan_items_by_hash
        ON blob_upload_plan_items(sha256, staging_id)
        """;

    private const string BlobUploadReceiptsByHashIndexDefinitionSql = """
        CREATE INDEX blob_upload_receipts_by_hash
        ON blob_upload_receipts(sha256, staging_id)
        """;

    private const string BlobPrincipalProjectGrantsByHashIndexDefinitionSql = """
        CREATE INDEX blob_principal_project_grants_by_hash
        ON blob_principal_project_grants(sha256, project_id)
        """;

    private const string BlobBuildPayloadReferencesByHashIndexDefinitionSql = """
        CREATE INDEX blob_build_payload_references_by_hash
        ON blob_build_payload_references(sha256, matrix_build_id)
        """;

    private const string BlobArtifactUploadStagingDueIndexDefinitionSql = """
        CREATE INDEX blob_artifact_upload_staging_due
        ON blob_artifact_upload_staging(expires_unix_ms, build_id, sha256)
        """;

    private const string BlobArtifactUploadReceiptsByHashIndexDefinitionSql = """
        CREATE INDEX blob_artifact_upload_receipts_by_hash
        ON blob_artifact_upload_receipts(sha256, build_id)
        """;

    private const string BlobBuildArtifactReferencesByHashIndexDefinitionSql = """
        CREATE INDEX blob_build_artifact_references_by_hash
        ON blob_build_artifact_references(sha256, build_id, artifact_id)
        """;

    private const string BuildEventsMatrixSequenceIndexDefinitionSql = """
        CREATE INDEX ix_build_events_matrix_sequence
        ON build_events(matrix_build_id, sequence)
        """;

    private const string MigrationLedgerSql = """
        CREATE TABLE IF NOT EXISTS schema_migrations (
            migration_number INTEGER PRIMARY KEY,
            migration_name TEXT NOT NULL UNIQUE,
            checksum TEXT NOT NULL,
            controller_version TEXT NOT NULL,
            applied_unix_ms INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS schema_metadata (
            metadata_id INTEGER PRIMARY KEY CHECK (metadata_id = 1),
            current_version INTEGER NOT NULL,
            minimum_controller_version TEXT NOT NULL
        );

        INSERT INTO schema_metadata(metadata_id, current_version, minimum_controller_version)
        VALUES (1, 0, '0.0.0')
        ON CONFLICT(metadata_id) DO NOTHING;
        """;

    private const string AgentSchemaSql = """
        CREATE TABLE IF NOT EXISTS agents (
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
            custom_parameters_json TEXT NOT NULL DEFAULT '{}',
            agent_version TEXT NOT NULL DEFAULT '',
            os_family TEXT NOT NULL DEFAULT '',
            os_version TEXT NOT NULL DEFAULT '',
            architecture TEXT NOT NULL DEFAULT '',
            interactive INTEGER NOT NULL DEFAULT 0
        );

        CREATE TABLE IF NOT EXISTS enroll_tokens (
            token_hash TEXT PRIMARY KEY,
            expires_unix_ms INTEGER NOT NULL,
            claimed_agent_id TEXT NULL
        );
        """;

    private const string BuildSchemaSql = """
        CREATE TABLE builds (
            build_id TEXT PRIMARY KEY,
            agent_id TEXT NULL,
            state TEXT NOT NULL CHECK (state IN ('QUEUED', 'RUNNING', 'CANCEL_REQUESTED', 'FINISHED')),
            assignment BLOB NOT NULL,
            result BLOB NULL,
            cancellation_reason TEXT NULL,
            owner_session_id TEXT NULL,
            reconnect_deadline_unix_ms INTEGER NULL,
            agent_name_snapshot TEXT NOT NULL DEFAULT '',
            agent_parameters_snapshot_json TEXT NOT NULL DEFAULT '{}',
            agent_custom_parameters_snapshot_json TEXT NOT NULL DEFAULT '{}',
            created_unix_ms INTEGER NOT NULL,
            updated_unix_ms INTEGER NOT NULL
        );
        """;

    private const string BuildIndexesSql = """
        CREATE UNIQUE INDEX IF NOT EXISTS builds_one_active_per_agent
            ON builds(agent_id)
            WHERE state IN ('RUNNING', 'CANCEL_REQUESTED');

        CREATE INDEX IF NOT EXISTS builds_due_reconnect
            ON builds(reconnect_deadline_unix_ms)
            WHERE state IN ('RUNNING', 'CANCEL_REQUESTED')
                AND reconnect_deadline_unix_ms IS NOT NULL;
        """;

    private const string QueueSchemaSql = """
        CREATE TABLE IF NOT EXISTS build_queue (
            queue_id INTEGER PRIMARY KEY AUTOINCREMENT,
            build_id TEXT NOT NULL UNIQUE REFERENCES builds(build_id) ON DELETE CASCADE,
            agent_expression TEXT NOT NULL,
            state TEXT NOT NULL CHECK (state IN ('QUEUED', 'CLAIMED', 'REMOVED')),
            claimed_agent_id TEXT NULL,
            dispatched_session_id TEXT NULL,
            enqueued_unix_ms INTEGER NOT NULL,
            queue_deadline_unix_ms INTEGER NULL,
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
        """;

    private const string QueueIndexesSql = """
        CREATE INDEX IF NOT EXISTS build_queue_pending_fifo
            ON build_queue(state, queue_id);

        CREATE UNIQUE INDEX IF NOT EXISTS build_queue_one_claim_per_agent
            ON build_queue(claimed_agent_id)
            WHERE state = 'CLAIMED';

        CREATE INDEX IF NOT EXISTS build_queue_due
            ON build_queue(queue_deadline_unix_ms)
            WHERE state IN ('QUEUED', 'CLAIMED')
                AND queue_deadline_unix_ms IS NOT NULL;
        """;

    private const string MatrixSchemaSql = """
        CREATE TABLE IF NOT EXISTS matrix_builds (
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

        CREATE TABLE IF NOT EXISTS matrix_build_cells (
            matrix_build_id TEXT NOT NULL
                REFERENCES matrix_builds(matrix_build_id) ON DELETE CASCADE,
            cell_name TEXT NOT NULL,
            ordinal INTEGER NOT NULL,
            build_id TEXT NOT NULL UNIQUE REFERENCES builds(build_id),
            agent_expression TEXT NOT NULL,
            rid TEXT NOT NULL DEFAULT '',
            PRIMARY KEY (matrix_build_id, cell_name),
            UNIQUE (matrix_build_id, ordinal)
        );

        CREATE INDEX IF NOT EXISTS matrix_build_cells_build
            ON matrix_build_cells(build_id);
        """;

    private const string AuditSchemaSql = """
        CREATE TABLE audit_events (
            audit_event_id TEXT PRIMARY KEY,
            received_unix_ms INTEGER NOT NULL,
            actor_type TEXT NOT NULL,
            actor_id TEXT NOT NULL,
            credential_kind TEXT NOT NULL,
            correlation_id TEXT NOT NULL,
            request_id TEXT NULL,
            action TEXT NOT NULL,
            target_type TEXT NOT NULL,
            target_id TEXT NOT NULL,
            outcome TEXT NOT NULL CHECK (outcome IN ('SUCCEEDED', 'DENIED', 'FAILED', 'NO_CHANGE')),
            reason_code TEXT NOT NULL DEFAULT '',
            details_json TEXT NOT NULL DEFAULT '{}',
            base_revision TEXT NULL,
            result_revision TEXT NULL
        );

        CREATE INDEX audit_events_by_time
            ON audit_events(received_unix_ms DESC, audit_event_id DESC);

        CREATE INDEX audit_events_by_actor
            ON audit_events(actor_id, received_unix_ms DESC, audit_event_id DESC);

        CREATE INDEX audit_events_by_target
            ON audit_events(target_type, target_id, received_unix_ms DESC, audit_event_id DESC);

        CREATE TRIGGER audit_events_no_update
        BEFORE UPDATE ON audit_events
        BEGIN
            SELECT RAISE(ABORT, 'audit_events is append-only');
        END;

        CREATE TRIGGER audit_events_no_delete
        BEFORE DELETE ON audit_events
        BEGIN
            SELECT RAISE(ABORT, 'audit_events is append-only');
        END;
        """;

    private const string PrincipalMatrixIdempotencySchemaSql = """
        CREATE TABLE matrix_build_idempotency (
            actor_type TEXT NOT NULL,
            actor_id TEXT NOT NULL,
            request_id TEXT NOT NULL,
            matrix_build_id TEXT NOT NULL UNIQUE
                REFERENCES matrix_builds(matrix_build_id) ON DELETE CASCADE,
            PRIMARY KEY (actor_type, actor_id, request_id)
        );

        INSERT INTO matrix_build_idempotency(
            actor_type, actor_id, request_id, matrix_build_id)
        SELECT
            'legacy', 'unattributed', request_id, matrix_build_id
        FROM matrix_builds;

        CREATE TRIGGER audit_events_no_replace
        BEFORE INSERT ON audit_events
        WHEN EXISTS (
            SELECT 1 FROM audit_events
            WHERE audit_event_id = NEW.audit_event_id
        )
        BEGIN
            SELECT RAISE(ABORT, 'audit_events is append-only');
        END;
        """;

    private const string AgentFactObservationSchemaSql = """
        ALTER TABLE agents ADD COLUMN credential_generation INTEGER NOT NULL DEFAULT 0
            CHECK (credential_generation >= 0);
        ALTER TABLE agents ADD COLUMN connection_generation INTEGER NOT NULL DEFAULT 0
            CHECK (connection_generation >= 0);

        UPDATE agents SET credential_generation = 1
        WHERE auth_token_hash IS NOT NULL;

        CREATE TABLE agent_fact_observations (
            agent_id TEXT NOT NULL PRIMARY KEY
                REFERENCES agents(agent_id) ON DELETE CASCADE,
            observation_revision INTEGER NOT NULL CHECK (observation_revision > 0),
            observed_unix_ms INTEGER NULL,
            received_unix_ms INTEGER NOT NULL,
            quality TEXT NOT NULL CHECK (quality IN ('complete', 'partial', 'unavailable')),
            collector_outcome TEXT NOT NULL CHECK (collector_outcome IN (
                'succeeded', 'partial', 'degraded', 'permission_denied',
                'temporarily_unavailable', 'failed')),
            complete INTEGER NOT NULL CHECK (complete IN (0, 1)),
            issues_json TEXT NOT NULL DEFAULT '[]',
            credential_generation INTEGER NOT NULL CHECK (credential_generation >= 0),
            connection_generation INTEGER NOT NULL CHECK (connection_generation >= 0),
            package_digest_sha256 TEXT NOT NULL DEFAULT '' CHECK (
                package_digest_sha256 = '' OR (
                    length(package_digest_sha256) = 64
                    AND package_digest_sha256 = lower(package_digest_sha256)
                    AND package_digest_sha256 NOT GLOB '*[^0-9a-f]*')),
            hostname TEXT NOT NULL DEFAULT '',
            os_family TEXT NOT NULL DEFAULT '',
            product_name TEXT NOT NULL DEFAULT '',
            product_version TEXT NOT NULL DEFAULT '',
            os_build TEXT NOT NULL DEFAULT '',
            kernel_version TEXT NOT NULL DEFAULT '',
            os_architecture TEXT NOT NULL DEFAULT '',
            process_architecture TEXT NOT NULL DEFAULT '',
            agent_version TEXT NOT NULL DEFAULT '',
            package_version TEXT NOT NULL DEFAULT '',
            collector_version TEXT NOT NULL DEFAULT '',
            interactive INTEGER NOT NULL DEFAULT 0 CHECK (interactive IN (0, 1)),
            extension_facts_json TEXT NOT NULL DEFAULT '{}'
        );

        CREATE TABLE agent_capabilities (
            agent_id TEXT NOT NULL REFERENCES agents(agent_id) ON DELETE CASCADE,
            capability_id TEXT NOT NULL,
            contract_major INTEGER NOT NULL CHECK (contract_major > 0),
            PRIMARY KEY (agent_id, capability_id)
        );

        CREATE INDEX agent_fact_observations_by_hostname
            ON agent_fact_observations(hostname COLLATE NOCASE, agent_id);

        CREATE INDEX agent_fact_observations_by_os_family
            ON agent_fact_observations(os_family COLLATE NOCASE, agent_id);

        CREATE INDEX agent_fact_observations_by_product_version
            ON agent_fact_observations(product_version COLLATE NOCASE, agent_id);

        CREATE INDEX agent_fact_observations_by_os_build
            ON agent_fact_observations(os_build COLLATE NOCASE, agent_id);

        CREATE INDEX agent_fact_observations_by_architecture
            ON agent_fact_observations(os_architecture COLLATE NOCASE, agent_id);

        CREATE INDEX agent_fact_observations_by_agent_version
            ON agent_fact_observations(agent_version COLLATE BINARY, agent_id);

        CREATE INDEX agent_fact_observations_by_package_digest
            ON agent_fact_observations(package_digest_sha256 COLLATE BINARY, agent_id);

        CREATE INDEX agent_capabilities_by_capability
            ON agent_capabilities(capability_id COLLATE BINARY, contract_major, agent_id);
        """;

    private const string ConfigurationReconciliationSchemaSql = """
        CREATE TABLE configuration_revision_sets (
            revision_set_id TEXT PRIMARY KEY CHECK (
                length(revision_set_id) = 64
                AND revision_set_id = lower(revision_set_id)
                AND revision_set_id NOT GLOB '*[^0-9a-f]*'),
            materialization_scope TEXT NOT NULL CHECK (
                length(materialization_scope) BETWEEN 1 AND 128),
            base_revision_set_id TEXT NULL
                REFERENCES configuration_revision_sets(revision_set_id),
            state TEXT NOT NULL CHECK (state IN ('INVALID', 'BLOCKED', 'ACTIVE', 'SUPERSEDED')),
            operation_id TEXT NOT NULL CHECK (length(operation_id) BETWEEN 1 AND 128),
            requested_unix_ms INTEGER NOT NULL,
            validated_unix_ms INTEGER NOT NULL,
            applied_unix_ms INTEGER NULL,
            actor_type TEXT NOT NULL CHECK (length(actor_type) BETWEEN 1 AND 32),
            actor_id TEXT NOT NULL CHECK (length(actor_id) BETWEEN 1 AND 256),
            correlation_id TEXT NOT NULL CHECK (length(correlation_id) BETWEEN 1 AND 128),
            request_id TEXT NULL CHECK (request_id IS NULL OR length(request_id) BETWEEN 1 AND 256),
            diagnostics_json TEXT NOT NULL DEFAULT '[]' CHECK (
                length(diagnostics_json) BETWEEN 2 AND 8192),
            CHECK (base_revision_set_id IS NULL OR base_revision_set_id <> revision_set_id),
            CHECK (requested_unix_ms <= validated_unix_ms),
            CHECK (
                (state IN ('INVALID', 'BLOCKED')
                    AND applied_unix_ms IS NULL AND diagnostics_json <> '[]')
                OR (state IN ('ACTIVE', 'SUPERSEDED')
                    AND applied_unix_ms IS NOT NULL AND diagnostics_json = '[]')),
            CHECK (applied_unix_ms IS NULL OR validated_unix_ms <= applied_unix_ms)
        );

        CREATE TABLE configuration_revision_members (
            revision_set_id TEXT NOT NULL
                REFERENCES configuration_revision_sets(revision_set_id) ON DELETE CASCADE,
            repository_id TEXT NOT NULL CHECK (length(repository_id) BETWEEN 1 AND 128),
            repository_role TEXT NOT NULL CHECK (repository_role IN ('CONTROL', 'PRODUCT')),
            commit_sha TEXT NOT NULL CHECK (
                length(commit_sha) IN (40, 64)
                AND commit_sha = lower(commit_sha)
                AND commit_sha NOT GLOB '*[^0-9a-f]*'),
            tree_hash TEXT NOT NULL CHECK (
                length(tree_hash) IN (40, 64)
                AND tree_hash = lower(tree_hash)
                AND tree_hash NOT GLOB '*[^0-9a-f]*'),
            content_hash TEXT NULL CHECK (
                content_hash IS NULL OR (
                    length(content_hash) = 64
                    AND content_hash = lower(content_hash)
                    AND content_hash NOT GLOB '*[^0-9a-f]*')),
            schema_version TEXT NULL CHECK (
                schema_version IS NULL OR length(schema_version) BETWEEN 1 AND 64),
            project_binding TEXT NULL CHECK (
                project_binding IS NULL OR length(project_binding) BETWEEN 1 AND 128),
            PRIMARY KEY (revision_set_id, repository_id),
            CHECK ((content_hash IS NULL) = (schema_version IS NULL)),
            CHECK (
                (repository_role = 'CONTROL' AND project_binding IS NULL)
                OR (repository_role = 'PRODUCT' AND project_binding IS NOT NULL))
        );

        CREATE TABLE configuration_materialization_scopes (
            materialization_scope TEXT PRIMARY KEY CHECK (
                length(materialization_scope) BETWEEN 1 AND 128),
            active_revision_set_id TEXT NULL
                REFERENCES configuration_revision_sets(revision_set_id),
            last_known_good_revision_set_id TEXT NULL
                REFERENCES configuration_revision_sets(revision_set_id),
            latest_attempt_revision_set_id TEXT NOT NULL
                REFERENCES configuration_revision_sets(revision_set_id),
            updated_unix_ms INTEGER NOT NULL,
            CHECK (active_revision_set_id IS last_known_good_revision_set_id)
        );

        CREATE TABLE configuration_mutation_operations (
            operation_id TEXT PRIMARY KEY CHECK (length(operation_id) BETWEEN 1 AND 128),
            operation_kind TEXT NOT NULL CHECK (length(operation_kind) BETWEEN 1 AND 128),
            materialization_scope TEXT NOT NULL CHECK (
                length(materialization_scope) BETWEEN 1 AND 128),
            actor_type TEXT NOT NULL CHECK (length(actor_type) BETWEEN 1 AND 32),
            actor_id TEXT NOT NULL CHECK (length(actor_id) BETWEEN 1 AND 256),
            credential_kind TEXT NOT NULL CHECK (length(credential_kind) BETWEEN 1 AND 64),
            request_id TEXT NOT NULL CHECK (length(request_id) BETWEEN 1 AND 256),
            correlation_id TEXT NOT NULL CHECK (length(correlation_id) BETWEEN 1 AND 128),
            request_source TEXT NOT NULL CHECK (length(request_source) BETWEEN 1 AND 128),
            repository_id TEXT NOT NULL CHECK (length(repository_id) BETWEEN 1 AND 128),
            expected_base_commit TEXT NOT NULL CHECK (
                length(expected_base_commit) IN (40, 64)
                AND expected_base_commit = lower(expected_base_commit)
                AND expected_base_commit NOT GLOB '*[^0-9a-f]*'),
            request_hash TEXT NOT NULL CHECK (
                length(request_hash) = 64
                AND request_hash = lower(request_hash)
                AND request_hash NOT GLOB '*[^0-9a-f]*'),
            state TEXT NOT NULL CHECK (
                state IN ('PENDING', 'COMMITTED', 'CONFLICT', 'REJECTED', 'APPLIED')),
            result_commit TEXT NULL CHECK (
                result_commit IS NULL OR (
                    length(result_commit) IN (40, 64)
                    AND result_commit = lower(result_commit)
                    AND result_commit NOT GLOB '*[^0-9a-f]*')),
            candidate_content_hash TEXT NULL CHECK (
                candidate_content_hash IS NULL OR (
                    length(candidate_content_hash) = 64
                    AND candidate_content_hash = lower(candidate_content_hash)
                    AND candidate_content_hash NOT GLOB '*[^0-9a-f]*')),
            failure_code TEXT NOT NULL DEFAULT '' CHECK (length(failure_code) <= 128),
            failure_summary TEXT NOT NULL DEFAULT '' CHECK (length(failure_summary) <= 512),
            revision_set_id TEXT NULL
                REFERENCES configuration_revision_sets(revision_set_id),
            created_unix_ms INTEGER NOT NULL,
            updated_unix_ms INTEGER NOT NULL,
            UNIQUE (actor_type, actor_id, operation_kind, request_id),
            CHECK (created_unix_ms <= updated_unix_ms),
            CHECK (
                (state = 'PENDING' AND result_commit IS NULL
                    AND candidate_content_hash IS NULL
                    AND failure_code = '' AND revision_set_id IS NULL)
                OR (state = 'COMMITTED' AND result_commit IS NOT NULL
                    AND candidate_content_hash IS NOT NULL
                    AND failure_code = '' AND revision_set_id IS NULL)
                OR (state = 'CONFLICT' AND result_commit IS NULL
                    AND candidate_content_hash IS NOT NULL
                    AND failure_code <> '' AND revision_set_id IS NULL)
                OR (state = 'REJECTED' AND result_commit IS NULL
                    AND candidate_content_hash IS NULL
                    AND failure_code <> '' AND revision_set_id IS NULL)
                OR (state = 'APPLIED' AND result_commit IS NOT NULL
                    AND candidate_content_hash IS NOT NULL
                    AND failure_code = '' AND revision_set_id IS NOT NULL))
        );

        CREATE TABLE agent_desired_configuration (
            agent_id TEXT PRIMARY KEY REFERENCES agents(agent_id) ON DELETE CASCADE,
            enabled INTEGER NOT NULL CHECK (enabled IN (0, 1)),
            source_repository_id TEXT NOT NULL CHECK (
                length(source_repository_id) BETWEEN 1 AND 128),
            source_commit TEXT NOT NULL CHECK (
                length(source_commit) IN (40, 64)
                AND source_commit = lower(source_commit)
                AND source_commit NOT GLOB '*[^0-9a-f]*'),
            content_hash TEXT NOT NULL CHECK (
                length(content_hash) = 64
                AND content_hash = lower(content_hash)
                AND content_hash NOT GLOB '*[^0-9a-f]*'),
            source_revision_set_id TEXT NOT NULL
                REFERENCES configuration_revision_sets(revision_set_id),
            applied_unix_ms INTEGER NOT NULL
        );

        CREATE UNIQUE INDEX configuration_revision_sets_one_active
            ON configuration_revision_sets(materialization_scope)
            WHERE state = 'ACTIVE';

        CREATE INDEX configuration_revision_sets_by_scope
            ON configuration_revision_sets(
                materialization_scope, validated_unix_ms DESC, revision_set_id DESC);

        CREATE INDEX configuration_revision_members_by_commit
            ON configuration_revision_members(repository_id, commit_sha, revision_set_id);

        CREATE UNIQUE INDEX configuration_revision_members_one_control
            ON configuration_revision_members(revision_set_id)
            WHERE repository_role = 'CONTROL';

        CREATE INDEX configuration_mutation_operations_by_state
            ON configuration_mutation_operations(state, updated_unix_ms, operation_id);

        CREATE INDEX agent_desired_configuration_by_source
            ON agent_desired_configuration(source_repository_id, source_commit, agent_id);
        """;

    private const string ConfigurationMutationEvidenceSchemaSql = """
        CREATE TABLE configuration_mutation_targets (
            operation_id TEXT NOT NULL
                REFERENCES configuration_mutation_operations(operation_id) ON DELETE CASCADE,
            ordinal INTEGER NOT NULL CHECK (ordinal BETWEEN 0 AND 31),
            target_type TEXT NOT NULL CHECK (length(target_type) BETWEEN 1 AND 64),
            target_id TEXT NOT NULL CHECK (length(target_id) BETWEEN 1 AND 256),
            path TEXT NOT NULL CHECK (length(path) BETWEEN 1 AND 256),
            PRIMARY KEY (operation_id, ordinal)
        );

        CREATE TABLE configuration_mutation_conflicts (
            operation_id TEXT PRIMARY KEY
                REFERENCES configuration_mutation_operations(operation_id) ON DELETE CASCADE,
            current_repository_id TEXT NOT NULL CHECK (
                length(current_repository_id) BETWEEN 1 AND 128),
            current_commit TEXT NOT NULL CHECK (
                length(current_commit) IN (40, 64)
                AND current_commit = lower(current_commit)
                AND current_commit NOT GLOB '*[^0-9a-f]*'),
            diff_json TEXT NOT NULL CHECK (length(diff_json) BETWEEN 2 AND 32768)
        );

        CREATE TABLE configuration_repository_attempt_failures (
            attempt_id TEXT PRIMARY KEY CHECK (length(attempt_id) BETWEEN 1 AND 128),
            operation_id TEXT NOT NULL
                REFERENCES configuration_mutation_operations(operation_id) ON DELETE CASCADE,
            failure_code TEXT NOT NULL CHECK (length(failure_code) BETWEEN 1 AND 128),
            failure_summary TEXT NOT NULL CHECK (length(failure_summary) BETWEEN 1 AND 512),
            attempted_unix_ms INTEGER NOT NULL
        );

        CREATE INDEX configuration_repository_attempt_failures_by_operation
            ON configuration_repository_attempt_failures(
                operation_id, attempted_unix_ms, attempt_id);
        """;

    private const string MatrixBuildIdempotencyV7DefinitionSql = """
        CREATE TABLE matrix_build_idempotency (
            actor_type TEXT NOT NULL CHECK (
                length(actor_type) BETWEEN 1 AND 64
                AND actor_type NOT GLOB '*[' || char(0, 10, 13) || ']*'),
            actor_id TEXT NOT NULL CHECK (length(actor_id) BETWEEN 1 AND 256),
            operation_kind TEXT NOT NULL CHECK (length(operation_kind) BETWEEN 1 AND 256),
            request_id TEXT NOT NULL CHECK (length(request_id) BETWEEN 1 AND 256),
            request_hash TEXT NOT NULL CHECK (
                (operation_kind = 'legacy-control-plane'
                    AND length(request_hash) BETWEEN 1 AND 256
                    AND instr(request_hash, char(0)) = 0
                    AND instr(request_hash, char(10)) = 0
                    AND instr(request_hash, char(13)) = 0)
                OR (length(request_hash) = 64
                    AND request_hash = lower(request_hash)
                    AND request_hash NOT GLOB '*[^0-9a-f]*')),
            matrix_build_id TEXT NOT NULL
                REFERENCES matrix_builds(matrix_build_id) ON DELETE CASCADE,
            response_status INTEGER NULL CHECK (
                response_status IS NULL OR response_status BETWEEN 100 AND 599),
            response_json TEXT NULL CHECK (
                response_json IS NULL OR length(response_json) BETWEEN 2 AND 1048576),
            response_etag TEXT NULL CHECK (
                response_etag IS NULL OR length(response_etag) BETWEEN 1 AND 256),
            created_unix_ms INTEGER NOT NULL CHECK (created_unix_ms >= 0),
            PRIMARY KEY (actor_type, actor_id, operation_kind, request_id),
            UNIQUE (matrix_build_id),
            CHECK (
                (response_status IS NULL AND response_json IS NULL AND response_etag IS NULL)
                OR (response_status IS NOT NULL AND response_json IS NOT NULL
                    AND response_etag IS NOT NULL))
        ) STRICT
        """;

    private const string BlobAccessAndBuildEventsSchemaSql = """
        CREATE TABLE matrix_build_idempotency_v7 (
            actor_type TEXT NOT NULL CHECK (
                length(actor_type) BETWEEN 1 AND 64
                AND actor_type NOT GLOB '*[' || char(0, 10, 13) || ']*'),
            actor_id TEXT NOT NULL CHECK (length(actor_id) BETWEEN 1 AND 256),
            operation_kind TEXT NOT NULL CHECK (length(operation_kind) BETWEEN 1 AND 256),
            request_id TEXT NOT NULL CHECK (length(request_id) BETWEEN 1 AND 256),
            request_hash TEXT NOT NULL CHECK (
                (operation_kind = 'legacy-control-plane'
                    AND length(request_hash) BETWEEN 1 AND 256
                    AND instr(request_hash, char(0)) = 0
                    AND instr(request_hash, char(10)) = 0
                    AND instr(request_hash, char(13)) = 0)
                OR (length(request_hash) = 64
                    AND request_hash = lower(request_hash)
                    AND request_hash NOT GLOB '*[^0-9a-f]*')),
            matrix_build_id TEXT NOT NULL
                REFERENCES matrix_builds(matrix_build_id) ON DELETE CASCADE,
            response_status INTEGER NULL CHECK (
                response_status IS NULL OR response_status BETWEEN 100 AND 599),
            response_json TEXT NULL CHECK (
                response_json IS NULL OR length(response_json) BETWEEN 2 AND 1048576),
            response_etag TEXT NULL CHECK (
                response_etag IS NULL OR length(response_etag) BETWEEN 1 AND 256),
            created_unix_ms INTEGER NOT NULL CHECK (created_unix_ms >= 0),
            PRIMARY KEY (actor_type, actor_id, operation_kind, request_id),
            UNIQUE (matrix_build_id),
            CHECK (
                (response_status IS NULL AND response_json IS NULL AND response_etag IS NULL)
                OR (response_status IS NOT NULL AND response_json IS NOT NULL
                    AND response_etag IS NOT NULL))
        ) STRICT;

        INSERT INTO matrix_build_idempotency_v7(
            actor_type, actor_id, operation_kind, request_id, request_hash,
            matrix_build_id, response_status, response_json, response_etag, created_unix_ms)
        SELECT
            actor_type, actor_id, 'legacy-control-plane', idempotency.request_id,
            matrix.request_hash, idempotency.matrix_build_id,
            NULL, NULL, NULL, matrix.created_unix_ms
        FROM matrix_build_idempotency idempotency
        JOIN matrix_builds matrix
            ON matrix.matrix_build_id = idempotency.matrix_build_id;

        DROP TABLE matrix_build_idempotency;
        ALTER TABLE matrix_build_idempotency_v7 RENAME TO matrix_build_idempotency;

        CREATE TABLE blob_upload_plans (
            staging_id TEXT PRIMARY KEY CHECK (length(staging_id) BETWEEN 1 AND 64),
            actor_type TEXT NOT NULL CHECK (length(actor_type) BETWEEN 1 AND 32),
            actor_id TEXT NOT NULL CHECK (length(actor_id) BETWEEN 1 AND 256),
            project_id TEXT NOT NULL CHECK (length(project_id) BETWEEN 1 AND 256),
            operation_kind TEXT NOT NULL CHECK (length(operation_kind) BETWEEN 1 AND 128),
            request_id TEXT NOT NULL CHECK (length(request_id) BETWEEN 1 AND 256),
            request_hash TEXT NOT NULL CHECK (
                length(request_hash) = 64 AND request_hash NOT GLOB '*[^0-9a-f]*'),
            created_unix_ms INTEGER NOT NULL,
            expires_unix_ms INTEGER NOT NULL CHECK (expires_unix_ms > created_unix_ms),
            UNIQUE (actor_type, actor_id, operation_kind, request_id)
        );

        CREATE INDEX blob_upload_plans_due
            ON blob_upload_plans(expires_unix_ms, staging_id);

        CREATE TABLE blob_upload_plan_items (
            staging_id TEXT NOT NULL
                REFERENCES blob_upload_plans(staging_id) ON DELETE RESTRICT,
            sha256 TEXT NOT NULL CHECK (
                length(sha256) = 64 AND sha256 NOT GLOB '*[^0-9a-f]*'),
            declared_size INTEGER NOT NULL CHECK (
                declared_size BETWEEN 0 AND 2147483648),
            PRIMARY KEY (staging_id, sha256),
            UNIQUE (staging_id, sha256, declared_size)
        );

        CREATE INDEX blob_upload_plan_items_by_hash
            ON blob_upload_plan_items(sha256, staging_id);

        CREATE TABLE blob_upload_receipts (
            staging_id TEXT NOT NULL,
            sha256 TEXT NOT NULL,
            declared_size INTEGER NOT NULL,
            received_unix_ms INTEGER NOT NULL,
            PRIMARY KEY (staging_id, sha256),
            UNIQUE (staging_id, sha256, declared_size),
            FOREIGN KEY (staging_id, sha256, declared_size)
                REFERENCES blob_upload_plan_items(staging_id, sha256, declared_size)
                ON DELETE RESTRICT
        );

        CREATE INDEX blob_upload_receipts_by_hash
            ON blob_upload_receipts(sha256, staging_id);

        CREATE TABLE blob_principal_project_grants (
            actor_type TEXT NOT NULL,
            actor_id TEXT NOT NULL,
            project_id TEXT NOT NULL,
            sha256 TEXT NOT NULL,
            declared_size INTEGER NOT NULL,
            source_staging_id TEXT NOT NULL,
            granted_unix_ms INTEGER NOT NULL,
            PRIMARY KEY (actor_type, actor_id, project_id, sha256),
            FOREIGN KEY (source_staging_id, sha256, declared_size)
                REFERENCES blob_upload_receipts(staging_id, sha256, declared_size)
                ON DELETE RESTRICT,
            CHECK (length(actor_type) BETWEEN 1 AND 32),
            CHECK (length(actor_id) BETWEEN 1 AND 256),
            CHECK (length(project_id) BETWEEN 1 AND 256),
            CHECK (length(sha256) = 64 AND sha256 NOT GLOB '*[^0-9a-f]*'),
            CHECK (declared_size BETWEEN 0 AND 2147483648)
        );

        CREATE INDEX blob_principal_project_grants_by_hash
            ON blob_principal_project_grants(sha256, project_id);

        CREATE TABLE blob_build_payload_sets (
            matrix_build_id TEXT PRIMARY KEY
                REFERENCES matrix_builds(matrix_build_id) ON DELETE RESTRICT,
            staging_id TEXT NOT NULL UNIQUE
                REFERENCES blob_upload_plans(staging_id) ON DELETE RESTRICT,
            actor_type TEXT NOT NULL,
            actor_id TEXT NOT NULL,
            project_id TEXT NOT NULL,
            operation_kind TEXT NOT NULL,
            request_id TEXT NOT NULL,
            attached_unix_ms INTEGER NOT NULL,
            CHECK (length(actor_type) BETWEEN 1 AND 32),
            CHECK (length(actor_id) BETWEEN 1 AND 256),
            CHECK (length(project_id) BETWEEN 1 AND 256),
            CHECK (length(operation_kind) BETWEEN 1 AND 128),
            CHECK (length(request_id) BETWEEN 1 AND 256),
            UNIQUE (actor_type, actor_id, operation_kind, request_id)
        );

        CREATE TABLE blob_build_payload_references (
            matrix_build_id TEXT NOT NULL
                REFERENCES blob_build_payload_sets(matrix_build_id) ON DELETE RESTRICT,
            sha256 TEXT NOT NULL,
            declared_size INTEGER NOT NULL,
            source_staging_id TEXT NOT NULL,
            PRIMARY KEY (matrix_build_id, sha256),
            FOREIGN KEY (source_staging_id, sha256, declared_size)
                REFERENCES blob_upload_receipts(staging_id, sha256, declared_size)
                ON DELETE RESTRICT,
            CHECK (length(sha256) = 64 AND sha256 NOT GLOB '*[^0-9a-f]*'),
            CHECK (declared_size BETWEEN 0 AND 2147483648)
        );

        CREATE INDEX blob_build_payload_references_by_hash
            ON blob_build_payload_references(sha256, matrix_build_id);

        CREATE TABLE blob_artifact_upload_staging (
            build_id TEXT NOT NULL REFERENCES builds(build_id) ON DELETE RESTRICT,
            sha256 TEXT NOT NULL CHECK (
                length(sha256) = 64 AND sha256 NOT GLOB '*[^0-9a-f]*'),
            declared_size INTEGER NOT NULL CHECK (
                declared_size BETWEEN 0 AND 2147483648),
            agent_id TEXT NOT NULL CHECK (length(agent_id) BETWEEN 1 AND 256),
            owner_session_id TEXT NOT NULL CHECK (
                length(owner_session_id) BETWEEN 1 AND 256),
            connection_generation INTEGER NOT NULL CHECK (connection_generation > 0),
            created_unix_ms INTEGER NOT NULL,
            expires_unix_ms INTEGER NOT NULL CHECK (expires_unix_ms > created_unix_ms),
            PRIMARY KEY (
                build_id, sha256, agent_id, owner_session_id, connection_generation),
            UNIQUE (
                build_id, sha256, declared_size, agent_id, owner_session_id,
                connection_generation)
        );

        CREATE INDEX blob_artifact_upload_staging_due
            ON blob_artifact_upload_staging(expires_unix_ms, build_id, sha256);

        CREATE TABLE blob_artifact_upload_receipts (
            build_id TEXT NOT NULL,
            sha256 TEXT NOT NULL,
            declared_size INTEGER NOT NULL,
            agent_id TEXT NOT NULL,
            owner_session_id TEXT NOT NULL,
            connection_generation INTEGER NOT NULL,
            received_unix_ms INTEGER NOT NULL,
            PRIMARY KEY (
                build_id, sha256, agent_id, owner_session_id, connection_generation),
            UNIQUE (
                build_id, sha256, declared_size, agent_id, owner_session_id,
                connection_generation),
            FOREIGN KEY (
                build_id, sha256, declared_size, agent_id, owner_session_id,
                connection_generation)
                REFERENCES blob_artifact_upload_staging(
                    build_id, sha256, declared_size, agent_id, owner_session_id,
                    connection_generation)
                ON DELETE RESTRICT
        );

        CREATE INDEX blob_artifact_upload_receipts_by_hash
            ON blob_artifact_upload_receipts(sha256, build_id);

        CREATE TABLE blob_build_artifact_sets (
            build_id TEXT PRIMARY KEY REFERENCES builds(build_id) ON DELETE RESTRICT,
            agent_id TEXT NOT NULL,
            owner_session_id TEXT NOT NULL,
            connection_generation INTEGER NOT NULL CHECK (connection_generation > 0),
            attached_unix_ms INTEGER NOT NULL,
            CHECK (length(agent_id) BETWEEN 1 AND 256),
            CHECK (length(owner_session_id) BETWEEN 1 AND 256)
        );

        CREATE TABLE blob_build_artifact_references (
            build_id TEXT NOT NULL
                REFERENCES blob_build_artifact_sets(build_id) ON DELETE RESTRICT,
            artifact_id TEXT NOT NULL CHECK (length(artifact_id) BETWEEN 1 AND 128),
            relative_path TEXT NOT NULL CHECK (length(relative_path) BETWEEN 1 AND 1024),
            sha256 TEXT NOT NULL CHECK (
                length(sha256) = 64 AND sha256 NOT GLOB '*[^0-9a-f]*'),
            declared_size INTEGER NOT NULL CHECK (
                declared_size BETWEEN 0 AND 2147483648),
            source_agent_id TEXT NOT NULL,
            source_session_id TEXT NOT NULL,
            source_connection_generation INTEGER NOT NULL,
            attached_unix_ms INTEGER NOT NULL,
            PRIMARY KEY (build_id, artifact_id),
            UNIQUE (build_id, relative_path),
            FOREIGN KEY (
                build_id, sha256, declared_size, source_agent_id, source_session_id,
                source_connection_generation)
                REFERENCES blob_artifact_upload_receipts(
                    build_id, sha256, declared_size, agent_id, owner_session_id,
                    connection_generation)
                ON DELETE RESTRICT
        );

        CREATE INDEX blob_build_artifact_references_by_hash
            ON blob_build_artifact_references(sha256, build_id, artifact_id);

        CREATE TABLE build_event_streams (
            matrix_build_id TEXT PRIMARY KEY
                REFERENCES matrix_builds(matrix_build_id) ON DELETE CASCADE,
            minimum_retained_sequence INTEGER NOT NULL CHECK (
                minimum_retained_sequence >= 1),
            latest_sequence INTEGER NOT NULL CHECK (latest_sequence >= 1),
            updated_unix_ms INTEGER NOT NULL CHECK (updated_unix_ms >= 0),
            CHECK (minimum_retained_sequence <= latest_sequence + 1)
        ) STRICT;

        CREATE TABLE build_events (
            sequence INTEGER PRIMARY KEY AUTOINCREMENT,
            event_id TEXT NOT NULL UNIQUE CHECK (
                length(event_id) = 34
                AND substr(event_id, 1, 5) = 'bevt_'
                AND substr(event_id, 22, 1) = '_'
                AND substr(event_id, 6, 16) NOT GLOB '*[^0-9a-f]*'
                AND substr(event_id, 23, 12) NOT GLOB '*[^0-9a-f]*'),
            matrix_build_id TEXT NOT NULL
                REFERENCES matrix_builds(matrix_build_id) ON DELETE CASCADE,
            event_type TEXT NOT NULL CHECK (length(event_type) BETWEEN 1 AND 128),
            occurred_unix_ms INTEGER NOT NULL CHECK (occurred_unix_ms >= 0),
            correlation_id TEXT NOT NULL CHECK (length(correlation_id) BETWEEN 1 AND 256),
            actor_type TEXT NOT NULL CHECK (length(actor_type) BETWEEN 1 AND 64),
            actor_id TEXT NOT NULL CHECK (length(actor_id) BETWEEN 1 AND 256),
            runtime_revision TEXT NOT NULL CHECK (
                length(runtime_revision) BETWEEN 9 AND 128
                AND runtime_revision GLOB 'runtime:[0-9]*'),
            resource_url TEXT NOT NULL CHECK (length(resource_url) BETWEEN 1 AND 512)
        ) STRICT;

        CREATE INDEX ix_build_events_matrix_sequence
            ON build_events(matrix_build_id, sequence);
        """;

    private const string TrxProjectionSchemaSql = """
        CREATE TABLE build_test_projection_states (
            build_id TEXT PRIMARY KEY
                REFERENCES builds(build_id) ON DELETE CASCADE,
            input_fingerprint TEXT NOT NULL CHECK (
                length(input_fingerprint) = 64
                AND input_fingerprint = lower(input_fingerprint)
                AND input_fingerprint NOT GLOB '*[^0-9a-f]*'),
            state TEXT NOT NULL CHECK (
                state IN ('PENDING', 'NO_REPORT', 'SUCCEEDED', 'PARTIAL', 'FAILED')),
            report_count INTEGER NOT NULL CHECK (report_count BETWEEN 0 AND 50000),
            successful_report_count INTEGER NOT NULL CHECK (
                successful_report_count BETWEEN 0 AND report_count),
            failed_report_count INTEGER NOT NULL CHECK (
                failed_report_count BETWEEN 0 AND report_count),
            started_unix_ms INTEGER NOT NULL CHECK (started_unix_ms >= 0),
            updated_unix_ms INTEGER NOT NULL CHECK (updated_unix_ms >= started_unix_ms),
            CHECK (successful_report_count + failed_report_count <= report_count),
            CHECK (
                state = 'PENDING'
                OR successful_report_count + failed_report_count = report_count)
        ) STRICT;

        CREATE TABLE trx_result_projections (
            projection_id TEXT PRIMARY KEY CHECK (length(projection_id) BETWEEN 1 AND 64),
            build_id TEXT NOT NULL
                REFERENCES builds(build_id) ON DELETE CASCADE,
            project_id TEXT NOT NULL CHECK (length(project_id) BETWEEN 1 AND 256),
            test_source_id TEXT NOT NULL CHECK (length(test_source_id) BETWEEN 1 AND 256),
            raw_artifact_id TEXT NOT NULL CHECK (length(raw_artifact_id) BETWEEN 1 AND 256),
            raw_artifact_path TEXT NOT NULL CHECK (length(raw_artifact_path) BETWEEN 1 AND 1024),
            raw_sha256 TEXT NOT NULL CHECK (
                length(raw_sha256) = 64
                AND raw_sha256 = lower(raw_sha256)
                AND raw_sha256 NOT GLOB '*[^0-9a-f]*'),
            raw_size INTEGER NOT NULL CHECK (raw_size BETWEEN 0 AND 2147483648),
            adapter_id TEXT NOT NULL CHECK (length(adapter_id) BETWEEN 1 AND 64),
            adapter_version TEXT NOT NULL CHECK (length(adapter_version) BETWEEN 1 AND 64),
            projection_schema_version INTEGER NOT NULL CHECK (projection_schema_version > 0),
            state TEXT NOT NULL CHECK (state IN ('SUCCEEDED', 'FAILED')),
            failure_code TEXT NULL CHECK (
                failure_code IS NULL OR length(failure_code) BETWEEN 1 AND 128),
            failure_summary TEXT NULL CHECK (
                failure_summary IS NULL OR length(failure_summary) BETWEEN 1 AND 512),
            run_json TEXT NULL CHECK (run_json IS NULL OR length(run_json) BETWEEN 2 AND 33554432),
            warnings_json TEXT NOT NULL CHECK (length(warnings_json) BETWEEN 2 AND 8388608),
            suppressed_warning_count INTEGER NOT NULL CHECK (
                suppressed_warning_count BETWEEN 0 AND 2147483647),
            projected_unix_ms INTEGER NOT NULL CHECK (projected_unix_ms >= 0),
            UNIQUE (build_id, raw_artifact_id),
            CHECK (
                (state = 'SUCCEEDED' AND failure_code IS NULL
                    AND failure_summary IS NULL AND run_json IS NOT NULL)
                OR (state = 'FAILED' AND failure_code IS NOT NULL
                    AND failure_summary IS NOT NULL AND run_json IS NULL))
        ) STRICT;

        CREATE INDEX trx_result_projections_by_build
            ON trx_result_projections(build_id, raw_artifact_id);

        CREATE TABLE trx_test_definitions (
            projection_id TEXT NOT NULL
                REFERENCES trx_result_projections(projection_id) ON DELETE CASCADE,
            test_id TEXT NOT NULL CHECK (length(test_id) BETWEEN 1 AND 128),
            identity_quality TEXT NOT NULL CHECK (
                identity_quality IN ('STABLE', 'FALLBACK')),
            identity_algorithm_version INTEGER NOT NULL CHECK (
                identity_algorithm_version > 0),
            definition_json TEXT NOT NULL CHECK (
                length(definition_json) BETWEEN 2 AND 1048576),
            PRIMARY KEY (projection_id, test_id)
        ) STRICT;

        CREATE INDEX trx_test_definitions_by_test
            ON trx_test_definitions(test_id, projection_id);

        CREATE TABLE trx_test_occurrences (
            projection_id TEXT NOT NULL
                REFERENCES trx_result_projections(projection_id) ON DELETE CASCADE,
            occurrence_id TEXT NOT NULL CHECK (length(occurrence_id) BETWEEN 1 AND 128),
            test_id TEXT NOT NULL CHECK (length(test_id) BETWEEN 1 AND 128),
            attempt_ordinal INTEGER NOT NULL CHECK (attempt_ordinal > 0),
            result_ordinal INTEGER NOT NULL CHECK (result_ordinal >= 0),
            normalized_outcome TEXT NOT NULL CHECK (
                normalized_outcome IN (
                    'passed', 'failed', 'skipped', 'ignored', 'inconclusive',
                    'aborted', 'not-run', 'unknown')),
            duration_ticks INTEGER NULL CHECK (duration_ticks IS NULL OR duration_ticks >= 0),
            occurrence_json TEXT NOT NULL CHECK (
                length(occurrence_json) BETWEEN 2 AND 1048576),
            PRIMARY KEY (projection_id, occurrence_id)
        ) STRICT;

        CREATE INDEX trx_test_occurrences_by_test_outcome
            ON trx_test_occurrences(test_id, normalized_outcome, projection_id);
        """;

    private const string TrxResultProjectionsByBuildIndexDefinitionSql =
        "CREATE INDEX trx_result_projections_by_build " +
        "ON trx_result_projections(build_id, raw_artifact_id)";

    private const string TrxTestDefinitionsByTestIndexDefinitionSql =
        "CREATE INDEX trx_test_definitions_by_test " +
        "ON trx_test_definitions(test_id, projection_id)";

    private const string TrxTestOccurrencesByTestOutcomeIndexDefinitionSql =
        "CREATE INDEX trx_test_occurrences_by_test_outcome " +
        "ON trx_test_occurrences(test_id, normalized_outcome, projection_id)";

    private const string AdministrationBootstrapSchemaSql = """
        CREATE TABLE administration_instances (
            instance_key INTEGER PRIMARY KEY CHECK (instance_key = 1),
            instance_id TEXT NOT NULL UNIQUE CHECK (length(instance_id) BETWEEN 1 AND 64),
            state TEXT NOT NULL CHECK (
                state IN (
                    'UNCLAIMED', 'SETUP_IN_PROGRESS', 'SETUP_WAITING_FOR_GIT',
                    'SETUP_ACTIVATING', 'ACTIVE', 'RECOVERY_AVAILABLE',
                    'RECOVERY_IN_PROGRESS')),
            state_version INTEGER NOT NULL CHECK (state_version > 0),
            setup_operation_id TEXT NULL CHECK (
                setup_operation_id IS NULL OR length(setup_operation_id) BETWEEN 1 AND 64),
            active_user_id TEXT NULL CHECK (
                active_user_id IS NULL OR length(active_user_id) BETWEEN 1 AND 128),
            active_repository_id TEXT NULL CHECK (
                active_repository_id IS NULL OR length(active_repository_id) BETWEEN 1 AND 64),
            active_commit TEXT NULL CHECK (
                active_commit IS NULL OR length(active_commit) BETWEEN 40 AND 64),
            created_unix_ms INTEGER NOT NULL CHECK (created_unix_ms >= 0),
            updated_unix_ms INTEGER NOT NULL CHECK (updated_unix_ms >= created_unix_ms),
            CHECK (
                (state = 'UNCLAIMED' AND setup_operation_id IS NULL
                    AND active_user_id IS NULL AND active_commit IS NULL)
                OR state <> 'UNCLAIMED'),
            CHECK (
                (state IN ('ACTIVE', 'RECOVERY_AVAILABLE', 'RECOVERY_IN_PROGRESS')
                    AND active_user_id IS NOT NULL
                    AND active_repository_id IS NOT NULL
                    AND active_commit IS NOT NULL)
                OR state NOT IN ('ACTIVE', 'RECOVERY_AVAILABLE', 'RECOVERY_IN_PROGRESS'))
        ) STRICT;

        CREATE TABLE administration_setup_operations (
            operation_id TEXT PRIMARY KEY CHECK (length(operation_id) BETWEEN 1 AND 64),
            state TEXT NOT NULL CHECK (
                state IN (
                    'IN_PROGRESS', 'WAITING_FOR_GIT', 'ACTIVATING', 'COMPLETED',
                    'ABANDONED', 'BLOCKED')),
            state_version INTEGER NOT NULL CHECK (state_version > 0),
            correlation_id TEXT NOT NULL CHECK (length(correlation_id) BETWEEN 8 AND 128),
            pending_user_id TEXT NULL CHECK (
                pending_user_id IS NULL OR length(pending_user_id) BETWEEN 1 AND 128),
            pending_login TEXT NULL CHECK (
                pending_login IS NULL OR length(pending_login) BETWEEN 1 AND 128),
            pending_display_name TEXT NULL CHECK (
                pending_display_name IS NULL OR length(pending_display_name) BETWEEN 1 AND 256),
            password_algorithm TEXT NULL CHECK (
                password_algorithm IS NULL OR length(password_algorithm) BETWEEN 1 AND 64),
            password_iterations INTEGER NULL CHECK (
                password_iterations IS NULL OR password_iterations BETWEEN 100000 AND 2000000),
            password_salt BLOB NULL CHECK (
                password_salt IS NULL OR length(password_salt) BETWEEN 16 AND 64),
            password_verifier BLOB NULL CHECK (
                password_verifier IS NULL OR length(password_verifier) BETWEEN 32 AND 64),
            repository_mode TEXT NULL CHECK (
                repository_mode IS NULL OR repository_mode IN ('managed-local')),
            repository_id TEXT NULL CHECK (
                repository_id IS NULL OR length(repository_id) BETWEEN 1 AND 64),
            expected_base_commit TEXT NULL CHECK (
                expected_base_commit IS NULL OR length(expected_base_commit) BETWEEN 40 AND 64),
            candidate_commit TEXT NULL CHECK (
                candidate_commit IS NULL OR length(candidate_commit) BETWEEN 40 AND 64),
            last_failure_code TEXT NOT NULL DEFAULT '' CHECK (length(last_failure_code) <= 128),
            created_unix_ms INTEGER NOT NULL CHECK (created_unix_ms >= 0),
            updated_unix_ms INTEGER NOT NULL CHECK (updated_unix_ms >= created_unix_ms),
            CHECK (
                (pending_user_id IS NULL AND pending_login IS NULL
                    AND pending_display_name IS NULL AND password_algorithm IS NULL
                    AND password_iterations IS NULL AND password_salt IS NULL
                    AND password_verifier IS NULL)
                OR (pending_user_id IS NOT NULL AND pending_login IS NOT NULL
                    AND pending_display_name IS NOT NULL AND password_algorithm IS NOT NULL
                    AND password_iterations IS NOT NULL AND password_salt IS NOT NULL
                    AND password_verifier IS NOT NULL)),
            CHECK (
                (repository_mode IS NULL AND repository_id IS NULL
                    AND expected_base_commit IS NULL)
                OR (repository_mode IS NOT NULL AND repository_id IS NOT NULL
                    AND expected_base_commit IS NOT NULL))
        ) STRICT;

        CREATE TABLE administration_token_generations (
            generation_id TEXT PRIMARY KEY CHECK (length(generation_id) BETWEEN 1 AND 64),
            purpose TEXT NOT NULL CHECK (purpose IN ('BOOTSTRAP', 'SETUP_RESUME', 'RECOVERY')),
            operation_id TEXT NULL
                REFERENCES administration_setup_operations(operation_id) ON DELETE RESTRICT,
            token_salt BLOB NOT NULL CHECK (length(token_salt) BETWEEN 16 AND 64),
            token_verifier BLOB NOT NULL CHECK (length(token_verifier) BETWEEN 32 AND 64),
            issued_unix_ms INTEGER NOT NULL CHECK (issued_unix_ms >= 0),
            expires_unix_ms INTEGER NOT NULL CHECK (expires_unix_ms > issued_unix_ms),
            consumed_unix_ms INTEGER NULL CHECK (
                consumed_unix_ms IS NULL OR consumed_unix_ms >= issued_unix_ms),
            revoked_unix_ms INTEGER NULL CHECK (
                revoked_unix_ms IS NULL OR revoked_unix_ms >= issued_unix_ms),
            revoke_reason TEXT NOT NULL DEFAULT '' CHECK (length(revoke_reason) <= 128),
            CHECK (
                (purpose = 'BOOTSTRAP' AND operation_id IS NULL)
                OR (purpose <> 'BOOTSTRAP' AND operation_id IS NOT NULL)),
            CHECK (consumed_unix_ms IS NULL OR revoked_unix_ms IS NULL)
        ) STRICT;

        CREATE UNIQUE INDEX administration_token_generations_one_current
            ON administration_token_generations(purpose)
            WHERE consumed_unix_ms IS NULL AND revoked_unix_ms IS NULL;

        CREATE INDEX administration_token_generations_due
            ON administration_token_generations(expires_unix_ms, generation_id)
            WHERE consumed_unix_ms IS NULL AND revoked_unix_ms IS NULL;

        CREATE TABLE administration_setup_sessions (
            session_id TEXT PRIMARY KEY CHECK (length(session_id) BETWEEN 1 AND 64),
            operation_id TEXT NOT NULL
                REFERENCES administration_setup_operations(operation_id) ON DELETE RESTRICT,
            generation_id TEXT NOT NULL
                REFERENCES administration_token_generations(generation_id) ON DELETE RESTRICT,
            token_salt BLOB NOT NULL CHECK (length(token_salt) BETWEEN 16 AND 64),
            token_verifier BLOB NOT NULL CHECK (length(token_verifier) BETWEEN 32 AND 64),
            issued_unix_ms INTEGER NOT NULL CHECK (issued_unix_ms >= 0),
            expires_unix_ms INTEGER NOT NULL CHECK (expires_unix_ms > issued_unix_ms),
            revoked_unix_ms INTEGER NULL CHECK (
                revoked_unix_ms IS NULL OR revoked_unix_ms >= issued_unix_ms),
            revoke_reason TEXT NOT NULL DEFAULT '' CHECK (length(revoke_reason) <= 128)
        ) STRICT;

        CREATE UNIQUE INDEX administration_setup_sessions_one_current
            ON administration_setup_sessions(operation_id)
            WHERE revoked_unix_ms IS NULL;

        CREATE INDEX administration_setup_sessions_due
            ON administration_setup_sessions(expires_unix_ms, session_id)
            WHERE revoked_unix_ms IS NULL;

        CREATE TABLE administration_setup_requests (
            operation_id TEXT NOT NULL
                REFERENCES administration_setup_operations(operation_id) ON DELETE RESTRICT,
            request_kind TEXT NOT NULL CHECK (length(request_kind) BETWEEN 1 AND 64),
            request_id TEXT NOT NULL CHECK (length(request_id) BETWEEN 1 AND 256),
            request_hash TEXT NOT NULL CHECK (
                length(request_hash) = 64
                AND request_hash = lower(request_hash)
                AND request_hash NOT GLOB '*[^0-9a-f]*'),
            response_status INTEGER NOT NULL CHECK (response_status BETWEEN 100 AND 599),
            response_json TEXT NOT NULL CHECK (length(response_json) BETWEEN 2 AND 1048576),
            created_unix_ms INTEGER NOT NULL CHECK (created_unix_ms >= 0),
            PRIMARY KEY (operation_id, request_kind, request_id)
        ) STRICT;
        """;

    private const string AdministrationTokenGenerationsOneCurrentIndexDefinitionSql =
        "CREATE UNIQUE INDEX administration_token_generations_one_current " +
        "ON administration_token_generations(purpose) " +
        "WHERE consumed_unix_ms IS NULL AND revoked_unix_ms IS NULL";

    private const string AdministrationTokenGenerationsDueIndexDefinitionSql =
        "CREATE INDEX administration_token_generations_due " +
        "ON administration_token_generations(expires_unix_ms, generation_id) " +
        "WHERE consumed_unix_ms IS NULL AND revoked_unix_ms IS NULL";

    private const string AdministrationSetupSessionsOneCurrentIndexDefinitionSql =
        "CREATE UNIQUE INDEX administration_setup_sessions_one_current " +
        "ON administration_setup_sessions(operation_id) " +
        "WHERE revoked_unix_ms IS NULL";

    private const string AdministrationSetupSessionsDueIndexDefinitionSql =
        "CREATE INDEX administration_setup_sessions_due " +
        "ON administration_setup_sessions(expires_unix_ms, session_id) " +
        "WHERE revoked_unix_ms IS NULL";

    private const string AuthorizationPolicySchemaSql = """
        CREATE TABLE authorization_desired_users (
            user_id TEXT PRIMARY KEY CHECK (length(user_id) BETWEEN 1 AND 128),
            login TEXT NOT NULL COLLATE NOCASE UNIQUE CHECK (length(login) BETWEEN 1 AND 128),
            display_name TEXT NOT NULL CHECK (length(display_name) BETWEEN 1 AND 256),
            desired_active INTEGER NOT NULL CHECK (desired_active IN (0, 1)),
            source_repository_id TEXT NOT NULL CHECK (
                length(source_repository_id) BETWEEN 1 AND 64),
            source_commit TEXT NOT NULL CHECK (length(source_commit) BETWEEN 40 AND 64),
            content_hash TEXT NOT NULL CHECK (
                length(content_hash) = 64
                AND content_hash = lower(content_hash)
                AND content_hash NOT GLOB '*[^0-9a-f]*'),
            source_revision_set_id TEXT NOT NULL
                REFERENCES configuration_revision_sets(revision_set_id) ON DELETE RESTRICT,
            applied_unix_ms INTEGER NOT NULL CHECK (applied_unix_ms >= 0)
        ) STRICT;

        CREATE TABLE authorization_role_bindings (
            binding_id TEXT PRIMARY KEY CHECK (length(binding_id) BETWEEN 1 AND 128),
            principal_type TEXT NOT NULL CHECK (principal_type IN ('user')),
            principal_id TEXT NOT NULL
                REFERENCES authorization_desired_users(user_id) ON DELETE RESTRICT,
            role_id TEXT NOT NULL CHECK (
                role_id IN (
                    'SYSTEM_ADMIN', 'PROJECT_ADMIN', 'PROJECT_DEVELOPER',
                    'PROJECT_VIEWER', 'AGENT_MANAGER')),
            scope_kind TEXT NOT NULL CHECK (
                scope_kind IN ('global', 'project', 'fleet', 'pool')),
            scope_id TEXT NOT NULL CHECK (length(scope_id) BETWEEN 1 AND 128),
            source_repository_id TEXT NOT NULL CHECK (
                length(source_repository_id) BETWEEN 1 AND 64),
            source_commit TEXT NOT NULL CHECK (length(source_commit) BETWEEN 40 AND 64),
            content_hash TEXT NOT NULL CHECK (
                length(content_hash) = 64
                AND content_hash = lower(content_hash)
                AND content_hash NOT GLOB '*[^0-9a-f]*'),
            source_revision_set_id TEXT NOT NULL
                REFERENCES configuration_revision_sets(revision_set_id) ON DELETE RESTRICT,
            applied_unix_ms INTEGER NOT NULL CHECK (applied_unix_ms >= 0),
            CHECK (
                (role_id = 'SYSTEM_ADMIN' AND scope_kind = 'global' AND scope_id = 'global')
                OR (role_id IN ('PROJECT_ADMIN', 'PROJECT_DEVELOPER', 'PROJECT_VIEWER')
                    AND scope_kind IN ('global', 'project'))
                OR (role_id = 'AGENT_MANAGER' AND scope_kind IN ('fleet', 'pool'))),
            UNIQUE(principal_type, principal_id, role_id, scope_kind, scope_id)
        ) STRICT;

        CREATE INDEX authorization_role_bindings_by_principal
            ON authorization_role_bindings(principal_type, principal_id, binding_id);

        CREATE INDEX authorization_role_bindings_by_scope
            ON authorization_role_bindings(scope_kind, scope_id, role_id, binding_id);
        """;

    private const string AuthorizationRoleBindingsByPrincipalIndexDefinitionSql =
        "CREATE INDEX authorization_role_bindings_by_principal " +
        "ON authorization_role_bindings(principal_type, principal_id, binding_id)";

    private const string AuthorizationRoleBindingsByScopeIndexDefinitionSql =
        "CREATE INDEX authorization_role_bindings_by_scope " +
        "ON authorization_role_bindings(scope_kind, scope_id, role_id, binding_id)";

    private const string UserCredentialSchemaSql = """
        CREATE TABLE authorization_user_credentials (
            user_id TEXT PRIMARY KEY CHECK (length(user_id) BETWEEN 1 AND 128),
            credential_state TEXT NOT NULL CHECK (
                credential_state IN ('ACTIVE', 'REVOKED')),
            password_algorithm TEXT NOT NULL CHECK (
                length(password_algorithm) BETWEEN 1 AND 64),
            password_iterations INTEGER NOT NULL CHECK (
                password_iterations BETWEEN 100000 AND 2000000),
            password_salt BLOB NOT NULL CHECK (length(password_salt) BETWEEN 16 AND 64),
            password_verifier BLOB NOT NULL CHECK (
                length(password_verifier) BETWEEN 32 AND 64),
            credential_generation INTEGER NOT NULL CHECK (credential_generation > 0),
            created_unix_ms INTEGER NOT NULL CHECK (created_unix_ms >= 0),
            updated_unix_ms INTEGER NOT NULL CHECK (updated_unix_ms >= created_unix_ms),
            last_authenticated_unix_ms INTEGER NULL CHECK (
                last_authenticated_unix_ms IS NULL
                OR last_authenticated_unix_ms >= created_unix_ms),
            revoked_unix_ms INTEGER NULL CHECK (
                revoked_unix_ms IS NULL OR revoked_unix_ms >= created_unix_ms),
            revoke_reason TEXT NOT NULL DEFAULT '' CHECK (length(revoke_reason) <= 128),
            CHECK (
                (credential_state = 'ACTIVE' AND revoked_unix_ms IS NULL)
                OR (credential_state = 'REVOKED' AND revoked_unix_ms IS NOT NULL))
        ) STRICT;
        """;

    private const string AgentPackageUpgradeSchemaSql = """
        CREATE TABLE agent_packages (
            package_id TEXT PRIMARY KEY CHECK (length(package_id) = 32),
            version TEXT NOT NULL CHECK (length(version) BETWEEN 1 AND 128),
            rid TEXT NOT NULL CHECK (
                rid IN ('win-x64', 'linux-x64', 'linux-arm64', 'osx-arm64')),
            sha256 TEXT NOT NULL CHECK (
                length(sha256) = 64
                AND sha256 = lower(sha256)
                AND sha256 NOT GLOB '*[^0-9a-f]*'),
            size INTEGER NOT NULL CHECK (size BETWEEN 1 AND 536870912),
            source TEXT NOT NULL CHECK (length(source) BETWEEN 1 AND 64),
            actor_type TEXT NOT NULL CHECK (length(actor_type) BETWEEN 1 AND 64),
            actor_id TEXT NOT NULL CHECK (length(actor_id) BETWEEN 1 AND 256),
            correlation_id TEXT NOT NULL CHECK (length(correlation_id) BETWEEN 1 AND 256),
            created_unix_ms INTEGER NOT NULL CHECK (created_unix_ms >= 0),
            UNIQUE(version, rid, sha256)
        ) STRICT;

        CREATE TABLE agent_package_publication_requests (
            actor_type TEXT NOT NULL CHECK (length(actor_type) BETWEEN 1 AND 64),
            actor_id TEXT NOT NULL CHECK (length(actor_id) BETWEEN 1 AND 256),
            request_id TEXT NOT NULL CHECK (length(request_id) BETWEEN 1 AND 256),
            request_hash TEXT NOT NULL CHECK (
                length(request_hash) = 64
                AND request_hash = lower(request_hash)
                AND request_hash NOT GLOB '*[^0-9a-f]*'),
            package_id TEXT NOT NULL REFERENCES agent_packages(package_id) ON DELETE RESTRICT,
            created_unix_ms INTEGER NOT NULL CHECK (created_unix_ms >= 0),
            PRIMARY KEY(actor_type, actor_id, request_id)
        ) STRICT;

        CREATE TABLE agent_upgrade_operations (
            operation_id TEXT PRIMARY KEY CHECK (length(operation_id) = 32),
            agent_id TEXT NOT NULL REFERENCES agents(agent_id) ON DELETE RESTRICT,
            package_id TEXT NOT NULL REFERENCES agent_packages(package_id) ON DELETE RESTRICT,
            state TEXT NOT NULL CHECK (
                state IN (
                    'DRAINING', 'HANDOFF_READY', 'AWAITING_HEALTH', 'COMMIT_PENDING', 'FINALIZING',
                    'ROLLBACK_REQUESTED',
                    'SUCCEEDED', 'ROLLED_BACK', 'FAILED', 'CANCELLED')),
            actor_type TEXT NOT NULL CHECK (length(actor_type) BETWEEN 1 AND 64),
            actor_id TEXT NOT NULL CHECK (length(actor_id) BETWEEN 1 AND 256),
            credential_kind TEXT NOT NULL CHECK (length(credential_kind) BETWEEN 1 AND 64),
            request_id TEXT NOT NULL CHECK (length(request_id) BETWEEN 1 AND 256),
            request_hash TEXT NOT NULL CHECK (
                length(request_hash) = 64
                AND request_hash = lower(request_hash)
                AND request_hash NOT GLOB '*[^0-9a-f]*'),
            correlation_id TEXT NOT NULL CHECK (length(correlation_id) BETWEEN 1 AND 256),
            reason TEXT NOT NULL CHECK (length(reason) BETWEEN 1 AND 512),
            maintenance_fence INTEGER NOT NULL CHECK (maintenance_fence > 0),
            prior_package_sha256 TEXT NULL CHECK (
                prior_package_sha256 IS NULL OR (
                    length(prior_package_sha256) = 64
                    AND prior_package_sha256 = lower(prior_package_sha256)
                    AND prior_package_sha256 NOT GLOB '*[^0-9a-f]*')),
            starting_connection_generation INTEGER NOT NULL CHECK (
                starting_connection_generation >= 0),
            observed_connection_generation INTEGER NULL CHECK (
                observed_connection_generation IS NULL
                OR observed_connection_generation > starting_connection_generation),
            restart_attempts INTEGER NOT NULL DEFAULT 0 CHECK (
                restart_attempts BETWEEN 0 AND 1000000),
            last_dispatch_connection_generation INTEGER NULL CHECK (
                last_dispatch_connection_generation IS NULL
                OR last_dispatch_connection_generation >= starting_connection_generation),
            next_restart_unix_ms INTEGER NULL CHECK (
                next_restart_unix_ms IS NULL OR next_restart_unix_ms >= created_unix_ms),
            cancellation_reason TEXT NOT NULL DEFAULT '' CHECK (
                length(cancellation_reason) <= 128),
            failure_code TEXT NOT NULL DEFAULT '' CHECK (length(failure_code) <= 128),
            result_package_sha256 TEXT NULL CHECK (
                result_package_sha256 IS NULL OR (
                    length(result_package_sha256) = 64
                    AND result_package_sha256 = lower(result_package_sha256)
                    AND result_package_sha256 NOT GLOB '*[^0-9a-f]*')),
            created_unix_ms INTEGER NOT NULL CHECK (created_unix_ms >= 0),
            updated_unix_ms INTEGER NOT NULL CHECK (updated_unix_ms >= created_unix_ms),
            deadline_unix_ms INTEGER NOT NULL CHECK (deadline_unix_ms > created_unix_ms),
            completed_unix_ms INTEGER NULL CHECK (
                completed_unix_ms IS NULL OR completed_unix_ms >= created_unix_ms),
            UNIQUE(actor_type, actor_id, request_id)
        ) STRICT;

        CREATE TABLE agent_upgrade_events (
            event_id INTEGER PRIMARY KEY,
            operation_id TEXT NOT NULL
                REFERENCES agent_upgrade_operations(operation_id) ON DELETE RESTRICT,
            phase TEXT NOT NULL CHECK (length(phase) BETWEEN 1 AND 64),
            code TEXT NOT NULL DEFAULT '' CHECK (length(code) <= 128),
            connection_generation INTEGER NULL CHECK (connection_generation IS NULL OR connection_generation > 0),
            package_sha256 TEXT NULL CHECK (
                package_sha256 IS NULL OR (
                    length(package_sha256) = 64
                    AND package_sha256 = lower(package_sha256)
                    AND package_sha256 NOT GLOB '*[^0-9a-f]*')),
            created_unix_ms INTEGER NOT NULL CHECK (created_unix_ms >= 0)
        ) STRICT;

        CREATE TABLE agent_maintenance_drains (
            agent_id TEXT PRIMARY KEY REFERENCES agents(agent_id) ON DELETE CASCADE,
            operation_id TEXT NOT NULL UNIQUE
                REFERENCES agent_upgrade_operations(operation_id) ON DELETE RESTRICT,
            fence INTEGER NOT NULL CHECK (fence > 0),
            reason TEXT NOT NULL CHECK (length(reason) BETWEEN 1 AND 128),
            acquired_unix_ms INTEGER NOT NULL CHECK (acquired_unix_ms >= 0)
        ) STRICT;

        CREATE INDEX agent_packages_by_rid
            ON agent_packages(rid, created_unix_ms DESC, package_id);

        CREATE INDEX agent_upgrade_operations_by_agent
            ON agent_upgrade_operations(agent_id, created_unix_ms DESC, operation_id);

        CREATE INDEX agent_upgrade_operations_due
            ON agent_upgrade_operations(deadline_unix_ms, operation_id)
            WHERE state IN (
                'DRAINING', 'HANDOFF_READY', 'AWAITING_HEALTH', 'COMMIT_PENDING', 'FINALIZING',
                'ROLLBACK_REQUESTED');

        CREATE UNIQUE INDEX agent_upgrade_operations_one_active
            ON agent_upgrade_operations(agent_id)
            WHERE state IN (
                'DRAINING', 'HANDOFF_READY', 'AWAITING_HEALTH', 'COMMIT_PENDING', 'FINALIZING',
                'ROLLBACK_REQUESTED');

        CREATE INDEX agent_upgrade_events_by_operation
            ON agent_upgrade_events(operation_id, event_id);
        """;

    private const string AgentPackagesByRidIndexDefinitionSql =
        "CREATE INDEX agent_packages_by_rid " +
        "ON agent_packages(rid, created_unix_ms DESC, package_id)";

    private const string AgentUpgradeOperationsByAgentIndexDefinitionSql =
        "CREATE INDEX agent_upgrade_operations_by_agent " +
        "ON agent_upgrade_operations(agent_id, created_unix_ms DESC, operation_id)";

    private const string AgentUpgradeOperationsDueIndexDefinitionSql =
        "CREATE INDEX agent_upgrade_operations_due " +
        "ON agent_upgrade_operations(deadline_unix_ms, operation_id) " +
        "WHERE state IN ( " +
        "'DRAINING', 'HANDOFF_READY', 'AWAITING_HEALTH', 'COMMIT_PENDING', 'FINALIZING', " +
        "'ROLLBACK_REQUESTED')";

    private const string AgentUpgradeOperationsOneActiveIndexDefinitionSql =
        "CREATE UNIQUE INDEX agent_upgrade_operations_one_active " +
        "ON agent_upgrade_operations(agent_id) " +
        "WHERE state IN ( " +
        "'DRAINING', 'HANDOFF_READY', 'AWAITING_HEALTH', 'COMMIT_PENDING', 'FINALIZING', " +
        "'ROLLBACK_REQUESTED')";

    private const string AgentUpgradeEventsByOperationIndexDefinitionSql =
        "CREATE INDEX agent_upgrade_events_by_operation " +
        "ON agent_upgrade_events(operation_id, event_id)";

    private const string AgentSchemaV4Sql = """
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
            custom_parameters_json TEXT NOT NULL DEFAULT '{}',
            agent_version TEXT NOT NULL DEFAULT '',
            os_family TEXT NOT NULL DEFAULT '',
            os_version TEXT NOT NULL DEFAULT '',
            architecture TEXT NOT NULL DEFAULT '',
            interactive INTEGER NOT NULL DEFAULT 0,
            credential_generation INTEGER NOT NULL DEFAULT 0 CHECK (credential_generation >= 0),
            connection_generation INTEGER NOT NULL DEFAULT 0 CHECK (connection_generation >= 0)
        );
        """;
}
