using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Chiral;
using EyeAuras.Storage;
using EyeAuras.Storage.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Vivarium.Controller.Hosting;
using Vivarium.Controller.Persistence;

namespace Vivarium.DefinitionProof;

[NonParallelizable]
public sealed class RuntimeHostPhysicalTests
{
    private string? ownedRoot;

    [TearDown]
    public void RemoveOwnedPrivateFiles()
    {
        if (ownedRoot is null) return;
        var parent = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.WorkDirectory, "artifacts/r0-host"))
            + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(ownedRoot);
        if (!target.StartsWith(parent, StringComparison.OrdinalIgnoreCase)
            || !Regex.IsMatch(Path.GetFileName(target), "^runtime-[a-f0-9]{32}$"))
            throw new InvalidDataException("Owned private fixture cleanup target differs.");
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        ownedRoot = null;
    }

    [Test]
    public async Task Real_Host_restores_nodes_and_framework_login_and_keeps_object_HTTP_private_across_restart()
    {
        await using var postgres = new OwnedPostgres();
        await postgres.StartAsync();
        var root = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.WorkDirectory,
            "artifacts/r0-host/runtime-" + Guid.NewGuid().ToString("N")));
        ownedRoot = root;
        Directory.CreateDirectory(root);
        var directory = Path.Combine(root, "nodes");
        var publicPort = Port();
        var storagePort = Port();
        Assert.That(publicPort, Is.Not.EqualTo(storagePort));
        var origin = $"https://127.0.0.1:{publicPort}";
        var storageOrigin = $"http://127.0.0.1:{storagePort}";
        var certificate = Certificate(root, "https", tls: true);
        var identity = new JsonObject
        {
            ["SuperuserEnabled"] = true, ["TokenLifetimeDays"] = 1,
            ["DataProtectionDirectory"] = Path.Combine(root, "keys"),
            ["Providers"] = new JsonArray(), ["TokenCreationPermitLimit"] = 10,
            ["TokenCreationWindowSeconds"] = 60, ["TokenCreationConcurrencyLimit"] = 2,
            ["TokenCreationQueueLimit"] = 0, ["TokenRedemptionConcurrencyLimit"] = 2,
        };
        foreach (var name in new[] { "ServerSigningCertificate", "ServerEncryptionCertificate",
            "ClientSigningCertificate", "ClientEncryptionCertificate" })
            identity[name] = new JsonObject { ["Path"] = Certificate(root, name, tls: false).Path };
        var storage = new LocalStorageOptions
        {
            Root = Path.Combine(root, "objects"), PublicBaseUrl = storageOrigin,
            SigningKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            MaxObjectBytes = 1048576, DefaultLifetimeSeconds = 60, MaxLifetimeSeconds = 300,
        };
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddVivariumNodes(builder.Configuration, directory);
        using (var setup = builder.Build())
        {
            await setup.StartAsync();
            try
            {
                var settings = setup.Services.GetRequiredService<NodeConfigurationService>();
                var documents = new Dictionary<string, JsonObject>
                {
                    ["vivarium-host"] = new()
                    {
                        ["PublicOrigin"] = origin, ["ListenUrl"] = origin,
                        ["StorageListenUrl"] = storageOrigin, ["CertificatePath"] = certificate.Path,
                    },
                    ["vivarium-database"] = new() { ["ConnectionString"] = postgres.ConnectionString },
                    ["vivarium-identity"] = identity,
                    ["vivarium-storage"] = JsonSerializer.SerializeToNode(new
                    {
                        Backend = "Local", storage.Root, storage.PublicBaseUrl, storage.SigningKey,
                        storage.MaxObjectBytes, storage.DefaultLifetimeSeconds, storage.MaxLifetimeSeconds,
                    })!.AsObject(),
                };
                foreach (var (id, config) in documents)
                {
                    var saved = await settings.SaveAsync(id, Guid.NewGuid(), true, config, NodeConfigActor.System, null);
                    Assert.That(saved.StatusCode, Is.EqualTo(200), id + " initialization failed.");
                }
            }
            finally { await setup.StopAsync(); }
        }
        await using (var database = new VivariumDbContext(new DbContextOptionsBuilder<VivariumDbContext>()
            .UseNpgsql(postgres.ConnectionString).Options))
            await PostgresSchema.MigrateAsync(database);

        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false, CookieContainer = new CookieContainer(),
            ServerCertificateCustomValidationCallback = (_, remote, _, _) => remote is not null
                && remote.Thumbprint == certificate.Thumbprint,
        };
        using var http = new HttpClient(handler) { BaseAddress = new Uri(origin), Timeout = TimeSpan.FromSeconds(15) };
        string oldPassword;
        var capture = new PrivateStartupLog();
        await using (var app = VivariumRuntime.Build(directory, logs => logs.ClearProviders().AddProvider(capture)))
        {
            // This owned logger receives only the product's intended private disclosure. It never
            // writes the password, form, token or cookie into console/TRX/diagnostic artifacts.
            await app.StartAsync();
            try
            {
                Assert.That((await http.GetAsync("/health/ready")).StatusCode, Is.EqualTo(HttpStatusCode.OK));
                foreach (var path in new[] { "/objects", "/objects/", "/OBJECTS" })
                    Assert.That((await http.GetAsync(path)).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
                Assert.That((await http.GetAsync(storageOrigin + "/health/live")).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
                oldPassword = capture.Password ?? throw new InvalidOperationException("Private startup disclosure absent.");
                var login = await LoginAsync(http, oldPassword);
                Assert.That(login.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
                Assert.That((await http.GetAsync("/Identity/Account/Manage/Tokens")).StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
                Assert.That((await http.GetAsync("/Identity/Account/Login?ReturnUrl=https://example.invalid/")).StatusCode,
                    Is.EqualTo(HttpStatusCode.BadRequest));
                var unsigned = await http.PostAsync("/Identity/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
                    { ["login"] = "superuser", ["token"] = oldPassword, ["returnUrl"] = "/" }));
                Assert.That(unsigned.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
                var objects = app.Services.GetRequiredService<IObjectStorage>();
                var upload = await objects.UploadAsync(new GetUploadUrl("owned-r0/object", "application/octet-stream"));
                using var bytes = new ByteArrayContent("owned-r0-object\n"u8.ToArray());
                bytes.Headers.ContentType = new("application/octet-stream");
                using var uploaded = await http.PutAsync(upload.Url, bytes);
                Assert.That(uploaded.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
            }
            finally { await app.StopAsync(); }
        }
        var restartedCapture = new PrivateStartupLog();
        await using (var app = VivariumRuntime.Build(directory, logs => logs.ClearProviders().AddProvider(restartedCapture)))
        {
            await app.StartAsync();
            try
            {
                Assert.That(restartedCapture.Password is not null && restartedCapture.Password != oldPassword, Is.True);
                Assert.That((await LoginAsync(http, oldPassword)).StatusCode, Is.EqualTo(HttpStatusCode.OK), "Old password must fail normal login.");
                Assert.That((await LoginAsync(http, restartedCapture.Password!)).StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
                var objects = app.Services.GetRequiredService<IObjectStorage>();
                var download = await objects.DownloadAsync(new GetDownloadUrl("owned-r0/object", "owned.bin"));
                using var retained = await http.GetAsync(download.Url);
                Assert.That(retained.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(await retained.Content.ReadAsByteArrayAsync(), Is.EqualTo("owned-r0-object\n"u8.ToArray()));
                await objects.DeleteAsync("owned-r0/object", CancellationToken.None);
                Assert.That((await http.GetAsync(download.Url)).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            }
            finally { await app.StopAsync(); }
        }
    }

    private static async Task<HttpResponseMessage> LoginAsync(HttpClient http, string password)
    {
        using var page = await http.GetAsync("/Identity/Account/Login");
        Assert.That(page.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var html = await page.Content.ReadAsStringAsync();
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        if (!match.Success) throw new InvalidDataException("Normal login antiforgery input missing.");
        return await http.PostAsync("/Identity/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["login"] = "superuser", ["token"] = password, ["returnUrl"] = "/",
            ["__RequestVerificationToken"] = WebUtility.HtmlDecode(match.Groups[1].Value),
        }));
    }

    private static int Port()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static (string Path, string Thumbprint) Certificate(string root, string name, bool tls)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=owned-r0-" + name, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        if (tls)
        {
            var names = new SubjectAlternativeNameBuilder();
            names.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(names.Build());
        }
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(2));
        var path = Path.Combine(root, name + ".pfx");
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx));
        return (path, certificate.Thumbprint);
    }

    private sealed class PrivateStartupLog : ILoggerProvider, ILogger
    {
        public string? Password { get; private set; }
        public ILogger CreateLogger(string categoryName) => categoryName == "Vivarium.PrivateStartup" ? this : NullLogger.Instance;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (state is IEnumerable<KeyValuePair<string, object?>> fields)
                Password = fields.FirstOrDefault(field => field.Key == "SuperuserPassword").Value as string;
        }
        public void Dispose() => Password = null;
    }
}
