using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vivarium.Agent;
using Vivarium.Agent.Facts;
using Vivarium.Controller;
using Vivarium.Controller.Agents;
using Vivarium.Controller.Deployment;

namespace Vivarium.Tests;

[TestFixture]
public sealed class AgentDeploymentTests
{
    private string rootDir = null!;

    [SetUp]
    public void SetUp()
    {
        rootDir = Path.Combine(Path.GetTempPath(), "vivarium-deployment-tests", Guid.NewGuid().ToString("N"));
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
            // A failed assertion is more useful than teardown noise from a briefly-held process file.
        }
    }

    [Test]
    public async Task Package_publication_is_bounded_content_addressed_and_principal_idempotent()
    {
        await using var controller = await StartControllerAsync();
        var rid = CurrentRid();
        var package = CreatePackage(rid, "first-package");
        var digest = Digest(package);

        using (var anonymous = PinnedClient(controller))
        using (var request = PackageRequest(rid, "2.0.0", "publish-one", digest, package))
        using (var response = await anonymous.SendAsync(request))
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        }

        using var http = PinnedClient(controller);
        http.DefaultRequestHeaders.Authorization = new("Bearer", controller.Tokens.AdminToken);
        using var first = await http.SendAsync(
            PackageRequest(rid, "2.0.0", "publish-one", digest, package));
        var firstText = await first.Content.ReadAsStringAsync();
        Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.Created), firstText);
        using var firstBody = JsonDocument.Parse(firstText);
        var packageId = firstBody.RootElement.GetProperty("packageId").GetString();

        using var replay = await http.SendAsync(
            PackageRequest(rid, "2.0.0", "publish-one", digest, package));
        Assert.That(replay.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var changed = CreatePackage(rid, "different-package");
        using var conflict = await http.SendAsync(
            PackageRequest(rid, "2.0.0", "publish-one", Digest(changed), changed));
        Assert.That(conflict.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));

        using var listed = await http.GetAsync("/api/v1/agent-packages");
        using var listBody = JsonDocument.Parse(await listed.Content.ReadAsStreamAsync());
        var storedPackage = (await controller.AgentPackages.ListAsync()).Single();
        Assert.Multiple(() =>
        {
            Assert.That(listed.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(packageId, Has.Length.EqualTo(32));
            Assert.That(firstBody.RootElement.GetProperty("sha256").GetString(), Is.EqualTo(digest));
            Assert.That(firstBody.RootElement.GetProperty("rid").GetString(), Is.EqualTo(rid));
            Assert.That(listBody.RootElement.GetProperty("items").GetArrayLength(), Is.EqualTo(1));
            Assert.That(controller.AgentPackages.ResolveContentPath(
                storedPackage), Is.Not.Null);
        });

        var contentPath = Path.Combine(rootDir, "controller", "agent-packages", $"{digest}.zip");
        var corrupted = await File.ReadAllBytesAsync(contentPath);
        corrupted[^1] ^= 0x5a;
        await File.WriteAllBytesAsync(contentPath, corrupted);
        Assert.That(controller.AgentPackages.ResolveContentPath(storedPackage), Is.Null);
        using var repaired = await http.SendAsync(
            PackageRequest(rid, "2.0.0", "publish-repair", digest, package));
        Assert.Multiple(() =>
        {
            Assert.That(repaired.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(controller.AgentPackages.ResolveContentPath(storedPackage), Is.Not.Null);
        });
    }

    [Test]
    public async Task Package_publication_rejects_nonportable_special_and_extreme_archives()
    {
        await using var controller = await StartControllerAsync();
        using var http = PinnedClient(controller);
        http.DefaultRequestHeaders.Authorization = new("Bearer", controller.Tokens.AdminToken);
        var rid = CurrentRid();
        var executableName = rid == "win-x64" ? "vivarium-agent.exe" : "vivarium-agent";

        static byte[] Archive(params (string Path, byte[] Content, int Attributes)[] entries)
        {
            using var result = new MemoryStream();
            using (var archive = new ZipArchive(result, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var value in entries)
                {
                    var entry = archive.CreateEntry(value.Path, CompressionLevel.SmallestSize);
                    entry.ExternalAttributes = value.Attributes;
                    using var output = entry.Open();
                    output.Write(value.Content);
                }
            }
            return result.ToArray();
        }

        var cases = new[]
        {
            Archive((executableName, "ok"u8.ToArray(), 0), ("CON", "x"u8.ToArray(), 0)),
            Archive((executableName, "ok"u8.ToArray(), 0), ("a//b", "x"u8.ToArray(), 0)),
            Archive((executableName, "ok"u8.ToArray(), 0),
                ("link", "target"u8.ToArray(), (0xA000 | 0x1FF) << 16)),
            Archive((executableName, new byte[2 * 1024 * 1024], 0)),
        };
        for (var index = 0; index < cases.Length; index++)
        {
            using var response = await http.SendAsync(PackageRequest(
                rid,
                $"invalid-{index}",
                $"invalid-package-{index}",
                Digest(cases[index]),
                cases[index]));
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.UnprocessableEntity),
                $"invalid archive case {index} was accepted");
        }
        Assert.That(await controller.AgentPackages.ListAsync(), Is.Empty);
    }

    [Test]
    public async Task Package_publication_surface_is_absent_in_production_mode()
    {
        await using var controller = await StartControllerAsync(developmentPackageApi: false);
        var package = CreatePackage(CurrentRid(), "development-only-package");
        using var http = PinnedClient(controller);
        http.DefaultRequestHeaders.Authorization = new("Bearer", controller.Tokens.AdminToken);
        using var response = await http.SendAsync(PackageRequest(
            CurrentRid(), "2.0.0", "development-only", Digest(package), package));

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(controller.AgentPackages.ListAsync().Result, Is.Empty);
        });
    }

    [Test]
    public void Bundled_catalog_rejects_missing_rids_and_a_different_server_version()
    {
        var incomplete = CreateReleaseCatalogAsync(
            AgentHubService.ServerVersion,
            [CurrentRid()]).GetAwaiter().GetResult();
        var mismatched = CreateReleaseCatalogAsync("different-server-version")
            .GetAwaiter().GetResult();

        var incompleteError = Assert.ThrowsAsync<AgentPackageException>(async () =>
            await StartControllerAsync(incomplete, developmentPackageApi: false));
        var versionError = Assert.ThrowsAsync<AgentPackageException>(async () =>
            await StartControllerAsync(mismatched, developmentPackageApi: false));

        Assert.Multiple(() =>
        {
            Assert.That(incompleteError!.Code, Is.EqualTo("agent_package_catalog_incomplete"));
            Assert.That(versionError!.Code, Is.EqualTo("agent_package_catalog_version_mismatch"));
        });
    }

    [Test]
    public async Task Bundled_catalog_is_complete_version_bound_and_idempotent_across_restarts()
    {
        var rid = CurrentRid();
        var catalogPath = await CreateReleaseCatalogAsync(AgentHubService.ServerVersion);

        await using (var first = await StartControllerAsync(catalogPath))
        {
            var imported = (await first.AgentPackages.ListAsync()).Single(item => item.Rid == rid);
            Assert.Multiple(() =>
            {
                Assert.That(imported.Version, Is.EqualTo(AgentHubService.ServerVersion));
                Assert.That(imported.Rid, Is.EqualTo(rid));
                Assert.That(imported.Source, Is.EqualTo("bundled"));
                Assert.That(first.AgentPackages.ResolveContentPath(imported), Is.Not.Null);
                Assert.That(first.AgentPackages.FindCurrentRelease(rid)?.PackageId,
                    Is.EqualTo(imported.PackageId));
            });
        }

        await using var restarted = await StartControllerAsync(catalogPath);
        Assert.That(await restarted.AgentPackages.ListAsync(),
            Has.Count.EqualTo(AgentPackageRids.Supported.Count));
    }

    [Test]
    public async Task Upgrade_without_the_running_server_release_package_fails_before_drain()
    {
        await using var controller = await StartControllerAsync(developmentPackageApi: false);
        const string agentId = "release-package-missing-agent";
        var platform = ManifestPlatform();
        var hello = new Vivarium.Contracts.V1.Hello
        {
            AgentId = agentId,
            EnrollToken = await controller.Tokens.CreateEnrollTokenAsync(),
            SessionId = "release-package-missing-session",
            AgentVersion = "0.0.1",
            AgentPackageSha256 = new string('a', 64),
            Os = new Vivarium.Contracts.V1.OsInfo
            {
                Family = platform.Os,
                Arch = platform.Arch,
                Version = "test",
            },
        };
        hello.Parameters["hostname"] = agentId;
        Assert.That(await controller.Tokens.AdmitAgentAsync(hello), Is.Not.Null);
        await controller.AgentStore.ObserveHelloAsync(hello);
        await controller.AuthorizeAgentAsync(agentId);

        using var http = PinnedClient(controller);
        http.DefaultRequestHeaders.Authorization = new("Bearer", controller.Tokens.AdminToken);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/agents/{agentId}/upgrade-operations")
        {
            Content = JsonContent.Create(new
            {
                reason = "must resolve current Server release",
                timeoutSeconds = 120,
            }),
        };
        request.Headers.Add("Idempotency-Key", "missing-server-release-package");
        using var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable), text);
            Assert.That(text, Does.Contain("server_agent_release_unavailable"));
            Assert.That(controller.AgentUpgradeStore.IsDrainedAsync(agentId).Result, Is.False);
        });
    }

    [Test]
    public async Task Active_upgrade_and_maintenance_drain_survive_controller_restart()
    {
        var rid = CurrentRid();
        var agentId = "restart-safe-agent";
        string operationId;
        string packageId;
        await using (var first = await StartControllerAsync())
        {
            var priorPackageBytes = CreatePackage(rid, "restart-prior-package");
            var hello = new Vivarium.Contracts.V1.Hello
            {
                AgentId = agentId,
                EnrollToken = await first.Tokens.CreateEnrollTokenAsync(),
                SessionId = "seed-session",
                AgentVersion = "1.0.0",
                AgentPackageSha256 = Digest(priorPackageBytes),
                Os = new Vivarium.Contracts.V1.OsInfo
                {
                    Family = ManifestPlatform().Os,
                    Arch = ManifestPlatform().Arch,
                    Version = "test",
                },
            };
            hello.Parameters["hostname"] = agentId;
            var admission = await first.Tokens.AdmitAgentAsync(hello);
            Assert.That(admission, Is.Not.Null);
            await first.AgentStore.ObserveHelloAsync(hello);
            var generations = await first.AgentStore.AcceptSessionAsync(
                agentId,
                admission!.CredentialGeneration);
            await first.AgentStore.ObserveCapabilitiesAsync(
                agentId,
                generations.CredentialGeneration,
                generations.ConnectionGeneration,
                [new AgentCapabilitySupport("vivarium.bootstrap-supervisor.v1", 1)]);
            await first.AgentStore.ObserveStaticFactsAsync(new AgentStaticObservation(
                agentId,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                AgentFactCollectorOutcome.Succeeded,
                Complete: true,
                Issues: [],
                Capabilities:
                [
                    new AgentCapabilitySupport("vivarium.bootstrap-supervisor.v1", 1),
                ],
                Facts: new AgentStaticFacts(
                    agentId,
                    ManifestPlatform().Os,
                    "test-os",
                    "1",
                    "test",
                    "test",
                    ManifestPlatform().Arch,
                    ManifestPlatform().Arch,
                    "1.0.0",
                    "1.0.0",
                    "test",
                    Interactive: false,
                    Extensions: new Dictionary<string, string>()),
                CredentialGeneration: generations.CredentialGeneration,
                ConnectionGeneration: generations.ConnectionGeneration,
                PackageDigestSha256: Digest(priorPackageBytes)));
            await first.AuthorizeAgentAsync(agentId);

            var packageBytes = CreatePackage(rid, "restart-target-package");
            using var admin = PinnedClient(first);
            admin.DefaultRequestHeaders.Authorization = new("Bearer", first.Tokens.AdminToken);
            using var priorPublished = await admin.SendAsync(PackageRequest(
                rid, "1.0.0", "restart-prior-package", Digest(priorPackageBytes), priorPackageBytes));
            Assert.That(priorPublished.StatusCode, Is.EqualTo(HttpStatusCode.Created));
            using var published = await admin.SendAsync(PackageRequest(
                rid, "4.0.0", "restart-package", Digest(packageBytes), packageBytes));
            using var publishedBody = JsonDocument.Parse(
                await published.Content.ReadAsStreamAsync());
            packageId = publishedBody.RootElement.GetProperty("packageId").GetString()!;

            using var create = new HttpRequestMessage(
                HttpMethod.Post,
                $"/api/v1/agents/{agentId}/upgrade-operations")
            {
                Content = JsonContent.Create(new
                {
                    reason = "restart recovery evidence",
                    timeoutSeconds = 120,
                }),
            };
            create.Headers.Add("Idempotency-Key", "restart-operation");
            using var created = await admin.SendAsync(create);
            var createdText = await created.Content.ReadAsStringAsync();
            Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.Accepted), createdText);
            using var createdBody = JsonDocument.Parse(createdText);
            operationId = createdBody.RootElement.GetProperty("operationId").GetString()!;
            Assert.That(await first.AgentUpgradeStore.IsDrainedAsync(agentId), Is.True);
        }

        await using var restarted = await StartControllerAsync();
        var recovered = await restarted.AgentUpgrades.FindAsync(operationId);
        var recoveredDrain = await restarted.AgentUpgradeStore.IsDrainedAsync(agentId);
        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.Not.Null);
            Assert.That(recovered!.State, Is.EqualTo(AgentUpgradeState.Draining));
            Assert.That(recovered.Package.PackageId, Is.EqualTo(packageId));
            Assert.That(recoveredDrain, Is.True);
            Assert.That(restarted.AgentPackages.ResolveContentPath(recovered.Package), Is.Not.Null);
        });
    }

    [Test]
    public async Task Upgrade_is_rejected_when_an_exact_rollback_digest_is_unavailable()
    {
        await using var controller = await StartControllerAsync();
        const string agentId = "digestless-agent";
        var platform = ManifestPlatform();
        var hello = new Vivarium.Contracts.V1.Hello
        {
            AgentId = agentId,
            EnrollToken = await controller.Tokens.CreateEnrollTokenAsync(),
            SessionId = "digestless-session",
            AgentVersion = "legacy",
            Os = new Vivarium.Contracts.V1.OsInfo
            {
                Family = platform.Os,
                Arch = platform.Arch,
                Version = "test",
            },
        };
        hello.Parameters["hostname"] = agentId;
        Assert.That(await controller.Tokens.AdmitAgentAsync(hello), Is.Not.Null);
        await controller.AgentStore.ObserveHelloAsync(hello);
        await controller.AuthorizeAgentAsync(agentId);

        var package = CreatePackage(CurrentRid(), "digest-required");
        using var admin = PinnedClient(controller);
        admin.DefaultRequestHeaders.Authorization = new("Bearer", controller.Tokens.AdminToken);
        using var published = await admin.SendAsync(PackageRequest(
            CurrentRid(), "4.1.0", "digest-required-package", Digest(package), package));
        using var packageBody = JsonDocument.Parse(await published.Content.ReadAsStreamAsync());
        var packageId = packageBody.RootElement.GetProperty("packageId").GetString()!;
        using var create = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/agents/{agentId}/upgrade-operations")
        {
            Content = JsonContent.Create(new
            {
                reason = "must retain exact rollback",
                timeoutSeconds = 120,
            }),
        };
        create.Headers.Add("Idempotency-Key", "digest-required-operation");
        using var response = await admin.SendAsync(create);
        var text = await response.Content.ReadAsStringAsync();
        var drained = await controller.AgentUpgradeStore.IsDrainedAsync(agentId);
        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.UnprocessableEntity), text);
            Assert.That(text, Does.Contain("agent_prior_package_unknown"));
            Assert.That(drained, Is.False);
        });
    }

    [Test]
    public async Task Upgrade_is_rejected_without_a_live_bootstrap_supervisor_capability()
    {
        await using var controller = await StartControllerAsync();
        var dataDir = Path.Combine(rootDir, "unsupervised-agent");
        var agent = CreateAgent(
            controller,
            dataDir,
            await controller.Tokens.CreateEnrollTokenAsync(),
            "1.0.0",
            new string('a', 64),
            supervised: false);
        using var lifetime = new CancellationTokenSource();
        var agentTask = agent.RunAsync(lifetime.Token);
        try
        {
            await WaitForAsync(
                () => controller.Registry.Get(agent.AgentId)?.Reconciled == true,
                TimeSpan.FromSeconds(20));
            await controller.AuthorizeAgentAsync(agent.AgentId);
            await agent.WaitAuthorizedAsync(TimeSpan.FromSeconds(20));

            var package = CreatePackage(CurrentRid(), "supervisor-required");
            using var admin = PinnedClient(controller);
            admin.DefaultRequestHeaders.Authorization = new("Bearer", controller.Tokens.AdminToken);
            using var published = await admin.SendAsync(PackageRequest(
                CurrentRid(), "4.2.0", "supervisor-required-package", Digest(package), package));
            using var body = JsonDocument.Parse(await published.Content.ReadAsStreamAsync());
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"/api/v1/agents/{agent.AgentId}/upgrade-operations")
            {
                Content = JsonContent.Create(new
                {
                    reason = "must have supervisor",
                    timeoutSeconds = 120,
                }),
            };
            request.Headers.Add("Idempotency-Key", "supervisor-required-operation");
            using var response = await admin.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();

            Assert.Multiple(() =>
            {
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.UnprocessableEntity), text);
                Assert.That(text, Does.Contain("agent_bootstrap_supervisor_required"));
                Assert.That(controller.AgentUpgradeStore.IsDrainedAsync(agent.AgentId).Result, Is.False);
            });
        }
        finally
        {
            lifetime.Cancel();
            await IgnoreCancellationAsync(agentTask);
        }
    }

    [Test]
    public async Task Expired_confirmation_manifest_never_authorizes_candidate_activation()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        await using var controller = await StartControllerAsync(timeProvider: time);
        var dataDir = Path.Combine(rootDir, "deadline-agent");
        var priorDigest = new string('b', 64);
        var agent = CreateAgent(
            controller,
            dataDir,
            await controller.Tokens.CreateEnrollTokenAsync(),
            "1.0.0",
            priorDigest);
        using var lifetime = new CancellationTokenSource();
        var agentTask = agent.RunAsync(lifetime.Token);
        try
        {
            await WaitForAsync(
                () => controller.Registry.Get(agent.AgentId)?.Reconciled == true,
                TimeSpan.FromSeconds(20));
            await controller.AuthorizeAgentAsync(agent.AgentId);
            await agent.WaitAuthorizedAsync(TimeSpan.FromSeconds(20));

            var package = CreatePackage(CurrentRid(), "deadline-target");
            using var admin = PinnedClient(controller);
            admin.DefaultRequestHeaders.Authorization = new("Bearer", controller.Tokens.AdminToken);
            using var published = await admin.SendAsync(PackageRequest(
                CurrentRid(), "4.3.0", "deadline-package", Digest(package), package));
            using var publishedBody = JsonDocument.Parse(await published.Content.ReadAsStreamAsync());
            using var create = new HttpRequestMessage(
                HttpMethod.Post,
                $"/api/v1/agents/{agent.AgentId}/upgrade-operations")
            {
                Content = JsonContent.Create(new
                {
                    reason = "deadline confirmation",
                    timeoutSeconds = 120,
                }),
            };
            create.Headers.Add("Idempotency-Key", "deadline-operation");
            using var created = await admin.SendAsync(create);
            Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
            await agentTask.WaitAsync(TimeSpan.FromSeconds(20));

            time.Advance(TimeSpan.FromMinutes(3));
            using var bootstrap = PinnedClient(controller);
            bootstrap.DefaultRequestHeaders.Authorization = new(
                "Bearer", File.ReadAllText(Path.Combine(dataDir, "auth.token")).Trim());
            var platform = ManifestPlatform();
            using var manifest = await bootstrap.GetAsync(
                $"/bootstrap/manifest?os={platform.Os}&arch={platform.Arch}");
            var text = await manifest.Content.ReadAsStringAsync();
            Assert.That(manifest.StatusCode, Is.EqualTo(HttpStatusCode.OK), text);
            using var body = JsonDocument.Parse(text);
            Assert.That(body.RootElement.GetProperty("action").GetString(), Is.EqualTo("rollback"));
        }
        finally
        {
            lifetime.Cancel();
            await IgnoreCancellationAsync(agentTask);
        }
    }

    [Test]
    public async Task Busy_agent_drains_then_restarts_and_exact_new_digest_completes_without_blocking_peer()
    {
        await using var controller = await StartControllerAsync();
        var firstData = Path.Combine(rootDir, "agent-one");
        var secondData = Path.Combine(rootDir, "agent-two");
        var first = CreateAgent(
            controller,
            firstData,
            await controller.Tokens.CreateEnrollTokenAsync(),
            "1.0.0",
            new string('a', 64));
        var second = CreateAgent(
            controller,
            secondData,
            await controller.Tokens.CreateEnrollTokenAsync(),
            "1.0.0",
            new string('b', 64));
        using var firstLifetime = new CancellationTokenSource();
        using var secondLifetime = new CancellationTokenSource();
        var firstTask = first.RunAsync(firstLifetime.Token);
        var secondTask = second.RunAsync(secondLifetime.Token);
        AgentRunner? replacement = null;
        Task? replacementTask = null;
        using var replacementLifetime = new CancellationTokenSource();
        try
        {
            await WaitForAsync(
                () => controller.Registry.All.Count(agent => agent.Connected && agent.Reconciled) == 2,
                TimeSpan.FromSeconds(20));
            await controller.AuthorizeAgentAsync(first.AgentId);
            await controller.AuthorizeAgentAsync(second.AgentId);
            await Task.WhenAll(
                first.WaitAuthorizedAsync(TimeSpan.FromSeconds(20)),
                second.WaitAuthorizedAsync(TimeSpan.FromSeconds(20)));

            var rid = CurrentRid();
            var packageBytes = CreatePackage(rid, "target-package");
            var targetDigest = Digest(packageBytes);
            using var admin = PinnedClient(controller);
            admin.DefaultRequestHeaders.Authorization = new("Bearer", controller.Tokens.AdminToken);
            using var published = await admin.SendAsync(
                PackageRequest(rid, "2.0.0", "target-publish", targetDigest, packageBytes));
            var publishedText = await published.Content.ReadAsStringAsync();
            Assert.That(published.StatusCode, Is.EqualTo(HttpStatusCode.Created), publishedText);
            using var publishedBody = JsonDocument.Parse(publishedText);
            var packageId = publishedBody.RootElement.GetProperty("packageId").GetString()!;

            Assert.That(controller.Registry.TryBeginBuild(first.AgentId, "busy-build", out _), Is.True);
            using var create = new HttpRequestMessage(
                HttpMethod.Post,
                $"/api/v1/agents/{Uri.EscapeDataString(first.AgentId)}/upgrade-operations")
            {
                Content = JsonContent.Create(new
                {
                    reason = "tier-two safe rollout",
                    timeoutSeconds = 120,
                }),
            };
            create.Headers.Add("Idempotency-Key", "upgrade-first-agent");
            using var created = await admin.SendAsync(create);
            var createdText = await created.Content.ReadAsStringAsync();
            Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.Accepted), createdText);
            using var createdBody = JsonDocument.Parse(createdText);
            var operationId = createdBody.RootElement.GetProperty("operationId").GetString()!;

            var draining = await controller.AgentUpgrades.FindAsync(operationId);
            Assert.Multiple(() =>
            {
                Assert.That(draining!.State, Is.EqualTo(AgentUpgradeState.Draining));
                Assert.That(controller.Registry.Get(first.AgentId)!.Activity,
                    Is.EqualTo(AgentActivity.Building));
                Assert.That(controller.Registry.TryBeginBuild(second.AgentId, "peer-build", out _), Is.True);
            });
            var agentToken = File.ReadAllText(Path.Combine(firstData, "auth.token")).Trim();
            using var agentHttp = PinnedClient(controller);
            agentHttp.DefaultRequestHeaders.Authorization = new("Bearer", agentToken);
            var platform = ManifestPlatform();
            using (var blockedManifest = await agentHttp.GetAsync(
                       $"/bootstrap/manifest?os={platform.Os}&arch={platform.Arch}"))
            {
                Assert.That(blockedManifest.StatusCode, Is.EqualTo(HttpStatusCode.NoContent),
                    "bootstrap must not see target bytes until the busy Build has handed off");
            }
            controller.Registry.EndBuild(second.AgentId, "peer-build");
            controller.Registry.EndBuild(first.AgentId, "busy-build");

            await firstTask.WaitAsync(TimeSpan.FromSeconds(20));
            using var manifest = await agentHttp.GetAsync(
                $"/bootstrap/manifest?os={platform.Os}&arch={platform.Arch}");
            var manifestText = await manifest.Content.ReadAsStringAsync();
            Assert.That(manifest.StatusCode, Is.EqualTo(HttpStatusCode.OK), manifestText);
            using var manifestBody = JsonDocument.Parse(manifestText);
            var packageUrl = manifestBody.RootElement.GetProperty("url").GetString()!;
            Assert.Multiple(() =>
            {
                Assert.That(manifestBody.RootElement.GetProperty("operationId").GetString(),
                    Is.EqualTo(operationId));
                Assert.That(manifestBody.RootElement.GetProperty("sha256").GetString(),
                    Is.EqualTo(targetDigest));
                Assert.That(packageUrl, Does.StartWith("/bootstrap/packages/"));
                Assert.That(manifestText, Does.Not.Contain(agentToken));
            });

            using var peerHttp = PinnedClient(controller);
            peerHttp.DefaultRequestHeaders.Authorization = new(
                "Bearer", File.ReadAllText(Path.Combine(secondData, "auth.token")).Trim());
            using var deniedPeer = await peerHttp.GetAsync(packageUrl);
            Assert.That(deniedPeer.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

            using var downloaded = await agentHttp.GetAsync(packageUrl);
            Assert.That(downloaded.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(await downloaded.Content.ReadAsByteArrayAsync(), Is.EqualTo(packageBytes));

            var marker = Path.Combine(firstData, "agent-upgrade-health.json");
            replacement = CreateAgent(
                controller,
                firstData,
                enrollToken: null,
                "2.0.0",
                targetDigest,
                operationId,
                marker);
            replacementTask = replacement.RunAsync(replacementLifetime.Token);
            var promotionTask = PromoteUpgradeMarkerAsync(marker);
            await replacement.WaitAuthorizedAsync(TimeSpan.FromSeconds(20));
            var completed = await WaitForValueAsync(
                async () =>
                {
                    var value = await controller.AgentUpgrades.FindAsync(operationId);
                    return value?.State == AgentUpgradeState.Succeeded ? value : null;
                },
                TimeSpan.FromSeconds(20));
            await promotionTask;

            Assert.Multiple(() =>
            {
                Assert.That(completed.ResultPackageSha256, Is.EqualTo(targetDigest));
                Assert.That(completed.ObservedConnectionGeneration,
                    Is.GreaterThan(completed.StartingConnectionGeneration));
                Assert.That(completed.RestartAttempts, Is.GreaterThanOrEqualTo(1));
                Assert.That(controller.Registry.Get(first.AgentId)!.Activity, Is.EqualTo(AgentActivity.Idle));
                Assert.That(secondTask.IsCompleted, Is.False);
                Assert.That(File.Exists(marker), Is.True);
            });
        }
        finally
        {
            firstLifetime.Cancel();
            secondLifetime.Cancel();
            replacementLifetime.Cancel();
            await IgnoreCancellationAsync(firstTask);
            await IgnoreCancellationAsync(secondTask);
            if (replacementTask is not null)
            {
                await IgnoreCancellationAsync(replacementTask);
            }
        }
    }

    [Test]
    public async Task Cancellation_releases_only_before_handoff_and_after_handoff_requires_exact_rollback()
    {
        await using var controller = await StartControllerAsync();
        var dataDir = Path.Combine(rootDir, "cancel-agent");
        var priorDigest = new string('c', 64);
        var agent = CreateAgent(
            controller,
            dataDir,
            await controller.Tokens.CreateEnrollTokenAsync(),
            "1.0.0",
            priorDigest);
        using var lifetime = new CancellationTokenSource();
        var agentTask = agent.RunAsync(lifetime.Token);
        Task? rollbackTask = null;
        using var rollbackLifetime = new CancellationTokenSource();
        try
        {
            await WaitForAsync(
                () => controller.Registry.Get(agent.AgentId)?.Reconciled == true,
                TimeSpan.FromSeconds(20));
            await controller.AuthorizeAgentAsync(agent.AgentId);
            await agent.WaitAuthorizedAsync(TimeSpan.FromSeconds(20));

            var packageBytes = CreatePackage(CurrentRid(), "cancel-target");
            using var admin = PinnedClient(controller);
            admin.DefaultRequestHeaders.Authorization = new("Bearer", controller.Tokens.AdminToken);
            using var published = await admin.SendAsync(PackageRequest(
                CurrentRid(), "7.0.0", "cancel-package", Digest(packageBytes), packageBytes));
            using var packageBody = JsonDocument.Parse(await published.Content.ReadAsStreamAsync());
            var packageId = packageBody.RootElement.GetProperty("packageId").GetString()!;

            async Task<string> CreateAsync(string key)
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Post,
                    $"/api/v1/agents/{Uri.EscapeDataString(agent.AgentId)}/upgrade-operations")
                {
                    Content = JsonContent.Create(new
                    {
                        reason = key,
                        timeoutSeconds = 120,
                    }),
                };
                request.Headers.Add("Idempotency-Key", key);
                using var response = await admin.SendAsync(request);
                var text = await response.Content.ReadAsStringAsync();
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted), text);
                using var body = JsonDocument.Parse(text);
                return body.RootElement.GetProperty("operationId").GetString()!;
            }

            Assert.That(controller.Registry.TryBeginBuild(agent.AgentId, "cancel-busy", out _), Is.True);
            var beforeHandoffId = await CreateAsync("cancel-before-handoff");
            using (var cancellation = await admin.PutAsJsonAsync(
                       $"/api/v1/agent-upgrade-operations/{beforeHandoffId}/cancellation",
                       new { reason = "operator-cancel-before-handoff" }))
            {
                using var body = JsonDocument.Parse(await cancellation.Content.ReadAsStreamAsync());
                Assert.Multiple(() =>
                {
                    Assert.That(cancellation.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                    Assert.That(body.RootElement.GetProperty("state").GetString(), Is.EqualTo("cancelled"));
                    Assert.That(body.RootElement.GetProperty("drainHeld").GetBoolean(), Is.False);
                    Assert.That(body.RootElement.GetProperty("events").GetArrayLength(), Is.GreaterThanOrEqualTo(2));
                });
            }
            Assert.That(agentTask.IsCompleted, Is.False);
            Assert.That(await controller.AgentUpgradeStore.IsDrainedAsync(agent.AgentId), Is.False);
            controller.Registry.EndBuild(agent.AgentId, "cancel-busy");

            var afterHandoffId = await CreateAsync("cancel-after-handoff");
            await agentTask.WaitAsync(TimeSpan.FromSeconds(20));
            using (var bootstrap = PinnedClient(controller))
            {
                bootstrap.DefaultRequestHeaders.Authorization = new(
                    "Bearer", File.ReadAllText(Path.Combine(dataDir, "auth.token")).Trim());
                using var failure = await bootstrap.PostAsJsonAsync(
                    "/bootstrap/upgrade-failure",
                    new
                    {
                        schemaVersion = 1,
                        operationId = afterHandoffId,
                        failureCode = "child_termination_failed",
                    });
                Assert.That(failure.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
                using var repeated = await bootstrap.PostAsJsonAsync(
                    "/bootstrap/upgrade-failure",
                    new
                    {
                        schemaVersion = 1,
                        operationId = afterHandoffId,
                        failureCode = "child_termination_failed",
                    });
                using var wrongOperation = await bootstrap.PostAsJsonAsync(
                    "/bootstrap/upgrade-failure",
                    new
                    {
                        schemaVersion = 1,
                        operationId = new string('f', 32),
                        failureCode = "child_termination_failed",
                    });
                Assert.Multiple(() =>
                {
                    Assert.That(repeated.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
                    Assert.That(wrongOperation.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
                });
            }
            var failed = await controller.AgentUpgrades.FindAsync(afterHandoffId);
            Assert.Multiple(() =>
            {
                Assert.That(failed?.State, Is.EqualTo(AgentUpgradeState.Failed));
                Assert.That(failed?.FailureCode, Is.EqualTo("child_termination_failed"));
                Assert.That(failed?.DrainHeld, Is.True);
            });
            using (var cancellation = await admin.PutAsJsonAsync(
                       $"/api/v1/agent-upgrade-operations/{afterHandoffId}/cancellation",
                       new { reason = "operator-rollback-after-handoff" }))
            {
                using var body = JsonDocument.Parse(await cancellation.Content.ReadAsStreamAsync());
                Assert.Multiple(() =>
                {
                    Assert.That(cancellation.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                    Assert.That(body.RootElement.GetProperty("state").GetString(),
                        Is.EqualTo("rollback-requested"));
                    Assert.That(body.RootElement.GetProperty("drainHeld").GetBoolean(), Is.True);
                });
            }

            var rollbackAgent = CreateAgent(
                controller,
                dataDir,
                enrollToken: null,
                "1.0.0",
                priorDigest,
                afterHandoffId);
            rollbackTask = rollbackAgent.RunAsync(rollbackLifetime.Token);
            var rolledBack = await WaitForValueAsync(async () =>
            {
                var operation = await controller.AgentUpgrades.FindAsync(afterHandoffId);
                return operation?.State == AgentUpgradeState.RolledBack ? operation : null;
            }, TimeSpan.FromSeconds(20));
            Assert.Multiple(() =>
            {
                Assert.That(rolledBack.ResultPackageSha256, Is.EqualTo(priorDigest));
                Assert.That(rolledBack.DrainHeld, Is.False);
            });
            Assert.That(await controller.AgentUpgradeStore.IsDrainedAsync(agent.AgentId), Is.False);
        }
        finally
        {
            lifetime.Cancel();
            rollbackLifetime.Cancel();
            await IgnoreCancellationAsync(agentTask);
            if (rollbackTask is not null)
            {
                await IgnoreCancellationAsync(rollbackTask);
            }
        }
    }

    [Test]
    public async Task Candidate_hello_with_durable_termination_failure_is_quarantined()
    {
        await using var controller = await StartControllerAsync();
        var dataDir = Path.Combine(rootDir, "hello-failure-agent");
        var priorDigest = new string('d', 64);
        var original = CreateAgent(
            controller,
            dataDir,
            await controller.Tokens.CreateEnrollTokenAsync(),
            "1.0.0",
            priorDigest);
        using var originalLifetime = new CancellationTokenSource();
        var originalTask = original.RunAsync(originalLifetime.Token);
        Task? candidateTask = null;
        using var candidateLifetime = new CancellationTokenSource();
        try
        {
            await WaitForAsync(
                () => controller.Registry.Get(original.AgentId)?.Reconciled == true,
                TimeSpan.FromSeconds(20));
            await controller.AuthorizeAgentAsync(original.AgentId);
            await original.WaitAuthorizedAsync(TimeSpan.FromSeconds(20));

            var package = CreatePackage(CurrentRid(), "failed-candidate");
            var targetDigest = Digest(package);
            using var admin = PinnedClient(controller);
            admin.DefaultRequestHeaders.Authorization = new("Bearer", controller.Tokens.AdminToken);
            using var published = await admin.SendAsync(PackageRequest(
                CurrentRid(), "4.4.0", "hello-failure-package", targetDigest, package));
            using var publishedBody = JsonDocument.Parse(await published.Content.ReadAsStreamAsync());
            using var create = new HttpRequestMessage(
                HttpMethod.Post,
                $"/api/v1/agents/{original.AgentId}/upgrade-operations")
            {
                Content = JsonContent.Create(new
                {
                    reason = "durable child termination evidence",
                    timeoutSeconds = 120,
                }),
            };
            create.Headers.Add("Idempotency-Key", "hello-failure-operation");
            using var created = await admin.SendAsync(create);
            using var createdBody = JsonDocument.Parse(await created.Content.ReadAsStreamAsync());
            var operationId = createdBody.RootElement.GetProperty("operationId").GetString()!;
            await originalTask.WaitAsync(TimeSpan.FromSeconds(20));

            var candidate = CreateAgent(
                controller,
                dataDir,
                enrollToken: null,
                "4.4.0",
                targetDigest,
                operationId,
                failureCode: "child_termination_failed");
            candidateTask = candidate.RunAsync(candidateLifetime.Token);
            var failed = await WaitForValueAsync(async () =>
            {
                var operation = await controller.AgentUpgrades.FindAsync(operationId);
                return operation?.State == AgentUpgradeState.Failed ? operation : null;
            }, TimeSpan.FromSeconds(20));

            Assert.Multiple(() =>
            {
                Assert.That(failed.FailureCode, Is.EqualTo("child_termination_failed"));
                Assert.That(failed.DrainHeld, Is.True);
            });
        }
        finally
        {
            originalLifetime.Cancel();
            candidateLifetime.Cancel();
            await IgnoreCancellationAsync(originalTask);
            if (candidateTask is not null)
            {
                await IgnoreCancellationAsync(candidateTask);
            }
        }
    }

    [Test]
    public async Task Bootstrap_fails_closed_when_schema_two_integrity_evidence_is_missing()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Ignore("The process-level bootstrap evidence runs on Linux/macOS tier-2 hosts.");
        }

        var repository = FindRepositoryRoot();
        var configuration = TestContext.CurrentContext.TestDirectory.Contains(
            $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
            StringComparison.Ordinal)
            ? "Release"
            : "Debug";
        var installDir = Path.Combine(rootDir, "integrity-agent");
        var packageDigest = new string('d', 64);
        var packageDir = Path.Combine(installDir, "agent", "packages", packageDigest);
        CopyRuntime(
            Path.Combine(repository, "src", "Vivarium.Bootstrap", "bin", configuration, "net10.0"),
            installDir,
            renameAgentHost: false);
        CopyRuntime(
            Path.Combine(repository, "src", "Vivarium.Agent", "bin", configuration, "net10.0"),
            packageDir,
            renameAgentHost: true);
        await File.WriteAllTextAsync(
            Path.Combine(installDir, "bootstrap.json"),
            JsonSerializer.Serialize(new
            {
                controllerUrl = "https://127.0.0.1:9",
                certFingerprint = new string('0', 64),
            }));
        await File.WriteAllTextAsync(
            Path.Combine(installDir, "agent", "active.json"),
            JsonSerializer.Serialize(new
            {
                schemaVersion = 2,
                active = new
                {
                    version = "2.0.0",
                    rid = CurrentRid(),
                    sha256 = packageDigest,
                    directory = $"packages/{packageDigest}",
                },
                fallback = (object?)null,
                pending = (object?)null,
                reportOperationId = (string?)null,
                reportFailureCode = (string?)null,
                consecutiveLaunchFailures = 0,
                nextLaunchUnixMs = 0,
            }));

        var bootstrapExecutable = Path.Combine(installDir, "Vivarium.Bootstrap");
        MakeExecutable(bootstrapExecutable);
        using var process = Process.Start(new ProcessStartInfo(bootstrapExecutable)
        {
            WorkingDirectory = installDir,
            UseShellExecute = false,
            RedirectStandardError = true,
        }) ?? throw new AssertionException("bootstrap process did not start");
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var errorText = await error;

        Assert.Multiple(() =>
        {
            Assert.That(process.ExitCode, Is.Not.Zero);
            Assert.That(File.Exists(Path.Combine(installDir, "agent", "child.json")), Is.False);
            Assert.That(errorText, Does.Contain("integrity verification failed"));
            Assert.That(File.Exists(Path.Combine(packageDir, ".vivarium-package-sha256")), Is.False,
                "schema-2 startup must never synthesize lost integrity evidence");
        });
    }

    [Test]
    public async Task Bootstrap_does_not_silently_reseed_an_initialized_installation_without_state()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Ignore("The process-level bootstrap evidence runs on Linux/macOS tier-2 hosts.");
        }

        var repository = FindRepositoryRoot();
        var configuration = TestContext.CurrentContext.TestDirectory.Contains(
            $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
            StringComparison.Ordinal)
            ? "Release"
            : "Debug";
        var installDir = Path.Combine(rootDir, "missing-state-agent");
        var currentDir = Path.Combine(installDir, "agent", "current");
        var packageDir = Path.Combine(installDir, "agent", "packages", new string('e', 64));
        CopyRuntime(
            Path.Combine(repository, "src", "Vivarium.Bootstrap", "bin", configuration, "net10.0"),
            installDir,
            renameAgentHost: false);
        CopyRuntime(
            Path.Combine(repository, "src", "Vivarium.Agent", "bin", configuration, "net10.0"),
            currentDir,
            renameAgentHost: true);
        Directory.CreateDirectory(packageDir);
        await File.WriteAllTextAsync(Path.Combine(packageDir, "upgrade-evidence"), "initialized");
        await File.WriteAllTextAsync(
            Path.Combine(installDir, "bootstrap.json"),
            JsonSerializer.Serialize(new
            {
                controllerUrl = "https://127.0.0.1:9",
                certFingerprint = new string('0', 64),
            }));

        var bootstrapExecutable = Path.Combine(installDir, "Vivarium.Bootstrap");
        MakeExecutable(bootstrapExecutable);
        using var process = Process.Start(new ProcessStartInfo(bootstrapExecutable)
        {
            WorkingDirectory = installDir,
            UseShellExecute = false,
            RedirectStandardError = true,
        }) ?? throw new AssertionException("bootstrap process did not start");
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var errorText = await error;

        Assert.Multiple(() =>
        {
            Assert.That(process.ExitCode, Is.Not.Zero);
            Assert.That(File.Exists(Path.Combine(installDir, "agent", "active.json")), Is.False);
            Assert.That(errorText, Does.Contain("missing from an initialized installation"));
        });
    }

    [Test]
    public async Task Bootstrap_retries_a_durable_termination_failure_before_launch_even_after_restart()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Ignore("The process-level bootstrap evidence runs on Linux/macOS tier-2 hosts.");
        }

        var repository = FindRepositoryRoot();
        var configuration = TestContext.CurrentContext.TestDirectory.Contains(
            $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
            StringComparison.Ordinal)
            ? "Release"
            : "Debug";
        var installDir = Path.Combine(rootDir, "pending-failure-agent");
        var currentDir = Path.Combine(installDir, "agent", "current");
        CopyRuntime(
            Path.Combine(repository, "src", "Vivarium.Bootstrap", "bin", configuration, "net10.0"),
            installDir,
            renameAgentHost: false);
        CopyRuntime(
            Path.Combine(repository, "src", "Vivarium.Agent", "bin", configuration, "net10.0"),
            currentDir,
            renameAgentHost: true);
        var activeDigest = Digest(await File.ReadAllBytesAsync(
            Path.Combine(currentDir, "vivarium-agent")));
        var operationId = new string('e', 32);
        await File.WriteAllTextAsync(
            Path.Combine(installDir, "bootstrap.json"),
            JsonSerializer.Serialize(new
            {
                controllerUrl = "https://127.0.0.1:9",
                certFingerprint = new string('0', 64),
            }));
        Directory.CreateDirectory(Path.Combine(installDir, "data"));
        await File.WriteAllTextAsync(
            Path.Combine(installDir, "data", "auth.token"),
            new string('A', 32));
        await File.WriteAllTextAsync(
            Path.Combine(installDir, "agent", "active.json"),
            JsonSerializer.Serialize(new
            {
                schemaVersion = 2,
                active = new
                {
                    version = "1.0.0",
                    rid = CurrentRid(),
                    sha256 = activeDigest,
                    directory = "current",
                },
                fallback = (object?)null,
                pending = (object?)null,
                reportOperationId = (string?)null,
                reportFailureCode = "child_termination_failed",
                consecutiveLaunchFailures = 0,
                nextLaunchUnixMs = 0,
                pendingFailureReport = new
                {
                    schemaVersion = 1,
                    operationId,
                    failureCode = "child_termination_failed",
                },
            }));

        var bootstrapExecutable = Path.Combine(installDir, "Vivarium.Bootstrap");
        MakeExecutable(bootstrapExecutable);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var process = Process.Start(new ProcessStartInfo(bootstrapExecutable)
            {
                WorkingDirectory = installDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            }) ?? throw new AssertionException("bootstrap process did not start");
            await Task.Delay(TimeSpan.FromSeconds(3));
            Assert.Multiple(() =>
            {
                Assert.That(process.HasExited, Is.False);
                Assert.That(File.Exists(Path.Combine(installDir, "agent", "child.json")), Is.False);
            });
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }

        using var state = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(installDir, "agent", "active.json")));
        Assert.That(
            state.RootElement.GetProperty("pendingFailureReport").GetProperty("operationId").GetString(),
            Is.EqualTo(operationId));
    }

    [Test]
    public async Task Bootstrap_rejects_activation_when_local_active_digest_is_not_the_controller_prior()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Ignore("The process-level bootstrap evidence runs on Linux/macOS tier-2 hosts.");
        }

        await using var controller = await StartControllerAsync();
        var enrollmentDir = Path.Combine(rootDir, "prior-enrollment");
        var controllerPrior = new string('a', 64);
        var enrolled = CreateAgent(
            controller,
            enrollmentDir,
            await controller.Tokens.CreateEnrollTokenAsync(),
            "1.0.0",
            controllerPrior);
        using var enrolledLifetime = new CancellationTokenSource();
        var enrolledTask = enrolled.RunAsync(enrolledLifetime.Token);
        Process? bootstrap = null;
        try
        {
            await WaitForAsync(
                () => controller.Registry.Get(enrolled.AgentId)?.Reconciled == true,
                TimeSpan.FromSeconds(20));
            await controller.AuthorizeAgentAsync(enrolled.AgentId);
            await enrolled.WaitAuthorizedAsync(TimeSpan.FromSeconds(20));

            var package = CreatePackage(CurrentRid(), "prior-binding-target");
            using var admin = PinnedClient(controller);
            admin.DefaultRequestHeaders.Authorization = new("Bearer", controller.Tokens.AdminToken);
            using var published = await admin.SendAsync(PackageRequest(
                CurrentRid(), "4.5.0", "prior-binding-package", Digest(package), package));
            using var publishedBody = JsonDocument.Parse(await published.Content.ReadAsStreamAsync());
            using var create = new HttpRequestMessage(
                HttpMethod.Post,
                $"/api/v1/agents/{enrolled.AgentId}/upgrade-operations")
            {
                Content = JsonContent.Create(new
                {
                    reason = "exact local prior binding",
                    timeoutSeconds = 120,
                }),
            };
            create.Headers.Add("Idempotency-Key", "prior-binding-operation");
            using var created = await admin.SendAsync(create);
            Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
            await enrolledTask.WaitAsync(TimeSpan.FromSeconds(20));

            var repository = FindRepositoryRoot();
            var configuration = TestContext.CurrentContext.TestDirectory.Contains(
                $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal)
                ? "Release"
                : "Debug";
            var installDir = Path.Combine(rootDir, "prior-binding-install");
            var currentDir = Path.Combine(installDir, "agent", "current");
            CopyRuntime(
                Path.Combine(repository, "src", "Vivarium.Bootstrap", "bin", configuration, "net10.0"),
                installDir,
                renameAgentHost: false);
            CopyRuntime(
                Path.Combine(repository, "src", "Vivarium.Agent", "bin", configuration, "net10.0"),
                currentDir,
                renameAgentHost: true);
            Directory.CreateDirectory(Path.Combine(installDir, "data"));
            foreach (var identityFile in new[] { "agent-id", "auth.token", "auth.generation" })
            {
                File.Copy(
                    Path.Combine(enrollmentDir, identityFile),
                    Path.Combine(installDir, "data", identityFile));
            }
            var localActive = Digest(await File.ReadAllBytesAsync(
                Path.Combine(currentDir, "vivarium-agent")));
            Assert.That(localActive, Is.Not.EqualTo(controllerPrior));
            await File.WriteAllTextAsync(
                Path.Combine(installDir, "bootstrap.json"),
                JsonSerializer.Serialize(new
                {
                    controllerUrl = controller.Url,
                    certFingerprint = "SHA256:" + controller.Certificate.FingerprintSha256,
                }));
            await File.WriteAllTextAsync(
                Path.Combine(installDir, "agent", "active.json"),
                JsonSerializer.Serialize(new
                {
                    schemaVersion = 2,
                    active = new
                    {
                        version = "local-divergent",
                        rid = CurrentRid(),
                        sha256 = localActive,
                        directory = "current",
                    },
                    fallback = (object?)null,
                    pending = (object?)null,
                    reportOperationId = (string?)null,
                    reportFailureCode = (string?)null,
                    consecutiveLaunchFailures = 0,
                    nextLaunchUnixMs = 0,
                }));

            var bootstrapExecutable = Path.Combine(installDir, "Vivarium.Bootstrap");
            MakeExecutable(bootstrapExecutable);
            bootstrap = Process.Start(new ProcessStartInfo(bootstrapExecutable)
            {
                WorkingDirectory = installDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            }) ?? throw new AssertionException("bootstrap process did not start");
            var statePath = Path.Combine(installDir, "agent", "active.json");
            await WaitForAsync(() =>
            {
                try
                {
                    using var state = JsonDocument.Parse(File.ReadAllText(statePath));
                    return state.RootElement.GetProperty("reportFailureCode").GetString() ==
                        "upgrade_prior_digest_mismatch";
                }
                catch (Exception exception) when (exception is IOException or JsonException)
                {
                    return false;
                }
            }, TimeSpan.FromSeconds(20));
            using var finalState = JsonDocument.Parse(await File.ReadAllTextAsync(statePath));
            Assert.Multiple(() =>
            {
                Assert.That(
                    finalState.RootElement.GetProperty("active").GetProperty("sha256").GetString(),
                    Is.EqualTo(localActive));
                Assert.That(finalState.RootElement.GetProperty("pending").ValueKind,
                    Is.EqualTo(JsonValueKind.Null));
                Assert.That(finalState.RootElement.GetProperty("fallback").ValueKind,
                    Is.EqualTo(JsonValueKind.Null));
            });
        }
        finally
        {
            enrolledLifetime.Cancel();
            await IgnoreCancellationAsync(enrolledTask);
            if (bootstrap is not null)
            {
                if (!bootstrap.HasExited)
                {
                    bootstrap.Kill(entireProcessTree: true);
                }
                await bootstrap.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                bootstrap.Dispose();
            }
        }
    }

    [Test]
    public async Task Bootstrap_is_singleton_and_readopts_the_exact_child_after_supervisor_restart()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Ignore("The process identity fixture currently runs on Linux/macOS tier-2 hosts.");
        }

        var repository = FindRepositoryRoot();
        var configuration = TestContext.CurrentContext.TestDirectory.Contains(
            $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
            StringComparison.Ordinal)
            ? "Release"
            : "Debug";
        var installDir = Path.Combine(rootDir, "singleton-agent");
        var currentDir = Path.Combine(installDir, "agent", "current");
        Directory.CreateDirectory(currentDir);
        CopyRuntime(
            Path.Combine(repository, "src", "Vivarium.Bootstrap", "bin", configuration, "net10.0"),
            installDir,
            renameAgentHost: false);
        CopyRuntime(
            Path.Combine(repository, "src", "Vivarium.Agent", "bin", configuration, "net10.0"),
            currentDir,
            renameAgentHost: true);
        await File.WriteAllTextAsync(
            Path.Combine(installDir, "bootstrap.json"),
            JsonSerializer.Serialize(new
            {
                controllerUrl = "https://127.0.0.1:9",
                certFingerprint = new string('0', 64),
            }));

        var bootstrapExecutable = Path.Combine(installDir, "Vivarium.Bootstrap");
        MakeExecutable(bootstrapExecutable);
        Process StartBootstrap() => Process.Start(new ProcessStartInfo(bootstrapExecutable)
        {
            WorkingDirectory = installDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }) ?? throw new AssertionException("bootstrap process did not start");

        using var first = StartBootstrap();
        Process? restarted = null;
        try
        {
            var childPath = Path.Combine(installDir, "agent", "child.json");
            var firstChildPidText = await WaitForValueAsync(() =>
            {
                try
                {
                    using var child = JsonDocument.Parse(File.ReadAllText(childPath));
                    return Task.FromResult<string?>(
                        child.RootElement.GetProperty("pid").GetInt32().ToString(
                            global::System.Globalization.CultureInfo.InvariantCulture));
                }
                catch (Exception exception) when (exception is IOException or JsonException)
                {
                    return Task.FromResult<string?>(null);
                }
            }, TimeSpan.FromSeconds(15));
            var firstChildPid = int.Parse(
                firstChildPidText,
                global::System.Globalization.CultureInfo.InvariantCulture);

            using (var duplicate = StartBootstrap())
            {
                await duplicate.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                Assert.That(duplicate.ExitCode, Is.EqualTo(3));
            }

            first.Kill(entireProcessTree: false);
            await first.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            restarted = StartBootstrap();
            await WaitForAsync(() =>
            {
                try
                {
                    using var child = JsonDocument.Parse(File.ReadAllText(childPath));
                    return child.RootElement.GetProperty("pid").GetInt32() == firstChildPid;
                }
                catch (Exception exception) when (exception is IOException or JsonException)
                {
                    return false;
                }
            }, TimeSpan.FromSeconds(10));
            Assert.That(Process.GetProcessById(firstChildPid).HasExited, Is.False);
        }
        finally
        {
            if (!first.HasExited)
            {
                first.Kill(entireProcessTree: true);
                await first.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
            if (restarted is not null)
            {
                if (!restarted.HasExited)
                {
                    restarted.Kill(entireProcessTree: true);
                }
                await restarted.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                restarted.Dispose();
            }
        }
    }

    [TestCase(-86_400_000L)]
    [TestCase(86_400_000L)]
    public async Task Bootstrap_waits_a_monotonic_orphan_window_for_any_lease_wall_timestamp(
        long wallClockOffsetMs)
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Ignore("The process identity fixture currently runs on Linux/macOS tier-2 hosts.");
        }

        var repository = FindRepositoryRoot();
        var configuration = TestContext.CurrentContext.TestDirectory.Contains(
            $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
            StringComparison.Ordinal)
            ? "Release"
            : "Debug";
        var installDir = Path.Combine(rootDir, $"orphan-lease-{wallClockOffsetMs}");
        var currentDir = Path.Combine(installDir, "agent", "current");
        CopyRuntime(
            Path.Combine(repository, "src", "Vivarium.Bootstrap", "bin", configuration, "net10.0"),
            installDir,
            renameAgentHost: false);
        CopyRuntime(
            Path.Combine(repository, "src", "Vivarium.Agent", "bin", configuration, "net10.0"),
            currentDir,
            renameAgentHost: true);
        var activeDigest = Digest(await File.ReadAllBytesAsync(
            Path.Combine(currentDir, "vivarium-agent")));
        await File.WriteAllTextAsync(
            Path.Combine(installDir, "bootstrap.json"),
            JsonSerializer.Serialize(new
            {
                controllerUrl = "https://127.0.0.1:9",
                certFingerprint = new string('0', 64),
            }));
        await File.WriteAllTextAsync(
            Path.Combine(installDir, "agent", "active.json"),
            JsonSerializer.Serialize(new
            {
                schemaVersion = 2,
                active = new
                {
                    version = "1.0.0",
                    rid = CurrentRid(),
                    sha256 = activeDigest,
                    directory = "current",
                },
                fallback = (object?)null,
                pending = (object?)null,
                reportOperationId = (string?)null,
                reportFailureCode = (string?)null,
                consecutiveLaunchFailures = 0,
                nextLaunchUnixMs = 0,
            }));
        Directory.CreateDirectory(Path.Combine(installDir, "data"));
        await File.WriteAllTextAsync(
            Path.Combine(installDir, "data", "bootstrap-lease.json"),
            JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                leaseId = new string('a', 32),
                writtenUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + wallClockOffsetMs,
            }));

        var bootstrapExecutable = Path.Combine(installDir, "Vivarium.Bootstrap");
        MakeExecutable(bootstrapExecutable);
        using var process = Process.Start(new ProcessStartInfo(bootstrapExecutable)
        {
            WorkingDirectory = installDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }) ?? throw new AssertionException("bootstrap process did not start");
        try
        {
            var childPath = Path.Combine(installDir, "agent", "child.json");
            await Task.Delay(TimeSpan.FromSeconds(4));
            Assert.That(File.Exists(childPath), Is.False,
                "persisted wall time must never bypass the local orphan wait");
            await WaitForAsync(() => File.Exists(childPath), TimeSpan.FromSeconds(20));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Test]
    public async Task Real_bootstrap_downloads_activates_and_health_confirms_real_agent_package()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Ignore("The process-level bootstrap evidence runs on the Linux/macOS tier-2 hosts.");
        }

        await using var controller = await StartControllerAsync();
        var installDir = Path.Combine(rootDir, "installed-agent");
        var currentDir = Path.Combine(installDir, "agent", "current");
        Directory.CreateDirectory(currentDir);
        var repository = FindRepositoryRoot();
        var framework = "net10.0";
        var configuration = TestContext.CurrentContext.TestDirectory.Contains(
            $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
            StringComparison.Ordinal)
            ? "Release"
            : "Debug";
        var bootstrapOutput = Path.Combine(
            repository, "src", "Vivarium.Bootstrap", "bin", configuration, framework);
        var agentOutput = Path.Combine(
            repository, "src", "Vivarium.Agent", "bin", configuration, framework);
        CopyRuntime(bootstrapOutput, installDir, renameAgentHost: false);
        CopyRuntime(agentOutput, currentDir, renameAgentHost: true);
        var enrollmentToken = await controller.Tokens.CreateEnrollTokenAsync();
        await File.WriteAllTextAsync(
            Path.Combine(installDir, "bootstrap.json"),
            JsonSerializer.Serialize(new
            {
                controllerUrl = controller.Url,
                certFingerprint = "SHA256:" + controller.Certificate.FingerprintSha256,
                enrollToken = enrollmentToken,
            }));

        var packageBytes = CreateRuntimePackage(agentOutput);
        var targetDigest = Digest(packageBytes);
        var bootstrapExecutable = Path.Combine(installDir, "Vivarium.Bootstrap");
        MakeExecutable(bootstrapExecutable);
        using var process = Process.Start(new ProcessStartInfo(bootstrapExecutable)
        {
            WorkingDirectory = installDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }) ?? throw new AssertionException("bootstrap process did not start");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        try
        {
            var connected = await WaitForValueAsync(
                () => Task.FromResult(controller.Registry.All.SingleOrDefault(
                    agent => agent.Connected && agent.Reconciled)),
                TimeSpan.FromSeconds(30));
            await controller.AuthorizeAgentAsync(connected.AgentId);
            await WaitForAsync(
                () => File.Exists(Path.Combine(installDir, "data", "auth.token")),
                TimeSpan.FromSeconds(20));

            using var admin = PinnedClient(controller);
            admin.DefaultRequestHeaders.Authorization = new("Bearer", controller.Tokens.AdminToken);
            using var published = await admin.SendAsync(PackageRequest(
                CurrentRid(), "5.0.0", "process-package", targetDigest, packageBytes));
            var publishedText = await published.Content.ReadAsStringAsync();
            Assert.That(published.StatusCode, Is.EqualTo(HttpStatusCode.Created), publishedText);
            using var publishedBody = JsonDocument.Parse(publishedText);
            var packageId = publishedBody.RootElement.GetProperty("packageId").GetString()!;

            using var create = new HttpRequestMessage(
                HttpMethod.Post,
                $"/api/v1/agents/{Uri.EscapeDataString(connected.AgentId)}/upgrade-operations")
            {
                Content = JsonContent.Create(new
                {
                    reason = "real bootstrap process evidence",
                    timeoutSeconds = 120,
                }),
            };
            create.Headers.Add("Idempotency-Key", "process-upgrade");
            using var created = await admin.SendAsync(create);
            var createdText = await created.Content.ReadAsStringAsync();
            Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.Accepted), createdText);
            using var createdBody = JsonDocument.Parse(createdText);
            var operationId = createdBody.RootElement.GetProperty("operationId").GetString()!;

            var completed = await WaitForValueAsync(async () =>
            {
                var operation = await controller.AgentUpgrades.FindAsync(operationId);
                return operation?.State == AgentUpgradeState.Succeeded ? operation : null;
            }, TimeSpan.FromSeconds(45));
            var statePath = Path.Combine(installDir, "agent", "active.json");
            await WaitForAsync(() =>
            {
                try
                {
                    using var observed = JsonDocument.Parse(File.ReadAllText(statePath));
                    return observed.RootElement.GetProperty("pending").ValueKind == JsonValueKind.Null;
                }
                catch (IOException)
                {
                    return false;
                }
                catch (JsonException)
                {
                    return false;
                }
            }, TimeSpan.FromSeconds(10));
            using var state = JsonDocument.Parse(await File.ReadAllTextAsync(
                statePath));
            Assert.Multiple(() =>
            {
                Assert.That(completed.ResultPackageSha256, Is.EqualTo(targetDigest));
                Assert.That(state.RootElement.GetProperty("active").GetProperty("sha256").GetString(),
                    Is.EqualTo(targetDigest));
                Assert.That(state.RootElement.GetProperty("pending").ValueKind,
                    Is.EqualTo(JsonValueKind.Null));
                Assert.That(controller.Registry.Get(connected.AgentId)!.Activity,
                    Is.EqualTo(AgentActivity.Idle));
            });
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            TestContext.Progress.WriteLine(await standardOutput);
            TestContext.Progress.WriteLine(await standardError);
        }
    }

    [Test]
    public async Task Real_bootstrap_rolls_back_failed_candidate_and_controller_observes_prior_digest()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Ignore("The process-level shell failure fixture runs on the Linux/macOS tier-2 hosts.");
        }

        await using var controller = await StartControllerAsync();
        var repository = FindRepositoryRoot();
        var configuration = TestContext.CurrentContext.TestDirectory.Contains(
            $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
            StringComparison.Ordinal)
            ? "Release"
            : "Debug";
        var bootstrapOutput = Path.Combine(
            repository, "src", "Vivarium.Bootstrap", "bin", configuration, "net10.0");
        var agentOutput = Path.Combine(
            repository, "src", "Vivarium.Agent", "bin", configuration, "net10.0");
        var installDir = Path.Combine(rootDir, "rollback-agent");
        CopyRuntime(bootstrapOutput, installDir, renameAgentHost: false);

        var oldPackage = CreateRuntimePackage(agentOutput);
        var oldDigest = Digest(oldPackage);
        var oldSlot = Path.Combine(installDir, "agent", "packages", oldDigest);
        Directory.CreateDirectory(oldSlot);
        using (var archive = new ZipArchive(new MemoryStream(oldPackage), ZipArchiveMode.Read))
        {
            archive.ExtractToDirectory(oldSlot);
        }

        MakeExecutable(Path.Combine(oldSlot, "vivarium-agent"));
        await File.WriteAllTextAsync(
            Path.Combine(installDir, "agent", "active.json"),
            JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                active = new
                {
                    version = "1.0.0",
                    rid = CurrentRid(),
                    sha256 = oldDigest,
                    directory = $"packages/{oldDigest}",
                },
                fallback = (object?)null,
                pending = (object?)null,
                reportOperationId = (string?)null,
            }));
        var enrollmentToken = await controller.Tokens.CreateEnrollTokenAsync();
        await File.WriteAllTextAsync(
            Path.Combine(installDir, "bootstrap.json"),
            JsonSerializer.Serialize(new
            {
                controllerUrl = controller.Url,
                certFingerprint = "SHA256:" + controller.Certificate.FingerprintSha256,
                enrollToken = enrollmentToken,
            }));

        var bootstrapExecutable = Path.Combine(installDir, "Vivarium.Bootstrap");
        MakeExecutable(bootstrapExecutable);
        using var process = Process.Start(new ProcessStartInfo(bootstrapExecutable)
        {
            WorkingDirectory = installDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }) ?? throw new AssertionException("bootstrap process did not start");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        try
        {
            var connected = await WaitForValueAsync(
                () => Task.FromResult(controller.Registry.All.SingleOrDefault(
                    agent => agent.Connected && agent.Reconciled)),
                TimeSpan.FromSeconds(30));
            await controller.AuthorizeAgentAsync(connected.AgentId);
            await WaitForAsync(
                () => File.Exists(Path.Combine(installDir, "data", "auth.token")),
                TimeSpan.FromSeconds(20));

            var failedPackage = CreatePackage(CurrentRid(), "#!/bin/sh\nexit 17\n");
            var failedDigest = Digest(failedPackage);
            using var admin = PinnedClient(controller);
            admin.DefaultRequestHeaders.Authorization = new("Bearer", controller.Tokens.AdminToken);
            using var published = await admin.SendAsync(PackageRequest(
                CurrentRid(), "6.0.0-bad", "failed-process-package", failedDigest, failedPackage));
            using var publishedBody = JsonDocument.Parse(await published.Content.ReadAsStreamAsync());
            var packageId = publishedBody.RootElement.GetProperty("packageId").GetString()!;
            using var create = new HttpRequestMessage(
                HttpMethod.Post,
                $"/api/v1/agents/{Uri.EscapeDataString(connected.AgentId)}/upgrade-operations")
            {
                Content = JsonContent.Create(new
                {
                    reason = "rollback process evidence",
                    timeoutSeconds = 120,
                }),
            };
            create.Headers.Add("Idempotency-Key", "rollback-process-upgrade");
            using var created = await admin.SendAsync(create);
            var createdText = await created.Content.ReadAsStringAsync();
            Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.Accepted), createdText);
            using var createdBody = JsonDocument.Parse(createdText);
            var operationId = createdBody.RootElement.GetProperty("operationId").GetString()!;

            var rolledBack = await WaitForValueAsync(async () =>
            {
                var operation = await controller.AgentUpgrades.FindAsync(operationId);
                return operation?.State == AgentUpgradeState.RolledBack ? operation : null;
            }, TimeSpan.FromSeconds(45));
            var drainRetained = await controller.AgentUpgradeStore.IsDrainedAsync(connected.AgentId);
            using var state = JsonDocument.Parse(await File.ReadAllTextAsync(
                Path.Combine(installDir, "agent", "active.json")));
            Assert.Multiple(() =>
            {
                Assert.That(rolledBack.PriorPackageSha256, Is.EqualTo(oldDigest));
                Assert.That(rolledBack.ResultPackageSha256, Is.EqualTo(oldDigest));
                Assert.That(rolledBack.FailureCode, Is.EqualTo("candidate_launch_failed"));
                Assert.That(state.RootElement.GetProperty("active").GetProperty("sha256").GetString(),
                    Is.EqualTo(oldDigest));
                Assert.That(state.RootElement.GetProperty("pending").ValueKind,
                    Is.EqualTo(JsonValueKind.Null));
                Assert.That(drainRetained, Is.False);
            });
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            TestContext.Progress.WriteLine(await standardOutput);
            TestContext.Progress.WriteLine(await standardError);
        }
    }

    private Task<VivariumControllerHost> StartControllerAsync(
        string? catalogPath = null,
        TimeProvider? timeProvider = null,
        bool developmentPackageApi = true) =>
        VivariumControllerHost.StartAsync(new ControllerOptions
        {
            DataDir = Path.Combine(rootDir, "controller"),
            Host = "127.0.0.1",
            Port = 0,
            AgentPackageCatalogPath = catalogPath,
            EnableDevelopmentAgentPackageApi = developmentPackageApi,
            TimeProvider = timeProvider ?? TimeProvider.System,
        });

    private async Task<string> CreateReleaseCatalogAsync(
        string version,
        IReadOnlyCollection<string>? rids = null)
    {
        var catalogDir = Path.Combine(rootDir, $"release-{Guid.NewGuid():N}");
        Directory.CreateDirectory(catalogDir);
        var entries = new List<object>();
        foreach (var rid in (rids ?? AgentPackageRids.Supported).Order(StringComparer.Ordinal))
        {
            var package = CreatePackage(rid, $"bundled-agent-package-{rid}");
            var file = $"agent-{rid}.zip";
            await File.WriteAllBytesAsync(Path.Combine(catalogDir, file), package);
            entries.Add(new
            {
                version,
                rid,
                file,
                sha256 = Digest(package),
            });
        }

        var catalogPath = Path.Combine(catalogDir, "catalog.json");
        await File.WriteAllTextAsync(catalogPath, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            packages = entries,
        }));
        return catalogPath;
    }

    private static AgentRunner CreateAgent(
        VivariumControllerHost controller,
        string dataDir,
        string? enrollToken,
        string packageVersion,
        string packageDigest,
        string? operationId = null,
        string? healthMarker = null,
        string? failureCode = null,
        bool supervised = true) => new(new AgentOptions
        {
            ControllerUrl = controller.Url,
            CertFingerprintSha256 = controller.Certificate.FingerprintSha256,
            EnrollToken = enrollToken,
            DataDir = dataDir,
            HeartbeatInterval = TimeSpan.FromMilliseconds(250),
            ReconnectDelay = TimeSpan.FromMilliseconds(250),
            AgentPackageVersion = packageVersion,
            AgentPackageSha256 = packageDigest,
            UpgradeOperationId = operationId,
            UpgradeHealthMarkerPath = healthMarker,
            UpgradeFailureCode = failureCode,
            PlatformFactsCollector = supervised ? new SupervisedFactsCollector() : null,
        });

    private sealed class SupervisedFactsCollector : IPlatformFactsCollector
    {
        private readonly IPlatformFactsCollector inner = PlatformFactsCollector.CreateDefault();

        public IReadOnlyList<PlatformCapabilitySupport> SupportedCapabilities =>
            [.. inner.SupportedCapabilities, new("vivarium.bootstrap-supervisor.v1", 1)];

        public async ValueTask<PlatformFactSnapshot> CollectAsync(
            AgentPackageIdentity package,
            CancellationToken cancellationToken = default)
        {
            var snapshot = await inner.CollectAsync(package, cancellationToken);
            return snapshot with
            {
                Capabilities = [.. snapshot.Capabilities, new("vivarium.bootstrap-supervisor.v1", 1)],
            };
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;

        public void Advance(TimeSpan amount) => utcNow += amount;
    }

    private static HttpRequestMessage PackageRequest(
        string rid,
        string version,
        string idempotencyKey,
        string digest,
        byte[] package)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"/api/v1/agent-packages/{Uri.EscapeDataString(rid)}/{Uri.EscapeDataString(version)}")
        {
            Content = new ByteArrayContent(package),
        };
        request.Content.Headers.ContentType = new("application/zip");
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        request.Headers.Add("X-Content-SHA256", digest);
        return request;
    }

    private static async Task PromoteUpgradeMarkerAsync(string markerPath)
    {
        await WaitForAsync(() =>
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(markerPath));
                return document.RootElement.GetProperty("stage").GetString() == "ready";
            }
            catch (Exception exception) when (exception is IOException or JsonException)
            {
                return false;
            }
        }, TimeSpan.FromSeconds(20));

        using var ready = JsonDocument.Parse(await File.ReadAllTextAsync(markerPath));
        var root = ready.RootElement;
        var promoted = JsonSerializer.Serialize(new
        {
            schemaVersion = 2,
            stage = "promoted",
            operationId = root.GetProperty("operationId").GetString(),
            packageSha256 = root.GetProperty("packageSha256").GetString(),
            sessionId = root.GetProperty("sessionId").GetString(),
            connectionGeneration = root.GetProperty("connectionGeneration").GetUInt64(),
            writtenUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });
        var promotionPath = markerPath + ".promoted";
        var temporary = promotionPath + ".tmp";
        await File.WriteAllTextAsync(temporary, promoted);
        File.Move(temporary, promotionPath, overwrite: true);
    }

    private static byte[] CreatePackage(string rid, string content)
    {
        using var result = new MemoryStream();
        using (var archive = new ZipArchive(result, ZipArchiveMode.Create, leaveOpen: true))
        {
            var executable = archive.CreateEntry(
                rid == "win-x64" ? "vivarium-agent.exe" : "vivarium-agent",
                CompressionLevel.NoCompression);
            using var writer = new StreamWriter(executable.Open(), Encoding.UTF8, leaveOpen: false);
            writer.Write(content);
        }

        return result.ToArray();
    }

    private static byte[] CreateRuntimePackage(string agentOutput)
    {
        using var result = new MemoryStream();
        using (var archive = new ZipArchive(result, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in Directory.EnumerateFiles(agentOutput).Order(StringComparer.Ordinal))
            {
                var name = Path.GetFileName(file);
                if (name == "Vivarium.Agent")
                {
                    name = "vivarium-agent";
                }

                var entry = archive.CreateEntry(name, CompressionLevel.Fastest);
                using var input = File.OpenRead(file);
                using var output = entry.Open();
                input.CopyTo(output);
            }
        }

        return result.ToArray();
    }

    private static void CopyRuntime(string source, string destination, bool renameAgentHost)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            var name = Path.GetFileName(file);
            if (renameAgentHost && name == "Vivarium.Agent")
            {
                name = "vivarium-agent";
            }

            var target = Path.Combine(destination, name);
            File.Copy(file, target, overwrite: true);
            if (!OperatingSystem.IsWindows() &&
                (name == "vivarium-agent" || name == "Vivarium.Bootstrap"))
            {
                File.SetUnixFileMode(
                    target,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
        }
    }

    private static void MakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Vivarium.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new AssertionException("repository root was not found");
    }

    private static string Digest(byte[] value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private static string CurrentRid()
    {
        var platform = ManifestPlatform();
        return AgentPackageRids.FromPlatform(platform.Os, platform.Arch);
    }

    private static (string Os, string Arch) ManifestPlatform()
    {
        var os = OperatingSystem.IsWindows() ? "windows" :
            OperatingSystem.IsMacOS() ? "macos" : "linux";
        var arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture ==
            System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64";
        return (os, arch);
    }

    private static HttpClient PinnedClient(VivariumControllerHost controller)
    {
        var handler = new SocketsHttpHandler();
        handler.SslOptions.RemoteCertificateValidationCallback = (_, cert, _, _) =>
            cert != null && Convert.ToHexString(SHA256.HashData(cert.GetRawCertData()))
                .Equals(controller.Certificate.FingerprintSha256, StringComparison.OrdinalIgnoreCase);
        return new HttpClient(handler) { BaseAddress = new Uri(controller.Url) };
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new AssertionException("condition not reached within timeout");
    }

    private static async Task<T> WaitForValueAsync<T>(
        Func<Task<T?>> probe,
        TimeSpan timeout) where T : class
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            var value = await probe();
            if (value is not null)
            {
                return value;
            }

            await Task.Delay(100);
        }

        throw new AssertionException("condition not reached within timeout");
    }

    private static async Task IgnoreCancellationAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
    }
}
