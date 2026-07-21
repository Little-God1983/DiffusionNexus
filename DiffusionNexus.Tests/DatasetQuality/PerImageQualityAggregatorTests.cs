using DiffusionNexus.Domain.Models;
using DiffusionNexus.Service.Services.DatasetQuality;
using DiffusionNexus.Service.Services.DatasetQuality.ImageAnalysis;
using FluentAssertions;

namespace DiffusionNexus.Tests.DatasetQuality;

/// <summary>
/// Unit tests for <see cref="PerImageQualityAggregator"/>: joining the per-image
/// scores of several image checks into one summary per file, which checks map to
/// which column, and the case-insensitive path keying.
/// </summary>
public class PerImageQualityAggregatorTests
{
    private static ImageCheckResult Result(string checkName, params PerImageScore[] scores) => new()
    {
        Score = 0,
        CheckName = checkName,
        Issues = [],
        PerImageScores = scores
    };

    #region Degenerate inputs

    [Fact]
    public void WhenResultsIsNullThenThrowsArgumentNullException()
    {
        var act = () => PerImageQualityAggregator.Aggregate(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void WhenResultsIsEmptyThenReturnsEmptyDictionary()
    {
        PerImageQualityAggregator.Aggregate([]).Should().BeEmpty();
    }

    [Fact]
    public void WhenChecksProducedNoPerImageScoresThenReturnsEmptyDictionary()
    {
        var results = new[]
        {
            Result(BlurDetector.CheckDisplayName),
            Result(NoiseEstimator.CheckDisplayName)
        };

        PerImageQualityAggregator.Aggregate(results).Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void WhenFilePathIsBlankThenScoreIsSkipped(string filePath)
    {
        var results = new[]
        {
            Result(BlurDetector.CheckDisplayName, new PerImageScore(filePath, 50, "detail"))
        };

        PerImageQualityAggregator.Aggregate(results).Should().BeEmpty();
    }

    #endregion

    #region Aggregation

    [Fact]
    public void WhenAllFourChecksScoredOneImageThenSummaryHasEveryColumn()
    {
        const string path = @"C:\ds\img.png";
        var results = new[]
        {
            Result(BlurDetector.CheckDisplayName, new PerImageScore(path, 80, "Laplacian variance: 452")),
            Result(ExposureAnalyzer.CheckDisplayName, new PerImageScore(path, 60, "8% clipped highlights")),
            Result(NoiseEstimator.CheckDisplayName, new PerImageScore(path, 70, "Estimated sigma 4.2")),
            Result(JpegArtifactDetector.CheckDisplayName, new PerImageScore(path, 90, "Quality factor 88"))
        };

        var summaries = PerImageQualityAggregator.Aggregate(results);

        summaries.Should().ContainSingle();
        var summary = summaries[path];
        summary.FilePath.Should().Be(path);
        summary.BlurScore.Should().Be(80);
        summary.BlurDetail.Should().Be("Laplacian variance: 452");
        summary.ExposureScore.Should().Be(60);
        summary.ExposureDetail.Should().Be("8% clipped highlights");
        summary.NoiseScore.Should().Be(70);
        summary.NoiseDetail.Should().Be("Estimated sigma 4.2");
        summary.JpegScore.Should().Be(90);
        summary.JpegDetail.Should().Be("Quality factor 88");
        summary.OverallScore.Should().Be(75);
    }

    [Fact]
    public void WhenOnlySomeChecksRanThenTheOtherColumnsStayNull()
    {
        const string path = @"C:\ds\img.png";
        var results = new[]
        {
            Result(BlurDetector.CheckDisplayName, new PerImageScore(path, 42, null))
        };

        var summary = PerImageQualityAggregator.Aggregate(results)[path];

        summary.BlurScore.Should().Be(42);
        summary.BlurDetail.Should().BeNull();
        summary.ExposureScore.Should().BeNull();
        summary.NoiseScore.Should().BeNull();
        summary.JpegScore.Should().BeNull();
    }

    [Fact]
    public void WhenSeveralImagesScoredThenOneSummaryPerImageIsProduced()
    {
        var results = new[]
        {
            Result(BlurDetector.CheckDisplayName,
                new PerImageScore(@"C:\ds\a.png", 10, null),
                new PerImageScore(@"C:\ds\b.png", 20, null)),
            Result(NoiseEstimator.CheckDisplayName,
                new PerImageScore(@"C:\ds\b.png", 30, null),
                new PerImageScore(@"C:\ds\c.png", 40, null))
        };

        var summaries = PerImageQualityAggregator.Aggregate(results);

        summaries.Should().HaveCount(3);
        summaries[@"C:\ds\a.png"].BlurScore.Should().Be(10);
        summaries[@"C:\ds\a.png"].NoiseScore.Should().BeNull();
        summaries[@"C:\ds\b.png"].BlurScore.Should().Be(20);
        summaries[@"C:\ds\b.png"].NoiseScore.Should().Be(30);
        summaries[@"C:\ds\c.png"].BlurScore.Should().BeNull();
        summaries[@"C:\ds\c.png"].NoiseScore.Should().Be(40);
    }

    [Fact]
    public void WhenMixedSeverityScoresAggregatedThenOverallScoreAveragesThemAll()
    {
        const string good = @"C:\ds\good.png";
        const string bad = @"C:\ds\bad.png";
        var results = new[]
        {
            Result(BlurDetector.CheckDisplayName,
                new PerImageScore(good, 95, null),
                new PerImageScore(bad, 10, null)),
            Result(ExposureAnalyzer.CheckDisplayName,
                new PerImageScore(good, 85, null),
                new PerImageScore(bad, 30, null))
        };

        var summaries = PerImageQualityAggregator.Aggregate(results);

        summaries[good].OverallScore.Should().Be(90);
        summaries[bad].OverallScore.Should().Be(20);
    }

    [Fact]
    public void WhenTheSameCheckScoresAFileTwiceThenTheLastValueWins()
    {
        const string path = @"C:\ds\img.png";
        var results = new[]
        {
            Result(BlurDetector.CheckDisplayName,
                new PerImageScore(path, 10, "first"),
                new PerImageScore(path, 90, "second"))
        };

        var summary = PerImageQualityAggregator.Aggregate(results)[path];

        summary.BlurScore.Should().Be(90);
        summary.BlurDetail.Should().Be("second");
    }

    #endregion

    #region Ignored checks

    [Theory]
    [InlineData(DuplicateDetector.CheckDisplayName)]
    [InlineData(ColorDistributionAnalyzer.CheckDisplayName)]
    public void WhenCheckHasItsOwnFixerThenItProducesNoScoreColumns(string checkName)
    {
        const string path = @"C:\ds\img.png";
        var results = new[] { Result(checkName, new PerImageScore(path, 5, "detail")) };

        var summaries = PerImageQualityAggregator.Aggregate(results);

        // The image is still keyed (it was seen), but none of the four columns are filled.
        summaries.Should().ContainSingle();
        var summary = summaries[path];
        summary.BlurScore.Should().BeNull();
        summary.ExposureScore.Should().BeNull();
        summary.NoiseScore.Should().BeNull();
        summary.JpegScore.Should().BeNull();
        double.IsNaN(summary.OverallScore).Should().BeTrue();
    }

    [Fact]
    public void WhenUnknownCheckNameThenNoColumnIsPopulated()
    {
        const string path = @"C:\ds\img.png";
        var results = new[] { Result("Some Future Check", new PerImageScore(path, 5, "detail")) };

        var summary = PerImageQualityAggregator.Aggregate(results)[path];

        summary.BlurScore.Should().BeNull();
        summary.JpegScore.Should().BeNull();
    }

    #endregion

    #region Case-insensitive keying

    [Fact]
    public void WhenChecksReportDifferentPathCasingThenScoresMergeIntoOneSummary()
    {
        var results = new[]
        {
            Result(BlurDetector.CheckDisplayName, new PerImageScore(@"C:\ds\Img.png", 80, null)),
            Result(NoiseEstimator.CheckDisplayName, new PerImageScore(@"c:\DS\IMG.PNG", 60, null))
        };

        var summaries = PerImageQualityAggregator.Aggregate(results);

        summaries.Should().ContainSingle();
        var summary = summaries.Values.Single();
        summary.BlurScore.Should().Be(80);
        summary.NoiseScore.Should().Be(60);
        // The first-seen spelling is the one that survives.
        summary.FilePath.Should().Be(@"C:\ds\Img.png");
    }

    [Fact]
    public void WhenLookingUpWithDifferentCasingThenTheSummaryIsStillFound()
    {
        var results = new[]
        {
            Result(BlurDetector.CheckDisplayName, new PerImageScore(@"C:\ds\Img.png", 80, null))
        };

        var summaries = PerImageQualityAggregator.Aggregate(results);

        summaries.ContainsKey(@"c:\DS\IMG.PNG").Should().BeTrue();
    }

    #endregion
}
