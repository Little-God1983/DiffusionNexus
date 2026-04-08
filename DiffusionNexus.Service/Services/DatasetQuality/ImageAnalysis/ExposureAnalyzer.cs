using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Models;
using DiffusionNexus.Domain.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace DiffusionNexus.Service.Services.DatasetQuality.ImageAnalysis;

/// <summary>
/// Analyzes image exposure quality using luminance histogram statistics.
/// Detects over-exposure, under-exposure, clipped highlights/shadows, and
/// low dynamic range. Poorly exposed images teach the model incorrect
/// lighting conditions.
/// </summary>
public sealed class ExposureAnalyzer : IImageQualityCheck
{
    /// <summary>Check name used on all generated issues.</summary>
    public const string CheckDisplayName = "Exposure Analysis";

    /// <summary>Maximum dimension for analysis.</summary>
    internal const int MaxAnalysisSize = 512;

    /// <summary>Mean brightness below this is flagged as Critical (severely underexposed).</summary>
    internal const double MeanCriticalLow = 40.0;

    /// <summary>Mean brightness above this is flagged as Critical (severely overexposed).</summary>
    internal const double MeanCriticalHigh = 220.0;

    /// <summary>Lower bound of the ideal brightness range.</summary>
    internal const double MeanIdealLow = 80.0;

    /// <summary>Upper bound of the ideal brightness range.</summary>
    internal const double MeanIdealHigh = 180.0;

    /// <summary>Percentage of clipped highlights above this is a Warning.</summary>
    internal const double ClipWarningThreshold = 15.0;

    /// <summary>Percentage of crushed shadows above this is a Warning.</summary>
    internal const double CrushWarningThreshold = 15.0;

    /// <summary>Standard deviation below this indicates low dynamic range (flat image).</summary>
    internal const double LowDynamicRangeThreshold = 30.0;

    /// <summary>Pixel value above which highlights are considered clipped.</summary>
    internal const int HighlightClipValue = 250;

    /// <summary>Pixel value below which shadows are considered crushed.</summary>
    internal const int ShadowCrushValue = 5;

    public string Name => CheckDisplayName;
    public string Description => "Detects over/under-exposed images using luminance histogram analysis.";
    public int Order => 11;
    public bool RequiresGpu => false;
    public QualityScoreCategory Category => QualityScoreCategory.ImageTechnicalQuality;

    public bool IsApplicable(LoraType loraType) => true;

    public async Task<ImageCheckResult> RunAsync(
        IReadOnlyList<ImageFileInfo> images,
        DatasetConfig config,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(config);

        if (images.Count == 0)
        {
            return new ImageCheckResult
            {
                Score = 100,
                CheckName = Name,
                Issues = [],
                PerImageScores = []
            };
        }

        var perImageScores = new List<PerImageScore>(images.Count);
        var criticalOverexposed = new List<string>();
        var criticalUnderexposed = new List<string>();
        var warningClippedHighlights = new List<string>();
        var warningCrushedShadows = new List<string>();
        var infoLowDynamicRange = new List<string>();

        for (int i = 0; i < images.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var img = images[i];
            var stats = await Task.Run(() => ComputeExposureStats(img.FilePath), cancellationToken);
            double score = StatsToScore(stats);

            perImageScores.Add(new PerImageScore(
                img.FilePath,
                Math.Round(score, 1),
                $"Mean: {stats.Mean:F1}, StdDev: {stats.StdDev:F1}, "
                + $"Highlights: {stats.ClippedHighlightPercent:F1}%, "
                + $"Shadows: {stats.CrushedShadowPercent:F1}%"));

            if (stats.Mean < MeanCriticalLow)
                criticalUnderexposed.Add(img.FilePath);
            else if (stats.Mean > MeanCriticalHigh)
                criticalOverexposed.Add(img.FilePath);

            if (stats.ClippedHighlightPercent > ClipWarningThreshold)
                warningClippedHighlights.Add(img.FilePath);

            if (stats.CrushedShadowPercent > CrushWarningThreshold)
                warningCrushedShadows.Add(img.FilePath);

            if (stats.StdDev < LowDynamicRangeThreshold
                && stats.Mean >= MeanCriticalLow
                && stats.Mean <= MeanCriticalHigh)
                infoLowDynamicRange.Add(img.FilePath);

            progress?.Report((double)(i + 1) / images.Count);
        }

        var issues = new List<Issue>();

        if (criticalUnderexposed.Count > 0)
        {
            issues.Add(new Issue
            {
                Severity = IssueSeverity.Critical,
                Message = $"{criticalUnderexposed.Count} image(s) are severely underexposed (very dark).",
                Details = $"Images with mean brightness below {MeanCriticalLow:F0}/255 have lost most "
                        + "shadow detail. The model will learn to produce overly dark outputs.",
                Domain = CheckDomain.Image,
                CheckName = Name,
                AffectedFiles = criticalUnderexposed
            });
        }

        if (criticalOverexposed.Count > 0)
        {
            issues.Add(new Issue
            {
                Severity = IssueSeverity.Critical,
                Message = $"{criticalOverexposed.Count} image(s) are severely overexposed (blown out).",
                Details = $"Images with mean brightness above {MeanCriticalHigh:F0}/255 have lost most "
                        + "highlight detail. The model will learn to produce washed-out outputs.",
                Domain = CheckDomain.Image,
                CheckName = Name,
                AffectedFiles = criticalOverexposed
            });
        }

        if (warningClippedHighlights.Count > 0)
        {
            issues.Add(new Issue
            {
                Severity = IssueSeverity.Warning,
                Message = $"{warningClippedHighlights.Count} image(s) have >15% clipped highlights.",
                Details = "Significant portions of these images are pure white, indicating lost highlight "
                        + "detail from overexposure or processing.",
                Domain = CheckDomain.Image,
                CheckName = Name,
                AffectedFiles = warningClippedHighlights
            });
        }

        if (warningCrushedShadows.Count > 0)
        {
            issues.Add(new Issue
            {
                Severity = IssueSeverity.Warning,
                Message = $"{warningCrushedShadows.Count} image(s) have >15% crushed shadows.",
                Details = "Significant portions of these images are near-black, indicating lost shadow "
                        + "detail from underexposure or heavy contrast.",
                Domain = CheckDomain.Image,
                CheckName = Name,
                AffectedFiles = warningCrushedShadows
            });
        }

        if (infoLowDynamicRange.Count > 0)
        {
            issues.Add(new Issue
            {
                Severity = IssueSeverity.Info,
                Message = $"{infoLowDynamicRange.Count} image(s) have low dynamic range (flat/low contrast).",
                Details = "These images have a narrow brightness range (standard deviation < "
                        + $"{LowDynamicRangeThreshold:F0}). This isn't necessarily bad but may indicate "
                        + "washed-out or heavily processed images.",
                Domain = CheckDomain.Image,
                CheckName = Name,
                AffectedFiles = infoLowDynamicRange
            });
        }

        double overallScore = perImageScores.Count > 0
            ? perImageScores.Average(s => s.Score)
            : 100;

        return new ImageCheckResult
        {
            Score = Math.Round(overallScore, 1),
            CheckName = Name,
            Issues = issues,
            PerImageScores = perImageScores
        };
    }

