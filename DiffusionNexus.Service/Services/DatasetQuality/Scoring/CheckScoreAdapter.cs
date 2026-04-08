using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Models;

namespace DiffusionNexus.Service.Services.DatasetQuality.Scoring;

/// <summary>
/// Converts issue-based results from legacy <see cref="DiffusionNexus.Domain.Services.IDatasetCheck"/>
/// implementations into numeric 0–100 scores for composite calculation.
/// </summary>
public static class CheckScoreAdapter
{
    /// <summary>Penalty per Critical issue, as a fraction of total files.</summary>
    private const double CriticalPenalty = 20.0;

    /// <summary>Penalty per Warning issue, as a fraction of total files.</summary>
    private const double WarningPenalty = 5.0;

    /// <summary>Penalty per Info issue, as a fraction of total files.</summary>
    private const double InfoPenalty = 1.0;

    /// <summary>
    /// Maps a check name to its scoring category and weight.
    /// </summary>
    private static readonly Dictionary<string, (QualityScoreCategory Category, double Weight)> CheckCategoryMap = new()
    {
        // Caption quality checks (weight reflects importance)
        ["Format Consistency"] = (QualityScoreCategory.CaptionQuality, 1.2),
        ["Trigger Word"] = (QualityScoreCategory.CaptionQuality, 1.5),
        ["Synonym Consistency"] = (QualityScoreCategory.CaptionQuality, 1.0),
        ["Feature Consistency"] = (QualityScoreCategory.CaptionQuality, 1.0),
        ["Type-Specific"] = (QualityScoreCategory.CaptionQuality, 1.0),
        ["Spelling"] = (QualityScoreCategory.CaptionQuality, 0.5),

        // Bucket analysis (Dataset Consistency)
        ["Bucket Analysis"] = (QualityScoreCategory.DatasetConsistency, 1.0),
    };

    /// <summary>
    /// Converts issues from a caption check into a <see cref="CheckScore"/>.
    /// </summary>
    /// <param name="checkName">Name of the check that produced the issues.</param>
    /// <param name="issues">Issues produced by the check.</param>
    /// <param name="totalFiles">Total number of files analyzed (for normalization).</param>
    /// <returns>A normalized check score, or null if the check name is unknown.</returns>
    public static CheckScore? ScoreFromIssues(string checkName, IReadOnlyList<Issue> issues, int totalFiles)
    {
        if (!CheckCategoryMap.TryGetValue(checkName, out var mapping))
            return null;

        double score = CalculateScoreFromIssues(issues, totalFiles);

        return new CheckScore
        {
            Score = score,
            CheckName = checkName,
            Category = mapping.Category,
            Weight = mapping.Weight
        };
    }

    /// <summary>
    /// Creates a <see cref="CheckScore"/> from a bucket analysis result.
    /// </summary>
    public static CheckScore ScoreFromBucketAnalysis(double distributionScore)
    {
        return new CheckScore
        {
            Score = Math.Clamp(distributionScore, 0, 100),
            CheckName = "Bucket Analysis",
            Category = QualityScoreCategory.DatasetConsistency,
            Weight = 1.0
        };
    }

    /// <summary>
    /// Creates a <see cref="CheckScore"/> for dataset completeness based on
    /// the ratio of paired captions to total images.
    /// </summary>
    public static CheckScore ScoreFromCompleteness(int totalCaptions, int totalImages)
    {
        double score;
        if (totalImages == 0)
        {
            score = 0;
        }
        else if (totalCaptions >= totalImages)
        {
            score = 100;
        }
        else
        {
            score = (double)totalCaptions / totalImages * 100;
        }

        return new CheckScore
        {
            Score = Math.Round(score, 1),
            CheckName = "Dataset Completeness",
            Category = QualityScoreCategory.DatasetCompleteness,
            Weight = 1.0
        };
    }

    /// <summary>
    /// Converts a list of issues into a 0–100 score.
    /// Score = 100 - (weighted penalty sum / max(totalFiles, 1)) * 100, clamped to [0, 100].
    /// </summary>
    internal static double CalculateScoreFromIssues(IReadOnlyList<Issue> issues, int totalFiles)
    {
        if (issues.Count == 0)
            return 100;

        int effectiveTotal = Math.Max(totalFiles, 1);

        double totalPenalty = 0;
        foreach (var issue in issues)
        {
            double issuePenalty = issue.Severity switch
            {
                IssueSeverity.Critical => CriticalPenalty,
                IssueSeverity.Warning => WarningPenalty,
                IssueSeverity.Info => InfoPenalty,
                _ => 0
            };

            // Scale by number of affected files (at least 1 per issue)
            int affectedCount = Math.Max(issue.AffectedFiles.Count, 1);
            totalPenalty += issuePenalty * affectedCount;
        }

        double score = 100.0 - (totalPenalty / effectiveTotal);
        return Math.Round(Math.Clamp(score, 0, 100), 1);
    }
}
