using Npgsql;

namespace Vivarium.DefinitionProof;

// P0 identity feasibility only. No queue, runtime state, HTTP facade or second build engine.
internal sealed record StudyBuildIdentity(long Id, string? NativeAlias, string Kind, long? Parent, int? Ordinal);
internal sealed record StudyAgentIdentity(int Id, string NativeAlias, int TypeId);

internal sealed class BuildIdentityStudy(string connectionString)
{
    private async Task<NpgsqlConnection> ConnectAsync()
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        return connection;
    }

    public async Task CreateSchemaAsync()
    {
        await using var connection = await ConnectAsync();
        await using var command = new NpgsqlCommand("""
            CREATE TABLE study_build_identity(
                id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY CHECK(id > 0),
                native_alias text UNIQUE CHECK(length(native_alias) BETWEEN 1 AND 128),
                kind text NOT NULL CHECK(kind IN ('normal','composite','native_matrix')),
                parent_id bigint, parent_kind text, ordinal integer,
                UNIQUE(id,kind), UNIQUE(parent_id,ordinal),
                FOREIGN KEY(parent_id,parent_kind) REFERENCES study_build_identity(id,kind),
                CHECK((parent_id IS NULL AND parent_kind IS NULL AND ordinal IS NULL)
                    OR (parent_id IS NOT NULL AND parent_kind IS NOT NULL AND parent_kind='native_matrix'
                        AND kind='normal' AND ordinal IS NOT NULL AND ordinal >= 0)));
            CREATE TABLE study_standalone_agent_type(
                id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY CHECK(id > 0),
                registration_alias text NOT NULL UNIQUE CHECK(length(registration_alias) BETWEEN 1 AND 128));
            CREATE TABLE study_agent_identity(
                id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY CHECK(id > 0),
                native_alias text NOT NULL UNIQUE CHECK(length(native_alias) BETWEEN 1 AND 128),
                type_id integer NOT NULL UNIQUE REFERENCES study_standalone_agent_type(id));
            """, connection);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<IReadOnlyList<StudyBuildIdentity>> RegisterMatrixAsync(string aggregate, string[] cells)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(aggregate);
        if (cells.Length is < 1 or > 64 || cells.Any(string.IsNullOrWhiteSpace) ||
            cells.Append(aggregate).Distinct(StringComparer.Ordinal).Count() != cells.Length + 1)
            throw new ArgumentException("Matrix aliases must be distinct and bounded.");
        await using var connection = await ConnectAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var insert = new NpgsqlCommand("""
            INSERT INTO study_build_identity(native_alias,kind) VALUES ($1,'native_matrix')
            ON CONFLICT(native_alias) DO NOTHING
            """, connection, transaction);
        insert.Parameters.AddWithValue(aggregate);
        var created = await insert.ExecuteNonQueryAsync() == 1;
        await using var find = new NpgsqlCommand("SELECT id,kind FROM study_build_identity WHERE native_alias=$1 FOR UPDATE", connection, transaction);
        find.Parameters.AddWithValue(aggregate);
        long parent;
        await using (var reader = await find.ExecuteReaderAsync())
        {
            if (!await reader.ReadAsync() || reader.GetString(1) != "native_matrix")
                throw new InvalidOperationException("An alias cannot change Build kind.");
            parent = reader.GetInt64(0);
        }
        if (created)
        {
            for (var ordinal = 0; ordinal < cells.Length; ordinal++)
            {
                await using var child = new NpgsqlCommand("""
                    INSERT INTO study_build_identity(native_alias,kind,parent_id,parent_kind,ordinal)
                    VALUES ($1,'normal',$2,'native_matrix',$3)
                    """, connection, transaction);
                child.Parameters.AddWithValue(cells[ordinal]);
                child.Parameters.AddWithValue(parent);
                child.Parameters.AddWithValue(ordinal);
                await child.ExecuteNonQueryAsync();
            }
        }
        await using var read = new NpgsqlCommand("""
            SELECT id,native_alias,kind,parent_id,ordinal FROM study_build_identity
            WHERE id=$1 OR parent_id=$1 ORDER BY parent_id NULLS FIRST,ordinal
            """, connection, transaction);
        read.Parameters.AddWithValue(parent);
        var rows = await ReadAsync(read);
        if (!rows.Skip(1).Select(row => row.NativeAlias).SequenceEqual(cells))
            throw new InvalidOperationException("An existing aggregate cannot acquire different cells.");
        await transaction.CommitAsync();
        return rows;
    }

    public async Task<StudyBuildIdentity> AllocateIndependentAsync(string kind)
    {
        if (kind is not ("normal" or "composite")) throw new ArgumentException("Expected an independent Build kind.");
        await using var connection = await ConnectAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO study_build_identity(kind) VALUES ($1)
            RETURNING id,native_alias,kind,parent_id,ordinal
            """, connection);
        command.Parameters.AddWithValue(kind);
        return (await ReadAsync(command)).Single();
    }

    public async Task<IReadOnlyList<StudyBuildIdentity>> ReadAllAsync()
    {
        await using var connection = await ConnectAsync();
        await using var command = new NpgsqlCommand("SELECT id,native_alias,kind,parent_id,ordinal FROM study_build_identity ORDER BY id", connection);
        return await ReadAsync(command);
    }

    public async Task<StudyAgentIdentity> RegisterStandaloneAgentAsync(string nativeAlias)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nativeAlias);
        await using var connection = await ConnectAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var type = new NpgsqlCommand("""
            INSERT INTO study_standalone_agent_type(registration_alias) VALUES ($1)
            ON CONFLICT(registration_alias) DO NOTHING
            """, connection, transaction);
        type.Parameters.AddWithValue(nativeAlias);
        await type.ExecuteNonQueryAsync();
        await using var agent = new NpgsqlCommand("""
            INSERT INTO study_agent_identity(native_alias,type_id)
            SELECT $1,id FROM study_standalone_agent_type WHERE registration_alias=$1
            ON CONFLICT(native_alias) DO NOTHING
            """, connection, transaction);
        agent.Parameters.AddWithValue(nativeAlias);
        await agent.ExecuteNonQueryAsync();
        await using var read = new NpgsqlCommand("SELECT id,native_alias,type_id FROM study_agent_identity WHERE native_alias=$1", connection, transaction);
        read.Parameters.AddWithValue(nativeAlias);
        StudyAgentIdentity identity;
        await using (var reader = await read.ExecuteReaderAsync())
        {
            if (!await reader.ReadAsync()) throw new InvalidDataException("Agent identity was not registered.");
            identity = new(reader.GetInt32(0), reader.GetString(1), reader.GetInt32(2));
        }
        await transaction.CommitAsync();
        return identity;
    }

    private static async Task<IReadOnlyList<StudyBuildIdentity>> ReadAsync(NpgsqlCommand command)
    {
        var rows = new List<StudyBuildIdentity>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) rows.Add(new(reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetInt64(3), reader.IsDBNull(4) ? null : reader.GetInt32(4)));
        return rows;
    }
}
