using System.Globalization;
using System.Text;
using Vivarium.Controller.ResultAdapters.Trx;

namespace Vivarium.Tests;

[TestFixture]
[NonParallelizable]
public sealed class TrxResultProjectionTests
{
    [Test]
    public async Task Golden_report_projects_normalized_tests_attempts_and_raw_provenance()
    {
        const string report = """
            <?xml version="1.0" encoding="utf-8"?>
            <TestRun id="run-1" name="Cross-platform run"
                     xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Times start="2026-08-29T10:00:00.0000000+02:00"
                     finish="2026-08-29T10:00:03.0000000+02:00" />
              <TestSettings name="default" id="settings-1" />
              <Results>
                <UnitTestResult executionId="exec-1" testId="definition-1" testName="Adds"
                    computerName="windows-agent" duration="00:00:00.1250000"
                    startTime="2026-08-29T10:00:00.1000000+02:00"
                    endTime="2026-08-29T10:00:00.2250000+02:00" outcome="Failed">
                  <Output>
                    <StdOut>first attempt output</StdOut>
                    <StdErr>first attempt error</StdErr>
                    <ErrorInfo>
                      <Message>Expected 3</Message>
                      <StackTrace>at CalculatorTests.Adds()</StackTrace>
                    </ErrorInfo>
                  </Output>
                  <ResultFiles><ResultFile path="evidence/screenshot.png" /></ResultFiles>
                </UnitTestResult>
                <UnitTestResult executionId="exec-2" testId="definition-1" testName="Adds"
                    computerName="linux-agent" duration="PT0.25S" outcome="Passed">
                  <CollectorDataEntries>
                    <Collector><UriAttachments><UriAttachment>
                      <A href="file:///tmp/coverage.xml">coverage</A>
                    </UriAttachment></UriAttachments></Collector>
                  </CollectorDataEntries>
                </UnitTestResult>
                <UnitTestResult executionId="exec-3" testId="definition-2"
                    testName="Adds(1, 2)" dataRowInfo="row:1,2"
                    duration="00:00:00.0100000" outcome="FutureProducerValue" />
              </Results>
              <TestDefinitions>
                <UnitTest name="Adds" storage="C:\tests\suite.dll" id="definition-1">
                  <Execution id="exec-1" />
                  <TestMethod codeBase="C:\tests\suite.dll" adapterTypeName="executor://mstest"
                      className="Example.CalculatorTests" name="Adds" />
                </UnitTest>
                <UnitTest name="Adds(1, 2)" storage="/tests/suite.dll" id="definition-2">
                  <Execution id="exec-3" />
                  <TestMethod codeBase="/tests/suite.dll" adapterTypeName="executor://nunit"
                      className="Example.CalculatorTests" name="Adds" />
                </UnitTest>
              </TestDefinitions>
              <ResultSummary outcome="Failed">
                <Counters total="3" executed="3" passed="1" failed="1" notExecuted="0" />
                <Output><StdOut>run output</StdOut></Output>
              </ResultSummary>
            </TestRun>
            """;
        var context = Context();

        var projection = await ProjectAsync(new TrxResultAdapter(), context, report);

        var stableTest = projection.Tests.Single(test =>
            test.IdentityQuality == TrxTestIdentityQuality.Stable);
        var fallbackTest = projection.Tests.Single(test =>
            test.IdentityQuality == TrxTestIdentityQuality.Fallback);
        var attempts = projection.Occurrences
            .Where(occurrence => occurrence.TestId == stableTest.TestId)
            .OrderBy(occurrence => occurrence.AttemptOrdinal)
            .ToArray();
        var first = attempts[0];
        var second = attempts[1];
        var unknown = projection.Occurrences.Single(occurrence =>
            occurrence.TestId == fallbackTest.TestId);

        Assert.Multiple(() =>
        {
            Assert.That(projection.AdapterId, Is.EqualTo("trx"));
            Assert.That(projection.AdapterVersion, Is.EqualTo("1.0.0"));
            Assert.That(projection.ProjectionSchemaVersion, Is.EqualTo(1));
            Assert.That(projection.Context, Is.EqualTo(context));
            Assert.That(projection.Run.RunId, Is.EqualTo("run-1"));
            Assert.That(projection.Run.Name, Is.EqualTo("Cross-platform run"));
            Assert.That(projection.Run.NativeOutcome, Is.EqualTo("Failed"));
            Assert.That(projection.Run.Outcome, Is.EqualTo(TrxNormalizedOutcome.Failed));
            Assert.That(projection.Run.Counters["total"], Is.EqualTo(3));
            Assert.That(projection.Run.Counters["passed"], Is.EqualTo(1));
            Assert.That(projection.Run.Source.ArtifactId, Is.EqualTo("artifact-trx-1"));
            Assert.That(projection.Run.Source.ArtifactPath, Is.EqualTo("reports/results.trx"));
            Assert.That(projection.Tests, Has.Count.EqualTo(2));
            Assert.That(projection.Occurrences, Has.Count.EqualTo(3));
            Assert.That(stableTest.ClassName, Is.EqualTo("Example.CalculatorTests"));
            Assert.That(stableTest.MethodName, Is.EqualTo("Adds"));
            Assert.That(fallbackTest.IdentityQuality, Is.EqualTo(TrxTestIdentityQuality.Fallback));
            Assert.That(attempts.Select(attempt => attempt.AttemptOrdinal), Is.EqualTo(new[] { 1, 2 }));
            Assert.That(first.NativeOutcome, Is.EqualTo("Failed"));
            Assert.That(first.Outcome, Is.EqualTo(TrxNormalizedOutcome.Failed));
            Assert.That(first.DurationTicks, Is.EqualTo(TimeSpan.FromMilliseconds(125).Ticks));
            Assert.That(first.StandardOutput!.Value, Is.EqualTo("first attempt output"));
            Assert.That(first.ErrorMessage!.Value, Is.EqualTo("Expected 3"));
            Assert.That(first.Attachments.Single().PathOrUri,
                Is.EqualTo("evidence/screenshot.png"));
            Assert.That(second.Outcome, Is.EqualTo(TrxNormalizedOutcome.Passed));
            Assert.That(second.DurationTicks, Is.EqualTo(TimeSpan.FromMilliseconds(250).Ticks));
            Assert.That(second.Attachments.Single().PathOrUri,
                Is.EqualTo("file:///tmp/coverage.xml"));
            Assert.That(unknown.NativeOutcome, Is.EqualTo("FutureProducerValue"));
            Assert.That(unknown.Outcome, Is.EqualTo(TrxNormalizedOutcome.Unknown));
            Assert.That(unknown.ParameterDisplay, Is.EqualTo("row:1,2"));
            Assert.That(projection.Warnings.Any(warning =>
                warning.Code == "trx_outcome_unknown" &&
                warning.Location.ArtifactId == context.RawArtifactId), Is.True);
            Assert.That(projection.SuppressedWarningCount, Is.Zero);
        });
    }

