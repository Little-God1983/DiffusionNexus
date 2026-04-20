using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Models;
using DiffusionNexus.Domain.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace DiffusionNexus.Service.Services.DatasetQuality.ImageAnalysis;

/// <summary>
/// Analyzes color distribution across a dataset to detect grayscale images mixed
/// into color datasets, color-cast issues, and wildly inconsistent palettes.
/// Uses HSV color space with compact histograms for efficient CPU-only analysis.
/// </summary>
public sealed class ColorDistributionAnalyzer : IImageQualityCheck
{
    /// <summary>Check name used on all generated issues.</summary>
    public const string CheckDisplayName = "Color Distribution";

    /// <summary>Maximum dimension for analysis.</summary>
    internal const int MaxAnalysisSize = 512;

    /// <summary>Number of hue bins.</summary>
    internal const int HueBins = 12;

    /// <summary>Number of saturation bins.</summary>
    internal const int SaturationBins = 4;

    /// <summary>Number of value (brightness) bins.</summary>
    internal const int ValueBins = 4;

    /// <summary>Total histogram bins.</summary>
    internal const int TotalBins = HueBins * SaturationBins * ValueBins;

    /// <summary>Saturation mean below this classifies an image as grayscale.</summary>
    internal const double GrayscaleThreshold = 0.1;

    /// <summary>Dominant hue above this fraction of saturated pixels indicates color-cast.</summary>
    internal const double ColorCastThreshold = 0.6;

    /// <summary>Value mean below this flags the image as very dark.</summary>
    internal const double VeryDarkThreshold = 0.15;

    /// <summary>Value mean above this flags the image as very bright.</summary>
    internal const double VeryBrightThreshold = 0.9;

    /// <summary>Minimum saturation to consider a pixel "saturated" for hue analysis.</summary>
    internal const double MinSaturationForHue = 0.15;

    /// <summary>Outlier threshold in standard deviations from the dataset mean histogram.</summary>
    internal const double OutlierSigmaThreshold = 2.0;

    /// <summary>Fraction threshold for flagging mixed grayscale/color datasets.</summary>
    internal const double MixedTypeThreshold = 0.8;

    public string Name => CheckDisplayName;
    public string Description => "Detects grayscale/color mixing, color-cast, and palette inconsistency across the dataset.";
    public int Order => 20;
    public bool RequiresGpu => false;
    public QualityScoreCategory Category => QualityScoreCategory.DatasetConsistency;

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

        // Phase 1: per-image analysis
        var perImageData = new List<ImageColorData>(images.Count);

        for (int i = 0; i < images.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var img = images[i];
            var data = await Task.Run(() => AnalyzeImage(img.FilePath), cancellationToken);
            perImageData.Add(data);

            progress?.Report((double)(i + 1) / images.Count * 0.8);
        }

        // Phase 2: dataset-level analysis
        var issues = new List<Issue>();
        var perImageScores = new List<PerImageScore>(images.Count);

        var grayscaleFiles = new List<string>();
        var colorCastFiles = new List<string>();
        var veryDarkFiles = new List<string>();
        var veryBrightFiles = new List<string>();

        // Per-image flags
        for (int i = 0; i < perImageData.Count; i++)
        {
            var data = perImageData[i];

            if (data.IsGrayscale) grayscaleFiles.Add(data.FilePath);
            if (data.HasColorCast) colorCastFiles.Add(data.FilePath);
            if (data.IsVeryDark) veryDarkFiles.Add(data.FilePath);
            if (data.IsVeryBright) veryBrightFiles.Add(data.FilePath);
        }

        // Dataset-level: compute mean histogram and find outliers
        var outlierFiles = new List<string>();
        if (perImageData.Count >= 3)
        {
            var meanHistogram = ComputeMeanHistogram(perImageData);
            var distances = new double[perImageData.Count];

            for (int i = 0; i < perImageData.Count; i++)
            {
                distances[i] = ChiSquaredDistance(perImageData[i].Histogram, meanHistogram);
            }

            double distMean = distances.Average();
            double distStdDev = Math.Sqrt(distances.Average(d => (d - distMean) * (d - distMean)));

            if (distStdDev > 0)
            {
                for (int i = 0; i < distances.Length; i++)
                {
                    if ((distances[i] - distMean) / distStdDev > OutlierSigmaThreshold)
                    {
                        outlierFiles.Add(perImageData[i].FilePath);
                    }
                }
            }
        }

        // Mixed grayscale/color detection
        int grayscaleCount = perImageData.Count(d => d.IsGrayscale);
        int colorCount = perImageData.Count - grayscaleCount;
        bool isMixedDataset = perImageData.Count >= 2
            && grayscaleCount > 0
            && colorCount > 0
            && (double)Math.Max(grayscaleCount, colorCount) / perImageData.Count >= MixedTypeThreshold;

