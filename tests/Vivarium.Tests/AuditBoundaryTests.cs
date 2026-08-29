using System.Security.Cryptography;
using System.Text;
using Grpc.Core;
using Grpc.Net.Client;
using Vivarium.Contracts.V1;
using Vivarium.Controller;
using Vivarium.Controller.Auditing;

namespace Vivarium.Tests;

[TestFixture]
[NonParallelizable]
public sealed class AuditBoundaryTests
{
    private const int MaximumIdentityLength = 256;
    private string rootDir = null!;

    [SetUp]
    public void SetUp()
    {
        rootDir = Path.Combine(
            Path.GetTempPath(), "vivarium-audit-boundary-tests", Guid.NewGuid().ToString("N"));
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
    public async Task Maximum_agent_and_session_identifiers_enroll_and_remain_auditable()
    {
        await using var controller = await StartControllerAsync();
        var hello = HelloFor(
            new string('a', MaximumIdentityLength),
            new string('s', MaximumIdentityLength),
            await controller.Tokens.CreateEnrollTokenAsync());

        var response = await EnrollAsync(controller, hello);
        var audit = (await controller.Audits.ListAsync()).Single(audit =>
            audit.Action == "agent.enroll" && audit.TargetId == hello.AgentId);

        Assert.Multiple(() =>
        {
            Assert.That(response.MsgCase, Is.EqualTo(ControllerMsg.MsgOneofCase.Welcome));
            Assert.That(audit.Action, Is.EqualTo("agent.enroll"));
            Assert.That(audit.TargetId, Is.EqualTo(hello.AgentId));
            Assert.That(audit.ActorId, Is.EqualTo(hello.AgentId));
            Assert.That(audit.Details["session_id"], Is.EqualTo(hello.SessionId));
            Assert.That(audit.Source, Is.EqualTo("agent-hub"));
        });
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task Overlong_agent_or_session_identifier_is_rejected_before_persistence(
        bool overlongAgentId)
    {
        await using var controller = await StartControllerAsync();
        var agentId = overlongAgentId
            ? new string('a', MaximumIdentityLength + 1)
            : "bounded-agent";
        var sessionId = overlongAgentId
            ? "bounded-session"
            : new string('s', MaximumIdentityLength + 1);
        var hello = HelloFor(
            agentId,
            sessionId,
            await controller.Tokens.CreateEnrollTokenAsync());

        var directError = Assert.ThrowsAsync<ArgumentException>(async () =>
            await controller.Tokens.AdmitAgentAsync(hello));
        var rpcError = Assert.ThrowsAsync<RpcException>(async () =>
            await EnrollAsync(controller, hello));
        var storedAgent = await controller.AgentStore.GetAsync(agentId);
        var claimedAgentId = await ReadClaimedAgentIdAsync(controller, hello.EnrollToken);
        var audits = await controller.Audits.ListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(directError!.Message, Does.Contain("must be between 1 and 256 characters"));
            Assert.That(rpcError!.StatusCode, Is.EqualTo(StatusCode.InvalidArgument));
            Assert.That(storedAgent, Is.Null);
            Assert.That(claimedAgentId, Is.Null);
            Assert.That(audits.Where(audit => IsEnrollmentAuditFor(audit, hello.AgentId)), Is.Empty);
        });
    }

