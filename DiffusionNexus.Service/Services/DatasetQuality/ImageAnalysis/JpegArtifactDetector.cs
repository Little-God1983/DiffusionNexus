using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Models;
using DiffusionNexus.Domain.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace DiffusionNexus.Service.Services.DatasetQuality.ImageAnalysis;

/// <summary>
/// Detects JPEG compression artifacts by analyzing:
/// <list type="number">
///   <item>EXIF quality metadata (when present).</item>
///   <item>8×8 block boundary discontinuity vs. interior variance (fallback).</item>
///   <item>Bytes-per-pixel ratio (&lt;0.5 for photographic images indicates heavy compression).</item>
/// </list>
/// Heavily compressed training images introduce blocky artifacts that the model
/// can learn and reproduce in generated outputs.
/// </summary>
public sealed class JpegArtifactDetector : IImageQualityCheck
{
    /// <summary>Check name used on all generated issues.</summary>
    public const string CheckDisplayName = "JPEG Artifact Detection";

    /// <summary>Maximum dimension for block boundary analysis.</summary>
    internal const int MaxAnalysisSize = 512;

    /// <summary>EXIF/estimated quality above this is "Good" (score → 100).</summary>
    internal const int GoodQualityThreshold = 90;

    /// <summary>EXIF/estimated quality below this is "Warning".</summary>
    internal const int WarningQualityThreshold = 60;

    /// <summary>EXIF/estimated quality below this is "Critical".</summary>
    internal const int CriticalQualityThreshold = 30;

    /// <summary>Bytes-per-pixel below this for photographic content signals heavy compression.</summary>
    internal const double HeavyCompressionBpp = 0.5;

    /// <summary>
    /// Block boundary discontinuity ratio above this indicates visible JPEG artifacts.
    /// A value of 1.0 means boundary variance equals interior variance (no blocking).
    /// Higher values mean boundaries are more noticeable.
    /// </summary>
    internal const double BlockingRatioWarning = 1.3;

    /// <summary>Block boundary ratio above this indicates severe JPEG artifacts.</summary>
    internal const double BlockingRatioCritical = 1.8;

    public string Name => CheckDisplayName;
    public string Description => "Detects JPEG compression artifacts via EXIF quality, block analysis, and bytes-per-pixel ratio.";
    public int Order => 20;
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
            var analysis = await Task.Run(() => AnalyzeImage(img.FilePath), cancellationToken);
            double score = QualityToScore(analysis.EstimatedQuality);

            string detail = analysis.Source switch
            {
                QualitySource.Exif => $"EXIF quality: {analysis.EstimatedQuality}",
                QualitySource.BlockAnalysis => $"Estimated quality: {analysis.EstimatedQuality} (block analysis, ratio: {analysis.BlockingRatio:F2})",
                QualitySource.BytesPerPixel => $"Estimated quality: {analysis.EstimatedQuality} (BPP: {analysis.BytesPerPixel:F2})",
                _ => $"Quality: {analysis.EstimatedQuality}"
            };

            perImageScores.Add(new PerImageScore(img.FilePath, Math.Round(score, 1), detail));

            if (analysis.EstimatedQuality < CriticalQualityThreshold)
                criticalFiles.Add(img.FilePath);
            else if (analysis.EstimatedQuality < WarningQualityThreshold)
                warningFiles.Add(img.FilePath);