        // Build per-image scores
        for (int i = 0; i < perImageData.Count; i++)
        {
            var data = perImageData[i];
            double score = ComputePerImageScore(data, outlierFiles.Contains(data.FilePath));

            var detailParts = new List<string>();
            if (data.IsGrayscale) detailParts.Add("Black & white image in a color dataset — consider converting to color or removing");
            if (data.HasColorCast) detailParts.Add($"Strong color tint detected ({data.DominantHueFraction:P0} of pixels share the same hue) — try adjusting white balance");
            if (data.IsVeryDark) detailParts.Add("Image is very dark / underexposed — try increasing brightness or re-shooting");
            if (data.IsVeryBright) detailParts.Add("Image is very bright / overexposed — try reducing brightness or re-shooting");
            if (outlierFiles.Contains(data.FilePath)) detailParts.Add("Colors look very different from the rest of the dataset — review if this image belongs");

            string detail = detailParts.Count > 0
                ? string.Join("; ", detailParts)
                : "Colors look good — no issues detected";

            perImageScores.Add(new PerImageScore(data.FilePath, Math.Round(score, 1), detail));
        }

        progress?.Report(1.0);

        // Build issues
        if (colorCastFiles.Count > 0)
        {
            issues.Add(new Issue
            {
                Severity = IssueSeverity.Warning,
                Message = $"{colorCastFiles.Count} image(s) have a strong color tint.",
                Details = "These images are dominated by a single color (e.g. everything looks orange or blue). "
                        + "This usually happens with bad lighting or white balance. It can make the AI learn that tint as part of the subject.\n\n"
                        + "How to fix: Open the image in an editor and adjust the white balance or color temperature, "
                        + "or remove these images if the tint is too strong.",
                Domain = CheckDomain.Image,
                CheckName = Name,
                AffectedFiles = colorCastFiles
            });
        }

        if (isMixedDataset)
        {
            var minorityFiles = grayscaleCount < colorCount ? grayscaleFiles : perImageData
                .Where(d => !d.IsGrayscale)
                .Select(d => d.FilePath)
                .ToList();

            issues.Add(new Issue
            {
                Severity = IssueSeverity.Warning,
                Message = $"Dataset mixes black & white and color images ({grayscaleCount} B&W + {colorCount} color).",
                Details = "Most of your images are one type but a few are the other. "
                        + "Mixing B&W and color images confuses training — the AI won't know if the subject should be colorful or not.\n\n"
                        + "How to fix: Either remove the minority images, or convert them to match the majority "
                        + "(e.g. colorize the B&W ones, or desaturate the color ones).",
                Domain = CheckDomain.Image,
                CheckName = Name,
                AffectedFiles = minorityFiles
            });
        }

        if (veryDarkFiles.Count > 0)
        {
            issues.Add(new Issue
            {
                Severity = IssueSeverity.Info,
                Message = $"{veryDarkFiles.Count} image(s) are very dark or underexposed.",
                Details = "These images are almost entirely dark. The AI may struggle to learn details from them.\n\n"
                        + "How to fix: Increase the brightness/exposure in an image editor, "
                        + "or remove them if the subject isn't clearly visible.",
                Domain = CheckDomain.Image,
                CheckName = Name,
                AffectedFiles = veryDarkFiles
            });
        }

        if (veryBrightFiles.Count > 0)
        {
            issues.Add(new Issue
            {
                Severity = IssueSeverity.Info,
                Message = $"{veryBrightFiles.Count} image(s) are very bright or overexposed.",
                Details = "These images are nearly washed out. Details are lost in the highlights, "
                        + "making it hard for the AI to learn from them.\n\n"
                        + "How to fix: Reduce the brightness/exposure in an image editor, "
                        + "or remove them if the subject is barely visible.",
                Domain = CheckDomain.Image,
                CheckName = Name,
                AffectedFiles = veryBrightFiles
            });
        }

