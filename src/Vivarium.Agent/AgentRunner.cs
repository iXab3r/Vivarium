using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Vivarium.Agent.Facts;
using Vivarium.Contracts.V1;

namespace Vivarium.Agent;

/// <summary>
/// The whole agent: reverse-connect session loop (D1), deliberately dumb — every decision lives in
/// the controller. Reconnects forever; builds survive a dropped connection (the result is queued and
/// kept on disk until acknowledged through a later session — re-adoption, D4). A restart request ends RunAsync so the
/// launcher can swap us (D2).
/// </summary>
public sealed class AgentRunner
{
    private const uint MinimumProtocolVersion = (uint)AgentProtocolVersion.V1;
    private const uint CurrentProtocolVersion = (uint)AgentProtocolVersion.V1;
    private const string BuildRunnerCapabilityId = "teamcity.build-runner.v1";
    private const string BootstrapSupervisorCapabilityId = "vivarium.bootstrap-supervisor.v1";
    private static readonly System.Text.Json.JsonSerializerOptions UpgradeJsonOptions =
        new(System.Text.Json.JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false,
        };

    public static readonly string Version =
        typeof(AgentRunner).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    private readonly AgentOptions options;
    private readonly BlobClient blobs;
    private readonly IPlatformFactsCollector platformFactsCollector;
    private readonly string? packageDigestSha256;
    private readonly TaskCompletionSource<bool> authorized = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource restartRequested = new();
    private readonly object pendingLock = new();
    private readonly object activeBuildLock = new();
    private string? authToken;
    private volatile string? runningBuildId;
    private volatile SessionWriter? currentWriter;
    private volatile string currentSessionId = string.Empty;
    private BuildResult? pendingResult;
    private ActiveBuild? activeBuild;
    private PlatformFactSnapshot? platformFacts;
    private ulong credentialGeneration;

    public string AgentId { get; }

    /// <summary>Server minus local clock, from the last Welcome (D4). Applying it is a later, elevated concern.</summary>
    public TimeSpan ClockSkew { get; private set; }

    public AgentRunner(AgentOptions options)
    {
        this.options = options;
        PrivateStorage.EnsureDirectory(options.DataDir);
        packageDigestSha256 = NormalizePackageDigest(options.AgentPackageSha256);
        if ((options.BootstrapLeasePath is null) != (options.BootstrapLeaseId is null))
        {
            throw new InvalidDataException("bootstrap lease path and id must be supplied together");
        }

        var idPath = Path.Combine(options.DataDir, "agent-id");
        if (File.Exists(idPath))
        {
            AgentId = File.ReadAllText(idPath).Trim();
            if (!IsLocalIdentity(AgentId))
            {
                throw new InvalidDataException("persisted Agent identity is invalid");
            }
        }
        else
        {
            if (File.Exists(Path.Combine(options.DataDir, "auth.token")) ||
                File.Exists(Path.Combine(options.DataDir, "auth.generation")))
            {
                throw new InvalidDataException(
                    "persisted Agent identity is missing from an initialized installation");
            }
            AgentId = Guid.NewGuid().ToString("N");
            DurableFile.ReplaceText(idPath, AgentId);
        }

        var tokenPath = TokenPath;
        if (!File.Exists(tokenPath) && File.Exists(CredentialGenerationPath))
        {
            throw new InvalidDataException(
                "persisted credential generation exists without an Agent bearer");
        }
        if (File.Exists(tokenPath))
        {
            PrivateStorage.RestrictSecretFile(tokenPath);
        }

        authToken = File.Exists(tokenPath)
            ? ValidateAuthToken(File.ReadAllText(tokenPath).Trim())
            : null;
        credentialGeneration = authToken is null ? 0 : ReadCredentialGeneration();
        if (File.Exists(PendingResultPath))
        {
            pendingResult = BuildResult.Parser.ParseFrom(File.ReadAllBytes(PendingResultPath));
            if (string.IsNullOrWhiteSpace(pendingResult.BuildId))
            {
                throw new InvalidDataException("the pending build result has no build id");
            }

            runningBuildId = pendingResult.BuildId;
        }

        blobs = new BlobClient(options.ControllerUrl, options.CertFingerprintSha256) { BearerToken = authToken };
        platformFactsCollector = options.PlatformFactsCollector ?? PlatformFactsCollector.CreateDefault();
    }

