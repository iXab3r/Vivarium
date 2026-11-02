using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Google.Protobuf;
using Grpc.Net.Client;
using Npgsql;
using Vivarium.Contracts.V1;
using Vivarium.Controller.Builds;
using Vivarium.Controller.Management;
using Vivarium.Controller.Security;
using Vivarium.Tests.TeamCity;

namespace Vivarium.DefinitionProof;

[NonParallelizable]
public sealed class BuildIdentityPhysicalTests
{
    [Test]
    public async Task Durable_numeric_identity_preserves_real_native_matrix_aliases_without_wrapping_ordinary_Builds()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Vivarium.slnx"))) directory = directory.Parent;
        var root = directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
        var run = Path.Combine(root, "artifacts/us013-identity", "physical-run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(run);
        static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
        var inputs = Directory.GetFiles(Path.Combine(root, "tests/Vivarium.DefinitionProof"), "*.cs")
            .Concat(Directory.GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
                .Where(path => !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) &&
                    !path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)))
            .Concat(Directory.GetFiles(Path.Combine(root, "src/Vivarium.Contracts/protos"), "*.proto", SearchOption.AllDirectories))
            .Concat(Directory.GetFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories))
            .Append(Path.Combine(root, "tests/Vivarium.DefinitionProof/Vivarium.DefinitionProof.csproj"))
            .Append(Path.Combine(root, "tests/Vivarium.Tests/TeamCity/CurrentController.cs"))
            .Append(Path.Combine(root, "global.json")).Append(Path.Combine(root, "Directory.Build.props"))
            .ToDictionary(path => Path.GetRelativePath(root, path), Hash);
        var postgres = new OwnedPostgres();
        var controller = new CurrentController();
        var observations = new List<object>();
        var passed = false;
        try
        {
            await controller.StartAsync();
            var host = controller.Host;
            var hello = new Hello
            {
                AgentId = "owned-identity-fixture-agent", SessionId = "owned-identity-fixture-session",
                EnrollToken = await host.Tokens.CreateEnrollTokenAsync(),
                Os = new OsInfo { Family = "windows", Arch = "x64", Version = "fixture" },
            };
            hello.Parameters["os.family"] = "windows";
            // Enroll through the actual transport; do not expose product internals to this study.
            await ObserveHelloAsync(hello);
            var agentToken = await host.Tokens.AuthorizeAgentAsync(hello.AgentId);
            Assert.That(agentToken, Is.Not.Null);
            var firstAgent = (await host.AgentStore.GetAsync(hello.AgentId))!;
            async Task ObserveHelloAsync(Hello message)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var channel = GrpcChannel.ForAddress(host.Url, new GrpcChannelOptions
                {
                    HttpHandler = new HttpClientHandler
                    {
                        ServerCertificateCustomValidationCallback = (_, certificate, _, _) => certificate is not null &&
                            Convert.ToHexString(SHA256.HashData(certificate.RawData)).Equals(
                                host.Certificate.FingerprintSha256, StringComparison.OrdinalIgnoreCase),
                    },
                });
                using var session = new AgentHub.AgentHubClient(channel).Session(cancellationToken: timeout.Token);
                await session.RequestStream.WriteAsync(new AgentMsg { Hello = message }, timeout.Token);
                Assert.That(await session.ResponseStream.MoveNext(timeout.Token), Is.True);
                Assert.That(session.ResponseStream.Current.MsgCase, Is.EqualTo(ControllerMsg.MsgOneofCase.Welcome));
                await session.RequestStream.CompleteAsync();
                while (await session.ResponseStream.MoveNext(timeout.Token))
                    Assert.That(session.ResponseStream.Current.MsgCase, Is.Not.EqualTo(ControllerMsg.MsgOneofCase.Build), "This bounded protocol actor never executes work.");
            }
            var context = await new ManagementRequestContextFactory(host.Tokens).FromBearerAsync(
                "Bearer " + host.Tokens.AdminToken, "identity-fixture", null, "p0-identity");
            Assert.That(context, Is.Not.Null);
            var payload = Encoding.UTF8.GetBytes("owned queued payload; no Agent execution claim");
            var digest = Convert.ToHexStringLower(SHA256.HashData(payload));
            await using (var stream = new MemoryStream(payload)) Assert.That(await host.Blobs.PutAsync(digest, stream, CancellationToken.None), Is.True);
            var request = new SubmitBuildRequest
            {
                RequestId = "identity-same-key", Project = "Vivarium", Configuration = "identity-matrix",
                DefinitionSnapshot = ByteString.CopyFromUtf8("project: Vivarium"),
            };
            foreach (var name in new[] { "first", "second" })
            {
                var assignment = new BuildAssignment();
                assignment.Payload.Add(new Blob { Sha256 = digest, FileName = "payload.zip" });
                request.Cells.Add(new MatrixBuildCell { Name = name, Rid = "win-x64", AgentExpression = "os.family == windows", Assignment = assignment });
            }
            var submitted = await host.MatrixBuildSubmissions.SubmitAsync(context!, request);
            var snapshot = (await host.MatrixBuildStore.GetSnapshotAsync(submitted.BuildId))!;
            Assert.That(snapshot.Cells, Has.Count.EqualTo(2));
            var aliases = snapshot.Cells.Select(cell => cell.BuildId).Prepend(submitted.BuildId).ToArray();
            Assert.That(aliases.Distinct().Count(), Is.EqualTo(3));
            Assert.That(aliases.All(alias => Guid.TryParseExact(alias, "N", out _)), Is.True);
            using var response = await controller.Http.GetAsync("/api/v1/builds/" + submitted.BuildId);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            var nativeJson = await response.Content.ReadAsStringAsync();
            await File.WriteAllTextAsync(Path.Combine(run, "native-build.json"), nativeJson);
            var native = JsonNode.Parse(nativeJson)!;
            Assert.That(native["id"]!.GetValue<string>(), Is.EqualTo(aliases[0]));
            Assert.That(native["children"]!.AsArray().Select(cell => cell!["id"]!.GetValue<string>()), Is.EqualTo(aliases.Skip(1)));
            var queue = new BuildQueueStore(host.Database);
            var assignments = new Dictionary<string, byte[]>();
            foreach (var alias in aliases.Skip(1)) assignments.Add(alias, (await queue.GetAsync(alias))!.Assignment.ToByteArray());

            await postgres.StartAsync();
            var study = new BuildIdentityStudy(postgres.ConnectionString);
            await study.CreateSchemaAsync();
            var agentIdentity = await study.RegisterStandaloneAgentAsync(hello.AgentId);
            var secondAgentIdentity = await study.RegisterStandaloneAgentAsync("another-owned-standalone");
            Assert.That(secondAgentIdentity.Id, Is.Not.EqualTo(agentIdentity.Id));
            Assert.That(secondAgentIdentity.TypeId, Is.Not.EqualTo(agentIdentity.TypeId), "Standalone Agents have distinct types; this does not model cloud/image sharing.");
            var registrations = await Task.WhenAll(Enumerable.Range(0, 4)
                .Select(_ => study.RegisterMatrixAsync(aliases[0], aliases[1..])));
            var identities = registrations[0];
            Assert.That(registrations.All(rows => rows.SequenceEqual(identities)), Is.True, "Concurrent registration retains one identity set.");
            Assert.That(identities.Select(row => row.NativeAlias), Is.EqualTo(aliases));
            Assert.That(identities.Select(row => row.Kind), Is.EqualTo(new[] { "native_matrix", "normal", "normal" }));
            Assert.That(identities.Skip(1).All(row => row.Parent == identities[0].Id), Is.True);
            Assert.That(identities.Select(row => row.Id).Distinct().Count(), Is.EqualTo(3));
            Assert.That(identities.All(row => row.Id > 0L && row.Id <= 9007199254740991L), Is.True, "This small allocation fits the pinned CLI's numeric-ID range; not a CLI execution or int64-wide claim.");
            // Map only declared identity fields. User configuration values are never rewritten.
            var canonical = native.DeepClone();
            canonical["id"] = identities[0].Id;
            canonical["url"] = "/api/v1/builds/" + identities[0].Id;
            for (var i = 0; i < identities.Count - 1; i++) canonical["children"]![i]!["id"] = identities[i + 1].Id;
            var roundTrip = canonical.DeepClone();
            roundTrip["id"] = identities[0].NativeAlias;
            roundTrip["url"] = "/api/v1/builds/" + identities[0].NativeAlias;
            for (var i = 0; i < identities.Count - 1; i++) roundTrip["children"]![i]!["id"] = identities[i + 1].NativeAlias;
            Assert.That(JsonNode.DeepEquals(roundTrip, native), Is.True);

            var ordinary = await study.AllocateIndependentAsync("normal");
            var composite = await study.AllocateIndependentAsync("composite");
            Assert.That(ordinary.Parent, Is.Null, "An ordinary Build has no synthetic matrix/composite wrapper.");
            Assert.That(composite.Parent, Is.Null);
            Assert.That((await study.ReadAllAsync()).Count, Is.EqualTo(5));
            Assert.ThrowsAsync<InvalidOperationException>(async () => await study.RegisterMatrixAsync(aliases[0], [aliases[2], aliases[1]]));
            var collision = Assert.ThrowsAsync<PostgresException>(async () => await study.RegisterMatrixAsync(Guid.NewGuid().ToString("N"), [aliases[1]]));
            Assert.That(collision!.SqlState, Is.EqualTo(PostgresErrorCodes.UniqueViolation));
            Assert.That((await study.ReadAllAsync()).Count, Is.EqualTo(5), "Failed child allocation rolls back the new parent as well.");
            await using (var connection = new NpgsqlConnection(postgres.ConnectionString))
            {
                await connection.OpenAsync();
                await using var malformed = new NpgsqlCommand("INSERT INTO study_build_identity(kind,parent_id) VALUES ('normal',$1)", connection);
                malformed.Parameters.AddWithValue(identities[0].Id);
                var rejected = Assert.ThrowsAsync<PostgresException>(async () => await malformed.ExecuteNonQueryAsync());
                Assert.That(rejected!.SqlState, Is.EqualTo(PostgresErrorCodes.CheckViolation), "NULL must not bypass a parent/ordinal invariant.");
            }
            var beforeRestart = await study.ReadAllAsync();
            await postgres.RestartAsync();
            study = new BuildIdentityStudy(postgres.ConnectionString);
            Assert.That(await study.ReadAllAsync(), Is.EqualTo(beforeRestart));
            Assert.That(await study.RegisterStandaloneAgentAsync(hello.AgentId), Is.EqualTo(agentIdentity));
            Assert.That(await study.RegisterMatrixAsync(aliases[0], aliases[1..]), Is.EqualTo(identities));
            Assert.That((await study.AllocateIndependentAsync("normal")).Id, Is.GreaterThan(beforeRestart.Max(row => row.Id)));

            Assert.That((await host.MatrixBuildSubmissions.SubmitAsync(context!, request)).BuildId, Is.EqualTo(submitted.BuildId));
            foreach (var identity in identities.Skip(1))
            {
                var assignment = (await queue.GetAsync(identity.NativeAlias!))!.Assignment;
                Assert.That(assignment.BuildId, Is.EqualTo(identity.NativeAlias));
                Assert.That(assignment.ToByteArray(), Is.EqualTo(assignments[identity.NativeAlias!]));
                Assert.That(await host.MatrixBuildStore.FindMatrixBuildIdForChildAsync(identity.NativeAlias!), Is.EqualTo(aliases[0]));
            }
            using var numericOnOldHost = await controller.Http.GetAsync("/api/v1/builds/" + identities[0].Id);
            Assert.That(numericOnOldHost.StatusCode, Is.EqualTo(HttpStatusCode.NotFound), "The legacy Host has no candidate numeric alias adapter; do not claim it does.");
            var configuration = new DirectoryInfo(TestContext.CurrentContext.TestDirectory).Parent!.Name;
            var cli = Path.Combine(root, "src/Vivarium.Cli/bin", configuration, "net10.0/Vivarium.Cli.dll");
            var cliResult = await ProofProcess.RunAsync("dotnet", [cli, "cancel", aliases[0], "--reason", "Owned identity proof"], root,
                new Dictionary<string, string>
                {
                    ["VIVARIUM_URL"] = host.Url,
                    ["VIVARIUM_CERT_FINGERPRINT"] = host.Certificate.FingerprintSha256,
                    ["VIVARIUM_TOKEN"] = host.Tokens.AdminToken,
                });
            Assert.That(cliResult.Text, Does.Contain("Cancellation requested for matrix build " + aliases[0]));
            Assert.That(cliResult.Text, Does.Not.Contain(host.Tokens.AdminToken));
            await File.WriteAllBytesAsync(Path.Combine(run, "native-cli-cancel.txt"), cliResult.Output);
            var cancelled = (await host.MatrixBuildStore.GetSnapshotAsync(aliases[0]))!;
            Assert.That(cancelled.Build.BuildId, Is.EqualTo(aliases[0]));
            Assert.That(cancelled.Cells.Select(cell => cell.BuildId), Is.EqualTo(aliases.Skip(1)));
            Assert.That(cancelled.Cells.All(cell => cell.Outcome == BuildOutcome.Cancelled), Is.True);
            Assert.That(await study.RegisterMatrixAsync(aliases[0], aliases[1..]), Is.EqualTo(identities));
            var reconnect = hello.Clone();
            reconnect.SessionId = "owned-identity-reconnected-session";
            reconnect.EnrollToken = "";
            reconnect.AuthToken = agentToken!;
            await ObserveHelloAsync(reconnect);
            var reconnectedAgent = (await host.AgentStore.GetAsync(hello.AgentId))!;
            Assert.That(reconnectedAgent.ConnectionGeneration, Is.GreaterThan(firstAgent.ConnectionGeneration));
            Assert.That(reconnectedAgent.CredentialGeneration, Is.EqualTo(firstAgent.CredentialGeneration));
            Assert.That(await study.RegisterStandaloneAgentAsync(reconnectedAgent.AgentId), Is.EqualTo(agentIdentity));
            using var nativeAgentResponse = await controller.Http.GetAsync("/api/v1/agents/" + hello.AgentId);
            Assert.That(nativeAgentResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            using var nativeAgent = JsonDocument.Parse(await nativeAgentResponse.Content.ReadAsStringAsync());
            Assert.That(nativeAgent.RootElement.GetProperty("id").GetString(), Is.EqualTo(agentIdentity.NativeAlias));
            observations.Add(new { scenario = "native-matrix-numeric-alias-roundtrip", identities, ordinary, composite, concurrentRegistrations = 4,
                sourceNativeJsonSha256 = Hash(Path.Combine(run, "native-build.json")), unchangedAssignmentPayloads = assignments.Count,
                collisionsRollBack = true, nullInvariantRejected = true, restartPreserved = true, legacyNumericHttpStatus = 404,
                nativeCli = new { assemblySha256 = Hash(cli), exitCode = cliResult.ExitCode, outputSha256 = Hash(Path.Combine(run, "native-cli-cancel.txt")), aliasesRetained = true },
                agentIdentity, secondAgentIdentity, firstConnectionGeneration = firstAgent.ConnectionGeneration,
                reconnectedAgent.ConnectionGeneration, credentialGenerationPreserved = true });
            passed = true;
        }
        finally
        {
            try { await controller.DisposeAsync(); }
            finally { await postgres.DisposeAsync(); }
            var stable = inputs.All(item => File.Exists(Path.Combine(root, item.Key)) && Hash(Path.Combine(root, item.Key)) == item.Value);
            await File.WriteAllTextAsync(Path.Combine(run, "receipt.json"), JsonSerializer.Serialize(new
            {
                result = passed && stable ? "PASS" : "FAILED", scope = "P0 numeric/native identity feasibility, not production adapter or TeamCity conformance",
                postgres = postgres.ServerVersion, inputsStable = stable, inputs, observations,
                limits = "Real legacy native submission/readback, queued protobuf assignments and unchanged native viv-cli cancellation; isolated PostgreSQL identity study. Enrollment uses a bounded protocol fixture, not a real Agent process. No compatible facade/TeamCity CLI/ordinary execution, replacement Host, logs/artifact/SSE preservation, imported-ID or queue-replacement acceptance."
            }, new JsonSerializerOptions { WriteIndented = true }));
            Assert.That(stable, Is.True);
            TestContext.Progress.WriteLine("Identity study receipt: " + Path.Combine(run, "receipt.json"));
        }
    }
}