    [Test]
    public async Task Stable_test_identity_ignores_build_artifact_and_platform_source_paths()
    {
        var windows = SimpleReport(
            storage: "C:\\agent\\work\\suite.dll",
            codeBase: "C:\\agent\\work\\suite.dll",
            duration: "00:00:01.5000000");
        var linux = SimpleReport(
            storage: "/opt/agent/work/suite.dll",
            codeBase: "/opt/agent/work/suite.dll",
            duration: "00:00:01.5000000");
        var first = await ProjectAsync(
            new TrxResultAdapter(),
            Context(buildId: "build-windows", artifactId: "artifact-windows"),
            windows);
        var second = await ProjectAsync(
            new TrxResultAdapter(),
            Context(buildId: "build-linux", artifactId: "artifact-linux"),
            linux);

        Assert.Multiple(() =>
        {
            Assert.That(first.Tests.Single().IdentityQuality,
                Is.EqualTo(TrxTestIdentityQuality.Stable));
            Assert.That(first.Tests.Single().TestId, Is.EqualTo(second.Tests.Single().TestId));
            Assert.That(first.Tests.Single().Source, Is.Not.EqualTo(second.Tests.Single().Source));
            Assert.That(first.Occurrences.Single().OccurrenceId,
                Is.Not.EqualTo(second.Occurrences.Single().OccurrenceId));
        });
    }

