using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vivarium.Controller.Persistence;

namespace Vivarium.DefinitionProof;

/// <summary>A bounded owned process gate; no product test route or authentication replacement.</summary>
internal sealed class RuntimeSecretScenario(string root)
{
    private readonly string secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private long buildId;
    private string? cursor;
    private string? agentId;
    private string? workload;
    private long lease;
    private long generation;

    internal string Script
    {
        get
        {
            var entered = Path.Combine(root, "secret-entered");
            var release = Path.Combine(root, "secret-release");
            if (!OperatingSystem.IsWindows())
                return $$"""
                    s="$US013_SECRET"
                    printf '%04096d' 0
                    printf '%s' "$s" | cut -c1-32 | tr -d '\n'
                    : > '{{entered}}'
                    n=0; while [ ! -f '{{release}}' ]; do n=$((n+1)); [ "$n" -lt 60 ] || exit 11; sleep 1; done
                    printf '%s' "$s" | cut -c33- | tr -d '\n'
                    printf ':done\n'
                    printf '%s\n' "$s" >&2
                    """;
            var script = $$"""
                $ProgressPreference='SilentlyContinue'
                $s=$env:US013_SECRET
                [Console]::Out.Write(('0'*4096)+$s.Substring(0,32)); [Console]::Out.Flush()
                [IO.File]::WriteAllText('{{entered.Replace("'", "''")}}','entered')
                $deadline=[DateTime]::UtcNow.AddSeconds(60)
                while(-not [IO.File]::Exists('{{release.Replace("'", "''")}}')) {
                    if([DateTime]::UtcNow -ge $deadline) { exit 11 }; Start-Sleep -Milliseconds 100
                }
                [Console]::Out.Write($s.Substring(32)+':done'+[char]10)
                [Console]::Error.Write($s+[char]10)
                """;
            return "powershell.exe -NoLogo -NoProfile -NonInteractive -InputFormat Text -OutputFormat Text -EncodedCommand "
                + Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        }
    }

