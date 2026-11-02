using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Microsoft.Extensions.Time.Testing;
using Vivarium.Contracts.V1;
using Vivarium.Controller;
using Vivarium.Controller.Agents;
using Vivarium.Controller.Auditing;
using Vivarium.Controller.Blobs;
using Vivarium.Controller.Builds;
using Vivarium.Controller.Management;
using Vivarium.Controller.Persistence;
using Vivarium.Controller.Security;

namespace Vivarium.Tests.TeamCity;

// Native service overlap for P0.REST.2, not /app/rest conformance or a replacement Host.
[NonParallelizable]
[Category("TeamCityCompositionControl")]
public sealed class ProductionSubmissionCompositionTests
{
    [TestCase(false, "submit")]
    [TestCase(true, "submit")]
    [TestCase(false, "denied")]
    [TestCase(true, "denied")]
    [TestCase(false, "retry")]
    [TestCase(true, "retry")]
    [TestCase(false, "cancel")]
    [TestCase(true, "cancel")]
    public async Task Native_submission_has_the_same_effects_in_minimal_and_real_Host_compositions(bool realHost, string scenario)
    {
        var root = Path.Combine(Path.GetTempPath(), "vivarium-submission-composition", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var elapsed = Stopwatch.StartNew();
        var allocated = GC.GetTotalAllocatedBytes(precise: true);
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero));
        VivariumControllerHost? host = null;
        VivariumDatabase? database = null;
        try
        {
            TokenStore tokens; AgentStore agents; BlobStore blobs; MatrixBuildStore matrices;
            MatrixBuildSubmissionService submissions; MatrixBuildCancellationService cancellations;
            AuditEventStore audits;
            if (realHost)
            {
                host = await VivariumControllerHost.StartAsync(new ControllerOptions
                { DataDir = root, Host = "127.0.0.1", Port = 0, TimeProvider = clock });
                database = host.Database; tokens = host.Tokens; agents = host.AgentStore; blobs = host.Blobs;
                matrices = host.MatrixBuildStore; submissions = host.MatrixBuildSubmissions;
                cancellations = host.MatrixBuildCancellations; audits = host.Audits;
            }
            else
            {
                database = new VivariumDatabase(root);
                tokens = new TokenStore(root, database); agents = new AgentStore(database);
                blobs = new BlobStore(Path.Combine(root, "blobs")); matrices = new MatrixBuildStore(database);
                audits = new AuditEventStore(database);
                var authorization = new ManagementCommandAuthorizer(new ManagementAuthorizer(database), audits, clock);
                var registry = new AgentRegistry(agents);
                var queueStore = new BuildQueueStore(database);
                var queue = new BuildQueueService(queueStore, registry, clock, authorization: authorization);
                var tracker = new BuildTracker(registry, new BuildStore(database), queueStore, clock, authorization: authorization);
                submissions = new(matrices, agents, blobs, queue, clock, authorization: authorization);
                cancellations = new(matrices, tracker, queue, clock, authorization);
            }

            // Pre-action external Hello seed via real enrollment/credential resolution and authorization.
            // No fake auth handler and no Agent process/execution claim.
            var hello = new Hello
            {
                AgentId = "owned-composition-agent", SessionId = "owned-composition-session",
                EnrollToken = await tokens.CreateEnrollTokenAsync(),
                Os = new OsInfo { Family = "windows", Arch = "x64", Version = "fixture" }
            };
            hello.Parameters["os.family"] = "windows";
            Assert.That(await tokens.AdmitAgentAsync(hello), Is.Not.Null);
            await agents.ObserveHelloAsync(hello);
            var agentToken = await tokens.AuthorizeAgentAsync(hello.AgentId);
            Assert.That(agentToken, Is.Not.Null);
            var contexts = new ManagementRequestContextFactory(tokens);
            var context = await contexts.FromBearerAsync("Bearer " + (scenario == "denied" ? agentToken : tokens.AdminToken),
                "composition-" + scenario, null, "p0-composition");
            Assert.That(context, Is.Not.Null);
            Assert.That(await contexts.FromBearerAsync("Bearer invalid-fixture-token", null, null, "p0-composition"), Is.Null);

            var payload = Encoding.UTF8.GetBytes("owned queued payload; no process execution claim");
            var hash = Convert.ToHexStringLower(SHA256.HashData(payload));
            await using (var stream = new MemoryStream(payload))
                Assert.That(await blobs.PutAsync(hash, stream, CancellationToken.None), Is.True);
            var request = new SubmitBuildRequest
            {
                RequestId = "same-case-key", Project = "Vivarium", Configuration = "composition",
                DefinitionSnapshot = ByteString.CopyFromUtf8("project: Vivarium")
            };
            var assignment = new BuildAssignment();
            assignment.Payload.Add(new Blob { Sha256 = hash, FileName = "payload.zip" });
            request.Cells.Add(new MatrixBuildCell
            { Name = "windows", Rid = "win-x64", AgentExpression = "os.family == windows", Assignment = assignment });
            var readyMs = elapsed.ElapsedMilliseconds;
            if (scenario == "denied")
            {
                var denied = Assert.ThrowsAsync<ManagementAuthorizationException>(async () =>
                    await submissions.SubmitAsync(context!, request));
                Assert.That(denied!.ReasonCode, Is.EqualTo("permission_denied"));
                Assert.That(await Counts(database), Is.EqualTo(new[] { 0, 0, 0 }));
                Assert.That((await audits.ListAsync()).Any(a => a.Action == "matrix-build.submit" && a.Outcome == AuditOutcome.Denied), Is.True);
            }
            else
            {
                var submitted = await submissions.SubmitAsync(context!, request);
                var snapshot = await matrices.GetSnapshotAsync(submitted.BuildId);
                Assert.That(snapshot, Is.Not.Null);
                Assert.That(snapshot!.Cells, Has.Count.EqualTo(1));
                Assert.That(snapshot.Cells[0].BuildId, Is.Not.EqualTo(submitted.BuildId));
                Assert.That(await Counts(database), Is.EqualTo(new[] { 1, 1, 1 }));
                if (scenario == "retry")
                {
                    Assert.That((await submissions.SubmitAsync(context!, request)).BuildId, Is.EqualTo(submitted.BuildId));
                    request.Configuration = "different";
                    Assert.ThrowsAsync<MatrixRequestConflictException>(async () => await submissions.SubmitAsync(context!, request));
                    Assert.That(await Counts(database), Is.EqualTo(new[] { 1, 1, 1 }));
                }
                if (scenario == "cancel")
                {
                    var cancelled = await cancellations.CancelAsync(context!, submitted.BuildId, "owned stop");
                    Assert.That(cancelled!.State, Is.EqualTo(DurableBuildState.Finished));
                    Assert.That(cancelled.Outcome, Is.EqualTo(BuildOutcome.Cancelled));
                    Assert.That(await Counts(database), Is.EqualTo(new[] { 1, 1, 1 }), "Cancellation retains durable history rows.");
                    Assert.That((await new BuildQueueStore(database).GetAsync(snapshot.Cells[0].BuildId))!.State,
                        Is.EqualTo(BuildQueueItemState.Removed), "Cancelled work is no longer pending despite its retained row.");
                }
            }
            TestContext.Out.WriteLine($"Composition {(realHost ? "real-host" : "minimal-services")}/{scenario}: setup {readyMs}ms; setup+action {elapsed.ElapsedMilliseconds}ms; allocated {GC.GetTotalAllocatedBytes(true) - allocated} bytes; process working set {Environment.WorkingSet} bytes.");
        }
        finally
        {
            if (host is not null) await host.DisposeAsync();
            else if (database is not null) await database.DisposeAsync();
            CurrentController.DeleteDataDirectory(root);
        }
        Assert.That(Directory.Exists(root), Is.False);
        TestContext.Out.WriteLine($"Including disposal/cleanup: {elapsed.ElapsedMilliseconds}ms.");
    }

    private static Task<int[]> Counts(VivariumDatabase database) => database.ReadAsync(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT (SELECT COUNT(*) FROM matrix_builds),(SELECT COUNT(*) FROM builds),(SELECT COUNT(*) FROM build_queue)";
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidDataException("Composition row-count read failed.");
        return Enumerable.Range(0, 3).Select(reader.GetInt32).ToArray();
    });
}