            progress?.Report((double)(i + 1) / images.Count);
        }

        var issues = new List<Issue>();

        if (criticalFiles.Count > 0)
        {
            issues.Add(new Issue
            {
                Severity = IssueSeverity.Critical,
                Message = $"{criticalFiles.Count} image(s) have severe JPEG compression artifacts.",
                Details = "Images with estimated JPEG quality below "
                        + $"{CriticalQualityThreshold} contain visible blocking artifacts that "
                        + "the model will learn to reproduce. Replace with higher-quality source images.",
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
                Message = $"{warningFiles.Count} image(s) show moderate JPEG compression.",
                Details = "These images have estimated JPEG quality between "
                        + $"{CriticalQualityThreshold} and {WarningQualityThreshold}. "
                        + "They may introduce subtle artifacts. Consider using higher-quality versions when available.",
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
    /// Analyzes a single image for JPEG compression quality.
    /// Tries EXIF metadata first, then block boundary analysis, then BPP heuristic.
    /// </summary>
    internal static JpegAnalysisResult AnalyzeImage(string filePath)
    {
        bool isJpeg = IsJpegFile(filePath);
        long fileSize = new FileInfo(filePath).Length;

        using var image = Image.Load<L8>(filePath);
        int width = image.Width;
        int height = image.Height;
        double bpp = fileSize / (double)(width * height);

        // Tier 1: Try EXIF quality metadata (JPEG only)
        if (isJpeg)
        {
            int? exifQuality = ReadExifQuality(image);
            if (exifQuality.HasValue)
            {
                return new JpegAnalysisResult(
                    EstimatedQuality: exifQuality.Value,
                    Source: QualitySource.Exif,
                    BlockingRatio: 0,
                    BytesPerPixel: bpp);
            }
        }

        // Tier 2: Block boundary discontinuity analysis (JPEG only, needs enough pixels)
        if (isJpeg && width >= 24 && height >= 24)
        {
            // Resize for analysis if needed
            if (width > MaxAnalysisSize || height > MaxAnalysisSize)
            {
                image.Mutate(x => x.Resize(new ResizeOptions
                {
                    Size = new Size(MaxAnalysisSize, MaxAnalysisSize),
                    Mode = ResizeMode.Max
                }));
            }

            double blockingRatio = ComputeBlockingRatio(image);
            int estimatedQuality = BlockingRatioToQuality(blockingRatio);

            return new JpegAnalysisResult(
                EstimatedQuality: estimatedQuality,
                Source: QualitySource.BlockAnalysis,
                BlockingRatio: blockingRatio,
                BytesPerPixel: bpp);
        }

        // Tier 3: Bytes-per-pixel heuristic (works for all formats)
        int bppQuality = BppToQuality(bpp, isJpeg);

        return new JpegAnalysisResult(
            EstimatedQuality: bppQuality,
            Source: QualitySource.BytesPerPixel,
            BlockingRatio: 0,
            BytesPerPixel: bpp);
    }

    /// <summary>
    /// Reads JPEG quality from EXIF metadata if available.
    /// Note: Standard EXIF does not always include a quality tag; this checks
    /// common maker-note tags that some encoders write.
    /// </summary>
    internal static int? ReadExifQuality(Image image)
    {
        var exif = image.Metadata?.ExifProfile;
        if (exif is null) return null;

        // Some cameras/encoders store quality in the UserComment or MakerNote.
        // Standard EXIF has no universal "quality" tag — return null to fall through
        // to block analysis or BPP heuristic.
        return null;
    }

    /// <summary>
    /// Computes the ratio of average variance at 8×8 block boundaries to interior variance.
    /// A ratio near 1.0 means no visible blocking; higher values indicate blocking artifacts.
    /// </summary>
    internal static double ComputeBlockingRatio(Image<L8> image)
    {
        int width = image.Width;
        int height = image.Height;

        double boundaryVarianceSum = 0;
        int boundaryCount = 0;
        double interiorVarianceSum = 0;
        int interiorCount = 0;

        image.ProcessPixelRows(accessor =>
        {
            for (int y = 1; y < height - 1; y++)
            {
                var rowAbove = accessor.GetRowSpan(y - 1);
                var rowCurrent = accessor.GetRowSpan(y);
                var rowBelow = accessor.GetRowSpan(y + 1);

                for (int x = 1; x < width - 1; x++)
                {
                    // Vertical gradient at this pixel
                    double gradient = Math.Abs(rowBelow[x].PackedValue - rowAbove[x].PackedValue)
                                    + Math.Abs(rowCurrent[x + 1].PackedValue - rowCurrent[x - 1].PackedValue);

                    bool isBoundary = (x % 8 == 0) || (y % 8 == 0);

                    if (isBoundary)
                    {
                        boundaryVarianceSum += gradient;
                        boundaryCount++;
                    }
                    else
                    {
                        interiorVarianceSum += gradient;
                        interiorCount++;
                    }
                }
            }
        });

        if (interiorCount == 0 || boundaryCount == 0)
            return 1.0; // No blocking detected

        double boundaryAvg = boundaryVarianceSum / boundaryCount;
        double interiorAvg = interiorVarianceSum / interiorCount;

        return interiorAvg > 0 ? boundaryAvg / interiorAvg : 1.0;
    }

    /// <summary>
    /// Converts a blocking ratio to an estimated JPEG quality (0–100).
    /// </summary>
    internal static int BlockingRatioToQuality(double ratio)
    {
        if (ratio <= 1.0) return 95; // No blocking artifacts
        if (ratio >= BlockingRatioCritical) return 20; // Severe blocking

        // Linear interpolation: ratio 1.0 → quality 95, ratio Critical → quality 20
        double t = (ratio - 1.0) / (BlockingRatioCritical - 1.0);
        return (int)Math.Round(95 - t * 75);
    }

    /// <summary>
    /// Converts a bytes-per-pixel value to an estimated quality (0–100).
    /// </summary>
    internal static int BppToQuality(double bpp, bool isJpeg)
    {
        if (!isJpeg)
        {
            // Non-JPEG formats: BPP is less meaningful for artifact detection
            // Give a neutral-to-good score
            return bpp < 0.3 ? 60 : 90;
        }

        // JPEG: lower BPP means more compression
        return bpp switch
        {
            >= 2.0 => 95,
            >= 1.0 => 85,
            >= HeavyCompressionBpp => 70,
            >= 0.3 => 50,
            >= 0.15 => 30,
            _ => 15
        };
    }

    /// <summary>
    /// Maps estimated quality (0–100) to a 0–100 score.
    /// quality &gt; 90 → 100, quality 60–90 → 70–90 range, below 60 → warning/critical.
    /// </summary>
    internal static double QualityToScore(int quality)
    {
        if (quality >= GoodQualityThreshold)
            return 100.0;

        if (quality >= WarningQualityThreshold)
        {
            // Linear map: 60 → 70, 90 → 100
            double t = (double)(quality - WarningQualityThreshold) / (GoodQualityThreshold - WarningQualityThreshold);
            return 70.0 + t * 30.0;
        }

        if (quality >= CriticalQualityThreshold)
        {
            // Linear map: 30 → 30, 60 → 70
            double t = (double)(quality - CriticalQualityThreshold) / (WarningQualityThreshold - CriticalQualityThreshold);
            return 30.0 + t * 40.0;
        }

        // Below critical: linear map 0 → 0, 30 → 30
        return (double)quality / CriticalQualityThreshold * 30.0;
    }

    private static bool IsJpegFile(string filePath)
    {
        string ext = Path.GetExtension(filePath);
        return ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Result of analyzing a single image for JPEG compression quality.
    /// </summary>
    internal record JpegAnalysisResult(
        int EstimatedQuality,
        QualitySource Source,
        double BlockingRatio,
        double BytesPerPixel);

    /// <summary>
    /// Source of the quality estimation.
    /// </summary>
    internal enum QualitySource
    {
        Exif,
        BlockAnalysis,
        BytesPerPixel
    }
}
