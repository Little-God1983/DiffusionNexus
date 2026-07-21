using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Models;
using DiffusionNexus.Service.Services.DatasetQuality.Scoring;
using FluentAssertions;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace DiffusionNexus.Tests.DatasetQuality.Scoring;

/// <summary>
/// Unit tests for <see cref="CheckScoreAdapter"/>: the issue → 0–100 normalisation
/// (per-severity penalties, affected-file scaling, clamping) and the two direct
/// score factories for bucket analysis and dataset completeness.
/// </summary>
public class CheckScoreAdapterTests
{
    private static Issue MakeIssue(
        IssueSeverity severity,
        int affectedFiles = 1,
        string checkName = "Trigger Word") => new()
        {
            Severity = severity,
            Message = "problem",
            Domain = CheckDomain.Caption,
            CheckName = checkName,
            AffectedFiles = Enumerable.Range(0, affectedFiles).Select(i => $"file{i}.txt").ToList()
        };

    #region ScoreFromIssues — mapping

    [Fact]
    public void WhenCheckNameIsUnknownThenScoreFromIssuesReturnsNull()
    {
        CheckScoreAdapter.ScoreFromIssues("Not A Real Check", [], 10).Should().BeNull();
    }

    [Theory]
    [InlineData("Format Consistency", 1.2)]
    [InlineData("Trigger Word", 1.5)]
    [InlineData("Synonym Consistency", 1.0)]
    [InlineData("Feature Consistency", 1.0)]
    [InlineData("Type-Specific", 1.0)]
    [InlineData("Spelling", 0.5)]
    public void WhenCaptionCheckNameIsKnownThenScoreIsCaptionQualityWithMappedWeight(
        string checkName, double expectedWeight)
    {
        var result = CheckScoreAdapter.ScoreFromIssues(checkName, [], totalFiles: 10);

        result.Should().NotBeNull();
        result!.CheckName.Should().Be(checkName);
        result.Category.Should().Be(QualityScoreCategory.CaptionQuality);
        result.Weight.Should().Be(expectedWeight);
    }

    [Fact]
    public void WhenCheckNameIsBucketAnalysisThenCategoryIsDatasetConsistency()
    {
        var result = CheckScoreAdapter.ScoreFromIssues("Bucket Analysis", [], totalFiles: 10);

        result.Should().NotBeNull();
        result!.Category.Should().Be(QualityScoreCategory.DatasetConsistency);
        result.Weight.Should().Be(1.0);
    }

    [Theory]
    [InlineData("trigger word")]
    [InlineData("TRIGGER WORD")]
    [InlineData("Trigger word")]
    public void WhenCheckNameCasingDiffersThenLookupStillResolvesToTheRightCategoryAndWeight(string checkName)
    {
        // The map uses an ordinal-ignore-case comparer — casing/spacing drift in a
        // check's display name must not silently drop it from the composite score.
        var result = CheckScoreAdapter.ScoreFromIssues(checkName, [], 10);

        result.Should().NotBeNull();
        result!.Category.Should().Be(QualityScoreCategory.CaptionQuality);
        result.Weight.Should().Be(1.5);
    }

    [Fact]
    public void WhenCheckNameIsUnknownThenAWarningIsLoggedInsteadOfFailingSilently()
    {
        var events = new List<LogEvent>();
        var testLogger = new LoggerConfiguration()
            .MinimumLevel.Warning()
            .WriteTo.Sink(new DelegatingSink(events.Add))
            .CreateLogger();

        var previousLogger = Log.Logger;
        Log.Logger = testLogger;
        try
        {
            CheckScoreAdapter.ScoreFromIssues("Not A Real Check", [], 10).Should().BeNull();
        }
        finally
        {
            Log.Logger = previousLogger;
            testLogger.Dispose();
        }

        events.Should().ContainSingle(e =>
            e.Level == LogEventLevel.Warning &&
            e.RenderMessage().Contains("Not A Real Check"));
    }

    /// <summary>Minimal Serilog sink that forwards every emitted event to a delegate,
    /// used to assert a warning was logged without pulling in a test-sink package.</summary>
    private sealed class DelegatingSink : ILogEventSink
    {
        private readonly Action<LogEvent> _write;
        public DelegatingSink(Action<LogEvent> write) => _write = write;
        public void Emit(LogEvent logEvent) => _write(logEvent);
    }

    #endregion

    #region ScoreFromIssues — penalties

    [Fact]
    public void WhenNoIssuesThenScoreIsOneHundred()
    {
        var result = CheckScoreAdapter.ScoreFromIssues("Trigger Word", [], totalFiles: 10);

        result.Should().NotBeNull();
        result!.Score.Should().Be(100.0);
    }

    [Fact]
    public void WhenNoIssuesAndNoFilesThenScoreIsStillOneHundred()
    {
        var result = CheckScoreAdapter.ScoreFromIssues("Trigger Word", [], totalFiles: 0);

        result.Should().NotBeNull();
        result!.Score.Should().Be(100.0);
    }

    [Theory]
    [InlineData(IssueSeverity.Critical, 98.0)]  // 100 - (20 * 1) / 10
    [InlineData(IssueSeverity.Warning, 99.5)]   // 100 - (5 * 1) / 10
    [InlineData(IssueSeverity.Info, 99.9)]      // 100 - (1 * 1) / 10
    public void WhenSingleIssueThenPenaltyMatchesSeverity(IssueSeverity severity, double expected)
    {
        var result = CheckScoreAdapter.ScoreFromIssues(
            "Trigger Word", [MakeIssue(severity)], totalFiles: 10);

        result.Should().NotBeNull();
        result!.Score.Should().Be(expected);
    }

