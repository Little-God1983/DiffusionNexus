using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Models;
using DiffusionNexus.Service.Services.DatasetQuality.ImageAnalysis;
using FluentAssertions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace DiffusionNexus.Tests.DatasetQuality;

public class ColorDistributionAnalyzerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ColorDistributionAnalyzer _analyzer;

    public ColorDistributionAnalyzerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"color_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _analyzer = new ColorDistributionAnalyzer();
    }

    public void Dispose()
    {
        // Test files left for diagnostics; temp directory will be cleaned by OS.
    }

    [Fact]
    public async Task WhenNoImages_ThenReturnsPerfectScore()
    {
        var config = MakeConfig();
        var result = await _analyzer.RunAsync([], config);

        result.Score.Should().Be(100);
        result.Issues.Should().BeEmpty();
    }

    [Fact]
    public async Task WhenAllColorImages_ThenReturnsHighScore()
    {
        var images = new[]
        {
            new ImageFileInfo(CreateColorImage("color1.png", 100, 100, 255, 0, 0), 100, 100),
            new ImageFileInfo(CreateColorImage("color2.png", 100, 100, 0, 255, 0), 100, 100),
            new ImageFileInfo(CreateColorImage("color3.png", 100, 100, 0, 0, 255), 100, 100),
        };
        var config = MakeConfig();

        var result = await _analyzer.RunAsync(images, config);

        result.Score.Should().BeGreaterThanOrEqualTo(50);
    }

    [Fact]
    public void WhenGrayscaleImage_ThenDetectedAsGrayscale()
    {
        string path = CreateGrayscaleImage("gray.png", 100, 100);
        var data = ColorDistributionAnalyzer.AnalyzeImage(path);

        data.IsGrayscale.Should().BeTrue();
    }

    [Fact]
    public void WhenColorImage_ThenNotGrayscale()
    {
        string path = CreateColorImage("color.png", 100, 100, 255, 0, 0);
        var data = ColorDistributionAnalyzer.AnalyzeImage(path);

        data.IsGrayscale.Should().BeFalse();
    }

    [Fact]
    public void WhenSingleColorImage_ThenDetectsColorCast()
    {
        // Solid red image — dominant hue should be detected
        string path = CreateColorImage("red_cast.png", 100, 100, 200, 30, 30);
        var data = ColorDistributionAnalyzer.AnalyzeImage(path);

        data.HasColorCast.Should().BeTrue();
    }

    [Fact]
    public void WhenVeryDarkImage_ThenDetectsVeryDark()
    {
        string path = CreateColorImage("dark.png", 100, 100, 10, 10, 10);
        var data = ColorDistributionAnalyzer.AnalyzeImage(path);

        data.IsVeryDark.Should().BeTrue();
    }

    [Fact]
    public void WhenVeryBrightImage_ThenDetectsVeryBright()
    {
        string path = CreateColorImage("bright.png", 100, 100, 250, 250, 250);
        var data = ColorDistributionAnalyzer.AnalyzeImage(path);

        data.IsVeryBright.Should().BeTrue();
    }

    [Fact]
    public async Task WhenMixedGrayscaleAndColor_ThenReportsMixedDataset()
    {
        // 4 color images + 1 grayscale = >80% color, minority grayscale
        var images = new List<ImageFileInfo>();
        for (int i = 0; i < 5; i++)
        {
            string path = CreateColorImage($"color_{i}.png", 100, 100, (byte)(50 * i), 100, 200);
            images.Add(new ImageFileInfo(path, 100, 100));
        }

        string grayPath = CreateGrayscaleImage("gray_outlier.png", 100, 100);
        images.Add(new ImageFileInfo(grayPath, 100, 100));

        var config = MakeConfig();
        var result = await _analyzer.RunAsync(images, config);

        // Should detect mixed dataset
        result.Issues.Should().Contain(i => i.Message.Contains("mixes black & white and color"));
    }

    [Fact]
    public async Task WhenAllGrayscale_ThenNoMixedWarning()
    {
        var images = new[]
        {
            new ImageFileInfo(CreateGrayscaleImage("g1.png", 100, 100), 100, 100),
            new ImageFileInfo(CreateGrayscaleImage("g2.png", 100, 100), 100, 100),
            new ImageFileInfo(CreateGrayscaleImage("g3.png", 100, 100), 100, 100),
        };
        var config = MakeConfig();

        var result = await _analyzer.RunAsync(images, config);

        result.Issues.Should().NotContain(i => i.Message.Contains("Mixed grayscale/color"));
    }

    [Fact]
    public async Task WhenOneOutlierInConsistentSet_ThenDetectsOutlier()
    {
        // 5 similar green images + 1 very different red image
        var images = new List<ImageFileInfo>();
        for (int i = 0; i < 5; i++)
        {
            string path = CreateColorImage($"green_{i}.png", 100, 100, 30, 200, 30);
            images.Add(new ImageFileInfo(path, 100, 100));
        }

        string outlierPath = CreateColorImage("red_outlier.png", 100, 100, 255, 0, 0);
        images.Add(new ImageFileInfo(outlierPath, 100, 100));

        var config = MakeConfig();
        var result = await _analyzer.RunAsync(images, config);

        result.Issues.Should().Contain(i =>
            i.Message.Contains("don't match the rest") && i.AffectedFiles.Contains(outlierPath));
    }

    [Theory]
    [InlineData(0, 0, 0, 0, 0, 0)]
    [InlineData(255, 0, 0, 0, 1, 1)]
    [InlineData(0, 255, 0, 120, 1, 1)]
    public void WhenRgbToHsvCalculated_ThenHueIsCorrect(byte r, byte g, byte b, double expectedH, double expectedS, double expectedV)
    {
        ColorDistributionAnalyzer.RgbToHsv(r, g, b, out double h, out double s, out double v);

        h.Should().BeApproximately(expectedH, 1.0);
        s.Should().BeApproximately(expectedS, 0.01);
        // V is max(r,g,b)/255
    }

    [Fact]
    public void WhenChiSquaredDistanceSameHistogram_ThenReturnsZero()
    {
        var hist = new double[] { 0.25, 0.25, 0.25, 0.25 };
        ColorDistributionAnalyzer.ChiSquaredDistance(hist, hist).Should().Be(0);
    }

    [Fact]
    public void WhenChiSquaredDistanceDifferentHistograms_ThenReturnsPositive()
    {
        var a = new double[] { 1.0, 0, 0, 0 };
        var b = new double[] { 0, 1.0, 0, 0 };
        ColorDistributionAnalyzer.ChiSquaredDistance(a, b).Should().BeGreaterThan(0);
    }

    private DatasetConfig MakeConfig() => new()
    {
        FolderPath = _tempDir,
        LoraType = LoraType.Character
    };

    private string CreateGrayscaleImage(string fileName, int width, int height)
    {
        string path = Path.Combine(_tempDir, fileName);
        using var image = new Image<Rgba32>(width, height);

        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < width; x++)
                {
                    byte val = (byte)((x * 255) / Math.Max(width - 1, 1));
                    row[x] = new Rgba32(val, val, val, 255);
                }
            }
        });

        image.SaveAsPng(path);
        return path;
    }

    private string CreateColorImage(string fileName, int width, int height, byte r, byte g, byte b)
    {
        string path = Path.Combine(_tempDir, fileName);
        using var image = new Image<Rgba32>(width, height);

        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < width; x++)
                {
                    row[x] = new Rgba32(r, g, b, 255);
                }
            }
        });

        image.SaveAsPng(path);
        return path;
    }
}
