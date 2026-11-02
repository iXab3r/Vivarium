using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Vivarium.Controller;

namespace Vivarium.Tests.TeamCity;

// Existing production Kestrel composition: real routes, auth, Git and transitional SQLite.
// No compatible routes, test authentication scheme or database rewrites are installed here.
internal sealed class CurrentController : IAsyncDisposable
{
    public string Root { get; } = Path.Combine(
        Path.GetTempPath(), "vivarium-teamcity-conformance", Guid.NewGuid().ToString("N"));
    public VivariumControllerHost Host { get; private set; } = null!;
    public HttpClient Http { get; private set; } = null!;

    public async Task StartAsync()
    {
        Host = await VivariumControllerHost.StartAsync(new ControllerOptions
        {
            DataDir = Root,
            Host = "127.0.0.1",
            Port = 0,
        });
        Http = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                certificate is not null &&
                Convert.ToHexString(SHA256.HashData(certificate.RawData)).Equals(
                    Host.Certificate.FingerprintSha256, StringComparison.OrdinalIgnoreCase),
        }) { BaseAddress = new Uri(Host.Url), Timeout = TimeSpan.FromSeconds(15) };
        Http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", Host.Tokens.AdminToken);
        await AssertReadyAsync();
    }

    public async Task AssertReadyAsync()
    {
        using var ready = await Http.GetAsync("/api/v1/system");
        Assert.That(ready.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            "harness readiness: real authenticated native management route");
        using var document = JsonDocument.Parse(await ready.Content.ReadAsStringAsync());
        Assert.That(document.RootElement.GetProperty("apiVersion").GetString(), Is.EqualTo("v1"));
        using var anonymous = new HttpRequestMessage(HttpMethod.Get, "/api/v1/system");
        anonymous.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "invalid-fixture-token");
        using var denied = await Http.SendAsync(anonymous);
        Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized),
            "harness readiness: invalid credentials must not share the positive probe");
    }

    public async ValueTask DisposeAsync()
    {
        Http?.Dispose();
        if (Host is not null)
        {
            await Host.DisposeAsync();
        }
        if (Directory.Exists(Root))
        {
            DeleteDataDirectory(Root);
        }
    }

    internal static void DeleteDataDirectory(string root)
    {
        // Drain only this fixture's pooled connections; do not clear another running case's pool.
        foreach (var path in Directory.EnumerateFiles(root, "*.db", SearchOption.AllDirectories))
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared,
            }.ToString());
            SqliteConnection.ClearPool(connection);
        }
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
            }
        }
        // Windows can briefly retain a directory handle after the owned Host has stopped.
        // Retry only sharing/lock violations; permanent leaks still fail cleanup.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Directory.Delete(root, recursive: true);
                break;
            }
            catch (IOException exception) when (OperatingSystem.IsWindows() &&
                (exception.HResult & 0xffff) is 32 or 33 && attempt < 10)
            {
                TestContext.Out.WriteLine($"Owned fixture cleanup sharing violation, retry {attempt + 1}: {exception.Message}");
                Thread.Sleep(50);
            }
        }
    }
}