    /// <summary>
    /// Computes exposure statistics from an image's luminance histogram.
    /// </summary>
    internal static ExposureStats ComputeExposureStats(string filePath)
    {
        using var image = Image.Load<L8>(filePath);

        // Resize for consistent analysis
        if (image.Width > MaxAnalysisSize || image.Height > MaxAnalysisSize)
        {
            image.Mutate(x => x.Resize(new ResizeOptions
            {
                Size = new Size(MaxAnalysisSize, MaxAnalysisSize),
                Mode = ResizeMode.Max
            }));
        }

        int width = image.Width;
        int height = image.Height;
        long totalPixels = (long)width * height;

        if (totalPixels == 0)
            return new ExposureStats(128, 50, 0, 0);

        // Build histogram and compute stats in a single pass
        double sum = 0;
        double sumSq = 0;
        long clippedHighlights = 0;
        long crushedShadows = 0;

        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < width; x++)
                {
                    byte val = row[x].PackedValue;
                    sum += val;
                    sumSq += (double)val * val;

                    if (val >= HighlightClipValue)
                        clippedHighlights++;
                    if (val <= ShadowCrushValue)
                        crushedShadows++;
                }
            }
        });

        double mean = sum / totalPixels;
        double variance = (sumSq / totalPixels) - (mean * mean);
        double stdDev = Math.Sqrt(Math.Max(0, variance));
        double highlightPct = (double)clippedHighlights / totalPixels * 100.0;
        double shadowPct = (double)crushedShadows / totalPixels * 100.0;

        return new ExposureStats(mean, stdDev, highlightPct, shadowPct);
    }

    /// <summary>
    /// Maps exposure statistics to a 0–100 quality score.
    /// </summary>
    internal static double StatsToScore(ExposureStats stats)
    {
        // Mean brightness component (0–100)
        double meanScore;
        if (stats.Mean >= MeanIdealLow && stats.Mean <= MeanIdealHigh)
        {
            meanScore = 100;
        }
        else if (stats.Mean < MeanCriticalLow || stats.Mean > MeanCriticalHigh)
        {
            meanScore = 0;
        }
        else if (stats.Mean < MeanIdealLow)
        {
            // Linear interpolation from Critical (0) to Ideal (100)
            meanScore = (stats.Mean - MeanCriticalLow) / (MeanIdealLow - MeanCriticalLow) * 100;
        }
        else
        {
            // Linear interpolation from Ideal (100) to Critical (0)
            meanScore = (MeanCriticalHigh - stats.Mean) / (MeanCriticalHigh - MeanIdealHigh) * 100;
        }

        // Clipping penalty (0–100, 100 = no clipping)
        double clipPenalty = Math.Max(0, 100 - (stats.ClippedHighlightPercent + stats.CrushedShadowPercent) * 2);

        // Weighted blend: 60% mean brightness, 40% clipping
        return Math.Clamp(meanScore * 0.6 + clipPenalty * 0.4, 0, 100);
    }

    /// <summary>
    /// Exposure statistics for a single image.
    /// </summary>
    internal readonly record struct ExposureStats(
        double Mean,
        double StdDev,
        double ClippedHighlightPercent,
        double CrushedShadowPercent);
}
