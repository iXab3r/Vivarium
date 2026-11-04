using System.Text.Json;
using Vivarium.Controller.Catalog;
using Vivarium.Controller.Configuration.Git;

namespace Vivarium.DefinitionProof;

[NonParallelizable]
public sealed class DefinitionGitWriterPhysicalTests
{
    private string root = "";
    private string basis = "";
    private const string AgentPath = ".vivarium/agents/owned-agent.yaml";
    private string Journal => Path.Combine(root, ".git/vivarium-catalogue-checkout.json");

    [SetUp]
    public async Task CreateRepository()
    {
        root = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.WorkDirectory, "artifacts/r0-host/git-edit-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(Path.Combine(root, ".vivarium/agents"));
        await SourceCompiler.GitAsync(root, "init", "-b", "main");
        await SourceCompiler.GitAsync(root, "config", "user.name", "Owned Git fixture");
        await SourceCompiler.GitAsync(root, "config", "user.email", "fixture@example.invalid");
        await SourceCompiler.GitAsync(root, "config", "core.autocrlf", "false");
        await File.WriteAllTextAsync(Path.Combine(root, "UserCode.cs"), "owned user computation\n");
        await File.WriteAllTextAsync(Path.Combine(root, AgentPath), "owned prior Agent declaration\n");
        await SourceCompiler.GitAsync(root, "add", "--all");
        await SourceCompiler.GitAsync(root, "commit", "-m", "fixture: initial tree");
        basis = await SourceCompiler.GitAsync(root, "rev-parse", "HEAD");
    }

