using System.Collections.ObjectModel;

namespace Vivarium.Controller.Configuration.Git;

public sealed record ConfigurationRevision
{
    public ConfigurationRevision(string repositoryId, string commit)
    {
        RepositoryId = ConfigurationGitIdentifiers.NormalizeRepositoryId(repositoryId);
        Commit = ConfigurationGitIdentifiers.NormalizeObjectId(commit, nameof(commit));
    }

    public string RepositoryId { get; }

    public string Commit { get; }

    public string Canonical => $"{RepositoryId}@{Commit}";

    public override string ToString() => Canonical;
}

public sealed record ConfigurationCommitProvenance(
    string OperationId,
    string RequestId,
    string CorrelationId,
    string ActorType,
    string ActorId);

public sealed record ConfigurationRevisionDescriptor(
    ConfigurationRevision Revision,
    string TreeHash,
    string AggregateContentHash,
    string SchemaVersion,
    IReadOnlyList<ConfigurationRevision> Parents,
    ConfigurationCommitProvenance? ControllerProvenance);

public sealed record ValidatedConfigurationDocument
{
    public ValidatedConfigurationDocument(
        string path,
        string apiVersion,
        string kind,
        string id,
        string contentHash,
        ReadOnlyMemory<byte> utf8Bytes,
        IReadOnlyDictionary<string, string> scalarFields)
    {
        Path = path;
        ApiVersion = apiVersion;
        Kind = kind;
        Id = id;
        ContentHash = contentHash;
        Utf8Bytes = utf8Bytes.ToArray();
        var copiedFields = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in scalarFields)
        {
            copiedFields.Add(key, value);
        }

        ScalarFields = new ReadOnlyDictionary<string, string>(copiedFields);
    }

    public string Path { get; }

    public string ApiVersion { get; }

    public string Kind { get; }

    public string Id { get; }

    public string ContentHash { get; }

    public ReadOnlyMemory<byte> Utf8Bytes { get; }

    public IReadOnlyDictionary<string, string> ScalarFields { get; }
}

public sealed record ValidatedConfigurationRevision(
    ConfigurationRevisionDescriptor Descriptor,
    IReadOnlyList<ValidatedConfigurationDocument> Documents);

public sealed record ConfigurationValidationDiagnostic(
    string Code,
    string? Path,
    string? Field,
    string Summary);

public sealed record ConfigurationRevisionValidation(
    ConfigurationRevision Revision,
    string? TreeHash,
    ValidatedConfigurationRevision? Validated,
    IReadOnlyList<ConfigurationValidationDiagnostic> Diagnostics)
{
    public bool IsValid => Validated is not null && Diagnostics.Count == 0;
}

public sealed record ConfigurationCommitActor(
    string SubjectId,
    string ActorType,
    string DisplayName,
    string? Email = null);

public sealed record ConfigurationCommitMetadata(
    string Summary,
    string OperationId,
    string RequestId,
    string CorrelationId,
    ConfigurationCommitActor Actor);

public sealed record ConfigurationDocumentMutation(
    ConfigurationRevision ExpectedBase,
    string Path,
    ReadOnlyMemory<byte> Utf8Bytes,
    ConfigurationCommitMetadata Commit);

public sealed record ConfigurationDocumentUpsert(
    string Path,
    ReadOnlyMemory<byte> Utf8Bytes);

public sealed record ConfigurationTreeMutation(
    ConfigurationRevision ExpectedBase,
    IReadOnlyList<ConfigurationDocumentUpsert> Upserts,
    ConfigurationCommitMetadata Commit);

public enum ConfigurationCommitOutcome
{
    Committed,
    Unchanged,
    Conflict,
    Rejected,
}

public enum ConfigurationPathChangeKind
{
    Added,
    Modified,
    Removed,
    Unchanged,
}

public sealed record ConfigurationPathDiff(
    string Path,
    ConfigurationPathChangeKind ChangeKind,
    string? PreviousContentHash,
    string? ResultContentHash);

public sealed record ConfigurationCommitResult(
    ConfigurationCommitOutcome Outcome,
    ConfigurationRevision ExpectedBase,
    ConfigurationRevision CurrentRevision,
    ConfigurationRevision? ResultRevision,
    string? CandidateAggregateContentHash,
    IReadOnlyList<ConfigurationPathDiff> Diff,
    IReadOnlyList<ConfigurationValidationDiagnostic> Diagnostics);

public interface IConfigurationRepository
{
    string RepositoryId { get; }

    Task<ConfigurationRevision> GetAuthoritativeHeadAsync(
        CancellationToken cancellationToken = default);

    Task<ConfigurationRevisionValidation> ValidateRevisionAsync(
        ConfigurationRevision revision,
        CancellationToken cancellationToken = default);

    Task<ConfigurationCommitResult> UpsertDocumentAsync(
        ConfigurationDocumentMutation mutation,
        CancellationToken cancellationToken = default);

    Task<ConfigurationCommitResult> UpsertDocumentsAsync(
        ConfigurationTreeMutation mutation,
        CancellationToken cancellationToken = default) =>
        mutation.Upserts.Count == 1
            ? UpsertDocumentAsync(
                new ConfigurationDocumentMutation(
                    mutation.ExpectedBase,
                    mutation.Upserts[0].Path,
                    mutation.Upserts[0].Utf8Bytes,
                    mutation.Commit),
                cancellationToken)
            : throw new NotSupportedException(
                "This configuration repository does not support atomic multi-document mutation.");
}

public sealed class ConfigurationRepositoryException : Exception
{
    public ConfigurationRepositoryException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public ConfigurationRepositoryException(string code, string message, Exception innerException)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}

internal static class ConfigurationGitIdentifiers
{
    public static string NormalizeRepositoryId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 64 ||
            !(IsAsciiLower(value[0]) || char.IsAsciiDigit(value[0])) ||
            !(IsAsciiLower(value[^1]) || char.IsAsciiDigit(value[^1])) ||
            value.Any(character =>
                !(IsAsciiLower(character) || char.IsAsciiDigit(character) || character is '.' or '-')))
        {
            throw new ArgumentException(
                "Repository IDs must be 1-64 lowercase ASCII letters, digits, dots, or hyphens.",
                nameof(value));
        }

        return value;
    }

    private static bool IsAsciiLower(char value) => value is >= 'a' and <= 'z';

    public static string NormalizeObjectId(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if ((value.Length is not 40 and not 64) ||
            value.Any(character => !char.IsAsciiHexDigit(character)))
        {
            throw new ArgumentException(
                "Git object IDs must be complete 40- or 64-character hexadecimal values.",
                parameterName);
        }

        return value.ToLowerInvariant();
    }
}
