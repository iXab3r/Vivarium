using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vivarium.Controller;
using Vivarium.Controller.Administration;

namespace Vivarium.Tests;

[TestFixture]
[NonParallelizable]
public sealed class AdministrationBootstrapTests
{
    private const string Password = "correct horse battery staple";
    private string rootDir = null!;

    [SetUp]
    public void SetUp()
    {
        rootDir = Path.Combine(
            Path.GetTempPath(),
            "vivarium-administration-bootstrap-tests",
            Guid.NewGuid().ToString("N"));
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
            // Best effort: preserve the original failure when a platform delays handle release.
        }
    }

    [Test]
    public async Task Bootstrap_is_private_rotatable_single_use_and_setup_only()
    {
        await using var controller = await StartControllerAsync();
        using var http = CreateClient(controller);
        var originalToken = controller.AdministrationBootstrap.Startup.BootstrapToken;
        Assert.That(originalToken, Is.Not.Null.And.Length.EqualTo(64));

        var statusResponse = await http.GetAsync("/api/v1/setup/status");
        var statusText = await statusResponse.Content.ReadAsStringAsync();
        using var status = JsonDocument.Parse(statusText);

        using var normalRoute = new HttpRequestMessage(HttpMethod.Get, "/api/v1/system");
        normalRoute.Headers.Authorization = new AuthenticationHeaderValue("Bearer", originalToken);
        var normalResponse = await http.SendAsync(normalRoute);

        var rotated = await controller.AdministrationBootstrap.RotateBootstrapAsync();
        var staleClaim = await ClaimAsync(http, originalToken!);
        var claim = await ClaimAsync(http, rotated.Token);
        var claimText = await claim.Content.ReadAsStringAsync();
        using var claimed = JsonDocument.Parse(claimText);
        var setupToken = claimed.RootElement.GetProperty("setupSessionToken").GetString()!;
        var operationId = claimed.RootElement.GetProperty("operationId").GetString()!;
        var replayedClaim = await ClaimAsync(http, rotated.Token);

        using var setupOnNormalRoute = new HttpRequestMessage(HttpMethod.Get, "/api/v1/system");
        setupOnNormalRoute.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", setupToken);
        var setupOnNormalResponse = await http.SendAsync(setupOnNormalRoute);

        var databaseState = await controller.Database.ReadAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    (SELECT COUNT(*) FROM administration_token_generations
                        WHERE purpose = 'BOOTSTRAP' AND consumed_unix_ms IS NOT NULL),
                    (SELECT COUNT(*) FROM administration_token_generations
                        WHERE purpose = 'BOOTSTRAP' AND revoked_unix_ms IS NOT NULL),
                    (SELECT COUNT(*) FROM administration_setup_sessions
                        WHERE operation_id = $operationId AND revoked_unix_ms IS NULL),
                    (SELECT COUNT(*) FROM administration_token_generations
                        WHERE typeof(token_salt) = 'blob' AND typeof(token_verifier) = 'blob');
                """;
            command.Parameters.AddWithValue("$operationId", operationId);
            using var reader = command.ExecuteReader();
            Assert.That(reader.Read(), Is.True);
            return (
                Consumed: reader.GetInt64(0),
                Revoked: reader.GetInt64(1),
                CurrentSessions: reader.GetInt64(2),
                HashedGenerations: reader.GetInt64(3));
        });
        var audits = JsonSerializer.Serialize(await controller.Audits.ListAsync(100));

        Assert.Multiple(() =>
        {
            Assert.That(statusResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(status.RootElement.GetProperty("state").GetString(), Is.EqualTo("unclaimed"));
            Assert.That(status.RootElement.GetProperty("tokenDeliveryHint").GetString(),
                Is.EqualTo("local-private-console"));
            Assert.That(statusText, Does.Not.Contain(originalToken!));
            Assert.That(normalResponse.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(staleClaim.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(claim.StatusCode, Is.EqualTo(HttpStatusCode.Created));
            Assert.That(claim.Headers.CacheControl?.NoStore, Is.True);
            Assert.That(replayedClaim.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(setupOnNormalResponse.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(databaseState, Is.EqualTo((1L, 1L, 1L, 2L)));
            Assert.That(audits, Does.Not.Contain(originalToken!));
            Assert.That(audits, Does.Not.Contain(rotated.Token));
            Assert.That(audits, Does.Not.Contain(setupToken));
        });
    }

    [Test]
    public async Task Setup_reservations_are_versioned_idempotent_and_do_not_persist_secrets()
    {
        await using var controller = await StartControllerAsync();
        using var http = CreateClient(controller);
        var bootstrapToken = controller.AdministrationBootstrap.Startup.BootstrapToken!;
        var claimResponse = await ClaimAsync(http, bootstrapToken);
        using var claim = JsonDocument.Parse(await claimResponse.Content.ReadAsStreamAsync());
        var setupToken = claim.RootElement.GetProperty("setupSessionToken").GetString()!;
        var operationId = claim.RootElement.GetProperty("operationId").GetString()!;

        var administrator = await PutSetupAsync(
            http,
            "/api/v1/setup/administrator",
            setupToken,
            "administrator-1",
            new
            {
                stateVersion = 1,
                login = "admin",
                displayName = "Vivarium Administrator",
                password = Password,
            });
        var administratorText = await administrator.Content.ReadAsStringAsync();
        using var administratorBody = JsonDocument.Parse(administratorText);
        var replay = await PutSetupAsync(
            http,
            "/api/v1/setup/administrator",
            setupToken,
            "administrator-1",
            new
            {
                stateVersion = 1,
                login = "admin",
                displayName = "Vivarium Administrator",
                password = Password,
            });
        using var replayBody = JsonDocument.Parse(await replay.Content.ReadAsStreamAsync());
        var changedPassword = await PutSetupAsync(
            http,
            "/api/v1/setup/administrator",
            setupToken,
            "administrator-1",
            new
            {
                stateVersion = 1,
                login = "admin",
                displayName = "Vivarium Administrator",
                password = "a different sufficiently long password",
            });

        var repository = await PutSetupAsync(
            http,
            "/api/v1/setup/config-repository",
            setupToken,
            "repository-1",
            new { stateVersion = 2, mode = "managed-local" });
        using var repositoryBody = JsonDocument.Parse(await repository.Content.ReadAsStreamAsync());
        using var changesRequest = SetupRequest(HttpMethod.Get, "/api/v1/setup/changes", setupToken);
        var changes = await http.SendAsync(changesRequest);
        using var changesBody = JsonDocument.Parse(await changes.Content.ReadAsStreamAsync());
        using var operationRequest = SetupRequest(
            HttpMethod.Get,
            $"/api/v1/setup/operations/{Uri.EscapeDataString(operationId)}",
            setupToken);
        var operation = await http.SendAsync(operationRequest);
        var operationText = await operation.Content.ReadAsStringAsync();
        var completion = await SendSetupAsync(
            http,
            HttpMethod.Post,
            "/api/v1/setup/completion",
            setupToken,
            "completion-1",
            new { stateVersion = 3 });
        var completionText = await completion.Content.ReadAsStringAsync();
        Assert.That(completion.StatusCode, Is.EqualTo(HttpStatusCode.OK), completionText);
        using var completionBody = JsonDocument.Parse(completionText);
        var activeCommit = completionBody.RootElement.GetProperty("commit").GetString();
        var administrationStatus = await controller.AdministrationBootstrap.GetStatusAsync();
        var finalValidation = await controller.ConfigurationRepository.ValidateRevisionAsync(
            await controller.ConfigurationRepository.GetAuthoritativeHeadAsync());

        using var login = new HttpRequestMessage(HttpMethod.Post, "/login")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["login"] = "admin",
                ["password"] = Password,
            }),
        };
        var signedIn = await http.SendAsync(login);
        var agentsPage = await http.GetAsync("/agents");

        var stored = await controller.Database.ReadAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT password_algorithm, password_iterations,
                       length(password_salt), length(password_verifier),
                       (SELECT group_concat(response_json, '')
                        FROM administration_setup_requests
                        WHERE operation_id = $operationId),
                       (SELECT credential_state FROM authorization_user_credentials
                        WHERE user_id = administration_setup_operations.pending_user_id),
                       (SELECT credential_generation FROM authorization_user_credentials
                        WHERE user_id = administration_setup_operations.pending_user_id)
                FROM administration_setup_operations
                WHERE operation_id = $operationId;
                """;
            command.Parameters.AddWithValue("$operationId", operationId);
            using var reader = command.ExecuteReader();
            Assert.That(reader.Read(), Is.True);
            return (
                Algorithm: reader.GetString(0),
                Iterations: reader.GetInt32(1),
                SaltLength: reader.GetInt32(2),
                VerifierLength: reader.GetInt32(3),
                Responses: reader.GetString(4),
                CredentialState: reader.GetString(5),
                CredentialGeneration: reader.GetInt64(6));
        });

        Assert.Multiple(() =>
        {
            Assert.That(administrator.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(administratorBody.RootElement.GetProperty("stateVersion").GetInt64(),
                Is.EqualTo(2));
            Assert.That(administratorBody.RootElement.GetProperty("replayed").GetBoolean(), Is.False);
            Assert.That(administratorText, Does.Not.Contain(Password));
            Assert.That(replay.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(replayBody.RootElement.GetProperty("replayed").GetBoolean(), Is.True);
            Assert.That(changedPassword.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
            Assert.That(repository.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(repositoryBody.RootElement.GetProperty("stateVersion").GetInt64(),
                Is.EqualTo(3));
            Assert.That(changes.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(changesBody.RootElement.GetProperty("valid").GetBoolean(), Is.True);
            Assert.That(operation.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(operationText, Does.Not.Contain(Password));
            Assert.That(completion.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(completionBody.RootElement.GetProperty("active").GetBoolean(), Is.True);
            Assert.That(administrationStatus.State, Is.EqualTo(AdministrationState.Active));
            Assert.That(administrationStatus.SetupOperationId, Is.EqualTo(operationId));
            Assert.That(finalValidation.IsValid, Is.True);
            Assert.That(finalValidation.Validated!.Documents.Count(document =>
                    document.Kind is "User" or "RoleBinding"),
                Is.EqualTo(2));
            Assert.That(finalValidation.Validated.Documents
                    .Where(document => document.Kind is "User" or "RoleBinding")
                    .Select(_ => finalValidation.Revision.Commit),
                Has.All.EqualTo(activeCommit));
            Assert.That(signedIn.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
            Assert.That(agentsPage.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(stored.Algorithm, Is.EqualTo("PBKDF2-SHA256"));
            Assert.That(stored.Iterations, Is.GreaterThanOrEqualTo(210_000));
            Assert.That(stored.SaltLength, Is.EqualTo(16));
            Assert.That(stored.VerifierLength, Is.EqualTo(32));
            Assert.That(stored.Responses, Does.Not.Contain(Password));
            Assert.That(stored.CredentialState, Is.EqualTo("ACTIVE"));
            Assert.That(stored.CredentialGeneration, Is.EqualTo(1));
            Assert.That(PrivateFilesContain(Password), Is.False);
            Assert.That(PrivateFilesContain(bootstrapToken), Is.False);
            Assert.That(PrivateFilesContain(setupToken), Is.False);
        });
    }

    [Test]
    public async Task Pending_setup_survives_restart_and_local_reissue_or_abandon_controls_access()
    {
        string operationId;
        string originalSession;
        await using (var first = await StartControllerAsync())
        {
            using var firstHttp = CreateClient(first);
            var response = await ClaimAsync(
                firstHttp,
                first.AdministrationBootstrap.Startup.BootstrapToken!);
            using var claim = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
            operationId = claim.RootElement.GetProperty("operationId").GetString()!;
            originalSession = claim.RootElement.GetProperty("setupSessionToken").GetString()!;
        }

        await using var restarted = await StartControllerAsync();
        using var http = CreateClient(restarted);
        using var persistedRequest = SetupRequest(
            HttpMethod.Get,
            $"/api/v1/setup/operations/{Uri.EscapeDataString(operationId)}",
            originalSession);
        var persisted = await http.SendAsync(persistedRequest);
        var reissue = await restarted.AdministrationBootstrap.ReissueSetupAccessAsync(operationId);
        using var revokedRequest = SetupRequest(
            HttpMethod.Get,
            $"/api/v1/setup/operations/{Uri.EscapeDataString(operationId)}",
            originalSession);
        var revoked = await http.SendAsync(revokedRequest);
        var resumedResponse = await ClaimAsync(http, reissue.Token);
        using var resumed = JsonDocument.Parse(await resumedResponse.Content.ReadAsStreamAsync());
        var resumedSession = resumed.RootElement.GetProperty("setupSessionToken").GetString()!;
        var reusedResume = await ClaimAsync(http, reissue.Token);

        var replacement = await restarted.AdministrationBootstrap.AbandonSetupAsync(
            operationId,
            "operator requested a clean first-run attempt");
        using var abandonedSessionRequest = SetupRequest(
            HttpMethod.Get,
            $"/api/v1/setup/operations/{Uri.EscapeDataString(operationId)}",
            resumedSession);
        var abandonedSession = await http.SendAsync(abandonedSessionRequest);
        var status = await restarted.AdministrationBootstrap.GetStatusAsync();
        var abandoned = await restarted.AdministrationBootstrap.GetOperationAsync(operationId);
        var replacementClaim = await ClaimAsync(http, replacement.Token);
        using var replacementBody = JsonDocument.Parse(
            await replacementClaim.Content.ReadAsStreamAsync());

        Assert.Multiple(() =>
        {
            Assert.That(restarted.AdministrationBootstrap.Startup.State,
                Is.EqualTo(AdministrationState.SetupInProgress));
            Assert.That(restarted.AdministrationBootstrap.Startup.BootstrapToken, Is.Null);
            Assert.That(persisted.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(revoked.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(resumedResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(resumed.RootElement.GetProperty("resumed").GetBoolean(), Is.True);
            Assert.That(resumed.RootElement.GetProperty("operationId").GetString(),
                Is.EqualTo(operationId));
            Assert.That(reusedResume.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(abandonedSession.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(status.State, Is.EqualTo(AdministrationState.Unclaimed));
            Assert.That(status.SetupOperationId, Is.Null);
            Assert.That(abandoned?.State, Is.EqualTo(SetupOperationState.Abandoned));
            Assert.That(replacementClaim.StatusCode, Is.EqualTo(HttpStatusCode.Created));
            Assert.That(replacementBody.RootElement.GetProperty("operationId").GetString(),
                Is.Not.EqualTo(operationId));
        });
    }

    [Test]
    public async Task Recovery_is_host_issued_single_use_restart_safe_and_explicitly_revocable()
    {
        string recoverySession;
        string recoveryToken;
        await using (var first = await StartControllerAsync())
        {
            using var http = CreateClient(first);
            var claimResponse = await ClaimAsync(
                http,
                first.AdministrationBootstrap.Startup.BootstrapToken!);
            using var claim = JsonDocument.Parse(await claimResponse.Content.ReadAsStreamAsync());
            var setupToken = claim.RootElement.GetProperty("setupSessionToken").GetString()!;
            await PutSetupAsync(
                http,
                "/api/v1/setup/administrator",
                setupToken,
                "recovery-admin",
                new
                {
                    stateVersion = 1,
                    login = "recovery.admin",
                    displayName = "Recovery Administrator",
                    password = Password,
                });
            await PutSetupAsync(
                http,
                "/api/v1/setup/config-repository",
                setupToken,
                "recovery-repository",
                new { stateVersion = 2, mode = "managed-local" });
            var completion = await SendSetupAsync(
                http,
                HttpMethod.Post,
                "/api/v1/setup/completion",
                setupToken,
                "recovery-completion",
                new { stateVersion = 3 });
            Assert.That(completion.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            var issued = await first.AdministrationBootstrap.IssueRecoveryAccessAsync(
                "lost administrator credential drill");
            recoveryToken = issued.Token;
            using var unexchanged = new HttpRequestMessage(HttpMethod.Get, "/api/v1/system");
            unexchanged.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", recoveryToken);
            Assert.That((await http.SendAsync(unexchanged)).StatusCode,
                Is.EqualTo(HttpStatusCode.Unauthorized));

            var invalid = await http.PostAsJsonAsync(
                "/api/v1/recovery/claims",
                new { token = new string('0', 64) });
            var exchanged = await http.PostAsJsonAsync(
                "/api/v1/recovery/claims",
                new { token = recoveryToken });
            var exchangedText = await exchanged.Content.ReadAsStringAsync();
            Assert.That(exchanged.StatusCode, Is.EqualTo(HttpStatusCode.OK), exchangedText);
            using var exchangeBody = JsonDocument.Parse(exchangedText);
            recoverySession = exchangeBody.RootElement
                .GetProperty("recoverySessionToken").GetString()!;
            var reused = await http.PostAsJsonAsync(
                "/api/v1/recovery/claims",
                new { token = recoveryToken });

            Assert.Multiple(() =>
            {
                Assert.That(invalid.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
                Assert.That(reused.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            });
        }

        await using var restarted = await StartControllerAsync();
        using var restartedHttp = CreateClient(restarted);
        using var authorized = new HttpRequestMessage(HttpMethod.Get, "/api/v1/system");
        authorized.Headers.Authorization =
            new AuthenticationHeaderValue("Vivarium-Recovery", recoverySession);
        var recoveryAccess = await restartedHttp.SendAsync(authorized);
        await restarted.AdministrationBootstrap.RevokeRecoveryAsync("recovery drill complete");
        using var revoked = new HttpRequestMessage(HttpMethod.Get, "/api/v1/system");
        revoked.Headers.Authorization =
            new AuthenticationHeaderValue("Vivarium-Recovery", recoverySession);
        var revokedAccess = await restartedHttp.SendAsync(revoked);
        var status = await restarted.AdministrationBootstrap.GetStatusAsync();
        var audits = JsonSerializer.Serialize(await restarted.Audits.ListAsync(100));

        Assert.Multiple(() =>
        {
            Assert.That(restarted.AdministrationBootstrap.Startup.State,
                Is.EqualTo(AdministrationState.RecoveryInProgress));
            Assert.That(restarted.AdministrationBootstrap.Startup.BootstrapToken, Is.Null);
            Assert.That(recoveryAccess.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(revokedAccess.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(status.State, Is.EqualTo(AdministrationState.Active));
            Assert.That(audits, Does.Not.Contain(recoveryToken));
            Assert.That(audits, Does.Not.Contain(recoverySession));
        });
    }

    private Task<VivariumControllerHost> StartControllerAsync() =>
        VivariumControllerHost.StartAsync(new ControllerOptions
        {
            DataDir = Path.Combine(rootDir, "controller"),
            Host = "127.0.0.1",
            Port = 0,
        });

    private static HttpClient CreateClient(VivariumControllerHost controller)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                certificate is not null &&
                Convert.ToHexString(SHA256.HashData(certificate.RawData)).Equals(
                    controller.Certificate.FingerprintSha256,
                    StringComparison.OrdinalIgnoreCase),
        };
        return new HttpClient(handler) { BaseAddress = new Uri(controller.Url) };
    }

    private static Task<HttpResponseMessage> ClaimAsync(HttpClient http, string token) =>
        http.PostAsJsonAsync("/api/v1/setup/claims", new { token });

    private static Task<HttpResponseMessage> PutSetupAsync<T>(
        HttpClient http,
        string url,
        string setupToken,
        string idempotencyKey,
        T body)
        => SendSetupAsync(http, HttpMethod.Put, url, setupToken, idempotencyKey, body);

    private static Task<HttpResponseMessage> SendSetupAsync<T>(
        HttpClient http,
        HttpMethod method,
        string url,
        string setupToken,
        string idempotencyKey,
        T body)
    {
        var request = SetupRequest(method, url, setupToken);
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        request.Content = JsonContent.Create(body);
        return http.SendAsync(request);
    }

    private static HttpRequestMessage SetupRequest(
        HttpMethod method,
        string url,
        string setupToken)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Vivarium-Setup", setupToken);
        return request;
    }

    private bool PrivateFilesContain(string secret)
    {
        var needle = Encoding.UTF8.GetBytes(secret);
        foreach (var path in Directory.EnumerateFiles(
                     Path.Combine(rootDir, "controller"),
                     "*",
                     SearchOption.AllDirectories))
        {
            byte[] bytes;
            try
            {
                using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                bytes = memory.ToArray();
            }
            catch (IOException)
            {
                continue;
            }

            if (bytes.AsSpan().IndexOf(needle) >= 0)
            {
                return true;
            }
        }

        return false;
    }
}
