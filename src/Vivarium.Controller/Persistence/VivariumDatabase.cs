using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace Vivarium.Controller.Persistence;

/// <summary>
/// SQLite persistence with one serialized writer, as required by ARCHITECTURE §6. Reads use short
/// independent connections while all mutations pass through the writer channel.
/// </summary>
public sealed class VivariumDatabase : IAsyncDisposable
{
    public const int CurrentSchemaVersion = DatabaseMigrator.CurrentVersion;

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
        DatabaseMigrator.Migrate(connection);
    }
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
