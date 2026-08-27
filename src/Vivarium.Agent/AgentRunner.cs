using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Grpc.Core;
using Grpc.Net.Client;
using Vivarium.Contracts.V1;

namespace Vivarium.Agent;

/// <summary>
/// The whole agent: reverse-connect session loop (D1), deliberately dumb — every decision lives in
/// the controller. Reconnects forever; a restart request ends RunAsync so the launcher can swap us (D2).
/// </summary>
public sealed class AgentRunner
{
    public static readonly string Version =
        typeof(AgentRunner).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    private readonly AgentOptions options;
    private readonly BlobClient blobs;
    private readonly TaskCompletionSource<bool> authorized = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource restartRequested = new();
    private string? authToken;
    private volatile string? runningBuildId;

    public string AgentId { get; }

    /// <summary>Server minus local clock, from the last Welcome (D4). Applying it is a later, elevated concern.</summary>
    public TimeSpan ClockSkew { get; private set; }

    public AgentRunner(AgentOptions options)
    {
        this.options = options;
        Directory.CreateDirectory(options.DataDir);

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
        authToken = File.Exists(tokenPath) ? File.ReadAllText(tokenPath).Trim() : null;
        blobs = new BlobClient(options.ControllerUrl, options.CertFingerprintSha256) { BearerToken = authToken };
    }

    private string TokenPath => Path.Combine(options.DataDir, "auth.token");

    public Task WaitAuthorizedAsync(TimeSpan timeout) => authorized.Task.WaitAsync(timeout);

    public async Task RunAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, restartRequested.Token);
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

                        break;

                    case ControllerMsg.MsgOneofCase.Authorized:
                        authToken = msg.Authorized.AuthToken;
                        File.WriteAllText(TokenPath, authToken);
                        blobs.BearerToken = authToken;
                        authorized.TrySetResult(true);
                        break;

                    case ControllerMsg.MsgOneofCase.Build:
                        var assignment = msg.Build;
                        _ = Task.Run(() => ExecuteBuildAsync(assignment, writer, sessionId, sessionCts.Token), CancellationToken.None);
                        break;

                    case ControllerMsg.MsgOneofCase.Restart:
                        restartRequested.Cancel();
                        return;
                }
            }
        }
        finally
        {
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

    private async Task ExecuteBuildAsync(BuildAssignment assignment, SessionWriter writer, string sessionId, CancellationToken ct)
    {
        runningBuildId = assignment.BuildId;
        try
        {
            var workRoot = Path.Combine(options.DataDir, "builds");
            var result = await BuildExecutor.ExecuteAsync(workRoot, assignment, blobs, writer, sessionId, ct);
            await writer.SendAsync(new AgentMsg { Result = result }, ct);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[agent] build {assignment.BuildId} failed: {ex}");
        }
        finally
        {
            runningBuildId = null;
        }
    }

    private async Task HeartbeatLoopAsync(SessionWriter writer, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(options.HeartbeatInterval);
        while (await timer.WaitForNextTickAsync(ct))
        {
            await writer.SendAsync(new AgentMsg
            {
                Heartbeat = new Heartbeat { RunningBuildId = runningBuildId ?? string.Empty },
            }, ct);
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
}
