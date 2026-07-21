using DiffusionNexus.Domain.Models;
using DiffusionNexus.Service.Services.DatasetQuality;
using FluentAssertions;

namespace DiffusionNexus.Tests.DatasetQuality;

/// <summary>
/// Unit tests for <see cref="ImageQualityAdvisor.Analyze"/>: verdict banding,
/// which metrics become problems, the per-metric wording, the headline variants
/// and the de-duplicated fix suggestions.
/// </summary>
public class ImageQualityAdvisorTests
{
    private const string FixReplaceOrTrash = "Replace with a higher-quality source, or mark as Trash.";
    private const string FixOpenInEditor = "Open in the Image Editor to adjust.";
    private const string FixReshoot = "Re-shoot in better conditions if possible.";
    private const string FixReexport = "Re-export from the original source at JPEG quality ≥ 85, or use PNG.";

    private static PerImageQualitySummary Summary(
        double? blur = null,
        double? exposure = null,
        double? noise = null,
        double? jpeg = null,
        string? blurDetail = null,
        string? exposureDetail = null,
        string? noiseDetail = null,
        string? jpegDetail = null) => new()
        {
            FilePath = @"C:\ds\img.png",
            BlurScore = blur,
            BlurDetail = blurDetail,
            ExposureScore = exposure,
            ExposureDetail = exposureDetail,
            NoiseScore = noise,
            NoiseDetail = noiseDetail,
            JpegScore = jpeg,
            JpegDetail = jpegDetail
        };

    #region Guard clauses

