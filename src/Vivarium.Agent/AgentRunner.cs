using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
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
    public static readonly string Version =
        typeof(AgentRunner).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    private readonly AgentOptions options;
    private readonly BlobClient blobs;
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

    public string AgentId { get; }

    /// <summary>Server minus local clock, from the last Welcome (D4). Applying it is a later, elevated concern.</summary>
    public TimeSpan ClockSkew { get; private set; }

    public AgentRunner(AgentOptions options)
    {
        this.options = options;
        PrivateStorage.EnsureDirectory(options.DataDir);

        var idPath = Path.Combine(options.DataDir, "agent-id");
        if (File.Exists(idPath))
        {
            AgentId = File.ReadAllText(idPath).Trim();
        }
        else
        {
            AgentId = Guid.NewGuid().ToString("N");
            File.WriteAllText(idPath, AgentId);
        }

        var tokenPath = TokenPath;
        if (File.Exists(tokenPath))
        {
            PrivateStorage.RestrictSecretFile(tokenPath);
        }

        authToken = File.Exists(tokenPath) ? File.ReadAllText(tokenPath).Trim() : null;
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
    }

    private string TokenPath => Path.Combine(options.DataDir, "auth.token");
    private string PendingResultPath => Path.Combine(options.DataDir, "pending-build-result.pb");

    public Task WaitAuthorizedAsync(TimeSpan timeout) => authorized.Task.WaitAsync(timeout);

    public async Task RunAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, restartRequested.Token);
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
                        if (msg.Welcome.Authorized)
                        {
                            authorized.TrySetResult(true);
                        }

                        // A build finished while we were disconnected? Deliver it now (D4).
                        await TryFlushPendingResultAsync(ct);
                        break;

                    case ControllerMsg.MsgOneofCase.Authorized:
                        authToken = msg.Authorized.AuthToken;
                        PrivateStorage.WriteSecretText(TokenPath, authToken);
                        blobs.BearerToken = authToken;
                        authorized.TrySetResult(true);
                        break;

                    case ControllerMsg.MsgOneofCase.Build:
                        await AcceptBuildAsync(msg.Build);
                        break;

                    case ControllerMsg.MsgOneofCase.Cancel:
                        CancelBuild(msg.Cancel);
                        break;

                    case ControllerMsg.MsgOneofCase.ResultAccepted:
                        AcceptResult(msg.ResultAccepted);
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
                Family = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux",
                Version = Environment.OSVersion.Version.ToString(),
                Arch = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
            },
            Interactive = Environment.UserInteractive,
            RunningBuildId = runningBuildId ?? string.Empty,
        };
        hello.Parameters["os.family"] = hello.Os.Family;
        hello.Parameters["os.version"] = hello.Os.Version;
        hello.Parameters["arch"] = hello.Os.Arch;
        hello.Parameters["hostname"] = Environment.MachineName;
        hello.Parameters["machine.kind"] = "enrolled";
        return hello;
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
}
