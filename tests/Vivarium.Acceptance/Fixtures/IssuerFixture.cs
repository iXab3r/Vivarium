// Adapted from EyeAuras.Depot 20238384013326cddb3296963a6d101a0970efc0; consumer-owned fixture policy.
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using CliWrap;
using EyeAuras.Automation;

namespace Vivarium.Acceptance.Fixtures;

/// <summary>Interactive private stdin protocol of the immutable published real EyeAuras OIDC fixture.</summary>
public sealed class IssuerFixture
{
    private readonly AutomationProcess process;
    private readonly StreamWriter input;
    private int receiptCount;
    private readonly AutomationScope scope;
    private IssuerFixture(AutomationScope scope, AutomationProcess process, StreamWriter input, string issuer, string clientId, string secret)
    { this.scope = scope; this.process = process; this.input = input; Issuer = issuer; ClientId = clientId; ClientSecret = secret; }
    public string Issuer { get; }
    public string ClientId { get; }
    public string ClientSecret { get; }
    public int ProcessId => process.ProcessId;

    /// <summary>Start the Automation-owned process before sending its private configuration; no secrets appear in argv or logs.</summary>
    public static async Task<IssuerFixture> StartAsync(AutomationScope scope, PostgresFixture postgres, RunTls tls, string redirectUri)
    {
        var issuer = $"https://127.0.0.1:{FixturePorts.Loopback()}/";
        var configuration = JsonSerializer.SerializeToElement(new
        {
            postgresConnectionString = await postgres.CreateIssuerDatabaseAsync(scope.CancellationToken),
            clientId = "vivarium-local", clientSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)),
            redirectUri, issuer, upstream = $"https://127.0.0.1:{FixturePorts.Loopback()}/",
            certificatePath = tls.CertificatePath, certificatePassword = tls.CertificatePassword,
            dataDirectory = Path.Combine(scope.WorkingDirectory, "issuer-state")
        });
        return await StartConfiguredAsync(scope, configuration, tls.Environment);
    }

    private static async Task<IssuerFixture> StartConfiguredAsync(AutomationScope scope, JsonElement configuration,
        IReadOnlyDictionary<string, string?> environment)
    {
        var issuer = configuration.GetProperty("issuer").GetString()!;
        if (!Uri.TryCreate(issuer, UriKind.Absolute, out var origin) || origin.Scheme != "https"
            || origin.Host != "127.0.0.1" || origin.UserInfo != "" || origin.Port < 1024 || origin.AbsolutePath != "/" || origin.Query != "" || origin.Fragment != "")
            throw new InvalidOperationException("The owned issuer must advertise a loopback HTTPS origin.");
        var path = System.Environment.GetEnvironmentVariable("VIVARIUM_ISSUER_PATH")
            ?? throw new InvalidOperationException("Prepare the pinned Linux issuer artifact first.");
        var fifo = Path.Combine(scope.WorkingDirectory, "issuer-input");
        await scope.ExecuteAsync(Cli.Wrap("mkfifo").WithArguments(["-m", "600", fifo]), TimeSpan.FromSeconds(5));
        var process = await scope.StartProcessAsync(Cli.Wrap("bash").WithArguments([
            "--noprofile", "--norc", "-c", "exec dotnet \"$1\" < \"$2\"", "vivarium-issuer", path, fifo
        ]).WithWorkingDirectory(scope.WorkingDirectory).WithEnvironmentVariables(environment));
        // Linux O_RDWR opens a FIFO without waiting for another reader; cancellation never leaves a blocked open task.
        var input = new StreamWriter(new FileStream(fifo, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite,
            4096, FileOptions.Asynchronous), new UTF8Encoding(false));
        scope.BeforeStop("Close private issuer stdin", async (_, token) =>
        {
            await input.DisposeAsync();
            var status = await process.WaitForExitAsync(TimeSpan.FromSeconds(10), token);
            if (status.ExitCode != 0) throw new InvalidOperationException("Owned issuer exited unsuccessfully.");
        }, timeoutSeconds: 15);
        var fixture = new IssuerFixture(scope, process, input, issuer,
            configuration.GetProperty("clientId").GetString()!, configuration.GetProperty("clientSecret").GetString()!);
        await fixture.WriteAsync(configuration);
        using var ready = await fixture.ReceiptAsync("ready");
        return fixture;
    }

    /// <summary>Set only the synthetic upstream persona; the real issuer still performs its ordinary login and local-user creation.</summary>
    public async Task SetPersonaAsync(string subject)
    {
        await WriteAsync(new { command = "persona", subject, email = subject + "@example.test", emailVerified = true, displayName = subject });
        using var receipt = await ReceiptAsync("persona_set");
    }

    /// <summary>Return the sanitized real-issuer local-user count receipt.</summary>
    public async Task<int> CountUsersAsync()
    {
        await WriteAsync(new { command = "countUsers" });
        using var receipt = await ReceiptAsync("users");
        return receipt.RootElement.GetProperty("count").GetInt32();
    }

    /// <summary>Read the real issuer subject assigned to a synthetic upstream persona after ordinary login.</summary>
    public async Task<string?> FindUserAsync(string subject)
    {
        await WriteAsync(new { command = "findUser", provider = "Google", subject });
        using var receipt = await ReceiptAsync("user");
        return receipt.RootElement.GetProperty("userId").GetString();
    }

    private async Task WriteAsync<T>(T message)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(scope.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        await input.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), deadline.Token);
        await input.FlushAsync(deadline.Token);
    }

    private async Task<JsonDocument> ReceiptAsync(string expected)
    {
        return await AutomationWait.UntilAsync(async token =>
        {
            var status = await process.GetStatusAsync(token);
            if (status.StandardOutputTruncated || status.StandardErrorTruncated)
                throw new InvalidOperationException("Owned issuer protocol exceeded its output bound.");
            var output = (await process.ReadDiagnosticsAsync(token)).StandardOutput;
            var complete = output.Split('\n');
            if (receiptCount >= complete.Length - 1)
            {
                if (status.HasExited) throw new InvalidOperationException("Owned issuer exited before the expected receipt.");
                return null;
            }
            JsonDocument result;
            try { result = JsonDocument.Parse(complete[receiptCount++]); }
            catch (JsonException) { throw new InvalidOperationException("Owned issuer returned a malformed receipt."); }
            if (result.RootElement.GetProperty("event").GetString() != expected)
            { result.Dispose(); throw new InvalidOperationException("Owned issuer returned an unexpected receipt."); }
            return result;
        }, value => value is not null, "owned issuer receipt", TimeSpan.FromSeconds(45), scope.CancellationToken)
            ?? throw new InvalidOperationException("Owned issuer receipt was absent.");
    }
}
