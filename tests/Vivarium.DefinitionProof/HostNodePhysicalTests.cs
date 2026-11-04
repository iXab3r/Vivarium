using System.Text.Json;
using System.Text.Json.Nodes;
using Chiral;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Vivarium.Controller.Hosting;

namespace Vivarium.DefinitionProof;

[NonParallelizable]
public sealed class HostNodePhysicalTests
{
    [Test]
    public async Task Database_setting_uses_Chiral_masked_save_and_saved_active_restart_boundary()
    {
        var directory = Path.Combine(TestContext.CurrentContext.WorkDirectory,
            "artifacts/r0-host/nodes-" + Guid.NewGuid().ToString("N"));
        HostApplicationBuilder Builder()
        {
            var builder = Host.CreateApplicationBuilder();
            builder.Logging.ClearProviders();
            builder.Services.AddVivariumNodes(builder.Configuration, directory);
            return builder;
        }
        const string first = "Host=127.0.0.1;Database=owned-first;Password=owned-synthetic-password";
        const string second = "Host=127.0.0.1;Database=owned-second;Password=owned-synthetic-replacement";
        var initial = Builder();
        using (var host = initial.Build())
        {
            await host.StartAsync();
            try
            {
                var settings = host.Services.GetRequiredService<NodeConfigurationService>();
                var saved = await settings.SaveAsync("vivarium-database", Guid.NewGuid(), true,
                    new JsonObject { ["ConnectionString"] = first }, NodeConfigActor.System, null);
                Assert.That(saved.StatusCode, Is.EqualTo(200));
                var view = (await settings.GetAsync("vivarium-database")).Value!;
                Assert.That(view.Config["ConnectionString"]!.GetValue<string>(), Is.EqualTo(NodeConfigValidation.SecretMask));
                Assert.That(initial.Configuration["ConnectionStrings:Vivarium"], Is.Null);
            }
            finally { await host.StopAsync(); }
        }
        var restarted = Builder();
        using (var host = restarted.Build())
        {
            await host.StartAsync();
            try
            {
                var settings = host.Services.GetRequiredService<NodeConfigurationService>();
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                while (settings.IsReady("vivarium-database") != true) await Task.Delay(20, deadline.Token);
                Assert.That(restarted.Configuration["ConnectionStrings:Vivarium"] == first, Is.True);
                var before = (await settings.GetAsync("vivarium-database")).Value!;
                var saved = await settings.SaveAsync("vivarium-database", Guid.NewGuid(), true,
                    new JsonObject { ["ConnectionString"] = second }, NodeConfigActor.System, before.SavedRevision!.Id);
                Assert.That(saved.StatusCode, Is.EqualTo(200));
                var after = (await settings.GetAsync("vivarium-database")).Value!;
                Assert.Multiple(() =>
                {
                    Assert.That(after.SavedRevision!.Id, Is.Not.EqualTo(before.SavedRevision.Id));
                    Assert.That(after.ActiveRevision!.Id, Is.EqualTo(before.ActiveRevision!.Id));
                    Assert.That(after.RestartRequired, Is.True);
                    Assert.That(restarted.Configuration["ConnectionStrings:Vivarium"] == first, Is.True);
                    Assert.That(JsonSerializer.Serialize(after).Contains("owned-synthetic-", StringComparison.Ordinal), Is.False);
                });
                var stale = await settings.SaveAsync("vivarium-database", Guid.NewGuid(), true,
                    new JsonObject { ["ConnectionString"] = first }, NodeConfigActor.System, before.SavedRevision.Id);
                Assert.That(stale.StatusCode, Is.EqualTo(409));
            }
            finally { await host.StopAsync(); }
        }
        var cold = Builder();
        using (var host = cold.Build())
        {
            await host.StartAsync();
            try
            {
                var settings = host.Services.GetRequiredService<NodeConfigurationService>();
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                while (settings.IsReady("vivarium-database") != true) await Task.Delay(20, deadline.Token);
                Assert.That(cold.Configuration["ConnectionStrings:Vivarium"] == second, Is.True);
                Assert.That((await settings.GetAsync("vivarium-database")).Value!.RestartRequired, Is.False);
            }
            finally { await host.StopAsync(); }
        }
    }
}
