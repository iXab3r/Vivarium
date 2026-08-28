using System.Net;
using System.Security.Cryptography;
using Google.Protobuf;
using Vivarium.Contracts.V1;
using Vivarium.Controller;

namespace Vivarium.Tests;

[TestFixture]
public class PanelTests
{
    private string rootDir = null!;

    [SetUp]
    public void SetUp()
    {
        rootDir = Path.Combine(Path.GetTempPath(), "vivarium-tests", Guid.NewGuid().ToString("N"));
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
            // best effort
        }
    }

    [Test]
    public async Task Panel_pages_require_admin_login()
    {
        await using var controller = await VivariumControllerHost.StartAsync(new ControllerOptions
        {
            DataDir = Path.Combine(rootDir, "controller"),
            Host = "127.0.0.1",
            Port = 0,
        });
        using var http = PinnedClient(controller);

        var anonymous = await http.GetAsync("/agents");
        Assert.Multiple(() =>
        {
            Assert.That(anonymous.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
            Assert.That(anonymous.Headers.Location?.AbsolutePath, Is.EqualTo("/login"));
        });

        var anonymousBuilds = await http.GetAsync("/builds");
        Assert.Multiple(() =>
        {
            Assert.That(anonymousBuilds.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
            Assert.That(anonymousBuilds.Headers.Location?.AbsolutePath, Is.EqualTo("/login"));
        });

        using var login = new HttpRequestMessage(HttpMethod.Post, "/login")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["token"] = controller.Tokens.AdminToken,
            }),
        };
        var signedIn = await http.SendAsync(login);
        Assert.That(signedIn.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));

        var page = await http.GetAsync("/agents");
        var html = await page.Content.ReadAsStringAsync();
        Assert.Multiple(() =>
        {
            Assert.That(page.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(html, Does.Contain("<h1>Agents</h1>"));
            Assert.That(html, Does.Contain("Create enrollment token"));
        });

        var buildsPage = await http.GetAsync("/builds");
        var buildsHtml = await buildsPage.Content.ReadAsStringAsync();
        Assert.Multiple(() =>
        {
            Assert.That(buildsPage.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(buildsHtml, Does.Contain("Queue &amp; Builds</h1>"));
            Assert.That(buildsHtml, Does.Contain("The queue is empty."));
        });
    }

    [Test]
    public async Task Panel_cookie_is_scoped_to_one_controller_data_directory()
    {
        await using var first = await VivariumControllerHost.StartAsync(new ControllerOptions
        {
            DataDir = Path.Combine(rootDir, "first-controller"),
            Host = "127.0.0.1",
            Port = 0,
        });
        await using var second = await VivariumControllerHost.StartAsync(new ControllerOptions
        {
            DataDir = Path.Combine(rootDir, "second-controller"),
            Host = "127.0.0.1",
            Port = 0,
        });
        var cookies = new CookieContainer();
        using var firstHttp = PinnedClient(first, cookies);
        using var secondHttp = PinnedClient(second, cookies);

        await LoginAsync(firstHttp, first.Tokens.AdminToken);
        Assert.That((await firstHttp.GetAsync("/agents")).StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var replayed = await secondHttp.GetAsync("/agents");
        Assert.Multiple(() =>
        {
            Assert.That(replayed.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
            Assert.That(replayed.Headers.Location?.AbsolutePath, Is.EqualTo("/login"));
        });
    }

    [Test]
    public async Task Agents_page_separates_reported_and_operator_owned_parameters()
    {
        await using var controller = await VivariumControllerHost.StartAsync(new ControllerOptions
        {
            DataDir = Path.Combine(rootDir, "controller"),
            Host = "127.0.0.1",
            Port = 0,
        });
        await RegisterKnownAgentAsync(controller);
        await controller.AgentAdministration.SetCustomParameterAsync(
            "known-windows", "pool", "hardware-lab");

        using var http = PinnedClient(controller);
        var anonymous = await http.GetAsync("/agents?agent=known-windows");
        Assert.That(anonymous.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));

        await LoginAsync(http, controller.Tokens.AdminToken);
        var html = await http.GetStringAsync("/agents?agent=known-windows");
        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("Reported parameters"));
            Assert.That(html, Does.Contain("os.family"));
            Assert.That(html, Does.Contain("Custom parameters"));
            Assert.That(html, Does.Contain("Operator-owned values used for compatibility matching."));
            Assert.That(html, Does.Contain("pool"));
            Assert.That(html, Does.Contain("hardware-lab"));
            Assert.That(html, Does.Contain("Add or update"));
        });
    }

    [Test]
    public async Task Build_details_and_artifact_download_are_protected_owned_and_durable()
    {
        await using var controller = await VivariumControllerHost.StartAsync(new ControllerOptions
        {
            DataDir = Path.Combine(rootDir, "controller"),
            Host = "127.0.0.1",
            Port = 0,
        });
        await RegisterKnownAgentAsync(controller);
        await controller.AgentAdministration.SetCustomParameterAsync(
            "known-windows", "pool", "hardware-lab");
        var payloadHash = await PutBlobAsync(controller, "payload"u8.ToArray());
        var artifactBytes = "exact artifact bytes"u8.ToArray();
        var artifactHash = await PutBlobAsync(controller, artifactBytes);
        var older = await controller.MatrixBuildSubmissions.SubmitAsync(
            Request("panel-older", "older-configuration", payloadHash));
        var newer = await controller.MatrixBuildSubmissions.SubmitAsync(
            Request("panel-newer", "newer-configuration", payloadHash));
        var newerSnapshot = (await controller.MatrixBuildStore.GetSnapshotAsync(newer.BuildId))!;
        var cellBuildId = newerSnapshot.Cells.Single().BuildId;
        await FinishBuildAsync(controller, cellBuildId, artifactHash, artifactBytes.Length);
        await controller.Database.WriteAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE matrix_builds SET created_unix_ms = CASE matrix_build_id
                    WHEN $older THEN 100
                    WHEN $newer THEN 200
                    END
                WHERE matrix_build_id IN ($older, $newer);
                """;
            command.Parameters.AddWithValue("$older", older.BuildId);
            command.Parameters.AddWithValue("$newer", newer.BuildId);
            return command.ExecuteNonQuery();
        });

        using var http = PinnedClient(controller);
        var artifactUrl = $"/builds/{newer.BuildId}/cells/{cellBuildId}/artifacts/0";
        var anonymousDetail = await http.GetAsync($"/builds/{newer.BuildId}");
        var anonymousArtifact = await http.GetAsync(artifactUrl);
        Assert.Multiple(() =>
        {
            Assert.That(anonymousDetail.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
            Assert.That(anonymousArtifact.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
        });

        await LoginAsync(http, controller.Tokens.AdminToken);

        var cancellableDetail = await http.GetStringAsync($"/builds/{older.BuildId}");
        Assert.That(cancellableDetail, Does.Contain("Stop matrix build"));

        var detail = await http.GetAsync($"/builds/{newer.BuildId}");
        var detailHtml = await detail.Content.ReadAsStringAsync();
        Assert.Multiple(() =>
        {
            Assert.That(detail.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(detailHtml, Does.Contain("newer-configuration"));
            Assert.That(detailHtml, Does.Contain("windows-cell"));
            Assert.That(detailHtml, Does.Contain("win-x64"));
            Assert.That(detailHtml, Does.Contain("known-windows"));
            Assert.That(detailHtml, Does.Contain("Reported agent parameters at assignment"));
            Assert.That(detailHtml, Does.Contain("os.family"));
            Assert.That(detailHtml, Does.Contain("Custom agent parameters at assignment"));
            Assert.That(detailHtml, Does.Contain("hardware-lab"));
            Assert.That(detailHtml, Does.Contain("one assertion failed"));
            Assert.That(detailHtml, Does.Contain("results/report.bin"));
            Assert.That(detailHtml, Does.Contain($"{artifactBytes.Length} B"));
        });

        var artifact = await http.GetAsync(artifactUrl);
        var downloadedBytes = await artifact.Content.ReadAsByteArrayAsync();
        Assert.Multiple(() =>
        {
            Assert.That(artifact.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(artifact.Content.Headers.ContentDisposition?.FileNameStar ??
                artifact.Content.Headers.ContentDisposition?.FileName,
                Does.Contain("report.bin"));
            Assert.That(artifact.Content.Headers.ContentDisposition?.ToString(),
                Does.Not.Contain("results/"));
            Assert.That(downloadedBytes, Is.EqualTo(artifactBytes));
        });

        Assert.That(
            (await http.GetAsync($"/builds/{older.BuildId}/cells/{cellBuildId}/artifacts/0")).StatusCode,
            Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That(
            (await http.GetAsync($"/builds/{newer.BuildId}/cells/{cellBuildId}/artifacts/1")).StatusCode,
            Is.EqualTo(HttpStatusCode.NotFound));

        var notFound = await http.GetStringAsync("/builds/unknown-matrix");
        Assert.That(notFound, Does.Contain("Not Found"));

        var buildsHtml = await http.GetStringAsync("/builds");
        Assert.Multiple(() =>
        {
            Assert.That(buildsHtml, Does.Contain($"/builds/{newer.BuildId}"));
            Assert.That(buildsHtml.IndexOf("newer-configuration", StringComparison.Ordinal),
                Is.LessThan(buildsHtml.IndexOf("older-configuration", StringComparison.Ordinal)));
        });
    }

    private static SubmitBuildRequest Request(string requestId, string configuration, string payloadHash)
    {
        var assignment = new BuildAssignment();
        assignment.Payload.Add(new Blob { Sha256 = payloadHash, FileName = "payload.zip" });
        var request = new SubmitBuildRequest
        {
            RequestId = requestId,
            Project = "Vivarium",
            Configuration = configuration,
            DefinitionSnapshot = ByteString.CopyFromUtf8("project: Vivarium"),
        };
        request.Cells.Add(new MatrixBuildCell
        {
            Name = "windows-cell",
            AgentExpression = "os.family == windows",
            Rid = "win-x64",
            Assignment = assignment,
        });
        return request;
    }

    private static async Task RegisterKnownAgentAsync(VivariumControllerHost controller)
    {
        var enrollToken = await controller.Tokens.CreateEnrollTokenAsync();
        var hello = new Hello
        {
            AgentId = "known-windows",
            EnrollToken = enrollToken,
            SessionId = "known-session",
            Os = new OsInfo { Family = "windows", Arch = "x64", Version = "test" },
        };
        hello.Parameters["hostname"] = "known-windows";
        hello.Parameters["os.family"] = "windows";
        Assert.That(await controller.Tokens.AdmitAgentAsync(hello), Is.Not.Null);
        await controller.AgentStore.ObserveHelloAsync(hello);
    }

    private static async Task FinishBuildAsync(
        VivariumControllerHost controller,
        string buildId,
        string artifactHash,
        int artifactSize)
    {
        var now = DateTimeOffset.UtcNow;
        const string agentId = "known-windows";
        const string sessionId = "result-session";
        Assert.That(await controller.BuildQueueStore.TryClaimAsync(buildId, agentId, now), Is.True);
        Assert.That(
            await controller.BuildQueueStore.TryPrepareDispatchAsync(
                buildId, agentId, sessionId, now),
            Is.True);
        Assert.That(
            await controller.BuildQueueStore.CompleteDispatchAsync(buildId, agentId, sessionId),
            Is.True);
        var result = new BuildResult
        {
            BuildId = buildId,
            SessionId = sessionId,
            Outcome = BuildOutcome.Failed,
            StatusText = "one assertion failed",
        };
        result.Steps.Add(new StepResult { StepIndex = 0, ExitCode = 1 });
        result.Artifacts.Add(new Artifact
        {
            Path = "results/report.bin",
            Sha256 = artifactHash,
            Size = artifactSize,
        });
        Assert.That(
            await controller.BuildStore.TryFinishAsync(result, agentId, sessionId, now),
            Is.True);
    }

    private static async Task<string> PutBlobAsync(VivariumControllerHost controller, byte[] content)
    {
        var hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        await using var stream = new MemoryStream(content);
        Assert.That(await controller.Blobs.PutAsync(hash, stream, CancellationToken.None), Is.True);
        return hash;
    }

    private static async Task LoginAsync(HttpClient http, string adminToken)
    {
        using var login = new HttpRequestMessage(HttpMethod.Post, "/login")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["token"] = adminToken,
            }),
        };
        var response = await http.SendAsync(login);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
    }

    private static HttpClient PinnedClient(
        VivariumControllerHost controller,
        CookieContainer? cookies = null)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            CookieContainer = cookies ?? new CookieContainer(),
            UseCookies = true,
            ServerCertificateCustomValidationCallback = (_, cert, _, _) =>
                cert != null &&
                Convert.ToHexString(SHA256.HashData(cert.RawData))
                    .Equals(controller.Certificate.FingerprintSha256, StringComparison.OrdinalIgnoreCase),
        };
        return new HttpClient(handler) { BaseAddress = new Uri(controller.Url) };
    }
}
