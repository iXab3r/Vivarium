using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Vivarium.Controller.Configuration.Git;

namespace Vivarium.Tests;

[TestFixture]
[NonParallelizable]
public sealed class ManagedGitRepositoryTests
{
    private string rootDir = null!;

    [SetUp]
    public void SetUp()
    {
        rootDir = Path.Combine(
            Path.GetTempPath(),
            "vivarium-managed-git-tests",
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
    public async Task Managed_repository_initializes_reopens_and_exposes_an_immutable_valid_head()
    {
        var repositoryPath = NewRepositoryPath();
        var repository = await ManagedGitRepository.OpenOrCreateAsync(
            repositoryPath,
            "control-main");
        var initial = await repository.GetAuthoritativeHeadAsync();
        var validation = await repository.ValidateRevisionAsync(initial);

        Assert.Multiple(() =>
        {
            Assert.That(initial.Canonical, Is.EqualTo($"control-main@{initial.Commit}"));
            Assert.That(validation.IsValid, Is.True);
            Assert.That(validation.TreeHash, Has.Length.EqualTo(40).Or.Length.EqualTo(64));
            Assert.That(validation.Validated!.Descriptor.SchemaVersion, Is.EqualTo("1"));
            Assert.That(validation.Validated.Descriptor.Parents, Is.Empty);
            Assert.That(validation.Validated.Descriptor.AggregateContentHash, Has.Length.EqualTo(64));
            Assert.That(validation.Validated.Documents.Select(document => document.Path),
                Is.EqualTo(new[] { ".vivarium/repository.yaml" }));
            Assert.That(Directory.Exists(Path.Combine(repositoryPath, ".git")), Is.True);
            Assert.That(File.Exists(Path.Combine(repositoryPath, ".vivarium", "repository.yaml")), Is.True);
        });
        Assert.That(await RunGitAsync(repositoryPath, "status", "--porcelain=v1"), Is.Empty);
        Assert.That(await RunGitAsync(repositoryPath, "symbolic-ref", "HEAD"), Is.EqualTo("refs/heads/main"));

        var reopened = await ManagedGitRepository.OpenOrCreateAsync(repositoryPath, "control-main");
        Assert.That(await reopened.GetAuthoritativeHeadAsync(), Is.EqualTo(initial));
        Assert.That((await reopened.ValidateRevisionAsync(initial)).IsValid, Is.True);
    }

    [Test]
    public async Task Upsert_normalizes_committed_bytes_and_links_actor_and_revision_provenance()
    {
        var repositoryPath = NewRepositoryPath();
        var repository = await ManagedGitRepository.OpenOrCreateAsync(repositoryPath, "control-main");
        var initial = await repository.GetAuthoritativeHeadAsync();
        var mutableInput = Encoding.UTF8.GetBytes(AgentDocument("agent-one", enabled: true)
            .Replace("\n", "\r\n", StringComparison.Ordinal));

        var commitTask = repository.UpsertDocumentAsync(new ConfigurationDocumentMutation(
            initial,
            ".vivarium/agents/agent-one.yaml",
            mutableInput,
            CommitMetadata("enable-agent-one")));
        Array.Fill(mutableInput, (byte)'x');
        var result = await commitTask;
        var validation = await repository.ValidateRevisionAsync(result.ResultRevision!);
        var committedBytes = await RunGitBytesAsync(
            repositoryPath,
            "show",
            $"{result.ResultRevision!.Commit}:.vivarium/agents/agent-one.yaml");
        var provenance = validation.Validated!.Descriptor.ControllerProvenance;

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(ConfigurationCommitOutcome.Committed));
            Assert.That(result.ExpectedBase, Is.EqualTo(initial));
            Assert.That(result.CurrentRevision, Is.EqualTo(result.ResultRevision));
            Assert.That(result.CandidateAggregateContentHash,
                Is.EqualTo(validation.Validated.Descriptor.AggregateContentHash));
            Assert.That(result.Diff, Has.Count.EqualTo(1));
            Assert.That(result.Diff[0].ChangeKind, Is.EqualTo(ConfigurationPathChangeKind.Added));
            Assert.That(committedBytes, Is.EqualTo(Encoding.UTF8.GetBytes(AgentDocument("agent-one", true))));
            Assert.That(validation.Validated.Descriptor.Parents, Is.EqualTo(new[] { initial }));
            Assert.That(validation.Validated.Documents.Single(document => document.Kind == "Agent")
                .ScalarFields["spec.enabled"], Is.EqualTo("true"));
            Assert.That(provenance, Is.EqualTo(new ConfigurationCommitProvenance(
                "enable-agent-one",
                "request-enable-agent-one",
                "correlation-enable-agent-one",
                "user",
                "user-one")));
        });
        Assert.That(
            await RunGitAsync(repositoryPath, "show", "-s", "--format=%an|%ae", result.ResultRevision.Commit),
            Is.EqualTo("User One|user.one@example.invalid"));
        Assert.That(await RunGitAsync(repositoryPath, "status", "--porcelain=v1"), Is.Empty);
    }

    [Test]
    public async Task External_head_movement_returns_conflict_and_preserves_the_candidate_draft()
    {
        var repositoryPath = NewRepositoryPath();
        var repository = await ManagedGitRepository.OpenOrCreateAsync(repositoryPath, "control-main");
        var expected = await repository.GetAuthoritativeHeadAsync();
        await CommitExternalAgentAsync(repositoryPath, "external-agent", enabled: true, "External edit");
        var externalHead = await repository.GetAuthoritativeHeadAsync();

        var result = await repository.UpsertDocumentAsync(new ConfigurationDocumentMutation(
            expected,
            ".vivarium/agents/draft-agent.yaml",
            Encoding.UTF8.GetBytes(AgentDocument("draft-agent", enabled: false)),
            CommitMetadata("draft-agent-change")));

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(ConfigurationCommitOutcome.Conflict));
            Assert.That(result.CurrentRevision, Is.EqualTo(externalHead));
            Assert.That(result.ResultRevision, Is.Null);
            Assert.That(result.CandidateAggregateContentHash, Has.Length.EqualTo(64));
            Assert.That(result.Diff.Single().Path, Is.EqualTo(".vivarium/agents/external-agent.yaml"));
            Assert.That(result.Diff.Single().ChangeKind, Is.EqualTo(ConfigurationPathChangeKind.Added));
            Assert.That(result.Diff.Single().PreviousContentHash, Is.Null);
            Assert.That(result.Diff.Single().ResultContentHash, Has.Length.EqualTo(64));
            Assert.That(File.Exists(Path.Combine(
                repositoryPath, ".vivarium", "agents", "draft-agent.yaml")), Is.False);
        });
        Assert.That(await repository.GetAuthoritativeHeadAsync(), Is.EqualTo(externalHead));
    }

    [Test]
    public async Task Invalid_secret_and_injected_metadata_create_no_commit_or_git_object()
    {
        var repositoryPath = NewRepositoryPath();
        var repository = await ManagedGitRepository.OpenOrCreateAsync(repositoryPath, "control-main");
        var initial = await repository.GetAuthoritativeHeadAsync();
        var objectsBefore = await LooseObjectCountAsync(repositoryPath);
        const string secret = "ultra-secret-credential-value";
        var secretDocument = AgentDocument("agent-one", true) + $"  token: {secret}\n";

        var secretResult = await repository.UpsertDocumentAsync(new ConfigurationDocumentMutation(
            initial,
            ".vivarium/agents/agent-one.yaml",
            Encoding.UTF8.GetBytes(secretDocument),
            CommitMetadata("reject-secret")));
        var injectedResult = await repository.UpsertDocumentAsync(new ConfigurationDocumentMutation(
            initial,
            ".vivarium/agents/agent-one.yaml",
            Encoding.UTF8.GetBytes(AgentDocument("agent-one", true)),
            CommitMetadata("safe\nVivarium-Actor-ID: attacker")));
        var bearerActor = CommitMetadata("actor-secret") with
        {
            Actor = new ConfigurationCommitActor(
                "user-one",
                "user",
                "Bearer abcdefghijklmnop",
                "user.one@example.invalid"),
        };
        var actorResult = await repository.UpsertDocumentAsync(new ConfigurationDocumentMutation(
            initial,
            ".vivarium/agents/agent-one.yaml",
            Encoding.UTF8.GetBytes(AgentDocument("agent-one", true)),
            bearerActor));
        var pathResult = await repository.UpsertDocumentAsync(new ConfigurationDocumentMutation(
            initial,
            ".env",
            Encoding.UTF8.GetBytes("TOKEN=plaintext\n"),
            CommitMetadata("reject-runtime-path")));

        var diagnosticText = JsonSerializer.Serialize(new[]
        {
            secretResult.Diagnostics,
            injectedResult.Diagnostics,
            actorResult.Diagnostics,
            pathResult.Diagnostics,
        });
        var finalHead = await repository.GetAuthoritativeHeadAsync();
        var objectsAfter = await LooseObjectCountAsync(repositoryPath);
        Assert.Multiple(() =>
        {
            Assert.That(secretResult.Outcome, Is.EqualTo(ConfigurationCommitOutcome.Rejected));
            Assert.That(injectedResult.Outcome, Is.EqualTo(ConfigurationCommitOutcome.Rejected));
            Assert.That(actorResult.Outcome, Is.EqualTo(ConfigurationCommitOutcome.Rejected));
            Assert.That(pathResult.Outcome, Is.EqualTo(ConfigurationCommitOutcome.Rejected));
            Assert.That(secretResult.CandidateAggregateContentHash, Is.Null);
            Assert.That(diagnosticText, Does.Not.Contain(secret));
            Assert.That(diagnosticText, Does.Not.Contain("abcdefghijklmnop"));
            Assert.That(finalHead, Is.EqualTo(initial));
            Assert.That(objectsAfter, Is.EqualTo(objectsBefore));
        });
        Assert.Throws<ArgumentException>(() =>
            new ConfigurationRevision("Control-Main", initial.Commit));
    }

    [Test]
    public async Task Invalid_external_head_reopens_and_returns_exact_tree_hash_with_safe_diagnostics()
    {
        var repositoryPath = NewRepositoryPath();
        var repository = await ManagedGitRepository.OpenOrCreateAsync(repositoryPath, "control-main");
        var applied = await repository.GetAuthoritativeHeadAsync();
        const string secret = "external-secret-credential";
        var agentPath = Path.Combine(repositoryPath, ".vivarium", "agents", "agent-one.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(agentPath)!);
        await File.WriteAllTextAsync(
            agentPath,
            AgentDocument("agent-one", true) + $"  password: {secret}\n",
            new UTF8Encoding(false));
        await File.WriteAllTextAsync(Path.Combine(repositoryPath, ".env"), "AUTH_TOKEN=plaintext\n");
        await RunGitAsync(repositoryPath, "add", ".vivarium/agents/agent-one.yaml", ".env");
        await CommitExternalAsync(repositoryPath, "Invalid external head");
        var invalidHead = await repository.GetAuthoritativeHeadAsync();

        var reopened = await ManagedGitRepository.OpenOrCreateAsync(repositoryPath, "control-main");
        var validation = await reopened.ValidateRevisionAsync(invalidHead);
        var diagnosticText = JsonSerializer.Serialize(validation.Diagnostics);

        Assert.Multiple(() =>
        {
            Assert.That(invalidHead, Is.Not.EqualTo(applied));
            Assert.That(validation.IsValid, Is.False);
            Assert.That(validation.TreeHash, Has.Length.EqualTo(40).Or.Length.EqualTo(64));
            Assert.That(validation.Validated, Is.Null);
            Assert.That(validation.Diagnostics.Select(diagnostic => diagnostic.Code),
                Does.Contain("CONFIG_PATH_FORBIDDEN"));
            Assert.That(validation.Diagnostics.Select(diagnostic => diagnostic.Code),
                Does.Contain("CONFIG_SECRET_FIELD_FORBIDDEN"));
            Assert.That(diagnosticText, Does.Not.Contain(secret));
        });
    }

    [Test]
    public async Task Oversized_external_tree_is_rejected_before_blob_materialization()
    {
        var repositoryPath = NewRepositoryPath();
        var repository = await ManagedGitRepository.OpenOrCreateAsync(repositoryPath, "control-main");
        var oversizedPath = Path.Combine(repositoryPath, ".vivarium", "oversized.bin");
        await File.WriteAllBytesAsync(oversizedPath, new byte[4 * 1024 * 1024 + 1]);
        await RunGitAsync(repositoryPath, "add", ".vivarium/oversized.bin");
        await CommitExternalAsync(repositoryPath, "Oversized tree");
        var head = await repository.GetAuthoritativeHeadAsync();

        var validation = await repository.ValidateRevisionAsync(head);

        Assert.Multiple(() =>
        {
            Assert.That(validation.IsValid, Is.False);
            Assert.That(validation.TreeHash, Is.Not.Null);
            Assert.That(validation.Diagnostics.Select(diagnostic => diagnostic.Code),
                Does.Contain("CONFIG_TREE_SIZE_LIMIT"));
        });
    }

    [Test]
    public async Task Dirty_human_checkout_is_never_overwritten_by_a_gateway_mutation()
    {
        var repositoryPath = NewRepositoryPath();
        var repository = await ManagedGitRepository.OpenOrCreateAsync(repositoryPath, "control-main");
        var initial = await repository.GetAuthoritativeHeadAsync();
        var first = await repository.UpsertDocumentAsync(new ConfigurationDocumentMutation(
            initial,
            ".vivarium/agents/agent-one.yaml",
            Encoding.UTF8.GetBytes(AgentDocument("agent-one", true)),
            CommitMetadata("create-agent-one")));
        var humanEdit = AgentDocument("agent-one", false);
        var agentPath = Path.Combine(repositoryPath, ".vivarium", "agents", "agent-one.yaml");
        await File.WriteAllTextAsync(agentPath, humanEdit, new UTF8Encoding(false));

        var error = Assert.ThrowsAsync<ConfigurationRepositoryException>(async () =>
            await repository.UpsertDocumentAsync(new ConfigurationDocumentMutation(
                first.ResultRevision!,
                ".vivarium/agents/agent-two.yaml",
                Encoding.UTF8.GetBytes(AgentDocument("agent-two", true)),
                CommitMetadata("create-agent-two"))));

        Assert.Multiple(() =>
        {
            Assert.That(error!.Code, Is.EqualTo("CONFIG_REPOSITORY_DIRTY"));
            Assert.That(File.ReadAllText(agentPath), Is.EqualTo(humanEdit));
            Assert.That(File.Exists(Path.Combine(
                repositoryPath, ".vivarium", "agents", "agent-two.yaml")), Is.False);
        });
    }

    [Test]
    public async Task Reopen_repairs_a_ref_checkout_crash_gap_but_refuses_later_human_dirt()
    {
        var repositoryPath = NewRepositoryPath();
        var repository = await ManagedGitRepository.OpenOrCreateAsync(repositoryPath, "control-main");
        var initial = await repository.GetAuthoritativeHeadAsync();
        var first = await repository.UpsertDocumentAsync(new ConfigurationDocumentMutation(
            initial,
            ".vivarium/agents/agent-one.yaml",
            Encoding.UTF8.GetBytes(AgentDocument("agent-one", true)),
            CommitMetadata("create-agent-one")));
        var expected = first.ResultRevision!;
        await CommitExternalAgentAsync(repositoryPath, "agent-one", enabled: false, "External disable");
        var result = await repository.GetAuthoritativeHeadAsync();

        await SimulateRefAdvancedBeforeCheckoutSyncAsync(repositoryPath, expected.Commit, result.Commit);
        var reopened = await ManagedGitRepository.OpenOrCreateAsync(repositoryPath, "control-main");
        var recoveredHead = await reopened.GetAuthoritativeHeadAsync();
        Assert.Multiple(() =>
        {
            Assert.That(recoveredHead, Is.EqualTo(result));
            Assert.That(File.ReadAllText(Path.Combine(
                repositoryPath, ".vivarium", "agents", "agent-one.yaml")),
                Is.EqualTo(AgentDocument("agent-one", false)));
            Assert.That(File.Exists(Path.Combine(
                repositoryPath, ".git", "vivarium-checkout-sync.json")), Is.False);
        });

        await RunGitAsync(repositoryPath, "update-ref", "refs/heads/main", expected.Commit, result.Commit);
        await RunGitAsync(repositoryPath, "reset", "--hard", expected.Commit);
        await WriteRecoveryMarkerAsync(repositoryPath, expected.Commit, result.Commit);
        await RunGitAsync(repositoryPath, "update-ref", "refs/heads/main", result.Commit, expected.Commit);
        var humanBytes = AgentDocument("agent-one", true) + "# human note\n";
        var agentPath = Path.Combine(repositoryPath, ".vivarium", "agents", "agent-one.yaml");
        await File.WriteAllTextAsync(agentPath, humanBytes, new UTF8Encoding(false));

        var recoveryError = Assert.ThrowsAsync<ConfigurationRepositoryException>(async () =>
            await ManagedGitRepository.OpenOrCreateAsync(repositoryPath, "control-main"));
        Assert.Multiple(() =>
        {
            Assert.That(recoveryError!.Code, Is.EqualTo("CONFIG_REPOSITORY_DIRTY"));
            Assert.That(File.ReadAllText(agentPath), Is.EqualTo(humanBytes));
        });
    }

    private string NewRepositoryPath()
    {
        var path = Path.Combine(rootDir, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static ConfigurationCommitMetadata CommitMetadata(string operationId) =>
        new(
            $"Update configuration for {operationId}",
            operationId,
            $"request-{operationId}",
            $"correlation-{operationId}",
            new ConfigurationCommitActor(
                "user-one",
                "user",
                "User One",
                "user.one@example.invalid"));

    private static string AgentDocument(string agentId, bool enabled) =>
        $"""
        apiVersion: vivarium.io/v1alpha1
        kind: Agent
        id: {agentId}
        spec:
          enabled: {enabled.ToString().ToLowerInvariant()}

        """;

    private static async Task CommitExternalAgentAsync(
        string repositoryPath,
        string agentId,
        bool enabled,
        string message)
    {
        var path = Path.Combine(repositoryPath, ".vivarium", "agents", $"{agentId}.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, AgentDocument(agentId, enabled), new UTF8Encoding(false));
        await RunGitAsync(repositoryPath, "add", $".vivarium/agents/{agentId}.yaml");
        await CommitExternalAsync(repositoryPath, message);
    }

    private static Task CommitExternalAsync(string repositoryPath, string message) =>
        RunGitAsync(
            repositoryPath,
            "-c", "user.name=External Administrator",
            "-c", "user.email=external@example.invalid",
            "commit", "-m", message);

    private static async Task SimulateRefAdvancedBeforeCheckoutSyncAsync(
        string repositoryPath,
        string expected,
        string result)
    {
        await RunGitAsync(repositoryPath, "update-ref", "refs/heads/main", expected, result);
        await RunGitAsync(repositoryPath, "reset", "--hard", expected);
        await WriteRecoveryMarkerAsync(repositoryPath, expected, result);
        await RunGitAsync(repositoryPath, "update-ref", "refs/heads/main", result, expected);
    }

    private static async Task WriteRecoveryMarkerAsync(
        string repositoryPath,
        string expected,
        string result)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Version = 1,
            ExpectedCommit = expected,
            ResultCommit = result,
        });
        await File.WriteAllBytesAsync(
            Path.Combine(repositoryPath, ".git", "vivarium-checkout-sync.json"),
            bytes);
    }

    private static async Task<int> LooseObjectCountAsync(string repositoryPath)
    {
        var output = await RunGitAsync(repositoryPath, "count-objects", "-v");
        var count = output.Split('\n')
            .Single(line => line.StartsWith("count: ", StringComparison.Ordinal));
        return int.Parse(count["count: ".Length..]);
    }

    private static async Task<string> RunGitAsync(string repositoryPath, params string[] arguments)
    {
        var bytes = await RunGitBytesAsync(repositoryPath, arguments);
        return Encoding.UTF8.GetString(bytes).TrimEnd('\r', '\n');
    }

    private static async Task<byte[]> RunGitBytesAsync(
        string repositoryPath,
        params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = repositoryPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        using var output = new MemoryStream();
        var outputTask = process.StandardOutput.BaseStream.CopyToAsync(output);
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await outputTask;
        var error = await errorTask;
        Assert.That(process.ExitCode, Is.Zero, error);
        return output.ToArray();
    }
}
