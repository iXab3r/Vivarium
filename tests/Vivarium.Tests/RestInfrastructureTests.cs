using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Vivarium.Controller;
using Vivarium.Controller.Rest.Common;
using Vivarium.Controller.Security;

namespace Vivarium.Tests;

[TestFixture]
[NonParallelizable]
public sealed class RestInfrastructureTests
{
    private string rootDir = null!;

    [SetUp]
    public void SetUp()
    {
        rootDir = Path.Combine(
            Path.GetTempPath(), "vivarium-rest-infrastructure-tests", Guid.NewGuid().ToString("N"));
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
    public async Task System_requires_management_authentication_and_returns_rfc_9457_problem()
    {
        await using var controller = await StartControllerAsync();
        using var http = PinnedClient(controller);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/system");
        request.Headers.Add(ManagementRequestContextFactory.CorrelationHeader, "rest-system-anonymous");

        var response = await http.SendAsync(request);
        var serializedBody = await response.Content.ReadAsStringAsync();
        using var body = JsonDocument.Parse(serializedBody);

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(response.Content.Headers.ContentType?.MediaType,
                Is.EqualTo("application/problem+json"));
            Assert.That(
                response.Headers.GetValues(ManagementRequestContextFactory.CorrelationHeader).Single(),
                Is.EqualTo("rest-system-anonymous"));
            Assert.That(body.RootElement.GetProperty("type").GetString(),
                Is.EqualTo("https://vivarium.dev/problems/authentication-required"));
            Assert.That(body.RootElement.GetProperty("status").GetInt32(), Is.EqualTo(401));
            Assert.That(body.RootElement.GetProperty("code").GetString(),
                Is.EqualTo("authentication_required"));
            Assert.That(body.RootElement.GetProperty("correlationId").GetString(),
                Is.EqualTo("rest-system-anonymous"));
            Assert.That(body.RootElement.TryGetProperty("target", out _), Is.False);
            Assert.That(body.RootElement.TryGetProperty("errors", out _), Is.False);
        });
    }

    [Test]
    public async Task System_accepts_bearer_and_cookie_authentication_and_honors_etag()
    {
        await using var controller = await StartControllerAsync();
        using var bearerHttp = PinnedClient(controller);
        using var bearerRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/system");
        bearerRequest.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", controller.Tokens.SubmitToken);
        bearerRequest.Headers.Add(
            ManagementRequestContextFactory.CorrelationHeader, "rest-system-bearer");

        var bearer = await bearerHttp.SendAsync(bearerRequest);
        using var bearerBody = JsonDocument.Parse(await bearer.Content.ReadAsStreamAsync());
        var etag = bearer.Headers.ETag?.Tag;

        var cookies = new CookieContainer();
        using var cookieHttp = PinnedClient(controller, cookies);
        await LoginAsync(cookieHttp, controller.Tokens.AdminToken);
        using var cookieRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/system");
        cookieRequest.Headers.Add(
            ManagementRequestContextFactory.CorrelationHeader, "rest-system-cookie");
        var cookie = await cookieHttp.SendAsync(cookieRequest);

        using var conditionalRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/system");
        conditionalRequest.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", controller.Tokens.SubmitToken);
        conditionalRequest.Headers.TryAddWithoutValidation("If-None-Match", etag);
        var notModified = await bearerHttp.SendAsync(conditionalRequest);

        Assert.Multiple(() =>
        {
            Assert.That(bearer.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(bearerBody.RootElement.GetProperty("apiVersion").GetString(), Is.EqualTo("v1"));
            Assert.That(bearerBody.RootElement.GetProperty("limits")
                .GetProperty("maximumPageLimit").GetInt32(), Is.EqualTo(200));
            Assert.That(etag, Is.Not.Null.And.StartsWith("\"").And.EndsWith("\""));
            Assert.That(cookie.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(
                cookie.Headers.GetValues(ManagementRequestContextFactory.CorrelationHeader).Single(),
                Is.EqualTo("rest-system-cookie"));
            Assert.That(notModified.StatusCode, Is.EqualTo(HttpStatusCode.NotModified));
            Assert.That(notModified.Content.Headers.ContentLength ?? 0, Is.Zero);
        });
    }

    [Test]
    public async Task Invalid_supplied_correlation_id_is_a_bounded_problem()
    {
        await using var controller = await StartControllerAsync();
        using var http = PinnedClient(controller);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/system");
        request.Headers.TryAddWithoutValidation(
            ManagementRequestContextFactory.CorrelationHeader,
            "token value with whitespace");

        var response = await http.SendAsync(request);
        var serializedBody = await response.Content.ReadAsStringAsync();
        using var body = JsonDocument.Parse(serializedBody);
        var generatedCorrelation = response.Headers
            .GetValues(ManagementRequestContextFactory.CorrelationHeader).Single();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(body.RootElement.GetProperty("code").GetString(),
                Is.EqualTo("invalid_correlation_id"));
            Assert.That(generatedCorrelation, Has.Length.EqualTo(32));
            Assert.That(body.RootElement.GetProperty("correlationId").GetString(),
                Is.EqualTo(generatedCorrelation));
            Assert.That(serializedBody,
                Does.Not.Contain("token value with whitespace"));
        });
    }

    [Test]
    public async Task Openapi_is_repeatable_across_restart_and_documents_stable_operations()
    {
        byte[] first;
        byte[] repeated;
        await using (var controller = await StartControllerAsync())
        {
            using var http = PinnedClient(controller);
            first = await http.GetByteArrayAsync("/openapi/v1.json");
            repeated = await http.GetByteArrayAsync("/openapi/v1.json");
        }

        byte[] afterRestart;
        await using (var restarted = await StartControllerAsync())
        {
            using var http = PinnedClient(restarted);
            afterRestart = await http.GetByteArrayAsync("/openapi/v1.json");
        }

        using var document = JsonDocument.Parse(first);
        var paths = document.RootElement.GetProperty("paths");
        var operations = paths
            .EnumerateObject()
            .SelectMany(path => path.Value.EnumerateObject()
                .Where(candidate => candidate.Name is
                    "get" or "put" or "post" or "patch" or "delete")
                .Select(candidate => candidate.Value))
            .ToArray();
        var operation = document.RootElement
            .GetProperty("paths")
            .GetProperty("/api/v1/system")
            .GetProperty("get");

        Assert.Multiple(() =>
        {
            Assert.That(repeated, Is.EqualTo(first),
                "the published contract must be byte-repeatable");
            Assert.That(afterRestart, Is.EqualTo(first),
                "controller restart must not drift the published contract");
            Assert.That(paths.EnumerateObject().Select(path => path.Name),
                Has.All.StartsWith("/api/v1/"),
                "the public management document must not publish panel or blob data-plane routes");
            Assert.That(paths.TryGetProperty(
                "/api/v1/agent-packages/{rid}/{version}", out _), Is.False,
                "raw Agent package publication is a hidden development fixture, not a public contract");
            Assert.That(document.RootElement.GetProperty("info").GetProperty("title").GetString(),
                Is.EqualTo("Vivarium Management API"));
            Assert.That(document.RootElement.GetProperty("info").GetProperty("version").GetString(),
                Is.EqualTo("v1"));
            Assert.That(operation.GetProperty("operationId").GetString(), Is.EqualTo("getSystem"));
            Assert.That(operation.GetProperty("tags")[0].GetString(), Is.EqualTo("System"));
            Assert.That(operation.GetProperty("responses").TryGetProperty("200", out _), Is.True);
            Assert.That(operation.GetProperty("responses").TryGetProperty("304", out _), Is.True);
            Assert.That(operation.GetProperty("responses").TryGetProperty("401", out _), Is.True);
            Assert.That(operation.GetProperty("responses").TryGetProperty("403", out _), Is.True);
            Assert.That(operations, Is.Not.Empty);
            Assert.That(operations.All(candidate =>
                candidate.TryGetProperty("operationId", out var id) &&
                !string.IsNullOrWhiteSpace(id.GetString())), Is.True,
                "every published operation must carry a stable operationId");
            Assert.That(operations.All(candidate =>
                candidate.TryGetProperty("tags", out var tags) &&
                tags.GetArrayLength() > 0), Is.True,
                "every published operation must carry a domain tag");
            Assert.That(operations.Select(candidate =>
                candidate.GetProperty("operationId").GetString()), Is.Unique,
                "operationIds must remain globally unique");
        });
    }

    [Test]
    public void Pagination_rejects_out_of_range_limits()
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?limit=201");

        var exception = Assert.Throws<RestApiException>(() =>
            RestPagination.ParseLimit(context.Request));

        Assert.Multiple(() =>
        {
            Assert.That(exception!.Status, Is.EqualTo(400));
            Assert.That(exception.Code, Is.EqualTo("invalid_limit"));
            Assert.That(exception.Errors, Has.Count.EqualTo(1));
            Assert.That(exception.Errors![0].Path, Is.EqualTo("limit"));
        });
    }

    [Test]
    public void Cursor_is_opaque_tamper_evident_scoped_and_expiring()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var time = new ManualTimeProvider(now);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(time);
        services.AddDataProtection()
            .SetApplicationName($"Vivarium.Rest.Tests.{Guid.NewGuid():N}");
        services.AddVivariumRestApi();
        using var provider = services.BuildServiceProvider();
        var codec = provider.GetRequiredService<RestCursorCodec>();
        var principal = ManagementPrincipal.LegacyAdmin;
        var cursor = codec.Encode(
            "created=10;id=agent-a",
            principal,
            "agents",
            "filter-a",
            "-createdAt,id");

        var decoded = codec.Decode(
            cursor,
            principal,
            "agents",
            "filter-a",
            "-createdAt,id");
        var tampered = cursor[..^1] + (cursor[^1] == 'A' ? 'B' : 'A');
        var tamperError = Assert.Throws<RestApiException>(() => codec.Decode(
            tampered, principal, "agents", "filter-a", "-createdAt,id"));
        var scopeError = Assert.Throws<RestApiException>(() => codec.Decode(
            cursor, ManagementPrincipal.LegacySubmit, "agents", "filter-a", "-createdAt,id"));
        time.Advance(RestCursorCodec.Lifetime + TimeSpan.FromMilliseconds(1));
        var expiryError = Assert.Throws<RestApiException>(() => codec.Decode(
            cursor, principal, "agents", "filter-a", "-createdAt,id"));

        Assert.Multiple(() =>
        {
            Assert.That(decoded, Is.EqualTo("created=10;id=agent-a"));
            Assert.That(cursor, Does.Not.Contain("created=10"));
            Assert.That(tamperError!.Code, Is.EqualTo("invalid_cursor"));
            Assert.That(scopeError!.Code, Is.EqualTo("cursor_context_mismatch"));
            Assert.That(expiryError!.Status, Is.EqualTo(410));
            Assert.That(expiryError.Code, Is.EqualTo("cursor_expired"));
        });
    }

    private Task<VivariumControllerHost> StartControllerAsync() =>
        VivariumControllerHost.StartAsync(new ControllerOptions
        {
            DataDir = Path.Combine(rootDir, "controller"),
            Host = "127.0.0.1",
            Port = 0,
        });

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
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                certificate is not null &&
                Convert.ToHexString(SHA256.HashData(certificate.RawData))
                    .Equals(controller.Certificate.FingerprintSha256, StringComparison.OrdinalIgnoreCase),
        };
        return new HttpClient(handler) { BaseAddress = new Uri(controller.Url) };
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;

        public void Advance(TimeSpan amount) => utcNow += amount;
    }
}
