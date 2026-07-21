using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Models;
using DiffusionNexus.Service.Services.DatasetQuality.Scoring;
using FluentAssertions;

namespace DiffusionNexus.Tests.DatasetQuality.Scoring;

/// <summary>
/// Unit tests for <see cref="CompositeScoreCalculator"/>: the weighted composite score,
/// the per-category breakdown, the label thresholds and the degenerate inputs
/// (empty, unknown category, zero weights, out-of-range scores).
/// </summary>
public class CompositeScoreCalculatorTests
{
    private static CheckScore Score(
        double score,
        QualityScoreCategory category,
        string name = "Check",
        double weight = 1.0) => new()
        {
            Score = score,
            CheckName = name,
            Category = category,
            Weight = weight
        };

    #region Calculate — degenerate inputs

    [Fact]
    public void WhenCheckScoresIsNullThenReturnsNull()
    {
        CompositeScoreCalculator.Calculate(null!).Should().BeNull();
    }

    [Fact]
    public void WhenCheckScoresIsEmptyThenReturnsNull()
    {
        CompositeScoreCalculator.Calculate([]).Should().BeNull();
    }

    [Fact]
    public void WhenEveryCategoryIsUnknownThenReturnsNull()
    {
        // A category outside the weight table contributes no weight, so the total
        // weight stays zero and the calculator reports "nothing to score".
        var scores = new[] { Score(90, (QualityScoreCategory)99) };

        CompositeScoreCalculator.Calculate(scores).Should().BeNull();
    }

    [Fact]
    public void WhenSomeCategoriesAreUnknownThenOnlyKnownOnesParticipate()
    {
        var scores = new[]
        {
            Score(80, QualityScoreCategory.CaptionQuality),
            Score(0, (QualityScoreCategory)99)
        };

        var result = CompositeScoreCalculator.Calculate(scores);

        result.Should().NotBeNull();
        result!.Score.Should().Be(80.0);
        result.ParticipatingCategories.Should().Be(1);
        result.CategoryScores.Should().ContainSingle()
            .Which.Category.Should().Be(QualityScoreCategory.CaptionQuality);
    }

    #endregion

    #region Calculate — weighting

    [Fact]
    public void WhenSingleCategoryThenCompositeEqualsThatCategoryScore()
    {
        var scores = new[] { Score(90, QualityScoreCategory.CaptionQuality) };

        var result = CompositeScoreCalculator.Calculate(scores);

        result.Should().NotBeNull();
        result!.Score.Should().Be(90.0);
        result.Label.Should().Be("Excellent");
        result.ParticipatingCategories.Should().Be(1);
        result.TotalCategories.Should().Be(4);
    }

    [Fact]
    public void WhenCategoryHasMultipleChecksThenScoreIsCheckWeightedAverage()
    {
        // (100 * 1.0 + 0 * 3.0) / 4.0 = 25
        var scores = new[]
        {
            Score(100, QualityScoreCategory.CaptionQuality, "Cheap", weight: 1.0),
            Score(0, QualityScoreCategory.CaptionQuality, "Expensive", weight: 3.0)
        };

        var result = CompositeScoreCalculator.Calculate(scores);

        result.Should().NotBeNull();
        result!.Score.Should().Be(25.0);
        result.CategoryScores.Should().ContainSingle()
            .Which.Score.Should().Be(25.0);
    }

    [Fact]
    public void WhenAllCheckWeightsAreZeroThenCategoryScoresZero()
    {
        var scores = new[]
        {
            Score(100, QualityScoreCategory.CaptionQuality, "A", weight: 0),
            Score(100, QualityScoreCategory.CaptionQuality, "B", weight: 0)
        };

        var result = CompositeScoreCalculator.Calculate(scores);

        result.Should().NotBeNull();
        result!.Score.Should().Be(0.0);
        result.Label.Should().Be("Poor");
    }

    [Fact]
    public void WhenTwoCategoriesParticipateThenCompositeIsRenormalizedByTheirWeights()
    {
        // CaptionQuality weight 0.30, DatasetCompleteness weight 0.15.
        // (100 * 0.30 + 0 * 0.15) / 0.45 = 66.666… → 66.7
        var scores = new[]
        {
            Score(100, QualityScoreCategory.CaptionQuality),
            Score(0, QualityScoreCategory.DatasetCompleteness)
        };

        var result = CompositeScoreCalculator.Calculate(scores);

        result.Should().NotBeNull();
        result!.Score.Should().Be(66.7);
        result.Label.Should().Be("Good");
        result.ParticipatingCategories.Should().Be(2);
    }

