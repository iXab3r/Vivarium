using System.Net;
using System.Security.Cryptography;
using Google.Protobuf;
using Vivarium.Contracts.V1;
using Vivarium.Controller;
using Vivarium.Controller.Auditing;
using Vivarium.Controller.Security;

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
            ManagementRequestContext.System("test"), "known-windows", "pool", "hardware-lab");

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
        var knownAgent = await RegisterKnownAgentAsync(controller);
        await controller.AgentAdministration.SetCustomParameterAsync(
            ManagementRequestContext.System("test"), "known-windows", "pool", "hardware-lab");
        var payloadHash = await PutBlobAsync(controller, "payload"u8.ToArray());
        var artifactBytes = "exact artifact bytes"u8.ToArray();
        var artifactHash = Convert.ToHexStringLower(SHA256.HashData(artifactBytes));
        var older = await controller.MatrixBuildSubmissions.SubmitAsync(
            ManagementRequestContext.System("test"),
            Request("panel-older", "older-configuration", payloadHash));
        var newer = await controller.MatrixBuildSubmissions.SubmitAsync(
            ManagementRequestContext.System("test"),
            Request("panel-newer", "newer-configuration", payloadHash));
        var newerSnapshot = (await controller.MatrixBuildStore.GetSnapshotAsync(newer.BuildId))!;
        var cellBuildId = newerSnapshot.Cells.Single().BuildId;
        await FinishBuildAsync(
            controller,
            knownAgent,
            cellBuildId,
            artifactHash,
            artifactBytes);
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
        using var anonymousArtifactRequest = new HttpRequestMessage(HttpMethod.Get, artifactUrl);
        anonymousArtifactRequest.Headers.Add(
            ManagementRequestContextFactory.CorrelationHeader, "artifact-read-anonymous");
        var anonymousArtifact = await http.SendAsync(anonymousArtifactRequest);
        var anonymousArtifactAudit = (await controller.Audits.ListAsync())
            .Single(audit => audit.CorrelationId == "artifact-read-anonymous");
        Assert.Multiple(() =>
        {
            Assert.That(anonymousDetail.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
            Assert.That(anonymousArtifact.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
            Assert.That(
                anonymousArtifact.Headers.GetValues(ManagementRequestContextFactory.CorrelationHeader).Single(),
                Is.EqualTo("artifact-read-anonymous"));
            Assert.That(anonymousArtifactAudit.Action, Is.EqualTo("artifact.read"));
            Assert.That(anonymousArtifactAudit.Outcome, Is.EqualTo(AuditOutcome.Denied));
            Assert.That(anonymousArtifactAudit.ReasonCode, Is.EqualTo("authentication_required"));
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

        using var artifactRequest = new HttpRequestMessage(HttpMethod.Get, artifactUrl);
        artifactRequest.Headers.Add(
            ManagementRequestContextFactory.CorrelationHeader, "artifact-read-success");
        var artifact = await http.SendAsync(artifactRequest);
        var downloadedBytes = await artifact.Content.ReadAsByteArrayAsync();
        var artifactAudit = (await controller.Audits.ListAsync())
            .Single(audit => audit.CorrelationId == "artifact-read-success");
        Assert.Multiple(() =>
        {
            Assert.That(artifact.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(
                artifact.Headers.GetValues(ManagementRequestContextFactory.CorrelationHeader).Single(),
                Is.EqualTo("artifact-read-success"));
            Assert.That(artifact.Content.Headers.ContentDisposition?.FileNameStar ??
                artifact.Content.Headers.ContentDisposition?.FileName,
                Does.Contain("report.bin"));
            Assert.That(artifact.Content.Headers.ContentDisposition?.ToString(),
                Does.Not.Contain("results/"));
            Assert.That(downloadedBytes, Is.EqualTo(artifactBytes));
            Assert.That(artifactAudit.Action, Is.EqualTo("blob-artifact.read"));
            Assert.That(artifactAudit.TargetId,
                Is.EqualTo($"{cellBuildId}:0"));
            Assert.That(artifactAudit.Outcome, Is.EqualTo(AuditOutcome.Succeeded));
        });

        using var wrongOwnerRequest = new HttpRequestMessage(
            HttpMethod.Get, $"/builds/{older.BuildId}/cells/{cellBuildId}/artifacts/0");
        wrongOwnerRequest.Headers.Add(
            ManagementRequestContextFactory.CorrelationHeader, "artifact-read-wrong-owner");
        var wrongOwner = await http.SendAsync(wrongOwnerRequest);
        using var missingOrdinalRequest = new HttpRequestMessage(
            HttpMethod.Get, $"/builds/{newer.BuildId}/cells/{cellBuildId}/artifacts/1");
        missingOrdinalRequest.Headers.Add(
            ManagementRequestContextFactory.CorrelationHeader, "artifact-read-missing-ordinal");
        var missingOrdinal = await http.SendAsync(missingOrdinalRequest);

        const string invalidMatrixId = "password-secret-matrix";
        using var invalidTargetRequest = new HttpRequestMessage(
            HttpMethod.Get, $"/builds/{invalidMatrixId}/cells/{cellBuildId}/artifacts/0");
        invalidTargetRequest.Headers.Add(
            ManagementRequestContextFactory.CorrelationHeader, "artifact-read-invalid-target");
        var invalidTarget = await http.SendAsync(invalidTargetRequest);
        var noChangeAudits = (await controller.Audits.ListAsync())
            .Where(audit => audit.CorrelationId is
                "artifact-read-wrong-owner" or
                "artifact-read-missing-ordinal" or
                "artifact-read-invalid-target")
            .ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(wrongOwner.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(missingOrdinal.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(invalidTarget.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(noChangeAudits, Has.Length.EqualTo(3));
            Assert.That(noChangeAudits.All(audit =>
                audit.Outcome == AuditOutcome.NoChange &&
                audit.ReasonCode == "not_found" &&
                audit.Source == "artifact-download"), Is.True);
            Assert.That(noChangeAudits.Single(audit =>
                audit.CorrelationId == "artifact-read-invalid-target").TargetId,
                Does.StartWith("invalid-artifact:"));
            Assert.That(string.Join('|', noChangeAudits.Select(audit => audit.TargetId)),
                Does.Not.Contain(invalidMatrixId));
        });

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

    private static async Task<KnownAgentSession> RegisterKnownAgentAsync(
        VivariumControllerHost controller)
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
        var token = await controller.Tokens.AuthorizeAgentAsync(hello.AgentId)
            ?? throw new AssertionException("Agent authorization did not issue a token");
        var generations = await controller.AgentStore.GetGenerationStateAsync(hello.AgentId)
            ?? throw new AssertionException("persisted Agent generations were missing");
        var accepted = await controller.AgentStore.AcceptSessionAsync(
            hello.AgentId,
            generations.CredentialGeneration);
        return new KnownAgentSession(
            hello.AgentId,
            hello.SessionId,
            accepted.ConnectionGeneration,
            token);
    }

    private static async Task FinishBuildAsync(
        VivariumControllerHost controller,
        KnownAgentSession agent,
        string buildId,
        string artifactHash,
        byte[] artifactBytes)
    {
        var now = DateTimeOffset.UtcNow;
        Assert.That(
            await controller.BuildQueueStore.TryClaimAsync(buildId, agent.AgentId, now),
            Is.True);
        Assert.That(
            await controller.BuildQueueStore.TryPrepareDispatchAsync(
                buildId, agent.AgentId, agent.SessionId, now),
            Is.True);
        Assert.That(
            await controller.BuildQueueStore.CompleteDispatchAsync(
                buildId,
                agent.AgentId,
                agent.SessionId),
            Is.True);
        using (var http = PinnedClient(controller))
        using (var upload = new HttpRequestMessage(HttpMethod.Put, $"/blobs/{artifactHash}")
        {
            Content = new ByteArrayContent(artifactBytes),
        })
        {
            upload.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer",
                agent.Token);
            upload.Headers.Add("X-Vivarium-Build-Id", buildId);
            upload.Headers.Add("X-Vivarium-Session-Id", agent.SessionId);
            upload.Headers.Add(
                "X-Vivarium-Blob-Declared-Size",
                artifactBytes.LongLength.ToString(System.Globalization.CultureInfo.InvariantCulture));
            using var uploaded = await http.SendAsync(upload);
            Assert.That(
                uploaded.StatusCode,
                Is.EqualTo(HttpStatusCode.NoContent),
                await uploaded.Content.ReadAsStringAsync());
        }

        var result = new BuildResult
        {
            BuildId = buildId,
            SessionId = agent.SessionId,
            Outcome = BuildOutcome.Failed,
            StatusText = "one assertion failed",
        };
        result.Steps.Add(new StepResult { StepIndex = 0, ExitCode = 1 });
        result.Artifacts.Add(new Artifact
        {
            Path = "results/report.bin",
            Sha256 = artifactHash,
            Size = artifactBytes.LongLength,
        });
        Assert.That(
            await controller.BuildStore.TryFinishAsync(
                result,
                agent.AgentId,
                agent.SessionId,
                agent.ConnectionGeneration,
                now),
            Is.True);
    }

    private sealed record KnownAgentSession(
        string AgentId,
        string SessionId,
        long ConnectionGeneration,
        string Token);

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
