using System.Security.Cryptography;
using System.Text;
using Vivarium.Controller.Blobs;
using Vivarium.Controller.Persistence;
using Vivarium.Controller.ResultAdapters.Trx;

namespace Vivarium.Tests;

[TestFixture]
[NonParallelizable]
public sealed class TrxProjectionPersistenceTests
{
    private string rootDir = null!;

    [SetUp]
    public void SetUp()
    {
        rootDir = Path.Combine(
            Path.GetTempPath(),
            "vivarium-trx-persistence-tests",
            Guid.NewGuid().ToString("N"));
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
            // Preserve the original failure if a file handle is released late.
        }
    }

    [Test]
    public async Task Restart_catch_up_persists_occurrences_failures_and_raw_provenance()
    {
        var dataDir = Path.Combine(rootDir, "controller");
        var blobs = new BlobStore(Path.Combine(dataDir, "blobs"));
        var valid = Encoding.UTF8.GetBytes("""
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Results>
                <UnitTestResult executionId="execution-1" testId="definition-1"
                    testName="DurableTest" duration="00:00:00.1250000" outcome="Passed" />
              </Results>
              <TestDefinitions>
                <UnitTest id="definition-1" name="DurableTest" storage="suite.dll">
                  <Execution id="execution-1" />
                  <TestMethod className="Example.DurableTests" name="DurableTest"
                      codeBase="suite.dll" adapterTypeName="executor://nunit" />
                </UnitTest>
              </TestDefinitions>
              <ResultSummary outcome="Passed"><Counters total="1" passed="1" /></ResultSummary>
            </TestRun>
            """);
        var malformed = "<TestRun><Results>"u8.ToArray();
        var validArtifact = await PutAsync(blobs, valid);
        var malformedArtifact = await PutAsync(blobs, malformed);

        await using (var database = new VivariumDatabase(dataDir))
        {
            await InsertFinishedBuildAsync(
                database,
                "matrix-with-trx",
                "build-with-trx",
                [
                    ("0", "reports/results.trx", validArtifact.Sha256, validArtifact.Size),
                    ("1", "reports/broken.trx", malformedArtifact.Sha256, malformedArtifact.Size),
                ]);
            await InsertFinishedBuildAsync(
                database,
                "matrix-without-trx",
                "build-without-trx",
                []);

            Assert.That(await new TrxProjectionStore(database).GetBuildAsync("build-with-trx"), Is.Null,
                "a terminal result can be durable before its restart-safe projection runs");
        }

        await using (var restarted = new VivariumDatabase(dataDir))
        {
            var store = new TrxProjectionStore(restarted);
            var service = new TrxProjectionService(store, blobs, TimeProvider.System);
            await service.ReconcilePendingAsync();

            var state = await store.GetBuildAsync("build-with-trx");
            var reports = await store.ListReportsAsync("build-with-trx");
            var tests = await store.ListTestsAsync("build-with-trx");
            var occurrences = await store.ListOccurrencesAsync("build-with-trx");
            var noReport = await store.GetBuildAsync("build-without-trx");
            Assert.Multiple(() =>
            {
                Assert.That(state?.State, Is.EqualTo(TrxBuildProjectionState.Partial));
                Assert.That(state?.ReportCount, Is.EqualTo(2));
                Assert.That(state?.SuccessfulReportCount, Is.EqualTo(1));
                Assert.That(state?.FailedReportCount, Is.EqualTo(1));
                Assert.That(reports, Has.Count.EqualTo(2));
                Assert.That(reports.Single(report => report.Succeeded).RawArtifactPath,
                    Is.EqualTo("reports/results.trx"));
                Assert.That(reports.Single(report => !report.Succeeded).FailureCode,
                    Is.EqualTo("trx_malformed_xml"));
                Assert.That(tests.Single().Test.ClassName, Is.EqualTo("Example.DurableTests"));
                Assert.That(occurrences.Single().Occurrence.Outcome,
                    Is.EqualTo(TrxNormalizedOutcome.Passed));
                Assert.That(occurrences.Single().Occurrence.DurationTicks,
                    Is.EqualTo(TimeSpan.FromMilliseconds(125).Ticks));
                Assert.That(noReport?.State, Is.EqualTo(TrxBuildProjectionState.NoReport));
                Assert.That(blobs.Contains(validArtifact.Sha256), Is.True,
                    "the normalized projection never replaces raw report evidence");
                Assert.That(blobs.Contains(malformedArtifact.Sha256), Is.True);
            });
        }

        await using var reopened = new VivariumDatabase(dataDir);
        var durableStore = new TrxProjectionStore(reopened);
        var durableState = await durableStore.GetBuildAsync("build-with-trx");
        var durableOccurrences = await durableStore.ListOccurrencesAsync("build-with-trx");
        Assert.Multiple(() =>
        {
            Assert.That(durableState?.State, Is.EqualTo(TrxBuildProjectionState.Partial));
            Assert.That(durableOccurrences, Has.Count.EqualTo(1));
        });
    }

    private static async Task<(string Sha256, long Size)> PutAsync(BlobStore blobs, byte[] bytes)
    {
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
        await using var stream = new MemoryStream(bytes);
        Assert.That(await blobs.PutAsync(sha256, stream, CancellationToken.None), Is.True);
        return (sha256, bytes.LongLength);
    }

    private static Task InsertFinishedBuildAsync(
        VivariumDatabase database,
        string matrixBuildId,
        string buildId,
        IReadOnlyList<(string Id, string Path, string Sha256, long Size)> artifacts) =>
        database.WriteAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT OR IGNORE INTO agents(
                    agent_id, name, authorized, enabled,
                    first_seen_unix_ms, last_seen_unix_ms,
                    credential_generation, connection_generation)
                VALUES ('trx-agent', 'TRX Agent', 1, 1, 1, 1, 1, 1);

                INSERT INTO builds(
                    build_id, agent_id, state, assignment, result, owner_session_id,
                    created_unix_ms, updated_unix_ms)
                VALUES ($buildId, 'trx-agent', 'FINISHED', X'00', X'00', 'trx-session', 10, 20);

                INSERT INTO matrix_builds(
                    matrix_build_id, request_id, request_hash, request_payload,
                    project, configuration, definition_snapshot, definition_hash,
                    created_unix_ms, updated_unix_ms)
                VALUES (
                    $matrixBuildId, $matrixBuildId,
                    'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', X'00',
                    'project-one', 'configuration-one', X'00',
                    'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb',
                    10, 20);

                INSERT INTO matrix_build_cells(
                    matrix_build_id, cell_name, ordinal, build_id, agent_expression, rid)
                VALUES ($matrixBuildId, 'linux', 0, $buildId, 'os.family == linux', 'linux-x64');
                """;
            command.Parameters.AddWithValue("$buildId", buildId);
            command.Parameters.AddWithValue("$matrixBuildId", matrixBuildId);
            command.ExecuteNonQuery();

            if (artifacts.Count > 0)
            {
                using var set = connection.CreateCommand();
                set.Transaction = transaction;
                set.CommandText = """
                    INSERT INTO blob_build_artifact_sets(
                        build_id, agent_id, owner_session_id,
                        connection_generation, attached_unix_ms)
                    VALUES ($buildId, 'trx-agent', 'trx-session', 1, 20);
                    """;
                set.Parameters.AddWithValue("$buildId", buildId);
                set.ExecuteNonQuery();
            }

            foreach (var artifact in artifacts)
            {
                using var staging = connection.CreateCommand();
                staging.Transaction = transaction;
                staging.CommandText = """
                    INSERT INTO blob_artifact_upload_staging(
                        build_id, sha256, declared_size, agent_id, owner_session_id,
                        connection_generation, created_unix_ms, expires_unix_ms)
                    VALUES (
                        $buildId, $sha256, $size, 'trx-agent', 'trx-session', 1, 10, 30);
                    INSERT INTO blob_artifact_upload_receipts(
                        build_id, sha256, declared_size, agent_id, owner_session_id,
                        connection_generation, received_unix_ms)
                    VALUES (
                        $buildId, $sha256, $size, 'trx-agent', 'trx-session', 1, 15);
                    INSERT INTO blob_build_artifact_references(
                        build_id, artifact_id, relative_path, sha256, declared_size,
                        source_agent_id, source_session_id, source_connection_generation,
                        attached_unix_ms)
                    VALUES (
                        $buildId, $artifactId, $path, $sha256, $size,
                        'trx-agent', 'trx-session', 1, 20);
                    """;
                staging.Parameters.AddWithValue("$buildId", buildId);
                staging.Parameters.AddWithValue("$artifactId", artifact.Id);
                staging.Parameters.AddWithValue("$path", artifact.Path);
                staging.Parameters.AddWithValue("$sha256", artifact.Sha256);
                staging.Parameters.AddWithValue("$size", artifact.Size);
                staging.ExecuteNonQuery();
            }

            transaction.Commit();
            return true;
        });
}
