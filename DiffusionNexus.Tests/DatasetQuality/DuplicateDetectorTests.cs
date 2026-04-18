using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Models;
using DiffusionNexus.Service.Services.DatasetQuality.ImageAnalysis;
using FluentAssertions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace DiffusionNexus.Tests.DatasetQuality;

public class DuplicateDetectorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DuplicateDetector _detector;

    public DuplicateDetectorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"dup_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _detector = new DuplicateDetector();
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
        result.PerImageScores.Should().BeEmpty();
    }

    [Fact]
    public async Task WhenSingleImage_ThenReturnsPerfectScore()
    {
        string path = CreateSolidColorImage("single.png", 100, 100, 128);
        var images = new[] { new ImageFileInfo(path, 100, 100) };
        var config = MakeConfig();

        var result = await _detector.RunAsync(images, config);

        result.Score.Should().Be(100);
        result.Issues.Should().BeEmpty();
    }

    [Fact]
    public async Task WhenExactDuplicateExists_ThenReportsCriticalIssue()
    {
        string original = CreateSolidColorImage("original.png", 100, 100, 128);
        string copy = Path.Combine(_tempDir, "copy.png");
        File.Copy(original, copy);

        var images = new[]
        {
            new ImageFileInfo(original, 100, 100),
            new ImageFileInfo(copy, 100, 100)
        };
        var config = MakeConfig();

        var result = await _detector.RunAsync(images, config);

        result.Score.Should().BeLessThan(100);
        result.Issues.Should().ContainSingle(i => i.Severity == IssueSeverity.Critical);
        _detector.LastClusters.Should().ContainSingle(c => c.IsExactDuplicate);
    }

    [Fact]
    public async Task WhenAllUniqueImages_ThenReturnsNoIssues()
    {
        // Use strongly different patterns (not just solid colors which can hash similarly)
        string img1 = CreatePatternedImage("unique1.png", 100, 100, horizontal: true);
        string img2 = CreatePatternedImage("unique2.png", 100, 100, horizontal: false);

        var images = new[]
        {
            new ImageFileInfo(img1, 100, 100),
            new ImageFileInfo(img2, 100, 100)
        };
        var config = MakeConfig();

        var result = await _detector.RunAsync(images, config);

        result.Score.Should().Be(100);
        result.Issues.Should().BeEmpty();
    }

    [Fact]
    public async Task WhenExactDuplicateDetected_ThenClusterContainsBothPaths()
    {
        string original = CreateSolidColorImage("a.png", 80, 80, 100);
        string copy = Path.Combine(_tempDir, "b.png");
        File.Copy(original, copy);

        var images = new[]
        {
            new ImageFileInfo(original, 80, 80),
            new ImageFileInfo(copy, 80, 80)
        };
        var config = MakeConfig();

        await _detector.RunAsync(images, config);

        _detector.LastClusters.Should().HaveCount(1);
        var cluster = _detector.LastClusters[0];
        cluster.IsExactDuplicate.Should().BeTrue();
        cluster.ImagePaths.Should().HaveCount(2);
        cluster.SimilarityPercent.Should().Be(100.0);
    }

    [Fact]
    public void WhenFindNearDuplicateClusters_WithIdenticalHashes_ThenGroupsThem()
    {
        var hashByPath = new Dictionary<string, ulong>
        {
            ["img1.png"] = 0xAAAAAAAAAAAAAAAA,
            ["img2.png"] = 0xAAAAAAAAAAAAAAAA,
            ["img3.png"] = 0x0000000000000000
        };

        var clusters = DuplicateDetector.FindNearDuplicateClusters(hashByPath);

        clusters.Should().HaveCount(1);
        clusters[0].ImagePaths.Should().Contain("img1.png");
        clusters[0].ImagePaths.Should().Contain("img2.png");
        clusters[0].ImagePaths.Should().NotContain("img3.png");
    }

    [Fact]
    public void WhenFindNearDuplicateClusters_WithNoPairs_ThenReturnsEmpty()
    {
        var hashByPath = new Dictionary<string, ulong>
        {
            ["img1.png"] = 0x0000000000000000,
            ["img2.png"] = 0xFFFFFFFFFFFFFFFF
        };

        var clusters = DuplicateDetector.FindNearDuplicateClusters(hashByPath);

        clusters.Should().BeEmpty();
    }

    [Fact]
    public async Task WhenMultipleExactDuplicateGroups_ThenReportsAllGroups()
    {
        string a1 = CreateSolidColorImage("group_a1.png", 60, 60, 100);
        string a2 = Path.Combine(_tempDir, "group_a2.png");
        File.Copy(a1, a2);

        string b1 = CreateSolidColorImage("group_b1.png", 60, 60, 200);
        string b2 = Path.Combine(_tempDir, "group_b2.png");
        File.Copy(b1, b2);

        var images = new[]
        {
            new ImageFileInfo(a1, 60, 60),
            new ImageFileInfo(a2, 60, 60),
            new ImageFileInfo(b1, 60, 60),
            new ImageFileInfo(b2, 60, 60)
        };
        var config = MakeConfig();

        var result = await _detector.RunAsync(images, config);

        result.Issues.Should().ContainSingle(i => i.Severity == IssueSeverity.Critical);
        _detector.LastClusters.Count(c => c.IsExactDuplicate).Should().Be(2);
    }

    private DatasetConfig MakeConfig() => new()
    {
        FolderPath = _tempDir,
        LoraType = LoraType.Character
    };

    private string CreateSolidColorImage(string fileName, int width, int height, byte grayValue)
    {
        string path = Path.Combine(_tempDir, fileName);
        using var image = new Image<L8>(width, height);
        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < width; x++)
                    row[x] = new L8(grayValue);
            }
        });
        image.SaveAsPng(path);
        return path;
    }

    private string CreatePatternedImage(string fileName, int width, int height, bool horizontal)
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
                    // Create strongly different frequency patterns
                    byte val = horizontal
                        ? (byte)((x * 7 + y * 3) % 256)
                        : (byte)((y * 11 + x) % 256);
                    row[x] = new L8(val);
                }
            }
        });
        image.SaveAsPng(path);
        return path;
    }
}
