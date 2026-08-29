using System.Buffers;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Vivarium.Controller.ResultAdapters.Trx;

public sealed class TrxResultAdapter(TrxAdapterLimits? limits = null)
{
    public const string AdapterId = "trx";
    public const string AdapterVersion = "1.0.0";
    public const int ProjectionSchemaVersion = 1;
    public const int IdentityAlgorithmVersion = 1;

    private readonly TrxAdapterLimits limits = ValidateLimits(limits ?? new TrxAdapterLimits());

    public async Task<TrxResultProjection> ProjectAsync(
        TrxProjectionContext context,
        Stream rawReport,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(rawReport);
        ValidateContext(context);

        await using var buffer = await ReadBoundedAsync(rawReport, cancellationToken);
        var document = ReadDocument(buffer);
        var root = document.Root;
        if (root is null || !Is(root, "TestRun"))
        {
            throw new TrxProjectionException(
                "trx_root_invalid",
                "TRX report root must be a TestRun element.");
        }

        var warningSink = new WarningSink(limits.MaxWarnings);
        var definitions = ReadDefinitions(root, context, warningSink);
        var run = ReadRun(root, context, warningSink);
        var tests = new Dictionary<string, TrxTestProjection>(StringComparer.Ordinal);
        var attempts = new Dictionary<string, int>(StringComparer.Ordinal);
        var occurrences = ReadOccurrences(
            root,
            context,
            definitions,
            tests,
            attempts,
            warningSink);

        return new TrxResultProjection(
            AdapterId,
            AdapterVersion,
            ProjectionSchemaVersion,
            context,
            run,
            tests.Values.OrderBy(test => test.TestId, StringComparer.Ordinal).ToArray(),
            occurrences,
            warningSink.Warnings,
            warningSink.SuppressedCount);
    }

    private async Task<MemoryStream> ReadBoundedAsync(
        Stream source,
        CancellationToken cancellationToken)
    {
        var output = new MemoryStream();
        var rented = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            long total = 0;
            while (true)
            {
                var read = await source.ReadAsync(rented.AsMemory(), cancellationToken);
                if (read == 0)
                {
                    break;
                }

                total += read;
                if (total > limits.MaxInputBytes)
                {
                    throw new TrxProjectionException(
                        "trx_input_too_large",
                        "TRX report exceeds the configured input-size limit.");
                }

                await output.WriteAsync(rented.AsMemory(0, read), cancellationToken);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }

        if (output.Length == 0)
        {
            output.Dispose();
            throw new TrxProjectionException("trx_empty", "TRX report is empty.");
        }

        output.Position = 0;
        return output;
    }

    private XDocument ReadDocument(MemoryStream buffer)
    {
        try
        {
            using (var reader = XmlReader.Create(buffer, ReaderSettings()))
            {
                var elements = 0;
                while (reader.Read())
                {
                    if (reader.NodeType != XmlNodeType.Element)
                    {
                        continue;
                    }

                    if (++elements > limits.MaxXmlElements)
                    {
                        throw new TrxProjectionException(
                            "trx_xml_element_limit_exceeded",
                            "TRX report contains too many XML elements.");
                    }

                    if (reader.Depth > limits.MaxXmlDepth)
                    {
                        throw new TrxProjectionException(
                            "trx_xml_depth_exceeded",
                            "TRX report exceeds the configured XML depth limit.");
                    }

                    if (reader.AttributeCount > limits.MaxAttributesPerElement)
                    {
                        throw new TrxProjectionException(
                            "trx_xml_attribute_limit_exceeded",
                            "A TRX element contains too many attributes.");
                    }

                    if (reader.HasAttributes)
                    {
                        while (reader.MoveToNextAttribute())
                        {
                            if (reader.Value.Length > limits.MaxAttributeCharacters)
                            {
                                throw new TrxProjectionException(
                                    "trx_xml_attribute_value_limit_exceeded",
                                    "A TRX attribute exceeds the configured value limit.");
                            }
                        }

                        reader.MoveToElement();
                    }
                }
            }

            buffer.Position = 0;
            using var documentReader = XmlReader.Create(buffer, ReaderSettings());
            return XDocument.Load(documentReader, LoadOptions.SetLineInfo);
        }
        catch (TrxProjectionException)
        {
            throw;
        }
        catch (XmlException exception)
        {
            throw new TrxProjectionException(
                "trx_malformed_xml",
                "TRX report is not well-formed XML.",
                exception);
        }
    }

