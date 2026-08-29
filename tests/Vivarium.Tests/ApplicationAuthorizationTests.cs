using System.Security.Cryptography;
using Google.Protobuf;
using Vivarium.Contracts.V1;
using Vivarium.Controller;
using Vivarium.Controller.Agents;
using Vivarium.Controller.Auditing;
using Vivarium.Controller.Blobs;
using Vivarium.Controller.Builds;
using Vivarium.Controller.Management;
using Vivarium.Controller.Persistence;
using Vivarium.Controller.Security;

namespace Vivarium.Tests;

[TestFixture]
[NonParallelizable]
public sealed class ApplicationAuthorizationTests
{
    private string rootDir = null!;

    [SetUp]
    public void SetUp()
    {
        rootDir = Path.Combine(
            Path.GetTempPath(),
            "vivarium-application-authorization-tests",
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
            // Best effort: preserve the original failure when a platform delays handle release.
        }
    }

    [Test]
    public async Task Legacy_submit_cannot_run_agent_administration_commands()
    {
        await using var fixture = await Fixture.CreateAsync(rootDir);
        const string agentId = "protected-agent";
        await fixture.RegisterAgentAsync(agentId);
        var enrollTokenCountBefore = await fixture.EnrollTokenCountAsync();
        var context = Context(ManagementPrincipal.LegacySubmit, "submit-agent-administration");

        await AssertDeniedAsync(() => fixture.Administration.AuthorizeAsync(context, agentId));
        await AssertDeniedAsync(() => fixture.Administration.UnauthorizeAsync(context, agentId));
        await AssertDeniedAsync(() => fixture.Administration.SetEnabledAsync(context, agentId, false));
        await AssertDeniedAsync(() => fixture.Administration.RenameAsync(context, agentId, "renamed"));
        await AssertDeniedAsync(() => fixture.Administration.SetCustomParameterAsync(
            context, agentId, "pool", "restricted"));
        await AssertDeniedAsync(() => fixture.Administration.DeleteCustomParameterAsync(
            context, agentId, "pool"));
        await AssertDeniedAsync(() => fixture.Administration.DeleteAsync(context, agentId));
        await AssertDeniedAsync(async () =>
            _ = await fixture.Administration.CreateEnrollTokenAsync(context));

        var unchanged = await fixture.AgentStore.GetAsync(agentId);
        var enrollTokenCount = await fixture.EnrollTokenCountAsync();
        var denials = (await fixture.Audits.ListAsync())
            .Where(audit => audit.Outcome == AuditOutcome.Denied)
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(unchanged, Is.Not.Null);
            Assert.That(unchanged!.Name, Is.EqualTo(agentId));
            Assert.That(unchanged.Authorized, Is.False);
            Assert.That(unchanged.Enabled, Is.True);
            Assert.That(unchanged.CustomParameters, Is.Empty);
            Assert.That(enrollTokenCount, Is.EqualTo(enrollTokenCountBefore));
            Assert.That(denials, Has.Length.EqualTo(8));
            Assert.That(denials.Count(audit => audit.TargetId == agentId), Is.EqualTo(7));
            Assert.That(denials.All(audit => audit.ActorId == "legacy-submit"), Is.True);
            Assert.That(denials.All(audit => audit.ReasonCode == "permission_denied"), Is.True);
            Assert.That(denials.Select(audit => audit.Action), Does.Contain("agent.authorize"));
            Assert.That(denials.Select(audit => audit.Action), Does.Contain("agent.delete"));
            Assert.That(denials.Select(audit => audit.Action), Does.Contain("enrollment-token.create"));
        });
    }

    [Test]
    public async Task Agent_cannot_submit_and_denial_precedes_domain_validation_or_writes()
    {
        await using var fixture = await Fixture.CreateAsync(rootDir);
        var request = Request("agent-submit-denied", new string('a', 64));
        var context = Context(ManagementPrincipal.Agent("agent-caller"), "agent-submit");

        var exception = Assert.ThrowsAsync<ManagementAuthorizationException>(async () =>
            await fixture.Submissions.SubmitAsync(context, request));
        var counts = await fixture.DomainRowCountsAsync();
        var denial = (await fixture.Audits.ListAsync()).Single();

        Assert.Multiple(() =>
        {
            Assert.That(exception!.Permission, Is.EqualTo(ManagementPermission.BuildSubmit));
            Assert.That(counts, Is.EqualTo(new[] { 0, 0, 0 }));
            Assert.That(denial.Action, Is.EqualTo("matrix-build.submit"));
            Assert.That(denial.TargetType, Is.EqualTo("project"));
            Assert.That(denial.TargetId, Is.EqualTo("Vivarium"));
            Assert.That(denial.Outcome, Is.EqualTo(AuditOutcome.Denied));
            Assert.That(denial.RequestId, Is.EqualTo(request.RequestId));
        });
    }

    [Test]
    public async Task Agent_cannot_cancel_queue_running_or_matrix_work()
    {
        await using var fixture = await Fixture.CreateAsync(rootDir);
        await fixture.RegisterAgentAsync("known-windows");
        var payloadHash = await fixture.PutBlobAsync("authorization payload"u8.ToArray());
        var submitted = await fixture.Submissions.SubmitAsync(
            Context(ManagementPrincipal.System, "system-submit"),
            Request("system-submit", payloadHash));
        var matrixBefore = await fixture.MatrixBuilds.GetSnapshotAsync(submitted.BuildId);
        var queuedBuildId = matrixBefore!.Cells.Single().BuildId;

        const string runningBuildId = "running-authorization-build";
        var runningAssignment = new BuildAssignment { BuildId = runningBuildId };
        await fixture.BuildStore.CreateAsync(
            "known-windows", "running-session", runningAssignment, DateTimeOffset.UtcNow);
        Assert.That(
            fixture.Builds.AttachPreparedBuild("known-windows", runningAssignment),
            Is.True);

        var agentContext = Context(ManagementPrincipal.Agent("agent-caller"), "agent-cancel");
        await AssertDeniedAsync(() => fixture.Queue.RemoveAsync(
            agentContext, queuedBuildId, "agent tried queue cancellation"));
        await AssertDeniedAsync(() => fixture.Builds.CancelBuildAsync(
            agentContext, runningBuildId, "agent tried running cancellation"));
        await AssertDeniedAsync(() => fixture.Cancellations.CancelAsync(
            agentContext, submitted.BuildId, "agent tried matrix cancellation"));

        var queueAfterDenial = await fixture.QueueStore.GetAsync(queuedBuildId);
        var runningAfterDenial = await fixture.BuildStore.GetAsync(runningBuildId);
        var matrixAfterDenial = await fixture.MatrixBuilds.GetSnapshotAsync(submitted.BuildId);
        var denials = (await fixture.Audits.ListAsync())
            .Where(audit => audit.Outcome == AuditOutcome.Denied)
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(queueAfterDenial!.State, Is.EqualTo(BuildQueueItemState.Queued));
            Assert.That(runningAfterDenial!.State, Is.EqualTo(TrackedBuildState.Running));
            Assert.That(matrixAfterDenial, Is.EqualTo(matrixBefore));
            Assert.That(denials, Has.Length.EqualTo(3));
            Assert.That(denials.Select(audit => (audit.TargetType, audit.TargetId)), Is.EquivalentTo(new[]
            {
                ("build", queuedBuildId),
                ("build", runningBuildId),
                ("matrix-build", submitted.BuildId),
            }));
            Assert.That(denials.All(audit =>
                audit.Details["permission"] == ManagementPermission.BuildCancel.ToString()), Is.True);
        });

        var systemContext = Context(ManagementPrincipal.System, "system-cancel");
        Assert.That(
            await fixture.Queue.RemoveAsync(systemContext, queuedBuildId, "controller queue stop"),
            Is.True);
        Assert.That(
            await fixture.Builds.CancelBuildAsync(
                systemContext, runningBuildId, "controller running stop"),
            Is.True);
        var queueAfterSystemCancellation = await fixture.QueueStore.GetAsync(queuedBuildId);
        var runningAfterSystemCancellation = await fixture.BuildStore.GetAsync(runningBuildId);
        Assert.Multiple(() =>
        {
            Assert.That(
                queueAfterSystemCancellation!.State,
                Is.EqualTo(BuildQueueItemState.Removed));
            Assert.That(
                runningAfterSystemCancellation!.State,
                Is.EqualTo(TrackedBuildState.CancelRequested));
        });
    }

    private static ManagementRequestContext Context(
        ManagementPrincipal principal,
        string correlationId) =>
        new(principal, correlationId, RequestId: null, Source: "application-authorization-test");

    private static async Task AssertDeniedAsync(Func<Task> command)
    {
        var exception = Assert.ThrowsAsync<ManagementAuthorizationException>(async () =>
            await command());
        Assert.That(exception!.ReasonCode, Is.EqualTo("permission_denied"));
    }

    private static SubmitBuildRequest Request(string requestId, string payloadHash)
    {
        var assignment = new BuildAssignment();
        assignment.Payload.Add(new Blob { Sha256 = payloadHash, FileName = "payload.zip" });
        var request = new SubmitBuildRequest
        {
            RequestId = requestId,
            Project = "Vivarium",
            Configuration = "authorization-tests",
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

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(
            VivariumDatabase database,
            AuditEventStore audits,
            TokenStore tokens,
            AgentStore agentStore,
            BlobStore blobs,
            BuildStore buildStore,
            BuildQueueStore queueStore,
            BuildQueueService queue,
            BuildTracker builds,
            MatrixBuildStore matrixBuilds,
            MatrixBuildSubmissionService submissions,
            MatrixBuildCancellationService cancellations,
            AgentAdministration administration)
        {
            Database = database;
            Audits = audits;
            Tokens = tokens;
            AgentStore = agentStore;
            Blobs = blobs;
            BuildStore = buildStore;
            QueueStore = queueStore;
            Queue = queue;
            Builds = builds;
            MatrixBuilds = matrixBuilds;
            Submissions = submissions;
            Cancellations = cancellations;
            Administration = administration;
        }

        public VivariumDatabase Database { get; }
        public AuditEventStore Audits { get; }
        public TokenStore Tokens { get; }
        public AgentStore AgentStore { get; }
        public BlobStore Blobs { get; }
        public BuildStore BuildStore { get; }
        public BuildQueueStore QueueStore { get; }
        public BuildQueueService Queue { get; }
        public BuildTracker Builds { get; }
        public MatrixBuildStore MatrixBuilds { get; }
        public MatrixBuildSubmissionService Submissions { get; }
        public MatrixBuildCancellationService Cancellations { get; }
        public AgentAdministration Administration { get; }

        public static Task<Fixture> CreateAsync(string rootDir)
        {
            var dataDir = Path.Combine(rootDir, "controller");
            Directory.CreateDirectory(dataDir);
            var database = new VivariumDatabase(dataDir);
            var audits = new AuditEventStore(database);
            var authorization = new ManagementCommandAuthorizer(
                new ManagementAuthorizer(), audits, TimeProvider.System);
            var tokens = new TokenStore(dataDir, database);
            var agentStore = new AgentStore(database);
            var registry = new AgentRegistry(agentStore);
            var blobs = new BlobStore(Path.Combine(dataDir, "blobs"));
            var buildStore = new BuildStore(database);
            var queueStore = new BuildQueueStore(database);
            var queue = new BuildQueueService(
                queueStore, registry, authorization: authorization);
            var builds = new BuildTracker(
                registry, buildStore, queueStore, authorization: authorization);
            var matrixBuilds = new MatrixBuildStore(database);
            var submissions = new MatrixBuildSubmissionService(
                matrixBuilds,
                agentStore,
                blobs,
                queue,
                TimeProvider.System,
                authorization: authorization);
            var cancellations = new MatrixBuildCancellationService(
                matrixBuilds,
                builds,
                queue,
                authorization: authorization);
            var administration = new AgentAdministration(
                registry,
                agentStore,
                buildStore,
                tokens,
                new AgentLifecycleCoordinator(),
                authorization: authorization);
            return Task.FromResult(new Fixture(
                database,
                audits,
                tokens,
                agentStore,
                blobs,
                buildStore,
                queueStore,
                queue,
                builds,
                matrixBuilds,
                submissions,
                cancellations,
                administration));
        }

        public async Task RegisterAgentAsync(string agentId)
        {
            var enrollToken = await Tokens.CreateEnrollTokenAsync();
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
            Assert.That(await Tokens.AdmitAgentAsync(hello), Is.Not.Null);
            await AgentStore.ObserveHelloAsync(hello);
        }

        public async Task<string> PutBlobAsync(byte[] content)
        {
            var hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
            await using var stream = new MemoryStream(content);
            Assert.That(await Blobs.PutAsync(hash, stream, CancellationToken.None), Is.True);
            return hash;
        }

        public Task<int[]> DomainRowCountsAsync() => Database.ReadAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    (SELECT COUNT(*) FROM matrix_builds),
                    (SELECT COUNT(*) FROM builds),
                    (SELECT COUNT(*) FROM build_queue);
                """;
            using var reader = command.ExecuteReader();
            Assert.That(reader.Read(), Is.True);
            return Enumerable.Range(0, 3).Select(reader.GetInt32).ToArray();
        });

        public Task<int> EnrollTokenCountAsync() => Database.ReadAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM enroll_tokens;";
            return Convert.ToInt32(command.ExecuteScalar());
        });

        public async ValueTask DisposeAsync() => await Database.DisposeAsync();
    }
}
