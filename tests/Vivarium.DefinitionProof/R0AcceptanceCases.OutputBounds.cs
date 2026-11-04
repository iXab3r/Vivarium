using System.Net;
using System.Text.Json;

namespace Vivarium.DefinitionProof;

public sealed partial class R0AcceptanceCases
{
    [Test]
    public async Task Queued_and_empty_process_output_stays_empty_and_bounded_pages_retain_both_literal_streams()
    {
        await using var host = await installation.CreateCaseAsync(catalogued: true);
        using var administrator = await host.AdministratorAsync(); var csrf = await host.AntiforgeryAsync(administrator);
        using var actor = await RestrictedActorAsync(host, administrator, csrf);
        var emptyId = await SubmitAsync(actor.BuildClient, "P_Empty");
        var queued = await OutputAsync(actor.BuildClient, emptyId);
        Assert.That(queued.GetProperty("items").GetArrayLength(), Is.Zero);
        Assert.That(queued.GetProperty("output").GetProperty("state").GetString(), Is.EqualTo("open"));
        Assert.That(queued.GetProperty("page").GetProperty("limit").GetInt32(), Is.EqualTo(50));
        Assert.That(queued.GetProperty("page").GetProperty("nextCursor").ValueKind, Is.EqualTo(JsonValueKind.Null));
        foreach (var limit in new[] { "0", "201", "no", "1&limit=2" })
            await ExpectProblemAsync(actor.BuildClient, $"/api/v1/builds/{emptyId}/output?limit=" + limit, HttpStatusCode.BadRequest, "invalid_query");
        await host.StartWindowsAgentAsync(administrator, csrf); await FinishedAsync(actor.BuildClient, emptyId);
        var empty = await OutputAsync(actor.BuildClient, emptyId);
        Assert.That(empty.GetProperty("items").GetArrayLength(), Is.Zero);
        Assert.That(empty.GetProperty("output").GetProperty("state").GetString(), Is.EqualTo("complete"));
        var gate = Path.Combine(host.Root, "paging-gate"); Directory.CreateDirectory(gate);
        var id = await SubmitAsync(actor.BuildClient, "P_Bulk", new() { ["env.CASE_DIR"] = gate });
        var lengths = new Dictionary<string, long> { ["stdout"] = 0, ["stderr"] = 0 }; string? liveCursor = null;
        using (var bound = new CancellationTokenSource(TimeSpan.FromSeconds(100)))
            for (var portion = 0; portion < 128; portion++)
            {
                // Bound the producer independently of chunking: a write may span several frames.
                while (lengths.Values.Any(length => length < (portion + 1) * 128))
                {
                    var live = await OutputAsync(actor.BuildClient, id, liveCursor, bound.Token);
                    foreach (var item in live.GetProperty("items").EnumerateArray())
                    {
                        Assert.That(item.GetProperty("kind").GetString(), Is.EqualTo("data"));
                        lengths[item.GetProperty("stream").GetString()!] += item.GetProperty("content").GetProperty("length").GetInt64();
                    }
                    liveCursor = live.GetProperty("output").GetProperty("resumeCursor").GetString();
                    if (lengths.Values.Any(length => length < (portion + 1) * 128)) await Task.Delay(10, bound.Token);
                }
                await File.WriteAllTextAsync(Path.Combine(gate, "continue-" + portion), "continue", bound.Token);
            }
        await FinishedAsync(actor.BuildClient, id);
        var first = await OutputAsync(actor.BuildClient, id);
        Assert.That(first.GetProperty("output").GetProperty("state").GetString(), Is.EqualTo("complete"));
        Assert.That(first.GetProperty("items").GetArrayLength(), Is.EqualTo(50));
        Assert.That(first.GetProperty("page").GetProperty("nextCursor").ValueKind, Is.EqualTo(JsonValueKind.String));
        var ids = new HashSet<string>();
        var streams = new Dictionary<string, List<byte>> { ["stdout"] = [], ["stderr"] = [] };
        string? cursor = null; var pages = 0;
        do
        {
            Assert.That(++pages, Is.LessThanOrEqualTo(32), "The owned producer must not create an unbounded metadata reader.");
            using var document = JsonDocument.Parse(await actor.BuildClient.GetStringAsync($"/api/v1/builds/{id}/output?limit=200"
                + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor))));
            var page = document.RootElement;
            Assert.That(page.GetProperty("page").GetProperty("limit").GetInt32(), Is.EqualTo(200));
            Assert.That(page.GetProperty("items").GetArrayLength(), Is.LessThanOrEqualTo(200));
            if (pages == 1) Assert.That(page.GetProperty("items").GetArrayLength(), Is.EqualTo(200));
            await AppendAsync(actor.BuildClient, page, streams, ids);
            cursor = page.GetProperty("page").GetProperty("nextCursor").GetString();
            Assert.That(page.GetProperty("output").GetProperty("state").GetString(), Is.EqualTo("complete"));
        } while (cursor is not null);
        foreach (var (stream, expectedByte) in new[] { ("stdout", (byte)'A'), ("stderr", (byte)'B') })
        {
            Assert.That(streams[stream].Count, Is.EqualTo(128 * 128));
            Assert.That(streams[stream].All(value => value == expectedByte), Is.True, "A stream contains unexpected producer bytes.");
        }
        var sample = (await OutputAsync(actor.BuildClient, id)).GetProperty("items")[0].GetProperty("content").GetProperty("href").GetString()!;
        using var crossBuild = await actor.BuildClient.GetAsync(sample.Replace($"builds/{id}/", $"builds/{emptyId}/", StringComparison.Ordinal));
        Assert.That(crossBuild.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

}
