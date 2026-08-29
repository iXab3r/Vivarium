using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Vivarium.Controller.Configuration.Git;

public sealed class ManagedGitRepository : IConfigurationRepository
{
    private const string AuthoritativeRef = "refs/heads/main";
    private const string CheckoutMarkerName = "vivarium-checkout-sync.json";
    private const string GatewayLockName = "vivarium-gateway.lock";
    private const int MaxCommitMetadataBytes = 64 * 1024;

    private readonly string repositoryPath;
    private readonly string gitDirectory;
    private readonly GitCommandRunner git;
    private readonly ConfigurationTreeValidator validator;
    private readonly SemaphoreSlim operationGate = new(1, 1);

    private ManagedGitRepository(string repositoryPath, string repositoryId)
    {
        this.repositoryPath = repositoryPath;
        RepositoryId = repositoryId;
        gitDirectory = Path.Combine(repositoryPath, ".git");
        git = new GitCommandRunner(repositoryPath);
        validator = new ConfigurationTreeValidator(repositoryId);
    }

    public string RepositoryId { get; }

    public static async Task<ManagedGitRepository> OpenOrCreateAsync(
        string repositoryPath,
        string repositoryId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        var normalizedId = ConfigurationGitIdentifiers.NormalizeRepositoryId(repositoryId);
        var fullPath = Path.GetFullPath(repositoryPath);
        Directory.CreateDirectory(fullPath);

        var gitPath = Path.Combine(fullPath, ".git");
        if (File.Exists(gitPath))
        {
            throw new ConfigurationRepositoryException(
                "CONFIG_REPOSITORY_UNSUPPORTED",
                "Managed-local configuration requires a normal primary Git checkout, not a linked worktree.");
        }

        if (!Directory.Exists(gitPath))
        {
            if (Directory.EnumerateFileSystemEntries(fullPath).Any())
            {
                throw new ConfigurationRepositoryException(
                    "CONFIG_REPOSITORY_NOT_EMPTY",
                    "A new managed-local configuration repository must start in an empty directory.");
            }

            var initializer = new GitCommandRunner(fullPath);
            await initializer.RunAsync(
                ["init", "--initial-branch=main", "."],
                cancellationToken: cancellationToken);
        }

        var resolver = new GitCommandRunner(fullPath);
        var prefix = await resolver.RunAsync(
            ["rev-parse", "--show-prefix"],
            cancellationToken: cancellationToken);
        if (prefix.Length != 0)
        {
            throw new ConfigurationRepositoryException(
                "CONFIG_REPOSITORY_UNSUPPORTED",
                "The configured path must be the root of its Git checkout.");
        }

        var canonicalRoot = Path.GetFullPath(await resolver.RunAsync(
            ["rev-parse", "--show-toplevel"],
            cancellationToken: cancellationToken));
        var repository = new ManagedGitRepository(canonicalRoot, normalizedId);
        await repository.WithRepositoryLockAsync(
            async () =>
            {
                await repository.InitializeAsync(cancellationToken);
                return true;
            },
            cancellationToken);
        return repository;
    }

    public Task<ConfigurationRevision> GetAuthoritativeHeadAsync(
        CancellationToken cancellationToken = default) =>
        WithRepositoryLockAsync(
            async () =>
            {
                await RecoverCheckoutAsync(cancellationToken);
                return await GetRequiredHeadAsync(cancellationToken);
            },
            cancellationToken);

    public Task<ConfigurationRevisionValidation> ValidateRevisionAsync(
        ConfigurationRevision revision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(revision);
        return WithRepositoryLockAsync(
            () => ValidateRevisionCoreAsync(revision, cancellationToken),
            cancellationToken);
    }

    public Task<ConfigurationCommitResult> UpsertDocumentAsync(
        ConfigurationDocumentMutation mutation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        var ownedBytes = mutation.Utf8Bytes.ToArray();
        var ownedMutation = mutation with { Utf8Bytes = ownedBytes };
        return WithRepositoryLockAsync(
            () => UpsertDocumentCoreAsync(ownedMutation, cancellationToken),
            cancellationToken);
    }

    public Task<ConfigurationCommitResult> UpsertDocumentsAsync(
        ConfigurationTreeMutation mutation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        var owned = mutation.Upserts
            .Select(upsert => upsert with { Utf8Bytes = upsert.Utf8Bytes.ToArray() })
            .ToArray();
        var ownedMutation = mutation with { Upserts = owned };
        return WithRepositoryLockAsync(
            () => UpsertDocumentsCoreAsync(ownedMutation, cancellationToken),
            cancellationToken);
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var isBare = await git.RunAsync(
            ["rev-parse", "--is-bare-repository"],
            cancellationToken: cancellationToken);
        if (!string.Equals(isBare, "false", StringComparison.Ordinal))
        {
            throw new ConfigurationRepositoryException(
                "CONFIG_REPOSITORY_UNSUPPORTED",
                "Managed-local configuration requires a normal non-bare Git repository.");
        }

        await RecoverCheckoutAsync(cancellationToken);
        var head = await TryGetHeadAsync(cancellationToken);
        if (head is null)
        {
            await CreateBaselineAsync(cancellationToken);
        }

        var symbolicHead = await git.RunAsync(
            ["symbolic-ref", "--quiet", "HEAD"],
            allowFailure: true,
            cancellationToken: cancellationToken);
        if (!string.Equals(symbolicHead, AuthoritativeRef, StringComparison.Ordinal))
        {
            throw new ConfigurationRepositoryException(
                "CONFIG_REPOSITORY_BRANCH",
                "Managed-local configuration must have main checked out as its authoritative branch.");
        }

        // Existing authoritative commits are intentionally not validated as an open precondition.
        // Reconciliation must be able to inspect an invalid human-authored HEAD after restart and
        // retain its durable last-known-good projection. The newly-created baseline is validated
        // before any Git object is made authoritative in CreateBaselineAsync.
    }

