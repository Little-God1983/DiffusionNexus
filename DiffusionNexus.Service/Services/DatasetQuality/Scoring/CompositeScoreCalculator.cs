using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Models;

namespace DiffusionNexus.Service.Services.DatasetQuality.Scoring;

/// <summary>
/// Calculates a weighted composite quality score from individual check scores
/// across four categories: Image Technical Quality, Caption Quality,
/// Dataset Consistency, and Dataset Completeness.
/// </summary>
public static class CompositeScoreCalculator
{
    /// <summary>
    /// Category weights used in composite calculation. Must sum to 1.0.
    /// </summary>
    private static readonly Dictionary<QualityScoreCategory, double> CategoryWeights = new()
    {
        [QualityScoreCategory.ImageTechnicalQuality] = 0.30,
        [QualityScoreCategory.CaptionQuality] = 0.30,
        [QualityScoreCategory.DatasetConsistency] = 0.25,
        [QualityScoreCategory.DatasetCompleteness] = 0.15,
    };

    /// <summary>
    /// Calculates the composite score from a collection of individual check scores.
    /// Only categories with at least one check score participate in the calculation.
    /// </summary>
    /// <param name="checkScores">All individual check scores.</param>
    /// <returns>Composite score result, or null if no scores were provided.</returns>
    public static CompositeScoreResult? Calculate(IReadOnlyList<CheckScore> checkScores)
    {
        if (checkScores == null || checkScores.Count == 0)
            return null;

        // Group by category
        var byCategory = checkScores
            .GroupBy(cs => cs.Category)
            .ToList();

        var categoryScores = new List<CategoryScore>();
        double weightedSum = 0;
        double totalWeight = 0;

        foreach (var group in byCategory)
        {
            var category = group.Key;

            if (!CategoryWeights.TryGetValue(category, out double categoryWeight))
                continue;

            // Weighted average within category
            double checkWeightSum = 0;
            double checkWeightedScore = 0;

            foreach (var cs in group)
            {
                checkWeightedScore += cs.Score * cs.Weight;
                checkWeightSum += cs.Weight;
            }

            double catScore = checkWeightSum > 0
                ? checkWeightedScore / checkWeightSum
                : 0;

            categoryScores.Add(new CategoryScore
            {
                Category = category,
                Score = Math.Round(catScore, 1),
                Weight = categoryWeight,
                CheckScores = group.ToList()
            });

            weightedSum += catScore * categoryWeight;
            totalWeight += categoryWeight;
        }

        if (totalWeight <= 0)
            return null;

        double compositeScore = Math.Round(weightedSum / totalWeight, 1);
        compositeScore = Math.Clamp(compositeScore, 0, 100);

        return new CompositeScoreResult
        {
            Score = compositeScore,
            Label = GetScoreLabel(compositeScore),
            CategoryScores = categoryScores
                .OrderByDescending(cs => cs.Weight)
                .ToList(),
            ParticipatingCategories = categoryScores.Count,
            TotalCategories = CategoryWeights.Count
        };
    }

    /// <summary>
    /// Returns a human-readable label for a composite score.
    /// Uses the same thresholds as <see cref="BucketAnalyzer.GetScoreLabel"/>.
    /// </summary>
    public static string GetScoreLabel(double score) => score switch
    {
        >= 85 => "Excellent",
        >= 65 => "Good",
        >= 40 => "Fair",
        _ => "Poor"
    };
}
