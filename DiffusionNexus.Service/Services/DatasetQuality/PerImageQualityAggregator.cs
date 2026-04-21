using DiffusionNexus.Domain.Models;
using DiffusionNexus.Service.Services.DatasetQuality.ImageAnalysis;

namespace DiffusionNexus.Service.Services.DatasetQuality;

/// <summary>
/// Joins all <see cref="ImageCheckResult.PerImageScores"/> from every image quality
/// check that ran into a single <see cref="PerImageQualitySummary"/> per image,
/// keyed by absolute file path.
/// </summary>
public static class PerImageQualityAggregator
{
    /// <summary>
    /// Builds a per-image quality summary from a collection of image check results.
    /// Only the four scoring image-quality checks (Blur, Exposure, Noise, JPEG) are
    /// represented as columns; other checks (Duplicate, Color Distribution) have
    /// their own dedicated fixers and are ignored here.
    /// </summary>
    /// <param name="results">All image check results from one analysis run.</param>
    /// <returns>One summary per image, keyed by absolute file path (case-insensitive).</returns>
    public static IReadOnlyDictionary<string, PerImageQualitySummary> Aggregate(
        IReadOnlyList<ImageCheckResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        // Working buffer keyed by file path; each value tracks the four nullable score components.
        var working = new Dictionary<string, MutableSummary>(StringComparer.OrdinalIgnoreCase);

        foreach (var result in results)
        {
            foreach (var score in result.PerImageScores)
            {
                if (string.IsNullOrWhiteSpace(score.FilePath))
                    continue;

                if (!working.TryGetValue(score.FilePath, out var summary))
                {
                    summary = new MutableSummary { FilePath = score.FilePath };
                    working[score.FilePath] = summary;
                }

                ApplyScore(summary, result.CheckName, score);
            }
        }

        var output = new Dictionary<string, PerImageQualitySummary>(working.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (path, summary) in working)
        {
            output[path] = summary.ToRecord();
        }

        return output;
    }

    private static void ApplyScore(MutableSummary summary, string checkName, PerImageScore score)
    {
        switch (checkName)
        {
            case BlurDetector.CheckDisplayName:
                summary.BlurScore = score.Score;
                summary.BlurDetail = score.Detail;
                break;
            case ExposureAnalyzer.CheckDisplayName:
                summary.ExposureScore = score.Score;
                summary.ExposureDetail = score.Detail;
                break;
            case NoiseEstimator.CheckDisplayName:
                summary.NoiseScore = score.Score;
                summary.NoiseDetail = score.Detail;
                break;
            case JpegArtifactDetector.CheckDisplayName:
                summary.JpegScore = score.Score;
                summary.JpegDetail = score.Detail;
                break;
            // Duplicate Detection and Color Distribution intentionally ignored:
            // they have their own dedicated fixer dialogs.
        }
    }

    private sealed class MutableSummary
    {
        public string FilePath { get; init; } = string.Empty;
        public double? BlurScore { get; set; }
        public string? BlurDetail { get; set; }
        public double? ExposureScore { get; set; }
        public string? ExposureDetail { get; set; }
        public double? NoiseScore { get; set; }
        public string? NoiseDetail { get; set; }
        public double? JpegScore { get; set; }
        public string? JpegDetail { get; set; }

        public PerImageQualitySummary ToRecord() => new()
        {
            FilePath = FilePath,
            BlurScore = BlurScore,
            BlurDetail = BlurDetail,
            ExposureScore = ExposureScore,
            ExposureDetail = ExposureDetail,
            NoiseScore = NoiseScore,
            NoiseDetail = NoiseDetail,
            JpegScore = JpegScore,
            JpegDetail = JpegDetail
        };
    }
}