    [Test]
    public async Task Projection_is_invariant_to_current_culture_and_preserves_duration_precision()
    {
        var report = SimpleReport("suite.dll", "suite.dll", "00:00:01.2345678");
        var previousCulture = CultureInfo.CurrentCulture;
        var previousUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            var commaCulture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
            commaCulture.NumberFormat.NumberDecimalSeparator = ",";
            commaCulture.DateTimeFormat.TimeSeparator = ".";
            CultureInfo.CurrentCulture = commaCulture;
            CultureInfo.CurrentUICulture = commaCulture;
            var comma = await ProjectAsync(new TrxResultAdapter(), Context(), report);
            var alternateCulture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
            alternateCulture.NumberFormat.NumberDecimalSeparator = ".";
            alternateCulture.DateTimeFormat.TimeSeparator = ":";
            CultureInfo.CurrentCulture = alternateCulture;
            CultureInfo.CurrentUICulture = alternateCulture;
            var alternate = await ProjectAsync(new TrxResultAdapter(), Context(), report);

            Assert.Multiple(() =>
            {
                Assert.That(comma.Tests.Single().TestId, Is.EqualTo(alternate.Tests.Single().TestId));
                Assert.That(comma.Occurrences.Single().OccurrenceId,
                    Is.EqualTo(alternate.Occurrences.Single().OccurrenceId));
                Assert.That(comma.Occurrences.Single().DurationTicks, Is.EqualTo(12_345_678));
                Assert.That(alternate.Occurrences.Single().DurationTicks, Is.EqualTo(12_345_678));
            });
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }

    [TestCase("<not-a-test-run />", "trx_root_invalid")]
    [TestCase("<TestRun><Results>", "trx_malformed_xml")]
    [TestCase("<!DOCTYPE TestRun [<!ENTITY xxe SYSTEM 'file:///etc/passwd'>]><TestRun>&xxe;</TestRun>",
        "trx_malformed_xml")]
    public void Invalid_or_entity_bearing_xml_fails_closed_with_a_typed_bounded_problem(
        string report,
        string expectedCode)
    {
        var exception = Assert.ThrowsAsync<TrxProjectionException>(async () =>
            await ProjectAsync(new TrxResultAdapter(), Context(), report));

        Assert.Multiple(() =>
        {
            Assert.That(exception!.Code, Is.EqualTo(expectedCode));
            Assert.That(exception.Message, Has.Length.LessThanOrEqualTo(128));
            Assert.That(exception.Message, Does.Not.Contain("/etc/passwd"));
        });
    }

