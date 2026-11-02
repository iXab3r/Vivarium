using System.Diagnostics;
using System.Security.Cryptography;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Time.Testing;
using Vivarium.Contracts.V1;
using Vivarium.Controller;
using Vivarium.Controller.Agents;
using Vivarium.Controller.Agents.Compatibility;
using Vivarium.Controller.Builds;
using Vivarium.Controller.Persistence;
using Vivarium.Controller.Security;

namespace Vivarium.Tests.TeamCity;

// Scripted external protocol actor: never runs a process or decides product state.
[NonParallelizable]
[Category("TeamCityCompositionControl")]
public sealed class AgentDeliveryCompositionTests
{
    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(true, true)]
    public async Task Assignment_delivery_result_replay_and_missing_ack_agree_at_service_and_real_Hub_boundaries(
        bool realHost, bool withholdAcknowledgement)
    {
        var root = Path.Combine(Path.GetTempPath(), "vivarium-agent-composition", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var elapsed = Stopwatch.StartNew();
        var allocated = GC.GetTotalAllocatedBytes(true);
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero));
        VivariumControllerHost? host = null;
        VivariumDatabase? database = null;
        GrpcChannel? channel = null;
        AsyncDuplexStreamingCall<AgentMsg, ControllerMsg>? call = null;
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var session = new CancellationTokenSource();
        AgentRegistry? registry = null;
        AgentConnectionHandle? connection = null;
        BuildTracker? tracker = null;
        try
        {
            TokenStore tokens; AgentStore agents; BuildStore builds;
            if (realHost)
            {
                host = await VivariumControllerHost.StartAsync(new ControllerOptions
                    { DataDir = root, Host = "127.0.0.1", Port = 0, TimeProvider = clock });
                database = host.Database; tokens = host.Tokens; agents = host.AgentStore;
                builds = host.BuildStore; registry = host.Registry; tracker = host.Builds;
            }
            else
            {
                database = new VivariumDatabase(root);
                tokens = new TokenStore(root, database); agents = new AgentStore(database);
                builds = new BuildStore(database);
                registry = new AgentRegistry(agents, clock, new AgentOperationalStore(database));
                tracker = new BuildTracker(registry, builds, timeProvider: clock);
                await tracker.InitializeAsync();
            }
            Assert.That(await builds.GetAsync("same-owned-assignment"), Is.Null, "Fresh fixture with the same identifiers.");
            var hello = Hello(await tokens.CreateEnrollTokenAsync(), clock.GetUtcNow());
            Assert.That(await tokens.AdmitAgentAsync(hello), Is.Not.Null);
            await agents.ObserveHelloAsync(hello);
            hello.AuthToken = await tokens.AuthorizeAgentAsync(hello.AgentId)
                ?? throw new AssertionException("Owned Agent authorization failed.");
            hello.EnrollToken = "";
            if (host is not null)
            {
                channel = GrpcChannel.ForAddress(host.Url, new GrpcChannelOptions
                {
                    HttpHandler = new HttpClientHandler
                    {
                        ServerCertificateCustomValidationCallback = (_, certificate, _, _) => certificate is not null &&
                            Convert.ToHexStringLower(SHA256.HashData(certificate.RawData)) == host.Certificate.FingerprintSha256.ToLowerInvariant()
                    }
                });
                call = new AgentHub.AgentHubClient(channel).Session(cancellationToken: watchdog.Token);
                await call.RequestStream.WriteAsync(new AgentMsg { Hello = hello }, watchdog.Token);
                Assert.That((await Read()).Welcome.Authorized, Is.True);
            }
            else
            {
                var admission = await tokens.AdmitAgentAsync(hello) ?? throw new AssertionException("Credential admission failed.");
                var generations = await agents.AcceptSessionAsync(hello.AgentId, admission.CredentialGeneration);
                var negotiation = AgentProtocolCompatibility.Negotiate(hello);
                await agents.ObserveCapabilitiesAsync(hello.AgentId, generations.CredentialGeneration,
                    generations.ConnectionGeneration, AgentProtocolCompatibility.CreateCapabilityObservation(negotiation));
                var observation = AgentProtocolCompatibility.CreateStaticObservation(hello, negotiation, clock.GetUtcNow(),
                    generations.CredentialGeneration, generations.ConnectionGeneration);
                Assert.That(observation, Is.Not.Null);
                await agents.ObserveStaticFactsAsync(observation!);
                connection = registry.Register(hello, admission.Authorization, admission.Enabled, generations.ConnectionGeneration, session);
                Assert.That(registry.Reconcile(connection, null), Is.True);
            }
            var readyMs = elapsed.ElapsedMilliseconds;
            var assignment = new BuildAssignment { BuildId = "same-owned-assignment" };
            var dispatched = tracker.DispatchBuildFromControllerAsync(hello.AgentId, assignment);
            var delivered = Read();
            if (await Task.WhenAny(delivered, dispatched) == dispatched) await dispatched;
            Assert.That((await delivered).Build, Is.EqualTo(assignment));
            Assert.That((await builds.GetAsync(assignment.BuildId))!.Result, Is.Null);
            if (withholdAcknowledgement)
            {
                // D31's existing15s assignment-ACK contract; external actor deliberately sends nothing.
                clock.Advance(TimeSpan.FromSeconds(15));
                await tracker.SweepDueAssignmentAttemptsAsync(clock.GetUtcNow());
                var stop = (await Read()).Cancel;
                Assert.That(stop.BuildId, Is.EqualTo(assignment.BuildId));
                Assert.That(stop.Mode, Is.EqualTo(BuildStopMode.Force));
                Assert.ThrowsAsync<AssignmentAcknowledgementTimeoutException>(async () =>
                    await dispatched.WaitAsync(watchdog.Token));
                Assert.That(registry.Get(hello.AgentId)!.Quarantined, Is.True);
                Assert.That(registry.Get(hello.AgentId)!.OperationalReason, Is.EqualTo("assignment_acknowledgement_expired"));
                Assert.That((await builds.GetAsync(assignment.BuildId))!.Result, Is.Null,
                    "Unknown execution is retained, never invented as successful completion.");
            }
            else
            {
                await Send(new AgentMsg { AssignmentAccepted = new AssignmentAccepted
                    { BuildId = assignment.BuildId, SessionId = hello.SessionId } });
                await dispatched.WaitAsync(watchdog.Token);
                var result = new BuildResult { BuildId = assignment.BuildId, SessionId = hello.SessionId, Outcome = BuildOutcome.Succeeded };
                await Send(new AgentMsg { Result = result });
                Assert.That((await Read()).ResultAccepted.BuildId, Is.EqualTo(assignment.BuildId));
                await Send(new AgentMsg { Result = result.Clone() });
                Assert.That((await Read()).ResultAccepted.BuildId, Is.EqualTo(assignment.BuildId));
                Assert.That((await builds.GetAsync(assignment.BuildId))!.Result, Is.EqualTo(result));
                Assert.That((await tracker.WaitForResultAsync(assignment.BuildId, watchdog.Token)).Outcome, Is.EqualTo(BuildOutcome.Succeeded));
                Assert.That(registry.Get(hello.AgentId)!.CurrentBuildId, Is.Null);
            }
            TestContext.Out.WriteLine($"Agent composition {(realHost ? "Kestrel-AgentHub" : "production-services")}/{(withholdAcknowledgement ? "missing-ACK" : "result-replay")}: setup {readyMs}ms; action {elapsed.ElapsedMilliseconds-readyMs}ms; allocated {GC.GetTotalAllocatedBytes(true)-allocated} bytes. Scripted reports, no process-execution or TeamCity wire claim.");

            async Task<ControllerMsg> Read()
            {
                if (call is not null)
                {
                    Assert.That(await call.ResponseStream.MoveNext(watchdog.Token), Is.True);
                    return call.ResponseStream.Current.Clone();
                }
                return await registry.Get(hello.AgentId)!.Outbox.Reader.ReadAsync(watchdog.Token);
            }
            Task Send(AgentMsg message) => call is not null ? call.RequestStream.WriteAsync(message, watchdog.Token) :
                message.MsgCase == AgentMsg.MsgOneofCase.AssignmentAccepted ? tracker.OnAssignmentAcceptedAsync(message.AssignmentAccepted, connection!) :
                tracker.OnResultAsync(message.Result, connection!);
        }
        finally
        {
            watchdog.Cancel();
            call?.Dispose(); channel?.Dispose();
            if (connection is not null && registry!.Disconnect(connection) is { } loss) await tracker!.OnSessionLostAsync(loss);
            session.Cancel();
            if (host is not null) await host.DisposeAsync();
            else if (database is not null) await database.DisposeAsync();
            CurrentController.DeleteDataDirectory(root);
        }
        Assert.That(Directory.Exists(root), Is.False);
        TestContext.Out.WriteLine($"Owned teardown complete; total {elapsed.ElapsedMilliseconds}ms.");
    }

    private static Hello Hello(string token, DateTimeOffset now)
    {
        var hello = new Hello
        {
            AgentId = "owned-agent", SessionId = "owned-session", EnrollToken = token,
            MinimumProtocolVersion = 1, CurrentProtocolVersion = 1,
            AgentVersion = "p0-scripted-actor", AgentPackageSha256 = new string('a', 64),
            Os = new OsInfo { Family = "linux", Arch = "x64", Version = "fixture" },
            HostFacts = new HostFacts { Family = "linux", ProductName = "Fixture", ProductVersion = "1", ProductBuild = "1",
                KernelVersion = "fixture", OsArchitecture = "x64", ProcessArchitecture = "x64", Hostname = "owned-agent",
                AgentPackageVersion = "p0-scripted-actor", ObservedAtUnixMs = now.ToUnixTimeMilliseconds(),
                CollectorVersion = "1", Outcome = HostFactsOutcome.Succeeded, Complete = true }
        };
        hello.Capabilities.Add(new CapabilitySupport { CapabilityId = AgentProtocolCompatibility.BuildRunnerCapabilityId, ContractMajor = 1 });
        hello.Capabilities.Add(new CapabilitySupport { CapabilityId = AgentProtocolCompatibility.HostFactsCapabilityId, ContractMajor = 1 });
        return hello;
    }
}