    private string TokenPath => Path.Combine(options.DataDir, "auth.token");
    private string CredentialGenerationPath => Path.Combine(options.DataDir, "auth.generation");
    private string PendingResultPath => Path.Combine(options.DataDir, "pending-build-result.pb");

    public Task WaitAuthorizedAsync(TimeSpan timeout) => authorized.Task.WaitAsync(timeout);

    public async Task RunAsync(CancellationToken ct)
    {
        using var leaseLost = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            ct, restartRequested.Token, leaseLost.Token);
        var leaseMonitor = MonitorBootstrapLeaseAsync(leaseLost, linked.Token);
        try
        {
            platformFacts = await platformFactsCollector.CollectAsync(
                new AgentPackageIdentity(
                    Version,
                    options.AgentPackageVersion ?? Version,
                    packageDigestSha256),
                linked.Token);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            // Static facts enrich admission but never prevent the reverse connection. Collectors
            // already bound native diagnostics; this message deliberately excludes raw host data.
            Console.Error.WriteLine($"[agent] static host facts unavailable: {exception.GetType().Name}");
            platformFacts = null;
        }

        try
        {
            while (!linked.IsCancellationRequested)
            {
                try
                {
                    await RunSessionAsync(linked.Token);
                }
                catch (OperationCanceledException) when (linked.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[agent] session ended: {ex.Message}");
                }

                try
                {
                    await Task.Delay(options.ReconnectDelay, linked.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        finally
        {
            lock (activeBuildLock)
            {
                activeBuild?.Cancellation.Cancel();
            }

            leaseLost.Cancel();
            try
            {
                await leaseMonitor;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private async Task RunSessionAsync(CancellationToken ct)
    {
        var handler = PinnedTls.CreateHandler(options.CertFingerprintSha256, keepAlive: true);
        using var channel = GrpcChannel.ForAddress(options.ControllerUrl, new GrpcChannelOptions
        {
            HttpHandler = handler,
            DisposeHttpClient = true,
        });
        var client = new AgentHub.AgentHubClient(channel);
        using var call = client.Session(cancellationToken: ct);
        var writer = new SessionWriter(call.RequestStream);
        var sessionId = Guid.NewGuid().ToString("N");

        await writer.SendAsync(new AgentMsg { Hello = BuildHello(sessionId) }, ct);
        currentSessionId = sessionId;
        currentWriter = writer;

        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeats = HeartbeatLoopAsync(writer, sessionCts.Token);
        try
        {
            await foreach (var msg in call.ResponseStream.ReadAllAsync(ct))
            {
                switch (msg.MsgCase)
                {
                    case ControllerMsg.MsgOneofCase.Welcome:
                        ClockSkew = DateTimeOffset.FromUnixTimeMilliseconds(msg.Welcome.ServerTimeUnixMs) - DateTimeOffset.UtcNow;
                        if (msg.Welcome.SelectedProtocolVersion != 0 &&
                            msg.Welcome.SelectedProtocolVersion is < MinimumProtocolVersion or > CurrentProtocolVersion)
                        {
                            throw new InvalidDataException(
                                $"controller selected unsupported Agent protocol version " +
                                $"{msg.Welcome.SelectedProtocolVersion}");
                        }

                        if (authToken is not null && msg.Welcome.CredentialGeneration > 0)
                        {
                            PersistCredentialGeneration(msg.Welcome.CredentialGeneration);
                        }

                        if (msg.Welcome.Authorized)
                        {
                            authorized.TrySetResult(true);
                        }

                        // A build finished while we were disconnected? Deliver it now (D4).
                        await TryFlushPendingResultAsync(ct);
                        break;

                    case ControllerMsg.MsgOneofCase.Authorized:
                        var deliveredToken = ValidateAuthToken(msg.Authorized.AuthToken);
                        PrivateStorage.WriteSecretText(TokenPath, deliveredToken);
                        if (msg.Authorized.CredentialGeneration > 0)
                        {
                            PersistCredentialGeneration(msg.Authorized.CredentialGeneration);
                        }

                        authToken = deliveredToken;
                        blobs.BearerToken = authToken;
                        // Enrollment proof is only a one-time credential delivery channel. Close it
                        // after the bearer is durable so the controller can consume the proof on a
                        // fresh Hello before this Agent is eligible for work.
                        return;

                    case ControllerMsg.MsgOneofCase.Build:
                        await AcceptBuildAsync(msg.Build);
                        break;

                    case ControllerMsg.MsgOneofCase.Cancel:
                        CancelBuild(msg.Cancel);
                        break;

                    case ControllerMsg.MsgOneofCase.ResultAccepted:
                        AcceptResult(msg.ResultAccepted);
                        break;

                    case ControllerMsg.MsgOneofCase.UpgradeHealthAccepted:
                        await ConfirmUpgradeHealthAsync(
                            msg.UpgradeHealthAccepted, writer, sessionId, ct);
                        break;

                    case ControllerMsg.MsgOneofCase.UpgradeCommitAccepted:
                        await ConfirmUpgradeCommitAsync(
                            msg.UpgradeCommitAccepted, writer, sessionId, ct);
                        break;

                    case ControllerMsg.MsgOneofCase.UpgradeCommitRecorded:
                        await ConfirmUpgradeFinalizationAsync(
                            msg.UpgradeCommitRecorded, writer, sessionId, ct);
                        break;

                    case ControllerMsg.MsgOneofCase.Restart:
                        restartRequested.Cancel();
                        return;
                }
            }
        }
        finally
        {
            if (ReferenceEquals(currentWriter, writer))
            {
                currentWriter = null;
            }

            sessionCts.Cancel();
            try
            {
                await heartbeats;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    /// <summary>
    /// Routes a message through the current session, whatever it is by now. Logs and statuses are
    /// best-effort; terminal results are persisted separately and retried until acknowledged.
    /// </summary>
    private async Task SendRoutedAsync(AgentMsg msg, CancellationToken ct)
    {
        var writer = currentWriter;
        if (writer == null)
        {
            QueueIfResult(msg);
            return;
        }

        try
        {
            await writer.SendAsync(msg, ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            QueueIfResult(msg);
        }
    }

    private void QueueIfResult(AgentMsg msg)
    {
        if (msg.MsgCase == AgentMsg.MsgOneofCase.Result)
        {
            QueueResultDurably(msg.Result);
        }
    }

    private void QueueResultDurably(BuildResult result)
    {
        lock (pendingLock)
        {
            if (pendingResult != null && pendingResult.BuildId != result.BuildId)
            {
                throw new InvalidOperationException(
                    $"build '{pendingResult.BuildId}' is still awaiting result acknowledgement");
            }

            var tempPath = PendingResultPath + ".tmp";
            using (var file = new FileStream(
                       tempPath, FileMode.Create, FileAccess.Write, FileShare.None,
                       bufferSize: 4096, FileOptions.WriteThrough))
            {
                result.WriteTo(file);
                file.Flush(flushToDisk: true);
            }

            File.Move(tempPath, PendingResultPath, overwrite: true);
            pendingResult = result.Clone();
            runningBuildId = result.BuildId;
        }
    }

    private void AcceptResult(BuildResultAccepted accepted)
    {
        lock (pendingLock)
        {
            if (pendingResult?.BuildId != accepted.BuildId ||
                accepted.SessionId != currentSessionId)
            {
                return;
            }

            File.Delete(PendingResultPath);
            pendingResult = null;
            runningBuildId = null;
        }
    }

    private async Task TryFlushPendingResultAsync(CancellationToken ct)
    {
        BuildResult? snapshot;
        lock (pendingLock)
        {
            snapshot = pendingResult?.Clone();
        }

        if (snapshot == null)
        {
            return;
        }

        var writer = currentWriter;
        if (writer == null)
        {
            return;
        }

        try
        {
            snapshot.SessionId = currentSessionId;
            await writer.SendAsync(new AgentMsg { Result = snapshot }, ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // still disconnected — the next Welcome or heartbeat tick retries
        }
    }

    private async Task AcceptBuildAsync(BuildAssignment assignment)
    {
        ActiveBuild? build = null;
        var accepted = false;
        lock (activeBuildLock)
        {
            if (activeBuild != null)
            {
                // The controller deliberately resends an unacknowledged assignment after a stream
                // or controller restart. Re-acknowledge the same ownership without starting twice;
                // a different concurrent assignment is rejected by withholding acknowledgement.
                accepted = activeBuild.Assignment.BuildId == assignment.BuildId;
            }
            else
            {
                lock (pendingLock)
                {
                    if (pendingResult != null)
                    {
                        // A terminal result is stronger proof of ownership than an active process.
                        // ACK its duplicate assignment but never execute the payload again.
                        accepted = pendingResult.BuildId == assignment.BuildId;
                    }
                    else
                    {
                        build = new ActiveBuild(
                            assignment.Clone(),
                            CancellationTokenSource.CreateLinkedTokenSource(
                                restartRequested.Token));
                        activeBuild = build;
                        runningBuildId = assignment.BuildId;
                        accepted = true;
                    }
                }
            }
        }

        if (!accepted)
        {
            return;
        }

        await SendRoutedAsync(new AgentMsg
        {
            AssignmentAccepted = new AssignmentAccepted
            {
                BuildId = assignment.BuildId,
                SessionId = currentSessionId,
            },
        }, CancellationToken.None);

        // Builds run on the runner-level token: they survive session death.
        if (build != null)
        {
            _ = Task.Run(() => ExecuteBuildAsync(build), CancellationToken.None);
        }
    }

    private void CancelBuild(CancelBuild cancellation)
    {
        lock (activeBuildLock)
        {
            if (activeBuild?.Assignment.BuildId != cancellation.BuildId)
            {
                return; // idempotent: already finished or belongs to another build
            }

            activeBuild.CancellationReason = cancellation.Reason;
            activeBuild.Cancellation.Cancel();
        }
    }

    private async Task ExecuteBuildAsync(ActiveBuild build)
    {
        var assignment = build.Assignment;
        BuildResult result;
        try
        {
            var workRoot = Path.Combine(options.DataDir, "builds");
            result = await BuildExecutor.ExecuteAsync(
                workRoot, assignment, blobs, SendRoutedAsync, currentSessionId, build.Cancellation.Token);
        }
        catch (OperationCanceledException) when (build.Cancellation.IsCancellationRequested)
        {
            result = new BuildResult
            {
                BuildId = assignment.BuildId,
                SessionId = currentSessionId,
                Outcome = BuildOutcome.Cancelled,
                StatusText = build.CancellationReason ?? "build stopped",
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[agent] build {assignment.BuildId} failed: {ex}");
            result = new BuildResult
            {
                BuildId = assignment.BuildId,
                SessionId = currentSessionId,
                Outcome = BuildOutcome.InfrastructureFailed,
                StatusText = ex.Message,
            };
        }

        // Make the terminal result durable before releasing active ownership. This closes the small
        // window in which a second assignment could otherwise start before the first result existed.
        QueueResultDurably(result);
        lock (activeBuildLock)
        {
            if (ReferenceEquals(activeBuild, build))
            {
                activeBuild = null;
            }
        }

        build.Cancellation.Dispose();
        await TryFlushPendingResultAsync(CancellationToken.None);
    }

    private async Task HeartbeatLoopAsync(SessionWriter writer, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(options.HeartbeatInterval);
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                await writer.SendAsync(new AgentMsg
                {
                    Heartbeat = new Heartbeat { RunningBuildId = runningBuildId ?? string.Empty },
                }, ct);
                await TryFlushPendingResultAsync(ct);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                return; // stream is dead; the session loop will reconnect
            }
        }
    }

    private Hello BuildHello(string sessionId)
    {
        var facts = platformFacts;
        var hello = new Hello
        {
            AgentId = AgentId,
            AuthToken = authToken ?? string.Empty,
            EnrollToken = authToken == null ? options.EnrollToken ?? string.Empty : string.Empty,
            SessionId = sessionId,
            Mac = FirstMacAddress(),
            AgentVersion = Version,
            Os = new OsInfo
            {
                Family = facts?.Family ??
                    (OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux"),
                Version = facts?.ProductVersion ?? Environment.OSVersion.Version.ToString(),
                Arch = facts?.OsArchitecture ??
                    RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
            },
            Interactive = Environment.UserInteractive,
            RunningBuildId = runningBuildId ?? string.Empty,
            MinimumProtocolVersion = MinimumProtocolVersion,
            CurrentProtocolVersion = CurrentProtocolVersion,
            CredentialGeneration = credentialGeneration,
            AgentPackageSha256 = facts?.PackageDigestSha256 ?? packageDigestSha256 ?? string.Empty,
            UpgradeOperationId = options.UpgradeOperationId ?? string.Empty,
            UpgradeFailureCode = BoundUpgradeFailureCode(options.UpgradeFailureCode),
        };
        hello.Capabilities.Add(new CapabilitySupport
        {
            CapabilityId = BuildRunnerCapabilityId,
            ContractMajor = 1,
        });
        if (options.BootstrapLeasePath is not null && options.BootstrapLeaseId is not null)
        {
            hello.Capabilities.Add(new CapabilitySupport
            {
                CapabilityId = BootstrapSupervisorCapabilityId,
                ContractMajor = 1,
            });
        }
        if (facts is not null)
        {
            hello.HostFacts = ToProtocolFacts(facts);
            foreach (var capability in facts.Capabilities
                         .Where(capability => capability.Id != BuildRunnerCapabilityId)
                         .OrderBy(capability => capability.Id, StringComparer.Ordinal))
            {
                hello.Capabilities.Add(new CapabilitySupport
                {
                    CapabilityId = capability.Id,
                    ContractMajor = checked((uint)capability.ContractMajor),
                });
            }
        }

        hello.Parameters["system.os.family"] = hello.Os.Family;
        hello.Parameters["system.os.version"] = hello.Os.Version;
        hello.Parameters["system.os.arch"] = hello.Os.Arch;
        hello.Parameters["system.hostname"] = facts?.Hostname ?? Environment.MachineName;
        hello.Parameters["os.family"] = hello.Os.Family;
        hello.Parameters["os.version"] = hello.Os.Version;
        hello.Parameters["arch"] = hello.Os.Arch;
        hello.Parameters["hostname"] = facts?.Hostname ?? Environment.MachineName;
        hello.Parameters["machine.kind"] = "enrolled";
        return hello;
    }

    private static HostFacts ToProtocolFacts(PlatformFactSnapshot snapshot)
    {
        var result = new HostFacts
        {
            Family = snapshot.Family,
            ProductName = snapshot.ProductName ?? string.Empty,
            ProductVersion = snapshot.ProductVersion ?? string.Empty,
            ProductBuild = snapshot.ProductBuild ?? string.Empty,
            KernelVersion = snapshot.KernelVersion ?? string.Empty,
            OsArchitecture = snapshot.OsArchitecture,
            ProcessArchitecture = snapshot.ProcessArchitecture,
            Hostname = snapshot.Hostname,
            AgentPackageVersion = snapshot.PackageVersion,
            ObservedAtUnixMs = snapshot.ObservedAt.ToUnixTimeMilliseconds(),
            CollectorVersion = snapshot.CollectorVersion,
            Outcome = snapshot.Outcome switch
            {
                PlatformFactCollectionOutcome.Succeeded => HostFactsOutcome.Succeeded,
                PlatformFactCollectionOutcome.Partial => HostFactsOutcome.Partial,
                PlatformFactCollectionOutcome.Degraded => HostFactsOutcome.Degraded,
                PlatformFactCollectionOutcome.PermissionDenied => HostFactsOutcome.PermissionDenied,
                PlatformFactCollectionOutcome.TemporarilyUnavailable =>
                    HostFactsOutcome.TemporarilyUnavailable,
                PlatformFactCollectionOutcome.Failed => HostFactsOutcome.Failed,
                _ => HostFactsOutcome.Unspecified,
            },
            Complete = snapshot.Complete,
        };
        foreach (var pair in snapshot.Values.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            result.Values.Add(pair.Key, pair.Value);
        }
        result.Issues.Add(snapshot.Issues.Select(issue => new HostFactIssue
        {
            Code = issue.Code,
            Field = issue.Field,
            NativeCode = issue.NativeCode ?? string.Empty,
            Message = issue.Message ?? string.Empty,
        }));
        return result;
    }

    private static string? NormalizePackageDigest(string? digest) =>
        digest is { Length: 64 } &&
        digest.All(static value => value is >= '0' and <= '9' or >= 'a' and <= 'f')
            ? digest
            : null;

    private static bool IsLocalIdentity(string value) => value.Length == 32 &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string ValidateAuthToken(string value) =>
        value.Length is >= 32 and <= 512 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
            ? value
            : throw new InvalidDataException("persisted Agent credential is invalid");

    private async Task ConfirmUpgradeHealthAsync(
        UpgradeHealthAccepted accepted,
        SessionWriter writer,
        string sessionId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.UpgradeOperationId) ||
            string.IsNullOrWhiteSpace(options.UpgradeHealthMarkerPath) ||
            packageDigestSha256 is null)
        {
            throw new InvalidDataException(
                "controller acknowledged upgrade health for an Agent not started by an upgrade");
        }

        if (!string.Equals(accepted.OperationId, options.UpgradeOperationId, StringComparison.Ordinal) ||
            !string.Equals(accepted.PackageSha256, packageDigestSha256, StringComparison.Ordinal) ||
            !string.Equals(accepted.SessionId, sessionId, StringComparison.Ordinal) ||
            accepted.ConnectionGeneration == 0)
        {
            throw new InvalidDataException(
                "controller upgrade health acknowledgement does not match this Agent session");
        }

        var markerPath = Path.GetFullPath(options.UpgradeHealthMarkerPath);
        EnsurePathInsideDataDirectory(markerPath);

        var marker = new UpgradeMarker(
            2,
            "ready",
            options.UpgradeOperationId,
            packageDigestSha256,
            sessionId,
            accepted.ConnectionGeneration,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        DurableFile.ReplaceText(
            markerPath,
            System.Text.Json.JsonSerializer.Serialize(marker, UpgradeJsonOptions));

        var promotionPath = markerPath + ".promoted";
        var promotionDeadline = DateTimeOffset.UtcNow.AddSeconds(45);
        while (DateTimeOffset.UtcNow < promotionDeadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryReadUpgradeMarker(promotionPath, out var promotion) &&
                promotion.Stage == "promoted" &&
                string.Equals(promotion.OperationId, marker.OperationId, StringComparison.Ordinal) &&
                string.Equals(promotion.PackageSha256, marker.PackageSha256, StringComparison.Ordinal) &&
                string.Equals(promotion.SessionId, marker.SessionId, StringComparison.Ordinal) &&
                promotion.ConnectionGeneration == marker.ConnectionGeneration)
            {
                break;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }

        if (!TryReadUpgradeMarker(promotionPath, out var promoted) ||
            promoted.Stage != "promoted" ||
            !string.Equals(promoted.OperationId, marker.OperationId, StringComparison.Ordinal) ||
            !string.Equals(promoted.PackageSha256, marker.PackageSha256, StringComparison.Ordinal) ||
            !string.Equals(promoted.SessionId, marker.SessionId, StringComparison.Ordinal) ||
            promoted.ConnectionGeneration != marker.ConnectionGeneration)
        {
            throw new TimeoutException("bootstrap did not durably acknowledge candidate promotion");
        }

        await writer.SendAsync(new AgentMsg
        {
            UpgradeHealthConfirmed = new UpgradeHealthConfirmed
            {
                OperationId = accepted.OperationId,
                PackageSha256 = accepted.PackageSha256,
                SessionId = accepted.SessionId,
                ConnectionGeneration = accepted.ConnectionGeneration,
            },
        }, cancellationToken);
    }

    private async Task ConfirmUpgradeCommitAsync(
        UpgradeCommitAccepted accepted,
        SessionWriter writer,
        string sessionId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.UpgradeOperationId) ||
            string.IsNullOrWhiteSpace(options.UpgradeHealthMarkerPath) ||
            packageDigestSha256 is null ||
            !string.Equals(accepted.OperationId, options.UpgradeOperationId, StringComparison.Ordinal) ||
            !string.Equals(accepted.PackageSha256, packageDigestSha256, StringComparison.Ordinal) ||
            !string.Equals(accepted.SessionId, sessionId, StringComparison.Ordinal) ||
            accepted.ConnectionGeneration == 0)
        {
            throw new InvalidDataException("controller upgrade commit does not match this Agent session");
        }

        var markerPath = Path.GetFullPath(options.UpgradeHealthMarkerPath);
        EnsurePathInsideDataDirectory(markerPath);
        DurableFile.ReplaceText(
            markerPath,
            System.Text.Json.JsonSerializer.Serialize(new UpgradeMarker(
                2,
                "committed",
                accepted.OperationId,
                accepted.PackageSha256,
                accepted.SessionId,
                accepted.ConnectionGeneration,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), UpgradeJsonOptions));

        await writer.SendAsync(new AgentMsg
        {
            UpgradeCommitConfirmed = new UpgradeCommitConfirmed
            {
                OperationId = accepted.OperationId,
                PackageSha256 = accepted.PackageSha256,
                SessionId = accepted.SessionId,
                ConnectionGeneration = accepted.ConnectionGeneration,
            },
        }, cancellationToken);
    }

    private async Task ConfirmUpgradeFinalizationAsync(
        UpgradeCommitRecorded recorded,
        SessionWriter writer,
        string sessionId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.UpgradeOperationId) ||
            string.IsNullOrWhiteSpace(options.UpgradeHealthMarkerPath) ||
            packageDigestSha256 is null ||
            !string.Equals(recorded.OperationId, options.UpgradeOperationId, StringComparison.Ordinal) ||
            !string.Equals(recorded.PackageSha256, packageDigestSha256, StringComparison.Ordinal) ||
            !string.Equals(recorded.SessionId, sessionId, StringComparison.Ordinal) ||
            recorded.ConnectionGeneration == 0)
        {
            throw new InvalidDataException(
                "controller upgrade finalization does not match this Agent session");
        }

        var markerPath = Path.GetFullPath(options.UpgradeHealthMarkerPath);
        EnsurePathInsideDataDirectory(markerPath);
        DurableFile.ReplaceText(
            markerPath,
            System.Text.Json.JsonSerializer.Serialize(new UpgradeMarker(
                2,
                "server-confirmed",
                recorded.OperationId,
                recorded.PackageSha256,
                recorded.SessionId,
                recorded.ConnectionGeneration,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), UpgradeJsonOptions));

        await writer.SendAsync(new AgentMsg
        {
            UpgradeFinalizationConfirmed = new UpgradeFinalizationConfirmed
            {
                OperationId = recorded.OperationId,
                PackageSha256 = recorded.PackageSha256,
                SessionId = recorded.SessionId,
                ConnectionGeneration = recorded.ConnectionGeneration,
            },
        }, cancellationToken);
    }

    private void EnsurePathInsideDataDirectory(string path)
    {
        var dataRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.DataDir)) +
            Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!path.StartsWith(dataRoot, comparison))
        {
            throw new InvalidDataException("upgrade state path must stay inside the Agent data directory");
        }
    }

    private static bool TryReadUpgradeMarker(string path, out UpgradeMarker marker)
    {
        marker = default!;
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            marker = System.Text.Json.JsonSerializer.Deserialize<UpgradeMarker>(
                File.ReadAllText(path), UpgradeJsonOptions)!;
            return marker is not null && marker.SchemaVersion == 2 &&
                marker.OperationId is { Length: >= 1 and <= 128 } &&
                marker.PackageSha256 is { Length: 64 } &&
                marker.SessionId is { Length: >= 1 and <= 128 } &&
                marker.ConnectionGeneration > 0;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private Task MonitorBootstrapLeaseAsync(
        CancellationTokenSource leaseLost,
        CancellationToken cancellationToken)
    {
        if (options.BootstrapLeasePath is null || options.BootstrapLeaseId is null)
        {
            return Task.CompletedTask;
        }

        var leasePath = Path.GetFullPath(options.BootstrapLeasePath);
        EnsurePathInsideDataDirectory(leasePath);
        var expectedLeaseId = options.BootstrapLeaseId;
        if (expectedLeaseId.Length is < 16 or > 128 ||
            expectedLeaseId.Any(character => !char.IsAsciiLetterOrDigit(character)))
        {
            throw new InvalidDataException("bootstrap lease id is invalid");
        }

        return MonitorAsync();

        async Task MonitorAsync()
        {
            var initialGrace = Stopwatch.StartNew();
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                var nowUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                BootstrapLease? lease = null;
                try
                {
                    if (File.Exists(leasePath))
                    {
                        lease = System.Text.Json.JsonSerializer.Deserialize<BootstrapLease>(
                            File.ReadAllText(leasePath), UpgradeJsonOptions);
                    }
                }
                catch (Exception exception) when (exception is IOException or
                                                   UnauthorizedAccessException or
                                                   System.Text.Json.JsonException)
                {
                }

                var valid = lease is not null && lease.SchemaVersion == 1 &&
                    string.Equals(lease.LeaseId, expectedLeaseId, StringComparison.Ordinal) &&
                    lease.WrittenUnixMs <= nowUnixMs + TimeSpan.FromSeconds(5).TotalMilliseconds &&
                    nowUnixMs - lease.WrittenUnixMs <= TimeSpan.FromSeconds(15).TotalMilliseconds;
                if (valid || initialGrace.Elapsed < TimeSpan.FromSeconds(5))
                {
                    continue;
                }

                Console.Error.WriteLine("[agent] bootstrap supervision lease was lost; stopping");
                leaseLost.Cancel();
                return;
            }
        }
    }

    private static string BoundUpgradeFailureCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var bounded = new string(value.Trim()
            .Where(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-')
            .Take(64)
            .ToArray());
        return bounded;
    }

    private ulong ReadCredentialGeneration()
    {
        if (!File.Exists(CredentialGenerationPath))
        {
            return 0;
        }

        PrivateStorage.RestrictSecretFile(CredentialGenerationPath);
        var value = File.ReadAllText(CredentialGenerationPath).Trim();
        return ulong.TryParse(
            value,
            global::System.Globalization.NumberStyles.None,
            global::System.Globalization.CultureInfo.InvariantCulture,
            out var generation)
            ? generation
            : throw new InvalidDataException("the persisted credential generation is invalid");
    }

    private void PersistCredentialGeneration(ulong generation)
    {
        if (generation < credentialGeneration)
        {
            throw new InvalidDataException("the controller reported a stale credential generation");
        }

        PrivateStorage.WriteSecretText(
            CredentialGenerationPath,
            generation.ToString(global::System.Globalization.CultureInfo.InvariantCulture));
        credentialGeneration = generation;
    }

    private static string FirstMacAddress()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                            n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Select(n => n.GetPhysicalAddress().ToString())
                .FirstOrDefault(m => m.Length > 0) ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private sealed class ActiveBuild
    {
        public ActiveBuild(BuildAssignment assignment, CancellationTokenSource cancellation)
        {
            Assignment = assignment;
            Cancellation = cancellation;
        }

        public BuildAssignment Assignment { get; }
        public CancellationTokenSource Cancellation { get; }
        public string? CancellationReason { get; set; }
    }

    private sealed record UpgradeMarker(
        int SchemaVersion,
        string Stage,
        string OperationId,
        string PackageSha256,
        string SessionId,
        ulong ConnectionGeneration,
        long WrittenUnixMs);

    private sealed record BootstrapLease(
        int SchemaVersion,
        string LeaseId,
        long WrittenUnixMs);
}
