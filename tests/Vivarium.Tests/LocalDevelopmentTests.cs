using Microsoft.Extensions.Configuration;
using Vivarium.Controller.Identity;

namespace Vivarium.Tests;

[NonParallelizable]
public sealed class LocalDevelopmentTests
{
    [Test]
    public void Opt_in_rejects_non_loopback_and_requires_explicit_development_environment()
    {
        var previousRoot = Environment.GetEnvironmentVariable("VIVARIUM_LOCAL_DEV_ROOT");
        var previousEnvironment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        var root = Path.Combine(Path.GetTempPath(), "vivarium-dev-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "owner.json"), "{}");
        try
        {
            var values = new Dictionary<string, string?> {
                ["Vivarium:PublicOrigin"] = "https://127.0.0.1:4001",
                ["Kestrel:Endpoints:Public:Url"] = "https://127.0.0.1:4002",
                ["Kestrel:Endpoints:Storage:Url"] = "http://127.0.0.1:4003"
            };
            IConfiguration Configuration() => new ConfigurationBuilder().AddInMemoryCollection(values).Build();
            Environment.SetEnvironmentVariable("VIVARIUM_LOCAL_DEV_ROOT", null);
            Assert.That(LocalDevelopment.Root(Configuration()), Is.Null);
            Environment.SetEnvironmentVariable("VIVARIUM_LOCAL_DEV_ROOT", root);
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Production");
            Assert.Throws<InvalidOperationException>(() => LocalDevelopment.Root(Configuration()));
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");
            Assert.That(LocalDevelopment.Root(Configuration()), Is.EqualTo(root));
            values["Vivarium:PublicOrigin"] = "http://127.0.0.1:4001";
            Assert.Throws<InvalidOperationException>(() => LocalDevelopment.Root(Configuration()));
            values["Vivarium:PublicOrigin"] = "https://127.0.0.1:4001";
            foreach (var key in values.Keys.ToArray())
            {
                var saved = values[key]; values[key] = "https://0.0.0.0:4001";
                Assert.Throws<InvalidOperationException>(() => LocalDevelopment.Root(Configuration()), key);
                values[key] = saved;
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("VIVARIUM_LOCAL_DEV_ROOT", previousRoot);
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", previousEnvironment);
            File.Delete(Path.Combine(root, "owner.json")); Directory.Delete(root);
        }
    }
}