    [Fact]
    public void WhenAllFourCategoriesScorePerfectThenCompositeIsOneHundred()
    {
        var scores = new[]
        {
            Score(100, QualityScoreCategory.ImageTechnicalQuality),
            Score(100, QualityScoreCategory.CaptionQuality),
            Score(100, QualityScoreCategory.DatasetConsistency),
            Score(100, QualityScoreCategory.DatasetCompleteness)
        };

        var result = CompositeScoreCalculator.Calculate(scores);

        result.Should().NotBeNull();
        result!.Score.Should().Be(100.0);
        result.Label.Should().Be("Excellent");
        result.ParticipatingCategories.Should().Be(4);
    }

    [Fact]
    public void WhenCategoriesArrivedOutOfOrderThenBreakdownIsSortedByWeightDescending()
    {
        var scores = new[]
        {
            Score(50, QualityScoreCategory.DatasetCompleteness), // 0.15
            Score(50, QualityScoreCategory.DatasetConsistency),  // 0.25
            Score(50, QualityScoreCategory.CaptionQuality)       // 0.30
        };

        var result = CompositeScoreCalculator.Calculate(scores);

        result.Should().NotBeNull();
        result!.CategoryScores.Select(c => c.Category).Should().Equal(
            QualityScoreCategory.CaptionQuality,
            QualityScoreCategory.DatasetConsistency,
            QualityScoreCategory.DatasetCompleteness);
        result.CategoryScores.Select(c => c.Weight).Should().Equal(0.30, 0.25, 0.15);
    }

    [Fact]
    public void WhenCategoryScoredThenItKeepsItsContributingCheckScores()
    {
        var a = Score(100, QualityScoreCategory.CaptionQuality, "A");
        var b = Score(60, QualityScoreCategory.CaptionQuality, "B");

        var result = CompositeScoreCalculator.Calculate([a, b]);

        result.Should().NotBeNull();
        result!.CategoryScores.Should().ContainSingle()
            .Which.CheckScores.Should().BeEquivalentTo(new[] { a, b });
    }

    #endregion

    #region Calculate — out-of-range scores

    [Fact]
    public void WhenScoresExceedOneHundredThenCompositeIsClampedButCategoryIsNot()
    {
        var scores = new[] { Score(150, QualityScoreCategory.CaptionQuality) };

        var result = CompositeScoreCalculator.Calculate(scores);

        result.Should().NotBeNull();
        result!.Score.Should().Be(100.0);
        result.Label.Should().Be("Excellent");
        // Only the composite is clamped — the per-category breakdown passes the raw value through.
        result.CategoryScores.Single().Score.Should().Be(150.0);
    }

    [Fact]
    public void WhenScoresAreNegativeThenCompositeIsClampedToZero()
    {
        var scores = new[] { Score(-50, QualityScoreCategory.ImageTechnicalQuality) };

        var result = CompositeScoreCalculator.Calculate(scores);

        result.Should().NotBeNull();
        result!.Score.Should().Be(0.0);
        result.Label.Should().Be("Poor");
    }

    [Fact]
    public void WhenCompositeHasManyDecimalsThenItIsRoundedToOnePlace()
    {
        // (100 * 1 + 0 * 2) / 3 = 33.333…
        var scores = new[]
        {
            Score(100, QualityScoreCategory.CaptionQuality, "A", weight: 1.0),
            Score(0, QualityScoreCategory.CaptionQuality, "B", weight: 2.0)
        };

        var result = CompositeScoreCalculator.Calculate(scores);

        result.Should().NotBeNull();
        result!.Score.Should().Be(33.3);
    }

    #endregion

    #region GetScoreLabel — boundaries

    [Theory]
    [InlineData(100, "Excellent")]
    [InlineData(85.0, "Excellent")]   // lower bound of Excellent
    [InlineData(84.9, "Good")]
    [InlineData(65.0, "Good")]        // lower bound of Good
    [InlineData(64.9, "Fair")]
    [InlineData(40.0, "Fair")]        // lower bound of Fair
    [InlineData(39.9, "Poor")]
    [InlineData(0, "Poor")]
    [InlineData(-10, "Poor")]
    public void WhenScoreGivenThenLabelMatchesThreshold(double score, string expected)
    {
        CompositeScoreCalculator.GetScoreLabel(score).Should().Be(expected);
    }

    [Fact]
    public void WhenScoreIsNaNThenLabelIsPoor()
    {
        // All the relational patterns are false for NaN, so it falls to the catch-all arm.
        CompositeScoreCalculator.GetScoreLabel(double.NaN).Should().Be("Poor");
    }

    #endregion
}