    private async Task CreateBaselineAsync(CancellationToken cancellationToken)
    {
        await EnsureCheckoutEmptyAsync(cancellationToken);
        var manifest = ConfigurationTreeValidator.RenderRepositoryManifest(RepositoryId);
        var treeValidation = validator.Validate(
        [
            new ConfigurationTreeDocument(
                ConfigurationTreeValidator.RepositoryManifestPath,
                "100644",
                manifest,
                manifest.Length),
        ]);
        if (!treeValidation.IsValid)
        {
            throw new ConfigurationRepositoryException(
                "CONFIG_REPOSITORY_BASELINE_INVALID",
                "The canonical baseline configuration could not be validated.");
        }

        var blob = await git.RunAsync(
            ["hash-object", "-w", "--stdin"],
            standardInput: manifest,
            cancellationToken: cancellationToken);
        var tree = await BuildCandidateTreeAsync(
            baseCommit: null,
            ConfigurationTreeValidator.RepositoryManifestPath,
            blob,
            cancellationToken);
        var operationId = Guid.NewGuid().ToString("D");
        var metadata = NormalizeCommitMetadata(new ConfigurationCommitMetadata(
            "Initialize Vivarium configuration repository",
            operationId,
            operationId,
            operationId,
            new ConfigurationCommitActor("vivarium-controller", "system", "Vivarium Controller")));
        var commit = await CreateCommitAsync(tree, parent: null, metadata, cancellationToken);

        await WriteCheckoutMarkerAsync(expectedCommit: null, commit, cancellationToken);
        var zeroId = new string('0', commit.Length);
        await git.RunAsync(
            ["update-ref", AuthoritativeRef, commit, zeroId],
            cancellationToken: cancellationToken);
        await git.RunAsync(
            ["symbolic-ref", "HEAD", AuthoritativeRef],
            cancellationToken: cancellationToken);
        await SynchronizeCheckoutAsync(
            commit,
            expectedCommit: null,
            cancellationToken: cancellationToken);
        DeleteCheckoutMarker();
    }

    private async Task<ConfigurationCommitResult> UpsertDocumentCoreAsync(
        ConfigurationDocumentMutation mutation,
        CancellationToken cancellationToken)
    {
        await RecoverCheckoutAsync(cancellationToken);
        var current = await GetRequiredHeadAsync(cancellationToken);
        var metadataDiagnostics = ValidateCommitMetadata(mutation.Commit);
        if (metadataDiagnostics.Count > 0)
        {
            return Rejected(mutation.ExpectedBase, current, metadataDiagnostics);
        }

        NormalizedCommitMetadata metadata;
        try
        {
            metadata = NormalizeCommitMetadata(mutation.Commit);
        }
        catch (ArgumentException)
        {
            return Rejected(
                mutation.ExpectedBase,
                current,
                [SafeDiagnostic(
                    "CONFIG_COMMIT_METADATA_INVALID",
                    null,
                    null,
                    "Commit metadata contains an unsupported value.")]);
        }

        if (!string.Equals(
                mutation.ExpectedBase.RepositoryId,
                RepositoryId,
                StringComparison.Ordinal))
        {
            return Rejected(
                mutation.ExpectedBase,
                current,
                [SafeDiagnostic(
                    "CONFIG_REPOSITORY_ID_MISMATCH",
                    null,
                    null,
                    "The expected revision belongs to a different configuration repository.")]);
        }

        var normalization = validator.NormalizeMutationDocument(mutation.Path, mutation.Utf8Bytes);
        if (!normalization.IsValid)
        {
            return Rejected(
                mutation.ExpectedBase,
                current,
                normalization.Diagnostics);
        }

        var expectedValidation = await ValidateRevisionCoreAsync(
            mutation.ExpectedBase,
            cancellationToken);
        if (!expectedValidation.IsValid)
        {
            return Rejected(
                mutation.ExpectedBase,
                current,
                [SafeDiagnostic(
                    "CONFIG_EXPECTED_REVISION_INVALID",
                    null,
                    null,
                    "The expected configuration revision is missing or invalid.")]);
        }

        var expectedDocuments = expectedValidation.Validated!.Documents;
        var previous = expectedDocuments.FirstOrDefault(document =>
            string.Equals(document.Path, normalization.Path, StringComparison.Ordinal));
        var changeKind = previous is null
            ? ConfigurationPathChangeKind.Added
            : string.Equals(previous.ContentHash, normalization.ContentHash, StringComparison.Ordinal)
                ? ConfigurationPathChangeKind.Unchanged
                : ConfigurationPathChangeKind.Modified;
        var proposalDiff = new[]
        {
            new ConfigurationPathDiff(
                normalization.Path!,
                changeKind,
                previous?.ContentHash,
                normalization.ContentHash),
        };

        var candidateDocuments = expectedDocuments
            .Where(document => !string.Equals(
                document.Path,
                normalization.Path,
                StringComparison.Ordinal))
            .Select(document => new ConfigurationTreeDocument(
                document.Path,
                "100644",
                document.Utf8Bytes.ToArray(),
                document.Utf8Bytes.Length))
            .Append(new ConfigurationTreeDocument(
                normalization.Path!,
                "100644",
                normalization.CanonicalBytes.ToArray(),
                normalization.CanonicalBytes.Length))
            .OrderBy(document => document.Path, StringComparer.Ordinal)
            .ToArray();
        var candidateValidation = validator.Validate(candidateDocuments);
        if (!candidateValidation.IsValid)
        {
            return Rejected(
                mutation.ExpectedBase,
                current,
                candidateValidation.Diagnostics);
        }

        var candidateAggregateHash = AggregateContentHash(candidateValidation.Documents);

        if (!string.Equals(
                current.Commit,
                mutation.ExpectedBase.Commit,
                StringComparison.Ordinal))
        {
            var conflictDiff = await ReadChangedPathsAsync(
                mutation.ExpectedBase,
                current,
                cancellationToken);
            return new ConfigurationCommitResult(
                ConfigurationCommitOutcome.Conflict,
                mutation.ExpectedBase,
                current,
                null,
                candidateAggregateHash,
                conflictDiff,
                [SafeDiagnostic(
                    "CONFIG_BASE_CONFLICT",
                    conflictDiff.FirstOrDefault()?.Path,
                    null,
                    "The authoritative repository head changed after the expected revision was read.")]);
        }

        await EnsureCleanHeadAsync(cancellationToken);
        if (changeKind == ConfigurationPathChangeKind.Unchanged)
        {
            return new ConfigurationCommitResult(
                ConfigurationCommitOutcome.Unchanged,
                mutation.ExpectedBase,
                current,
                current,
                candidateAggregateHash,
                proposalDiff,
                []);
        }

        var blob = await git.RunAsync(
            ["hash-object", "-w", "--stdin"],
            standardInput: normalization.CanonicalBytes,
            cancellationToken: cancellationToken);
        var tree = await BuildCandidateTreeAsync(
            mutation.ExpectedBase.Commit,
            normalization.Path!,
            blob,
            cancellationToken);
        var commit = await CreateCommitAsync(
            tree,
            mutation.ExpectedBase.Commit,
            metadata,
            cancellationToken);
        var resultRevision = new ConfigurationRevision(RepositoryId, commit);
        var committedValidation = await ValidateRevisionCoreAsync(resultRevision, cancellationToken);
        if (!committedValidation.IsValid)
        {
            throw new ConfigurationRepositoryException(
                "CONFIG_COMMITTED_REVISION_INVALID",
                "The exact candidate Git commit failed validation and was not made authoritative.");
        }

        await EnsureCleanHeadAsync(cancellationToken);
        await WriteCheckoutMarkerAsync(mutation.ExpectedBase.Commit, commit, cancellationToken);
        try
        {
            await git.RunAsync(
                ["update-ref", AuthoritativeRef, commit, mutation.ExpectedBase.Commit],
                cancellationToken: cancellationToken);
        }
        catch (ConfigurationRepositoryException exception)
            when (exception.Code == "CONFIG_GIT_COMMAND_FAILED")
        {
            DeleteCheckoutMarker();
            var movedHead = await GetRequiredHeadAsync(cancellationToken);
            if (!string.Equals(movedHead.Commit, mutation.ExpectedBase.Commit, StringComparison.Ordinal))
            {
                var conflictDiff = await ReadChangedPathsAsync(
                    mutation.ExpectedBase,
                    movedHead,
                    cancellationToken);
                return new ConfigurationCommitResult(
                    ConfigurationCommitOutcome.Conflict,
                    mutation.ExpectedBase,
                    movedHead,
                    null,
                    candidateAggregateHash,
                    conflictDiff,
                    [SafeDiagnostic(
                        "CONFIG_BASE_CONFLICT",
                        conflictDiff.FirstOrDefault()?.Path,
                        null,
                        "The authoritative repository head changed while the commit was being published.")]);
            }

            throw;
        }

        await SynchronizeCheckoutAsync(commit, mutation.ExpectedBase.Commit, cancellationToken);
        DeleteCheckoutMarker();
        return new ConfigurationCommitResult(
            ConfigurationCommitOutcome.Committed,
            mutation.ExpectedBase,
            resultRevision,
            resultRevision,
            candidateAggregateHash,
            proposalDiff,
            []);
    }

