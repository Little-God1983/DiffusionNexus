using DiffusionNexus.Domain.Models;
using DiffusionNexus.Service.Services.DatasetQuality.ImageAnalysis;

namespace DiffusionNexus.Service.Services.DatasetQuality;

/// <summary>
/// Joins all <see cref="ImageCheckResult.PerImageScores"/> from every image quality
/// check that ran into a single <see cref="PerImageQualitySummary"/> per image,
/// keyed by absolute file path.
/// </summary>
/// <remarks>
/// <para>
/// <b>No-score-row contract (issue #449):</b> an image is keyed into the result as soon
/// as ANY check reports a <see cref="PerImageScore"/> for it — including checks that are
/// intentionally ignored here (Duplicate Detection, Color Distribution; see
/// <see cref="ApplyScore"/>) and any unrecognized/future check name that falls through
/// the switch below. When none of the four scoring columns (Blur/Exposure/Noise/JPEG)
/// end up populated for that image, the row is still returned rather than dropped: all
/// four score columns are <see langword="null"/> and
/// <see cref="PerImageQualitySummary.OverallScore"/> evaluates to
/// <see cref="double.NaN"/>.
/// </para>
/// <para>
/// This is a deliberate decision, not an accident. Suppressing the row would silently
/// hide the image from the Image Quality Fixer grid, which is worse than showing an
/// explicit "no score data" row for it. Callers MUST treat <c>OverallScore == NaN</c> as
/// "no quality checks scored this image" — never as a real score of zero, and never
/// let it flow unguarded into arithmetic (NaN silently propagates through sums/averages,
/// and NaN comparisons are always <see langword="false"/>). The canonical consumer is
/// <see cref="ImageQualityAdvisor.Analyze(PerImageQualitySummary)"/>, which maps NaN to
/// the distinct <see cref="ImageQualityVerdict.Unknown"/> verdict ("No quality checks ran
/// for this image") instead of trying to band a non-existent score. The Image Quality
/// Fixer grid's sort likewise special-cases NaN so these rows land at the correct end
/// regardless of sort direction, rather than participating in a NaN-corrupted ordering.
/// </para>
/// </remarks>
public static class PerImageQualityAggregator
{
    /// <summary>
    /// Builds a per-image quality summary from a collection of image check results.
    /// Only the four scoring image-quality checks (Blur, Exposure, Noise, JPEG) are
    /// represented as columns; other checks (Duplicate, Color Distribution) have
    /// their own dedicated fixers and are ignored here.
    /// </summary>
    /// <param name="results">All image check results from one analysis run.</param>
    /// <returns>
    /// One summary per image, keyed by absolute file path (case-insensitive). Includes
    /// "no score data" rows (all-null columns, NaN <see cref="PerImageQualitySummary.OverallScore"/>)
    /// for images seen only by ignored or unrecognized checks — see the type-level
    /// remarks for the contract this implies for callers.
    /// </returns>
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