    [Fact]
    public void WhenIssueAffectsManyFilesThenPenaltyScalesWithAffectedCount()
    {
        // 100 - (20 * 3) / 10 = 94
        var result = CheckScoreAdapter.ScoreFromIssues(
            "Trigger Word", [MakeIssue(IssueSeverity.Critical, affectedFiles: 3)], totalFiles: 10);

        result.Should().NotBeNull();
        result!.Score.Should().Be(94.0);
    }

    [Fact]
    public void WhenIssueHasNoAffectedFilesThenItStillCountsAsOne()
    {
        // 100 - (1 * 1) / 2 = 99.5
        var result = CheckScoreAdapter.ScoreFromIssues(
            "Trigger Word", [MakeIssue(IssueSeverity.Info, affectedFiles: 0)], totalFiles: 2);

        result.Should().NotBeNull();
        result!.Score.Should().Be(99.5);
    }

    [Fact]
    public void WhenTotalFilesIsZeroThenDivisorFallsBackToOne()
    {
        // 100 - (5 * 1) / max(0, 1) = 95
        var result = CheckScoreAdapter.ScoreFromIssues(
            "Trigger Word", [MakeIssue(IssueSeverity.Warning)], totalFiles: 0);

        result.Should().NotBeNull();
        result!.Score.Should().Be(95.0);
    }

    [Fact]
    public void WhenSeverityIsUnrecognisedThenNoPenaltyIsApplied()
    {
        var result = CheckScoreAdapter.ScoreFromIssues(
            "Trigger Word", [MakeIssue((IssueSeverity)99)], totalFiles: 10);

        result.Should().NotBeNull();
        result!.Score.Should().Be(100.0);
    }

    [Fact]
    public void WhenMixedSeveritiesThenPenaltiesAccumulate()
    {
        // (20 * 1) + (5 * 2) + (1 * 1) = 31 over 10 files → 100 - 3.1 = 96.9
        var issues = new[]
        {
            MakeIssue(IssueSeverity.Critical),
            MakeIssue(IssueSeverity.Warning, affectedFiles: 2),
            MakeIssue(IssueSeverity.Info)
        };

        var result = CheckScoreAdapter.ScoreFromIssues("Trigger Word", issues, totalFiles: 10);

        result.Should().NotBeNull();
        result!.Score.Should().Be(96.9);
    }

    [Fact]
    public void WhenPenaltiesExceedOneHundredThenScoreIsClampedToZero()
    {
        var issues = Enumerable.Range(0, 10)
            .Select(_ => MakeIssue(IssueSeverity.Critical))
            .ToList();

        var result = CheckScoreAdapter.ScoreFromIssues("Trigger Word", issues, totalFiles: 1);

        result.Should().NotBeNull();
        result!.Score.Should().Be(0.0);
    }

    [Fact]
    public void WhenScoreHasManyDecimalsThenItIsRoundedToOnePlace()
    {
        // 100 - (1 * 1) / 3 = 99.666… → 99.7
        var result = CheckScoreAdapter.ScoreFromIssues(
            "Trigger Word", [MakeIssue(IssueSeverity.Info)], totalFiles: 3);

        result.Should().NotBeNull();
        result!.Score.Should().Be(99.7);
    }

    #endregion

    #region ScoreFromBucketAnalysis

    [Fact]
    public void WhenBucketDistributionIsInRangeThenScoreIsPassedThrough()
    {
        var result = CheckScoreAdapter.ScoreFromBucketAnalysis(73.4);

        result.Score.Should().Be(73.4);
        result.CheckName.Should().Be("Bucket Analysis");
        result.Category.Should().Be(QualityScoreCategory.DatasetConsistency);
        result.Weight.Should().Be(1.0);
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(0, 0)]
    [InlineData(100, 100)]
    [InlineData(150, 100)]
    public void WhenBucketDistributionIsOutOfRangeThenScoreIsClamped(double input, double expected)
    {
        CheckScoreAdapter.ScoreFromBucketAnalysis(input).Score.Should().Be(expected);
    }

    #endregion

    #region ScoreFromCompleteness

    [Fact]
    public void WhenNoImagesThenCompletenessIsZero()
    {
        var result = CheckScoreAdapter.ScoreFromCompleteness(totalCaptions: 5, totalImages: 0);

        result.Score.Should().Be(0.0);
    }

    [Fact]
    public void WhenEveryImageHasACaptionThenCompletenessIsOneHundred()
    {
        CheckScoreAdapter.ScoreFromCompleteness(10, 10).Score.Should().Be(100.0);
    }

    [Fact]
    public void WhenMoreCaptionsThanImagesThenCompletenessIsCappedAtOneHundred()
    {
        CheckScoreAdapter.ScoreFromCompleteness(15, 10).Score.Should().Be(100.0);
    }

    [Fact]
    public void WhenSomeCaptionsMissingThenCompletenessIsTheRatio()
    {
        CheckScoreAdapter.ScoreFromCompleteness(3, 4).Score.Should().Be(75.0);
    }

    [Fact]
    public void WhenRatioHasManyDecimalsThenCompletenessIsRoundedToOnePlace()
    {
        // 1/3 → 33.333… → 33.3
        CheckScoreAdapter.ScoreFromCompleteness(1, 3).Score.Should().Be(33.3);
    }

    [Fact]
    public void WhenCompletenessScoredThenItIsTaggedAsTheCompletenessCategory()
    {
        var result = CheckScoreAdapter.ScoreFromCompleteness(1, 2);

        result.CheckName.Should().Be("Dataset Completeness");
        result.Category.Should().Be(QualityScoreCategory.DatasetCompleteness);
        result.Weight.Should().Be(1.0);
    }

    #endregion
}
