using System.Net;
using System.Security.Cryptography;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Vivarium.Contracts.V1;
using Vivarium.Controller;
using Vivarium.Controller.Agents;
using Vivarium.Controller.Blobs;
using Vivarium.Controller.Builds;
using Vivarium.Controller.Management;
using Vivarium.Controller.Persistence;
using Vivarium.Controller.Security;

namespace Vivarium.Tests;

[TestFixture]
[NonParallelizable]
public class ControlPlaneTests
{
    private string rootDir = null!;

    [SetUp]
    public void SetUp()
    {
        rootDir = Path.Combine(Path.GetTempPath(), "vivarium-tests", Guid.NewGuid().ToString("N"));
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
            // Best effort on Windows, where Kestrel or SQLite may release a handle a beat later.
        }
    }

    [Test]
    public async Task Invalid_cell_leaves_no_partial_matrix_build_or_queue_rows()
    {
        await using var fixture = await MatrixFixture.CreateAsync(rootDir);
        var presentHash = await fixture.PutBlobAsync("present payload"u8.ToArray());
        var request = Request("atomic-request", presentHash, cellCount: 2);
        request.Cells[1].Assignment.Payload[0].Sha256 = new string('a', 64);

        var exception = Assert.ThrowsAsync<MatrixBuildValidationException>(async () =>
            await fixture.Submissions.SubmitAsync(request));
        Assert.That(exception!.Message, Does.Contain("references missing payload"));

        var counts = await fixture.Database.ReadAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    (SELECT COUNT(*) FROM matrix_builds),
                    (SELECT COUNT(*) FROM matrix_build_cells),
                    (SELECT COUNT(*) FROM builds),
                    (SELECT COUNT(*) FROM build_queue);
                """;
            using var reader = command.ExecuteReader();
            Assert.That(reader.Read(), Is.True);
            return Enumerable.Range(0, 4).Select(reader.GetInt32).ToArray();
        });
        Assert.That(counts, Is.EqualTo(new[] { 0, 0, 0, 0 }));
    }

    [Test]
    public async Task Idempotent_retry_returns_same_ref_and_changed_content_conflicts()
    {
        await using var fixture = await MatrixFixture.CreateAsync(rootDir);
        var hash = await fixture.PutBlobAsync("idempotent payload"u8.ToArray());
        var firstRequest = Request("same-request", hash, reverseParameterOrder: false);
        var retryRequest = Request("same-request", hash, reverseParameterOrder: true);

        var first = await fixture.Submissions.SubmitAsync(firstRequest);
        await fixture.Agents.DeleteAsync("known-windows");
        var retry = await fixture.Submissions.SubmitAsync(retryRequest);
        Assert.That(retry.BuildId, Is.EqualTo(first.BuildId),
            "a retry must not revalidate mutable agent capacity, and map order is not identity");

        var changed = retryRequest.Clone();
        changed.Configuration = "different";
        Assert.ThrowsAsync<MatrixRequestConflictException>(async () =>
            await fixture.Submissions.SubmitAsync(changed));

        var pending = await fixture.QueueStore.ListPendingAsync();
        Assert.That(pending, Has.Count.EqualTo(1));
        var snapshot = await fixture.MatrixBuilds.GetSnapshotAsync(first.BuildId);
        Assert.Multiple(() =>
        {
            Assert.That(snapshot, Is.Not.Null);
            Assert.That(snapshot!.Cells, Has.Count.EqualTo(1));
            Assert.That(snapshot.State, Is.EqualTo(DurableBuildState.Queued));
        });
    }

    [Test]
    public async Task Durable_snapshot_is_available_after_database_restart()
    {
        string matrixBuildId;
        string childBuildId;
        var result = new BuildResult
        {
            Outcome = BuildOutcome.Failed,
            StatusText = "one assertion failed",
        };
        result.Steps.Add(new StepResult { StepIndex = 0, ExitCode = 7 });
        result.Artifacts.Add(new Artifact
        {
            Path = "results/report.trx",
            Sha256 = new string('e', 64),
            Size = 321,
        });
        await using (var fixture = await MatrixFixture.CreateAsync(rootDir))
        {
            var hash = await fixture.PutBlobAsync("restart payload"u8.ToArray());
            var submitted = await fixture.Submissions.SubmitAsync(Request("restart-request", hash));
            matrixBuildId = submitted.BuildId;
            childBuildId = (await fixture.MatrixBuilds.GetSnapshotAsync(matrixBuildId))!.Cells[0].BuildId;
            result.BuildId = childBuildId;
            await fixture.Database.WriteAsync(connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    UPDATE builds SET state = 'FINISHED', result = $result
                    WHERE build_id = $buildId;
                    """;
                command.Parameters.AddWithValue("$result", result.ToByteArray());
                command.Parameters.AddWithValue("$buildId", childBuildId);
                return command.ExecuteNonQuery();
            });
        }

        await using var restartedDatabase = new VivariumDatabase(rootDir);
        var restartedStore = new MatrixBuildStore(restartedDatabase);
        var snapshot = await restartedStore.GetSnapshotAsync(matrixBuildId);

        Assert.Multiple(() =>
        {
            Assert.That(snapshot, Is.Not.Null);
            Assert.That(snapshot!.Project, Is.EqualTo("Vivarium"));
            Assert.That(snapshot.Configuration, Is.EqualTo("tier-2"));
            Assert.That(snapshot.State, Is.EqualTo(DurableBuildState.Finished));
            Assert.That(snapshot.Outcome, Is.EqualTo(BuildOutcome.Failed));
            Assert.That(snapshot.Cells.Single().BuildId, Is.EqualTo(childBuildId));
            Assert.That(snapshot.Cells.Single().Name, Is.EqualTo("windows-1"));
            Assert.That(snapshot.Cells.Single().Rid, Is.EqualTo("win-x64"));
            Assert.That(snapshot.Cells.Single().StatusText, Is.EqualTo("one assertion failed"));
            Assert.That(snapshot.Cells.Single().Steps.Single().ExitCode, Is.EqualTo(7));
            Assert.That(snapshot.Cells.Single().Artifacts.Single().Path, Is.EqualTo("results/report.trx"));
        });

        var artifact = await restartedStore.FindArtifactAsync(matrixBuildId, childBuildId, 0);
        Assert.Multiple(() =>
        {
            Assert.That(artifact, Is.Not.Null);
            Assert.That(artifact!.Sha256, Is.EqualTo(new string('e', 64)));
            Assert.That(artifact.Size, Is.EqualTo(321));
        });
    }

    [Test]
    public async Task Matrix_cancellation_is_atomic_idempotent_and_restart_safe()
    {
        string matrixBuildId;
        string queuedBuildId;
        string runningBuildId;
        string terminalBuildId;
        const string firstReason = "operator stopped the matrix";
        await using (var fixture = await MatrixFixture.CreateAsync(rootDir))
        {
            var hash = await fixture.PutBlobAsync("cancellation payload"u8.ToArray());
            var submitted = await fixture.Submissions.SubmitAsync(
                Request("cancel-matrix", hash, cellCount: 3));
            matrixBuildId = submitted.BuildId;
            var initial = (await fixture.MatrixBuilds.GetSnapshotAsync(matrixBuildId))!;
            queuedBuildId = initial.Cells[0].BuildId;
            runningBuildId = initial.Cells[1].BuildId;
            terminalBuildId = initial.Cells[2].BuildId;

            Assert.That(
                await fixture.QueueStore.TryClaimAsync(
                    queuedBuildId, "reserved-agent", DateTimeOffset.UtcNow),
                Is.True,
                "a claimed-but-unprepared child is still queued and must be cancelled atomically");
            Assert.That(
                await fixture.QueueStore.TryClaimAsync(
                    runningBuildId, "running-agent", DateTimeOffset.UtcNow),
                Is.True);
            Assert.That(
                await fixture.QueueStore.TryPrepareDispatchAsync(
                    runningBuildId,
                    "running-agent",
                    "running-session",
                    DateTimeOffset.UtcNow),
                Is.True);

            var succeeded = new BuildResult
            {
                BuildId = terminalBuildId,
                Outcome = BuildOutcome.Succeeded,
                StatusText = "already passed",
            };
            await fixture.Database.WriteAsync(connection =>
            {
                using var transaction = connection.BeginTransaction();
                using (var queue = connection.CreateCommand())
                {
                    queue.Transaction = transaction;
                    queue.CommandText = """
                        UPDATE build_queue SET
                            state = 'REMOVED', removed_unix_ms = $now,
                            removal_reason = 'finished before cancellation'
                        WHERE build_id = $buildId;
                        """;
                    queue.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    queue.Parameters.AddWithValue("$buildId", terminalBuildId);
                    queue.ExecuteNonQuery();
                }

                using var build = connection.CreateCommand();
                build.Transaction = transaction;
                build.CommandText = """
                    UPDATE builds SET state = 'FINISHED', result = $result
                    WHERE build_id = $buildId;
                    """;
                build.Parameters.AddWithValue("$result", succeeded.ToByteArray());
                build.Parameters.AddWithValue("$buildId", terminalBuildId);
                build.ExecuteNonQuery();
                transaction.Commit();
                return true;
            });

            var first = await fixture.Cancellations.CancelAsync(matrixBuildId, firstReason);
            var firstUpdated = first!.UpdatedUnixMs;
            var retry = await fixture.Cancellations.CancelAsync(
                matrixBuildId, "a later reason must not replace the first");
            var queued = await fixture.QueueStore.GetAsync(queuedBuildId);
            var running = await fixture.BuildStore.GetAsync(runningBuildId);

            Assert.Multiple(() =>
            {
                Assert.That(first.State, Is.EqualTo(DurableBuildState.CancelRequested));
                Assert.That(first.Cells[0].State, Is.EqualTo(DurableBuildState.Finished));
                Assert.That(first.Cells[0].Outcome, Is.EqualTo(BuildOutcome.Cancelled));
                Assert.That(first.Cells[0].StatusText, Is.EqualTo(firstReason));
                Assert.That(first.Cells[1].State, Is.EqualTo(DurableBuildState.CancelRequested));
                Assert.That(first.Cells[2].State, Is.EqualTo(DurableBuildState.Finished));
                Assert.That(first.Cells[2].Outcome, Is.EqualTo(BuildOutcome.Succeeded));
                Assert.That(first.Cells[2].StatusText, Is.EqualTo("already passed"));
                Assert.That(queued!.State, Is.EqualTo(BuildQueueItemState.Removed));
                Assert.That(queued.RemovalReason, Is.EqualTo(firstReason));
                Assert.That(running!.CancellationReason, Is.EqualTo(firstReason));
                Assert.That(retry!.UpdatedUnixMs, Is.EqualTo(firstUpdated),
                    "an idempotent retry must not rewrite durable timestamps");
            });
        }

        await using var restartedDatabase = new VivariumDatabase(rootDir);
        var restartedMatrixStore = new MatrixBuildStore(restartedDatabase);
        var restartedBuildStore = new BuildStore(restartedDatabase);
        var restartedQueueStore = new BuildQueueStore(restartedDatabase);
        var restartedAgents = new AgentStore(restartedDatabase);
        var restartedTracker = new BuildTracker(
            new AgentRegistry(restartedAgents), restartedBuildStore, restartedQueueStore);
        await restartedTracker.InitializeAsync();

        var restartedSnapshot = await restartedMatrixStore.GetSnapshotAsync(matrixBuildId);
        var restoredRunning = await restartedBuildStore.GetAsync(runningBuildId);
        Assert.Multiple(() =>
        {
            Assert.That(restartedSnapshot!.Cells.Single(cell => cell.BuildId == queuedBuildId).Outcome,
                Is.EqualTo(BuildOutcome.Cancelled));
            Assert.That(restartedSnapshot.Cells.Single(cell => cell.BuildId == terminalBuildId).Outcome,
                Is.EqualTo(BuildOutcome.Succeeded));
            Assert.That(restoredRunning!.State, Is.EqualTo(TrackedBuildState.CancelRequested));
            Assert.That(restoredRunning.CancellationReason, Is.EqualTo(firstReason));
            Assert.That(restartedTracker.GetSnapshots().Single().State,
                Is.EqualTo(TrackedBuildState.CancelRequested));
        });
    }

    [Test]
    public async Task Historical_agent_provenance_survives_restart_and_registration_changes()
    {
        string matrixBuildId;
        string childBuildId;
        await using (var fixture = await MatrixFixture.CreateAsync(rootDir))
        {
            await fixture.Agents.RenameAsync("known-windows", "original-agent-name");
            var assignmentHello = new Hello { AgentId = "known-windows" };
            assignmentHello.Parameters["os.family"] = "windows";
            assignmentHello.Parameters["software.browser"] = "1.2";
            await fixture.Agents.ObserveHelloAsync(assignmentHello);
            await fixture.Agents.SetCustomParameterAsync(
                "known-windows", "pool", "hardware-lab");

            var hash = await fixture.PutBlobAsync("provenance payload"u8.ToArray());
            var submitted = await fixture.Submissions.SubmitAsync(
                Request("provenance-request", hash));
            matrixBuildId = submitted.BuildId;
            childBuildId = (await fixture.MatrixBuilds.GetSnapshotAsync(matrixBuildId))!
                .Cells.Single().BuildId;

            var now = DateTimeOffset.UtcNow;
            Assert.That(
                await fixture.QueueStore.TryClaimAsync(childBuildId, "known-windows", now),
                Is.True);
            Assert.That(
                await fixture.QueueStore.TryPrepareDispatchAsync(
                    childBuildId,
                    "known-windows",
                    "provenance-session",
                    now,
                    "original-agent-name",
                    assignmentHello.Parameters.ToDictionary(),
                    new Dictionary<string, string> { ["pool"] = "hardware-lab" }),
                Is.True);

            var result = new BuildResult
            {
                BuildId = childBuildId,
                SessionId = "provenance-session",
                Outcome = BuildOutcome.Succeeded,
                StatusText = "passed",
            };
            await fixture.Database.WriteAsync(connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    UPDATE builds SET state = 'FINISHED', result = $result
                    WHERE build_id = $buildId;
                    """;
                command.Parameters.AddWithValue("$result", result.ToByteArray());
                command.Parameters.AddWithValue("$buildId", childBuildId);
                return command.ExecuteNonQuery();
            });

            await fixture.Agents.RenameAsync("known-windows", "renamed-agent");
            var laterHello = new Hello { AgentId = "known-windows" };
            laterHello.Parameters["os.family"] = "linux";
            laterHello.Parameters["software.browser"] = "2.0";
            await fixture.Agents.ObserveHelloAsync(laterHello);
            await fixture.Agents.SetCustomParameterAsync(
                "known-windows", "pool", "cloud-lab");
            await fixture.Agents.DeleteAsync("known-windows");
        }

        await using var restartedDatabase = new VivariumDatabase(rootDir);
        var restartedAgents = new AgentStore(restartedDatabase);
        var restartedStore = new MatrixBuildStore(restartedDatabase);
        var snapshot = await restartedStore.GetSnapshotAsync(matrixBuildId);
        var cell = snapshot!.Cells.Single();
        var deletedAgent = await restartedAgents.GetAsync("known-windows");

        Assert.Multiple(() =>
        {
            Assert.That(deletedAgent, Is.Null);
            Assert.That(cell.BuildId, Is.EqualTo(childBuildId));
            Assert.That(cell.State, Is.EqualTo(DurableBuildState.Finished));
            Assert.That(cell.AgentId, Is.EqualTo("known-windows"));
            Assert.That(cell.AgentName, Is.EqualTo("original-agent-name"));
            Assert.That(cell.AgentParameters["os.family"], Is.EqualTo("windows"));
            Assert.That(cell.AgentParameters["software.browser"], Is.EqualTo("1.2"));
            Assert.That(cell.AgentParameters.Values, Does.Not.Contain("linux"));
            Assert.That(cell.AgentParameters.Values, Does.Not.Contain("2.0"));
            Assert.That(cell.AgentCustomParameters["pool"], Is.EqualTo("hardware-lab"));
            Assert.That(cell.AgentCustomParameters.Values, Does.Not.Contain("cloud-lab"));
        });
    }

    [Test]
    public async Task Recent_matrix_builds_are_newest_first_and_bounded()
    {
        await using var fixture = await MatrixFixture.CreateAsync(rootDir);
        var hash = await fixture.PutBlobAsync("recent payload"u8.ToArray());
        var older = await fixture.Submissions.SubmitAsync(Request("recent-older", hash));
        var newer = await fixture.Submissions.SubmitAsync(Request("recent-newer", hash));
        await fixture.Database.WriteAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE matrix_builds SET created_unix_ms = CASE matrix_build_id
                    WHEN $older THEN 100
                    WHEN $newer THEN 200
                    END
                WHERE matrix_build_id IN ($older, $newer);
                """;
            command.Parameters.AddWithValue("$older", older.BuildId);
            command.Parameters.AddWithValue("$newer", newer.BuildId);
            return command.ExecuteNonQuery();
        });

        var recent = await fixture.MatrixBuilds.ListRecentAsync(1);

        Assert.Multiple(() =>
        {
            Assert.That(recent, Has.Count.EqualTo(1));
            Assert.That(recent[0].MatrixBuildId, Is.EqualTo(newer.BuildId));
            Assert.That(recent[0].Project, Is.EqualTo("Vivarium"));
            Assert.That(recent[0].CellCount, Is.EqualTo(1));
            Assert.That(recent[0].FinishedCellCount, Is.Zero);
        });
    }

    [Test]
    public async Task Control_plane_and_blob_endpoints_enforce_independent_scopes()
    {
        await using var controller = await StartControllerAsync();
        var agentToken = await RegisterAgentAsync(controller, "agent-token-owner", authorize: true);
        await RegisterAgentAsync(controller, "pending-agent", authorize: false);
        using var channel = PinnedChannel(controller);
        var client = new Vivarium.Contracts.V1.ControlPlane.ControlPlaneClient(channel);
        var emptyHashes = new BlobHashes();

        await client.MissingBlobsAsync(emptyHashes, Headers(controller.Tokens.SubmitToken));
        await client.MissingBlobsAsync(emptyHashes, Headers(controller.Tokens.AdminToken));
        AssertRpcCode(
            () => client.MissingBlobsAsync(emptyHashes, Headers(agentToken)).ResponseAsync,
            StatusCode.PermissionDenied);
        AssertRpcCode(
            () => client.MissingBlobsAsync(emptyHashes, Headers("invalid")).ResponseAsync,
            StatusCode.Unauthenticated);
        AssertRpcCode(
            () => client.MissingBlobsAsync(emptyHashes).ResponseAsync,
            StatusCode.Unauthenticated);

        var duplicateMissing = new BlobHashes();
        duplicateMissing.Sha256.Add(new string('c', 64));
        duplicateMissing.Sha256.Add(new string('d', 64));
        duplicateMissing.Sha256.Add(new string('c', 64));
        var missing = await client.MissingBlobsAsync(
            duplicateMissing, Headers(controller.Tokens.SubmitToken));
        Assert.That(missing.Sha256, Is.EqualTo(new[] { new string('c', 64), new string('d', 64) }));

        AssertRpcCode(
            () => client.ListAgentsAsync(
                new ListAgentsRequest(), Headers(controller.Tokens.SubmitToken)).ResponseAsync,
            StatusCode.PermissionDenied);
        var listed = await client.ListAgentsAsync(
            new ListAgentsRequest(), Headers(controller.Tokens.AdminToken));
        Assert.That(listed.Agents.Select(agent => agent.AgentId),
            Does.Contain("pending-agent"));

        AssertRpcCode(
            () => client.AuthorizeAgentAsync(
                new AgentRef { AgentId = "pending-agent" },
                Headers(controller.Tokens.SubmitToken)).ResponseAsync,
            StatusCode.PermissionDenied);
        var authorized = await client.AuthorizeAgentAsync(
            new AgentRef { AgentId = "pending-agent" },
            Headers(controller.Tokens.AdminToken));
        Assert.That(authorized.Authorized, Is.True);

        var payloadHash = await PutBlobAsync(controller.Blobs, "scope cancellation"u8.ToArray());
        var submitted = await client.SubmitBuildAsync(
            Request("scope-cancel", payloadHash), Headers(controller.Tokens.SubmitToken));
        var cancelRequest = new CancelBuildRequest
        {
            BuildId = submitted.BuildId,
            Reason = "scope test stop",
        };
        AssertRpcCode(
            () => client.CancelBuildAsync(cancelRequest, Headers(agentToken)).ResponseAsync,
            StatusCode.PermissionDenied);
        var cancelled = await client.CancelBuildAsync(
            cancelRequest, Headers(controller.Tokens.SubmitToken));
        var adminRetry = await client.CancelBuildAsync(
            new CancelBuildRequest
            {
                BuildId = submitted.BuildId,
                Reason = "admin retry must not replace the first reason",
            },
            Headers(controller.Tokens.AdminToken));
        Assert.Multiple(() =>
        {
            Assert.That(cancelled.State, Is.EqualTo(DurableBuildState.Finished));
            Assert.That(cancelled.Outcome, Is.EqualTo(BuildOutcome.Cancelled));
            Assert.That(adminRetry.Cells.Single().StatusText, Is.EqualTo("scope test stop"));
        });
        AssertRpcCode(
            () => client.CancelBuildAsync(
                new CancelBuildRequest { BuildId = "unknown-matrix" },
                Headers(controller.Tokens.SubmitToken)).ResponseAsync,
            StatusCode.NotFound);

        var missingHash = new string('b', 64);
        using var http = PinnedHttpClient(controller);
        foreach (var token in new[] { agentToken, controller.Tokens.SubmitToken, controller.Tokens.AdminToken })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/blobs/{missingHash}");
            request.Headers.Authorization = new("Bearer", token);
            using var response = await http.SendAsync(request);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        }
    }

    [Test]
    public async Task Watch_emits_immediate_full_snapshot_and_closes_after_terminal_change()
    {
        await using var controller = await StartControllerAsync();
        await RegisterAgentAsync(controller, "known-windows", authorize: false);
        var hash = await PutBlobAsync(controller.Blobs, "watch payload"u8.ToArray());
        using var channel = PinnedChannel(controller);
        var client = new Vivarium.Contracts.V1.ControlPlane.ControlPlaneClient(channel);
        var submitted = await client.SubmitBuildAsync(
            Request("watch-request", hash), Headers(controller.Tokens.SubmitToken));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var watch = client.WatchBuild(
            submitted, Headers(controller.Tokens.SubmitToken), cancellationToken: timeout.Token);
        Assert.That(await watch.ResponseStream.MoveNext(timeout.Token), Is.True);
        var initial = watch.ResponseStream.Current;
        Assert.Multiple(() =>
        {
            Assert.That(initial.Build.BuildId, Is.EqualTo(submitted.BuildId));
            Assert.That(initial.State, Is.EqualTo(DurableBuildState.Queued));
            Assert.That(initial.Cells, Has.Count.EqualTo(1));
        });

        Assert.That(
            await controller.BuildQueue.RemoveAsync(initial.Cells[0].BuildId, "cancelled from test"),
            Is.True);
        Assert.That(await watch.ResponseStream.MoveNext(timeout.Token), Is.True);
        var terminal = watch.ResponseStream.Current;
        Assert.Multiple(() =>
        {
            Assert.That(terminal.State, Is.EqualTo(DurableBuildState.Finished));
            Assert.That(terminal.Outcome, Is.EqualTo(BuildOutcome.Cancelled));
            Assert.That(terminal.Cells[0].StatusText, Is.EqualTo("cancelled from test"));
        });
        Assert.That(await watch.ResponseStream.MoveNext(timeout.Token), Is.False);
    }

    private Task<VivariumControllerHost> StartControllerAsync() =>
        VivariumControllerHost.StartAsync(new ControllerOptions
        {
            DataDir = Path.Combine(rootDir, "controller"),
            Host = "127.0.0.1",
            Port = 0,
        });

    private static SubmitBuildRequest Request(
        string requestId,
        string payloadHash,
        int cellCount = 1,
        bool reverseParameterOrder = false)
    {
        var request = new SubmitBuildRequest
        {
            RequestId = requestId,
            Project = "Vivarium",
            Configuration = "tier-2",
            DefinitionSnapshot = ByteString.CopyFromUtf8("project: Vivarium"),
        };
        for (var index = 0; index < cellCount; index++)
        {
            var assignment = new BuildAssignment();
            assignment.Payload.Add(new Blob { Sha256 = payloadHash, FileName = "payload.zip" });
            if (reverseParameterOrder)
            {
                assignment.Parameters["b"] = "2";
                assignment.Parameters["a"] = "1";
            }
            else
            {
                assignment.Parameters["a"] = "1";
                assignment.Parameters["b"] = "2";
            }

            request.Cells.Add(new MatrixBuildCell
            {
                Name = $"windows-{index + 1}",
                AgentExpression = "os.family == windows",
                Rid = "win-x64",
                Assignment = assignment,
            });
        }

        return request;
    }

    private static async Task<string> RegisterAgentAsync(
        VivariumControllerHost controller,
        string agentId,
        bool authorize)
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
        if (!authorize)
        {
            return string.Empty;
        }

        return await controller.Tokens.AuthorizeAgentAsync(agentId)
            ?? throw new AssertionException("newly registered agent did not receive a token");
    }

    private static Metadata Headers(string token) =>
        new() { { "authorization", $"Bearer {token}" } };

    private static void AssertRpcCode(Func<Task> call, StatusCode expected)
    {
        var exception = Assert.ThrowsAsync<RpcException>(async () => await call());
        Assert.That(exception!.StatusCode, Is.EqualTo(expected));
    }

    private static GrpcChannel PinnedChannel(VivariumControllerHost controller) =>
        GrpcChannel.ForAddress(controller.Url, new GrpcChannelOptions
        {
            HttpHandler = PinnedHandler(controller),
        });

    private static HttpClient PinnedHttpClient(VivariumControllerHost controller) =>
        new(PinnedHandler(controller)) { BaseAddress = new Uri(controller.Url) };

    private static HttpClientHandler PinnedHandler(VivariumControllerHost controller) => new()
    {
        ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
            certificate != null &&
            Convert.ToHexString(SHA256.HashData(certificate.RawData)).Equals(
                controller.Certificate.FingerprintSha256, StringComparison.OrdinalIgnoreCase),
    };

    private static async Task<string> PutBlobAsync(BlobStore blobs, byte[] content)
    {
        var hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        await using var stream = new MemoryStream(content);
        Assert.That(await blobs.PutAsync(hash, stream, CancellationToken.None), Is.True);
        return hash;
    }

    private sealed class MatrixFixture : IAsyncDisposable
    {
        public required VivariumDatabase Database { get; init; }
        public required BlobStore Blobs { get; init; }
        public required AgentStore Agents { get; init; }
        public required BuildQueueStore QueueStore { get; init; }
        public required BuildStore BuildStore { get; init; }
        public required MatrixBuildStore MatrixBuilds { get; init; }
        public required MatrixBuildSubmissionService Submissions { get; init; }
        public required MatrixBuildCancellationService Cancellations { get; init; }

        public static async Task<MatrixFixture> CreateAsync(string dataDir)
        {
            var database = new VivariumDatabase(dataDir);
            var tokens = new TokenStore(dataDir, database);
            var agents = new AgentStore(database);
            var registry = new AgentRegistry(agents);
            var queueStore = new BuildQueueStore(database);
            var queue = new BuildQueueService(queueStore, registry);
            var buildStore = new BuildStore(database);
            var buildTracker = new BuildTracker(registry, buildStore, queueStore);
            await buildTracker.InitializeAsync();
            var blobs = new BlobStore(Path.Combine(dataDir, "blobs"));
            var matrixBuilds = new MatrixBuildStore(database);
            var submissions = new MatrixBuildSubmissionService(
                matrixBuilds, agents, blobs, queue, TimeProvider.System);
            var cancellations = new MatrixBuildCancellationService(
                matrixBuilds, buildTracker, queue);

            var enrollToken = await tokens.CreateEnrollTokenAsync();
            var hello = new Hello
            {
                AgentId = "known-windows",
                EnrollToken = enrollToken,
                SessionId = "fixture-session",
                Os = new OsInfo { Family = "windows", Arch = "x64", Version = "test" },
            };
            hello.Parameters["hostname"] = "known-windows";
            hello.Parameters["os.family"] = "windows";
            Assert.That(await tokens.AdmitAgentAsync(hello), Is.Not.Null);
            await agents.ObserveHelloAsync(hello);

            return new MatrixFixture
            {
                Database = database,
                Blobs = blobs,
                Agents = agents,
                QueueStore = queueStore,
                BuildStore = buildStore,
                MatrixBuilds = matrixBuilds,
                Submissions = submissions,
                Cancellations = cancellations,
            };
        }

        public Task<string> PutBlobAsync(byte[] content) =>
            ControlPlaneTests.PutBlobAsync(Blobs, content);

        public ValueTask DisposeAsync() => Database.DisposeAsync();
    }
}
