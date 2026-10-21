using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Models;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Service.Services.Imaging;

namespace DiffusionNexus.Service.Services.DatasetQuality.ImageAnalysis;

/// <summary>
/// Detects blurry/soft images using Laplacian variance.
/// Higher variance indicates sharper edges; lower variance indicates blur.
/// Blurry training images teach the model to reproduce blur — especially
/// harmful for Character LoRAs where facial detail matters.
/// </summary>
public sealed class BlurDetector : IImageQualityCheck
{
    /// <summary>Check name used on all generated issues.</summary>
    public const string CheckDisplayName = "Blur Detection";

    /// <summary>Maximum dimension for analysis (longest side). Images are downscaled to this.</summary>
    internal const int MaxAnalysisSize = 512;

    /// <summary>Laplacian variance below this is flagged as Critical (very blurry).</summary>
    internal const double CriticalThreshold = 100.0;

    /// <summary>Laplacian variance below this is flagged as Warning (soft/slightly blurry).</summary>
    internal const double WarningThreshold = 300.0;

    /// <summary>Sigmoid center point for score mapping.</summary>
    internal const double SigmoidCenter = 200.0;

    /// <summary>Sigmoid steepness factor.</summary>
    internal const double SigmoidSteepness = 0.02;

    public string Name => CheckDisplayName;
    public string Description => "Detects blurry or soft images using Laplacian edge variance.";
    public int Order => 10;
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
        var criticalFiles = new List<string>();
        var warningFiles = new List<string>();

        for (int i = 0; i < images.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var img = images[i];
            double variance = await Task.Run(() => ComputeLaplacianVariance(img.FilePath), cancellationToken);
            double score = VarianceToScore(variance);

            perImageScores.Add(new PerImageScore(
                img.FilePath,
                Math.Round(score, 1),
                $"Laplacian variance: {variance:F1}"));

            if (variance < CriticalThreshold)
                criticalFiles.Add(img.FilePath);
            else if (variance < WarningThreshold)
                warningFiles.Add(img.FilePath);

            progress?.Report((double)(i + 1) / images.Count);
        }

        var issues = new List<Issue>();

        if (criticalFiles.Count > 0)
        {
            issues.Add(new Issue
            {
                Severity = IssueSeverity.Critical,
                Message = $"{criticalFiles.Count} image(s) are very blurry — likely to degrade training quality.",
                Details = "Images with extremely low sharpness (Laplacian variance < "
                        + $"{CriticalThreshold:F0}) produce blurry outputs when used for LoRA training. "
                        + "Consider replacing these with sharper source images.",
                Domain = CheckDomain.Image,
                CheckName = Name,
                AffectedFiles = criticalFiles
            });
        }

        if (warningFiles.Count > 0)
        {
            issues.Add(new Issue
            {
                Severity = IssueSeverity.Warning,
                Message = $"{warningFiles.Count} image(s) appear slightly soft or out of focus.",
                Details = "These images have below-average sharpness (Laplacian variance < "
                        + $"{WarningThreshold:F0}). They may still be usable but could reduce "
                        + "output detail. Review and consider replacing the softest ones.",
                Domain = CheckDomain.Image,
                CheckName = Name,
                AffectedFiles = warningFiles
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
    /// Computes the Laplacian variance of an image — a measure of edge sharpness.
    /// The image is converted to grayscale and resized before analysis.
    /// </summary>
    internal static double ComputeLaplacianVariance(string filePath)
    {
        // Grayscale, downscaled to the analysis size to normalize scale
        var (gray, width, height) = SkiaPixels.LoadGray(filePath, MaxAnalysisSize);

        if (width < 3 || height < 3)
            return 0;

        // Apply 3x3 Laplacian kernel: [0,1,0; 1,-4,1; 0,1,0]
        // and compute variance of the response
        double sum = 0;
        double sumSq = 0;
        int count = 0;

        for (int y = 1; y < height - 1; y++)
        {
            int rowAbove = (y - 1) * width;
            int rowCurrent = y * width;
            int rowBelow = (y + 1) * width;

            for (int x = 1; x < width - 1; x++)
            {
                // Laplacian: center * -4 + top + bottom + left + right
                double laplacian =
                    gray[rowAbove + x]
                    + gray[rowBelow + x]
                    + gray[rowCurrent + x - 1]
                    + gray[rowCurrent + x + 1]
                    - 4.0 * gray[rowCurrent + x];

                sum += laplacian;
                sumSq += laplacian * laplacian;
                count++;
            }
        }

        if (count == 0)
            return 0;

        double mean = sum / count;
        double variance = (sumSq / count) - (mean * mean);
        return Math.Max(0, variance);
    }

    /// <summary>
    /// Maps Laplacian variance to a 0–100 score using a sigmoid function.
    /// </summary>
    internal static double VarianceToScore(double variance)
    {
        return 100.0 / (1.0 + Math.Exp(-SigmoidSteepness * (variance - SigmoidCenter)));
    }
}