    [Test]
    public void Structural_and_input_limits_fail_before_unbounded_projection()
    {
        var oversized = Assert.ThrowsAsync<TrxProjectionException>(async () =>
            await ProjectAsync(
                new TrxResultAdapter(new TrxAdapterLimits(MaxInputBytes: 64)),
                Context(),
                "<TestRun><Results><UnitTestResult testName=\"long report payload\" /></Results></TestRun>"));
        var deep = Assert.ThrowsAsync<TrxProjectionException>(async () =>
            await ProjectAsync(
                new TrxResultAdapter(new TrxAdapterLimits(MaxXmlDepth: 2)),
                Context(),
                "<TestRun><a><b><c /></b></a></TestRun>"));
        var highCount = Assert.ThrowsAsync<TrxProjectionException>(async () =>
            await ProjectAsync(
                new TrxResultAdapter(new TrxAdapterLimits(MaxOccurrences: 1)),
                Context(),
                "<TestRun><Results><UnitTestResult /><UnitTestResult /></Results></TestRun>"));
        var manyAttachments = Assert.ThrowsAsync<TrxProjectionException>(async () =>
            await ProjectAsync(
                new TrxResultAdapter(new TrxAdapterLimits(MaxAttachmentsPerOccurrence: 1)),
                Context(),
                """
                <TestRun><Results><UnitTestResult>
                  <ResultFiles><ResultFile path="one"/><ResultFile path="two"/></ResultFiles>
                </UnitTestResult></Results></TestRun>
                """));
        var longAttribute = Assert.ThrowsAsync<TrxProjectionException>(async () =>
            await ProjectAsync(
                new TrxResultAdapter(new TrxAdapterLimits(MaxAttributeCharacters: 4)),
                Context(),
                "<TestRun name=\"12345\" />"));

        Assert.Multiple(() =>
        {
            Assert.That(oversized!.Code, Is.EqualTo("trx_input_too_large"));
            Assert.That(deep!.Code, Is.EqualTo("trx_xml_depth_exceeded"));
            Assert.That(highCount!.Code, Is.EqualTo("trx_occurrence_limit_exceeded"));
            Assert.That(manyAttachments!.Code, Is.EqualTo("trx_attachment_limit_exceeded"));
            Assert.That(longAttribute!.Code, Is.EqualTo("trx_xml_attribute_value_limit_exceeded"));
        });
    }

    [Test]
    public async Task Oversized_text_is_explicitly_truncated_while_raw_artifact_provenance_remains()
    {
        const string report = """
            <TestRun>
              <Results>
                <UnitTestResult testId="definition-1" testName="Test" outcome="Passed">
                  <Output><StdOut>0123456789</StdOut></Output>
                </UnitTestResult>
              </Results>
              <TestDefinitions>
                <UnitTest id="definition-1" name="Test">
                  <TestMethod className="Example.Tests" name="Test" />
                </UnitTest>
              </TestDefinitions>
            </TestRun>
            """;

        var projection = await ProjectAsync(
            new TrxResultAdapter(new TrxAdapterLimits(MaxTextCharacters: 4)),
            Context(),
            report);
        var output = projection.Occurrences.Single().StandardOutput!;
        var warning = projection.Warnings.Single(item => item.Code == "trx_text_truncated");

        Assert.Multiple(() =>
        {
            Assert.That(output.Value, Is.EqualTo("0123"));
            Assert.That(output.OriginalCharacterCount, Is.EqualTo(10));
            Assert.That(output.Truncated, Is.True);
            Assert.That(warning.Location.ArtifactId, Is.EqualTo("artifact-trx-1"));
            Assert.That(warning.Summary, Does.Contain("raw TRX evidence remains authoritative"));
        });
    }

    private static TrxProjectionContext Context(
        string buildId = "build-1",
        string artifactId = "artifact-trx-1") => new(
        buildId,
        "project-1",
        "dotnet-tests",
        artifactId,
        "reports/results.trx");

    private static async Task<TrxResultProjection> ProjectAsync(
        TrxResultAdapter adapter,
        TrxProjectionContext context,
        string report)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(report));
        return await adapter.ProjectAsync(context, stream);
    }

    private static string SimpleReport(string storage, string codeBase, string duration) => $"""
        <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
          <Results>
            <UnitTestResult executionId="execution-1" testId="definition-1"
                testName="InvariantTest" duration="{duration}" outcome="Passed" />
          </Results>
          <TestDefinitions>
            <UnitTest id="definition-1" name="InvariantTest" storage="{storage}">
              <Execution id="execution-1" />
              <TestMethod className="Example.CultureTests" name="InvariantTest"
                  codeBase="{codeBase}" adapterTypeName="executor://nunit" />
            </UnitTest>
          </TestDefinitions>
          <ResultSummary outcome="Passed"><Counters total="1" passed="1" /></ResultSummary>
        </TestRun>
        """;
}