    [Fact]
    public void WhenSummaryIsNullThenThrowsArgumentNullException()
    {
        var act = () => ImageQualityAdvisor.Analyze(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void WhenNoChecksRanThenVerdictIsUnknownWithNoProblemsOrFixes()
    {
        var advice = ImageQualityAdvisor.Analyze(Summary());

        advice.Verdict.Should().Be(ImageQualityVerdict.Unknown);
        advice.Headline.Should().Be("No quality checks ran for this image.");
        advice.Problems.Should().BeEmpty();
        advice.SuggestedFixes.Should().BeEmpty();
    }

    #endregion

    #region Verdict banding

    [Theory]
    [InlineData(100, ImageQualityVerdict.Excellent)]
    [InlineData(80, ImageQualityVerdict.Excellent)]   // lower bound of Excellent
    [InlineData(79, ImageQualityVerdict.Good)]
    [InlineData(65, ImageQualityVerdict.Good)]        // lower bound of Good
    [InlineData(64, ImageQualityVerdict.Mediocre)]
    [InlineData(40, ImageQualityVerdict.Mediocre)]    // lower bound of Mediocre
    [InlineData(39, ImageQualityVerdict.Bad)]
    [InlineData(0, ImageQualityVerdict.Bad)]
    public void WhenSingleMetricScoredThenVerdictMatchesTheBand(
        double score, ImageQualityVerdict expected)
    {
        var advice = ImageQualityAdvisor.Analyze(Summary(blur: score));

        advice.Verdict.Should().Be(expected);
    }

    #endregion

    #region Problem selection

    [Fact]
    public void WhenMetricScoresAtTheMediocreThresholdThenItIsNotAProblem()
    {
        // 65 is the exclusive upper bound — only scores strictly below it are reported.
        var advice = ImageQualityAdvisor.Analyze(Summary(blur: 65));

        advice.Problems.Should().BeEmpty();
        advice.SuggestedFixes.Should().BeEmpty();
        advice.Headline.Should().Be("Looks good — overall score 65/100.");
    }

    [Fact]
    public void WhenMetricScoresJustBelowThresholdThenItBecomesAProblem()
    {
        var advice = ImageQualityAdvisor.Analyze(Summary(blur: 64));

        advice.Problems.Should().ContainSingle()
            .Which.MetricName.Should().Be("Blur");
    }

    [Fact]
    public void WhenMetricDidNotRunThenItIsNeverAProblem()
    {
        var advice = ImageQualityAdvisor.Analyze(Summary(blur: 10));

        advice.Problems.Should().ContainSingle();
        advice.Problems.Single().MetricName.Should().Be("Blur");
    }

    [Fact]
    public void WhenEveryMetricIsPoorThenProblemsAreReportedInMetricOrder()
    {
        var advice = ImageQualityAdvisor.Analyze(Summary(blur: 10, exposure: 20, noise: 30, jpeg: 40));

        advice.Problems.Select(p => p.MetricName).Should().Equal(
            "Blur", "Exposure", "Noise", "JPEG quality");
        advice.Problems.Select(p => p.Score).Should().Equal(10, 20, 30, 40);
    }

    [Fact]
    public void WhenAllMetricsAreHealthyThenThereAreNoProblems()
    {
        var advice = ImageQualityAdvisor.Analyze(Summary(blur: 90, exposure: 90, noise: 90, jpeg: 90));

        advice.Verdict.Should().Be(ImageQualityVerdict.Excellent);
        advice.Problems.Should().BeEmpty();
        advice.SuggestedFixes.Should().BeEmpty();
        advice.Headline.Should().Be("Looks good — overall score 90/100.");
    }

    #endregion

    #region Problem descriptions

    [Theory]
    [InlineData(30, "Image looks very blurry or soft.")]
    [InlineData(50, "Image is slightly soft.")]
    public void WhenBlurIsAProblemThenDescriptionMatchesItsSeverity(double score, string expected)
    {
        var advice = ImageQualityAdvisor.Analyze(Summary(blur: score));

        advice.Problems.Single().Description.Should().Be(expected);
    }

    [Theory]
    [InlineData(30, "Severe under- or over-exposure with clipped pixels.")]
    [InlineData(50, "Exposure is off — highlights or shadows are weak.")]
    public void WhenExposureIsAProblemThenDescriptionMatchesItsSeverity(double score, string expected)
    {
        var advice = ImageQualityAdvisor.Analyze(Summary(exposure: score));

        advice.Problems.Single().Description.Should().Be(expected);
    }

    [Theory]
    [InlineData(30, "High visible noise (likely shot at high ISO).")]
    [InlineData(50, "Some visible noise.")]
    public void WhenNoiseIsAProblemThenDescriptionMatchesItsSeverity(double score, string expected)
    {
        var advice = ImageQualityAdvisor.Analyze(Summary(noise: score));

        advice.Problems.Single().Description.Should().Be(expected);
    }

    [Theory]
    [InlineData(30, "Heavy JPEG compression artifacts (low quality save).")]
    [InlineData(50, "Mild JPEG compression artifacts.")]
    public void WhenJpegIsAProblemThenDescriptionMatchesItsSeverity(double score, string expected)
    {
        var advice = ImageQualityAdvisor.Analyze(Summary(jpeg: score));

        advice.Problems.Single().Description.Should().Be(expected);
    }

    [Fact]
    public void WhenDetailIsPresentThenItIsAppendedInParentheses()
    {
        var advice = ImageQualityAdvisor.Analyze(
            Summary(blur: 30, blurDetail: "Laplacian variance: 67"));

        advice.Problems.Single().Description
            .Should().Be("Image looks very blurry or soft. (Laplacian variance: 67)");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void WhenDetailIsBlankThenNoParenthesesAreAdded(string? detail)
    {
        var advice = ImageQualityAdvisor.Analyze(Summary(blur: 30, blurDetail: detail));

        advice.Problems.Single().Description.Should().Be("Image looks very blurry or soft.");
    }

    #endregion

    #region Headline variants

    [Fact]
    public void WhenVerdictIsBadThenHeadlineNamesTheWorstMetric()
    {
        // (10 + 20 + 30 + 40) / 4 = 25
        var advice = ImageQualityAdvisor.Analyze(Summary(blur: 10, exposure: 20, noise: 30, jpeg: 40));

        advice.Verdict.Should().Be(ImageQualityVerdict.Bad);
        advice.Headline.Should().Be("Poor quality (25/100). Worst issue: blur.");
    }

    [Fact]
    public void WhenVerdictIsMediocreThenHeadlineWarnsAboutTheWorstMetric()
    {
        // (50 + 60 + 64 + 62) / 4 = 59
        var advice = ImageQualityAdvisor.Analyze(Summary(blur: 50, exposure: 60, noise: 64, jpeg: 62));

        advice.Verdict.Should().Be(ImageQualityVerdict.Mediocre);
        advice.Headline.Should().Be("Mediocre quality (59/100). Watch the blur.");
    }

    [Fact]
    public void WhenVerdictIsGoodButSomethingIsWeakThenHeadlineSaysAcceptable()
    {
        // (60 + 70 + 72 + 74) / 4 = 69; only blur is below the problem threshold.
        var advice = ImageQualityAdvisor.Analyze(Summary(blur: 60, exposure: 70, noise: 72, jpeg: 74));

        advice.Verdict.Should().Be(ImageQualityVerdict.Good);
        advice.Problems.Should().ContainSingle();
        advice.Headline.Should().Be("Acceptable (69/100), but blur could be better.");
    }

    [Fact]
    public void WhenVerdictIsExcellentButOneMetricIsWeakThenHeadlineIsTheBareScore()
    {
        // (60 + 100 + 100 + 100) / 4 = 90 — excellent overall, yet blur is still flagged.
        var advice = ImageQualityAdvisor.Analyze(Summary(blur: 60, exposure: 100, noise: 100, jpeg: 100));

        advice.Verdict.Should().Be(ImageQualityVerdict.Excellent);
        advice.Problems.Should().ContainSingle();
        advice.Headline.Should().Be("Score 90/100.");
    }

    [Fact]
    public void WhenWorstMetricIsNotBlurThenHeadlineNamesThatMetric()
    {
        var advice = ImageQualityAdvisor.Analyze(Summary(blur: 60, jpeg: 5));

        advice.Headline.Should().Contain("jpeg quality");
    }

    #endregion

    #region Fix suggestions

    [Fact]
    public void WhenBlurIsSevereThenSuggestReplacingTheSource()
    {
        var advice = ImageQualityAdvisor.Analyze(Summary(blur: 20));

        advice.SuggestedFixes.Should().Equal(FixReplaceOrTrash);
    }

    [Fact]
    public void WhenNoiseIsSevereThenSuggestReplacingTheSource()
    {
        var advice = ImageQualityAdvisor.Analyze(Summary(noise: 20));

        advice.SuggestedFixes.Should().Equal(FixReplaceOrTrash);
    }

    [Fact]
    public void WhenBothBlurAndNoiseAreSevereThenTheReplaceFixIsNotDuplicated()
    {
        var advice = ImageQualityAdvisor.Analyze(Summary(blur: 20, noise: 20));

        advice.SuggestedFixes.Should().Equal(FixReplaceOrTrash);
    }

    [Fact]
    public void WhenExposureIsAProblemThenSuggestEditingAndReshooting()
    {
        var advice = ImageQualityAdvisor.Analyze(Summary(exposure: 50));

        advice.SuggestedFixes.Should().Equal(FixOpenInEditor, FixReshoot);
    }

    [Fact]
    public void WhenJpegIsAProblemThenSuggestReexporting()
    {
        var advice = ImageQualityAdvisor.Analyze(Summary(jpeg: 50));

        advice.SuggestedFixes.Should().Equal(FixReexport);
    }

    [Fact]
    public void WhenOnlyMildBlurThenTheCatchAllEditorFixIsUsed()
    {
        // Blur 50 is a problem but not severe, and nothing else matched — the
        // catch-all keeps the panel from showing an empty fix list.
        var advice = ImageQualityAdvisor.Analyze(Summary(blur: 50));

        advice.SuggestedFixes.Should().Equal(FixOpenInEditor);
    }

    [Fact]
    public void WhenEverythingIsWrongThenAllFixesAppearOnceInPriorityOrder()
    {
        var advice = ImageQualityAdvisor.Analyze(Summary(blur: 10, exposure: 20, noise: 30, jpeg: 40));

        advice.SuggestedFixes.Should().Equal(
            FixReplaceOrTrash, FixOpenInEditor, FixReshoot, FixReexport);
    }

    #endregion
}
