using System.Security.Cryptography;
using Google.Protobuf;
using Vivarium.Contracts.V1;
using Vivarium.Controller.Auditing;
using Vivarium.Controller.Blobs;
using Vivarium.Controller.Blobs.Access;
using Vivarium.Controller.Persistence;
using Vivarium.Controller.Security;

namespace Vivarium.Tests;

[TestFixture]
[NonParallelizable]
public sealed class BlobAccessServiceTests
{
    private string rootDir = null!;
    private ManualTimeProvider time = null!;

    [SetUp]
    public void SetUp()
    {
        rootDir = Path.Combine(
            Path.GetTempPath(),
            "vivarium-blob-access-service-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootDir);
        Directory.CreateDirectory(Path.Combine(rootDir, "data"));
        time = new ManualTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1_800_000_000));
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
            // Preserve the original failure if SQLite or the filesystem releases a handle late.
        }
    }

    [Test]
    public async Task Upload_plans_are_idempotent_and_do_not_disclose_cross_owner_presence()
    {
        await using var database = NewDatabase();
        var fixture = CreateFixture(database);
        var bytes = "shared physical content"u8.ToArray();
        var descriptor = Descriptor(bytes);
        var owner = SubmitContext("service-one", "plan-owner");

        var first = await fixture.Service.CreateUploadPlanAsync(
            owner,
            "project-one",
            [descriptor]);
        var exact = await fixture.Service.CreateUploadPlanAsync(
            owner,
            "project-one",
            [descriptor]);
        await using (var body = new MemoryStream(bytes))
        {
            Assert.That(
                await fixture.Service.UploadStagedAsync(
                    owner,
                    first.Id,
                    descriptor.Sha256,
                    body),
                Is.EqualTo(BlobUploadOutcome.Uploaded));
        }

        var sameOwner = await fixture.Service.CreateUploadPlanAsync(
            SubmitContext("service-one", "plan-owner-reuse"),
            "project-one",
            [descriptor]);
        var otherPrincipal = await fixture.Service.CreateUploadPlanAsync(
            SubmitContext("service-two", "plan-other-principal"),
            "project-one",
            [descriptor]);
        var otherProject = await fixture.Service.CreateUploadPlanAsync(
            SubmitContext("service-one", "plan-other-project"),
            "project-two",
            [descriptor]);
        var changed = Assert.ThrowsAsync<BlobAccessException>(async () =>
            await fixture.Service.CreateUploadPlanAsync(
                owner,
                "project-one",
                [Descriptor("different"u8.ToArray())]));

        Assert.Multiple(() =>
        {
            Assert.That(first.Id, Is.EqualTo(exact.Id));
            Assert.That(exact.Replayed, Is.True);
            Assert.That(first.Items.Single().UploadRequired, Is.True);
            Assert.That(sameOwner.Items.Single().UploadRequired, Is.False);
            Assert.That(otherPrincipal.Items.Single().UploadRequired, Is.True);
            Assert.That(otherProject.Items.Single().UploadRequired, Is.True);
            Assert.That(changed!.Failure, Is.EqualTo(BlobAccessFailure.Conflict));
            Assert.That(changed.Code, Is.EqualTo("idempotency_key_reused"));
        });
    }

    [Test]
    public async Task Staged_upload_verifies_hash_size_and_changed_replay()
    {
        await using var database = NewDatabase();
        var fixture = CreateFixture(database);
        var bytes = "verified content"u8.ToArray();
        var descriptor = Descriptor(bytes);
        var context = SubmitContext("service-one", "verified-plan");
        var plan = await fixture.Service.CreateUploadPlanAsync(
            context,
            "project-one",
            [descriptor]);

        var wrong = Assert.ThrowsAsync<BlobAccessException>(async () =>
        {
            await using var body = new MemoryStream("wrong bytes"u8.ToArray());
            await fixture.Service.UploadStagedAsync(
                context,
                plan.Id,
                descriptor.Sha256,
                body);
        });
        BlobUploadOutcome uploaded;
        await using (var body = new MemoryStream(bytes))
        {
            uploaded = await fixture.Service.UploadStagedAsync(
                context,
                plan.Id,
                descriptor.Sha256,
                body);
        }

        BlobUploadOutcome replay;
        await using (var body = new MemoryStream(bytes))
        {
            replay = await fixture.Service.UploadStagedAsync(
                context,
                plan.Id,
                descriptor.Sha256,
                body);
        }

        var changedReplay = Assert.ThrowsAsync<BlobAccessException>(async () =>
        {
            await using var body = new MemoryStream(new byte[bytes.Length]);
            await fixture.Service.UploadStagedAsync(
                context,
                plan.Id,
                descriptor.Sha256,
                body);
        });
        var audits = await fixture.Audits.ListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(wrong!.Code, Is.EqualTo("blob_size_mismatch"));
            Assert.That(uploaded, Is.EqualTo(BlobUploadOutcome.Uploaded));
            Assert.That(replay, Is.EqualTo(BlobUploadOutcome.ExactReplay));
            Assert.That(changedReplay!.Failure, Is.EqualTo(BlobAccessFailure.Conflict));
            Assert.That(changedReplay.Code, Is.EqualTo("blob_upload_replay_conflict"));
            Assert.That(fixture.Blobs.Contains(descriptor.Sha256), Is.True);
            Assert.That(audits, Has.Some.Matches<StoredAuditEvent>(item =>
                item.Action == "blob-staging.upload" &&
                item.TargetId == plan.Id &&
                item.Outcome == AuditOutcome.NoChange &&
                item.ReasonCode == "exact_replay"));
        });
    }

    [Test]
    public async Task Payload_attachment_consumes_one_exact_uploaded_set_atomically()
    {
        await using var database = NewDatabase();
        var fixture = CreateFixture(database);
        var bytes = "payload"u8.ToArray();
        var descriptor = Descriptor(bytes);
        var context = SubmitContext("service-one", "payload-plan");
        var plan = await fixture.Service.CreateUploadPlanAsync(
            context,
            "project-one",
            [descriptor]);
        await using (var body = new MemoryStream(bytes))
        {
            _ = await fixture.Service.UploadStagedAsync(
                context,
                plan.Id,
                descriptor.Sha256,
                body);
        }

        var first = await database.WriteAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            InsertMatrix(connection, transaction, "matrix-one", "matrix-request-one");
            var request = new BlobBuildAttachmentRequest(
                context.Principal,
                "matrix-build.submit",
                "matrix-request-one",
                plan.Id,
                "project-one",
                "matrix-one",
                [descriptor.Sha256],
                time.GetUtcNow());
            var attached = fixture.Store.Attach(connection, transaction, request);
            var replay = fixture.Store.Attach(connection, transaction, request);
            transaction.Commit();
            return (attached, replay);
        });

        var reused = Assert.ThrowsAsync<BlobAccessException>(async () =>
            await database.WriteAsync(connection =>
            {
                using var transaction = connection.BeginTransaction();
                InsertMatrix(connection, transaction, "matrix-two", "matrix-request-two");
                _ = fixture.Store.Attach(
                    connection,
                    transaction,
                    new BlobBuildAttachmentRequest(
                        context.Principal,
                        "matrix-build.submit",
                        "matrix-request-two",
                        plan.Id,
                        "project-one",
                        "matrix-two",
                        [descriptor.Sha256],
                        time.GetUtcNow()));
                transaction.Commit();
                return true;
            }));

        Assert.Multiple(() =>
        {
            Assert.That(first.attached, Is.EqualTo(BlobBuildAttachmentOutcome.Attached));
            Assert.That(first.replay, Is.EqualTo(BlobBuildAttachmentOutcome.ExactReplay));
            Assert.That(reused!.Code, Is.EqualTo("blob_staging_already_consumed"));
        });
    }

    [Test]
    public async Task Artifact_receipts_are_fenced_across_build_session_and_reconnect_generation()
    {
        await using var database = NewDatabase();
        var fixture = CreateFixture(database);
        await InsertAgentAndBuildAsync(database, "build-one", "session-one", generation: 1);
        var bytes = "artifact"u8.ToArray();
        var descriptor = Descriptor(bytes);
        var agentOne = AgentContext("agent-one", "artifact-one");
        var firstUpload = new BlobArtifactUploadRequest(
            "agent-one",
            "session-one",
            "build-one",
            descriptor.Sha256,
            descriptor.Size,
            time.GetUtcNow());
        await using (var body = new MemoryStream(bytes))
        {
            _ = await fixture.Service.UploadArtifactAsync(agentOne, firstUpload, body);
        }

        await database.WriteAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE builds SET state = 'FINISHED' WHERE build_id = 'build-one';";
            command.ExecuteNonQuery();
            return true;
        });
        await InsertBuildAsync(database, "build-two", "session-one");
        var crossBuild = Assert.ThrowsAsync<BlobAccessException>(async () =>
            await database.WriteAsync(connection =>
            {
                using var transaction = connection.BeginTransaction();
                _ = fixture.Store.Attach(
                    connection,
                    transaction,
                    ArtifactRequest("build-two", "session-one", 1, descriptor));
                transaction.Commit();
                return true;
            }));

        await database.WriteAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            using var build = connection.CreateCommand();
            build.Transaction = transaction;
            build.CommandText = """
                UPDATE builds SET state = 'FINISHED' WHERE build_id = 'build-two';
                UPDATE builds SET state = 'RUNNING', owner_session_id = 'session-two'
                WHERE build_id = 'build-one';
                """;
            build.ExecuteNonQuery();
            using var agent = connection.CreateCommand();
            agent.Transaction = transaction;
            agent.CommandText = "UPDATE agents SET connection_generation = 2 WHERE agent_id = 'agent-one';";
            agent.ExecuteNonQuery();
            transaction.Commit();
            return true;
        });

        var oldSession = Assert.ThrowsAsync<BlobAccessException>(async () =>
            await database.WriteAsync(connection =>
            {
                using var transaction = connection.BeginTransaction();
                _ = fixture.Store.Attach(
                    connection,
                    transaction,
                    ArtifactRequest("build-one", "session-one", 1, descriptor));
                transaction.Commit();
                return true;
            }));
        var staleUpload = await fixture.Service.StageArtifactUploadAsync(
            agentOne,
            firstUpload with { Now = time.GetUtcNow() });

        var secondUpload = firstUpload with { SessionId = "session-two" };
        await using (var body = new MemoryStream(bytes))
        {
            _ = await fixture.Service.UploadArtifactAsync(agentOne, secondUpload, body);
        }

        var attached = await database.WriteAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            var result = fixture.Store.Attach(
                connection,
                transaction,
                ArtifactRequest("build-one", "session-two", 2, descriptor));
            transaction.Commit();
            return result;
        });
        var human = await fixture.Service.ResolveHumanArtifactAsync(
            new BlobHumanArtifactReadRequest(
                AdminContext("artifact-human"),
                "build-one",
                "artifact-0"));

        Assert.Multiple(() =>
        {
            Assert.That(oldSession!.Code, Is.EqualTo("blob_artifact_owner_conflict"));
            Assert.That(crossBuild!.Code, Is.EqualTo("blob_artifact_receipt_missing"));
            Assert.That(staleUpload, Is.Null);
            Assert.That(attached, Is.EqualTo(BlobArtifactAttachmentOutcome.Attached));
            Assert.That(human, Is.EqualTo(descriptor));
        });
    }

    private VivariumDatabase NewDatabase() =>
        new(Path.Combine(rootDir, "data"));

    private Fixture CreateFixture(VivariumDatabase database)
    {
        var audits = new AuditEventStore(database);
        var store = new BlobAccessStore(database);
        var blobs = new BlobStore(Path.Combine(rootDir, "blobs"));
        var service = new BlobAccessService(
            store,
            blobs,
            new ManagementCommandAuthorizer(new ManagementAuthorizer(), audits, time),
            audits,
            time);
        return new Fixture(store, blobs, service, audits);
    }

    private static BlobArtifactAttachmentRequest ArtifactRequest(
        string buildId,
        string sessionId,
        long generation,
        BlobDescriptor descriptor) =>
        new(
            buildId,
            "agent-one",
            sessionId,
            generation,
            [new BlobArtifactAttachment("artifact-0", "results/report.xml", descriptor.Sha256, descriptor.Size)],
            DateTimeOffset.FromUnixTimeSeconds(1_800_000_000));

    private static BlobDescriptor Descriptor(byte[] bytes) =>
        new(Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length);

    private static ManagementRequestContext SubmitContext(string actorId, string requestId) =>
        new(
            new ManagementPrincipal("service", actorId, "test", BearerScope.Submit),
            "blob-service-correlation",
            requestId,
            "blob-access-service-test");

    private static ManagementRequestContext AgentContext(string agentId, string correlationId) =>
        new(
            ManagementPrincipal.Agent(agentId),
            correlationId,
            RequestId: null,
            "blob-access-service-test");

    private static ManagementRequestContext AdminContext(string correlationId) =>
        new(
            ManagementPrincipal.LegacyAdmin,
            correlationId,
            RequestId: null,
            "blob-access-service-test");

    private static void InsertMatrix(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteTransaction transaction,
        string matrixBuildId,
        string requestId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO matrix_builds(
                matrix_build_id, request_id, request_hash, request_payload, project,
                configuration, definition_snapshot, definition_hash,
                created_unix_ms, updated_unix_ms)
            VALUES (
                $matrixBuildId, $requestId, $requestId, X'00', 'project-one',
                'configuration-one', X'00', $requestId, 1, 1);
            """;
        command.Parameters.AddWithValue("$matrixBuildId", matrixBuildId);
        command.Parameters.AddWithValue("$requestId", requestId);
        command.ExecuteNonQuery();
    }

    private static Task InsertAgentAndBuildAsync(
        VivariumDatabase database,
        string buildId,
        string sessionId,
        long generation) => database.WriteAsync(connection =>
    {
        using var transaction = connection.BeginTransaction();
        using (var agent = connection.CreateCommand())
        {
            agent.Transaction = transaction;
            agent.CommandText = """
                INSERT INTO agents(
                    agent_id, name, first_seen_unix_ms, last_seen_unix_ms,
                    connection_generation)
                VALUES ('agent-one', 'agent-one', 1, 1, $generation);
                """;
            agent.Parameters.AddWithValue("$generation", generation);
            agent.ExecuteNonQuery();
        }

        InsertBuild(connection, transaction, buildId, sessionId);
        transaction.Commit();
        return true;
    });

    private static Task InsertBuildAsync(
        VivariumDatabase database,
        string buildId,
        string sessionId) => database.WriteAsync(connection =>
    {
        using var transaction = connection.BeginTransaction();
        InsertBuild(connection, transaction, buildId, sessionId);
        transaction.Commit();
        return true;
    });

    private static void InsertBuild(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteTransaction transaction,
        string buildId,
        string sessionId)
    {
        var assignment = new BuildAssignment { BuildId = buildId };
        using var build = connection.CreateCommand();
        build.Transaction = transaction;
        build.CommandText = """
            INSERT INTO builds(
                build_id, agent_id, owner_session_id, state, assignment,
                created_unix_ms, updated_unix_ms)
            VALUES ($buildId, 'agent-one', $sessionId, 'RUNNING', $assignment, 1, 1);
            """;
        build.Parameters.AddWithValue("$buildId", buildId);
        build.Parameters.AddWithValue("$sessionId", sessionId);
        build.Parameters.Add("$assignment", Microsoft.Data.Sqlite.SqliteType.Blob).Value =
            assignment.ToByteArray();
        build.ExecuteNonQuery();
    }

    private sealed record Fixture(
        BlobAccessStore Store,
        BlobStore Blobs,
        BlobAccessService Service,
        AuditEventStore Audits);

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
