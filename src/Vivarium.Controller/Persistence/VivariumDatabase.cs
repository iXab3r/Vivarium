using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace Vivarium.Controller.Persistence;

/// <summary>
/// SQLite persistence with one serialized writer, as required by ARCHITECTURE §6. Reads use short
/// independent connections while all mutations pass through the writer channel.
/// </summary>
public sealed class VivariumDatabase : IAsyncDisposable
{
    private interface IWriteOperation
    {
        Task ExecuteAsync(SqliteConnection connection);
    }

    private sealed class WriteOperation<T> : IWriteOperation
    {
        private readonly Func<SqliteConnection, T> action;
        private readonly TaskCompletionSource<T> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public WriteOperation(Func<SqliteConnection, T> action) => this.action = action;

        public Task<T> Task => completion.Task;

        public Task ExecuteAsync(SqliteConnection connection)
        {
            try
            {
                completion.TrySetResult(action(connection));
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }

            return System.Threading.Tasks.Task.CompletedTask;
        }
    }

    private readonly string connectionString;
    private readonly Channel<IWriteOperation> writes = Channel.CreateUnbounded<IWriteOperation>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly Task writer;

    /// <summary>
    /// Best-effort wake-up for durable projections. Consumers must re-read SQLite and may use a
    /// periodic fallback; this event is never a source of truth.
    /// </summary>
    public event Action? Changed;

    public VivariumDatabase(string dataDir)
    {
        var path = Path.Combine(dataDir, "vivarium.db");
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();

        Initialize();
        writer = Task.Run(WriterLoopAsync);
    }

    public Task<T> ReadAsync<T>(Func<SqliteConnection, T> action)
    {
        using var connection = OpenConnection();
        return Task.FromResult(action(connection));
    }

    public Task<T> WriteAsync<T>(Func<SqliteConnection, T> action)
    {
        var operation = new WriteOperation<T>(action);
        if (!writes.Writer.TryWrite(operation))
        {
            throw new InvalidOperationException("the Vivarium database is shutting down");
        }

        return operation.Task;
    }

    public async ValueTask DisposeAsync()
    {
        writes.Writer.TryComplete();
        await writer;
    }

    private void Initialize()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA foreign_keys = ON;

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
        command.ExecuteNonQuery();

        EnsureColumn(
            connection,
            table: "agents",
            column: "custom_parameters_json",
            definition: "TEXT NOT NULL DEFAULT '{}'");

        EnsureBuildSchema(connection);
        EnsureColumn(
            connection,
            table: "builds",
            column: "owner_session_id",
            definition: "TEXT NULL");
        EnsureColumn(
            connection,
            table: "builds",
            column: "reconnect_deadline_unix_ms",
            definition: "INTEGER NULL");
        EnsureColumn(
            connection,
            table: "builds",
            column: "agent_name_snapshot",
            definition: "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(
            connection,
            table: "builds",
            column: "agent_parameters_snapshot_json",
            definition: "TEXT NOT NULL DEFAULT '{}'");
        EnsureColumn(
            connection,
            table: "builds",
            column: "agent_custom_parameters_snapshot_json",
            definition: "TEXT NOT NULL DEFAULT '{}'");

        using (var buildLeaseIndex = connection.CreateCommand())
        {
            buildLeaseIndex.CommandText = BuildLeaseIndexSql;
            buildLeaseIndex.ExecuteNonQuery();
        }

        using var queueCommand = connection.CreateCommand();
        queueCommand.CommandText = """
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

            CREATE INDEX IF NOT EXISTS build_queue_pending_fifo
                ON build_queue(state, queue_id);

            CREATE UNIQUE INDEX IF NOT EXISTS build_queue_one_claim_per_agent
                ON build_queue(claimed_agent_id)
                WHERE state = 'CLAIMED';
            """;
        queueCommand.ExecuteNonQuery();
        EnsureColumn(
            connection,
            table: "build_queue",
            column: "dispatched_session_id",
            definition: "TEXT NULL");
        EnsureColumn(
            connection,
            table: "build_queue",
            column: "queue_deadline_unix_ms",
            definition: "INTEGER NULL");

        using (var queueDeadlineIndex = connection.CreateCommand())
        {
            queueDeadlineIndex.CommandText = QueueDeadlineIndexSql;
            queueDeadlineIndex.ExecuteNonQuery();
        }

        using var matrixCommand = connection.CreateCommand();
        matrixCommand.CommandText = """
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
        matrixCommand.ExecuteNonQuery();
        EnsureColumn(
            connection,
            table: "matrix_build_cells",
            column: "rid",
            definition: "TEXT NOT NULL DEFAULT ''");
    }

    private static void EnsureColumn(
        SqliteConnection connection,
        string table,
        string column,
        string definition)
    {
        using var inspect = connection.CreateCommand();
        inspect.CommandText = $"PRAGMA table_info({table});";
        using var reader = inspect.ExecuteReader();
        while (reader.Read())
        {
            if (reader.GetString(1) == column)
            {
                return;
            }
        }

        reader.Close();
        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
        alter.ExecuteNonQuery();
    }

    private static void EnsureBuildSchema(SqliteConnection connection)
    {
        using var inspect = connection.CreateCommand();
        inspect.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'builds';";
        var existingSql = inspect.ExecuteScalar() as string;
        if (existingSql == null)
        {
            using var create = connection.CreateCommand();
            create.CommandText = BuildSchemaSql;
            create.ExecuteNonQuery();
            return;
        }

        if (existingSql.Contains("'QUEUED'", StringComparison.OrdinalIgnoreCase))
        {
            using var createIndex = connection.CreateCommand();
            createIndex.CommandText = BuildActiveIndexSql;
            createIndex.ExecuteNonQuery();
            return;
        }

        // Phase 1 originally created builds with a mandatory agent and no queued state. Rebuild the
        // table transactionally so an existing controller can adopt the durable queue without losing
        // running builds or terminal results.
        using var transaction = connection.BeginTransaction();
        using var migrate = connection.CreateCommand();
        migrate.Transaction = transaction;
        migrate.CommandText = $"""
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
            """;
        migrate.ExecuteNonQuery();
        transaction.Commit();
    }

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

        CREATE UNIQUE INDEX builds_one_active_per_agent
            ON builds(agent_id)
            WHERE state IN ('RUNNING', 'CANCEL_REQUESTED');
        """;

    private const string BuildActiveIndexSql = """
        CREATE UNIQUE INDEX IF NOT EXISTS builds_one_active_per_agent
            ON builds(agent_id)
            WHERE state IN ('RUNNING', 'CANCEL_REQUESTED');
        """;

    private const string BuildLeaseIndexSql = """
        CREATE INDEX IF NOT EXISTS builds_due_reconnect
            ON builds(reconnect_deadline_unix_ms)
            WHERE state IN ('RUNNING', 'CANCEL_REQUESTED')
                AND reconnect_deadline_unix_ms IS NOT NULL;
        """;

    private const string QueueDeadlineIndexSql = """
        CREATE INDEX IF NOT EXISTS build_queue_due
            ON build_queue(queue_deadline_unix_ms)
            WHERE state IN ('QUEUED', 'CLAIMED')
                AND queue_deadline_unix_ms IS NOT NULL;
        """;

    private async Task WriterLoopAsync()
    {
        using var connection = OpenConnection();
        await foreach (var operation in writes.Reader.ReadAllAsync())
        {
            await operation.ExecuteAsync(connection);
            try
            {
                Changed?.Invoke();
            }
            catch
            {
                // A live-view wake-up must never stop the durable writer.
            }
        }
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        command.ExecuteNonQuery();
        return connection;
    }
}
