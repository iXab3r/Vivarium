using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Vivarium.Agent;
using Vivarium.Contracts.V1;
using Vivarium.Controller.Security;

namespace Vivarium.Tests;

// Transport/Agent SDK evidence only. This bounded peer records literal frames; it implements no
// Build, IAM, PostgreSQL, output-store or TeamCity model and cannot satisfy R0 product acceptance.
public sealed class DurableOutputAgentTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task Real_Agent_keeps_masked_output_after_result_ACK_and_replays_without_rerunning(bool coldRestart)
    {
        var root = Path.Combine(Path.GetTempPath(), "vivarium-output-agent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var certificate = ControllerCertificate.LoadOrCreate(root);
            using var certificateLifetime = certificate.Certificate;
            var marker = Path.Combine(root, "execution-count.txt");
            var state = new OutputPeerState(marker);
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders(); builder.Services.AddGrpc(); builder.Services.AddSingleton(state);
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listener =>
            { listener.Protocols = HttpProtocols.Http2; listener.UseHttps(certificate.Certificate); }));
            await using var app = builder.Build();
            app.MapGrpcService<OutputPeer>(); await app.StartAsync();
            try
            {
                var origin = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
                var data = Path.Combine(root, "agent");
                Directory.CreateDirectory(data);
                File.WriteAllText(Path.Combine(data, "agent-id"), Guid.NewGuid().ToString("N"));
                File.WriteAllText(Path.Combine(data, "auth.token"), new string('a', 48));
                var options = new AgentOptions { ControllerUrl = origin,
                    CertFingerprintSha256 = certificate.FingerprintSha256, DataDir = data,
                    HeartbeatInterval = TimeSpan.FromMilliseconds(100), ReconnectDelay = TimeSpan.FromMilliseconds(50) };
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var cancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                var running = new AgentRunner(options).RunAsync(cancellation.Token);
                try
                {
                    var result = await state.Result.Task.WaitAsync(cancellation.Token);
                    var reconnect = await state.Reconnected.Task.WaitAsync(cancellation.Token);
                    await state.Replayed.Task.WaitAsync(cancellation.Token);
                    Assert.That(result.Outcome, Is.EqualTo(BuildOutcome.Succeeded));
                    Assert.That(result.OutputProducerLost, Is.False);
                    Assert.That(result.OutputWatermarks.Count, Is.EqualTo(2));
                    Assert.That(reconnect.RunningBuildId, Is.EqualTo(state.Assignment.BuildId));
                    Assert.That(File.Exists(Path.Combine(data, "pending-build-result.pb")), Is.True,
                        "The result ACK and stale output ACKs must not retire unacknowledged output.");
                    Assert.That(await File.ReadAllTextAsync(marker), Is.EqualTo("run\n"));
                    foreach (var path in Directory.EnumerateFiles(Path.Combine(data, "build-output"), "*", SearchOption.AllDirectories))
                        Assert.That(Encoding.UTF8.GetString(File.ReadAllBytes(path)), Does.Not.Contain("SECRET"));
                    if (coldRestart)
                    {
                        await cancellation.CancelAsync(); await running.WaitAsync(TimeSpan.FromSeconds(20)); cancellation.Dispose();
                        cancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                        running = new AgentRunner(options).RunAsync(cancellation.Token);
                        var coldHello = await state.ColdReconnected.Task.WaitAsync(deadline.Token);
                        await state.ColdReplayed.Task.WaitAsync(deadline.Token);
                        Assert.That(coldHello.RunningBuildId, Is.EqualTo(state.Assignment.BuildId));
                        Assert.That(File.Exists(Path.Combine(data, "pending-build-result.pb")), Is.True);
                    }
                    state.PermitOutputAck = true;
                    await state.Released.Task.WaitAsync(cancellation.Token);
                    Assert.That(File.Exists(Path.Combine(data, "pending-build-result.pb")), Is.False);
                    Assert.That(Directory.EnumerateFiles(Path.Combine(data, "build-output"), "*", SearchOption.AllDirectories), Is.Empty);
                    Assert.That(await File.ReadAllTextAsync(marker), Is.EqualTo("run\n"), "Duplicate assignment cannot execute twice.");
                    Assert.That(state.Bytes(LogStream.Stdout), Is.EqualTo("prefix-[REDACTED]-tail"u8.ToArray()));
                    Assert.That(state.Bytes(LogStream.Stderr), Is.EqualTo("stderr-control"u8.ToArray()));
                }
                finally
                {
                    await cancellation.CancelAsync();
                    await running.WaitAsync(TimeSpan.FromSeconds(20));
                    cancellation.Dispose();
                }
            }
            finally { await app.StopAsync(); }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    internal sealed class OutputPeerState
    {
        internal OutputPeerState(string marker)
        {
            Assignment = new BuildAssignment { BuildId = Guid.NewGuid().ToString("N"), OutputContractMajor = 1 };
            Assignment.OutputSecrets.Add(ByteString.CopyFromUtf8("SECRET"));
            var step = new Step { Program = OperatingSystem.IsWindows() ? "powershell.exe" : "/bin/sh" };
            if (OperatingSystem.IsWindows())
                step.Args.Add(["-NoLogo", "-NoProfile", "-NonInteractive", "-Command",
                    "[IO.File]::AppendAllText($env:OWNED_MARKER,\"run`n\");[Console]::Out.Write('prefix-SECRET-tail');[Console]::Error.Write('stderr-control')"]);
            else step.Args.Add(["-c", "printf 'run\\n' >> \"$OWNED_MARKER\"; printf 'prefix-SECRET-tail'; printf 'stderr-control' >&2"]);
            step.Env.Add("OWNED_MARKER", marker); Assignment.Steps.Add(step);
        }
        internal BuildAssignment Assignment { get; }
        internal int Sessions;
        internal string FirstSession = "";
        internal volatile bool PermitOutputAck;
        internal TaskCompletionSource<BuildResult> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<Hello> Reconnected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> Replayed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<Hello> ColdReconnected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> ColdReplayed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ConcurrentDictionary<(LogStream Stream, ulong Start), BuildOutput> Frames { get; } = new();
        internal byte[] Bytes(LogStream stream) => Frames.Values.Where(f => f.Stream == stream)
            .OrderBy(f => f.SourceSequenceStart).SelectMany(f => f.Data.ToByteArray()).ToArray();
    }

    internal sealed class OutputPeer(OutputPeerState state) : AgentHub.AgentHubBase
    {
        public override async Task Session(IAsyncStreamReader<AgentMsg> input, IServerStreamWriter<ControllerMsg> output,
            ServerCallContext context)
        {
            if (!await input.MoveNext(context.CancellationToken)) return;
            var hello = input.Current.Hello.Clone();
            var session = Interlocked.Increment(ref state.Sessions);
            if (session == 1) state.FirstSession = hello.SessionId;
            var welcome = new Welcome { Authorized = true, SelectedProtocolVersion = 1, CurrentProtocolVersion = 1,
                MinimumProtocolVersion = 1, CredentialGeneration = 1, ConnectionGeneration = (ulong)session };
            welcome.NegotiatedCapabilities.Add(new CapabilitySupport { CapabilityId = "teamcity.build-runner.v1", ContractMajor = 1 });
            welcome.NegotiatedCapabilities.Add(new CapabilitySupport { CapabilityId = "teamcity.build-output.v1", ContractMajor = 1 });
            await output.WriteAsync(new ControllerMsg { Welcome = welcome });
            await output.WriteAsync(new ControllerMsg { Build = state.Assignment.Clone() });
            if (session > 1) state.Reconnected.TrySetResult(hello);
            if (session > 2) state.ColdReconnected.TrySetResult(hello);
            await foreach (var message in input.ReadAllAsync(context.CancellationToken))
            {
                switch (message.MsgCase)
                {
                    case AgentMsg.MsgOneofCase.Result:
                        state.Result.TrySetResult(message.Result.Clone());
                        await output.WriteAsync(new ControllerMsg { ResultAccepted = new BuildResultAccepted
                        { BuildId = message.Result.BuildId, SessionId = hello.SessionId } });
                        if (session == 1) throw new RpcException(new Status(StatusCode.Unavailable, "Owned disconnect after result ACK."));
                        break;
                    case AgentMsg.MsgOneofCase.BuildOutput:
                        var frame = message.BuildOutput.Clone();
                        if (frame.SessionId != hello.SessionId || frame.BuildId != state.Assignment.BuildId)
                            throw new RpcException(new Status(StatusCode.InvalidArgument, "Owned peer received an incorrect fence."));
                        state.Frames.AddOrUpdate((frame.Stream, frame.SourceSequenceStart), frame, (_, previous) =>
                        {
                            if (!previous.Data.Equals(frame.Data) || previous.SourceSequenceEndExclusive != frame.SourceSequenceEndExclusive)
                                throw new InvalidDataException("Output retry changed its bytes.");
                            return frame;
                        });
                        if (session > 1)
                        {
                            await output.WriteAsync(new ControllerMsg { BuildOutputAcknowledged = new BuildOutputAcknowledged
                            { BuildId = frame.BuildId, SessionId = state.PermitOutputAck ? hello.SessionId : state.FirstSession,
                                StepIndex = frame.StepIndex, Stream = frame.Stream, SequenceExclusive = frame.SourceSequenceEndExclusive,
                                ProducedBytes = frame.SourceByteEndExclusive } });
                            if (state.Frames.Values.Select(f => f.Stream).Distinct().Count() == 2) state.Replayed.TrySetResult(true);
                            if (session > 2 && state.Frames.Values.Where(f => f.SessionId == hello.SessionId)
                                .Select(f => f.Stream).Distinct().Count() == 2) state.ColdReplayed.TrySetResult(true);
                        }
                        break;
                    case AgentMsg.MsgOneofCase.Heartbeat:
                        if (state.PermitOutputAck && state.Result.Task.IsCompleted && message.Heartbeat.RunningBuildId.Length == 0)
                            state.Released.TrySetResult(true);
                        break;
                }
            }
        }
    }
}
