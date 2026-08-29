using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Vivarium.Controller.Persistence;

namespace Vivarium.Controller.ResultAdapters.Trx;

public enum TrxBuildProjectionState
{
    Pending,
    NoReport,
    Succeeded,
    Partial,
    Failed,
}

public sealed record TrxProjectionInput(
    string BuildId,
    string ProjectId,
    string ArtifactId,
    string ArtifactPath,
    string Sha256,
    long Size);

public sealed record TrxProjectionAttempt(
    TrxProjectionInput Input,
    TrxResultProjection? Projection,
    string? FailureCode = null,
    string? FailureSummary = null)
{
    public bool Succeeded => Projection is not null;
}

public sealed record StoredTrxBuildProjection(
    string BuildId,
    string InputFingerprint,
    TrxBuildProjectionState State,
    int ReportCount,
    int SuccessfulReportCount,
    int FailedReportCount,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt);

public sealed record StoredTrxReportProjection(
    string ProjectionId,
    string BuildId,
    string ProjectId,
    string TestSourceId,
    string RawArtifactId,
    string RawArtifactPath,
    string RawSha256,
    long RawSize,
    string AdapterId,
    string AdapterVersion,
    int ProjectionSchemaVersion,
    bool Succeeded,
    string? FailureCode,
    string? FailureSummary,
    TrxTestRunProjection? Run,
    IReadOnlyList<TrxProjectionWarning> Warnings,
    int SuppressedWarningCount,
    DateTimeOffset ProjectedAt);

public sealed record StoredTrxTestProjection(
    string ProjectionId,
    TrxTestProjection Test);

public sealed record StoredTrxOccurrenceProjection(
    string ProjectionId,
    TrxTestOccurrenceProjection Occurrence);

public interface IBuildResultProjectionParticipant
{
    Task ProjectTerminalBuildAsync(
        string buildId,
        CancellationToken cancellationToken = default);
}