    [TearDown]
    public void Cleanup()
    {
        var parent = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.WorkDirectory, "artifacts/r0-host")) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(root);
        if (!target.StartsWith(parent, StringComparison.OrdinalIgnoreCase)
            || !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(target), "^git-edit-[a-f0-9]{32}$"))
            throw new InvalidDataException("The owned Git fixture path differs.");
        if (Directory.Exists(target))
        {
            foreach (var file in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
            Directory.Delete(target, recursive: true);
        }
    }

    [Test]
    public async Task Acceptance_updates_the_checkout_without_running_an_author_reference_hook()
    {
        await File.WriteAllTextAsync(Path.Combine(root, ".git/hooks/reference-transaction"), "#!/bin/sh\nprintf forbidden > hook-fired\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path.Combine(root, ".git/hooks/reference-transaction"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var writer = new DefinitionGitWriter(root); var candidate = await PrepareAsync(writer);
        Assert.That(await writer.AcceptAsync(basis, candidate, CancellationToken.None), Is.True);
        Assert.That(await SourceCompiler.GitAsync(root, "rev-parse", "HEAD"), Is.EqualTo(candidate));
        Assert.That(await File.ReadAllTextAsync(Path.Combine(root, AgentPath)), Is.EqualTo("owned replacement\n"));
        Assert.That(await File.ReadAllTextAsync(Path.Combine(root, "UserCode.cs")), Is.EqualTo("owned user computation\n"));
        Assert.That(File.Exists(Path.Combine(root, "hook-fired")), Is.False);
        Assert.That(await SourceCompiler.GitAsync(root, "status", "--porcelain=v1"), Is.Empty);
    }

    [Test]
    public async Task Acceptance_does_not_execute_a_configured_filter_selected_by_authored_attributes()
    {
        await File.WriteAllTextAsync(Path.Combine(root, ".gitattributes"), ".vivarium/agents/*.yaml filter=owned\n");
        await SourceCompiler.GitAsync(root, "config", "filter.owned.smudge", "printf forbidden > filter-fired; cat");
        await SourceCompiler.GitAsync(root, "add", ".gitattributes");
        await SourceCompiler.GitAsync(root, "commit", "-m", "fixture: authored attributes");
        basis = await SourceCompiler.GitAsync(root, "rev-parse", "HEAD");
        var writer = new DefinitionGitWriter(root); var candidate = await PrepareAsync(writer);
        Assert.That(await writer.AcceptAsync(basis, candidate, CancellationToken.None), Is.True);
        Assert.That(File.Exists(Path.Combine(root, "filter-fired")), Is.False);
        Assert.That(await File.ReadAllTextAsync(Path.Combine(root, AgentPath)), Is.EqualTo("owned replacement\n"));
    }

    [Test]
    public async Task Ref_conflict_retains_the_external_commit_and_does_not_accept_the_prepared_candidate()
    {
        var writer = new DefinitionGitWriter(root); var candidate = await PrepareAsync(writer);
        await File.WriteAllTextAsync(Path.Combine(root, "UserCode.cs"), "owned external edit\n");
        await SourceCompiler.GitAsync(root, "add", "UserCode.cs");
        await SourceCompiler.GitAsync(root, "commit", "-m", "fixture: competing edit");
        var external = await SourceCompiler.GitAsync(root, "rev-parse", "HEAD");
        Assert.That(await writer.AcceptAsync(basis, candidate, CancellationToken.None), Is.False);
        Assert.That(await SourceCompiler.GitAsync(root, "rev-parse", "HEAD"), Is.EqualTo(external));
        Assert.That(await File.ReadAllTextAsync(Path.Combine(root, "UserCode.cs")), Is.EqualTo("owned external edit\n"));
        Assert.That(await File.ReadAllTextAsync(Path.Combine(root, AgentPath)), Is.EqualTo("owned prior Agent declaration\n"));
        Assert.That(File.Exists(Journal), Is.False);
    }

    [Test]
    public async Task Recovery_after_ref_acceptance_completes_only_its_checkout_and_preserves_an_unrelated_edit()
    {
        var writer = new DefinitionGitWriter(root); var candidate = await PrepareAsync(writer);
        await JournalAsync(candidate);
        await SourceCompiler.GitAsync(root, "update-ref", "refs/heads/main", candidate, basis);
        await File.WriteAllTextAsync(Path.Combine(root, "UserCode.cs"), "owned uncommitted edit\n");
        await new DefinitionGitWriter(root).RecoverAsync(CancellationToken.None);
        Assert.That(await File.ReadAllTextAsync(Path.Combine(root, AgentPath)), Is.EqualTo("owned replacement\n"));
        Assert.That(await File.ReadAllTextAsync(Path.Combine(root, "UserCode.cs")), Is.EqualTo("owned uncommitted edit\n"));
        Assert.That(File.Exists(Journal), Is.False);
    }

    [Test]
    public async Task Recovery_does_not_overwrite_a_conflicting_target_edit_and_can_resume_after_it_is_resolved()
    {
        var writer = new DefinitionGitWriter(root); var candidate = await PrepareAsync(writer);
        await JournalAsync(candidate);
        await SourceCompiler.GitAsync(root, "update-ref", "refs/heads/main", candidate, basis);
        await File.WriteAllTextAsync(Path.Combine(root, AgentPath), "owned conflicting local edit\n");
        Assert.ThrowsAsync<ConfigurationRepositoryException>(() => new DefinitionGitWriter(root).RecoverAsync(CancellationToken.None));
        Assert.That(await File.ReadAllTextAsync(Path.Combine(root, AgentPath)), Is.EqualTo("owned conflicting local edit\n"));
        Assert.That(File.Exists(Journal), Is.True);
        await File.WriteAllTextAsync(Path.Combine(root, AgentPath), "owned prior Agent declaration\n");
        await new DefinitionGitWriter(root).RecoverAsync(CancellationToken.None);
        Assert.That(await File.ReadAllTextAsync(Path.Combine(root, AgentPath)), Is.EqualTo("owned replacement\n"));
        Assert.That(File.Exists(Journal), Is.False);
    }

    [Test]
    public async Task An_unperformed_ref_transition_is_not_invented_by_recovery()
    {
        var writer = new DefinitionGitWriter(root); var candidate = await PrepareAsync(writer);
        await JournalAsync(candidate);
        await new DefinitionGitWriter(root).RecoverAsync(CancellationToken.None);
        Assert.That(await SourceCompiler.GitAsync(root, "rev-parse", "HEAD"), Is.EqualTo(basis));
        Assert.That(await File.ReadAllTextAsync(Path.Combine(root, AgentPath)), Is.EqualTo("owned prior Agent declaration\n"));
        Assert.That(File.Exists(Journal), Is.False);
    }

    private Task JournalAsync(string candidate) => File.WriteAllTextAsync(Journal, JsonSerializer.Serialize(new { Expected = basis, Candidate = candidate }));
    private Task<string> PrepareAsync(DefinitionGitWriter writer) => writer.PrepareAsync(basis,
        new Dictionary<string, string> { [AgentPath] = "owned replacement\n" }, Guid.NewGuid(), Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), CancellationToken.None);
}