    private async Task<ConfigurationCommitResult> UpsertDocumentsCoreAsync(
        ConfigurationTreeMutation mutation,
        CancellationToken cancellationToken)
    {
        await RecoverCheckoutAsync(cancellationToken);
        var current = await GetRequiredHeadAsync(cancellationToken);
        if (mutation.Upserts.Count is < 1 or > 32)
        {
            return Rejected(
                mutation.ExpectedBase,
                current,
                [SafeDiagnostic(
                    "CONFIG_MUTATION_DOCUMENT_COUNT",
                    null,
                    null,
                    "An atomic configuration mutation must contain 1-32 documents.")]);
        }

        var metadataDiagnostics = ValidateCommitMetadata(mutation.Commit);
        if (metadataDiagnostics.Count > 0)
        {
            return Rejected(mutation.ExpectedBase, current, metadataDiagnostics);
        }

        NormalizedCommitMetadata metadata;
        try
        {
            metadata = NormalizeCommitMetadata(mutation.Commit);
        }
        catch (ArgumentException)
        {
            return Rejected(
                mutation.ExpectedBase,
                current,
                [SafeDiagnostic(
                    "CONFIG_COMMIT_METADATA_INVALID",
                    null,
                    null,
                    "Commit metadata contains an unsupported value.")]);
        }

        if (!string.Equals(
                mutation.ExpectedBase.RepositoryId,
                RepositoryId,
                StringComparison.Ordinal))
        {
            return Rejected(
                mutation.ExpectedBase,
                current,
                [SafeDiagnostic(
                    "CONFIG_REPOSITORY_ID_MISMATCH",
                    null,
                    null,
                    "The expected revision belongs to a different configuration repository.")]);
        }

        var normalized = mutation.Upserts
            .Select(upsert => validator.NormalizeMutationDocument(upsert.Path, upsert.Utf8Bytes))
            .ToArray();
        var normalizationDiagnostics = normalized.SelectMany(item => item.Diagnostics).Take(64).ToArray();
        if (normalizationDiagnostics.Length > 0)
        {
            return Rejected(mutation.ExpectedBase, current, normalizationDiagnostics);
        }

        var duplicatePath = normalized
            .GroupBy(item => item.Path!, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Skip(1).Any())?.Key;
        if (duplicatePath is not null)
        {
            return Rejected(
                mutation.ExpectedBase,
                current,
                [SafeDiagnostic(
                    "CONFIG_MUTATION_DUPLICATE_PATH",
                    duplicatePath,
                    null,
                    "An atomic configuration mutation may address each path only once.")]);
        }

        var expectedValidation = await ValidateRevisionCoreAsync(
            mutation.ExpectedBase,
            cancellationToken);
        if (!expectedValidation.IsValid)
        {
            return Rejected(
                mutation.ExpectedBase,
                current,
                [SafeDiagnostic(
                    "CONFIG_EXPECTED_REVISION_INVALID",
                    null,
                    null,
                    "The expected configuration revision is missing or invalid.")]);
        }

        var expectedDocuments = expectedValidation.Validated!.Documents;
        var replacements = normalized.ToDictionary(item => item.Path!, StringComparer.Ordinal);
        var proposalDiff = normalized
            .OrderBy(item => item.Path, StringComparer.Ordinal)
            .Select(item =>
            {
                var previous = expectedDocuments.FirstOrDefault(document =>
                    string.Equals(document.Path, item.Path, StringComparison.Ordinal));
                var kind = previous is null
                    ? ConfigurationPathChangeKind.Added
                    : string.Equals(previous.ContentHash, item.ContentHash, StringComparison.Ordinal)
                        ? ConfigurationPathChangeKind.Unchanged
                        : ConfigurationPathChangeKind.Modified;
                return new ConfigurationPathDiff(
                    item.Path!, kind, previous?.ContentHash, item.ContentHash);
            })
            .ToArray();
        var candidateDocuments = expectedDocuments
            .Where(document => !replacements.ContainsKey(document.Path))
            .Select(document => new ConfigurationTreeDocument(
                document.Path,
                "100644",
                document.Utf8Bytes.ToArray(),
                document.Utf8Bytes.Length))
            .Concat(normalized.Select(item => new ConfigurationTreeDocument(
                item.Path!,
                "100644",
                item.CanonicalBytes.ToArray(),
                item.CanonicalBytes.Length)))
            .OrderBy(document => document.Path, StringComparer.Ordinal)
            .ToArray();
        var candidateValidation = validator.Validate(candidateDocuments);
        if (!candidateValidation.IsValid)
        {
            return Rejected(
                mutation.ExpectedBase,
                current,
                candidateValidation.Diagnostics);
        }

        var candidateAggregateHash = AggregateContentHash(candidateValidation.Documents);
        if (!string.Equals(
                current.Commit,
                mutation.ExpectedBase.Commit,
                StringComparison.Ordinal))
        {
            var conflictDiff = await ReadChangedPathsAsync(
                mutation.ExpectedBase,
                current,
                cancellationToken);
            return new ConfigurationCommitResult(
                ConfigurationCommitOutcome.Conflict,
                mutation.ExpectedBase,
                current,
                null,
                candidateAggregateHash,
                conflictDiff,
                [SafeDiagnostic(
                    "CONFIG_BASE_CONFLICT",
                    conflictDiff.FirstOrDefault()?.Path,
                    null,
                    "The authoritative repository head changed after the expected revision was read.")]);
        }

        await EnsureCleanHeadAsync(cancellationToken);
        if (proposalDiff.All(item => item.ChangeKind == ConfigurationPathChangeKind.Unchanged))
        {
            return new ConfigurationCommitResult(
                ConfigurationCommitOutcome.Unchanged,
                mutation.ExpectedBase,
                current,
                current,
                candidateAggregateHash,
                proposalDiff,
                []);
        }

        var treeUpdates = new List<(string Path, string Blob)>(normalized.Length);
        foreach (var item in normalized.OrderBy(item => item.Path, StringComparer.Ordinal))
        {
            var blob = await git.RunAsync(
                ["hash-object", "-w", "--stdin"],
                standardInput: item.CanonicalBytes,
                cancellationToken: cancellationToken);
            treeUpdates.Add((item.Path!, blob));
        }

        var tree = await BuildCandidateTreeAsync(
            mutation.ExpectedBase.Commit,
            treeUpdates,
            cancellationToken);
        var commit = await CreateCommitAsync(
            tree,
            mutation.ExpectedBase.Commit,
            metadata,
            cancellationToken);
        var resultRevision = new ConfigurationRevision(RepositoryId, commit);
        var committedValidation = await ValidateRevisionCoreAsync(resultRevision, cancellationToken);
        if (!committedValidation.IsValid)
        {
            throw new ConfigurationRepositoryException(
                "CONFIG_COMMITTED_REVISION_INVALID",
                "The exact candidate Git commit failed validation and was not made authoritative.");
        }

        await EnsureCleanHeadAsync(cancellationToken);
        await WriteCheckoutMarkerAsync(mutation.ExpectedBase.Commit, commit, cancellationToken);
        try
        {
            await git.RunAsync(
                ["update-ref", AuthoritativeRef, commit, mutation.ExpectedBase.Commit],
                cancellationToken: cancellationToken);
        }
        catch (ConfigurationRepositoryException exception)
            when (exception.Code == "CONFIG_GIT_COMMAND_FAILED")
        {
            DeleteCheckoutMarker();
            var movedHead = await GetRequiredHeadAsync(cancellationToken);
            if (!string.Equals(movedHead.Commit, mutation.ExpectedBase.Commit, StringComparison.Ordinal))
            {
                var conflictDiff = await ReadChangedPathsAsync(
                    mutation.ExpectedBase,
                    movedHead,
                    cancellationToken);
                return new ConfigurationCommitResult(
                    ConfigurationCommitOutcome.Conflict,
                    mutation.ExpectedBase,
                    movedHead,
                    null,
                    candidateAggregateHash,
                    conflictDiff,
                    [SafeDiagnostic(
                        "CONFIG_BASE_CONFLICT",
                        conflictDiff.FirstOrDefault()?.Path,
                        null,
                        "The authoritative repository head changed while the commit was being published.")]);
            }

            throw;
        }

        await SynchronizeCheckoutAsync(commit, mutation.ExpectedBase.Commit, cancellationToken);
        DeleteCheckoutMarker();
        return new ConfigurationCommitResult(
            ConfigurationCommitOutcome.Committed,
            mutation.ExpectedBase,
            resultRevision,
            resultRevision,
            candidateAggregateHash,
            proposalDiff,
            []);
    }

