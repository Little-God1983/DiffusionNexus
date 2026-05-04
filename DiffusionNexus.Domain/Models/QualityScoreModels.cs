using DiffusionNexus.Domain.Enums;

namespace DiffusionNexus.Domain.Models;

/// <summary>
/// Result of running a single <see cref="DiffusionNexus.Domain.Services.IImageQualityCheck"/>.
/// </summary>
public record ImageCheckResult
{
    /// <summary>
    /// Overall score for this check (0–100). Higher is better.
    /// </summary>
    public required double Score { get; init; }

    /// <summary>
    /// Name of the check that produced this result.
    /// </summary>
    public required string CheckName { get; init; }

    /// <summary>
    /// Issues detected during the check.
    /// </summary>
    public required IReadOnlyList<Issue> Issues { get; init; }

    /// <summary>
    /// Per-image score breakdown.
    /// </summary>
    public required IReadOnlyList<PerImageScore> PerImageScores { get; init; }
}

/// <summary>
/// Quality score for a single image from one check.
/// </summary>
/// <param name="FilePath">Absolute path to the image.</param>
/// <param name="Score">Score for this image (0–100). Higher is better.</param>
/// <param name="Detail">Human-readable detail (e.g. "Laplacian variance: 452").</param>
public record PerImageScore(string FilePath, double Score, string? Detail);

/// <summary>
/// Aggregated per-image quality summary across every image quality check that ran.
/// One instance per image, keyed by absolute file path. All score fields are
/// nullable so the consumer can tell "check did not run" apart from "scored zero".
/// </summary>
public record PerImageQualitySummary
{
    /// <summary>Absolute path to the image.</summary>
    public required string FilePath { get; init; }

    /// <summary>Blur score (0–100, higher = sharper) or null if the check did not run.</summary>
    public double? BlurScore { get; init; }

    /// <summary>Human-readable blur detail (e.g. "Laplacian variance: 67").</summary>
    public string? BlurDetail { get; init; }

    /// <summary>Exposure score (0–100, higher = better exposed) or null if the check did not run.</summary>
    public double? ExposureScore { get; init; }

    /// <summary>Human-readable exposure detail (e.g. "8% clipped highlights").</summary>
    public string? ExposureDetail { get; init; }

    /// <summary>Noise score (0–100, higher = cleaner) or null if the check did not run.</summary>
    public double? NoiseScore { get; init; }

    /// <summary>Human-readable noise detail (e.g. "Estimated sigma 4.2").</summary>
    public string? NoiseDetail { get; init; }

    /// <summary>JPEG quality score (0–100, higher = better) or null if the check did not run.</summary>
    public double? JpegScore { get; init; }

    /// <summary>Human-readable JPEG detail (e.g. "Estimated quality factor 62").</summary>
    public string? JpegDetail { get; init; }

    /// <summary>
    /// Mean of all non-null score components. Returns NaN when no checks reported a score.
    /// </summary>
    public double OverallScore
    {
        get
        {
            var values = new List<double>(4);
            if (BlurScore is { } b) values.Add(b);
            if (ExposureScore is { } e) values.Add(e);
            if (NoiseScore is { } n) values.Add(n);
            if (JpegScore is { } j) values.Add(j);
            return values.Count == 0 ? double.NaN : values.Average();
        }
    }
}

/// <summary>
/// A scored result from any check (caption or image), normalized for composite calculation.
/// </summary>
public record CheckScore
{
    /// <summary>Score value (0–100).</summary>
    public required double Score { get; init; }

    /// <summary>Name of the check.</summary>
    public required string CheckName { get; init; }

    /// <summary>Scoring category this check belongs to.</summary>
    public required QualityScoreCategory Category { get; init; }

    /// <summary>Relative weight within its category (default 1.0).</summary>
    public double Weight { get; init; } = 1.0;
}

/// <summary>
/// Per-category score breakdown for the composite score.
/// </summary>
public record CategoryScore
{
    /// <summary>The category.</summary>
    public required QualityScoreCategory Category { get; init; }

    /// <summary>Weighted average score for this category (0–100).</summary>
    public required double Score { get; init; }

    /// <summary>Category weight used in composite calculation (0.0–1.0).</summary>
    public required double Weight { get; init; }

    /// <summary>Individual check scores that contributed.</summary>
    public required IReadOnlyList<CheckScore> CheckScores { get; init; }
}

/// <summary>
/// Complete composite quality score for a dataset.
/// </summary>
public record CompositeScoreResult
{
    /// <summary>
    /// Overall composite score (0–100). Higher is better.
    /// </summary>
    public required double Score { get; init; }

    /// <summary>
    /// Human-readable label: Poor / Fair / Good / Excellent.
    /// </summary>
    public required string Label { get; init; }

    /// <summary>
    /// Per-category breakdown.
    /// </summary>
    public required IReadOnlyList<CategoryScore> CategoryScores { get; init; }

    /// <summary>
    /// Number of scoring categories that had at least one check run.
    /// </summary>
    public required int ParticipatingCategories { get; init; }

    /// <summary>
    /// Total number of scoring categories.
    /// </summary>
    public int TotalCategories { get; init; } = 4;
}
