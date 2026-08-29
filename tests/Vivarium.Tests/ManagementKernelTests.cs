using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using Microsoft.Data.Sqlite;
using Vivarium.Contracts.V1;
using Vivarium.Controller;
using Vivarium.Controller.Agents;
using Vivarium.Controller.Auditing;
using Vivarium.Controller.Builds;
using Vivarium.Controller.Management;
using Vivarium.Controller.Persistence;
using Vivarium.Controller.Security;

namespace Vivarium.Tests;

[TestFixture]
[NonParallelizable]
public sealed class ManagementKernelTests
{
    private string rootDir = null!;

    [SetUp]
    public void SetUp()
    {
        rootDir = Path.Combine(
            Path.GetTempPath(), "vivarium-management-kernel-tests", Guid.NewGuid().ToString("N"));
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
            // Best effort: preserve the original failure when a platform delays handle release.
        }
    }

    [Test]
    public void Legacy_credentials_keep_their_existing_permission_boundaries()
    {
        var authorizer = new ManagementAuthorizer();
        var submitPermissions = new[]
        {
            ManagementPermission.BlobRead,
            ManagementPermission.BlobWrite,
            ManagementPermission.BlobDiscover,
            ManagementPermission.BuildSubmit,
            ManagementPermission.BuildWatch,
            ManagementPermission.BuildCancel,
        };
        var administrationPermissions = new[]
        {
            ManagementPermission.PanelAccess,
            ManagementPermission.AgentList,
            ManagementPermission.AgentAuthorize,
            ManagementPermission.AgentManage,
            ManagementPermission.EnrollmentTokenCreate,
            ManagementPermission.ArtifactRead,
        };

        Assert.Multiple(() =>
        {
            foreach (var permission in Enum.GetValues<ManagementPermission>())
            {
                Assert.That(
                    authorizer.Allows(ManagementPrincipal.LegacyAdmin, permission),
                    Is.True,
                    $"admin should retain {permission}");
                Assert.That(
                    authorizer.Allows(ManagementPrincipal.LegacySubmit, permission),
                    Is.EqualTo(submitPermissions.Contains(permission)),
                    $"submit scope for {permission}");
                Assert.That(
                    authorizer.Allows(ManagementPrincipal.Agent("agent-a"), permission),
                    Is.EqualTo(permission is
                        ManagementPermission.BlobRead or
                        ManagementPermission.BlobWrite or
                        ManagementPermission.AgentPackageRead),
                    $"agent scope for {permission}");
            }

            Assert.That(administrationPermissions.All(permission =>
                !authorizer.Allows(ManagementPrincipal.LegacySubmit, permission)), Is.True);
        });
    }

    [Test]
    public async Task Audit_rows_are_append_only_and_survive_restart()
    {
        var dataDir = Path.Combine(rootDir, "controller");
        Directory.CreateDirectory(dataDir);
        var context = new ManagementRequestContext(
            ManagementPrincipal.LegacyAdmin,
            "audit-restart-correlation",
            "request-1",
            "test");

        await using (var database = new VivariumDatabase(dataDir))
        {
            var audits = new AuditEventStore(database);
            await audits.AppendAsync(AuditEventDraft.Create(
                context,
                DateTimeOffset.FromUnixTimeSeconds(1_700_000_000),
                "test.append",
                "test-target",
                "one"));

            Assert.ThrowsAsync<SqliteException>(async () => await database.WriteAsync(connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE audit_events SET reason_code = 'changed';";
                command.ExecuteNonQuery();
                return true;
            }));
            Assert.ThrowsAsync<SqliteException>(async () => await database.WriteAsync(connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM audit_events;";
                command.ExecuteNonQuery();
                return true;
            }));
        }

        await using var restarted = new VivariumDatabase(dataDir);
        var restored = await new AuditEventStore(restarted).ListAsync();
        Assert.Multiple(() =>
        {
            Assert.That(restored, Has.Count.EqualTo(1));
            Assert.That(restored[0].Action, Is.EqualTo("test.append"));
            Assert.That(restored[0].ActorId, Is.EqualTo("legacy-admin"));
            Assert.That(restored[0].CorrelationId, Is.EqualTo("audit-restart-correlation"));
        });
    }

    [Test]
    public async Task Rejected_audit_insert_rolls_back_its_agent_mutation()
    {
        var dataDir = Path.Combine(rootDir, "controller");
        Directory.CreateDirectory(dataDir);
        await using var database = new VivariumDatabase(dataDir);
        var tokens = new TokenStore(dataDir, database);
        var store = new AgentStore(database);
        var authorization = new ManagementCommandAuthorizer(
            new ManagementAuthorizer(), new AuditEventStore(database), TimeProvider.System);
        const string agentId = "atomic-audit-agent";
        await RegisterAgentAsync(tokens, store, agentId);
        await store.SetAuthorizedAsync(agentId, authorized: true);
        var administration = new AgentAdministration(
            new AgentRegistry(store),
            store,
            new BuildStore(database),
            tokens,
            new AgentLifecycleCoordinator(),
            authorization: authorization);

        await database.WriteAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TRIGGER reject_test_audit
                BEFORE INSERT ON audit_events
                BEGIN
                    SELECT RAISE(ABORT, 'test audit rejection');
                END;
                """;
            command.ExecuteNonQuery();
            return true;
        });

        var context = new ManagementRequestContext(
            ManagementPrincipal.LegacyAdmin,
            "atomic-audit-correlation",
            RequestId: null,
            "test");
        var error = Assert.ThrowsAsync<SqliteException>(async () =>
            await administration.UnauthorizeAsync(context, agentId));
        var restored = await store.GetAsync(agentId);
        var auditCount = await ReadAuditCountAsync(database);

        Assert.Multiple(() =>
        {
            Assert.That(error!.Message, Does.Contain("test audit rejection"));
            Assert.That(restored!.Authorized, Is.True);
            Assert.That(auditCount, Is.Zero);
        });
    }

    [Test]
    public async Task Enrollment_token_audit_never_contains_the_plaintext_secret()
    {
        var dataDir = Path.Combine(rootDir, "controller");
        Directory.CreateDirectory(dataDir);
        await using var database = new VivariumDatabase(dataDir);
        var tokens = new TokenStore(dataDir, database);
        var store = new AgentStore(database);
        var authorization = new ManagementCommandAuthorizer(
            new ManagementAuthorizer(), new AuditEventStore(database), TimeProvider.System);
        var administration = new AgentAdministration(
            new AgentRegistry(store),
            store,
            new BuildStore(database),
            tokens,
            new AgentLifecycleCoordinator(),
            authorization: authorization);
        var context = new ManagementRequestContext(
            ManagementPrincipal.LegacyAdmin,
            "token-redaction-correlation",
            RequestId: null,
            "test");

        await RegisterAgentAsync(tokens, store, "automatic-lifecycle-agent");
        Assert.That(await ReadAuditCountAsync(database), Is.Zero,
            "automatic credential and agent lifecycle work must not flood the audit journal");
        var plaintext = await administration.CreateEnrollTokenAsync(context);
        var audit = (await new AuditEventStore(database).ListAsync()).Single();
        var serializedAudit = string.Join('|', new[]
        {
            audit.AuditEventId,
            audit.ActorType,
            audit.ActorId,
            audit.CredentialKind,
            audit.CorrelationId,
            audit.RequestId ?? string.Empty,
            audit.Action,
            audit.TargetType,
            audit.TargetId,
            audit.ReasonCode,
            string.Join(';', audit.Details.Select(pair => $"{pair.Key}={pair.Value}")),
        });

        Assert.Multiple(() =>
        {
            Assert.That(audit.Action, Is.EqualTo("enrollment-token.create"));
            Assert.That(audit.TargetType, Is.EqualTo("enrollment-token"));
            Assert.That(serializedAudit, Does.Not.Contain(plaintext));
            Assert.That(serializedAudit, Does.Not.Contain(
                Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(plaintext)))));
        });
    }

    [Test]
    public async Task Matrix_submission_retry_has_one_durable_success_audit()
    {
        await using var controller = await VivariumControllerHost.StartAsync(new ControllerOptions
        {
            DataDir = Path.Combine(rootDir, "controller"),
            Host = "127.0.0.1",
            Port = 0,
        });
        await RegisterAgentAsync(controller.Tokens, controller.AgentStore, "known-windows");
        var payloadHash = await PutBlobAsync(controller, "audited matrix payload"u8.ToArray());
        var request = MatrixRequest("audit-idempotency-request", payloadHash);

        var context = ManagementRequestContext.System("test");
        var first = await controller.MatrixBuildSubmissions.SubmitAsync(context, request);
        var retry = await controller.MatrixBuildSubmissions.SubmitAsync(context, request.Clone());
        var successEvents = (await controller.Audits.ListAsync())
            .Where(audit => audit.Action == "matrix-build.submit")
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(retry.BuildId, Is.EqualTo(first.BuildId));
            Assert.That(successEvents, Has.Length.EqualTo(1));
            Assert.That(successEvents[0].TargetId, Is.EqualTo(first.BuildId));
            Assert.That(successEvents[0].RequestId, Is.EqualTo(request.RequestId));
        });
    }

    [Test]
    public async Task Panel_login_records_accepted_and_denied_decisions_without_tokens()
    {
        await using var controller = await VivariumControllerHost.StartAsync(new ControllerOptions
        {
            DataDir = Path.Combine(rootDir, "controller"),
            Host = "127.0.0.1",
            Port = 0,
        });
        using var http = PinnedClient(controller);

        var denied = await PostLoginAsync(http, "not-a-valid-token", "login-denied-correlation");
        var accepted = await PostLoginAsync(
            http, controller.Tokens.AdminToken, "login-accepted-correlation");
        var decisions = (await controller.Audits.ListAsync())
            .Where(audit => audit.Action == "security.authentication")
            .OrderBy(audit => audit.ReceivedAt)
            .ToArray();
        var flattened = string.Join('|', decisions.SelectMany(audit => new[]
        {
            audit.ActorId,
            audit.CredentialKind,
            audit.CorrelationId,
            audit.TargetId,
            audit.ReasonCode,
        }));

        Assert.Multiple(() =>
        {
            Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(accepted.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
            Assert.That(decisions.Select(audit => audit.Outcome),
                Is.EquivalentTo(new[] { AuditOutcome.Denied, AuditOutcome.Succeeded }));
            Assert.That(decisions.Select(audit => audit.CorrelationId),
                Does.Contain("login-denied-correlation"));
            Assert.That(decisions.Select(audit => audit.CorrelationId),
                Does.Contain("login-accepted-correlation"));
            Assert.That(flattened, Does.Not.Contain(controller.Tokens.AdminToken));
            Assert.That(flattened, Does.Not.Contain("not-a-valid-token"));
        });
    }

    [Test]
    public async Task Blob_writes_return_correlation_and_audit_success_failure_and_denial()
    {
        await using var controller = await VivariumControllerHost.StartAsync(new ControllerOptions
        {
            DataDir = Path.Combine(rootDir, "controller"),
            Host = "127.0.0.1",
            Port = 0,
        });
        using var http = PinnedClient(controller);
        var content = "audited blob"u8.ToArray();
        var sha256 = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        var acceptedPlan = await CreateBlobUploadPlanAsync(
            http,
            controller.Tokens.SubmitToken,
            "audit-project",
            sha256,
            content.LongLength,
            "blob-plan-accepted");

        using var acceptedRequest = new HttpRequestMessage(HttpMethod.Put, $"/blobs/{sha256}")
        {
            Content = new ByteArrayContent(content),
        };
        acceptedRequest.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", controller.Tokens.SubmitToken);
        acceptedRequest.Headers.Add("X-Vivarium-Blob-Staging-Id", acceptedPlan);
        acceptedRequest.Headers.Add(
            ManagementRequestContextFactory.CorrelationHeader, "blob-write-accepted");
        var accepted = await http.SendAsync(acceptedRequest);

        using var retryRequest = new HttpRequestMessage(HttpMethod.Put, $"/blobs/{sha256}")
        {
            Content = new ByteArrayContent(content),
        };
        retryRequest.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", controller.Tokens.SubmitToken);
        retryRequest.Headers.Add("X-Vivarium-Blob-Staging-Id", acceptedPlan);
        retryRequest.Headers.Add(
            ManagementRequestContextFactory.CorrelationHeader, "blob-write-retry");
        var retry = await http.SendAsync(retryRequest);

        var wrongSha256 = new string('f', 64);
        var rejectedPlan = await CreateBlobUploadPlanAsync(
            http,
            controller.Tokens.SubmitToken,
            "audit-project",
            wrongSha256,
            content.LongLength,
            "blob-plan-rejected");
        using var rejectedRequest = new HttpRequestMessage(HttpMethod.Put, $"/blobs/{wrongSha256}")
        {
            Content = new ByteArrayContent(content),
        };
        rejectedRequest.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", controller.Tokens.SubmitToken);
        rejectedRequest.Headers.Add("X-Vivarium-Blob-Staging-Id", rejectedPlan);
        rejectedRequest.Headers.Add(
            ManagementRequestContextFactory.CorrelationHeader, "blob-write-rejected");
        var rejected = await http.SendAsync(rejectedRequest);

        using var deniedRequest = new HttpRequestMessage(HttpMethod.Put, $"/blobs/{sha256}")
        {
            Content = new ByteArrayContent(content),
        };
        deniedRequest.Headers.Add("X-Vivarium-Blob-Staging-Id", acceptedPlan);
        deniedRequest.Headers.Add(
            ManagementRequestContextFactory.CorrelationHeader, "blob-write-denied");
        var denied = await http.SendAsync(deniedRequest);

        var invalidTarget = new string('s', 65) + "-password-secret-route-value";
        using var invalidTargetRequest = new HttpRequestMessage(
            HttpMethod.Put, $"/blobs/{sha256}")
        {
            Content = new ByteArrayContent(content),
        };
        invalidTargetRequest.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", controller.Tokens.SubmitToken);
        invalidTargetRequest.Headers.Add("X-Vivarium-Blob-Staging-Id", invalidTarget);
        invalidTargetRequest.Headers.Add(
            ManagementRequestContextFactory.CorrelationHeader, "blob-write-invalid-target");
        var invalid = await http.SendAsync(invalidTargetRequest);

        var events = (await controller.Audits.ListAsync())
            .Where(audit => audit.Action == "blob-staging.upload")
            .OrderBy(audit => audit.CorrelationId, StringComparer.Ordinal)
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(accepted.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
            Assert.That(
                accepted.Headers.GetValues(ManagementRequestContextFactory.CorrelationHeader).Single(),
                Is.EqualTo("blob-write-accepted"));
            Assert.That(retry.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
            Assert.That(rejected.StatusCode, Is.EqualTo(HttpStatusCode.UnprocessableEntity));
            Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(invalid.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(
                rejected.Headers.GetValues(ManagementRequestContextFactory.CorrelationHeader).Single(),
                Is.EqualTo("blob-write-rejected"));
            Assert.That(
                denied.Headers.GetValues(ManagementRequestContextFactory.CorrelationHeader).Single(),
                Is.EqualTo("blob-write-denied"));
            Assert.That(events, Has.Length.EqualTo(5));
            Assert.That(events.Single(audit => audit.CorrelationId == "blob-write-accepted").Outcome,
                Is.EqualTo(AuditOutcome.Succeeded));
            Assert.That(events.Single(audit => audit.CorrelationId == "blob-write-retry").Outcome,
                Is.EqualTo(AuditOutcome.NoChange));
            Assert.That(events.Single(audit => audit.CorrelationId == "blob-write-retry").ReasonCode,
                Is.EqualTo("exact_replay"));
            Assert.That(events.Single(audit => audit.CorrelationId == "blob-write-rejected").ReasonCode,
                Is.EqualTo("blob_digest_mismatch"));
            Assert.That(events.Single(audit => audit.CorrelationId == "blob-write-denied").Outcome,
                Is.EqualTo(AuditOutcome.Denied));
            Assert.That(
                events.Single(audit => audit.CorrelationId == "blob-write-invalid-target").TargetId,
                Is.EqualTo("(invalid)"));
            Assert.That(events.Select(audit => audit.TargetId),
                Does.Not.Contain(invalidTarget));
            Assert.That(events.All(audit => audit.Source == "blob-staging-put"), Is.True);
            Assert.That(string.Join('|', events.SelectMany(audit => new[]
            {
                audit.ActorId,
                audit.CredentialKind,
                audit.TargetId,
                audit.ReasonCode,
                string.Join(';', audit.Details.Select(pair => $"{pair.Key}={pair.Value}")),
            })), Does.Not.Contain(invalidTarget));
        });
    }

    [Test]
    public async Task Queued_and_running_child_cancellation_write_one_success_each()
    {
        await using var controller = await VivariumControllerHost.StartAsync(new ControllerOptions
        {
            DataDir = Path.Combine(rootDir, "controller"),
            Host = "127.0.0.1",
            Port = 0,
        });
        var context = new ManagementRequestContext(
            ManagementPrincipal.LegacyAdmin,
            "child-cancel-correlation",
            RequestId: null,
            "test");

        var payloadHash = await PutBlobAsync(controller, "queued cancellation payload"u8.ToArray());
        await RegisterAgentAsync(controller.Tokens, controller.AgentStore, "known-windows");
        var matrix = await controller.MatrixBuildSubmissions.SubmitAsync(
            context,
            MatrixRequest("queue-cancel-request", payloadHash));
        var queuedBuildId = (await controller.MatrixBuildStore.GetSnapshotAsync(matrix.BuildId))!
            .Cells.Single().BuildId;
        Assert.That(
            await controller.BuildQueue.RemoveAsync(context, queuedBuildId, "operator queue stop"),
            Is.True);
        Assert.That(
            await controller.BuildQueue.RemoveAsync(context, queuedBuildId, "duplicate queue stop"),
            Is.False);

        const string runningAgentId = "running-agent";
        const string runningSessionId = "running-session";
        var assignment = new BuildAssignment { BuildId = "running-cancel-build" };
        await controller.BuildStore.CreateAsync(
            runningAgentId, runningSessionId, assignment, DateTimeOffset.UtcNow);
        Assert.That(controller.Builds.AttachPreparedBuild(runningAgentId, assignment), Is.True);
        Assert.That(
            await controller.Builds.CancelBuildAsync(
                context, assignment.BuildId, "operator running stop"),
            Is.True);
        Assert.That(
            await controller.Builds.CancelBuildAsync(
                context, assignment.BuildId, "duplicate running stop"),
            Is.True);

        var cancellationEvents = (await controller.Audits.ListAsync())
            .Where(audit => audit.Action == "build.cancel")
            .ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(cancellationEvents, Has.Length.EqualTo(2));
            Assert.That(cancellationEvents.Select(audit => audit.TargetId),
                Is.EquivalentTo(new[] { queuedBuildId, assignment.BuildId }));
            Assert.That(cancellationEvents.All(audit =>
                audit.CorrelationId == context.CorrelationId), Is.True);
        });
    }

    private static async Task RegisterAgentAsync(
        TokenStore tokens,
        AgentStore store,
        string agentId)
    {
        var enrollToken = await tokens.CreateEnrollTokenAsync();
        var hello = new Hello
        {
            AgentId = agentId,
            EnrollToken = enrollToken,
            SessionId = $"session-{agentId}",
            AgentVersion = "test",
            Os = new OsInfo { Family = "windows", Arch = "x64", Version = "test" },
        };
        hello.Parameters["hostname"] = agentId;
        hello.Parameters["os.family"] = "windows";
        Assert.That(await tokens.AdmitAgentAsync(hello), Is.Not.Null);
        await store.ObserveHelloAsync(hello);
    }

    private static SubmitBuildRequest MatrixRequest(string requestId, string payloadHash)
    {
        var assignment = new BuildAssignment();
        assignment.Payload.Add(new Blob { Sha256 = payloadHash, FileName = "payload.zip" });
        var request = new SubmitBuildRequest
        {
            RequestId = requestId,
            Project = "Vivarium",
            Configuration = "management-kernel",
            DefinitionSnapshot = ByteString.CopyFromUtf8("project: Vivarium"),
        };
        request.Cells.Add(new MatrixBuildCell
        {
            Name = "windows",
            AgentExpression = "os.family == windows",
            Rid = "win-x64",
            Assignment = assignment,
        });
        return request;
    }

    private static async Task<string> PutBlobAsync(
        VivariumControllerHost controller,
        byte[] content)
    {
        var hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        await using var stream = new MemoryStream(content);
        Assert.That(
            await controller.Blobs.PutAsync(hash, stream, CancellationToken.None),
            Is.True);
        return hash;
    }

    private static async Task<string> CreateBlobUploadPlanAsync(
        HttpClient http,
        string token,
        string projectId,
        string sha256,
        long size,
        string idempotencyKey)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/blob-upload-plans")
        {
            Content = JsonContent.Create(new
            {
                projectId,
                blobs = new[] { new { sha256, size } },
            }),
        };
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        using var response = await http.SendAsync(request);
        Assert.That(
            response.StatusCode,
            Is.EqualTo(HttpStatusCode.Created),
            await response.Content.ReadAsStringAsync());
        var plan = await response.Content.ReadFromJsonAsync<JsonElement>();
        return plan.GetProperty("id").GetString()
            ?? throw new AssertionException("blob staging ID was missing");
    }

    private static Task<int> ReadAuditCountAsync(VivariumDatabase database) =>
        database.ReadAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM audit_events;";
            return Convert.ToInt32(command.ExecuteScalar());
        });

    private static async Task<HttpResponseMessage> PostLoginAsync(
        HttpClient http,
        string token,
        string correlationId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/login")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token }),
        };
        request.Headers.Add(ManagementRequestContextFactory.CorrelationHeader, correlationId);
        return await http.SendAsync(request);
    }

    private static HttpClient PinnedClient(VivariumControllerHost controller)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = true,
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                certificate is not null &&
                Convert.ToHexString(SHA256.HashData(certificate.RawData)).Equals(
                    controller.Certificate.FingerprintSha256,
                    StringComparison.OrdinalIgnoreCase),
        };
        return new HttpClient(handler) { BaseAddress = new Uri(controller.Url) };
    }
}