    private async Task<ConfigurationRevisionValidation> ValidateRevisionCoreAsync(
        ConfigurationRevision revision,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(revision.RepositoryId, RepositoryId, StringComparison.Ordinal))
        {
            return UnreadableRevision(
                revision,
                "CONFIG_REPOSITORY_ID_MISMATCH",
                "The revision belongs to a different configuration repository.");
        }

        var resolved = await git.RunAsync(
            ["rev-parse", "--verify", $"{revision.Commit}^{{commit}}"],
            allowFailure: true,
            cancellationToken: cancellationToken);
        if (!string.Equals(resolved, revision.Commit, StringComparison.OrdinalIgnoreCase))
        {
            return UnreadableRevision(
                revision,
                "CONFIG_REVISION_UNREADABLE",
                "The requested Git commit is missing or unreadable.");
        }

        var treeHash = ConfigurationGitIdentifiers.NormalizeObjectId(
            await git.RunAsync(
                ["rev-parse", $"{revision.Commit}^{{tree}}"],
                cancellationToken: cancellationToken),
            "treeHash");
        var treeLoad = await LoadTreeAsync(revision.Commit, cancellationToken);
        var treeValidation = validator.Validate(treeLoad.Documents);
        var diagnostics = treeLoad.Diagnostics
            .Concat(treeValidation.Diagnostics)
            .Take(64)
            .ToList();

        var metadata = await ReadCommitMetadataAsync(revision, cancellationToken);
        diagnostics.AddRange(metadata.Diagnostics.Take(Math.Max(0, 64 - diagnostics.Count)));
        if (diagnostics.Count > 0)
        {
            return new ConfigurationRevisionValidation(
                revision,
                treeHash,
                null,
                diagnostics.ToArray());
        }

