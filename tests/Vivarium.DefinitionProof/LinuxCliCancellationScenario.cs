using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Vivarium.DefinitionProof;

internal sealed class LinuxCliCancellationScenario(string root, Uri origin, string certificatePath)
{
    internal async Task RunAsync(HttpClient administrator, HttpClient ordinary, string csrf, string credential, UnchangedCliScenario cli, string windowsAgentId)
    {
        using var enrollmentRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/enrollment-tokens");
        enrollmentRequest.Headers.Add("RequestVerificationToken", csrf);
        using var enrollmentResponse = await administrator.SendAsync(enrollmentRequest);
        Assert.That(enrollmentResponse.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        using var enrollment = JsonDocument.Parse(await enrollmentResponse.Content.ReadAsStringAsync());
        await using var agent = new OwnedDockerAgent(root);
        await agent.StartAsync(origin, certificatePath, enrollment.RootElement.GetProperty("token").GetString()!);
        JsonElement linux = default;
        try
        {
            using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            while (linux.ValueKind == JsonValueKind.Undefined)
            {
                using var agents = JsonDocument.Parse(await administrator.GetStringAsync("/api/v1/agents", bound.Token));
                var found = agents.RootElement.GetProperty("items").EnumerateArray().FirstOrDefault(a =>
                    a.GetProperty("id").GetString() != windowsAgentId && a.GetProperty("connected").GetBoolean());
                if (found.ValueKind != JsonValueKind.Undefined) linux = found.Clone();
                if (linux.ValueKind == JsonValueKind.Undefined) await Task.Delay(100, bound.Token);
            }
        }
        catch
        {
            TestContext.Progress.WriteLine(await agent.DiagnosticsAsync(enrollment.RootElement.GetProperty("token").GetString()!));
            throw;
        }
        var agentId = linux.GetProperty("id").GetString()!;
        var teamCityId = linux.GetProperty("teamCityId").GetInt32();
        await PutAsync(administrator, csrf, "/api/v1/agents/" + agentId + "/authorization", "{\"authorized\":true}", HttpStatusCode.NoContent);
        var enabledPath = $"/app/rest/agents/id:{teamCityId}/enabledInfo?fields=status,comment(text,timestamp)";
        await PutAsync(administrator, csrf, enabledPath, "{\"status\":true,\"comment\":{\"text\":\"Owned Linux CLI cancellation\"}}", HttpStatusCode.OK);
        var run = await cli.RunAsync(credential, 0, "-p", "owned-r0", "jobs", "run", "P_LocalSmoke", "--param", "env.MODE=long");
        var id = run.GetProperty("build").GetProperty("id").GetInt64();
        using (var bound = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
        {
            var started = false;
            while (!started)
            {
                using var output = JsonDocument.Parse(await ordinary.GetStringAsync($"/api/v1/builds/{id}/output", bound.Token));
                foreach (var entry in output.RootElement.GetProperty("items").EnumerateArray().Where(e => e.GetProperty("kind").GetString() == "data"))
                    started |= (await ordinary.GetStringAsync(entry.GetProperty("content").GetProperty("href").GetString(), bound.Token)).Contains("owned-running-start", StringComparison.Ordinal);
                if (!started) await Task.Delay(100, bound.Token);
            }
        }
        var cancelled = await cli.RunAsync(credential, 0, "-p", "owned-r0", "builds", "cancel", id.ToString(CultureInfo.InvariantCulture));
        Assert.That(cancelled.GetProperty("status").GetString(), Is.EqualTo("UNKNOWN"));
        Assert.That(cancelled.GetProperty("state").GetString(), Is.EqualTo("running"));
        await agent.ReleaseCancellationAsync();
        var stopped = await cli.RunAsync(credential, 1, "-p", "owned-r0", "builds", "wait", id.ToString(CultureInfo.InvariantCulture), "--timeout", "2m", "--interval", "1s");
        Assert.That(stopped.GetProperty("outcome").GetString(), Is.EqualTo("canceled"));
        Assert.That(stopped.GetProperty("build").GetProperty("state").GetString(), Is.EqualTo("finished"));
        Assert.That(stopped.GetProperty("build").GetProperty("agent").GetProperty("id").GetInt32(), Is.EqualTo(teamCityId));
        // Reuse the same real Linux Agent without an administrator force stop or process substitute.
        var next = await cli.RunAsync(credential, 0, "-p", "owned-r0", "jobs", "run", "P_LocalSmoke", "--wait", "--timeout", "2m", "--interval", "1s");
        Assert.That(next.GetProperty("outcome").GetString(), Is.EqualTo("succeeded"));
        Assert.That(next.GetProperty("build").GetProperty("agent").GetProperty("id").GetInt32(), Is.EqualTo(teamCityId));
        await PutAsync(administrator, csrf, enabledPath, "{\"status\":false,\"comment\":{\"text\":\"Owned Linux CLI cancellation complete\"}}", HttpStatusCode.OK);
    }

    private static async Task PutAsync(HttpClient client, string csrf, string path, string body, HttpStatusCode status)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("RequestVerificationToken", csrf);
        using var response = await client.SendAsync(request);
        Assert.That(response.StatusCode, Is.EqualTo(status), path);
    }
}
