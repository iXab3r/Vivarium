using System.Security.Authentication;
using System.Text;
using Grpc.Core;
using Vivarium.Cli.Configuration;
using Vivarium.Contracts.V1;

namespace Vivarium.Cli;

internal sealed class VivariumCliApplication(
    ICliConsole console,
    IClientConfigurationStore configurationStore,
    IServerCertificateProbe certificateProbe,
    IControlPlaneEndpointFactory endpointFactory,
    ITemporaryPayloadArchiveFactory archiveFactory,
    Func<string, string?> environment)
{
    public static VivariumCliApplication CreateDefault() => new(
        new SystemCliConsole(),
        new UserClientConfigurationStore(),
        new ServerCertificateProbe(),
        new ControlPlaneEndpointFactory(),
        new TemporaryPayloadArchiveFactory(),
        Environment.GetEnvironmentVariable);

    public async Task<int> ExecuteAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        try
        {
            return await ExecuteCoreAsync(CliArguments.Parse(args), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 130;
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            return 130;
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            console.WriteError($"error: {FailureMessage(exception)}");
            return 2;
        }
    }

    private async Task<int> ExecuteCoreAsync(CliCommand command, CancellationToken cancellationToken) =>
        command switch
        {
            VersionCommand => PrintVersion(),
            LoginCommand login => await LoginAsync(login, cancellationToken),
            RunCommand run => await RunAsync(run, cancellationToken),
            CancelCommand cancel => await CancelAsync(cancel, cancellationToken),
            AgentUpgradeCommand upgrade => await UpgradeAgentAsync(upgrade, cancellationToken),
            AgentUpgradeStatusCommand status =>
                await ShowAgentUpgradeAsync(status, cancellationToken),
            AgentUpgradeCancellationCommand cancellation =>
                await CancelAgentUpgradeAsync(cancellation, cancellationToken),
            _ => throw new InvalidOperationException("unsupported command"),
        };

    private int PrintVersion()
    {
        var version = typeof(VivariumCliApplication).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        console.WriteLine($"viv {version}");
        return 0;
    }

    private async Task<int> LoginAsync(LoginCommand command, CancellationToken cancellationToken)
    {
        var url = PinnedTls.NormalizeControllerUrl(command.Url);
        var observedFingerprint = await certificateProbe.GetFingerprintAsync(url, cancellationToken);
        console.WriteLine($"Controller:  {url}");
        console.WriteLine($"Certificate: {observedFingerprint}");

        if (command.Fingerprint is not null)
        {
            var expected = PinnedTls.NormalizeFingerprint(command.Fingerprint);
            if (!expected.Equals(observedFingerprint, StringComparison.Ordinal))
            {
                throw new AuthenticationException(
                    $"controller certificate does not match the supplied fingerprint (observed {observedFingerprint})");
            }
        }
        else
        {
            if (!console.IsInteractive)
            {
                throw new InvalidOperationException(
                    "interactive certificate confirmation is required; supply --fingerprint in non-interactive use");
            }

            console.WriteError("Trust this certificate? Type 'yes' to continue:");
            var confirmation = await console.ReadLineAsync(cancellationToken);
            if (!string.Equals(confirmation?.Trim(), "yes", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("certificate was not confirmed");
            }
        }

        var token = command.Token;
        if (string.IsNullOrWhiteSpace(token))
        {
            if (!console.IsInteractive)
            {
                throw new InvalidOperationException("--token is required in non-interactive use");
            }

            console.WriteError("Submit or admin token:");
            token = await console.ReadSecretAsync(cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("controller token must not be empty");
        }

        var settings = new EndpointSettings(url, observedFingerprint, token);
        await using (var endpoint = endpointFactory.Create(settings))
        {
            await endpoint.ValidateAsync(cancellationToken);
        }

        await configurationStore.SaveAsync(
            new ClientConfiguration(url, observedFingerprint, token), cancellationToken);
        console.WriteLine("Login saved.");
        return 0;
    }

    private async Task<int> RunAsync(RunCommand command, CancellationToken cancellationToken)
    {
        var saved = await configurationStore.LoadAsync(cancellationToken);
        var settings = EndpointSettingsResolver.Resolve(
            command.Url, command.Token, command.Fingerprint, environment, saved);

        var definitionPath = Path.GetFullPath(command.FilePath);
        var definitionBytes = await File.ReadAllBytesAsync(definitionPath, cancellationToken);
        string yaml;
        try
        {
            var content = definitionBytes.AsSpan();
            if (content.Length >= 3 &&
                content[0] == 0xEF &&
                content[1] == 0xBB &&
                content[2] == 0xBF)
            {
                content = content[3..];
            }

            yaml = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(content);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidOperationException("vivarium.yaml must be valid UTF-8", exception);
        }

        var configurationRoot = Path.GetDirectoryName(definitionPath)
            ?? throw new InvalidOperationException("vivarium.yaml must have a parent directory");
        var resolved = VivariumDefinitionParser.Parse(
            yaml, configurationRoot, command.Configuration, command.OnlyCells);

        await using var archives = await archiveFactory.CreateAsync(
            resolved.Cells, cancellationToken);
        await using var endpoint = endpointFactory.Create(settings);

        var requestId = Guid.NewGuid().ToString("D");
        var request = BuildRequestMapper.Create(
            resolved, definitionBytes, archives.Archives, requestId);
        var stagingId = await endpoint.StageBlobsAsync(
            resolved.Project,
            archives.Archives.Values.ToArray(),
            requestId,
            cancellationToken);
        var submitted = await endpoint.SubmitBuildAsync(request, stagingId, cancellationToken);
        if (string.IsNullOrWhiteSpace(submitted.BuildId))
        {
            throw new InvalidOperationException("controller returned an empty matrix build id");
        }

        console.WriteLine($"Submitted matrix build {submitted.BuildId}");
        console.WriteLine($"Results: {settings.Url.TrimEnd('/')}/builds/{Uri.EscapeDataString(submitted.BuildId)}");
        if (command.NoWait)
        {
            return 0;
        }

        var snapshot = await WatchUntilFinishedAsync(endpoint, submitted.BuildId, cancellationToken);
        return AggregateExitCode(snapshot);
    }

    private async Task<int> CancelAsync(
        CancelCommand command,
        CancellationToken cancellationToken)
    {
        var saved = await configurationStore.LoadAsync(cancellationToken);
        var settings = EndpointSettingsResolver.Resolve(
            command.Url, command.Token, command.Fingerprint, environment, saved);
        await using var endpoint = endpointFactory.Create(settings);
        var snapshot = await endpoint.CancelBuildAsync(
            command.BuildId, command.Reason, cancellationToken);

        console.WriteLine($"Cancellation requested for matrix build {snapshot.Build.BuildId}");
        console.WriteLine($"State: {snapshot.State.ToString().ToUpperInvariant()}");
        console.WriteLine($"Results: {settings.Url.TrimEnd('/')}/builds/{Uri.EscapeDataString(snapshot.Build.BuildId)}");
        return 0;
    }

    private async Task<int> UpgradeAgentAsync(
        AgentUpgradeCommand command,
        CancellationToken cancellationToken)
    {
        var saved = await configurationStore.LoadAsync(cancellationToken);
        var settings = EndpointSettingsResolver.Resolve(
            command.Url, command.Token, command.Fingerprint, environment, saved);
        await using var endpoint = endpointFactory.Create(settings);
        var operation = await endpoint.CreateAgentUpgradeAsync(
            command.AgentId,
            command.Reason,
            command.TimeoutSeconds,
            Guid.NewGuid().ToString("D"),
            cancellationToken);
        PrintAgentUpgrade(operation);
        if (command.NoWait || operation.IsTerminal)
        {
            return AgentUpgradeExitCode(operation);
        }

        var lastState = operation.State;
        while (!operation.IsTerminal)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            operation = await endpoint.GetAgentUpgradeAsync(
                operation.OperationId, cancellationToken);
            if (!string.Equals(operation.State, lastState, StringComparison.Ordinal))
            {
                PrintAgentUpgrade(operation);
                lastState = operation.State;
            }
        }

        return AgentUpgradeExitCode(operation);
    }

    private async Task<int> ShowAgentUpgradeAsync(
        AgentUpgradeStatusCommand command,
        CancellationToken cancellationToken)
    {
        var saved = await configurationStore.LoadAsync(cancellationToken);
        var settings = EndpointSettingsResolver.Resolve(
            command.Url, command.Token, command.Fingerprint, environment, saved);
        await using var endpoint = endpointFactory.Create(settings);
        var operation = await endpoint.GetAgentUpgradeAsync(
            command.OperationId, cancellationToken);
        PrintAgentUpgrade(operation);
        return AgentUpgradeExitCode(operation);
    }

    private async Task<int> CancelAgentUpgradeAsync(
        AgentUpgradeCancellationCommand command,
        CancellationToken cancellationToken)
    {
        var saved = await configurationStore.LoadAsync(cancellationToken);
        var settings = EndpointSettingsResolver.Resolve(
            command.Url, command.Token, command.Fingerprint, environment, saved);
        await using var endpoint = endpointFactory.Create(settings);
        var operation = await endpoint.CancelAgentUpgradeAsync(
            command.OperationId, command.Reason, cancellationToken);
        PrintAgentUpgrade(operation);
        if (command.NoWait || operation.IsTerminal)
        {
            return AgentUpgradeExitCode(operation);
        }

        var lastState = operation.State;
        while (!operation.IsTerminal)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            operation = await endpoint.GetAgentUpgradeAsync(
                operation.OperationId, cancellationToken);
            if (!string.Equals(operation.State, lastState, StringComparison.Ordinal))
            {
                PrintAgentUpgrade(operation);
                lastState = operation.State;
            }
        }
        return AgentUpgradeExitCode(operation);
    }

    private void PrintAgentUpgrade(AgentUpgradeSnapshot operation)
    {
        console.WriteLine($"Agent upgrade {operation.OperationId}: {operation.State.ToUpperInvariant()}");
        console.WriteLine($"Agent: {operation.AgentId}");
        console.WriteLine($"Server release: {operation.PackageVersion}");
        console.WriteLine($"Maintenance drain: {(operation.DrainHeld ? "HELD" : "released")}");
        console.WriteLine($"Restart attempts: {operation.RestartAttempts}");
        if (operation.LastDispatchConnectionGeneration is { } dispatchGeneration)
        {
            console.WriteLine($"Last restart generation: {dispatchGeneration}");
        }
        if (operation.NextRestartAt is { } nextRestart)
        {
            console.WriteLine($"Next restart retry: {nextRestart:O}");
        }
        if (!string.IsNullOrWhiteSpace(operation.CancellationReason))
        {
            console.WriteLine($"Cancellation: {operation.CancellationReason}");
        }
        if (!string.IsNullOrWhiteSpace(operation.FailureCode))
        {
            console.WriteLine($"Failure: {operation.FailureCode}");
        }
        if (operation.Events.Count > 0)
        {
            console.WriteLine("History:");
            foreach (var value in operation.Events.OrderBy(value => value.Sequence))
            {
                var generation = value.ConnectionGeneration is { } observed
                    ? $" generation={observed}"
                    : string.Empty;
                console.WriteLine($"  {value.Sequence}: {value.Phase} ({value.Code}){generation}");
            }
        }
    }

    private static int AgentUpgradeExitCode(AgentUpgradeSnapshot operation) =>
        operation.State == "succeeded" ? 0 : operation.IsTerminal ? 1 : 0;

    private async Task<BuildSnapshot> WatchUntilFinishedAsync(
        IControlPlaneEndpoint endpoint,
        string buildId,
        CancellationToken cancellationToken)
    {
        var transitions = new BuildTransitionPrinter(console);
        var delay = TimeSpan.FromMilliseconds(250);
        while (true)
        {
            try
            {
                BuildSnapshot? last = null;
                await foreach (var snapshot in endpoint.WatchBuildAsync(buildId, cancellationToken))
                {
                    transitions.Print(snapshot);
                    last = snapshot;
                    delay = TimeSpan.FromMilliseconds(250);
                    if (snapshot.State == DurableBuildState.Finished)
                    {
                        return snapshot;
                    }
                }

                if (last?.State == DurableBuildState.Finished)
                {
                    return last;
                }

                throw new IOException("build watch ended before the build reached a terminal state");
            }
            catch (Exception exception) when (
                IsTransientWatchFailure(exception) && !cancellationToken.IsCancellationRequested)
            {
                console.WriteError($"watch disconnected; reconnecting in {delay.TotalSeconds:0.##}s");
                await Task.Delay(delay, cancellationToken);
                delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 5000));
            }
        }
    }

    internal static int AggregateExitCode(BuildSnapshot snapshot) =>
        snapshot.State == DurableBuildState.Finished &&
        snapshot.Cells.Count > 0 &&
        snapshot.Cells.All(cell => cell.Outcome == BuildOutcome.Succeeded)
            ? 0
            : 1;

    private static bool IsTransientWatchFailure(Exception exception) => exception switch
    {
        IOException => true,
        HttpRequestException => true,
        RpcException rpc => rpc.StatusCode is
            StatusCode.Unavailable or
            StatusCode.Internal or
            StatusCode.DeadlineExceeded or
            StatusCode.ResourceExhausted or
            StatusCode.Cancelled,
        _ => false,
    };

    private static bool IsExpectedFailure(Exception exception) => exception is
        CliUsageException or
        VivariumConfigurationException or
        InvalidOperationException or
        IOException or
        UnauthorizedAccessException or
        HttpRequestException or
        RpcException or
        AuthenticationException;

    private static string FailureMessage(Exception exception) => exception is RpcException rpc
        ? $"controller RPC failed ({rpc.StatusCode}): {rpc.Status.Detail}"
        : exception.Message;

    private sealed class BuildTransitionPrinter(ICliConsole console)
    {
        private AggregateTransition? aggregate;
        private readonly Dictionary<string, CellTransition> cells = new(StringComparer.Ordinal);

        public void Print(BuildSnapshot snapshot)
        {
            var nextAggregate = new AggregateTransition(snapshot.State, snapshot.Outcome);
            if (nextAggregate != aggregate)
            {
                console.WriteLine($"matrix: {Format(snapshot.State, snapshot.Outcome)}");
                aggregate = nextAggregate;
            }

            foreach (var cell in snapshot.Cells)
            {
                var next = new CellTransition(
                    cell.State, cell.Outcome, cell.AgentId, cell.StatusText);
                if (cells.TryGetValue(cell.BuildId, out var previous) && next == previous)
                {
                    continue;
                }

                var agent = string.IsNullOrEmpty(cell.AgentId) ? string.Empty : $" on {cell.AgentId}";
                var status = string.IsNullOrEmpty(cell.StatusText) ? string.Empty : $" — {cell.StatusText}";
                console.WriteLine($"  {cell.Name}: {Format(cell.State, cell.Outcome)}{agent}{status}");
                cells[cell.BuildId] = next;
            }
        }

        private static string Format(DurableBuildState state, BuildOutcome outcome) =>
            state == DurableBuildState.Finished
                ? $"FINISHED/{outcome.ToString().ToUpperInvariant()}"
                : state.ToString().ToUpperInvariant();

        private sealed record AggregateTransition(DurableBuildState State, BuildOutcome Outcome);
        private sealed record CellTransition(
            DurableBuildState State,
            BuildOutcome Outcome,
            string AgentId,
            string StatusText);
    }
}
