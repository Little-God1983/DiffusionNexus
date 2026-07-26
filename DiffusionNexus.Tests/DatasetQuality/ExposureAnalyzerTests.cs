using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Models;
using DiffusionNexus.Service.Services.DatasetQuality.ImageAnalysis;
using FluentAssertions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace DiffusionNexus.Tests.DatasetQuality;

/// <summary>
/// Covers <see cref="ExposureAnalyzer"/> — the luminance-histogram exposure check. The second of the
/// two <c>IImageQualityCheck</c> implementations that had no tests (issue #443). Uses real lossless
/// PNGs with fully controlled pixel values so the mean/stdDev/clip statistics are exact.
/// </summary>
public class ExposureAnalyzerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ExposureAnalyzer _analyzer;

    public ExposureAnalyzerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"exposure_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _analyzer = new ExposureAnalyzer();
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task WhenNoImages_ThenReturnsPerfectScore()
    {
        var result = await _analyzer.RunAsync([], MakeConfig());

        result.Score.Should().Be(100);
        result.Issues.Should().BeEmpty();
    }

    [Fact]
    public async Task WhenWellExposedGradient_ThenNoIssues()
    {
        // Full 0..255 horizontal ramp: mean ≈ 127.5 (ideal band), high stdDev (no low-DR flag),
        // and only ~2.3% pixels clip at each end (< 15% warning thresholds).
        string path = CreateHorizontalRamp("ramp.png", 256, 64);
        var images = new[] { new ImageFileInfo(path, 256, 64) };

        var result = await _analyzer.RunAsync(images, MakeConfig());

        result.Issues.Should().BeEmpty();
        result.Score.Should().BeGreaterThanOrEqualTo(80);
    }

    [Fact]
    public async Task WhenSeverelyUnderexposed_ThenCriticalUnderexposedIssue()
    {
        string path = CreateUniform("dark.png", 128, 128, 20); // mean 20 < MeanCriticalLow (40)
        var images = new[] { new ImageFileInfo(path, 128, 128) };

        var result = await _analyzer.RunAsync(images, MakeConfig());

        result.Issues.Should().Contain(i =>
            i.Severity == IssueSeverity.Critical && i.Message.Contains("underexposed"));
    }

    [Fact]
    public async Task WhenSeverelyOverexposed_ThenCriticalOverexposedIssue()
    {
        string path = CreateUniform("bright.png", 128, 128, 240); // mean 240 > MeanCriticalHigh (220)
        var images = new[] { new ImageFileInfo(path, 128, 128) };

        var result = await _analyzer.RunAsync(images, MakeConfig());

        result.Issues.Should().Contain(i =>
            i.Severity == IssueSeverity.Critical && i.Message.Contains("overexposed"));
    }

    [Fact]
    public async Task WhenManyClippedHighlights_ThenHighlightWarning()
    {
        // 30% pure white (>= HighlightClipValue 250), 70% mid-grey → mean ≈ 146 (in range),
        // highlight clip 30% (> 15% warning) but not critically over/under exposed.
        string path = CreateColumnSplit("clip.png", 100, 100, 0.30, 255, 100);
        var images = new[] { new ImageFileInfo(path, 100, 100) };

        var result = await _analyzer.RunAsync(images, MakeConfig());

        result.Issues.Should().Contain(i =>
            i.Severity == IssueSeverity.Warning && i.Message.Contains("clipped highlights"));
    }

    [Fact]
    public async Task WhenManyCrushedShadows_ThenShadowWarning()
    {
        // 30% pure black (<= ShadowCrushValue 5), 70% mid-grey → crushed shadows 30% (> 15%).
        string path = CreateColumnSplit("crush.png", 100, 100, 0.30, 0, 150);
        var images = new[] { new ImageFileInfo(path, 100, 100) };

        var result = await _analyzer.RunAsync(images, MakeConfig());

        result.Issues.Should().Contain(i =>
            i.Severity == IssueSeverity.Warning && i.Message.Contains("crushed shadows"));
    }

    [Fact]
    public async Task WhenFlatMidToneImage_ThenLowDynamicRangeInfo()
    {
        // Uniform mid-grey: mean 128 (ideal band, not critical), stdDev 0 (< LowDynamicRangeThreshold 30).
        string path = CreateUniform("flat.png", 128, 128, 128);
        var images = new[] { new ImageFileInfo(path, 128, 128) };

        var result = await _analyzer.RunAsync(images, MakeConfig());

        result.Issues.Should().Contain(i =>
            i.Severity == IssueSeverity.Info && i.Message.Contains("low dynamic range"));
    }

    [Fact]
    public async Task WhenCancelled_ThenThrowsOperationCanceled()
    {
        string path = CreateUniform("cancel.png", 32, 32, 128);
        var images = new[] { new ImageFileInfo(path, 32, 32) };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await _analyzer.RunAsync(images, MakeConfig(), cancellationToken: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ── ComputeExposureStats (internal) ──

    [Fact]
    public void ComputeExposureStats_UniformImage_HasExactMeanAndZeroSpread()
    {
        string path = CreateUniform("stats.png", 64, 64, 128);

        var stats = ExposureAnalyzer.ComputeExposureStats(path);

        stats.Mean.Should().BeApproximately(128, 0.001);
        stats.StdDev.Should().BeApproximately(0, 0.001);
        stats.ClippedHighlightPercent.Should().Be(0);
        stats.CrushedShadowPercent.Should().Be(0);
    }

    [Fact]
    public void ComputeExposureStats_AllWhite_Is100PercentClipped()
    {
        string path = CreateUniform("white.png", 32, 32, 255);

        var stats = ExposureAnalyzer.ComputeExposureStats(path);

        stats.ClippedHighlightPercent.Should().BeApproximately(100, 0.001);
        stats.CrushedShadowPercent.Should().Be(0);
    }

    // ── StatsToScore (internal) — brightness × clipping blend ──

    [Theory]
    [InlineData(128.0, 100.0)]  // ideal band, no clipping → full score
    [InlineData(30.0, 40.0)]    // below MeanCriticalLow → meanScore 0, clipPenalty 100 → 0.6*0 + 0.4*100
    [InlineData(230.0, 40.0)]   // above MeanCriticalHigh → same 40
    [InlineData(60.0, 70.0)]    // halfway Critical→Ideal (meanScore 50) → 0.6*50 + 0.4*100
    [InlineData(200.0, 70.0)]   // halfway Ideal→Critical (meanScore 50) → 70
    public void StatsToScore_BrightnessComponent(double mean, double expected)
    {
        var stats = new ExposureAnalyzer.ExposureStats(mean, StdDev: 50, ClippedHighlightPercent: 0, CrushedShadowPercent: 0);
        ExposureAnalyzer.StatsToScore(stats).Should().BeApproximately(expected, 0.1);
    }

    [Fact]
    public void StatsToScore_ClippingReducesScore()
    {
        var clean = new ExposureAnalyzer.ExposureStats(128, 50, 0, 0);
        var clipped = new ExposureAnalyzer.ExposureStats(128, 50, ClippedHighlightPercent: 20, CrushedShadowPercent: 10);

        ExposureAnalyzer.StatsToScore(clipped).Should().BeLessThan(ExposureAnalyzer.StatsToScore(clean));
    }

    private DatasetConfig MakeConfig() => new()
    {
        FolderPath = _tempDir,
        LoraType = LoraType.Character
    };

    private string CreateUniform(string fileName, int width, int height, byte value)
    {
        string path = Path.Combine(_tempDir, fileName);
        using var image = new Image<L8>(width, height, new L8(value));
        image.SaveAsPng(path);
        return path;
    }

    private string CreateHorizontalRamp(string fileName, int width, int height)
    {
        string path = Path.Combine(_tempDir, fileName);
        using var image = new Image<L8>(width, height);
        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < width; x++)
                    row[x] = new L8((byte)(x * 255 / Math.Max(width - 1, 1)));
            }
        });
        image.SaveAsPng(path);
        return path;
    }

    /// <summary>
    /// Splits the image into a left band of <paramref name="leftFraction"/> columns at
    /// <paramref name="leftValue"/> and the remainder at <paramref name="rightValue"/>.
    /// </summary>
    private string CreateColumnSplit(string fileName, int width, int height, double leftFraction, byte leftValue, byte rightValue)
    {
        string path = Path.Combine(_tempDir, fileName);
        int splitCol = (int)Math.Round(width * leftFraction);
        using var image = new Image<L8>(width, height);
        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < width; x++)
                    row[x] = new L8(x < splitCol ? leftValue : rightValue);
            }
        });
        image.SaveAsPng(path);
        return path;
    }
}
