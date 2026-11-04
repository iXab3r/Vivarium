using System.Text;
using System.Text.Json;
using Vivarium.Controller.Configuration.Git;

namespace Vivarium.Controller.Catalog;

/// <summary>Prepare regular blobs without author hooks or the user's index; accept by Git CAS.</summary>
internal sealed class DefinitionGitWriter(string path)
{
    private readonly GitCommandRunner git = new(Path.GetFullPath(path));
    private string Journal => Path.Combine(Path.GetFullPath(path), ".git", "vivarium-catalogue-checkout.json");
    private sealed record Checkout(string Expected, string Candidate);

    internal async Task RecoverAsync(CancellationToken token)
    {
        if (!File.Exists(Journal)) return;
        if (new FileInfo(Journal).Length > 1024) throw Unavailable("The catalogue checkout journal exceeds its bound.");
        Checkout state;
        try { state = JsonSerializer.Deserialize<Checkout>(await File.ReadAllTextAsync(Journal, token))
            ?? throw new JsonException(); }
        catch (JsonException) { throw Unavailable("The catalogue checkout journal is invalid."); }
        try
        {
            _ = new ConfigurationRevision("catalogue", state.Expected);
            _ = new ConfigurationRevision("catalogue", state.Candidate);
        }
        catch (ArgumentException) { throw Unavailable("The catalogue checkout journal is invalid."); }
        var current = await RunAsync(["rev-parse", "refs/heads/main"], token);
        if (current == state.Candidate)
        {
            await RunAsync(["update-index", "--refresh"], token, allowFailure: true);
            await RunAsync(["read-tree", "-m", "-u", state.Expected, state.Candidate], token);
        }
        else if (current != state.Expected
            && (await RunAsync(["status", "--porcelain=v1", "--untracked-files=all"], token)).Length != 0)
            throw Unavailable("The journalled catalogue checkout was changed externally.");
        File.Delete(Journal);
    }

    internal async Task<string> PrepareAsync(string expected, IReadOnlyDictionary<string, string> upserts,
        Guid operationId, string actorId, string correlation, CancellationToken token)
    {
        if (!Directory.Exists(Path.Combine(path, ".git")))
            throw Unavailable("Managed catalogue edits require a primary non-bare checkout.");
        await RecoverAsync(token);
        if (await RunAsync(["symbolic-ref", "HEAD"], token) != "refs/heads/main"
            || (await RunAsync(["status", "--porcelain=v1", "--untracked-files=all"], token)).Length != 0)
            throw Unavailable("Managed catalogue edits require a clean main checkout.");
        _ = new ConfigurationRevision("catalogue", expected);
        var index = Path.Combine(Path.GetFullPath(path), ".git", "vivarium-index-" + operationId.ToString("N"));
        var environment = DefinitionGitReader.GitEnvironment(); environment["GIT_INDEX_FILE"] = index;
        environment["GIT_AUTHOR_NAME"] = environment["GIT_COMMITTER_NAME"] = "Vivarium";
        environment["GIT_AUTHOR_EMAIL"] = environment["GIT_COMMITTER_EMAIL"] = "vivarium@localhost";
        try
        {
            await RunAsync(["read-tree", expected], token, environment);
            foreach (var (name, text) in upserts)
            {
                var blob = await RunAsync(["hash-object", "-w", "--stdin"], token, environment, Encoding.UTF8.GetBytes(text));
                await RunAsync(["update-index", "--add", "--cacheinfo", "100644", blob, name], token, environment);
            }
            var tree = await RunAsync(["write-tree"], token, environment);
            var message = $"Update Agent enablement\n\nVivarium-Operation: {operationId}\nVivarium-Actor: {actorId}\nVivarium-Correlation: {correlation}\n";
            var candidate = await RunAsync(["-c", "commit.gpgSign=false", "commit-tree", tree, "-p", expected], token, environment,
                Encoding.UTF8.GetBytes(message));
            _ = new ConfigurationRevision("catalogue", candidate);
            return candidate;
        }
        finally { File.Delete(index); }
    }

    internal async Task<bool> AcceptAsync(string expected, string candidate, CancellationToken token)
    {
        // Recovery never invents a ref transition. A failed/unperformed CAS retains the previous Git authority.
        var temporary = Journal + ".tmp";
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            await stream.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(new Checkout(expected, candidate)), token);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, Journal, overwrite: true);
        try { await RunAsync(["update-ref", "refs/heads/main", candidate, expected], token); }
        catch (ConfigurationRepositoryException error) when (error.Code == "CONFIG_GIT_COMMAND_FAILED")
        {
            if (await RunAsync(["rev-parse", "refs/heads/main"], token) != candidate)
            { File.Delete(Journal); return false; }
        }
        await RecoverAsync(token);
        return true;
    }

    private async Task<string> RunAsync(string[] arguments, CancellationToken token,
        IReadOnlyDictionary<string, string?>? environment = null, ReadOnlyMemory<byte>? input = null, bool allowFailure = false)
    {
        var isolated = environment ?? DefinitionGitReader.GitEnvironment();
        var disabledHooks = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        var options = new List<string> { "-c", "core.hooksPath=" + disabledHooks, "-c", "core.fsmonitor=false" };
        // Attribute-selected commands are not authored C# and must not gain a Host execution path.
        // Name-only discovery never reads credential-bearing filter command values into diagnostics.
        var keys = await git.RunAsync([.. options, "config", "--local", "--name-only", "--list"],
            isolated, cancellationToken: token);
        foreach (var key in keys.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var name = key.TrimEnd('\r');
            if (!name.StartsWith("filter.", StringComparison.Ordinal)) continue;
            if (!name.EndsWith(".clean", StringComparison.Ordinal) && !name.EndsWith(".smudge", StringComparison.Ordinal)
                && !name.EndsWith(".process", StringComparison.Ordinal) && !name.EndsWith(".required", StringComparison.Ordinal)) continue;
            options.Add("-c"); options.Add(name + "=" + (name.EndsWith(".required", StringComparison.Ordinal) ? "false" : ""));
        }
        return await git.RunAsync([.. options, .. arguments], isolated, input, allowFailure, cancellationToken: token);
    }
    private static ConfigurationRepositoryException Unavailable(string message) => new("CONFIG_CHECKOUT_UNAVAILABLE", message);
}
