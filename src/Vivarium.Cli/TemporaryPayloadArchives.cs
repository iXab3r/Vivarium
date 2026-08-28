using Vivarium.Cli.Configuration;

namespace Vivarium.Cli;

internal interface ITemporaryPayloadArchives : IAsyncDisposable
{
    IReadOnlyDictionary<string, PayloadArchiveInfo> Archives { get; }
}

internal interface ITemporaryPayloadArchiveFactory
{
    Task<ITemporaryPayloadArchives> CreateAsync(
        IEnumerable<ResolvedVivariumCell> cells,
        CancellationToken cancellationToken);
}

internal sealed class TemporaryPayloadArchiveFactory : ITemporaryPayloadArchiveFactory
{
    public async Task<ITemporaryPayloadArchives> CreateAsync(
        IEnumerable<ResolvedVivariumCell> cells,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cells);

        var temporaryRoot = Path.Combine(
            Path.GetTempPath(), "vivarium", "cli", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryRoot);
        try
        {
            var comparer = OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;
            var archives = new Dictionary<string, PayloadArchiveInfo>(comparer);
            foreach (var payloadGroup in cells.GroupBy(cell => cell.Payload.SourceDirectory, comparer))
            {
                var payloadRoot = payloadGroup.Key;
                var output = Path.Combine(temporaryRoot, archives.Count.ToString("D4") + ".zip");
                var executableFiles = ResolvePortableStepPrograms(payloadRoot, payloadGroup, comparer);
                archives.Add(
                    payloadRoot,
                    await PayloadArchive.CreateAsync(
                        payloadRoot,
                        output,
                        executableFiles,
                        cancellationToken));
            }

            return new TemporaryPayloadArchives(temporaryRoot, archives);
        }
        catch
        {
            DeleteBestEffort(temporaryRoot);
            throw;
        }
    }

    private static IReadOnlySet<string> ResolvePortableStepPrograms(
        string payloadRoot,
        IEnumerable<ResolvedVivariumCell> cells,
        StringComparer pathComparer)
    {
        var executableFiles = new HashSet<string>(pathComparer);
        foreach (var cell in cells.Where(cell => MayTargetUnix(cell.RuntimeIdentifier)))
        {
            foreach (var step in cell.Steps)
            {
                var program = ResolvePayloadProgram(payloadRoot, step);
                if (program is not null && File.Exists(program) && new FileInfo(program).LinkTarget is null)
                {
                    executableFiles.Add(program);
                }
            }
        }

        return executableFiles;
    }

    private static string? ResolvePayloadProgram(string payloadRoot, ResolvedVivariumStep step)
    {
        if (IsPortableRooted(step.Program) || step.Program.Contains('\\'))
        {
            return null;
        }

        var relativeProgram = step.WorkingDirectory is "" or "."
            ? step.Program
            : step.WorkingDirectory.TrimEnd('/') + "/" + step.Program;
        var platformPath = relativeProgram.Replace('/', Path.DirectorySeparatorChar);
        var candidate = Path.GetFullPath(Path.Combine(payloadRoot, platformPath));
        var relativeToRoot = Path.GetRelativePath(payloadRoot, candidate);
        if (Path.IsPathRooted(relativeToRoot) ||
            relativeToRoot.Equals("..", StringComparison.Ordinal) ||
            relativeToRoot.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            return null;
        }

        return candidate;
    }

    private static bool MayTargetUnix(string? runtimeIdentifier) =>
        // Without an RID the selector may still choose Unix; executable metadata is harmless on Windows.
        runtimeIdentifier is null ||
        runtimeIdentifier.StartsWith("linux-", StringComparison.Ordinal) ||
        runtimeIdentifier.StartsWith("osx-", StringComparison.Ordinal);

    private static bool IsPortableRooted(string value) =>
        value.StartsWith("/", StringComparison.Ordinal) ||
        value.StartsWith("\\", StringComparison.Ordinal) ||
        (value.Length >= 2 && char.IsAsciiLetter(value[0]) && value[1] == ':');

    private sealed class TemporaryPayloadArchives(
        string temporaryRoot,
        IReadOnlyDictionary<string, PayloadArchiveInfo> archives) : ITemporaryPayloadArchives
    {
        public IReadOnlyDictionary<string, PayloadArchiveInfo> Archives { get; } = archives;

        public ValueTask DisposeAsync()
        {
            DeleteBestEffort(temporaryRoot);
            return ValueTask.CompletedTask;
        }
    }

    private static void DeleteBestEffort(string temporaryRoot)
    {
        try
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Archives contain no credentials; a locked temp file can be reclaimed by the OS later.
        }
    }
}