public sealed class TrxProjectionStore(VivariumDatabase database)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task<IReadOnlyList<TrxProjectionInput>> ListInputsAsync(string buildId) =>
        database.ReadAsync<IReadOnlyList<TrxProjectionInput>>(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT artifact_refs.build_id, COALESCE(matrix.project, 'unscoped'),
                       artifact_refs.artifact_id, artifact_refs.relative_path,
                       artifact_refs.sha256, artifact_refs.declared_size
                FROM blob_build_artifact_references artifact_refs
                JOIN builds ON builds.build_id = artifact_refs.build_id
                LEFT JOIN matrix_build_cells cells ON cells.build_id = artifact_refs.build_id
                LEFT JOIN matrix_builds matrix ON matrix.matrix_build_id = cells.matrix_build_id
                WHERE artifact_refs.build_id = $buildId AND builds.state = 'FINISHED'
                ORDER BY artifact_refs.artifact_id COLLATE BINARY;
                """;
            command.Parameters.AddWithValue("$buildId", buildId);
            using var reader = command.ExecuteReader();
            var result = new List<TrxProjectionInput>();
            while (reader.Read())
            {
                var path = reader.GetString(3);
                if (!path.EndsWith(".trx", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                result.Add(new TrxProjectionInput(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    path,
                    reader.GetString(4),
                    reader.GetInt64(5)));
            }

            return result;
        });

    public Task<IReadOnlyList<string>> ListPendingBuildIdsAsync() =>
        database.ReadAsync<IReadOnlyList<string>>(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT builds.build_id
                FROM builds
                LEFT JOIN build_test_projection_states states ON states.build_id = builds.build_id
                WHERE builds.state = 'FINISHED'
                  AND (states.build_id IS NULL OR states.state = 'PENDING')
                ORDER BY builds.created_unix_ms, builds.build_id COLLATE BINARY;
                """;
            using var reader = command.ExecuteReader();
            var result = new List<string>();
            while (reader.Read())
            {
                result.Add(reader.GetString(0));
            }

            return result;
        });

    public Task BeginAsync(
        string buildId,
        string inputFingerprint,
        int reportCount,
        DateTimeOffset now) => database.WriteAsync(connection =>
    {
        using var transaction = connection.BeginTransaction();
        using (var state = connection.CreateCommand())
        {
            state.Transaction = transaction;
            state.CommandText = """
                INSERT INTO build_test_projection_states(
                    build_id, input_fingerprint, state, report_count,
                    successful_report_count, failed_report_count,
                    started_unix_ms, updated_unix_ms)
                VALUES ($buildId, $fingerprint, 'PENDING', $reportCount, 0, 0, $now, $now)
                ON CONFLICT(build_id) DO UPDATE SET
                    input_fingerprint = excluded.input_fingerprint,
                    state = 'PENDING',
                    report_count = excluded.report_count,
                    successful_report_count = 0,
                    failed_report_count = 0,
                    started_unix_ms = excluded.started_unix_ms,
                    updated_unix_ms = excluded.updated_unix_ms;
                """;
            state.Parameters.AddWithValue("$buildId", buildId);
            state.Parameters.AddWithValue("$fingerprint", inputFingerprint);
            state.Parameters.AddWithValue("$reportCount", reportCount);
            state.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
            if (state.ExecuteNonQuery() != 1)
            {
                throw new InvalidOperationException($"could not begin TRX projection for build '{buildId}'");
            }
        }

        using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM trx_result_projections WHERE build_id = $buildId;";
            clear.Parameters.AddWithValue("$buildId", buildId);
            clear.ExecuteNonQuery();
        }

        transaction.Commit();
        return true;
    });

    public Task CompleteAsync(
        string buildId,
        string inputFingerprint,
        IReadOnlyList<TrxProjectionAttempt> attempts,
        DateTimeOffset now) => database.WriteAsync(connection =>
    {
        using var transaction = connection.BeginTransaction();
        foreach (var attempt in attempts)
        {
            InsertAttempt(connection, transaction, attempt, now);
        }

        var succeeded = attempts.Count(attempt => attempt.Succeeded);
        var failed = attempts.Count - succeeded;
        var state = attempts.Count switch
        {
            0 => "NO_REPORT",
            _ when succeeded == attempts.Count => "SUCCEEDED",
            _ when succeeded > 0 => "PARTIAL",
            _ => "FAILED",
        };
        using var finish = connection.CreateCommand();
        finish.Transaction = transaction;
        finish.CommandText = """
            UPDATE build_test_projection_states SET
                input_fingerprint = $fingerprint,
                state = $state,
                report_count = $reportCount,
                successful_report_count = $succeeded,
                failed_report_count = $failed,
                updated_unix_ms = $now
            WHERE build_id = $buildId AND state = 'PENDING';
            """;
        finish.Parameters.AddWithValue("$fingerprint", inputFingerprint);
        finish.Parameters.AddWithValue("$state", state);
        finish.Parameters.AddWithValue("$reportCount", attempts.Count);
        finish.Parameters.AddWithValue("$succeeded", succeeded);
        finish.Parameters.AddWithValue("$failed", failed);
        finish.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        finish.Parameters.AddWithValue("$buildId", buildId);
        if (finish.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException($"TRX projection state for build '{buildId}' is not pending");
        }

        transaction.Commit();
        return true;
    });

    public Task<StoredTrxBuildProjection?> GetBuildAsync(string buildId) =>
        database.ReadAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT build_id, input_fingerprint, state, report_count,
                       successful_report_count, failed_report_count,
                       started_unix_ms, updated_unix_ms
                FROM build_test_projection_states WHERE build_id = $buildId;
                """;
            command.Parameters.AddWithValue("$buildId", buildId);
            using var reader = command.ExecuteReader();
            return reader.Read()
                ? new StoredTrxBuildProjection(
                    reader.GetString(0),
                    reader.GetString(1),
                    ParseState(reader.GetString(2)),
                    reader.GetInt32(3),
                    reader.GetInt32(4),
                    reader.GetInt32(5),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(6)),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(7)))
                : null;
        });

    public Task<IReadOnlyList<StoredTrxReportProjection>> ListReportsAsync(string buildId) =>
        database.ReadAsync<IReadOnlyList<StoredTrxReportProjection>>(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT projection_id, build_id, project_id, test_source_id,
                       raw_artifact_id, raw_artifact_path, raw_sha256, raw_size,
                       adapter_id, adapter_version, projection_schema_version, state,
                       failure_code, failure_summary, run_json, warnings_json,
                       suppressed_warning_count, projected_unix_ms
                FROM trx_result_projections
                WHERE build_id = $buildId ORDER BY raw_artifact_id COLLATE BINARY;
                """;
            command.Parameters.AddWithValue("$buildId", buildId);
            using var reader = command.ExecuteReader();
            var result = new List<StoredTrxReportProjection>();
            while (reader.Read())
            {
                result.Add(new StoredTrxReportProjection(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetString(6),
                    reader.GetInt64(7),
                    reader.GetString(8),
                    reader.GetString(9),
                    reader.GetInt32(10),
                    reader.GetString(11) == "SUCCEEDED",
                    reader.IsDBNull(12) ? null : reader.GetString(12),
                    reader.IsDBNull(13) ? null : reader.GetString(13),
                    reader.IsDBNull(14)
                        ? null
                        : Deserialize<TrxTestRunProjection>(reader.GetString(14)),
                    Deserialize<TrxProjectionWarning[]>(reader.GetString(15)),
                    reader.GetInt32(16),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(17))));
            }

            return result;
        });

    public Task<IReadOnlyList<StoredTrxTestProjection>> ListTestsAsync(string buildId) =>
        database.ReadAsync<IReadOnlyList<StoredTrxTestProjection>>(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT tests.projection_id, tests.definition_json
                FROM trx_test_definitions tests
                JOIN trx_result_projections reports ON reports.projection_id = tests.projection_id
                WHERE reports.build_id = $buildId
                ORDER BY tests.test_id COLLATE BINARY;
                """;
            command.Parameters.AddWithValue("$buildId", buildId);
            using var reader = command.ExecuteReader();
            var result = new List<StoredTrxTestProjection>();
            while (reader.Read())
            {
                result.Add(new StoredTrxTestProjection(
                    reader.GetString(0),
                    Deserialize<TrxTestProjection>(reader.GetString(1))));
            }

            return result;
        });

    public Task<IReadOnlyList<StoredTrxOccurrenceProjection>> ListOccurrencesAsync(string buildId) =>
        database.ReadAsync<IReadOnlyList<StoredTrxOccurrenceProjection>>(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT occurrences.projection_id, occurrences.occurrence_json
                FROM trx_test_occurrences occurrences
                JOIN trx_result_projections reports
                    ON reports.projection_id = occurrences.projection_id
                WHERE reports.build_id = $buildId
                ORDER BY occurrences.result_ordinal, occurrences.occurrence_id COLLATE BINARY;
                """;
            command.Parameters.AddWithValue("$buildId", buildId);
            using var reader = command.ExecuteReader();
            var result = new List<StoredTrxOccurrenceProjection>();
            while (reader.Read())
            {
                result.Add(new StoredTrxOccurrenceProjection(
                    reader.GetString(0),
                    Deserialize<TrxTestOccurrenceProjection>(reader.GetString(1))));
            }

            return result;
        });

    public static string InputFingerprint(IReadOnlyList<TrxProjectionInput> inputs)
    {
        var canonical = new StringBuilder();
        foreach (var input in inputs.OrderBy(input => input.ArtifactId, StringComparer.Ordinal))
        {
            canonical.Append(input.ArtifactId).Append('\n')
                .Append(input.ArtifactPath).Append('\n')
                .Append(input.Sha256).Append(':').Append(input.Size).Append('\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    private static void InsertAttempt(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TrxProjectionAttempt attempt,
        DateTimeOffset now)
    {
        var projection = attempt.Projection;
        var projectionId = ProjectionId(attempt.Input);
        var testSourceId = projection?.Context.TestSourceId ?? TestSourceId(attempt.Input);
        using (var report = connection.CreateCommand())
        {
            report.Transaction = transaction;
            report.CommandText = """
                INSERT INTO trx_result_projections(
                    projection_id, build_id, project_id, test_source_id,
                    raw_artifact_id, raw_artifact_path, raw_sha256, raw_size,
                    adapter_id, adapter_version, projection_schema_version, state,
                    failure_code, failure_summary, run_json, warnings_json,
                    suppressed_warning_count, projected_unix_ms)
                VALUES (
                    $projectionId, $buildId, $projectId, $testSourceId,
                    $artifactId, $artifactPath, $sha256, $size,
                    'trx', '1.0.0', 1, $state,
                    $failureCode, $failureSummary, $runJson, $warningsJson,
                    $suppressedWarnings, $projectedAt);
                """;
            report.Parameters.AddWithValue("$projectionId", projectionId);
            report.Parameters.AddWithValue("$buildId", attempt.Input.BuildId);
            report.Parameters.AddWithValue("$projectId", attempt.Input.ProjectId);
            report.Parameters.AddWithValue("$testSourceId", testSourceId);
            report.Parameters.AddWithValue("$artifactId", attempt.Input.ArtifactId);
            report.Parameters.AddWithValue("$artifactPath", attempt.Input.ArtifactPath);
            report.Parameters.AddWithValue("$sha256", attempt.Input.Sha256);
            report.Parameters.AddWithValue("$size", attempt.Input.Size);
            report.Parameters.AddWithValue("$state", projection is null ? "FAILED" : "SUCCEEDED");
            report.Parameters.AddWithValue(
                "$failureCode",
                (object?)attempt.FailureCode ?? DBNull.Value);
            report.Parameters.AddWithValue(
                "$failureSummary",
                (object?)attempt.FailureSummary ?? DBNull.Value);
            report.Parameters.AddWithValue(
                "$runJson",
                projection is null ? DBNull.Value : Serialize(projection.Run));
            report.Parameters.AddWithValue(
                "$warningsJson",
                Serialize(projection?.Warnings ?? []));
            report.Parameters.AddWithValue(
                "$suppressedWarnings",
                projection?.SuppressedWarningCount ?? 0);
            report.Parameters.AddWithValue("$projectedAt", now.ToUnixTimeMilliseconds());
            report.ExecuteNonQuery();
        }

        if (projection is null)
        {
            return;
        }

        foreach (var test in projection.Tests)
        {
            using var definition = connection.CreateCommand();
            definition.Transaction = transaction;
            definition.CommandText = """
                INSERT INTO trx_test_definitions(
                    projection_id, test_id, identity_quality,
                    identity_algorithm_version, definition_json)
                VALUES ($projectionId, $testId, $quality, $algorithm, $json);
                """;
            definition.Parameters.AddWithValue("$projectionId", projectionId);
            definition.Parameters.AddWithValue("$testId", test.TestId);
            definition.Parameters.AddWithValue(
                "$quality",
                test.IdentityQuality == TrxTestIdentityQuality.Stable ? "STABLE" : "FALLBACK");
            definition.Parameters.AddWithValue("$algorithm", test.IdentityAlgorithmVersion);
            definition.Parameters.AddWithValue("$json", Serialize(test));
            definition.ExecuteNonQuery();
        }

        foreach (var occurrence in projection.Occurrences)
        {
            using var result = connection.CreateCommand();
            result.Transaction = transaction;
            result.CommandText = """
                INSERT INTO trx_test_occurrences(
                    projection_id, occurrence_id, test_id, attempt_ordinal,
                    result_ordinal, normalized_outcome, duration_ticks, occurrence_json)
                VALUES (
                    $projectionId, $occurrenceId, $testId, $attempt,
                    $ordinal, $outcome, $duration, $json);
                """;
            result.Parameters.AddWithValue("$projectionId", projectionId);
            result.Parameters.AddWithValue("$occurrenceId", occurrence.OccurrenceId);
            result.Parameters.AddWithValue("$testId", occurrence.TestId);
            result.Parameters.AddWithValue("$attempt", occurrence.AttemptOrdinal);
            result.Parameters.AddWithValue("$ordinal", occurrence.ResultOrdinal);
            result.Parameters.AddWithValue("$outcome", OutcomeValue(occurrence.Outcome));
            result.Parameters.AddWithValue(
                "$duration",
                occurrence.DurationTicks is null ? DBNull.Value : occurrence.DurationTicks.Value);
            result.Parameters.AddWithValue("$json", Serialize(occurrence));
            result.ExecuteNonQuery();
        }
    }

    internal static string TestSourceId(TrxProjectionInput input)
    {
        var value = "trx:" + input.ArtifactPath;
        return value.Length <= 256
            ? value
            : "trx:" + Convert.ToHexStringLower(
                SHA256.HashData(Encoding.UTF8.GetBytes(input.ArtifactPath)));
    }

    private static string ProjectionId(TrxProjectionInput input)
    {
        var value = $"{input.BuildId}\n{input.ArtifactId}\n{input.Sha256}";
        return "tproj_" + Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..32];
    }

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);

    private static T Deserialize<T>(string value) =>
        JsonSerializer.Deserialize<T>(value, JsonOptions)
        ?? throw new InvalidDataException("stored TRX projection JSON is empty");

    private static TrxBuildProjectionState ParseState(string value) => value switch
    {
        "PENDING" => TrxBuildProjectionState.Pending,
        "NO_REPORT" => TrxBuildProjectionState.NoReport,
        "SUCCEEDED" => TrxBuildProjectionState.Succeeded,
        "PARTIAL" => TrxBuildProjectionState.Partial,
        "FAILED" => TrxBuildProjectionState.Failed,
        _ => throw new InvalidDataException($"stored TRX build projection state '{value}' is invalid"),
    };

    private static string OutcomeValue(TrxNormalizedOutcome outcome) => outcome switch
    {
        TrxNormalizedOutcome.Passed => "passed",
        TrxNormalizedOutcome.Failed => "failed",
        TrxNormalizedOutcome.Skipped => "skipped",
        TrxNormalizedOutcome.Ignored => "ignored",
        TrxNormalizedOutcome.Inconclusive => "inconclusive",
        TrxNormalizedOutcome.Aborted => "aborted",
        TrxNormalizedOutcome.NotRun => "not-run",
        _ => "unknown",
    };
}