    private XmlReaderSettings ReaderSettings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        MaxCharactersFromEntities = 0,
        MaxCharactersInDocument = limits.MaxInputBytes,
        CloseInput = false,
    };

    private IReadOnlyDictionary<string, TestDefinition> ReadDefinitions(
        XElement root,
        TrxProjectionContext context,
        WarningSink warnings)
    {
        var container = Child(root, "TestDefinitions");
        if (container is null)
        {
            return new ReadOnlyDictionary<string, TestDefinition>(
                new Dictionary<string, TestDefinition>(StringComparer.Ordinal));
        }

        var elements = container.Elements().ToArray();
        if (elements.Length > limits.MaxTestDefinitions)
        {
            throw new TrxProjectionException(
                "trx_test_definition_limit_exceeded",
                "TRX report contains too many test definitions.");
        }

        var definitions = new Dictionary<string, TestDefinition>(StringComparer.Ordinal);
        foreach (var element in elements)
        {
            var id = Attribute(element, "id");
            if (string.IsNullOrWhiteSpace(id))
            {
                warnings.Add(
                    "trx_definition_id_missing",
                    "A TRX test definition has no producer test id.",
                    Location(context, element));
                continue;
            }

            if (definitions.ContainsKey(id))
            {
                throw new TrxProjectionException(
                    "trx_duplicate_test_definition",
                    "TRX report contains duplicate producer test definitions.");
            }

            var method = Child(element, "TestMethod");
            var execution = Child(element, "Execution");
            definitions.Add(id, new TestDefinition(
                id,
                Attribute(element, "name"),
                Attribute(element, "storage"),
                Attribute(execution, "id"),
                Attribute(method, "className"),
                Attribute(method, "name"),
                Attribute(method, "codeBase"),
                Attribute(method, "adapterTypeName")));
        }

        return new ReadOnlyDictionary<string, TestDefinition>(definitions);
    }

    private TrxTestRunProjection ReadRun(
        XElement root,
        TrxProjectionContext context,
        WarningSink warnings)
    {
        var summary = Child(root, "ResultSummary");
        var times = Child(root, "Times");
        var startedAt = ParseTimestamp(Attribute(times, "start"), "run start", context, times, warnings);
        var finishedAt = ParseTimestamp(Attribute(times, "finish"), "run finish", context, times, warnings);
        WarnIfTimeOrderInvalid(startedAt, finishedAt, context, times ?? root, warnings);
        var nativeOutcome = Attribute(summary, "outcome");
        var outcome = NormalizeOutcome(nativeOutcome, context, summary ?? root, warnings);
        var counters = ReadCounters(Child(summary, "Counters"), context, warnings);
        var testSettings = Child(root, "TestSettings");
        var hints = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(root.Name.NamespaceName))
        {
            hints["xmlNamespace"] = BoundValue(root.Name.NamespaceName, context, root, warnings);
        }

        AddHint(hints, "testSettingsName", Attribute(testSettings, "name"), context, testSettings, warnings);
        AddHint(hints, "testSettingsId", Attribute(testSettings, "id"), context, testSettings, warnings);
        var summaryOutput = Child(Child(summary, "Output"), "StdOut");
        return new TrxTestRunProjection(
            Attribute(root, "id"),
            Attribute(root, "name"),
            nativeOutcome,
            outcome,
            startedAt,
            finishedAt,
            new ReadOnlyDictionary<string, long>(counters),
            new ReadOnlyDictionary<string, string>(hints),
            ReadText(summaryOutput, "run standard output", context, warnings),
            Location(context, root));
    }

    private IReadOnlyList<TrxTestOccurrenceProjection> ReadOccurrences(
        XElement root,
        TrxProjectionContext context,
        IReadOnlyDictionary<string, TestDefinition> definitions,
        Dictionary<string, TrxTestProjection> tests,
        Dictionary<string, int> attempts,
        WarningSink warnings)
    {
        var resultElements = Child(root, "Results")?.Elements().ToArray() ?? [];
        if (resultElements.Length > limits.MaxOccurrences)
        {
            throw new TrxProjectionException(
                "trx_occurrence_limit_exceeded",
                "TRX report contains too many test result occurrences.");
        }

        var definitionsByExecution = definitions.Values
            .Where(definition => !string.IsNullOrWhiteSpace(definition.ExecutionId))
            .GroupBy(definition => definition.ExecutionId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var occurrences = new List<TrxTestOccurrenceProjection>(resultElements.Length);
        for (var resultOrdinal = 0; resultOrdinal < resultElements.Length; resultOrdinal++)
        {
            var result = resultElements[resultOrdinal];
            var producerTestId = Attribute(result, "testId");
            var executionId = Attribute(result, "executionId");
            TestDefinition? definition = null;
            if (!string.IsNullOrWhiteSpace(producerTestId))
            {
                definitions.TryGetValue(producerTestId, out definition);
            }

            if (definition is null && !string.IsNullOrWhiteSpace(executionId))
            {
                definitionsByExecution.TryGetValue(executionId, out definition);
            }

            if (definition is null)
            {
                warnings.Add(
                    "trx_test_definition_missing",
                    "A TRX result could not be matched to a test definition; fallback identity was used.",
                    Location(context, result));
            }

            var displayName = Attribute(result, "testName") ?? definition?.Name;
            var parameterDisplay = ParameterDisplay(
                displayName,
                definition?.ClassName,
                definition?.MethodName,
                Attribute(result, "dataRowInfo"));
            var identityQuality = IsStableIdentity(definition, parameterDisplay)
                ? TrxTestIdentityQuality.Stable
                : TrxTestIdentityQuality.Fallback;
            var testId = ComputeTestId(
                context,
                identityQuality,
                definition,
                displayName,
                producerTestId);
            var test = new TrxTestProjection(
                testId,
                identityQuality,
                IdentityAlgorithmVersion,
                producerTestId ?? definition?.Id,
                definition?.ClassName,
                definition?.MethodName,
                FullyQualifiedName(definition, displayName),
                definition?.Storage ?? definition?.CodeBase,
                definition?.AdapterTypeName);
            if (tests.TryGetValue(testId, out var existing) && existing != test)
            {
                throw new TrxProjectionException(
                    "trx_test_identity_collision",
                    "TRX report produced conflicting records for one normalized test identity.");
            }

            tests[testId] = test;
            var attemptOrdinal = attempts.TryGetValue(testId, out var currentAttempt)
                ? checked(currentAttempt + 1)
                : 1;
            attempts[testId] = attemptOrdinal;
            var startedAt = ParseTimestamp(
                Attribute(result, "startTime"), "test start", context, result, warnings);
            var finishedAt = ParseTimestamp(
                Attribute(result, "endTime"), "test finish", context, result, warnings);
            WarnIfTimeOrderInvalid(startedAt, finishedAt, context, result, warnings);
            var nativeOutcome = Attribute(result, "outcome");
            var output = Child(result, "Output");
            var error = Child(output, "ErrorInfo");
            occurrences.Add(new TrxTestOccurrenceProjection(
                ComputeOccurrenceId(context, executionId, producerTestId, resultOrdinal),
                testId,
                attemptOrdinal,
                resultOrdinal,
                result.Name.LocalName,
                producerTestId,
                executionId,
                Attribute(result, "parentExecutionId"),
                displayName,
                parameterDisplay,
                nativeOutcome,
                NormalizeOutcome(nativeOutcome, context, result, warnings),
                ParseDuration(Attribute(result, "duration"), context, result, warnings),
                startedAt,
                finishedAt,
                Attribute(result, "computerName"),
                Attribute(result, "relativeResultsDirectory"),
                Attribute(result, "dataRowInfo"),
                ReadText(Child(output, "StdOut"), "test standard output", context, warnings),
                ReadText(Child(output, "StdErr"), "test standard error", context, warnings),
                ReadText(Child(error, "Message"), "test error message", context, warnings),
                ReadText(Child(error, "StackTrace"), "test stack trace", context, warnings),
                ReadAttachments(result, context, warnings),
                ReadAttributes(result, context, warnings),
                Location(context, result)));
        }

        return occurrences;
    }

    private IReadOnlyList<TrxAttachmentProjection> ReadAttachments(
        XElement result,
        TrxProjectionContext context,
        WarningSink warnings)
    {
        var elements = result.Descendants().Where(element =>
            Is(element, "ResultFile") || Is(element, "UriAttachment")).ToArray();
        if (elements.Length > limits.MaxAttachmentsPerOccurrence)
        {
            throw new TrxProjectionException(
                "trx_attachment_limit_exceeded",
                "A TRX test occurrence contains too many attachments.");
        }

        var attachments = new List<TrxAttachmentProjection>(elements.Length);
        foreach (var element in elements)
        {
            var value = Attribute(element, "path") ?? Attribute(element, "href") ??
                Attribute(Child(element, "A"), "href") ?? element.Value.Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                warnings.Add(
                    "trx_attachment_location_missing",
                    "A TRX attachment has no path or URI.",
                    Location(context, element));
                continue;
            }

            attachments.Add(new TrxAttachmentProjection(
                Is(element, "ResultFile") ? "result-file" : "uri",
                BoundValue(value, context, element, warnings),
                Location(context, element)));
        }

        return attachments;
    }

    private SortedDictionary<string, long> ReadCounters(
        XElement? counters,
        TrxProjectionContext context,
        WarningSink warnings)
    {
        var result = new SortedDictionary<string, long>(StringComparer.Ordinal);
        if (counters is null)
        {
            return result;
        }

        foreach (var attribute in counters.Attributes().Where(attribute => !attribute.IsNamespaceDeclaration))
        {
            if (!long.TryParse(attribute.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ||
                value < 0)
            {
                warnings.Add(
                    "trx_counter_invalid",
                    "A TRX result counter is not a non-negative invariant integer.",
                    Location(context, counters));
                continue;
            }

            result[attribute.Name.LocalName] = value;
        }

        return result;
    }

    private IReadOnlyDictionary<string, string> ReadAttributes(
        XElement element,
        TrxProjectionContext context,
        WarningSink warnings)
    {
        var attributes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var attribute in element.Attributes().Where(attribute => !attribute.IsNamespaceDeclaration))
        {
            attributes[attribute.Name.ToString()] = BoundValue(
                attribute.Value,
                context,
                element,
                warnings);
        }

        return new ReadOnlyDictionary<string, string>(attributes);
    }

    private TrxTextProjection? ReadText(
        XElement? element,
        string field,
        TrxProjectionContext context,
        WarningSink warnings)
    {
        if (element is null)
        {
            return null;
        }

        var value = element.Value;
        if (value.Length <= limits.MaxTextCharacters)
        {
            return new TrxTextProjection(value, value.Length, Truncated: false);
        }

        warnings.Add(
            "trx_text_truncated",
            $"The bounded {field} projection was truncated; raw TRX evidence remains authoritative.",
            Location(context, element));
        return new TrxTextProjection(
            value[..limits.MaxTextCharacters],
            value.Length,
            Truncated: true);
    }

    private string BoundValue(
        string value,
        TrxProjectionContext context,
        XElement element,
        WarningSink warnings)
    {
        if (value.Length <= limits.MaxAttributeCharacters)
        {
            return value;
        }

        warnings.Add(
            "trx_value_truncated",
            "A bounded native TRX value was truncated; raw TRX evidence remains authoritative.",
            Location(context, element));
        return value[..limits.MaxAttributeCharacters];
    }

    private long? ParseDuration(
        string? value,
        TrxProjectionContext context,
        XElement element,
        WarningSink warnings)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var duration) &&
            duration >= TimeSpan.Zero)
        {
            return duration.Ticks;
        }

        try
        {
            duration = XmlConvert.ToTimeSpan(value);
            if (duration >= TimeSpan.Zero)
            {
                return duration.Ticks;
            }
        }
        catch (FormatException)
        {
            // Report one stable warning below.
        }

        warnings.Add(
            "trx_duration_invalid",
            "A TRX duration is not a non-negative invariant duration.",
            Location(context, element));
        return null;
    }

    private static DateTimeOffset? ParseTimestamp(
        string? value,
        string field,
        TrxProjectionContext context,
        XElement? element,
        WarningSink warnings)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.RoundtripKind,
                out var timestamp))
        {
            return timestamp;
        }

        warnings.Add(
            "trx_timestamp_invalid",
            $"A TRX {field} timestamp is not an invariant round-trip timestamp.",
            Location(context, element));
        return null;
    }

    private static void WarnIfTimeOrderInvalid(
        DateTimeOffset? startedAt,
        DateTimeOffset? finishedAt,
        TrxProjectionContext context,
        XElement element,
        WarningSink warnings)
    {
        if (startedAt is not null && finishedAt is not null && finishedAt < startedAt)
        {
            warnings.Add(
                "trx_time_order_invalid",
                "A TRX finish timestamp precedes its start timestamp.",
                Location(context, element));
        }
    }

    private static TrxNormalizedOutcome NormalizeOutcome(
        string? native,
        TrxProjectionContext context,
        XElement element,
        WarningSink warnings)
    {
        var key = native?.Trim().Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
        var outcome = key switch
        {
            "passed" => TrxNormalizedOutcome.Passed,
            "failed" or "error" => TrxNormalizedOutcome.Failed,
            "skipped" => TrxNormalizedOutcome.Skipped,
            "ignored" => TrxNormalizedOutcome.Ignored,
            "inconclusive" => TrxNormalizedOutcome.Inconclusive,
            "aborted" or "timeout" or "disconnected" or "passedbutrunaborted" =>
                TrxNormalizedOutcome.Aborted,
            "notexecuted" or "notrun" or "notrunnable" or "pending" or "inprogress" =>
                TrxNormalizedOutcome.NotRun,
            _ => TrxNormalizedOutcome.Unknown,
        };
        if (outcome == TrxNormalizedOutcome.Unknown)
        {
            warnings.Add(
                "trx_outcome_unknown",
                "A TRX native outcome is unknown and was not guessed as success.",
                Location(context, element));
        }

        return outcome;
    }

    private static string? ParameterDisplay(
        string? displayName,
        string? className,
        string? methodName,
        string? dataRowInfo)
    {
        if (!string.IsNullOrWhiteSpace(dataRowInfo))
        {
            return dataRowInfo;
        }

        if (string.IsNullOrWhiteSpace(displayName))
        {
            return null;
        }

        var candidate = displayName.Trim();
        if (candidate.Contains('(') || candidate.Contains('['))
        {
            return candidate;
        }

        if (!string.IsNullOrWhiteSpace(methodName) &&
            !string.Equals(candidate, methodName, StringComparison.Ordinal) &&
            !string.Equals(candidate, $"{className}.{methodName}", StringComparison.Ordinal))
        {
            return candidate;
        }

        return null;
    }

    private static bool IsStableIdentity(TestDefinition? definition, string? parameterDisplay) =>
        definition is not null &&
        !string.IsNullOrWhiteSpace(definition.ClassName) &&
        !string.IsNullOrWhiteSpace(definition.MethodName) &&
        parameterDisplay is null;

    private static string? FullyQualifiedName(TestDefinition? definition, string? displayName) =>
        !string.IsNullOrWhiteSpace(definition?.ClassName) &&
        !string.IsNullOrWhiteSpace(definition.MethodName)
            ? $"{definition.ClassName}.{definition.MethodName}"
            : displayName;

    private static string ComputeTestId(
        TrxProjectionContext context,
        TrxTestIdentityQuality quality,
        TestDefinition? definition,
        string? displayName,
        string? producerTestId)
    {
        var identity = quality == TrxTestIdentityQuality.Stable
            ? $"method\0{Canonical(definition!.ClassName)}\0{Canonical(definition.MethodName)}"
            : $"fallback\0{Canonical(definition?.ClassName)}\0{Canonical(definition?.MethodName)}\0" +
              $"{Canonical(displayName)}\0" +
              (string.IsNullOrWhiteSpace(displayName) ? Canonical(producerTestId) : string.Empty);
        return "test_" + HashIdentifier(
            $"{Canonical(context.ProjectId)}\0{Canonical(context.TestSourceId)}\0" +
            $"{IdentityAlgorithmVersion}\0{identity}");
    }

    private static string ComputeOccurrenceId(
        TrxProjectionContext context,
        string? executionId,
        string? producerTestId,
        int ordinal) => "occ_" + HashIdentifier(
        $"{Canonical(context.BuildId)}\0{Canonical(context.RawArtifactId)}\0" +
        $"{Canonical(executionId)}\0{Canonical(producerTestId)}\0{ordinal}");

    private static string HashIdentifier(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToBase64String(hash)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static string Canonical(string? value) =>
        value?.Trim().Normalize(NormalizationForm.FormC) ?? string.Empty;

    private void AddHint(
        IDictionary<string, string> hints,
        string name,
        string? value,
        TrxProjectionContext context,
        XElement? element,
        WarningSink warnings)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            hints[name] = BoundValue(value, context, element ?? throw new InvalidOperationException(), warnings);
        }
    }

    private static TrxRawSourceLocation Location(
        TrxProjectionContext context,
        XElement? element)
    {
        var lineInfo = element as IXmlLineInfo;
        return new TrxRawSourceLocation(
            context.RawArtifactId,
            context.RawArtifactPath,
            lineInfo?.HasLineInfo() == true ? lineInfo.LineNumber : null,
            lineInfo?.HasLineInfo() == true ? lineInfo.LinePosition : null);
    }

    private static XElement? Child(XElement? parent, string localName) =>
        parent?.Elements().FirstOrDefault(element => Is(element, localName));

    private static bool Is(XElement element, string localName) =>
        string.Equals(element.Name.LocalName, localName, StringComparison.Ordinal);

    private static string? Attribute(XElement? element, string localName) =>
        element?.Attributes().FirstOrDefault(attribute =>
            !attribute.IsNamespaceDeclaration &&
            string.Equals(attribute.Name.LocalName, localName, StringComparison.Ordinal))?.Value;

    private static TrxAdapterLimits ValidateLimits(TrxAdapterLimits limits)
    {
        limits.Validate();
        return limits;
    }

    private static void ValidateContext(TrxProjectionContext context)
    {
        RequireIdentifier(context.BuildId, 256, nameof(context.BuildId));
        RequireIdentifier(context.ProjectId, 256, nameof(context.ProjectId));
        RequireIdentifier(context.TestSourceId, 256, nameof(context.TestSourceId));
        RequireIdentifier(context.RawArtifactId, 256, nameof(context.RawArtifactId));
        RequireIdentifier(context.RawArtifactPath, 1_024, nameof(context.RawArtifactPath));
    }

    private static void RequireIdentifier(string value, int maximum, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(char.IsControl))
        {
            throw new ArgumentException(
                $"TRX projection {name} must be a bounded non-control string",
                name);
        }
    }

    private sealed record TestDefinition(
        string Id,
        string? Name,
        string? Storage,
        string? ExecutionId,
        string? ClassName,
        string? MethodName,
        string? CodeBase,
        string? AdapterTypeName);

    private sealed class WarningSink(int maximum)
    {
        private readonly List<TrxProjectionWarning> warnings = [];

        public IReadOnlyList<TrxProjectionWarning> Warnings => warnings;

        public int SuppressedCount { get; private set; }

        public void Add(string code, string summary, TrxRawSourceLocation location)
        {
            if (warnings.Count < maximum)
            {
                warnings.Add(new TrxProjectionWarning(code, summary, location));
            }
            else
            {
                SuppressedCount++;
            }
        }
    }
}
