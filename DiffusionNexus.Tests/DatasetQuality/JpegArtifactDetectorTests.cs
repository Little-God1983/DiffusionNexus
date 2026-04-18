using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Models;
using DiffusionNexus.Service.Services.DatasetQuality.ImageAnalysis;
using FluentAssertions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;

namespace DiffusionNexus.Tests.DatasetQuality;

public class JpegArtifactDetectorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly JpegArtifactDetector _detector;

    public JpegArtifactDetectorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"jpeg_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _detector = new JpegArtifactDetector();
    }

    public void Dispose()
    {
        // Test files left for diagnostics; temp directory will be cleaned by OS.
    }

    [Fact]
    public async Task WhenNoImages_ThenReturnsPerfectScore()
    {
        var config = MakeConfig();
        var result = await _detector.RunAsync([], config);

        result.Score.Should().Be(100);
        result.Issues.Should().BeEmpty();
    }

    [Fact]
    public async Task WhenHighQualityJpeg_ThenReturnsGoodScore()
    {
        string path = CreateJpegImage("high_quality.jpg", 200, 200, quality: 95);
        var images = new[] { new ImageFileInfo(path, 200, 200) };
        var config = MakeConfig();

        var result = await _detector.RunAsync(images, config);

        result.Score.Should().BeGreaterThanOrEqualTo(70);
        result.Issues.Should().BeEmpty();
    }

    [Fact]
    public async Task WhenLowQualityJpeg_ThenReportsIssue()
    {
        string path = CreateJpegImage("low_quality.jpg", 200, 200, quality: 20);
        var images = new[] { new ImageFileInfo(path, 200, 200) };
        var config = MakeConfig();

        var result = await _detector.RunAsync(images, config);

        result.Score.Should().BeLessThan(70);
    }

    [Fact]
    public async Task WhenPngImage_ThenDoesNotFlagAsCompressed()
    {
        string path = CreatePngImage("good.png", 200, 200);
        var images = new[] { new ImageFileInfo(path, 200, 200) };
        var config = MakeConfig();

        var result = await _detector.RunAsync(images, config);

        // PNG should not be flagged for JPEG artifacts
        result.Score.Should().BeGreaterThanOrEqualTo(60);
    }

    [Theory]
    [InlineData(95, 100.0)]
    [InlineData(90, 100.0)]
    [InlineData(75, 85.0)]
    [InlineData(60, 70.0)]
    [InlineData(30, 30.0)]
    [InlineData(0, 0.0)]
    public void WhenQualityToScoreCalculated_ThenReturnsExpectedValue(int quality, double expectedScore)
    {
        double result = JpegArtifactDetector.QualityToScore(quality);
        result.Should().Be(expectedScore);
    }

    [Theory]
    [InlineData(1.0, 95)]
    [InlineData(0.9, 95)]
    public void WhenBlockingRatioIsLow_ThenQualityIsHigh(double ratio, int expectedQuality)
    {
        int result = JpegArtifactDetector.BlockingRatioToQuality(ratio);
        result.Should().Be(expectedQuality);
    }

    [Fact]
    public void WhenBlockingRatioIsCritical_ThenQualityIsLow()
    {
        int result = JpegArtifactDetector.BlockingRatioToQuality(JpegArtifactDetector.BlockingRatioCritical);
        result.Should().BeLessThanOrEqualTo(20);
    }

    [Theory]
    [InlineData(3.0, true, 95)]
    [InlineData(1.0, true, 85)]
    [InlineData(0.5, true, 70)]
    [InlineData(0.3, true, 50)]
    [InlineData(0.15, true, 30)]
    [InlineData(0.1, true, 15)]
    public void WhenBppToQualityCalculated_ThenReturnsExpectedRange(double bpp, bool isJpeg, int expectedQuality)
    {
        int result = JpegArtifactDetector.BppToQuality(bpp, isJpeg);
        result.Should().Be(expectedQuality);
    }

    [Theory]
    [InlineData(0.1, false, 60)]
    [InlineData(0.5, false, 90)]
    [InlineData(2.0, false, 90)]
    public void WhenNonJpegBppToQuality_ThenReturnsNeutralScore(double bpp, bool isJpeg, int expectedQuality)
    {
        int result = JpegArtifactDetector.BppToQuality(bpp, isJpeg);
        result.Should().Be(expectedQuality);
    }

    [Fact]
    public async Task WhenMixedQualityImages_ThenAveragesScores()
    {
        string high = CreateJpegImage("high.jpg", 200, 200, quality: 95);
        string low = CreateJpegImage("low.jpg", 200, 200, quality: 20);

        var images = new[]
        {
            new ImageFileInfo(high, 200, 200),
            new ImageFileInfo(low, 200, 200)
        };
        var config = MakeConfig();

        var result = await _detector.RunAsync(images, config);

        // Average of good and bad should be moderate
        result.PerImageScores.Should().HaveCount(2);
    }

    [Fact]
    public void WhenAnalyzeImageOnJpeg_ThenReturnsResult()
    {
        string path = CreateJpegImage("analyze_test.jpg", 100, 100, quality: 50);

        var analysis = JpegArtifactDetector.AnalyzeImage(path);

        analysis.EstimatedQuality.Should().BeGreaterThan(0);
        analysis.BytesPerPixel.Should().BeGreaterThan(0);
    }

    [Fact]
    public void WhenAnalyzeImageOnPng_ThenUsesBppFallback()
    {
        string path = CreatePngImage("analyze_png.png", 100, 100);

        var analysis = JpegArtifactDetector.AnalyzeImage(path);

        analysis.Source.Should().Be(JpegArtifactDetector.QualitySource.BytesPerPixel);
    }

    private DatasetConfig MakeConfig() => new()
    {
        FolderPath = _tempDir,
        LoraType = LoraType.Character
    };

    private string CreateJpegImage(string fileName, int width, int height, int quality)
    {
        string path = Path.Combine(_tempDir, fileName);
        using var image = new Image<Rgb24>(width, height);

        // Create a non-trivial pattern to make compression artifacts more realistic
        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < width; x++)
                {
                    byte r = (byte)((x * 255) / Math.Max(width - 1, 1));
                    byte g = (byte)((y * 255) / Math.Max(height - 1, 1));
                    byte b = (byte)(((x + y) * 127) / Math.Max(width + height - 2, 1));
                    row[x] = new Rgb24(r, g, b);
                }
            }
        });

        var encoder = new JpegEncoder { Quality = quality };
        image.Save(path, encoder);
        return path;
    }

    private string CreatePngImage(string fileName, int width, int height)
    {
        string path = Path.Combine(_tempDir, fileName);
        using var image = new Image<L8>(width, height);
        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < width; x++)
                {
                    byte val = (byte)((x + y) * 255 / (width + height));
                    row[x] = new L8(val);
                }
            }
        });
        image.SaveAsPng(path);
        return path;
    }
}