        var aggregateHash = AggregateContentHash(treeValidation.Documents);
        var descriptor = new ConfigurationRevisionDescriptor(
            revision,
            treeHash,
            aggregateHash,
            ConfigurationTreeValidator.SchemaVersion,
            metadata.Parents,
            metadata.Provenance);
        return new ConfigurationRevisionValidation(
            revision,
            treeHash,
            new ValidatedConfigurationRevision(descriptor, treeValidation.Documents),
            []);
    }

    private async Task<IReadOnlyList<ConfigurationPathDiff>> ReadChangedPathsAsync(
        ConfigurationRevision expected,
        ConfigurationRevision current,
        CancellationToken cancellationToken)
    {
        var expectedValidation = await ValidateRevisionCoreAsync(expected, cancellationToken);
        var currentValidation = await ValidateRevisionCoreAsync(current, cancellationToken);
        var expectedHashes = ContentHashes(expectedValidation);
        var currentHashes = ContentHashes(currentValidation);
        var output = await git.RunBytesAsync(
            [
                "diff", "--name-status", "--no-renames", "-z",
                expected.Commit, current.Commit, "--",
            ],
            cancellationToken: cancellationToken);
        var fields = Encoding.UTF8.GetString(output)
            .Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var result = new List<ConfigurationPathDiff>(Math.Min(fields.Length / 2, 32));
        for (var index = 0; index + 1 < fields.Length && result.Count < 32; index += 2)
        {
            var status = fields[index];
            var path = BoundSafe(fields[index + 1], 512)!;
            expectedHashes.TryGetValue(fields[index + 1], out var previousHash);
            currentHashes.TryGetValue(fields[index + 1], out var resultHash);
            var kind = status[0] switch
            {
                'A' => ConfigurationPathChangeKind.Added,
                'D' => ConfigurationPathChangeKind.Removed,
                _ => ConfigurationPathChangeKind.Modified,
            };
            result.Add(new ConfigurationPathDiff(path, kind, previousHash, resultHash));
        }

        return result;
    }

    private static IReadOnlyDictionary<string, string> ContentHashes(
        ConfigurationRevisionValidation validation) =>
        validation.Validated?.Documents.ToDictionary(
            document => document.Path,
            document => document.ContentHash,
            StringComparer.Ordinal)
        ?? new Dictionary<string, string>(StringComparer.Ordinal);

    private async Task<TreeLoadResult> LoadTreeAsync(
        string commit,
        CancellationToken cancellationToken)
    {
        var output = await git.RunBytesAsync(
            ["ls-tree", "-r", "-l", "-z", "--full-tree", commit],
            cancellationToken: cancellationToken);
        var records = Encoding.UTF8.GetString(output)
            .Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var entries = new List<TreeEntry>(Math.Min(records.Length, ConfigurationTreeValidator.MaxTreeEntries));
        var diagnostics = new List<ConfigurationValidationDiagnostic>();
        long totalBytes = 0;

        foreach (var record in records.Take(ConfigurationTreeValidator.MaxTreeEntries + 1))
        {
            var tab = record.IndexOf('\t');
            if (tab <= 0)
            {
                diagnostics.Add(SafeDiagnostic(
                    "CONFIG_TREE_ENTRY_INVALID",
                    null,
                    null,
                    "The Git tree contains an unreadable entry."));
                continue;
            }

            var header = record[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var path = record[(tab + 1)..];
            if (header.Length != 4 ||
                !long.TryParse(header[3], out var size) ||
                size < 0)
            {
                entries.Add(new TreeEntry(path, header.ElementAtOrDefault(0) ?? string.Empty, string.Empty, 0));
                continue;
            }

            totalBytes += size;
            entries.Add(new TreeEntry(path, header[0], header[2], size));
        }

        if (records.Length > ConfigurationTreeValidator.MaxTreeEntries)
        {
            diagnostics.Add(SafeDiagnostic(
                "CONFIG_TREE_ENTRY_LIMIT",
                null,
                null,
                "The configuration tree exceeds the 4096-entry limit."));
        }

        if (totalBytes > ConfigurationTreeValidator.MaxAggregateTreeBytes)
        {
            diagnostics.Add(SafeDiagnostic(
                "CONFIG_TREE_SIZE_LIMIT",
                null,
                null,
                "The configuration tree exceeds the 4 MiB aggregate limit."));
        }

        if (diagnostics.Count > 0)
        {
            return new TreeLoadResult([], diagnostics);
        }

        var documents = new List<ConfigurationTreeDocument>(entries.Count);
        foreach (var entry in entries.OrderBy(entry => entry.Path, StringComparer.Ordinal))
        {
            ReadOnlyMemory<byte> bytes = ReadOnlyMemory<byte>.Empty;
            if (entry.Path.StartsWith(".vivarium/", StringComparison.Ordinal) &&
                entry.Size <= 64 * 1024 &&
                string.Equals(entry.Mode, "100644", StringComparison.Ordinal))
            {
                bytes = await git.RunBytesAsync(
                    ["cat-file", "blob", entry.ObjectId],
                    cancellationToken: cancellationToken);
            }

            documents.Add(new ConfigurationTreeDocument(
                entry.Path,
                entry.Mode,
                bytes,
                entry.Size));
        }

        return new TreeLoadResult(documents, diagnostics);
    }

    private async Task<CommitMetadataRead> ReadCommitMetadataAsync(
        ConfigurationRevision revision,
        CancellationToken cancellationToken)
    {
        var parentsLine = await git.RunAsync(
            ["rev-list", "--parents", "-n", "1", revision.Commit],
            cancellationToken: cancellationToken);
        var parentIds = parentsLine.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1);
        var parents = parentIds
            .Select(parent => new ConfigurationRevision(RepositoryId, parent))
            .ToArray();

        var commitSizeText = await git.RunAsync(
            ["cat-file", "-s", revision.Commit],
            cancellationToken: cancellationToken);
        if (!long.TryParse(commitSizeText, out var commitSize) || commitSize > MaxCommitMetadataBytes)
        {
            return new CommitMetadataRead(
                parents,
                null,
                [SafeDiagnostic(
                    "CONFIG_COMMIT_METADATA_TOO_LARGE",
                    null,
                    null,
                    "The Git commit metadata exceeds the 64 KiB validation limit.")]);
        }

        var message = await git.RunAsync(
            ["show", "-s", "--format=%B", revision.Commit],
            cancellationToken: cancellationToken);
        if (ContainsSecretMaterial(message))
        {
            return new CommitMetadataRead(
                parents,
                null,
                [SafeDiagnostic(
                    "CONFIG_COMMIT_SECRET_FORBIDDEN",
                    null,
                    null,
                    "Git commit metadata must not contain credential material.")]);
        }

        var trailers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var line in message.Split('\n'))
        {
            foreach (var key in ControllerTrailerKeys)
            {
                var prefix = key + ": ";
                if (line.StartsWith(prefix, StringComparison.Ordinal))
                {
                    if (!trailers.TryGetValue(key, out var values))
                    {
                        values = [];
                        trailers.Add(key, values);
                    }

                    values.Add(line[prefix.Length..]);
                }
            }
        }

        if (ControllerTrailerKeys.Any(key =>
                !trailers.TryGetValue(key, out var values) ||
                values.Count != 1 ||
                !IsSafeIdentifier(values[0], 256)))
        {
            return new CommitMetadataRead(parents, null, []);
        }

        var actorType = trailers["Vivarium-Actor-Type"][0];
        if (actorType is not ("user" or "service" or "system"))
        {
            return new CommitMetadataRead(parents, null, []);
        }

        return new CommitMetadataRead(
            parents,
            new ConfigurationCommitProvenance(
                trailers["Vivarium-Operation-ID"][0],
                trailers["Vivarium-Request-ID"][0],
                trailers["Vivarium-Correlation-ID"][0],
                actorType,
                trailers["Vivarium-Actor-ID"][0]),
            []);
    }

    private async Task<string> BuildCandidateTreeAsync(
        string? baseCommit,
        string path,
        string blob,
        CancellationToken cancellationToken) =>
        await BuildCandidateTreeAsync(baseCommit, [(path, blob)], cancellationToken);

    private async Task<string> BuildCandidateTreeAsync(
        string? baseCommit,
        IReadOnlyList<(string Path, string Blob)> updates,
        CancellationToken cancellationToken)
    {
        var indexPath = Path.Combine(gitDirectory, $"vivarium-index-{Guid.NewGuid():N}");
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["GIT_INDEX_FILE"] = indexPath,
        };
        try
        {
            await git.RunAsync(
                baseCommit is null ? ["read-tree", "--empty"] : ["read-tree", baseCommit],
                environment,
                cancellationToken: cancellationToken);
            foreach (var (path, blob) in updates)
            {
                await git.RunAsync(
                    ["update-index", "--add", "--cacheinfo", $"100644,{blob},{path}"],
                    environment,
                    cancellationToken: cancellationToken);
            }
            return await git.RunAsync(
                ["write-tree"],
                environment,
                cancellationToken: cancellationToken);
        }
        finally
        {
            try
            {
                File.Delete(indexPath);
                File.Delete(indexPath + ".lock");
            }
            catch (IOException)
            {
                // An orphan temporary index is harmless and can be removed on maintenance.
            }
        }
    }

    private async Task<string> CreateCommitAsync(
        string tree,
        string? parent,
        NormalizedCommitMetadata metadata,
        CancellationToken cancellationToken)
    {
        var message = $"""
            {metadata.Summary}

            Vivarium-Operation-ID: {metadata.OperationId}
            Vivarium-Request-ID: {metadata.RequestId}
            Vivarium-Correlation-ID: {metadata.CorrelationId}
            Vivarium-Actor-ID: {metadata.ActorId}
            Vivarium-Actor-Type: {metadata.ActorType}

            """;
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["GIT_AUTHOR_NAME"] = metadata.DisplayName,
            ["GIT_AUTHOR_EMAIL"] = metadata.Email,
            ["GIT_COMMITTER_NAME"] = "Vivarium Controller",
            ["GIT_COMMITTER_EMAIL"] = "controller@vivarium.invalid",
        };
        var arguments = new List<string> { "commit-tree", tree };
        if (parent is not null)
        {
            arguments.Add("-p");
            arguments.Add(parent);
        }

        return await git.RunAsync(
            arguments,
            environment,
            Encoding.UTF8.GetBytes(message),
            cancellationToken: cancellationToken);
    }

    private async Task RecoverCheckoutAsync(CancellationToken cancellationToken)
    {
        var markerPath = Path.Combine(gitDirectory, CheckoutMarkerName);
        if (!File.Exists(markerPath))
        {
            return;
        }

        CheckoutSyncMarker marker;
        try
        {
            if (new FileInfo(markerPath).Length > 4096)
            {
                throw new JsonException("Marker exceeds its size limit.");
            }

            var bytes = await File.ReadAllBytesAsync(markerPath, cancellationToken);
            marker = JsonSerializer.Deserialize<CheckoutSyncMarker>(bytes)
                ?? throw new JsonException("Missing marker payload.");
            if (marker.Version != 1)
            {
                throw new JsonException("Unsupported marker version.");
            }

            _ = ConfigurationGitIdentifiers.NormalizeObjectId(marker.ResultCommit, "resultCommit");
            if (marker.ExpectedCommit is not null)
            {
                _ = ConfigurationGitIdentifiers.NormalizeObjectId(marker.ExpectedCommit, "expectedCommit");
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException or ArgumentException)
        {
            throw new ConfigurationRepositoryException(
                "CONFIG_CHECKOUT_RECOVERY_INVALID",
                "The managed repository checkout recovery marker is unreadable.",
                exception);
        }

        var head = await TryGetHeadAsync(cancellationToken);
        if (head is not null &&
            string.Equals(head.Commit, marker.ResultCommit, StringComparison.Ordinal))
        {
            if (await CheckoutMatchesAsync(marker.ResultCommit, cancellationToken))
            {
                DeleteCheckoutMarker();
                return;
            }

            var matchesExpected = marker.ExpectedCommit is null
                ? await CheckoutIsEmptyAsync(cancellationToken)
                : await CheckoutMatchesAsync(marker.ExpectedCommit, cancellationToken);
            if (!matchesExpected)
            {
                throw DirtyCheckoutException();
            }

            await SynchronizeCheckoutAsync(
                marker.ResultCommit,
                marker.ExpectedCommit,
                cancellationToken);
            DeleteCheckoutMarker();
            return;
        }

        if (marker.ExpectedCommit is not null &&
            head is not null &&
            string.Equals(head.Commit, marker.ExpectedCommit, StringComparison.Ordinal) &&
            await CheckoutMatchesAsync(marker.ExpectedCommit, cancellationToken))
        {
            DeleteCheckoutMarker();
            return;
        }

        if (marker.ExpectedCommit is null && head is null && await CheckoutIsEmptyAsync(cancellationToken))
        {
            DeleteCheckoutMarker();
            return;
        }

        throw new ConfigurationRepositoryException(
            "CONFIG_CHECKOUT_RECOVERY_CONFLICT",
            "The authoritative ref moved independently while checkout recovery was pending.");
    }

    private async Task WriteCheckoutMarkerAsync(
        string? expectedCommit,
        string resultCommit,
        CancellationToken cancellationToken)
    {
        var markerPath = Path.Combine(gitDirectory, CheckoutMarkerName);
        var temporaryPath = markerPath + ".tmp-" + Guid.NewGuid().ToString("N");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            new CheckoutSyncMarker(1, expectedCommit, resultCommit));
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, markerPath, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (IOException)
            {
                // The final marker remains authoritative if the temporary cleanup is delayed.
            }
        }
    }

    private void DeleteCheckoutMarker()
    {
        File.Delete(Path.Combine(gitDirectory, CheckoutMarkerName));
    }

    private async Task SynchronizeCheckoutAsync(
        string commit,
        string? expectedCommit,
        CancellationToken cancellationToken)
    {
        if (await CheckoutMatchesAsync(commit, cancellationToken))
        {
            return;
        }

        var matchesExpected = expectedCommit is null
            ? await CheckoutIsEmptyAsync(cancellationToken)
            : await CheckoutMatchesAsync(expectedCommit, cancellationToken);
        if (!matchesExpected)
        {
            throw DirtyCheckoutException();
        }

        // Re-prove immediately before the only destructive checkout operation. The private
        // gateway lock serializes controller writers; a human edit is never intentionally reset.
        matchesExpected = expectedCommit is null
            ? await CheckoutIsEmptyAsync(cancellationToken)
            : await CheckoutMatchesAsync(expectedCommit, cancellationToken);
        if (!matchesExpected)
        {
            throw DirtyCheckoutException();
        }

        await git.RunAsync(
            ["reset", "--hard", commit],
            cancellationToken: cancellationToken);
        if (!await CheckoutMatchesAsync(commit, cancellationToken))
        {
            throw new ConfigurationRepositoryException(
                "CONFIG_CHECKOUT_SYNC_FAILED",
                "The authoritative ref advanced, but the primary checkout could not be synchronized.");
        }
    }

    private async Task EnsureCleanHeadAsync(CancellationToken cancellationToken)
    {
        var head = await GetRequiredHeadAsync(cancellationToken);
        if (!await CheckoutMatchesAsync(head.Commit, cancellationToken))
        {
            throw DirtyCheckoutException();
        }
    }

    private async Task EnsureCheckoutEmptyAsync(CancellationToken cancellationToken)
    {
        if (!await CheckoutIsEmptyAsync(cancellationToken))
        {
            throw DirtyCheckoutException();
        }
    }

    private async Task<bool> CheckoutIsEmptyAsync(CancellationToken cancellationToken)
    {
        var tracked = await git.RunAsync(
            ["ls-files", "--cached"],
            cancellationToken: cancellationToken);
        var untracked = await git.RunAsync(
            ["ls-files", "--others"],
            cancellationToken: cancellationToken);
        return tracked.Length == 0 && untracked.Length == 0;
    }

    private async Task<bool> CheckoutMatchesAsync(
        string commit,
        CancellationToken cancellationToken)
    {
        var changed = await git.RunAsync(
            ["diff", "--name-only", "--no-ext-diff", commit, "--"],
            cancellationToken: cancellationToken);
        var untracked = await git.RunAsync(
            ["ls-files", "--others"],
            cancellationToken: cancellationToken);
        return changed.Length == 0 && untracked.Length == 0;
    }

    private async Task<ConfigurationRevision?> TryGetHeadAsync(CancellationToken cancellationToken)
    {
        var commit = await git.RunAsync(
            ["rev-parse", "--verify", $"{AuthoritativeRef}^{{commit}}"],
            allowFailure: true,
            cancellationToken: cancellationToken);
        return commit.Length == 0 ? null : new ConfigurationRevision(RepositoryId, commit);
    }

    private async Task<ConfigurationRevision> GetRequiredHeadAsync(CancellationToken cancellationToken) =>
        await TryGetHeadAsync(cancellationToken) ??
        throw new ConfigurationRepositoryException(
            "CONFIG_REPOSITORY_HEAD_MISSING",
            "The managed configuration repository has no authoritative main commit.");

    private async Task<T> WithRepositoryLockAsync<T>(
        Func<Task<T>> action,
        CancellationToken cancellationToken)
    {
        await operationGate.WaitAsync(cancellationToken);
        FileStream? repositoryLock = null;
        try
        {
            try
            {
                repositoryLock = new FileStream(
                    Path.Combine(gitDirectory, GatewayLockName),
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None);
            }
            catch (IOException exception)
            {
                throw new ConfigurationRepositoryException(
                    "CONFIG_REPOSITORY_LOCKED",
                    "Another configuration repository operation is in progress.",
                    exception);
            }

            return await action();
        }
        finally
        {
            repositoryLock?.Dispose();
            operationGate.Release();
        }
    }

    private static IReadOnlyList<ConfigurationValidationDiagnostic> ValidateCommitMetadata(
        ConfigurationCommitMetadata metadata)
    {
        if (metadata.Actor is null ||
            !IsSafeSummary(metadata.Summary) ||
            !IsSafeIdentifier(metadata.OperationId, 128) ||
            !IsSafeIdentifier(metadata.RequestId, 256) ||
            !IsSafeIdentifier(metadata.CorrelationId, 256) ||
            !IsSafeIdentifier(metadata.Actor.SubjectId, 256) ||
            metadata.Actor.ActorType is not ("user" or "service" or "system" or "setup") ||
            !IsSafeDisplayName(metadata.Actor.DisplayName) ||
            !IsSafeEmail(metadata.Actor.Email) ||
            ContainsSecretMaterial(metadata.Summary) ||
            ContainsSecretMaterial(metadata.OperationId) ||
            ContainsSecretMaterial(metadata.RequestId) ||
            ContainsSecretMaterial(metadata.CorrelationId) ||
            ContainsSecretMaterial(metadata.Actor.SubjectId) ||
            ContainsSecretMaterial(metadata.Actor.DisplayName) ||
            (metadata.Actor.Email is not null && ContainsSecretMaterial(metadata.Actor.Email)))
        {
            return
            [
                SafeDiagnostic(
                    "CONFIG_COMMIT_METADATA_INVALID",
                    null,
                    null,
                    "Commit metadata must be bounded, single-line, secret-free, and trailer-safe."),
            ];
        }

        return [];
    }

    private static NormalizedCommitMetadata NormalizeCommitMetadata(ConfigurationCommitMetadata metadata)
    {
        var diagnostics = ValidateCommitMetadata(metadata);
        if (diagnostics.Count > 0)
        {
            throw new ArgumentException("Commit metadata is invalid.", nameof(metadata));
        }

        var email = metadata.Actor.Email ??
            $"subject-{Hash(Encoding.UTF8.GetBytes(metadata.Actor.SubjectId))[..16]}@vivarium.invalid";
        return new NormalizedCommitMetadata(
            metadata.Summary,
            metadata.OperationId,
            metadata.RequestId,
            metadata.CorrelationId,
            metadata.Actor.SubjectId,
            metadata.Actor.ActorType,
            metadata.Actor.DisplayName,
            email);
    }

    private static bool IsSafeSummary(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 120 &&
        value.All(character => !char.IsControl(character)) &&
        !value.StartsWith("Vivarium-", StringComparison.Ordinal);

    private static bool IsSafeDisplayName(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 128 &&
        value.All(character => !char.IsControl(character) && character is not '<' and not '>');

    private static bool IsSafeEmail(string? value) =>
        value is null ||
        (value.Length is > 2 and <= 254 &&
         value.Count(character => character == '@') == 1 &&
         value.All(character => character is >= '!' and <= '~' && character is not '<' and not '>'));

    private static bool IsSafeIdentifier(string value, int maxLength) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maxLength &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or ':' or '@' or '/' or '-');

    private static bool ContainsSecretMaterial(string value)
    {
        var lower = value.ToLowerInvariant();
        return lower.Contains("-----begin private key-----", StringComparison.Ordinal) ||
               ContainsBearerCredential(lower) ||
               lower.Contains("password:", StringComparison.Ordinal) ||
               lower.Contains("passwd:", StringComparison.Ordinal) ||
               lower.Contains("token:", StringComparison.Ordinal) ||
               HasCredentialUrl(lower);
    }

    private static bool ContainsBearerCredential(string value)
    {
        var start = value.IndexOf("bearer ", StringComparison.Ordinal);
        while (start >= 0)
        {
            var credentialStart = start + "bearer ".Length;
            var credentialLength = 0;
            while (credentialStart + credentialLength < value.Length &&
                   value[credentialStart + credentialLength] is not (' ' or '\r' or '\n' or '\t'))
            {
                credentialLength++;
            }

            if (credentialLength >= 8)
            {
                return true;
            }

            start = value.IndexOf("bearer ", credentialStart + credentialLength, StringComparison.Ordinal);
        }

        return false;
    }

    private static bool HasCredentialUrl(string value)
    {
        foreach (var scheme in new[] { "http://", "https://", "ssh://" })
        {
            var start = value.IndexOf(scheme, StringComparison.Ordinal);
            if (start < 0)
            {
                continue;
            }

            var authorityStart = start + scheme.Length;
            var slash = value.IndexOf('/', authorityStart);
            var authorityEnd = slash < 0 ? value.Length : slash;
            var colon = value.IndexOf(':', authorityStart, authorityEnd - authorityStart);
            var at = value.IndexOf('@', authorityStart, authorityEnd - authorityStart);
            if (colon >= 0 && at > colon)
            {
                return true;
            }
        }

        return false;
    }

    private static string AggregateContentHash(
        IReadOnlyList<ValidatedConfigurationDocument> documents)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var document in documents.OrderBy(document => document.Path, StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(document.Path));
            hash.AppendData([0]);
            hash.AppendData(Encoding.ASCII.GetBytes(document.ContentHash));
            hash.AppendData([10]);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static string Hash(ReadOnlySpan<byte> value) =>
        Convert.ToHexStringLower(SHA256.HashData(value));

    private static ConfigurationCommitResult Rejected(
        ConfigurationRevision expected,
        ConfigurationRevision current,
        IReadOnlyList<ConfigurationValidationDiagnostic> diagnostics) =>
        new(
            ConfigurationCommitOutcome.Rejected,
            expected,
            current,
            null,
            null,
            [],
            diagnostics.Take(64).ToArray());

    private static ConfigurationRevisionValidation UnreadableRevision(
        ConfigurationRevision revision,
        string code,
        string summary) =>
        new(revision, null, null, [SafeDiagnostic(code, null, null, summary)]);

    private static ConfigurationValidationDiagnostic SafeDiagnostic(
        string code,
        string? path,
        string? field,
        string summary) =>
        new(
            code,
            BoundSafe(path, 512),
            BoundSafe(field, 128),
            BoundSafe(summary, 512)!);

    private static string? BoundSafe(string? value, int maxLength)
    {
        if (value is null)
        {
            return null;
        }

        return new string(value.Take(maxLength)
            .Select(character => char.IsControl(character) ? '?' : character)
            .ToArray());
    }

    private static ConfigurationRepositoryException DirtyCheckoutException() =>
        new(
            "CONFIG_REPOSITORY_DIRTY",
            "The managed repository checkout has uncommitted or untracked files; Git remains authoritative and no write was attempted.");

    private static readonly string[] ControllerTrailerKeys =
    [
        "Vivarium-Operation-ID",
        "Vivarium-Request-ID",
        "Vivarium-Correlation-ID",
        "Vivarium-Actor-ID",
        "Vivarium-Actor-Type",
    ];

    private sealed record TreeEntry(string Path, string Mode, string ObjectId, long Size);

    private sealed record TreeLoadResult(
        IReadOnlyList<ConfigurationTreeDocument> Documents,
        IReadOnlyList<ConfigurationValidationDiagnostic> Diagnostics);

    private sealed record CommitMetadataRead(
        IReadOnlyList<ConfigurationRevision> Parents,
        ConfigurationCommitProvenance? Provenance,
        IReadOnlyList<ConfigurationValidationDiagnostic> Diagnostics);

    private sealed record NormalizedCommitMetadata(
        string Summary,
        string OperationId,
        string RequestId,
        string CorrelationId,
        string ActorId,
        string ActorType,
        string DisplayName,
        string Email);

    private sealed record CheckoutSyncMarker(
        int Version,
        string? ExpectedCommit,
        string ResultCommit);
}
