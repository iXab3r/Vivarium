using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Vivarium.Controller.Rest.Builds;
using Vivarium.Controller.Rest.Builds.Mutations;
using Vivarium.Controller.Rest.Events;

namespace Vivarium.Tests;

[TestFixture]
public sealed class BuildEventStreamTests
{
    private string rootDir = null!;

    [SetUp]
    public void SetUp()
    {
        rootDir = Path.Combine(
            Path.GetTempPath(), "vivarium-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootDir);
    }

    [TearDown]
    public void TearDown() => BuildMutationRestHarness.DeleteDirectory(rootDir);

    [Test]
    public async Task Sse_replays_resumes_and_returns_retention_gap_recovery()
    {
        await using var controller = await BuildMutationRestHarness.StartAsync(rootDir);
        await BuildMutationRestHarness.RegisterAuthorizedAgentAsync(controller, "agent-events");
        using var http = BuildMutationRestHarness.CreateClient(
            controller, controller.Tokens.AdminToken);
        var stagingId = await BuildMutationRestHarness.CreateUploadedPlanAsync(
            http, "project-events", "plan-events-1");
        using var submitted = await BuildMutationRestHarness.SubmitAsync(
            http,
            BuildMutationRestHarness.CreateRequest(
                "project-events", "configuration-events", stagingId),
            "build-events-1");
        var build = await submitted.Content.ReadFromJsonAsync<BuildResource>()
            ?? throw new AssertionException("Build response was empty");

        using var streamCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var streamRequest = EventRequest(build.Id);
        using var streamResponse = await http.SendAsync(
            streamRequest,
            HttpCompletionOption.ResponseHeadersRead,
            streamCancellation.Token);
        Assert.That(streamResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        await using var stream = await streamResponse.Content.ReadAsStreamAsync(
            streamCancellation.Token);
        using var reader = new StreamReader(stream);
        var created = await ReadEventAsync(reader, streamCancellation.Token);
        Assert.Multiple(() =>
        {
            Assert.That(created.Type, Is.EqualTo("build.created"));
            Assert.That(created.Resource.Type, Is.EqualTo("build"));
            Assert.That(created.Resource.Id, Is.EqualTo(build.Id));
            Assert.That(created.RuntimeRevision, Is.EqualTo(build.RuntimeRevision));
        });

        await Task.Delay(5, streamCancellation.Token);
        using var cancelledResponse = await http.PutAsJsonAsync(
            $"/api/v1/builds/{build.Id}/cancellation",
            new BuildCancellationRequest("event-stream cancellation"),
            streamCancellation.Token);
        Assert.That(cancelledResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var cancelled = await ReadEventAsync(reader, streamCancellation.Token);
        Assert.Multiple(() =>
        {
            Assert.That(cancelled.Type, Is.EqualTo("build.cancellation-requested"));
            Assert.That(cancelled.Sequence, Is.GreaterThan(created.Sequence));
            Assert.That(cancelled.Id, Is.Not.EqualTo(created.Id));
        });

        streamCancellation.Cancel();

        using var resumeCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var resumeRequest = EventRequest(build.Id);
        resumeRequest.Headers.TryAddWithoutValidation("Last-Event-ID", created.Id);
        using var resumedResponse = await http.SendAsync(
            resumeRequest,
            HttpCompletionOption.ResponseHeadersRead,
            resumeCancellation.Token);
        await using var resumedStream = await resumedResponse.Content.ReadAsStreamAsync(
            resumeCancellation.Token);
        using var resumedReader = new StreamReader(resumedStream);
        var resumed = await ReadEventAsync(resumedReader, resumeCancellation.Token);
        Assert.Multiple(() =>
        {
            Assert.That(resumed.Id, Is.EqualTo(cancelled.Id));
            Assert.That(resumed.Sequence, Is.EqualTo(cancelled.Sequence));
            Assert.That(resumed.Type, Is.EqualTo(cancelled.Type));
            Assert.That(resumed.Resource, Is.EqualTo(cancelled.Resource));
            Assert.That(resumed.RuntimeRevision, Is.EqualTo(cancelled.RuntimeRevision));
            Assert.That(resumed.Data.GetRawText(), Is.EqualTo(cancelled.Data.GetRawText()));
        });
        resumeCancellation.Cancel();

        var store = new BuildEventStore(controller.Database);
        await store.PruneBeforeAsync(build.Id, cancelled.Sequence);
        using var expiredRequest = EventRequest(
            build.Id,
            $"&cursor={Uri.EscapeDataString(created.Id)}");
        using var expired = await http.SendAsync(expiredRequest);
        Assert.That(expired.StatusCode, Is.EqualTo(HttpStatusCode.Gone));
        var problem = await expired.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Multiple(() =>
        {
            Assert.That(problem.GetProperty("code").GetString(),
                Is.EqualTo("event_cursor_expired"));
            Assert.That(problem.GetProperty("detail").GetString(),
                Does.Contain($"/api/v1/builds/{build.Id}"));
        });
    }

    [Test]
    public async Task Every_visible_matrix_transition_appends_once_and_noops_append_nothing()
    {
        await using var controller = await BuildMutationRestHarness.StartAsync(rootDir);
        await BuildMutationRestHarness.RegisterAuthorizedAgentAsync(controller, "agent-ordering");
        using var http = BuildMutationRestHarness.CreateClient(
            controller, controller.Tokens.AdminToken);
        var stagingId = await BuildMutationRestHarness.CreateUploadedPlanAsync(
            http, "project-ordering", "plan-ordering-1");
        using var submitted = await BuildMutationRestHarness.SubmitAsync(
            http,
            BuildMutationRestHarness.CreateRequest(
                "project-ordering", "configuration-ordering", stagingId),
            "build-ordering-1");
        var matrix = await submitted.Content.ReadFromJsonAsync<BuildResource>()
            ?? throw new AssertionException("Build response was empty");
        var createdEtag = submitted.Headers.ETag?.Tag;
        var childId = matrix.Children.Single().Id;
        var now = matrix.CreatedAt;

        Assert.That(await controller.BuildQueueStore.TryClaimAsync(
            childId, "agent-ordering", now), Is.True);
        using var claimedResponse = await http.GetAsync($"/api/v1/builds/{matrix.Id}");
        var claimedEtag = claimedResponse.Headers.ETag?.Tag;
        Assert.That(await controller.BuildQueueStore.TryClaimAsync(
            childId, "agent-ordering", now), Is.False);
        Assert.That(await controller.BuildQueueStore.TryPrepareDispatchAsync(
            childId,
            "agent-ordering",
            "session-one",
            now,
            "Ordering agent",
            new Dictionary<string, string>(),
            new Dictionary<string, string>()), Is.True);
        using var runningResponse = await http.GetAsync($"/api/v1/builds/{matrix.Id}");
        var running = await runningResponse.Content.ReadFromJsonAsync<BuildResource>()
            ?? throw new AssertionException("running Build response was empty");
        Assert.That(await controller.BuildQueueStore.TryPrepareDispatchAsync(
            childId,
            "agent-ordering",
            "session-one",
            now), Is.False);
        Assert.That(await controller.BuildQueueStore.RecordDispatchAttemptAsync(
            childId,
            "agent-ordering",
            "session-one",
            "session-two",
            now), Is.True);
        Assert.That(await controller.BuildQueueStore.RecordDispatchAttemptAsync(
            childId,
            "agent-ordering",
            "session-one",
            "session-two",
            now), Is.False);
        Assert.That(await controller.BuildQueueStore.CompleteDispatchAsync(
            childId, "agent-ordering", "session-two"), Is.True);
        Assert.That(await controller.BuildQueueStore.CompleteDispatchAsync(
            childId, "agent-ordering", "session-two"), Is.False);
        Assert.That(await controller.BuildStore.TryArmReconnectGraceAsync(
            childId,
            "agent-ordering",
            "session-two",
            now.AddMinutes(1),
            now), Is.True);
        Assert.That(await controller.BuildStore.TryArmReconnectGraceAsync(
            childId,
            "agent-ordering",
            "session-two",
            now.AddMinutes(1),
            now), Is.False);

        var page = await new BuildEventStore(controller.Database).ReadAfterAsync(
            matrix.Id, afterEventId: null, BuildEventStore.MaximumBatchSize);
        Assert.Multiple(() =>
        {
            Assert.That(page.Items.Select(item => item.Type), Is.EqualTo(new[]
            {
                "build.created",
                "build.queue-claimed",
                "build.running",
                "build.owner-adopted",
                "build.dispatch-completed",
                "build.reconnect-grace-armed",
            }));
            Assert.That(page.Items.Select(item => item.Sequence), Is.Ordered.Ascending);
            Assert.That(
                page.Items.Select(item => item.Sequence).Distinct().Count(),
                Is.EqualTo(page.Items.Count));
            Assert.That(page.LatestSequence, Is.EqualTo(page.Items[^1].Sequence));
            Assert.That(
                page.Items.Select(item => item.RuntimeRevision),
                Is.EqualTo(page.Items.Select(item =>
                    BuildEventStore.RuntimeRevision(item.Sequence))));
            Assert.That(createdEtag, Is.Not.EqualTo(claimedEtag));
            Assert.That(claimedEtag, Is.Not.EqualTo(runningResponse.Headers.ETag?.Tag));
            Assert.That(running.RuntimeRevision, Is.EqualTo("runtime:3"));
        });
    }

    private static HttpRequestMessage EventRequest(string buildId, string suffix = "")
    {
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/v1/events?topic=build&resourceId={Uri.EscapeDataString(buildId)}{suffix}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        return request;
    }

    private static async Task<RestEventEnvelope> ReadEventAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken)
                ?? throw new EndOfStreamException("SSE stream ended before an event arrived");
            if (!line.StartsWith("data: ", StringComparison.Ordinal))
            {
                continue;
            }

            return JsonSerializer.Deserialize<RestEventEnvelope>(
                line[6..],
                new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new InvalidDataException("SSE event envelope was empty");
        }
    }
}
