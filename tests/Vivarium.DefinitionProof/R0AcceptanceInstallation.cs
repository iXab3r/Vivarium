using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Chiral;
using EyeAuras.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Vivarium.Controller.Hosting;
using Vivarium.Controller.Persistence;

namespace Vivarium.DefinitionProof;

/// <summary>One owned database template; each case starts the actual Host against an independent clone.</summary>
internal sealed class R0AcceptanceInstallation : IAsyncDisposable
{
    private readonly OwnedPostgres postgres = new();
    private readonly string template = "r0_template_" + Guid.NewGuid().ToString("N")[..12];
    private readonly string root = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.WorkDirectory,
        "artifacts/r0-host/acceptance-" + Guid.NewGuid().ToString("N")));
    private readonly Dictionary<string, string> certificates = [];
    private string certificateThumbprint = "";
    private Uri origin = null!;
    private int activeCases;
    private string Seed => Path.Combine(root, "source-seed");
    private string ActiveTemplate => template + "_active";
    private bool activeTemplatePrepared;

    internal async Task StartAsync()
    {
        Directory.CreateDirectory(root);
        var port = RuntimeHostPhysicalTests.Port(); origin = new($"https://127.0.0.1:{port}");
        foreach (var name in new[] { "https", "ServerSigningCertificate", "ServerEncryptionCertificate", "ClientSigningCertificate", "ClientEncryptionCertificate" })
        {
            var certificate = RuntimeHostPhysicalTests.Certificate(root, name, tls: name == "https");
            certificates.Add(name, certificate.Path);
            if (name == "https") certificateThumbprint = certificate.Thumbprint;
        }
        await postgres.StartAsync();
        await R0AcceptanceSeed.CreateAsync(Seed);
        await DatabaseCommandAsync($"CREATE DATABASE \"{template}\"");
        var connection = Connection(template);
        await using (var database = new VivariumDbContext(new DbContextOptionsBuilder<VivariumDbContext>().UseNpgsql(connection).Options))
            await PostgresSchema.MigrateAsync(database);
        // Complete the real framework's initial schema/account/client bootstrap before cloning.
        await using var prepared = await StartHostAsync(Path.Combine(root, "template"), template);
    }

    internal async Task<Case> CreateCaseAsync(bool catalogued = false, Action<IServiceCollection>? configureServices = null,
        int? outputRetentionDays = null)
    {
        if (Interlocked.Increment(ref activeCases) != 1)
        { Interlocked.Decrement(ref activeCases); throw new InvalidOperationException("Acceptance cases must not share a live Host or database."); }
        var name = "r0_case_" + Guid.NewGuid().ToString("N")[..12];
        try
        {
            if (catalogued && !activeTemplatePrepared) await PrepareActiveTemplateAsync();
            await DatabaseCommandAsync($"CREATE DATABASE \"{name}\" TEMPLATE \"{(catalogued ? ActiveTemplate : template)}\"");
            return await StartHostAsync(Path.Combine(root, name), name, isCase: true, configureServices, outputRetentionDays);
        }
        catch { Interlocked.Decrement(ref activeCases); throw; }
    }

    private async Task PrepareActiveTemplateAsync()
    {
        await DatabaseCommandAsync($"CREATE DATABASE \"{ActiveTemplate}\" TEMPLATE \"{template}\"");
        await using var host = await StartHostAsync(Path.Combine(root, "active-template"), ActiveTemplate);
        using var administrator = await host.AdministratorAsync();
        var csrf = await host.AntiforgeryAsync(administrator);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/configuration-changes/reconcile");
        request.Headers.Add("RequestVerificationToken", csrf);
        using var accepted = await administrator.SendAsync(request);
        Assert.That(accepted.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        while (true)
        {
            using var response = JsonDocument.Parse(await administrator.GetStringAsync(accepted.Headers.Location, bound.Token));
            var state = response.RootElement.GetProperty("state").GetString();
            if (state == "applied") break;
            Assert.That(state, Is.EqualTo("pending").Or.EqualTo("evaluated"));
            await Task.Delay(100, bound.Token);
        }
        activeTemplatePrepared = true;
    }

    private string Connection(string name, bool pooled = false) => new NpgsqlConnectionStringBuilder(postgres.ConnectionString)
        { Database = name, Pooling = pooled, MaxPoolSize = 16 }.ConnectionString;
    private async Task DatabaseCommandAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(Connection("template1"));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection); await command.ExecuteNonQueryAsync();
    }

    private async Task<Case> StartHostAsync(string directory, string databaseName, bool isCase = false,
        Action<IServiceCollection>? configureServices = null, int? outputRetentionDays = null)
    {
        Directory.CreateDirectory(directory);
        var definitions = Path.Combine(directory, "definitions");
        await SourceCompiler.GitAsync(directory, "-c", "core.autocrlf=false", "clone", "--local", "--no-hardlinks", Seed, definitions);
        await SourceCompiler.GitAsync(definitions, "config", "core.autocrlf", "false");
        await SourceCompiler.GitAsync(definitions, "config", "user.name", "Owned R0 acceptance");
        await SourceCompiler.GitAsync(definitions, "config", "user.email", "fixture@example.invalid");
        var nodes = Path.Combine(directory, "nodes");
        var storageOrigin = $"http://127.0.0.1:{RuntimeHostPhysicalTests.Port()}";
        var identity = new JsonObject
        {
            ["SuperuserEnabled"] = true, ["TokenLifetimeDays"] = 3,
            ["DataProtectionDirectory"] = Path.Combine(directory, "keys"), ["Providers"] = new JsonArray(),
            ["TokenCreationPermitLimit"] = 10, ["TokenCreationWindowSeconds"] = 60,
            ["TokenCreationConcurrencyLimit"] = 2, ["TokenCreationQueueLimit"] = 0, ["TokenRedemptionConcurrencyLimit"] = 2,
        };
        foreach (var name in certificates.Keys.Where(n => n != "https")) identity[name] = new JsonObject { ["Path"] = certificates[name] };
        var documents = new Dictionary<string, JsonObject>
        {
            ["vivarium-host"] = new() { ["PublicOrigin"] = origin.AbsoluteUri.TrimEnd('/'), ["ListenUrl"] = origin.AbsoluteUri.TrimEnd('/'),
                ["StorageListenUrl"] = storageOrigin, ["CertificatePath"] = certificates["https"],
                ["DefinitionSources"] = JsonSerializer.SerializeToNode(new { RepositoryId = R0AcceptanceSeed.RepositoryId, Path = definitions,
                    SettingsRepositories = Array.Empty<object>() }) },
            ["vivarium-database"] = new() { ["ConnectionString"] = Connection(databaseName, pooled: true) },
            ["vivarium-identity"] = identity,
            ["vivarium-storage"] = JsonSerializer.SerializeToNode(new { Backend = "Local", Root = Path.Combine(directory, "objects"),
                PublicBaseUrl = storageOrigin, SigningKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
                MaxObjectBytes = 1048576, DefaultLifetimeSeconds = 60, MaxLifetimeSeconds = 300 })!.AsObject(),
        };
        var builder = Host.CreateApplicationBuilder(); builder.Logging.ClearProviders();
        if (outputRetentionDays is not null) documents["vivarium-host"]["OutputRetentionDays"] = outputRetentionDays;
        builder.Services.AddVivariumNodes(builder.Configuration, nodes);
        using (var settingsHost = builder.Build())
        {
            await settingsHost.StartAsync();
            try
            {
                var settings = settingsHost.Services.GetRequiredService<NodeConfigurationService>();
                foreach (var (id, document) in documents)
                    Assert.That((await settings.SaveAsync(id, Guid.NewGuid(), true, document, NodeConfigActor.System, null)).StatusCode, Is.EqualTo(200), id);
            }
            finally { await settingsHost.StopAsync(); }
        }
        var log = new RuntimeHostPhysicalTests.PrivateStartupLog();
        var app = VivariumRuntime.Build(nodes, logging => logging.ClearProviders().AddProvider(log), configureServices);
        try
        {
            await app.StartAsync();
            return new Case(this, directory, databaseName, app, log, origin, certificates["https"], certificateThumbprint, isCase, configureServices);
        }
        catch { await app.DisposeAsync(); log.Dispose(); throw; }
    }

    public async ValueTask DisposeAsync()
    {
        if (activeCases != 0) throw new InvalidOperationException("An acceptance case is still live.");
        await postgres.DisposeAsync();
        var parent = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.WorkDirectory, "artifacts/r0-host")) + Path.DirectorySeparatorChar;
        if (!root.StartsWith(parent, StringComparison.OrdinalIgnoreCase)
            || !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(root), "^acceptance-[a-f0-9]{32}$"))
            throw new InvalidDataException("The owned acceptance root differs.");
        if (Directory.Exists(root))
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
            Directory.Delete(root, recursive: true);
        }
    }

    internal sealed class Case(R0AcceptanceInstallation owner, string root, string databaseName,
        Microsoft.AspNetCore.Builder.WebApplication app, RuntimeHostPhysicalTests.PrivateStartupLog log,
        Uri origin, string certificate, string thumbprint, bool isCase, Action<IServiceCollection>? configureServices) : IAsyncDisposable
    {
        private System.Diagnostics.Process? agent;
        private Task<string>? agentOutput;
        private Task<string>? agentError;
        internal string Root => root;
        internal Uri Origin => origin;
        internal string Certificate => certificate;
        internal string Thumbprint => thumbprint;
        internal IServiceProvider Services => app.Services;
        internal string Definitions => Path.Combine(root, "definitions");
        internal string? AgentId { get; private set; }
        internal int? TeamCityAgentId { get; private set; }
        internal JsonElement PendingRegistration { get; private set; }
        internal JsonElement AuthorizedRegistration { get; private set; }

        internal async Task RestartAsync(Func<Task>? afterStop = null)
        {
            await app.StopAsync(); await app.DisposeAsync(); log.Dispose();
            if (afterStop is not null) await afterStop();
            log = new RuntimeHostPhysicalTests.PrivateStartupLog();
            app = VivariumRuntime.Build(Path.Combine(root, "nodes"), logging => logging.ClearProviders().AddProvider(log), configureServices);
            await app.StartAsync();
        }

        internal async Task ExecuteDatabaseCommandAsync(string sql)
        {
            await using var connection = new NpgsqlConnection(owner.Connection(databaseName)); await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection); await command.ExecuteNonQueryAsync();
        }

        internal async Task ConfigureSettingsRepositoryAsync(string id, string path, string trustReference)
        {
            var settings = app.Services.GetRequiredService<NodeConfigurationService>();
            var current = (await settings.GetAsync("vivarium-host")).Value!;
            var document = current.Config.DeepClone().AsObject();
            document["DefinitionSources"]!["SettingsRepositories"] = JsonSerializer.SerializeToNode(new[] { new { Id = id, Path = path, TrustReference = trustReference } });
            var saved = await settings.SaveAsync("vivarium-host", Guid.NewGuid(), true, document, NodeConfigActor.System, current.SavedRevision!.Id);
            Assert.That(saved.StatusCode, Is.EqualTo(200)); await RestartAsync();
        }

        internal async Task StopOwnedAgentAsync()
        {
            if (agent is null) return;
            if (!agent.HasExited) { agent.Kill(entireProcessTree: true); await agent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
            await Task.WhenAll(agentOutput!, agentError!).WaitAsync(TimeSpan.FromSeconds(10));
            agent.Dispose(); agent = null;
        }

        internal async Task RestartOwnedAgentAsync()
        {
            await StopOwnedAgentAsync(); StartAgentProcess();
        }

        private void StartAgentProcess()
        {
            if (agent is not null) throw new InvalidOperationException("The case already has an Agent process.");
            agent = RuntimeHostPhysicalTests.StartAgent(Path.Combine(root, "agent-bootstrap.json"), Path.Combine(root, "agent-data"));
            agentOutput = agent.StandardOutput.ReadToEndAsync(); agentError = agent.StandardError.ReadToEndAsync();
        }

        internal async Task StartWindowsAgentAsync(HttpClient administrator, string csrf)
        {
            if (agent is not null) throw new InvalidOperationException("The case already has an Agent.");
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/enrollment-tokens");
            request.Headers.Add("RequestVerificationToken", csrf);
            using var response = await administrator.SendAsync(request);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created));
            using var enrollment = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            using var publicCertificate = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12FromFile(certificate, null);
            var bootstrap = Path.Combine(root, "agent-bootstrap.json");
            await File.WriteAllTextAsync(bootstrap, JsonSerializer.Serialize(new { controllerUrl = origin.AbsoluteUri.TrimEnd('/'),
                certFingerprint = publicCertificate.GetCertHashString(HashAlgorithmName.SHA256), enrollToken = enrollment.RootElement.GetProperty("token").GetString() }));
            StartAgentProcess();
            using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (AgentId is null)
            {
                using var agents = JsonDocument.Parse(await administrator.GetStringAsync("/api/v1/agents", bound.Token));
                var current = agents.RootElement.GetProperty("items").EnumerateArray().SingleOrDefault();
                if (current.ValueKind != JsonValueKind.Undefined && current.GetProperty("connected").GetBoolean())
                {
                    AgentId = current.GetProperty("id").GetString(); TeamCityAgentId = current.GetProperty("teamCityId").GetInt32();
                    PendingRegistration = current.Clone();
                }
                else await Task.Delay(100, bound.Token);
            }
            await MutateAsync(administrator, csrf, HttpMethod.Put, "/api/v1/agents/" + AgentId + "/authorization", "{\"authorized\":true}", HttpStatusCode.NoContent);
            while (true)
            {
                using var proven = JsonDocument.Parse(await administrator.GetStringAsync("/api/v1/agents/" + AgentId, bound.Token));
                var state = proven.RootElement;
                if (state.GetProperty("authorized").GetBoolean() && state.GetProperty("connected").GetBoolean() && state.GetProperty("reconciled").GetBoolean())
                { AuthorizedRegistration = state.Clone(); break; }
                await Task.Delay(50, bound.Token);
            }
            administrator.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            await MutateAsync(administrator, csrf, HttpMethod.Put, $"/app/rest/agents/id:{TeamCityAgentId}/enabledInfo",
                "{\"status\":true,\"comment\":{\"text\":\"Owned R0 reference fixture\"}}", HttpStatusCode.OK);
            while (true)
            {
                using var state = JsonDocument.Parse(await administrator.GetStringAsync("/api/v1/agents/" + AgentId, bound.Token));
                if (state.RootElement.GetProperty("ready").GetBoolean()) break;
                await Task.Delay(100, bound.Token);
            }
        }

        internal static async Task MutateAsync(HttpClient client, string csrf, HttpMethod method, string path, string? body, HttpStatusCode expected)
        {
            using var request = new HttpRequestMessage(method, path);
            request.Headers.Add("RequestVerificationToken", csrf);
            if (body is not null) request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(request);
            Assert.That(response.StatusCode, Is.EqualTo(expected), path);
        }

        internal async Task<string> AntiforgeryAsync(HttpClient client)
        {
            var html = await client.GetStringAsync("/access");
            var match = System.Text.RegularExpressions.Regex.Match(html,
                "<script id=\"vivarium-session\" type=\"application/json\">(.*?)</script>", System.Text.RegularExpressions.RegexOptions.Singleline);
            using var session = JsonDocument.Parse(match.Groups[1].Value);
            return session.RootElement.GetProperty("requestVerificationToken").GetString()!;
        }

        internal HttpClient Client() => new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true,
            ServerCertificateCustomValidationCallback = (_, remote, _, _) => remote?.Thumbprint == thumbprint })
            { BaseAddress = origin, Timeout = TimeSpan.FromSeconds(15) };

        internal HttpClient InstallationPasswordAsBearerClient()
        {
            var client = Client(); client.DefaultRequestHeaders.Authorization = new("Bearer", log.Password!); return client;
        }

        internal async Task<HttpClient> AdministratorAsync()
        {
            var client = Client();
            using var login = await RuntimeHostPhysicalTests.LoginAsync(client, log.Password ?? throw new InvalidDataException("Private startup disclosure is absent."));
            Assert.That(login.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
            return client;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await StopOwnedAgentAsync();
                await app.StopAsync();
            }
            finally
            {
                await app.DisposeAsync(); log.Dispose();
                // Close only this owned database's idle sessions before cloning/dropping it.
                // Runtime uses an ordinary bounded pool, not one new OS socket per PG query.
                using (var pool = new NpgsqlConnection(owner.Connection(databaseName, pooled: true))) NpgsqlConnection.ClearPool(pool);
                if (isCase)
                {
                    try { await owner.DatabaseCommandAsync($"DROP DATABASE \"{databaseName}\" WITH (FORCE)"); }
                    finally { Interlocked.Decrement(ref owner.activeCases); }
                }
            }
        }
    }
}
