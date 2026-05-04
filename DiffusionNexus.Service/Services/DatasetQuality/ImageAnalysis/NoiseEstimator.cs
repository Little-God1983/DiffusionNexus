using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Models;
using DiffusionNexus.Domain.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace DiffusionNexus.Service.Services.DatasetQuality.ImageAnalysis;

/// <summary>
/// Estimates image noise level using a Laplacian high-pass filter combined
/// with the Robust Median Estimator (MAD / 0.6745).
/// Noisy training images teach the model to reproduce noise patterns,
/// reducing output fidelity — particularly visible in smooth areas
/// (skin, sky, gradients).
/// </summary>
public sealed class NoiseEstimator : IImageQualityCheck
{
    /// <summary>Check name used on all generated issues.</summary>
    public const string CheckDisplayName = "Noise Estimation";

    /// <summary>Maximum dimension for analysis (longest side).</summary>
    internal const int MaxAnalysisSize = 512;

    /// <summary>Noise sigma below this is considered clean (score 100).</summary>
    internal const double CleanThreshold = 5.0;

    /// <summary>Lower bound of the acceptable range (score 70–90).</summary>
    internal const double AcceptableLow = 5.0;

    /// <summary>Upper bound of the acceptable range.</summary>
    internal const double AcceptableHigh = 15.0;

    /// <summary>Sigma above this triggers a Warning.</summary>
    internal const double WarningThreshold = 25.0;

    /// <summary>Sigma above this triggers a Critical issue.</summary>
    internal const double CriticalThreshold = 40.0;

    /// <summary>MAD-to-sigma conversion constant for Gaussian noise.</summary>
    internal const double MadToSigmaFactor = 0.6745;

    public string Name => CheckDisplayName;
    public string Description => "Estimates image noise level using Laplacian MAD (Robust Median Estimator).";
    public int Order => 12;
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
            double sigma = await Task.Run(() => EstimateNoiseSigma(img.FilePath), cancellationToken);
            double score = SigmaToScore(sigma);

            perImageScores.Add(new PerImageScore(
                img.FilePath,
                Math.Round(score, 1),
                $"Noise sigma: {sigma:F1}"));

            if (sigma > CriticalThreshold)
                criticalFiles.Add(img.FilePath);
            else if (sigma > WarningThreshold)
                warningFiles.Add(img.FilePath);

            progress?.Report((double)(i + 1) / images.Count);
        }

        var issues = new List<Issue>();

        if (criticalFiles.Count > 0)
        {
            issues.Add(new Issue
            {
                Severity = IssueSeverity.Critical,
                Message = $"{criticalFiles.Count} image(s) have very high noise levels — likely to degrade training quality.",
                Details = $"Images with noise sigma above {CriticalThreshold:F0} contain excessive noise that "
                        + "the model will learn to reproduce. Consider denoising or replacing these images.",
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
                Message = $"{warningFiles.Count} image(s) have noticeable noise levels.",
                Details = $"Images with noise sigma above {WarningThreshold:F0} may reduce output fidelity. "
                        + "Review and consider light denoising on the noisiest ones.",
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
    /// Estimates the noise standard deviation of an image using the Robust Median Estimator:
    /// sigma = MAD(Laplacian(image)) / 0.6745.
    /// </summary>
    internal static double EstimateNoiseSigma(string filePath)
    {
        using var image = Image.Load<L8>(filePath);

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

        if (width < 3 || height < 3)
            return 0;

        // Apply Laplacian and collect absolute values for MAD computation
        var laplacianValues = new List<double>((width - 2) * (height - 2));

        image.ProcessPixelRows(accessor =>
        {
            for (int y = 1; y < height - 1; y++)
            {
                var rowAbove = accessor.GetRowSpan(y - 1);
                var rowCurrent = accessor.GetRowSpan(y);
                var rowBelow = accessor.GetRowSpan(y + 1);

                for (int x = 1; x < width - 1; x++)
                {
                    double laplacian =
                        rowAbove[x].PackedValue
                        + rowBelow[x].PackedValue
                        + rowCurrent[x - 1].PackedValue
                        + rowCurrent[x + 1].PackedValue
                        - 4.0 * rowCurrent[x].PackedValue;

                    laplacianValues.Add(Math.Abs(laplacian));
                }
            }
        });

        if (laplacianValues.Count == 0)
            return 0;

        // Compute MAD (Median Absolute Deviation)
        laplacianValues.Sort();
        double median = MedianOfSorted(laplacianValues);

        // MAD = median(|x_i - median(x)|)
        for (int i = 0; i < laplacianValues.Count; i++)
        {
            laplacianValues[i] = Math.Abs(laplacianValues[i] - median);
        }

        laplacianValues.Sort();
        double mad = MedianOfSorted(laplacianValues);

        return mad / MadToSigmaFactor;
    }

    /// <summary>
    /// Maps noise sigma to a 0–100 score.
    /// </summary>
    internal static double SigmaToScore(double sigma)
    {
        if (sigma <= CleanThreshold)
            return 100;

        if (sigma <= AcceptableHigh)
        {
            // Linear interpolation from 90 at sigma=5 to 70 at sigma=15
            double t = (sigma - AcceptableLow) / (AcceptableHigh - AcceptableLow);
            return 90 - (t * 20);
        }

        if (sigma <= WarningThreshold)
        {
            // 70 at sigma=15 down to 40 at sigma=25
            double t = (sigma - AcceptableHigh) / (WarningThreshold - AcceptableHigh);
            return 70 - (t * 30);
        }

        if (sigma <= CriticalThreshold)
        {
            // 40 at sigma=25 down to 10 at sigma=40
            double t = (sigma - WarningThreshold) / (CriticalThreshold - WarningThreshold);
            return 40 - (t * 30);
        }

        // Beyond critical: clamp to 0–10
        return Math.Max(0, 10 - ((sigma - CriticalThreshold) * 0.5));
    }

    private static double MedianOfSorted(List<double> sorted)
    {
        int n = sorted.Count;
        if (n == 0) return 0;
        if (n % 2 == 1) return sorted[n / 2];
        return (sorted[(n / 2) - 1] + sorted[n / 2]) / 2.0;
    }
}
