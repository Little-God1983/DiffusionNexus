using DiffusionNexus.Domain.Models;
using FluentAssertions;

namespace DiffusionNexus.Tests.Domain.Models;

/// <summary>
/// Unit tests for the computed <see cref="PerImageQualitySummary.OverallScore"/>
/// aggregation logic.
/// </summary>
public class PerImageQualitySummaryTests
{
    [Fact]
    public void OverallScore_IsNaN_WhenNoChecksRan()
    {
        var summary = new PerImageQualitySummary { FilePath = "image.png" };

        double.IsNaN(summary.OverallScore).Should().BeTrue();
    }

    [Fact]
    public void OverallScore_AveragesAllPresentScores()
    {
        var summary = new PerImageQualitySummary
        {
            FilePath = "image.png",
            BlurScore = 80,
            ExposureScore = 60,
            NoiseScore = 70,
            JpegScore = 90
        };

        summary.OverallScore.Should().Be(75.0);
    }

    [Fact]
    public void OverallScore_IgnoresNullComponents()
    {
        var summary = new PerImageQualitySummary
        {
            FilePath = "image.png",
            BlurScore = 100,
            JpegScore = 50
            // Exposure and Noise intentionally null.
        };

        summary.OverallScore.Should().Be(75.0);
    }

    [Fact]
    public void OverallScore_HandlesSingleComponent()
    {
        var summary = new PerImageQualitySummary
        {
            FilePath = "image.png",
            ExposureScore = 42
        };

        summary.OverallScore.Should().Be(42.0);
    }

    [Fact]
    public void OverallScore_TreatsZeroAsValidScore()
    {
        var summary = new PerImageQualitySummary
        {
            FilePath = "image.png",
            BlurScore = 0,
            ExposureScore = 100
        };

        summary.OverallScore.Should().Be(50.0);
    }
}
