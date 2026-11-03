using System.Text;
using System.Text.RegularExpressions;
using Vivarium.Controller.Configuration.Git;

namespace Vivarium.Controller.Catalog;

public sealed record DefinitionTree(string RepositoryId, string Commit, IReadOnlyDictionary<string, string> Sources);

/// <summary>Read committed regular blobs, never a checkout, author hook or repository build project.</summary>
public sealed class DefinitionGitReader(string repositoryPath, string repositoryId)
{
    private readonly GitCommandRunner git = new(Path.GetFullPath(repositoryPath));
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly IReadOnlyDictionary<string, string?> environment = GitEnvironment();

    private static Dictionary<string, string?> GitEnvironment()
    {
        var values = Environment.GetEnvironmentVariables().Keys.Cast<string>()
            .Where(key => key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(key => key, _ => (string?)null, StringComparer.OrdinalIgnoreCase);
        values["GIT_CONFIG_NOSYSTEM"] = "1";
        values["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        return values;
    }

    public async Task<string> HeadAsync(string branch = "refs/heads/main", CancellationToken token = default)
    {
        if (!branch.StartsWith("refs/heads/", StringComparison.Ordinal)
            || branch.Any(char.IsControl) || branch.Contains(':'))
            throw new InvalidDataException("The configured Git branch is invalid.");
        await git.RunAsync(["check-ref-format", branch], environment, cancellationToken: token);
        var commit = await git.RunAsync(["rev-parse", "--verify", branch + "^{commit}"], environment,
            cancellationToken: token);
        ObjectId(commit);
        return commit;
    }

    public async Task<DefinitionTree> ReadAsync(string commit, bool control, CancellationToken token = default)
    {
        ObjectId(commit);
        var tree = await git.RunBytesAsync(["ls-tree", "-r", "-z", "--full-tree", commit, "--", ".vivarium/"],
            environment, cancellationToken: token);
        var entries = Decode(tree).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (entries.Length > 4096) throw new InvalidDataException("The settings tree exceeds its entry bound.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        var bytes = 0;
        foreach (var entry in entries)
        {
            var tab = entry.IndexOf('\t');
            if (tab < 0) throw new InvalidDataException("Git tree metadata is invalid.");
            var fields = entry[..tab].Split(' ');
            var path = entry[(tab + 1)..];
            if (!paths.Add(path) || path.Contains('\\') || path.Any(char.IsControl)
                || path.Split('/').Any(p => p is "" or "." or "..") || path.Split('/').Length > 8)
                throw new InvalidDataException("The settings tree has conflicting or invalid paths.");
            if (fields.Length != 3 || fields[1] != "blob" || fields[0] is not ("100644" or "100755"))
                throw new InvalidDataException("The settings tree may contain only regular files.");
            ObjectId(fields[2]);
            var sizeText = await git.RunAsync(["cat-file", "-s", fields[2]], environment, cancellationToken: token);
            if (!int.TryParse(sizeText, out var size) || size < 0 || size > 256 * 1024 || (bytes += size) > 4 * 1024 * 1024)
                throw new InvalidDataException("The settings tree exceeds its byte bound.");
            // Only fixed declaration files and the version manifest enter the evaluator boundary.
            if (path == ".vivarium/repository.yaml" || path.EndsWith("/Control.g.cs", StringComparison.Ordinal)
                || path.EndsWith("/Settings.g.cs", StringComparison.Ordinal) || path.EndsWith("/UserCode.cs", StringComparison.Ordinal)
                || path.StartsWith(".vivarium/agents/", StringComparison.Ordinal) && path.EndsWith(".yaml", StringComparison.Ordinal))
            {
                var blob = await git.RunBytesAsync(["cat-file", "blob", fields[2]], environment, cancellationToken: token);
                if (blob.Length != size) throw new InvalidDataException("Git blob size differs.");
                sources.Add(path, Decode(blob));
            }
        }
        if (!sources.TryGetValue(".vivarium/repository.yaml", out var manifest)
            || manifest.ReplaceLineEndings("\n").TrimEnd('\n') != Manifest(repositoryId, control)
                && (!control || manifest.ReplaceLineEndings("\n").TrimEnd('\n') != Manifest(repositoryId, control, agents: true)))
            throw new InvalidDataException("The selected definition repository/schema is unsupported.");
        return new(repositoryId, commit, sources);
    }

    public static string Manifest(string repositoryId, bool control, bool agents = false)
    {
        DefinitionValidation.Identity(repositoryId);
        if (repositoryId.Any(c => c is ':' or '#' or '"' or '\'' || char.IsWhiteSpace(c)))
            throw new InvalidDataException("The repository identity is invalid.");
        var text = $"""
            apiVersion: vivarium.io/v1alpha1
            kind: Repository
            id: {repositoryId}
            spec:
              schemaVersion: "2"
              documentKinds:
                - {(control ? "Project" : "Settings")}
            """.ReplaceLineEndings("\n");
        return agents && control ? text + "\n    - Agent" : text;
    }

    private static void ObjectId(string value)
    {
        if (!Regex.IsMatch(value, "^(?:[a-f0-9]{40}|[a-f0-9]{64})$", RegexOptions.CultureInvariant))
            throw new InvalidDataException("Git commit/object identity is invalid.");
    }

    private static string Decode(byte[] bytes)
    {
        try { return Utf8.GetString(bytes); }
        catch (DecoderFallbackException) { throw new InvalidDataException("Definition paths and sources must be valid UTF-8."); }
    }
}
