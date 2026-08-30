using System.Security.Cryptography;
using Grpc.Core;
using Grpc.Net.Client;
using Vivarium.Contracts.V1;
using Vivarium.Controller;
using Vivarium.Controller.Agents;
using Vivarium.Controller.Agents.Compatibility;

namespace Vivarium.Tests;

/// <summary>
/// Tier-1 descriptor and tier-2 real-Kestrel evidence for the additive AgentHub compatibility
/// handshake. Previous-release package CI replaces the descriptor surrogate once a release exists.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class AgentProtocolCompatibilityTests
{
    private string rootDir = null!;

    [SetUp]
    public void SetUp()
    {
        rootDir = Path.Combine(
            Path.GetTempPath(),
            "vivarium-agent-protocol-tests",
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
            // Preserve the original test outcome when an OS delays releasing a loopback handle.
        }
    }

    [Test]
    public void Protocol_extensions_preserve_every_existing_field_number()
    {
        Assert.Multiple(() =>
        {
            AssertField(Hello.Descriptor, 1, "agent_id");
            AssertField(Hello.Descriptor, 2, "auth_token");
            AssertField(Hello.Descriptor, 3, "enroll_token");
            AssertField(Hello.Descriptor, 4, "parameters");
            AssertField(Hello.Descriptor, 5, "image_id");
            AssertField(Hello.Descriptor, 6, "session_id");
            AssertField(Hello.Descriptor, 7, "mac");
            AssertField(Hello.Descriptor, 8, "agent_version");
            AssertField(Hello.Descriptor, 9, "os");
            AssertField(Hello.Descriptor, 10, "interactive");
            AssertField(Hello.Descriptor, 11, "running_build_id");
            AssertField(Hello.Descriptor, 12, "pool_nonce");
            AssertField(Hello.Descriptor, 13, "host_facts");
            AssertField(Hello.Descriptor, 14, "capabilities");
            AssertField(Hello.Descriptor, 15, "minimum_protocol_version");
            AssertField(Hello.Descriptor, 16, "current_protocol_version");
            AssertField(Hello.Descriptor, 17, "credential_generation");
            AssertField(Hello.Descriptor, 18, "agent_package_sha256");
            AssertField(Hello.Descriptor, 19, "upgrade_operation_id");
            AssertField(Hello.Descriptor, 20, "upgrade_failure_code");
            AssertField(Hello.Descriptor, 21, "workload_recovery_outcome");
            AssertField(Hello.Descriptor, 22, "workload_recovery_build_id");
            AssertField(Hello.Descriptor, 23, "workload_recovery_failure_code");
            AssertField(Hello.Descriptor, 24, "process_instance_id");

            AssertField(Welcome.Descriptor, 1, "server_time_unix_ms");
            AssertField(Welcome.Descriptor, 2, "authorized");
            AssertField(Welcome.Descriptor, 3, "server_version");
            AssertField(Welcome.Descriptor, 4, "selected_protocol_version");
            AssertField(Welcome.Descriptor, 10, "connection_generation");

            Assert.That(AgentMsg.Descriptor.FindFieldByNumber(7), Is.Null,
                "AgentMsg tag 7 stays reserved for TeamCity service messages (D14)");
            AssertField(AgentMsg.Descriptor, 8, "upgrade_health_confirmed");
            AssertField(AgentMsg.Descriptor, 9, "upgrade_commit_confirmed");
            AssertField(AgentMsg.Descriptor, 10, "upgrade_finalization_confirmed");
            AssertField(AgentMsg.Descriptor, 11, "build_stop_acknowledged");
            AssertField(AgentMsg.Descriptor, 12, "agent_restart_acknowledged");
            AssertField(ControllerMsg.Descriptor, 7, "upgrade_health_accepted");
            AssertField(ControllerMsg.Descriptor, 8, "upgrade_commit_accepted");
            AssertField(ControllerMsg.Descriptor, 9, "upgrade_commit_recorded");
        });
    }

    [Test]
    public async Task Empty_negotiation_fields_are_explicit_legacy_mode_and_do_not_admit_new_work()
    {
        await using var controller = await StartControllerAsync();
        var hello = LegacyHello(
            "legacy-agent",
            "legacy-session",
            await controller.Tokens.CreateEnrollTokenAsync());

        await using var session = await ProtocolSession.OpenAsync(controller, hello);
        var welcome = session.Welcome.Welcome;
        var live = controller.Registry.Get(hello.AgentId);

        Assert.Multiple(() =>
        {
            Assert.That(welcome.ProtocolMode, Is.EqualTo(AgentProtocolMode.Legacy));
            Assert.That(welcome.SelectedProtocolVersion, Is.Zero);
            Assert.That(welcome.MinimumProtocolVersion,
                Is.EqualTo(AgentProtocolCompatibility.MinimumSupportedVersion));
            Assert.That(welcome.CurrentProtocolVersion,
                Is.EqualTo(AgentProtocolCompatibility.CurrentVersion));
            Assert.That(welcome.NegotiatedCapabilities, Is.Empty);
            Assert.That(welcome.ConnectionGeneration, Is.EqualTo(1));
            Assert.That(live, Is.Not.Null);
            Assert.That(live!.Connected, Is.True);
            Assert.That(live.Reconciled, Is.False,
                "an idle legacy agent stays visible but drained for new assignments");
        });
    }

    [Test]
    public async Task Current_agent_negotiates_only_known_capabilities_and_becomes_build_eligible()
    {
        await using var controller = await StartControllerAsync();
        var hello = CurrentHello(
            "current-agent",
            "current-session",
            await controller.Tokens.CreateEnrollTokenAsync());
        hello.Capabilities.Add(new CapabilitySupport
        {
            CapabilityId = "future.observer.v2",
            ContractMajor = 2,
        });

        await using var session = await ProtocolSession.OpenAsync(controller, hello);
        var welcome = session.Welcome.Welcome;
        var live = await WaitForAsync(
            () => controller.Registry.Get(hello.AgentId) is { Reconciled: true } agent ? agent : null,
            TimeSpan.FromSeconds(5));
        var projection = await controller.AgentStore.GetProjectionAsync(hello.AgentId);

        Assert.Multiple(() =>
        {
            Assert.That(welcome.ProtocolMode, Is.EqualTo(AgentProtocolMode.Negotiated));
            Assert.That(welcome.SelectedProtocolVersion, Is.EqualTo(1));
            Assert.That(welcome.ConnectionGeneration, Is.EqualTo(1));
            Assert.That(welcome.NegotiatedCapabilities.Select(capability => capability.CapabilityId),
                Is.EqualTo(new[]
                {
                    AgentProtocolCompatibility.HostFactsCapabilityId,
                    AgentProtocolCompatibility.BuildRunnerCapabilityId,
                }));
            Assert.That(welcome.NegotiatedCapabilities.All(capability => capability.ContractMajor == 1),
                Is.True);
            Assert.That(live.Reconciled, Is.True);
            Assert.That(projection!.Observation, Is.Not.Null);
            Assert.That(projection.Observation!.Facts.Hostname, Is.EqualTo(hello.AgentId));
            Assert.That(projection.Observation.PackageDigestSha256, Is.EqualTo(new string('a', 64)));
            Assert.That(projection.Observation.ConnectionGeneration,
                Is.EqualTo((long)welcome.ConnectionGeneration));
            Assert.That(projection.Observation.Capabilities.Select(capability => capability.CapabilityId),
                Does.Contain("future.observer.v2"),
                "well-formed future support remains observed but is not negotiated for dispatch");
        });
    }

    [Test]
    public async Task Legacy_reconnect_clears_current_support_without_deleting_last_safe_facts()
    {
        await using var controller = await StartControllerAsync();
        var enrollToken = await controller.Tokens.CreateEnrollTokenAsync();
        var currentHello = CurrentHello("mixed-agent", "current-session", enrollToken);
        await using (var current = await ProtocolSession.OpenAsync(controller, currentHello))
        {
            Assert.That(current.Welcome.Welcome.ProtocolMode,
                Is.EqualTo(AgentProtocolMode.Negotiated));
        }

        var before = await controller.AgentStore.GetProjectionAsync(currentHello.AgentId);
        var legacyHello = LegacyHello(currentHello.AgentId, "legacy-session", enrollToken);
        await using var legacy = await ProtocolSession.OpenAsync(controller, legacyHello);
        var after = await controller.AgentStore.GetProjectionAsync(currentHello.AgentId);

        Assert.Multiple(() =>
        {
            Assert.That(before!.Observation, Is.Not.Null);
            Assert.That(before.Observation!.Capabilities, Is.Not.Empty);
            Assert.That(after!.Observation, Is.Not.Null);
            Assert.That(after.Observation!.Revision, Is.EqualTo(before.Observation.Revision));
            Assert.That(after.Observation.Facts.Hostname, Is.EqualTo(currentHello.AgentId));
            Assert.That(after.Observation.Capabilities, Is.Empty,
                "empty legacy advertisement means no current capability support");
            Assert.That(legacy.Welcome.Welcome.ProtocolMode,
                Is.EqualTo(AgentProtocolMode.Legacy));
        });
    }

    [Test]
    public async Task Authorization_and_reconnect_echo_controller_authoritative_credential_generation()
    {
        await using var controller = await StartControllerAsync();
        var hello = CurrentHello(
            "credential-agent",
            "credential-session-one",
            await controller.Tokens.CreateEnrollTokenAsync());

        string authToken;
        await using (var enrollment = await ProtocolSession.OpenAsync(controller, hello))
        {
            Assert.That(enrollment.Welcome.Welcome.CredentialGeneration, Is.Zero);
            await controller.AuthorizeAgentAsync(hello.AgentId);
            var granted = await enrollment.ReadNextAsync();
            Assert.Multiple(() =>
            {
                Assert.That(granted.MsgCase, Is.EqualTo(ControllerMsg.MsgOneofCase.Authorized));
                Assert.That(granted.Authorized.CredentialGeneration, Is.EqualTo(1));
                Assert.That(granted.Authorized.AuthToken, Is.Not.Empty);
            });
            authToken = granted.Authorized.AuthToken;
        }

        var reconnect = CurrentHello(hello.AgentId, "credential-session-two", enrollToken: string.Empty);
        reconnect.AuthToken = authToken;
        reconnect.CredentialGeneration = 999; // diagnostic belief is never credential authority
        await using var accepted = await ProtocolSession.OpenAsync(controller, reconnect);
        var projection = await controller.AgentStore.GetProjectionAsync(hello.AgentId);

        Assert.Multiple(() =>
        {
            Assert.That(accepted.Welcome.Welcome.Authorized, Is.True);
            Assert.That(accepted.Welcome.Welcome.CredentialGeneration, Is.EqualTo(1));
            Assert.That(accepted.Welcome.Welcome.ConnectionGeneration, Is.EqualTo(2));
            Assert.That(projection!.Agent.CredentialGeneration, Is.EqualTo(1));
            Assert.That(projection.Observation!.CredentialGeneration, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Reenrollment_revokes_the_old_credential_and_advances_generation_before_reauthorization()
    {
        await using var controller = await StartControllerAsync();
        var initial = CurrentHello(
            "reenrolled-agent",
            "enrollment-session",
            await controller.Tokens.CreateEnrollTokenAsync());

        string oldCredential;
        await using (var enrollment = await ProtocolSession.OpenAsync(controller, initial))
        {
            await controller.AuthorizeAgentAsync(initial.AgentId);
            var grant = await enrollment.ReadNextAsync();
            oldCredential = grant.Authorized.AuthToken;
            Assert.That(grant.Authorized.CredentialGeneration, Is.EqualTo(1));
        }

        var replacement = CurrentHello(
            initial.AgentId,
            "replacement-session",
            await controller.Tokens.CreateEnrollTokenAsync());
        await using var replacementSession = await ProtocolSession.OpenAsync(controller, replacement);

        Assert.Multiple(() =>
        {
            Assert.That(replacementSession.Welcome.Welcome.Authorized, Is.False);
            Assert.That(replacementSession.Welcome.Welcome.CredentialGeneration, Is.EqualTo(2));
        });
        Assert.That(await controller.Tokens.IsValidBearerAsync(oldCredential), Is.False);

        var stale = CurrentHello(initial.AgentId, "stale-session", enrollToken: string.Empty);
        stale.AuthToken = oldCredential;
        stale.CredentialGeneration = 1;
        var staleFailure = await ProtocolSession.ExpectFailureAsync(controller, stale);
        Assert.That(staleFailure.StatusCode, Is.EqualTo(StatusCode.PermissionDenied));

        await controller.AuthorizeAgentAsync(initial.AgentId);
        var replacementGrant = await replacementSession.ReadNextAsync();
        Assert.Multiple(() =>
        {
            Assert.That(replacementGrant.Authorized.CredentialGeneration, Is.EqualTo(2));
            Assert.That(replacementGrant.Authorized.AuthToken, Is.Not.EqualTo(oldCredential));
        });
    }

    [Test]
    public async Task Replacement_revocation_fences_the_old_live_session_even_if_observation_fails()
    {
        await using var controller = await StartControllerAsync();
        var initial = CurrentHello(
            "replacement-failure-agent",
            "old-live-session",
            await controller.Tokens.CreateEnrollTokenAsync());

        string oldCredential;
        await using var oldSession = await ProtocolSession.OpenAsync(controller, initial);
        await controller.AuthorizeAgentAsync(initial.AgentId);
        oldCredential = (await oldSession.ReadNextAsync()).Authorized.AuthToken;
        await WaitForAsync(
            () => controller.Registry.Get(initial.AgentId) is { Reconciled: true } agent ? agent : null,
            TimeSpan.FromSeconds(5));
        await controller.AgentStore.SetCustomParameterAsync(
            initial.AgentId,
            "collision",
            "desired");

        var replacement = CurrentHello(
            initial.AgentId,
            "replacement-that-fails-observation",
            await controller.Tokens.CreateEnrollTokenAsync());
        replacement.Parameters["collision"] = "reported";
        _ = await ProtocolSession.ExpectFailureAsync(controller, replacement);

        var live = controller.Registry.Get(initial.AgentId);
        var stored = await controller.AgentStore.GetAsync(initial.AgentId);
        var reserved = controller.Registry.TryBeginBuild(
            initial.AgentId,
            "must-not-run",
            out var reason);
        Assert.Multiple(() =>
        {
            Assert.That(live, Is.Not.Null);
            Assert.That(live!.SessionId, Is.EqualTo(initial.SessionId),
                "the failed replacement did not register a new runtime session");
            Assert.That(live.Auth, Is.EqualTo(AgentAuth.Unauthorized));
            Assert.That(stored!.Authorized, Is.False);
            Assert.That(stored.CredentialGeneration, Is.EqualTo(2));
            Assert.That(reserved, Is.False);
            Assert.That(reason, Does.Contain("not authorized"));
        });
        Assert.That(await controller.Tokens.IsValidBearerAsync(oldCredential), Is.False);
    }

    [Test]
    public async Task Incompatible_agent_fails_before_claiming_enrollment_or_creating_an_agent()
    {
        await using var controller = await StartControllerAsync();
        var enrollToken = await controller.Tokens.CreateEnrollTokenAsync();
        var incompatible = CurrentHello("future-agent", "future-session", enrollToken);
        incompatible.MinimumProtocolVersion = 2;
        incompatible.CurrentProtocolVersion = 2;

        var error = await ProtocolSession.ExpectFailureAsync(controller, incompatible);

        Assert.Multiple(() =>
        {
            Assert.That(error.StatusCode, Is.EqualTo(StatusCode.FailedPrecondition));
            Assert.That(error.Status.Detail, Does.Contain("incompatible"));
        });
        Assert.That(await controller.AgentStore.GetAsync(incompatible.AgentId), Is.Null);

        // The same single-use proof remains available because compatibility failed before admission.
        var compatible = CurrentHello(incompatible.AgentId, "compatible-session", enrollToken);
        await using var accepted = await ProtocolSession.OpenAsync(controller, compatible);
        Assert.That(accepted.Welcome.Welcome.ProtocolMode, Is.EqualTo(AgentProtocolMode.Negotiated));
    }

    [Test]
    public async Task Reconnect_after_controller_restart_receives_a_strictly_newer_generation()
    {
        ulong firstGeneration;
        string enrollToken;
        var firstHello = new Hello();
        await using (var firstController = await StartControllerAsync())
        {
            enrollToken = await firstController.Tokens.CreateEnrollTokenAsync();
            firstHello = CurrentHello("reconnecting-agent", "session-one", enrollToken);
            await using var first = await ProtocolSession.OpenAsync(firstController, firstHello);
            firstGeneration = first.Welcome.Welcome.ConnectionGeneration;
        }

        await using var controller = await StartControllerAsync();
        var secondHello = CurrentHello(firstHello.AgentId, "session-two", enrollToken);
        await using var second = await ProtocolSession.OpenAsync(controller, secondHello);

        Assert.Multiple(() =>
        {
            Assert.That(firstGeneration, Is.EqualTo(1));
            Assert.That(second.Welcome.Welcome.ConnectionGeneration,
                Is.GreaterThan(firstGeneration));
            Assert.That(controller.Registry.Get(firstHello.AgentId)!.SessionId,
                Is.EqualTo(secondHello.SessionId));
        });
    }

    [Test]
    public async Task Two_agents_keep_independent_connection_generation_sequences()
    {
        await using var controller = await StartControllerAsync();
        var firstHello = CurrentHello(
            "independent-agent-a",
            "agent-a-session-one",
            await controller.Tokens.CreateEnrollTokenAsync());
        var secondHello = CurrentHello(
            "independent-agent-b",
            "agent-b-session-one",
            await controller.Tokens.CreateEnrollTokenAsync());

        await using var first = await ProtocolSession.OpenAsync(controller, firstHello);
        await using var second = await ProtocolSession.OpenAsync(controller, secondHello);

        Assert.Multiple(() =>
        {
            Assert.That(first.Welcome.Welcome.ConnectionGeneration, Is.EqualTo(1));
            Assert.That(second.Welcome.Welcome.ConnectionGeneration, Is.EqualTo(1));
            Assert.That(controller.Registry.Get(firstHello.AgentId), Is.Not.Null);
            Assert.That(controller.Registry.Get(secondHello.AgentId), Is.Not.Null);
        });
    }

    [Test]
    public async Task Partially_populated_negotiation_is_rejected_instead_of_guessed_as_legacy()
    {
        await using var controller = await StartControllerAsync();
        var hello = LegacyHello(
            "partial-agent",
            "partial-session",
            await controller.Tokens.CreateEnrollTokenAsync());
        hello.CurrentProtocolVersion = 1;

        var error = await ProtocolSession.ExpectFailureAsync(controller, hello);

        Assert.That(error.StatusCode, Is.EqualTo(StatusCode.InvalidArgument));
        Assert.That(await controller.AgentStore.GetAsync(hello.AgentId), Is.Null);
    }

    [Test]
    public void Capability_and_digest_inputs_are_bounded_before_controller_state_is_touched()
    {
        var tooMany = CurrentHello("bounded-agent", "bounded-session", "unused");
        for (var index = tooMany.Capabilities.Count;
             index <= AgentProtocolCompatibility.MaximumCapabilities;
             index++)
        {
            tooMany.Capabilities.Add(new CapabilitySupport
            {
                CapabilityId = $"future.cap-{index}.v1",
                ContractMajor = 1,
            });
        }

        var tooManyError = Assert.Throws<AgentProtocolException>(() =>
            AgentProtocolCompatibility.Negotiate(tooMany));

        var badDigest = CurrentHello("digest-agent", "digest-session", "unused");
        badDigest.AgentPackageSha256 = new string('A', 64);
        var digestError = Assert.Throws<AgentProtocolException>(() =>
            AgentProtocolCompatibility.Negotiate(badDigest));

        var badFacts = CurrentHello("facts-agent", "facts-session", "unused");
        badFacts.HostFacts.Issues.Add(new HostFactIssue
        {
            Code = " ",
            Field = "hostname",
        });
        var factsError = Assert.Throws<AgentProtocolException>(() =>
            AgentProtocolCompatibility.Negotiate(badFacts));

        Assert.Multiple(() =>
        {
            Assert.That(tooManyError!.StatusCode, Is.EqualTo(StatusCode.InvalidArgument));
            Assert.That(digestError!.StatusCode, Is.EqualTo(StatusCode.InvalidArgument));
            Assert.That(factsError!.StatusCode, Is.EqualTo(StatusCode.InvalidArgument));
        });
    }

    private Task<VivariumControllerHost> StartControllerAsync() =>
        VivariumControllerHost.StartAsync(new ControllerOptions
        {
            DataDir = Path.Combine(rootDir, "controller"),
            Host = "127.0.0.1",
            Port = 0,
        });

    private static Hello LegacyHello(string agentId, string sessionId, string enrollToken) => new()
    {
        AgentId = agentId,
        SessionId = sessionId,
        EnrollToken = enrollToken,
        AgentVersion = "legacy-test",
        Os = new OsInfo { Family = "linux", Version = "legacy", Arch = "x64" },
    };

    private static Hello CurrentHello(string agentId, string sessionId, string enrollToken)
    {
        var hello = new Hello
        {
            AgentId = agentId,
            SessionId = sessionId,
            EnrollToken = enrollToken,
            AgentVersion = "current-test",
            Os = new OsInfo { Family = "linux", Version = "current", Arch = "x64" },
            MinimumProtocolVersion = 1,
            CurrentProtocolVersion = 1,
            AgentPackageSha256 = new string('a', 64),
            HostFacts = new HostFacts
            {
                Family = "linux",
                ProductName = "Test Linux",
                ProductVersion = "1",
                ProductBuild = "1.0-test",
                KernelVersion = "test-kernel",
                OsArchitecture = "x64",
                ProcessArchitecture = "x64",
                Hostname = agentId,
                AgentPackageVersion = "current-test",
                ObservedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                CollectorVersion = "1",
                Outcome = HostFactsOutcome.Succeeded,
                Complete = true,
            },
        };
        hello.Capabilities.Add(new CapabilitySupport
        {
            CapabilityId = AgentProtocolCompatibility.BuildRunnerCapabilityId,
            ContractMajor = 1,
        });
        hello.Capabilities.Add(new CapabilitySupport
        {
            CapabilityId = AgentProtocolCompatibility.HostFactsCapabilityId,
            ContractMajor = 1,
        });
        return hello;
    }

    private static void AssertField(
        Google.Protobuf.Reflection.MessageDescriptor descriptor,
        int number,
        string expectedName) =>
        Assert.That(descriptor.FindFieldByNumber(number)?.Name, Is.EqualTo(expectedName));

    private static HttpClientHandler PinnedHandler(VivariumControllerHost controller) => new()
    {
        ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
            certificate is not null &&
            Convert.ToHexString(SHA256.HashData(certificate.RawData)).Equals(
                controller.Certificate.FingerprintSha256,
                StringComparison.OrdinalIgnoreCase),
    };

    private static async Task<T> WaitForAsync<T>(Func<T?> probe, TimeSpan timeout) where T : class
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (probe() is { } value)
            {
                return value;
            }

            await Task.Delay(25);
        }

        throw new AssertionException("condition not reached within timeout");
    }

    private sealed class ProtocolSession : IAsyncDisposable
    {
        private readonly GrpcChannel channel;
        private readonly AsyncDuplexStreamingCall<AgentMsg, ControllerMsg> call;
        private readonly CancellationTokenSource timeout;

        private ProtocolSession(
            GrpcChannel channel,
            AsyncDuplexStreamingCall<AgentMsg, ControllerMsg> call,
            CancellationTokenSource timeout,
            ControllerMsg welcome)
        {
            this.channel = channel;
            this.call = call;
            this.timeout = timeout;
            Welcome = welcome;
        }

        public ControllerMsg Welcome { get; }

        public async Task<ControllerMsg> ReadNextAsync()
        {
            if (!await call.ResponseStream.MoveNext(timeout.Token))
            {
                throw new AssertionException("AgentHub ended before the expected controller message");
            }

            return call.ResponseStream.Current.Clone();
        }

        public static async Task<ProtocolSession> OpenAsync(
            VivariumControllerHost controller,
            Hello hello)
        {
            var channel = GrpcChannel.ForAddress(controller.Url, new GrpcChannelOptions
            {
                HttpHandler = PinnedHandler(controller),
            });
            var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var call = new AgentHub.AgentHubClient(channel).Session(cancellationToken: timeout.Token);
            try
            {
                await call.RequestStream.WriteAsync(new AgentMsg { Hello = hello }, timeout.Token);
                if (!await call.ResponseStream.MoveNext(timeout.Token))
                {
                    throw new AssertionException("AgentHub ended before Welcome");
                }

                Assert.That(call.ResponseStream.Current.MsgCase,
                    Is.EqualTo(ControllerMsg.MsgOneofCase.Welcome));
                return new ProtocolSession(
                    channel,
                    call,
                    timeout,
                    call.ResponseStream.Current.Clone());
            }
            catch
            {
                call.Dispose();
                timeout.Dispose();
                channel.Dispose();
                throw;
            }
        }

        public static async Task<RpcException> ExpectFailureAsync(
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
            return Assert.ThrowsAsync<RpcException>(async () =>
                await call.ResponseStream.MoveNext(timeout.Token))!;
        }

        public ValueTask DisposeAsync()
        {
            timeout.Cancel();
            call.Dispose();
            timeout.Dispose();
            channel.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