    [Test]
    public async Task Fresh_enrollment_audit_is_high_signal_and_contains_no_enrollment_secret()
    {
        await using var controller = await StartControllerAsync();
        var enrollmentToken = await controller.Tokens.CreateEnrollTokenAsync();
        var hello = HelloFor("fresh-agent", "fresh-session", enrollmentToken);

        await EnrollAsync(controller, hello);
        var audit = (await controller.Audits.ListAsync()).Single(audit =>
            audit.Action == "agent.enroll" && audit.TargetId == hello.AgentId);
        var rawAudit = await ReadRawAuditAsync(controller);
        var enrollmentTokenHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(enrollmentToken)));

        Assert.Multiple(() =>
        {
            Assert.That(audit.ActorType, Is.EqualTo("agent"));
            Assert.That(audit.ActorId, Is.EqualTo(hello.AgentId));
            Assert.That(audit.CredentialKind, Is.EqualTo("enrollment-token"));
            Assert.That(audit.Action, Is.EqualTo("agent.enroll"));
            Assert.That(audit.TargetType, Is.EqualTo("agent"));
            Assert.That(audit.TargetId, Is.EqualTo(hello.AgentId));
            Assert.That(audit.Outcome, Is.EqualTo(AuditOutcome.Succeeded));
            Assert.That(audit.Source, Is.EqualTo("agent-hub"));
            Assert.That(audit.Details, Has.Count.EqualTo(1));
            Assert.That(audit.Details["session_id"], Is.EqualTo(hello.SessionId));
            Assert.That(rawAudit, Does.Not.Contain(enrollmentToken));
            Assert.That(rawAudit, Does.Not.Contain(enrollmentTokenHash));
        });
    }

    [Test]
    public async Task Existing_agent_reconnect_with_bound_enrollment_proof_does_not_append_audit()
    {
        await using var controller = await StartControllerAsync();
        var enrollmentToken = await controller.Tokens.CreateEnrollTokenAsync();
        var initialHello = HelloFor("reconnect-agent", "initial-session", enrollmentToken);
        var reconnectHello = HelloFor("reconnect-agent", "reconnect-session", enrollmentToken);

        await EnrollAsync(controller, initialHello);
        await EnrollAsync(controller, reconnectHello);
        var audits = (await controller.Audits.ListAsync())
            .Where(audit => IsEnrollmentAuditFor(audit, initialHello.AgentId))
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(audits, Has.Length.EqualTo(1));
            Assert.That(audits[0].Action, Is.EqualTo("agent.enroll"));
            Assert.That(audits[0].Details["session_id"], Is.EqualTo(initialHello.SessionId));
        });
    }

    [Test]
    public async Task Replacement_enrollment_is_audited_and_contains_no_enrollment_secret()
    {
        await using var controller = await StartControllerAsync();
        var initialToken = await controller.Tokens.CreateEnrollTokenAsync();
        var initialHello = HelloFor("replacement-agent", "initial-session", initialToken);
        await EnrollAsync(controller, initialHello);
        Assert.That(await controller.Tokens.AuthorizeAgentAsync(initialHello.AgentId), Is.Not.Null);

        var replacementToken = await controller.Tokens.CreateEnrollTokenAsync();
        var replacementHello = HelloFor(
            initialHello.AgentId,
            "replacement-session",
            replacementToken);
        await EnrollAsync(controller, replacementHello);

        var storedAgent = await controller.AgentStore.GetAsync(initialHello.AgentId);
        var storedEnrollmentHash = await ReadAgentEnrollmentHashAsync(
            controller,
            initialHello.AgentId);
        var claimedAgentId = await ReadClaimedAgentIdAsync(controller, replacementToken);
        var audits = await controller.Audits.ListAsync();
        var replacementAudit = audits.Single(audit =>
            audit.Action == "agent.reenroll" && audit.TargetId == initialHello.AgentId);
        var enrollmentAudits = audits
            .Where(audit => IsEnrollmentAuditFor(audit, initialHello.AgentId))
            .ToArray();
        var rawAudit = await ReadRawAuditAsync(controller);
        var replacementTokenHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(replacementToken)));

        Assert.Multiple(() =>
        {
            Assert.That(storedAgent!.Authorized, Is.False);
            Assert.That(storedEnrollmentHash, Is.EqualTo(replacementTokenHash));
            Assert.That(claimedAgentId, Is.EqualTo(initialHello.AgentId));
            Assert.That(enrollmentAudits, Has.Length.EqualTo(2));
            Assert.That(replacementAudit.ActorType, Is.EqualTo("agent"));
            Assert.That(replacementAudit.ActorId, Is.EqualTo(initialHello.AgentId));
            Assert.That(replacementAudit.CredentialKind, Is.EqualTo("enrollment-token"));
            Assert.That(replacementAudit.TargetId, Is.EqualTo(initialHello.AgentId));
            Assert.That(replacementAudit.Outcome, Is.EqualTo(AuditOutcome.Succeeded));
            Assert.That(replacementAudit.Details["session_id"], Is.EqualTo(replacementHello.SessionId));
            Assert.That(rawAudit, Does.Not.Contain(replacementToken));
            Assert.That(rawAudit, Does.Not.Contain(replacementTokenHash));
        });
    }

    [Test]
    public async Task Replacement_enrollment_and_token_claim_roll_back_when_audit_insert_fails()
    {
        await using var controller = await StartControllerAsync();
        var initialToken = await controller.Tokens.CreateEnrollTokenAsync();
        var initialHello = HelloFor("replacement-rollback-agent", "initial-session", initialToken);
        await EnrollAsync(controller, initialHello);
        Assert.That(await controller.Tokens.AuthorizeAgentAsync(initialHello.AgentId), Is.Not.Null);

        var initialEnrollmentHash = await ReadAgentEnrollmentHashAsync(
            controller,
            initialHello.AgentId);
        var replacementToken = await controller.Tokens.CreateEnrollTokenAsync();
        var replacementHello = HelloFor(
            initialHello.AgentId,
            "replacement-session",
            replacementToken);
        await controller.Database.WriteAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TRIGGER reject_reenrollment_audit
                BEFORE INSERT ON audit_events
                WHEN NEW.action = 'agent.reenroll'
                BEGIN
                    SELECT RAISE(ABORT, 'test reenrollment audit rejection');
                END;
                """;
            command.ExecuteNonQuery();
            return true;
        });

        Assert.ThrowsAsync<RpcException>(async () => await EnrollAsync(controller, replacementHello));
        var storedAgent = await controller.AgentStore.GetAsync(initialHello.AgentId);
        var storedEnrollmentHash = await ReadAgentEnrollmentHashAsync(
            controller,
            initialHello.AgentId);
        var claimedAgentId = await ReadClaimedAgentIdAsync(controller, replacementToken);
        var audits = await controller.Audits.ListAsync();
        var enrollmentAudits = audits
            .Where(audit => IsEnrollmentAuditFor(audit, initialHello.AgentId))
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(storedAgent!.Authorized, Is.True);
            Assert.That(storedEnrollmentHash, Is.EqualTo(initialEnrollmentHash));
            Assert.That(claimedAgentId, Is.Null);
            Assert.That(
                enrollmentAudits.Select(audit => audit.Action),
                Is.EqualTo(new[] { "agent.enroll" }));
        });
    }

    [Test]
    public async Task Invalid_enrollment_proof_records_redacted_anonymous_denial()
    {
        await using var controller = await StartControllerAsync();
        const string invalidToken = "invalid-enrollment-secret";
        var hello = HelloFor("denied-agent", "denied-session", invalidToken);

        var exception = Assert.ThrowsAsync<RpcException>(async () => await EnrollAsync(controller, hello));
        var audit = (await controller.Audits.ListAsync()).Single(audit =>
            audit.Action == "agent.enroll" && audit.TargetId == hello.AgentId);
        var rawAudit = await ReadRawAuditAsync(controller);
        var invalidTokenHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(invalidToken)));

        Assert.Multiple(() =>
        {
            Assert.That(exception!.StatusCode, Is.EqualTo(StatusCode.PermissionDenied));
            Assert.That(audit.ActorType, Is.EqualTo("anonymous"));
            Assert.That(audit.ActorId, Is.EqualTo("anonymous"));
            Assert.That(audit.CredentialKind, Is.EqualTo("none"));
            Assert.That(audit.Action, Is.EqualTo("agent.enroll"));
            Assert.That(audit.TargetType, Is.EqualTo("agent"));
            Assert.That(audit.TargetId, Is.EqualTo(hello.AgentId));
            Assert.That(audit.Outcome, Is.EqualTo(AuditOutcome.Denied));
            Assert.That(audit.ReasonCode, Is.EqualTo("invalid_enrollment_proof"));
            Assert.That(audit.Source, Is.EqualTo("agent-hub"));
            Assert.That(audit.Details["session_id"], Is.EqualTo(hello.SessionId));
            Assert.That(rawAudit, Does.Not.Contain(invalidToken));
            Assert.That(rawAudit, Does.Not.Contain(invalidTokenHash));
        });
    }

    [Test]
    public async Task Audit_rows_without_source_metadata_remain_readable()
    {
        await using var controller = await StartControllerAsync();
        await controller.Database.WriteAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO audit_events(
                    audit_event_id, received_unix_ms, actor_type, actor_id, credential_kind,
                    correlation_id, request_id, action, target_type, target_id, outcome,
                    reason_code, details_json, base_revision, result_revision)
                VALUES (
                    'legacy-audit-row', 1, 'system', 'controller', 'internal',
                    'legacy-correlation', NULL, 'legacy.action', 'legacy', 'legacy-target',
                    'SUCCEEDED', '', '{}', NULL, NULL);
                """;
            command.ExecuteNonQuery();
            return true;
        });

        var audit = (await controller.Audits.ListAsync()).Single(audit =>
            audit.Action == "legacy.action" && audit.TargetId == "legacy-target");

        Assert.Multiple(() =>
        {
            Assert.That(audit.Source, Is.Null);
            Assert.That(audit.Details, Is.Empty);
        });
    }

    [Test]
    public async Task Enrollment_identity_and_token_claim_roll_back_when_audit_insert_fails()
    {
        await using var controller = await StartControllerAsync();
        var enrollmentToken = await controller.Tokens.CreateEnrollTokenAsync();
        var hello = HelloFor("rollback-agent", "rollback-session", enrollmentToken);
        await controller.Database.WriteAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TRIGGER reject_enrollment_audit
                BEFORE INSERT ON audit_events
                BEGIN
                    SELECT RAISE(ABORT, 'test enrollment audit rejection');
                END;
                """;
            command.ExecuteNonQuery();
            return true;
        });

        Assert.ThrowsAsync<RpcException>(async () => await EnrollAsync(controller, hello));
        var storedAgent = await controller.AgentStore.GetAsync(hello.AgentId);
        var claimedAgentId = await ReadClaimedAgentIdAsync(controller, enrollmentToken);
        var audits = await controller.Audits.ListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(storedAgent, Is.Null);
            Assert.That(claimedAgentId, Is.Null);
            Assert.That(audits.Where(audit => IsEnrollmentAuditFor(audit, hello.AgentId)), Is.Empty);
        });
    }

    private Task<VivariumControllerHost> StartControllerAsync() =>
        VivariumControllerHost.StartAsync(new ControllerOptions
        {
            DataDir = Path.Combine(rootDir, "controller"),
            Host = "127.0.0.1",
            Port = 0,
        });

    private static Hello HelloFor(string agentId, string sessionId, string enrollmentToken) => new()
    {
        AgentId = agentId,
        SessionId = sessionId,
        EnrollToken = enrollmentToken,
        AgentVersion = "test",
        Os = new OsInfo { Family = "linux", Arch = "x64", Version = "test" },
    };

    private static bool IsEnrollmentAudit(StoredAuditEvent audit) =>
        audit.Action is "agent.enroll" or "agent.reenroll";

    private static bool IsEnrollmentAuditFor(StoredAuditEvent audit, string agentId) =>
        IsEnrollmentAudit(audit) && audit.TargetId == agentId;

    private static async Task<ControllerMsg> EnrollAsync(
        VivariumControllerHost controller,
        Hello hello)
    {
        using var channel = GrpcChannel.ForAddress(controller.Url, new GrpcChannelOptions
        {
            HttpHandler = PinnedHandler(controller),
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var call = new AgentHub.AgentHubClient(channel).Session(cancellationToken: timeout.Token);
        await call.RequestStream.WriteAsync(new AgentMsg { Hello = hello }, timeout.Token);
        if (!await call.ResponseStream.MoveNext(timeout.Token))
        {
            throw new AssertionException("AgentHub ended before returning Welcome");
        }

        return call.ResponseStream.Current.Clone();
    }

    private static HttpClientHandler PinnedHandler(VivariumControllerHost controller) => new()
    {
        ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
            certificate is not null &&
            Convert.ToHexString(SHA256.HashData(certificate.RawData)).Equals(
                controller.Certificate.FingerprintSha256,
                StringComparison.OrdinalIgnoreCase),
    };

    private static Task<string?> ReadClaimedAgentIdAsync(
        VivariumControllerHost controller,
        string enrollmentToken) =>
        controller.Database.ReadAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT claimed_agent_id FROM enroll_tokens WHERE token_hash = $hash;";
            command.Parameters.AddWithValue(
                "$hash",
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(enrollmentToken))));
            var value = command.ExecuteScalar();
            return value is null or DBNull ? null : (string)value;
        });

    private static Task<string?> ReadAgentEnrollmentHashAsync(
        VivariumControllerHost controller,
        string agentId) =>
        controller.Database.ReadAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT enroll_token_hash FROM agents WHERE agent_id = $agentId;";
            command.Parameters.AddWithValue("$agentId", agentId);
            var value = command.ExecuteScalar();
            return value is null or DBNull ? null : (string)value;
        });

    private static Task<string> ReadRawAuditAsync(VivariumControllerHost controller) =>
        controller.Database.ReadAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM audit_events ORDER BY audit_event_id;";
            using var reader = command.ExecuteReader();
            var rows = new List<string>();
            while (reader.Read())
            {
                var values = new string[reader.FieldCount];
                for (var index = 0; index < values.Length; index++)
                {
                    values[index] = reader.IsDBNull(index) ? string.Empty : reader.GetValue(index).ToString()!;
                }

                rows.Add(string.Join('|', values));
            }

            Assert.That(rows, Is.Not.Empty);
            return string.Join('\n', rows);
        });
}
