using Npgsql;

namespace Vivarium.DefinitionProof;

// Physical P0 feasibility study, not production Host/schema/authorization implementation.
internal sealed record StudyOperation(Guid Id, string Scope, long BaseVersion, string BaseControl,
    string BaseProduct, string TargetControl, string TargetProduct, string State);
internal sealed record ActiveStudyRevision(long Version, string Control, string SettingsRepository,
    string Settings, string PlanHash, string Plan);

internal sealed class RevisionSetStudy(string connectionString, StudyResources resources)
{
    private async Task<NpgsqlConnection> ConnectAsync(Guid? operation = null)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            ApplicationName = "definition-study-" + operation,
        };
        var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    public async Task CreateSchemaAsync()
    {
        await using var connection = await ConnectAsync();
        await using var command = new NpgsqlCommand("""
            CREATE TABLE study_scopes(
                scope text PRIMARY KEY, version bigint NOT NULL DEFAULT 0,
                control_commit text, settings_repository text, settings_commit text,
                plan_hash text, plan text);
            CREATE TABLE study_operations(
                id uuid PRIMARY KEY, scope text NOT NULL REFERENCES study_scopes(scope),
                base_version bigint NOT NULL, base_control text NOT NULL, base_product text NOT NULL,
                target_control text NOT NULL, target_product text NOT NULL,
                state text NOT NULL CHECK (state IN ('PENDING','VALIDATED','PRODUCT_REF','REFS','APPLIED','REJECTED','CONFLICT')));
            CREATE TABLE study_audit(
                operation_id uuid NOT NULL REFERENCES study_operations(id), event text NOT NULL,
                PRIMARY KEY(operation_id,event));
            CREATE TABLE study_history(
                scope text NOT NULL REFERENCES study_scopes(scope), version bigint NOT NULL,
                control_commit text NOT NULL, settings_repository text NOT NULL, settings_commit text NOT NULL,
                plan_hash text NOT NULL, plan text NOT NULL, PRIMARY KEY(scope,version));
            """, connection);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<StudyOperation> PrepareAsync(string scope, string targetControl, string targetProduct)
    {
        var control = await SourceCompiler.GitAsync(resources.Control, "rev-parse", "main");
        var product = await SourceCompiler.GitAsync(resources.Product, "rev-parse", "main");
        await using var connection = await ConnectAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ExecuteAsync(connection, transaction, "INSERT INTO study_scopes(scope) VALUES ($1) ON CONFLICT DO NOTHING", scope);
        await using var scopeCommand = new NpgsqlCommand("SELECT version FROM study_scopes WHERE scope=$1", connection, transaction);
        scopeCommand.Parameters.AddWithValue(scope);
        var version = (long)(await scopeCommand.ExecuteScalarAsync())!;
        var operation = new StudyOperation(Guid.NewGuid(), scope, version, control, product, targetControl, targetProduct, "PENDING");
        await ExecuteAsync(connection, transaction, "INSERT INTO study_operations VALUES ($1,$2,$3,$4,$5,$6,$7,'PENDING')",
            operation.Id, scope, version, control, product, targetControl, targetProduct);
        await ExecuteAsync(connection, transaction, "INSERT INTO study_audit VALUES ($1,'INTENT')", operation.Id);
        await transaction.CommitAsync();
        return operation;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, params object[] values)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var value in values) command.Parameters.AddWithValue(value);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<StudyOperation> ReadOperationAsync(Guid id)
    {
        await using var connection = await ConnectAsync();
        await using var command = new NpgsqlCommand("SELECT * FROM study_operations WHERE id=$1", connection);
        command.Parameters.AddWithValue(id);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) throw new InvalidOperationException("Proof intent is absent.");
        return new(reader.GetGuid(0), reader.GetString(1), reader.GetInt64(2), reader.GetString(3), reader.GetString(4),
            reader.GetString(5), reader.GetString(6), reader.GetString(7));
    }

    private async Task SetStateAsync(Guid id, string state)
    {
        await using var connection = await ConnectAsync(id);
        await using var command = new NpgsqlCommand("UPDATE study_operations SET state=$2 WHERE id=$1", connection);
        command.Parameters.AddWithValue(id); command.Parameters.AddWithValue(state);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<ActiveStudyRevision?> ReadActiveAsync(string scope)
    {
        await using var connection = await ConnectAsync();
        await using var command = new NpgsqlCommand("SELECT version,control_commit,settings_repository,settings_commit,plan_hash,plan FROM study_scopes WHERE scope=$1", connection);
        command.Parameters.AddWithValue(scope);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync() || reader.IsDBNull(1)) return null;
        return new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5));
    }

    public async Task<string> AdvanceAsync(Guid id)
    {
        var operation = await ReadOperationAsync(id);
        if (operation.State is "APPLIED" or "REJECTED" or "CONFLICT") return operation.State;
        CompiledRevisionPair compiled;
        try { compiled = await new SourceCompiler(resources).CompileAsync(operation.TargetControl, operation.TargetProduct); }
        catch (InvalidDataException) { await SetStateAsync(id, "REJECTED"); return "REJECTED"; }
        await SetStateAsync(id, "VALIDATED");

        // No SQL transaction spans compilation or either external Git operation.
        if (!await SourceCompiler.CompareExchangeAsync(resources.Product, operation.BaseProduct, operation.TargetProduct))
        { await SetStateAsync(id, "CONFLICT"); return "CONFLICT"; }
        await SetStateAsync(id, "PRODUCT_REF");
        if (!await SourceCompiler.CompareExchangeAsync(resources.Control, operation.BaseControl, operation.TargetControl))
        { await SetStateAsync(id, "CONFLICT"); return "CONFLICT"; }
        await SetStateAsync(id, "REFS");
        // A writer moving either head after CAS cannot authorize an unrelated projection.
        if (await SourceCompiler.GitAsync(resources.Control, "rev-parse", "main") != operation.TargetControl ||
            await SourceCompiler.GitAsync(resources.Product, "rev-parse", "main") != operation.TargetProduct)
        { await SetStateAsync(id, "CONFLICT"); return "CONFLICT"; }

        await using var connection = await ConnectAsync(id);
        await using var transaction = await connection.BeginTransactionAsync();
        await using var versionCommand = new NpgsqlCommand("SELECT version FROM study_scopes WHERE scope=$1 FOR UPDATE", connection, transaction);
        versionCommand.Parameters.AddWithValue(operation.Scope);
        var version = (long)(await versionCommand.ExecuteScalarAsync())!;
        if (version != operation.BaseVersion)
        {
            await transaction.RollbackAsync();
            await SetStateAsync(id, "CONFLICT");
            return "CONFLICT";
        }
        await ExecuteAsync(connection, transaction, """
            UPDATE study_scopes SET version=version+1,control_commit=$2,settings_repository=$3,
                settings_commit=$4,plan_hash=$5,plan=$6 WHERE scope=$1
            """, operation.Scope, compiled.ControlCommit, compiled.SettingsRepository, compiled.SettingsCommit, compiled.PlanHash, compiled.Plan);
        await ExecuteAsync(connection, transaction, "INSERT INTO study_history SELECT * FROM study_scopes WHERE scope=$1", operation.Scope);
        await ExecuteAsync(connection, transaction, "INSERT INTO study_audit VALUES ($1,'APPLIED')", id);
        await ExecuteAsync(connection, transaction, "UPDATE study_operations SET state='APPLIED' WHERE id=$1", id);
        await transaction.CommitAsync();
        return "APPLIED";
    }
}
