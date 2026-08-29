using System.Security.Cryptography;
using Google.Protobuf;
using Vivarium.Contracts.V1;
using Vivarium.Controller;
using Vivarium.Controller.Agents;
using Vivarium.Controller.Auditing;
using Vivarium.Controller.Management;
using Vivarium.Controller.Security;

namespace Vivarium.Tests;

[TestFixture]
[NonParallelizable]
public sealed class PrincipalIdempotencyTests
{
    private string rootDir = null!;

    [SetUp]
    public void SetUp()
    {
        rootDir = Path.Combine(
            Path.GetTempPath(),
            "vivarium-principal-idempotency-tests",
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
            // Best effort: a failed database assertion must remain visible.
        }
    }

    [Test]
    public async Task Matrix_submission_deduplicates_by_actor_identity_and_client_request_id()
    {
        await using var controller = await VivariumControllerHost.StartAsync(new ControllerOptions
        {
            DataDir = Path.Combine(rootDir, "controller"),
            Host = "127.0.0.1",
            Port = 0,
        });
        await RegisterAgentAsync(controller, "known-windows");
        var payloadHash = await PutBlobAsync(controller, "principal idempotency payload"u8.ToArray());
        var request = MatrixRequest("shared-client-request", payloadHash);

        var actorA = new ManagementPrincipal(
            "service", "ci-principal-a", "token-generation-1", BearerScope.Submit);
        var rotatedActorA = new ManagementPrincipal(
            "service", "ci-principal-a", "token-generation-2", BearerScope.Submit);
        var actorB = new ManagementPrincipal(
            "service", "ci-principal-b", "token-generation-1", BearerScope.Submit);

        var first = await controller.MatrixBuildSubmissions.SubmitAsync(
            Context(actorA, "principal-a-first"), request);
        var retry = await controller.MatrixBuildSubmissions.SubmitAsync(
            Context(rotatedActorA, "principal-a-retry"), request.Clone());

        var changed = request.Clone();
        changed.Configuration = "changed-configuration";
        Assert.ThrowsAsync<MatrixRequestConflictException>(async () =>
            await controller.MatrixBuildSubmissions.SubmitAsync(
                Context(rotatedActorA, "principal-a-conflict"), changed));

        var deniedPrincipal = actorA with { CredentialKind = "none", LegacyScope = null };
        Assert.ThrowsAsync<ManagementAuthorizationException>(async () =>
            await controller.MatrixBuildSubmissions.SubmitAsync(
                Context(deniedPrincipal, "principal-a-denied"), changed));

        var independent = await controller.MatrixBuildSubmissions.SubmitAsync(
            Context(actorB, "principal-b-first"), request.Clone());

        var persisted = await controller.Database.ReadAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    i.actor_id, i.request_id, i.matrix_build_id, m.request_id
                FROM matrix_build_idempotency i
                JOIN matrix_builds m ON m.matrix_build_id = i.matrix_build_id
                WHERE i.request_id = 'shared-client-request'
                ORDER BY i.actor_id;
                """;
            using var reader = command.ExecuteReader();
            var rows = new List<(string ActorId, string ClientRequestId, string BuildId, string StorageKey)>();
            while (reader.Read())
            {
                rows.Add((
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3)));
            }

            return rows;
        });
        var auditEvents = (await controller.Audits.ListAsync())
            .Where(audit => audit.Action == "matrix-build.submit")
            .ToArray();
        var successes = auditEvents
            .Where(audit => audit.Outcome == AuditOutcome.Succeeded)
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(retry.BuildId, Is.EqualTo(first.BuildId));
            Assert.That(independent.BuildId, Is.Not.EqualTo(first.BuildId));
            Assert.That(persisted, Has.Count.EqualTo(2));
            Assert.That(persisted.Select(row => row.ActorId),
                Is.EqualTo(new[] { "ci-principal-a", "ci-principal-b" }));
            Assert.That(persisted.Select(row => row.BuildId),
                Is.EquivalentTo(new[] { first.BuildId, independent.BuildId }));
            Assert.That(persisted.All(row => row.ClientRequestId == request.RequestId), Is.True);
            Assert.That(persisted.All(row => row.StorageKey.StartsWith(
                "principal:", StringComparison.Ordinal)), Is.True);
            Assert.That(persisted.Select(row => row.StorageKey).Distinct().ToArray(), Has.Length.EqualTo(2));
            Assert.That(successes, Has.Length.EqualTo(2));
            Assert.That(successes.Select(audit => audit.TargetId),
                Is.EquivalentTo(new[] { first.BuildId, independent.BuildId }));
            Assert.That(auditEvents.Count(audit => audit.Outcome == AuditOutcome.Denied), Is.EqualTo(1));
        });
    }

    private static ManagementRequestContext Context(
        ManagementPrincipal principal,
        string correlationId) =>
        new(principal, correlationId, RequestId: null, Source: "test");

    private static async Task RegisterAgentAsync(
        VivariumControllerHost controller,
        string agentId)
    {
        var enrollToken = await controller.Tokens.CreateEnrollTokenAsync();
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
        Assert.That(await controller.Tokens.AdmitAgentAsync(hello), Is.Not.Null);
        await controller.AgentStore.ObserveHelloAsync(hello);
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

    private static SubmitBuildRequest MatrixRequest(string requestId, string payloadHash)
    {
        var assignment = new BuildAssignment();
        assignment.Payload.Add(new Blob { Sha256 = payloadHash, FileName = "payload.zip" });
        var request = new SubmitBuildRequest
        {
            RequestId = requestId,
            Project = "Vivarium",
            Configuration = "principal-idempotency",
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
}
