using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Vivarium.Contracts.V1;
using Vivarium.Controller;
using Vivarium.Controller.Agents;
using Vivarium.Controller.Auditing;
using Vivarium.Controller.Persistence;
using Vivarium.Controller.Security;

namespace Vivarium.Tests;

[TestFixture]
[NonParallelizable]
public sealed class AgentRestApiTests
{
    private string rootDir = null!;

    [SetUp]
    public void SetUp()
    {
        rootDir = Path.Combine(
            Path.GetTempPath(), "vivarium-agent-rest-tests", Guid.NewGuid().ToString("N"));
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
    public async Task Agent_store_query_is_filtered_and_keyset_paginated_with_id_tiebreaker()
    {
        var dataDir = Path.Combine(rootDir, "store");
        Directory.CreateDirectory(dataDir);
        await using var database = new VivariumDatabase(dataDir);
        var tokens = new TokenStore(dataDir, database);
        var agents = new AgentStore(database);
        await AddStoredAgentAsync(tokens, agents, "agent-c", "Berlin", "windows", authorized: true);
        await AddStoredAgentAsync(tokens, agents, "agent-a", "berlin", "windows", authorized: true);
        await AddStoredAgentAsync(tokens, agents, "agent-b", "Paris", "linux", authorized: false);
        await agents.SetCustomParameterAsync("agent-a", "custom.lab", "secure-berlin");

        var query = new AgentStoreQuery(
            Search: "berlin",
            Authorized: true,
            OsFamilies: ["WINDOWS"],
            Sort: AgentStoreSort.NameAscending);
        var first = await agents.QueryPageAsync(query, after: null, limit: 1);
        var second = await agents.QueryPageAsync(query, first.NextCursor, limit: 1);

        Assert.Multiple(() =>
        {
            Assert.That(first.Items, Has.Count.EqualTo(1));
            Assert.That(first.Items[0].Agent.AgentId, Is.EqualTo("agent-c"),
                "case-insensitive name ties must retain a deterministic binary-name then ID order");
            Assert.That(first.NextCursor, Is.Not.Null);
            Assert.That(second.Items, Has.Count.EqualTo(1));
            Assert.That(second.Items[0].Agent.AgentId, Is.EqualTo("agent-a"));
            Assert.That(second.NextCursor, Is.Null);
            Assert.That(second.Items[0].Agent.CustomParameters["custom.lab"], Is.EqualTo("secure-berlin"));
        });
    }

    [Test]
    public async Task Audit_store_query_preserves_source_outcome_redaction_and_stable_cursor_order()
    {
        var dataDir = Path.Combine(rootDir, "audit-store");
        Directory.CreateDirectory(dataDir);
        await using var database = new VivariumDatabase(dataDir);
        var audits = new AuditEventStore(database);
        var receivedAt = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var context = new ManagementRequestContext(
            ManagementPrincipal.LegacyAdmin,
            "audit-rest-correlation",
            "audit-request",
            "tier-one-test");
        foreach (var eventId in new[] { "event-a", "event-b", "event-c" })
        {
            await audits.AppendAsync(new AuditEventDraft(
                eventId,
                receivedAt,
                context,
                "agent.read-sensitive",
                "agent",
                "agent-a",
                eventId == "event-c" ? AuditOutcome.Denied : AuditOutcome.Succeeded,
                eventId == "event-c" ? "permission_denied" : string.Empty,
                new Dictionary<string, string> { ["field_set"] = "process-summary" }));
        }

        var query = new AuditEventQuery(
            Actions: ["agent.read-sensitive"],
            TargetTypes: ["agent"]);
        var first = await audits.QueryPageAsync(query, after: null, limit: 2);
        var second = await audits.QueryPageAsync(query, first.NextCursor, limit: 2);

        Assert.Multiple(() =>
        {
            Assert.That(first.Items.Select(item => item.AuditEvent.AuditEventId),
                Is.EqualTo(new[] { "event-c", "event-b" }));
            Assert.That(first.Items[0].AuditEvent.Outcome, Is.EqualTo(AuditOutcome.Denied));
            Assert.That(first.Items[0].AuditEvent.Source, Is.EqualTo("tier-one-test"));
            Assert.That(first.Items[0].AuditEvent.Details,
                Is.EqualTo(new Dictionary<string, string> { ["field_set"] = "process-summary" }));
            Assert.That(first.NextCursor, Is.Not.Null);
            Assert.That(second.Items.Select(item => item.AuditEvent.AuditEventId),
                Is.EqualTo(new[] { "event-a" }));
            Assert.That(second.NextCursor, Is.Null);
        });
    }

    [Test]
    public async Task Agents_rest_exposes_status_axes_parameters_filters_cursor_and_detail_etag()
    {
        await using var controller = await StartControllerAsync();
        await AddStoredAgentAsync(
            controller.Tokens,
            controller.AgentStore,
            "agent-b",
            "Beta",
            "linux",
            authorized: false);
        var liveHello = await AddStoredAgentAsync(
            controller.Tokens,
            controller.AgentStore,
            "agent-a",
            "Alpha",
            "windows",
            authorized: true);
        await controller.AgentStore.SetCustomParameterAsync(
            "agent-a", "custom.lab", "berlin");
        using var sessionAbort = new CancellationTokenSource();
        var connection = controller.Registry.Register(
            liveHello,
            AgentAuth.Authorized,
            enabled: true,
            sessionAbort);
        Assert.That(controller.Registry.Reconcile(connection, currentBuildId: "orphan-child-build"), Is.True);

        using var http = PinnedClient(controller);
        using var firstRequest = AdminGet(
            controller,
            "/api/v1/agents?connected=true&osFamily=windows&limit=1&sort=name");
        var first = await http.SendAsync(firstRequest);
        using var firstBody = JsonDocument.Parse(await first.Content.ReadAsStreamAsync());
        var item = firstBody.RootElement.GetProperty("items")[0];
        var hasNextCursor = firstBody.RootElement.GetProperty("page")
            .TryGetProperty("nextCursor", out _);

        using var detailRequest = AdminGet(controller, "/api/v1/agents/agent-a");
        var detail = await http.SendAsync(detailRequest);
        var etag = detail.Headers.ETag?.Tag;
        using var conditionalRequest = AdminGet(controller, "/api/v1/agents/agent-a");
        conditionalRequest.Headers.TryAddWithoutValidation("If-None-Match", etag);
        var notModified = await http.SendAsync(conditionalRequest);

        using var allFirstRequest = AdminGet(controller, "/api/v1/agents?limit=1&sort=name");
        var allFirst = await http.SendAsync(allFirstRequest);
        using var allFirstBody = JsonDocument.Parse(await allFirst.Content.ReadAsStreamAsync());
        var collectionEtag = allFirst.Headers.ETag?.Tag;
        var collectionCursor = allFirstBody.RootElement.GetProperty("page")
            .GetProperty("nextCursor").GetString();
        using var allSecondRequest = AdminGet(
            controller,
            $"/api/v1/agents?limit=1&sort=name&cursor={Uri.EscapeDataString(collectionCursor!)}");
        var allSecond = await http.SendAsync(allSecondRequest);
        using var allSecondBody = JsonDocument.Parse(await allSecond.Content.ReadAsStreamAsync());
        using var collectionConditionalRequest = AdminGet(
            controller, "/api/v1/agents?limit=1&sort=name");
        collectionConditionalRequest.Headers.TryAddWithoutValidation("If-None-Match", collectionEtag);
        var collectionNotModified = await http.SendAsync(collectionConditionalRequest);

        Assert.Multiple(() =>
        {
            Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(firstBody.RootElement.GetProperty("items").GetArrayLength(), Is.EqualTo(1));
            Assert.That(hasNextCursor, Is.False,
                "runtime filtering must not manufacture a next page when no later Agent matches");
            Assert.That(item.GetProperty("id").GetString(), Is.EqualTo("agent-a"));
            Assert.That(item.GetProperty("displayName").GetString(), Is.EqualTo("Alpha"));
            Assert.That(item.GetProperty("hostname").GetString(), Is.EqualTo("Alpha"));
            Assert.That(item.GetProperty("status").GetProperty("connected").GetBoolean(), Is.True);
            Assert.That(item.GetProperty("status").GetProperty("reconciled").GetBoolean(), Is.True);
            Assert.That(item.GetProperty("status").GetProperty("authorized").GetBoolean(), Is.True);
            Assert.That(item.GetProperty("status").GetProperty("enabled").GetBoolean(), Is.True);
            Assert.That(item.GetProperty("status").GetProperty("activity").GetString(),
                Is.EqualTo("building"));
            Assert.That(item.GetProperty("currentBuild").GetProperty("id").GetString(),
                Is.EqualTo("orphan-child-build"));
            Assert.That(item.GetProperty("currentBuild").TryGetProperty("matrixBuildId", out _), Is.False);
            Assert.That(item.GetProperty("currentBuild").TryGetProperty("url", out _), Is.False,
                "a child build ID must never be emitted as a matrix-build route");
            Assert.That(item.GetProperty("freshness").GetString(), Is.EqualTo("current"));
            Assert.That(item.GetProperty("operatingSystem").GetProperty("family").GetString(),
                Is.EqualTo("windows"));
            Assert.That(item.GetProperty("software").GetProperty("agentVersion").GetString(),
                Is.EqualTo("1.0-test"));
            Assert.That(item.GetProperty("parameters").GetProperty("reported")
                .GetProperty("os.family").GetString(), Is.EqualTo("windows"));
            Assert.That(item.GetProperty("parameters").GetProperty("custom")
                .GetProperty("custom.lab").GetString(), Is.EqualTo("berlin"));
            Assert.That(item.GetProperty("parameters").GetProperty("effective")
                .GetProperty("custom.lab").GetString(), Is.EqualTo("berlin"));
            Assert.That(detail.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(etag, Is.Not.Null.And.StartsWith("\"").And.EndsWith("\""));
            Assert.That(notModified.StatusCode, Is.EqualTo(HttpStatusCode.NotModified));
            Assert.That(allFirst.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(collectionCursor, Is.Not.Null.And.Not.Empty);
            Assert.That(collectionEtag, Is.Not.Null.And.StartsWith("\"").And.EndsWith("\""));
            Assert.That(allSecond.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(allSecondBody.RootElement.GetProperty("items")[0].GetProperty("id").GetString(),
                Is.EqualTo("agent-b"));
            Assert.That(collectionNotModified.StatusCode, Is.EqualTo(HttpStatusCode.NotModified));
        });

        controller.Registry.Remove("agent-a");
    }

    [Test]
    public async Task Agent_and_audit_rest_enforce_legacy_scope_and_return_bounded_problem_details()
    {
        await using var controller = await StartControllerAsync();
        await AddStoredAgentAsync(
            controller.Tokens,
            controller.AgentStore,
            "visible-agent",
            "Visible",
            "windows",
            authorized: false);
        using var http = PinnedClient(controller);

        var anonymous = await http.GetAsync("/api/v1/agents");
        using var submitAgentRequest = SubmitGet(controller, "/api/v1/agents");
        var submitAgent = await http.SendAsync(submitAgentRequest);
        using var submitAuditRequest = SubmitGet(controller, "/api/v1/audit-events");
        var submitAudit = await http.SendAsync(submitAuditRequest);
        using var missingRequest = AdminGet(controller, "/api/v1/agents/missing-agent");
        var missing = await http.SendAsync(missingRequest);
        using var unsupportedRequest = AdminGet(controller, "/api/v1/agents?processName=dotnet");
        var unsupported = await http.SendAsync(unsupportedRequest);
        using var missingBody = JsonDocument.Parse(await missing.Content.ReadAsStreamAsync());
        using var unsupportedBody = JsonDocument.Parse(await unsupported.Content.ReadAsStreamAsync());

        Assert.Multiple(() =>
        {
            Assert.That(anonymous.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(submitAgent.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(submitAudit.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(missing.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(missingBody.RootElement.GetProperty("code").GetString(),
                Is.EqualTo("resource_not_found"));
            Assert.That(missingBody.RootElement.GetProperty("target").GetProperty("type").GetString(),
                Is.EqualTo("agent"));
            Assert.That(unsupported.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(unsupportedBody.RootElement.GetProperty("code").GetString(),
                Is.EqualTo("unsupported_filter"));
        });
    }

    [Test]
    public async Task Agent_and_audit_rest_survive_restart_and_audit_pages_remain_redacted_and_bounded()
    {
        var dataDir = Path.Combine(rootDir, "restart-controller");
        Directory.CreateDirectory(dataDir);
        const string correlationId = "rest-audit-restart";
        await using (var controller = await StartControllerAsync(dataDir))
        {
            await AddStoredAgentAsync(
                controller.Tokens,
                controller.AgentStore,
                "restart-agent",
                "Restart Agent",
                "macos",
                authorized: true);
            var context = new ManagementRequestContext(
                ManagementPrincipal.LegacyAdmin,
                correlationId,
                "request-restart",
                "rest-seed");
            await controller.Audits.AppendAsync(AuditEventDraft.Create(
                context,
                DateTimeOffset.FromUnixTimeSeconds(1_800_000_001),
                "agent.inspect",
                "agent",
                "restart-agent",
                AuditOutcome.Denied,
                "permission_denied",
                new Dictionary<string, string> { ["field_set"] = "safe-summary" }));
        }

        await using var restarted = await StartControllerAsync(dataDir);
        using var http = PinnedClient(restarted);
        using var agentRequest = AdminGet(restarted, "/api/v1/agents/restart-agent");
        var agent = await http.SendAsync(agentRequest);
        using var auditRequest = AdminGet(
            restarted,
            "/api/v1/audit-events?action=agent.inspect&outcome=denied&limit=1");
        var audit = await http.SendAsync(auditRequest);
        var auditJson = await audit.Content.ReadAsStringAsync();
        using var auditBody = JsonDocument.Parse(auditJson);
        var auditItem = auditBody.RootElement.GetProperty("items")[0];

        Assert.Multiple(() =>
        {
            Assert.That(agent.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(audit.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(auditBody.RootElement.GetProperty("items").GetArrayLength(), Is.EqualTo(1));
            Assert.That(auditBody.RootElement.GetProperty("page").GetProperty("limit").GetInt32(),
                Is.EqualTo(1));
            Assert.That(auditItem.GetProperty("source").GetString(), Is.EqualTo("rest-seed"));
            Assert.That(auditItem.GetProperty("outcome").GetString(), Is.EqualTo("denied"));
            Assert.That(auditItem.GetProperty("correlationId").GetString(), Is.EqualTo(correlationId));
            Assert.That(auditItem.GetProperty("details").GetProperty("field_set").GetString(),
                Is.EqualTo("safe-summary"));
            Assert.That(auditJson, Does.Not.Contain("_request_source"));
            Assert.That(auditJson, Does.Not.Contain(restarted.Tokens.AdminToken));
            Assert.That(auditJson, Does.Not.Contain(restarted.Tokens.SubmitToken));
        });
    }

    private Task<VivariumControllerHost> StartControllerAsync(string? dataDir = null) =>
        VivariumControllerHost.StartAsync(new ControllerOptions
        {
            DataDir = dataDir ?? Path.Combine(rootDir, "controller"),
            Host = "127.0.0.1",
            Port = 0,
        });

    private static async Task<Hello> AddStoredAgentAsync(
        TokenStore tokens,
        AgentStore agents,
        string agentId,
        string hostname,
        string osFamily,
        bool authorized)
    {
        var hello = new Hello
        {
            AgentId = agentId,
            EnrollToken = await tokens.CreateEnrollTokenAsync(),
            SessionId = $"session-{agentId}",
            AgentVersion = "1.0-test",
            Os = new OsInfo
            {
                Family = osFamily,
                Version = "test-version",
                Arch = "x64",
            },
            Interactive = true,
        };
        hello.Parameters["hostname"] = hostname;
        hello.Parameters["os.family"] = osFamily;
        Assert.That(await tokens.AdmitAgentAsync(hello), Is.Not.Null);
        await agents.ObserveHelloAsync(hello);
        await agents.RenameAsync(agentId, hostname);
        if (authorized)
        {
            _ = await tokens.AuthorizeAgentAsync(agentId);
        }

        return hello;
    }

    private static HttpRequestMessage AdminGet(VivariumControllerHost controller, string path) =>
        BearerGet(path, controller.Tokens.AdminToken);

    private static HttpRequestMessage SubmitGet(VivariumControllerHost controller, string path) =>
        BearerGet(path, controller.Tokens.SubmitToken);

    private static HttpRequestMessage BearerGet(string path, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static HttpClient PinnedClient(VivariumControllerHost controller)
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
}
