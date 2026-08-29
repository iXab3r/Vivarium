namespace Vivarium.Controller.ResultAdapters.Trx;

public sealed record TrxAdapterLimits(
    long MaxInputBytes = 8 * 1024 * 1024,
    int MaxXmlElements = 100_000,
    int MaxXmlDepth = 64,
    int MaxAttributesPerElement = 128,
    int MaxTestDefinitions = 50_000,
    int MaxOccurrences = 50_000,
    int MaxAttachmentsPerOccurrence = 128,
    int MaxAttributeCharacters = 4 * 1024,
    int MaxTextCharacters = 64 * 1024,
    int MaxWarnings = 1_024)
{
    internal void Validate()
    {
        RequireRange(MaxInputBytes, 1, 64L * 1024 * 1024, nameof(MaxInputBytes));
        RequireRange(MaxXmlElements, 1, 1_000_000, nameof(MaxXmlElements));
        RequireRange(MaxXmlDepth, 1, 128, nameof(MaxXmlDepth));
        RequireRange(MaxAttributesPerElement, 1, 512, nameof(MaxAttributesPerElement));
        RequireRange(MaxTestDefinitions, 1, 250_000, nameof(MaxTestDefinitions));
        RequireRange(MaxOccurrences, 1, 250_000, nameof(MaxOccurrences));
        RequireRange(MaxAttachmentsPerOccurrence, 1, 1_024, nameof(MaxAttachmentsPerOccurrence));
        RequireRange(MaxAttributeCharacters, 1, 64 * 1024, nameof(MaxAttributeCharacters));
        RequireRange(MaxTextCharacters, 1, 1024 * 1024, nameof(MaxTextCharacters));
        RequireRange(MaxWarnings, 1, 10_000, nameof(MaxWarnings));
    }

    private static void RequireRange(long value, long minimum, long maximum, string name)
    {
        if (value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(
                name,
                value,
                $"TRX adapter limit must be between {minimum} and {maximum}");
        }
    }
}

public sealed record TrxProjectionContext(
    string BuildId,
    string ProjectId,
    string TestSourceId,
    string RawArtifactId,
    string RawArtifactPath);

public enum TrxNormalizedOutcome
{
    Passed,
    Failed,
    Skipped,
    Ignored,
    Inconclusive,
    Aborted,
    NotRun,
    Unknown,
}

public enum TrxTestIdentityQuality
{
    Stable,
    Fallback,
}

public sealed record TrxTextProjection(
    string Value,
    int OriginalCharacterCount,
    bool Truncated);

public sealed record TrxRawSourceLocation(
    string ArtifactId,
    string ArtifactPath,
    int? Line,
    int? Column);

public sealed record TrxProjectionWarning(
    string Code,
    string Summary,
    TrxRawSourceLocation Location);

public sealed record TrxTestRunProjection(
    string? RunId,
    string? Name,
    string? NativeOutcome,
    TrxNormalizedOutcome Outcome,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    IReadOnlyDictionary<string, long> Counters,
    IReadOnlyDictionary<string, string> ProducerHints,
    TrxTextProjection? StandardOutput,
    TrxRawSourceLocation Source);

public sealed record TrxTestProjection(
    string TestId,
    TrxTestIdentityQuality IdentityQuality,
    int IdentityAlgorithmVersion,
    string? ProducerTestId,
    string? ClassName,
    string? MethodName,
    string? FullyQualifiedName,
    string? Source,
    string? AdapterTypeName);

public sealed record TrxAttachmentProjection(
    string Kind,
    string PathOrUri,
    TrxRawSourceLocation Source);

public sealed record TrxTestOccurrenceProjection(
    string OccurrenceId,
    string TestId,
    int AttemptOrdinal,
    int ResultOrdinal,
    string NativeResultType,
    string? ProducerTestId,
    string? ExecutionId,
    string? ParentExecutionId,
    string? DisplayName,
    string? ParameterDisplay,
    string? NativeOutcome,
    TrxNormalizedOutcome Outcome,
    long? DurationTicks,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    string? TestHost,
    string? RelativeResultsDirectory,
    string? DataRowInfo,
    TrxTextProjection? StandardOutput,
    TrxTextProjection? StandardError,
    TrxTextProjection? ErrorMessage,
    TrxTextProjection? StackTrace,
    IReadOnlyList<TrxAttachmentProjection> Attachments,
    IReadOnlyDictionary<string, string> NativeAttributes,
    TrxRawSourceLocation Source);

public sealed record TrxResultProjection(
    string AdapterId,
    string AdapterVersion,
    int ProjectionSchemaVersion,
    TrxProjectionContext Context,
    TrxTestRunProjection Run,
    IReadOnlyList<TrxTestProjection> Tests,
    IReadOnlyList<TrxTestOccurrenceProjection> Occurrences,
    IReadOnlyList<TrxProjectionWarning> Warnings,
    int SuppressedWarningCount);

public sealed class TrxProjectionException : Exception
{
    public TrxProjectionException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public TrxProjectionException(string code, string message, Exception innerException)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}
