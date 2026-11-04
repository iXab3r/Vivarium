using Vivarium.Contracts;
using Vivarium.Controller.Catalog;
using Vivarium.Controller.Configuration.Git;
using Vivarium.Definitions;

namespace Vivarium.Controller.Builds.Runtime;

/// <summary>Anonymous Git observations only. Never checks out files or executes repository code.</summary>
internal static class RuntimeVcsChanges
{
    internal static string[] Rules(Trigger trigger)
    {
        if (trigger.Type is not ("vcs" or "vcsTrigger") || trigger.BranchFilter != "+:<default>"
            || trigger.QuietPeriodMode != "USE_CUSTOM" || trigger.QuietSeconds < 0)
            throw new InvalidDataException("This VCS trigger profile is unavailable.");
        var rules = (trigger.Rules ?? "").ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (rules.Length > 64 || rules.Any(r => !r.StartsWith("-:", StringComparison.Ordinal)
            || r.Length is < 3 or > 512 || r[2..].StartsWith('/') || r.Contains('\\') || r.Any(char.IsControl)
            || r[2..].Split('/').Any(p => p is "" or "." or "..")
            || (r.EndsWith("/**", StringComparison.Ordinal) ? r[2..^3] : r[2..]).IndexOfAny(['*', '?', '[', ']', ':']) >= 0))
            throw new InvalidDataException("This trigger accepts literal and directory-recursive exclusions.");
        return rules.Select(r => r[2..]).ToArray();
    }

    internal static bool Relevant(IEnumerable<string> paths, Trigger trigger)
    {
        var rules = Rules(trigger);
        return paths.Any(path => !rules.Any(rule => rule.EndsWith("/**", StringComparison.Ordinal)
            ? path.StartsWith(rule[..^2], StringComparison.Ordinal) : path == rule));
    }

    internal static async Task<(string Version, string[]? Paths)> ObserveAsync(Source source, string? previous, CancellationToken token)
    {
        RuntimeBuildSources.Profile(source);
        var directory = Directory.CreateTempSubdirectory("vivarium-trigger-git-");
        try
        {
            var environment = DefinitionGitReader.GitEnvironment();
            foreach (var key in Environment.GetEnvironmentVariables().Keys.Cast<string>().Where(k => k.EndsWith("_PROXY", StringComparison.OrdinalIgnoreCase))) environment[key] = null;
            foreach (var key in new[] { "HOME", "USERPROFILE", "XDG_CONFIG_HOME", "CURL_HOME" }) environment[key] = directory.FullName;
            environment["GIT_CEILING_DIRECTORIES"] = directory.Parent!.FullName;
            environment["GIT_ALLOW_PROTOCOL"] = "https:http:git";
            environment["GIT_TERMINAL_PROMPT"] = "0"; environment["GCM_INTERACTIVE"] = "never";
            var git = new GitCommandRunner(directory.FullName);
            var args = VcsCheckoutProfile.ConfigurationArguments;
            var remote = (await git.RunAsync([.. args, "ls-remote", "--exit-code", "--refs", "--", source.Url, source.Branch], environment, cancellationToken: token)).Split('\t');
            if (remote.Length != 2 || remote[1] != source.Branch || !System.Text.RegularExpressions.Regex.IsMatch(remote[0], "^[a-f0-9]{40}$"))
                throw new InvalidDataException("Git trigger source identity differs.");
            var version = remote[0];
            if (previous is null || previous == version) return (version, null);
            if (!System.Text.RegularExpressions.Regex.IsMatch(previous, "^[a-f0-9]{40}$")) throw new InvalidDataException("Invalid prior Git source identity.");
            // Only two commit/tree observations. No working tree, submodules, LFS, hooks or credentials.
            await git.RunAsync([.. args, "init", "--bare", "."], environment, cancellationToken: token);
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
            var monitor = BoundStorageAsync(directory.FullName, budget);
            try
            {
                await git.RunAsync([.. args, "-c", "fetch.unpackLimit=1", "fetch", "--no-tags", "--no-write-fetch-head",
                    "--depth=1", "--filter=blob:none", "--", source.Url, previous, version], environment, cancellationToken: budget.Token);
                var changed = await git.RunBytesAsync([.. args, "diff-tree", "--no-commit-id", "--no-renames", "--name-only", "-r", "-z", previous, version], environment, cancellationToken: budget.Token);
                var paths = new System.Text.UTF8Encoding(false, true).GetString(changed).Split('\0', StringSplitOptions.RemoveEmptyEntries);
                if (paths.Length > 65536 || paths.Any(p => p.Length > 4096 || p.Any(char.IsControl))) throw new InvalidDataException("Changed paths exceed the trigger profile.");
                return (version, paths);
            }
            finally { budget.Cancel(); await monitor; }
        }
        finally
        {
            // The unshared, freshly created directory is the sole deletion root.
            foreach (var file in Directory.EnumerateFiles(directory.FullName, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            directory.Delete(recursive: true);
        }
    }

    private static async Task BoundStorageAsync(string directory, CancellationTokenSource budget)
    {
        try
        {
            while (!budget.IsCancellationRequested)
            {
                long total = 0; var count = 0;
                foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                {
                    long length;
                    try { length = new FileInfo(file).Length; }
                    catch (FileNotFoundException) { continue; } // Git atomically renames pack/index temporaries.
                    if (++count > 4096 || (total += length) > 128L * 1024 * 1024) { budget.Cancel(); return; }
                }
                await Task.Delay(50, budget.Token);
            }
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested) { }
    }
}
