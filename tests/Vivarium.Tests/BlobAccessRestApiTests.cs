using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using Vivarium.Contracts.V1;
using Vivarium.Controller;
using Vivarium.Controller.Auditing;
using Vivarium.Controller.Blobs.Access;
using Vivarium.Controller.Security;

namespace Vivarium.Tests;

[TestFixture]
[NonParallelizable]
public sealed class BlobAccessRestApiTests
{
    private string rootDir = null!;

    [SetUp]
    public void SetUp()
    {
        rootDir = Path.Combine(
            Path.GetTempPath(),
            "vivarium-blob-access-rest-tests",
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
            // Preserve the original failure if the host releases a file late.
        }
    }

    [Test]
    public async Task Upload_plan_requires_its_staging_header_and_raw_management_get_is_forbidden()
    {
        await using var controller = await StartControllerAsync();
        using var http = PinnedClient(controller);
        var bytes = "object-scoped payload"u8.ToArray();
        var descriptor = Descriptor(bytes);
        var plan = await CreatePlanAsync(http, controller.Tokens.SubmitToken, descriptor, "plan-one");

        using var missingStaging = AgentPut(
            descriptor,
            bytes,
            controller.Tokens.SubmitToken,
            buildId: "not-a-build",
            sessionId: "not-a-session");
        using var missingStagingResponse = await http.SendAsync(missingStaging);

        using var upload = new HttpRequestMessage(HttpMethod.Put, $"/blobs/{descriptor.Sha256}")
        {
            Content = new ByteArrayContent(bytes),
        };
        upload.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", controller.Tokens.SubmitToken);
        upload.Headers.Add("X-Vivarium-Blob-Staging-Id", plan);
        using var uploadResponse = await http.SendAsync(upload);

        using var changedReplay = new HttpRequestMessage(
            HttpMethod.Put,
            $"/blobs/{descriptor.Sha256}")
        {
            Content = new ByteArrayContent(new byte[bytes.Length]),
        };
        changedReplay.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", controller.Tokens.SubmitToken);
        changedReplay.Headers.Add("X-Vivarium-Blob-Staging-Id", plan);
        using var changedReplayResponse = await http.SendAsync(changedReplay);

        using var anonymousPut = AgentPut(
            descriptor,
            bytes,
            controller.Tokens.SubmitToken,
            "anonymous-build",
            "anonymous-session");
        anonymousPut.Headers.Authorization = null;
        using var anonymousPutResponse = await http.SendAsync(anonymousPut);

        using var rawGet = new HttpRequestMessage(HttpMethod.Get, $"/blobs/{descriptor.Sha256}");
        rawGet.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", controller.Tokens.SubmitToken);
        using var rawGetResponse = await http.SendAsync(rawGet);
        var audits = await controller.Audits.ListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(missingStagingResponse.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(uploadResponse.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
            Assert.That(changedReplayResponse.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
            Assert.That(anonymousPutResponse.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(rawGetResponse.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(audits, Has.Some.Matches<StoredAuditEvent>(item =>
                item.Action == "blob-artifact.upload" &&
                item.ActorId == "legacy-submit" &&
                item.TargetId == "not-a-build" &&
                item.Outcome == AuditOutcome.Denied &&
                item.ReasonCode == "permission_denied"));
            Assert.That(audits, Has.Some.Matches<StoredAuditEvent>(item =>
                item.Action == "blob-artifact.upload" &&
                item.ActorType == "anonymous" &&
                item.TargetId == "anonymous-build" &&
                item.Outcome == AuditOutcome.Denied &&
                item.ReasonCode == "authentication_required"));
            Assert.That(audits, Has.Some.Matches<StoredAuditEvent>(item =>
                item.Action == "blob-staging.upload" &&
                item.TargetId == plan &&
                item.Outcome == AuditOutcome.Failed &&
                item.ReasonCode == "blob_upload_replay_conflict"));
        });
    }

    [Test]
    public async Task Expired_staging_put_is_rejected_and_audited_with_a_stable_reason()
    {
        var time = new ManualTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1_800_000_000));
        await using var controller = await StartControllerAsync(time);
        using var http = PinnedClient(controller);
        var bytes = "expiring payload"u8.ToArray();
        var descriptor = Descriptor(bytes);
        var plan = await CreatePlanAsync(
            http,
            controller.Tokens.SubmitToken,
            descriptor,
            "expiring-plan");
        time.Advance(TimeSpan.FromMinutes(16));

        using var upload = new HttpRequestMessage(HttpMethod.Put, $"/blobs/{descriptor.Sha256}")
        {
            Content = new ByteArrayContent(bytes),
        };
        upload.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", controller.Tokens.SubmitToken);
        upload.Headers.Add("X-Vivarium-Blob-Staging-Id", plan);
        using var response = await http.SendAsync(upload);
        var audits = await controller.Audits.ListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Gone));
            Assert.That(audits, Has.Some.Matches<StoredAuditEvent>(item =>
                item.Action == "blob-staging.upload" &&
                item.TargetId == plan &&
                item.Outcome == AuditOutcome.Failed &&
                item.ReasonCode == "blob_staging_expired"));
        });
    }

    [Test]
    public async Task Agent_payload_get_requires_exact_build_session_and_logical_assignment()
    {
        await using var controller = await StartControllerAsync();
        using var http = PinnedClient(controller);
        var agentToken = await AddAuthorizedAgentAsync(controller, "agent-one", "session-one", 1);
        var bytes = "assigned payload"u8.ToArray();
        var descriptor = Descriptor(bytes);
        var stagingId = await CreatePlanAsync(
            http,
            controller.Tokens.SubmitToken,
            descriptor,
            "assigned-plan");
        await UploadStagedAsync(
            http,
            controller.Tokens.SubmitToken,
            stagingId,
            descriptor,
            bytes);
        await AttachPayloadAsync(controller, stagingId, descriptor);

        using var exactRequest = AgentGet(descriptor.Sha256, agentToken, "build-one", "session-one");
        using var exact = await http.SendAsync(exactRequest);
        var received = await exact.Content.ReadAsByteArrayAsync();
        using var wrongSessionRequest = AgentGet(
            descriptor.Sha256, agentToken, "build-one", "session-other");
        using var wrongSession = await http.SendAsync(wrongSessionRequest);
        using var wrongBuildRequest = AgentGet(
            descriptor.Sha256, agentToken, "build-other", "session-one");
        using var wrongBuild = await http.SendAsync(wrongBuildRequest);

        Assert.Multiple(() =>
        {
            Assert.That(exact.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(received, Is.EqualTo(bytes));
            Assert.That(exact.Headers.CacheControl?.NoStore, Is.True);
            Assert.That(wrongSession.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(wrongBuild.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        });
    }

    [Test]
    public async Task Agent_artifact_put_requires_exact_build_session_and_declared_size()
    {
        await using var controller = await StartControllerAsync();
        using var http = PinnedClient(controller);
        var agentToken = await AddAuthorizedAgentAsync(controller, "agent-one", "session-one", 1);
        await InsertRunningBuildAsync(controller, "build-one", "agent-one", "session-one");
        var bytes = "artifact bytes"u8.ToArray();
        var descriptor = Descriptor(bytes);

        using var exactRequest = AgentPut(
            descriptor, bytes, agentToken, "build-one", "session-one");
        using var exact = await http.SendAsync(exactRequest);
        using var wrongSessionRequest = AgentPut(
            descriptor, bytes, agentToken, "build-one", "session-other");
        using var wrongSession = await http.SendAsync(wrongSessionRequest);
        using var wrongBuildRequest = AgentPut(
            descriptor, bytes, agentToken, "build-other", "session-one");
        using var wrongBuild = await http.SendAsync(wrongBuildRequest);
        using var wrongSizeRequest = AgentPut(
            descriptor, bytes, agentToken, "build-one", "session-one", descriptor.Size + 1);
        using var wrongSize = await http.SendAsync(wrongSizeRequest);
        var audits = await controller.Audits.ListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(exact.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
            Assert.That(wrongSession.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(wrongBuild.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(wrongSize.StatusCode, Is.EqualTo(HttpStatusCode.UnprocessableEntity));
            Assert.That(controller.Blobs.Contains(descriptor.Sha256), Is.True);
            Assert.That(audits, Has.Some.Matches<StoredAuditEvent>(item =>
                item.Action == "blob-artifact.upload" &&
                item.TargetId == "build-one" &&
                item.Outcome == AuditOutcome.Failed &&
                item.ReasonCode == "blob_declared_size_invalid"));
            Assert.That(audits.Count(item =>
                    item.Action == "blob-artifact.upload" &&
                    item.Outcome == AuditOutcome.Denied &&
                    item.ReasonCode == "blob_artifact_upload_not_found"),
                Is.EqualTo(2));
        });
    }

    private Task<VivariumControllerHost> StartControllerAsync(TimeProvider? timeProvider = null) =>
        VivariumControllerHost.StartAsync(new ControllerOptions
        {
            DataDir = Path.Combine(rootDir, "controller"),
            Host = "127.0.0.1",
            Port = 0,
            TimeProvider = timeProvider ?? TimeProvider.System,
        });

    private static async Task<string> CreatePlanAsync(
        HttpClient http,
        string token,
        BlobDescriptor descriptor,
        string idempotencyKey)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/blob-upload-plans")
        {
            Content = JsonContent.Create(new
            {
                projectId = "project-one",
                blobs = new[] { new { sha256 = descriptor.Sha256, size = descriptor.Size } },
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        using var response = await http.SendAsync(request);
        var serialized = await response.Content.ReadAsStringAsync();
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created), serialized);
        using var body = JsonDocument.Parse(serialized);
        return body.RootElement.GetProperty("id").GetString()!;
    }

    private static async Task UploadStagedAsync(
        HttpClient http,
        string token,
        string stagingId,
        BlobDescriptor descriptor,
        byte[] bytes)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/blobs/{descriptor.Sha256}")
        {
            Content = new ByteArrayContent(bytes),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("X-Vivarium-Blob-Staging-Id", stagingId);
        using var response = await http.SendAsync(request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
    }

    private static async Task<string> AddAuthorizedAgentAsync(
        VivariumControllerHost controller,
        string agentId,
        string sessionId,
        long connectionGeneration)
    {
        var admission = await controller.Tokens.AdmitAgentAsync(new Hello
        {
            AgentId = agentId,
            SessionId = sessionId,
            EnrollToken = await controller.Tokens.CreateEnrollTokenAsync(),
        });
        Assert.That(admission, Is.Not.Null);
        var token = await controller.Tokens.AuthorizeAgentAsync(agentId);
        Assert.That(token, Is.Not.Null.And.Not.Empty);
        await controller.Database.WriteAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE agents SET connection_generation = $generation WHERE agent_id = $agentId;";
            command.Parameters.AddWithValue("$generation", connectionGeneration);
            command.Parameters.AddWithValue("$agentId", agentId);
            Assert.That(command.ExecuteNonQuery(), Is.EqualTo(1));
            return true;
        });
        return token!;
    }

    private static async Task AttachPayloadAsync(
        VivariumControllerHost controller,
        string stagingId,
        BlobDescriptor descriptor)
    {
        var participant = new BlobAccessStore(controller.Database);
        await controller.Database.WriteAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            InsertMatrix(connection, transaction);
            InsertBuild(connection, transaction, "build-one", "agent-one", "session-one");
            using (var cell = connection.CreateCommand())
            {
                cell.Transaction = transaction;
                cell.CommandText = """
                    INSERT INTO matrix_build_cells(
                        matrix_build_id, cell_name, ordinal, build_id, agent_expression, rid)
                    VALUES ('matrix-one', 'cell-one', 0, 'build-one', 'true', 'test-rid');
                    """;
                cell.ExecuteNonQuery();
            }

            _ = participant.Attach(
                connection,
                transaction,
                new BlobBuildAttachmentRequest(
                    ManagementPrincipal.LegacySubmit,
                    "matrix-build.submit",
                    "assigned-plan",
                    stagingId,
                    "project-one",
                    "matrix-one",
                    [descriptor.Sha256],
                    DateTimeOffset.UtcNow));
            transaction.Commit();
            return true;
        });
    }

    private static Task InsertRunningBuildAsync(
        VivariumControllerHost controller,
        string buildId,
        string agentId,
        string sessionId) => controller.Database.WriteAsync(connection =>
    {
        using var transaction = connection.BeginTransaction();
        InsertBuild(connection, transaction, buildId, agentId, sessionId);
        transaction.Commit();
        return true;
    });

    private static void InsertMatrix(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO matrix_builds(
                matrix_build_id, request_id, request_hash, request_payload, project,
                configuration, definition_snapshot, definition_hash,
                created_unix_ms, updated_unix_ms)
            VALUES (
                'matrix-one', 'matrix-request-one', 'matrix-request-hash', X'00',
                'project-one', 'configuration-one', X'00', 'definition-hash', 1, 1);
            """;
        command.ExecuteNonQuery();
    }

    private static void InsertBuild(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteTransaction transaction,
        string buildId,
        string agentId,
        string sessionId)
    {
        var assignment = new BuildAssignment { BuildId = buildId };
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO builds(
                build_id, agent_id, owner_session_id, state, assignment,
                created_unix_ms, updated_unix_ms)
            VALUES ($buildId, $agentId, $sessionId, 'RUNNING', $assignment, 1, 1);
            """;
        command.Parameters.AddWithValue("$buildId", buildId);
        command.Parameters.AddWithValue("$agentId", agentId);
        command.Parameters.AddWithValue("$sessionId", sessionId);
        command.Parameters.Add("$assignment", Microsoft.Data.Sqlite.SqliteType.Blob).Value =
            assignment.ToByteArray();
        command.ExecuteNonQuery();
    }

    private static HttpRequestMessage AgentGet(
        string sha256,
        string token,
        string buildId,
        string sessionId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/blobs/{sha256}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("X-Vivarium-Build-Id", buildId);
        request.Headers.Add("X-Vivarium-Session-Id", sessionId);
        return request;
    }

    private static HttpRequestMessage AgentPut(
        BlobDescriptor descriptor,
        byte[] bytes,
        string token,
        string buildId,
        string sessionId,
        long? declaredSize = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/blobs/{descriptor.Sha256}")
        {
            Content = new ByteArrayContent(bytes),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("X-Vivarium-Build-Id", buildId);
        request.Headers.Add("X-Vivarium-Session-Id", sessionId);
        request.Headers.Add(
            "X-Vivarium-Blob-Declared-Size",
            (declaredSize ?? descriptor.Size).ToString(System.Globalization.CultureInfo.InvariantCulture));
        return request;
    }

    private static BlobDescriptor Descriptor(byte[] bytes) =>
        new(Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length);

    private static HttpClient PinnedClient(VivariumControllerHost controller)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                certificate is not null &&
                Convert.ToHexString(SHA256.HashData(certificate.RawData)).Equals(
                    controller.Certificate.FingerprintSha256,
                    StringComparison.OrdinalIgnoreCase),
        };
        return new HttpClient(handler) { BaseAddress = new Uri(controller.Url) };
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;

        public void Advance(TimeSpan amount) => utcNow += amount;
    }
}