    internal async Task BeforeRestartAsync(HttpClient api, IServiceProvider services)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/builds")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { configurationId = "P_Secret",
                secretEnvironment = new Dictionary<string, string> { ["US013_SECRET"] = secret } }), Encoding.UTF8, "application/json")
        };
        using var submitted = await api.SendAsync(request);
        Assert.That(submitted.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        var admission = await submitted.Content.ReadAsStringAsync(); AssertPrivate(admission);
        using (var receipt = JsonDocument.Parse(admission)) buildId = receipt.RootElement.GetProperty("id").GetInt64();
        var until = DateTimeOffset.UtcNow.AddSeconds(25);
        JsonElement? observed = null;
        while (DateTimeOffset.UtcNow < until)
        {
            using var output = JsonDocument.Parse(await api.GetStringAsync($"/api/v1/builds/{buildId}/output"));
            if (File.Exists(Path.Combine(root, "secret-entered")) && output.RootElement.GetProperty("items").GetArrayLength() > 0)
            { observed = output.RootElement.Clone(); break; }
            await Task.Delay(100);
        }
        Assert.That(observed.HasValue, Is.True, "The gated process must publish a real live prefix before Host restart.");
        cursor = observed!.Value.GetProperty("output").GetProperty("resumeCursor").GetString();
        var live = await ReadBytesAsync(api, observed.Value, "stdout");
        Assert.That(live.Length, Is.Positive);
        Assert.That(live.All(b => b == (byte)'0'), Is.True, "A partial secret must remain in the Agent masker.");
        await using var database = await services.GetRequiredService<IDbContextFactory<VivariumDbContext>>().CreateDbContextAsync();
        var input = await database.RuntimeBuildInputs.AsNoTracking().SingleAsync(i => i.BuildId == buildId);
        var execution = await database.RuntimeBuildExecutions.AsNoTracking().SingleAsync(e => e.BuildId == buildId);
        Assert.That(input.ProtectedEnvironment.Length, Is.Positive);
        Assert.That(execution.AssignmentProtectionVersion, Is.EqualTo(1));
        AssertPrivate(input.ProtectedEnvironment); AssertPrivate(execution.Assignment);
        AssertPrivate(JsonSerializer.Serialize(input)); AssertPrivate(JsonSerializer.Serialize(execution));
        Assert.That(execution.AcceptedAt, Is.Not.Null);
        agentId = execution.AgentId; workload = execution.WorkloadId; lease = execution.LeaseFence;
        generation = execution.CurrentConnectionGeneration;
    }

    internal async Task AfterRestartAsync(HttpClient api, IServiceProvider services)
    {
        // The same process remains gated. Reconnect/adoption happens before releasing its second
        // half, so a terminal result cannot race ahead and stand in for active-work recovery.
        var until = DateTimeOffset.UtcNow.AddSeconds(25);
        var adopted = false;
        while (DateTimeOffset.UtcNow < until)
        {
            await using var state = await services.GetRequiredService<IDbContextFactory<VivariumDbContext>>().CreateDbContextAsync();
            var execution = await state.RuntimeBuildExecutions.AsNoTracking().SingleAsync(e => e.BuildId == buildId);
            if (execution.CurrentConnectionGeneration > generation && execution.TerminalReceivedAt is null)
            { adopted = true; break; }
            await Task.Delay(100);
        }
        Assert.That(adopted, Is.True, "A newer authenticated connection must adopt the still-running owned workload.");
        using (var resumed = await api.GetAsync($"/api/v1/builds/{buildId}/output?cursor=" + Uri.EscapeDataString(cursor!)))
            Assert.That(resumed.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        await File.WriteAllTextAsync(Path.Combine(root, "secret-release"), "release");
        until = DateTimeOffset.UtcNow.AddSeconds(25);
        var finished = false;
        while (DateTimeOffset.UtcNow < until)
        {
            var response = await api.GetStringAsync($"/api/v1/builds/{buildId}"); AssertPrivate(response);
            using var build = JsonDocument.Parse(response);
            if (build.RootElement.GetProperty("state").GetString() == "finished")
            { Assert.That(build.RootElement.GetProperty("status").GetString(), Is.EqualTo("SUCCESS")); finished = true; break; }
            await Task.Delay(100);
        }
        Assert.That(finished, Is.True, "The original workload must finish after reconnect without being rerun.");
        var metadata = await api.GetStringAsync($"/api/v1/builds/{buildId}/output"); AssertPrivate(metadata);
        using var output = JsonDocument.Parse(metadata);
        Assert.That(output.RootElement.GetProperty("output").GetProperty("state").GetString(), Is.EqualTo("complete"));
        Assert.That(Encoding.UTF8.GetString(await ReadBytesAsync(api, output.RootElement, "stdout")),
            Is.EqualTo(new string('0', 4096) + "[REDACTED]:done\n"));
        Assert.That(Encoding.UTF8.GetString(await ReadBytesAsync(api, output.RootElement, "stderr")), Is.EqualTo("[REDACTED]\n"));
        await using var database = await services.GetRequiredService<IDbContextFactory<VivariumDbContext>>().CreateDbContextAsync();
        var current = await database.RuntimeBuildExecutions.AsNoTracking().SingleAsync(e => e.BuildId == buildId);
        Assert.That(current.AgentId, Is.EqualTo(agentId)); Assert.That(current.WorkloadId, Is.EqualTo(workload));
        Assert.That(current.LeaseFence, Is.EqualTo(lease));
        Assert.That(await database.RuntimeBuildResults.CountAsync(r => r.BuildId == buildId), Is.EqualTo(1));
        AssertPrivate(JsonSerializer.Serialize(await database.AuditEvents.AsNoTracking().Where(e => e.TargetType == "build"
            && e.TargetId == buildId.ToString()).ToArrayAsync()));
    }

    internal void AssertPrivate(string text) => Assert.That(text.Contains(secret, StringComparison.Ordinal), Is.False,
        "Registered runtime material must not appear in public or retained plaintext.");
    private void AssertPrivate(byte[] bytes) => Assert.That(bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(secret)), Is.EqualTo(-1),
        "Registered runtime material must not appear in retained plaintext.");
    internal void AssertOwnedFilesPrivate()
    {
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            if (new FileInfo(path).Length <= 8 * 1024 * 1024) AssertPrivate(File.ReadAllBytes(path));
    }
    private async Task<byte[]> ReadBytesAsync(HttpClient api, JsonElement page, string stream)
    {
        using var bytes = new MemoryStream();
        foreach (var entry in page.GetProperty("items").EnumerateArray().Where(e => e.GetProperty("stream").GetString() == stream))
        {
            Assert.That(entry.GetProperty("kind").GetString(), Is.EqualTo("data"));
            var content = await api.GetByteArrayAsync(entry.GetProperty("content").GetProperty("href").GetString());
            AssertPrivate(content); bytes.Write(content);
        }
        return bytes.ToArray();
    }
}