        if (outlierFiles.Count > 0)
        {
            issues.Add(new Issue
            {
                Severity = IssueSeverity.Warning,
                Message = $"{outlierFiles.Count} image(s) have colors that don't match the rest of the dataset.",
                Details = "These images look very different from the others in terms of colors and tones. "
                        + "They may be from a different scene, lighting, or camera setting.\n\n"
                        + "How to fix: Review these images — if they're unrelated or from a different session, "
                        + "consider removing them. If they belong, try color-correcting them to match the others.",
                Domain = CheckDomain.Image,
                CheckName = Name,
                AffectedFiles = outlierFiles
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
    /// Analyzes a single image's color distribution in HSV space.
    /// </summary>
    internal static ImageColorData AnalyzeImage(string filePath)
    {
        using var image = Image.Load<Rgba32>(filePath);

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

        var histogram = new double[TotalBins];
        double satSum = 0, valSum = 0;
        long totalPixels = 0;
        var hueCounts = new long[HueBins];
        long saturatedPixels = 0;

        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < width; x++)
                {
                    var pixel = row[x];
                    RgbToHsv(pixel.R, pixel.G, pixel.B, out double h, out double s, out double v);

                    int hBin = Math.Clamp((int)(h / 360.0 * HueBins), 0, HueBins - 1);
                    int sBin = Math.Clamp((int)(s * SaturationBins), 0, SaturationBins - 1);
                    int vBin = Math.Clamp((int)(v * ValueBins), 0, ValueBins - 1);

                    histogram[(hBin * SaturationBins * ValueBins) + (sBin * ValueBins) + vBin]++;

                    satSum += s;
                    valSum += v;
                    totalPixels++;

                    if (s >= MinSaturationForHue)
                    {
                        hueCounts[hBin]++;
                        saturatedPixels++;
                    }
                }
            }
        });

        if (totalPixels == 0)
        {
            return new ImageColorData(filePath, new double[TotalBins], 0, 0, false, false, false, false, 0);
        }

        // Normalize histogram
        for (int i = 0; i < TotalBins; i++)
        {
            histogram[i] /= totalPixels;
        }

        double satMean = satSum / totalPixels;
        double valMean = valSum / totalPixels;
        bool isGrayscale = satMean < GrayscaleThreshold;
        bool isVeryDark = valMean < VeryDarkThreshold;
        bool isVeryBright = valMean > VeryBrightThreshold;

        // Color-cast detection
        bool hasColorCast = false;
        double dominantHueFraction = 0;
        if (saturatedPixels > 0 && !isGrayscale)
        {
            long maxHueCount = hueCounts.Max();
            dominantHueFraction = (double)maxHueCount / saturatedPixels;
            hasColorCast = dominantHueFraction > ColorCastThreshold;
        }

        return new ImageColorData(filePath, histogram, satMean, valMean, isGrayscale, hasColorCast, isVeryDark, isVeryBright, dominantHueFraction);
    }

    /// <summary>
    /// Computes the per-image score based on detected issues.
    /// </summary>
    internal static double ComputePerImageScore(ImageColorData data, bool isOutlier)
    {
        double score = 100;

        if (data.HasColorCast) score -= 25;
        if (data.IsVeryDark) score -= 25;
        if (data.IsVeryBright) score -= 25;
        if (isOutlier) score -= 20;

        return Math.Max(0, score);
    }

    internal static double[] ComputeMeanHistogram(IReadOnlyList<ImageColorData> data)
    {
        var mean = new double[TotalBins];
        foreach (var d in data)
        {
            for (int i = 0; i < TotalBins; i++)
            {
                mean[i] += d.Histogram[i];
            }
        }

        for (int i = 0; i < TotalBins; i++)
        {
            mean[i] /= data.Count;
        }

        return mean;
    }

    /// <summary>
    /// Computes the chi-squared distance between two normalized histograms.
    /// </summary>
    internal static double ChiSquaredDistance(double[] a, double[] b)
    {
        double sum = 0;
        for (int i = 0; i < a.Length; i++)
        {
            double denom = a[i] + b[i];
            if (denom > 1e-10)
            {
                double diff = a[i] - b[i];
                sum += (diff * diff) / denom;
            }
        }
        return sum;
    }

    /// <summary>
    /// Converts RGB (0–255) to HSV (H: 0–360, S: 0–1, V: 0–1).
    /// </summary>
    internal static void RgbToHsv(byte r, byte g, byte b, out double h, out double s, out double v)
    {
        double rf = r / 255.0;
        double gf = g / 255.0;
        double bf = b / 255.0;

        double max = Math.Max(rf, Math.Max(gf, bf));
        double min = Math.Min(rf, Math.Min(gf, bf));
        double delta = max - min;

        v = max;
        s = max > 0 ? delta / max : 0;

        if (delta == 0)
        {
            h = 0;
        }
        else if (max == rf)
        {
            h = 60 * (((gf - bf) / delta) % 6);
        }
        else if (max == gf)
        {
            h = 60 * (((bf - rf) / delta) + 2);
        }
        else
        {
            h = 60 * (((rf - gf) / delta) + 4);
        }

        if (h < 0) h += 360;
    }

    /// <summary>
    /// Per-image color analysis data.
    /// </summary>
    internal record ImageColorData(
        string FilePath,
        double[] Histogram,
        double SaturationMean,
        double ValueMean,
        bool IsGrayscale,
        bool HasColorCast,
        bool IsVeryDark,
        bool IsVeryBright,
        double DominantHueFraction);
}
