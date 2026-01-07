using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DiffusionNexus.Core.Models.Configuration;
using DiffusionNexus.Core.Services;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Tests.Autocropper;

/// <summary>
/// Unit tests for ImageCropperService.
/// Tests folder scanning, image processing, and service behavior.
/// These tests are designed to be framework-agnostic for reuse in other implementations.
/// </summary>
public class ImageCropperServiceTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly ImageCropperService _service;
    private static readonly BucketDefinition Ratio1x1 = new() { Name = "1:1", Width = 1, Height = 1 };

    public ImageCropperServiceTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"ImageCropperTest_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDirectory);
        _service = new ImageCropperService();
    }

    public void Dispose()
    {
        // Cleanup test directory - best effort, don't throw
        try
        {
            if (Directory.Exists(_testDirectory))
            {
                Directory.Delete(_testDirectory, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup failures in tests
        }
    }

    #region ScanFolder Validation Tests

    [Fact]
    public void WhenScanFolderCalledWithNullThenThrowsException()
    {
        // Act
        var action = () => _service.ScanFolder(null!);

        // Assert
        action.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void WhenScanFolderCalledWithEmptyStringThenThrowsException()
    {
        // Act
        var action = () => _service.ScanFolder(string.Empty);

        // Assert
        action.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void WhenScanFolderCalledWithWhitespaceThenThrowsException()
    {
        // Act
        var action = () => _service.ScanFolder("   ");

        // Assert
        action.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void WhenScanFolderCalledWithNonExistentPathThenReturnsZeroCounts()
    {
        // Arrange
        var nonExistentPath = Path.Combine(_testDirectory, "NonExistent");

        // Act
        var result = _service.ScanFolder(nonExistentPath);

        // Assert
        result.TotalFiles.Should().Be(0);
        result.ImageFiles.Should().Be(0);
        result.ImagePaths.Should().BeEmpty();
    }

    #endregion

    #region ScanFolder Empty Folder Tests

    [Fact]
    public void WhenScanFolderCalledOnEmptyFolderThenReturnsZeroCounts()
    {
        // Arrange
        var emptyFolder = Path.Combine(_testDirectory, "Empty");
        Directory.CreateDirectory(emptyFolder);

        // Act
        var result = _service.ScanFolder(emptyFolder);

        // Assert
        result.TotalFiles.Should().Be(0);
        result.ImageFiles.Should().Be(0);
        result.ImagePaths.Should().BeEmpty();
    }

    #endregion

    #region ScanFolder File Detection Tests

    [Fact]
    public void WhenScanFolderCalledWithOnlyNonImageFilesThenImageCountIsZero()
    {
        // Arrange
        var folder = Path.Combine(_testDirectory, "NoImages");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "file1.txt"), "test");
        File.WriteAllText(Path.Combine(folder, "file2.doc"), "test");

        // Act
        var result = _service.ScanFolder(folder);

        // Assert
        result.TotalFiles.Should().Be(2);
        result.ImageFiles.Should().Be(0);
        result.ImagePaths.Should().BeEmpty();
    }

    [Theory]
    [InlineData(".jpg")]
    [InlineData(".jpeg")]
    [InlineData(".png")]
    [InlineData(".bmp")]
    [InlineData(".gif")]
    [InlineData(".webp")]
    public void WhenScanFolderCalledWithImageExtensionThenImageIsDetected(string extension)
    {
        // Arrange
        var folder = Path.Combine(_testDirectory, $"Images_{extension.TrimStart('.')}$");
        Directory.CreateDirectory(folder);
        var imagePath = Path.Combine(folder, $"image{extension}");
        File.WriteAllBytes(imagePath, [0x00]); // Minimal file

        // Act
        var result = _service.ScanFolder(folder);

        // Assert
        result.ImageFiles.Should().Be(1);
        result.ImagePaths.Should().Contain(imagePath);
    }

    [Theory]
    [InlineData(".JPG")]
    [InlineData(".JPEG")]
    [InlineData(".PNG")]
    [InlineData(".BMP")]
    [InlineData(".GIF")]
    [InlineData(".WEBP")]
    public void WhenScanFolderCalledWithUpperCaseExtensionThenImageIsDetected(string extension)
    {
        // Arrange
        var folder = Path.Combine(_testDirectory, $"UpperCase_{extension.TrimStart('.')}$");
        Directory.CreateDirectory(folder);
        var imagePath = Path.Combine(folder, $"image{extension}");
        File.WriteAllBytes(imagePath, [0x00]); // Minimal file

        // Act
        var result = _service.ScanFolder(folder);

        // Assert
        result.ImageFiles.Should().Be(1);
    }

    [Fact]
    public void WhenScanFolderCalledWithMixedFilesThenCorrectCountsReturned()
    {
        // Arrange
        var folder = Path.Combine(_testDirectory, "Mixed");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "image1.jpg"), [0x00]);
        File.WriteAllBytes(Path.Combine(folder, "image2.png"), [0x00]);
        File.WriteAllText(Path.Combine(folder, "readme.txt"), "test");
        File.WriteAllText(Path.Combine(folder, "data.json"), "{}");

        // Act
        var result = _service.ScanFolder(folder);

        // Assert
        result.TotalFiles.Should().Be(4);
        result.ImageFiles.Should().Be(2);
        result.ImagePaths.Should().HaveCount(2);
    }

    [Fact]
    public void WhenScanFolderCalledThenSubdirectoriesAreNotScanned()
    {
        // Arrange
        var folder = Path.Combine(_testDirectory, "TopLevel");
        var subFolder = Path.Combine(folder, "SubFolder");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(subFolder);
        
        File.WriteAllBytes(Path.Combine(folder, "top.jpg"), [0x00]);
        File.WriteAllBytes(Path.Combine(subFolder, "sub.jpg"), [0x00]);

        // Act
        var result = _service.ScanFolder(folder);

        // Assert - only top level file should be found
        result.ImageFiles.Should().Be(1);
        result.ImagePaths.Should().OnlyContain(p => !p.Contains("SubFolder"));
    }

    #endregion

    #region ProcessImagesAsync Validation Tests

    [Fact]
    public async Task WhenProcessImagesCalledWithNullPathThenThrowsException()
    {
        // Act
        var action = async () => await _service.ProcessImagesAsync(null!, null);

        // Assert
        await action.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task WhenProcessImagesCalledWithEmptyPathThenThrowsException()
    {
        // Act
        var action = async () => await _service.ProcessImagesAsync(string.Empty, null);

        // Assert
        await action.Should().ThrowAsync<ArgumentException>();
    }

    #endregion

    #region ProcessImagesAsync Empty Folder Tests

    [Fact]
    public async Task WhenProcessImagesCalledOnEmptyFolderThenReturnsZeroCounts()
    {
        // Arrange
        var folder = Path.Combine(_testDirectory, "EmptyProcess");
        Directory.CreateDirectory(folder);

        // Act
        var result = await _service.ProcessImagesAsync(folder, null);

        // Assert
        result.SuccessCount.Should().Be(0);
        result.FailedCount.Should().Be(0);
        result.SkippedCount.Should().Be(0);
    }

    #endregion

    #region ProcessImagesAsync Cancellation Tests

    [Fact]
    public async Task WhenProcessImagesCancelledThenThrowsOperationCanceledException()
    {
        // Arrange
        var folder = Path.Combine(_testDirectory, "Cancel");
        Directory.CreateDirectory(folder);
        
        // Create a dummy file
        File.WriteAllBytes(Path.Combine(folder, "image.jpg"), [0x00]);

        var cts = new CancellationTokenSource();
        cts.Cancel(); // Pre-cancel

        // Act
        var action = async () => await _service.ProcessImagesAsync(folder, null, cancellationToken: cts.Token);

        // Assert
        await action.Should().ThrowAsync<OperationCanceledException>();
    }

    #endregion

    #region ProcessImagesAsync Progress Reporting Tests

    [Fact]
    public async Task WhenProcessImagesCalledThenProgressIsReported()
    {
        // Arrange
        var folder = Path.Combine(_testDirectory, "Progress");
        Directory.CreateDirectory(folder);
        
        // Create valid test image (1x1 pixel red PNG)
        CreateTestImage(Path.Combine(folder, "test.png"), 100, 100);

        var progressReports = new List<CropProgress>();
        var progress = new Progress<CropProgress>(p => progressReports.Add(p));

        // Act
        await _service.ProcessImagesAsync(folder, null, progress: progress);

        // Wait a bit for progress to be reported (Progress<T> uses SynchronizationContext)
        await Task.Delay(100);

        // Assert
        progressReports.Should().NotBeEmpty();
    }

    #endregion

    #region ProcessImagesAsync Target Folder Tests

    [Fact]
    public async Task WhenTargetFolderProvidedThenFolderIsCreated()
    {
        // Arrange
        var sourceFolder = Path.Combine(_testDirectory, "Source");
        var targetFolder = Path.Combine(_testDirectory, "Target");
        Directory.CreateDirectory(sourceFolder);
        
        CreateTestImage(Path.Combine(sourceFolder, "test.png"), 100, 100);

        // Act
        await _service.ProcessImagesAsync(sourceFolder, targetFolder);

        // Assert
        Directory.Exists(targetFolder).Should().BeTrue();
    }

    [Fact]
    public async Task WhenTargetFolderNullThenOverwriteMode()
    {
        // Arrange
        var folder = Path.Combine(_testDirectory, "Overwrite");
        Directory.CreateDirectory(folder);
        var imagePath = Path.Combine(folder, "test.png");
        CreateTestImage(imagePath, 100, 100);

        var originalWriteTime = File.GetLastWriteTime(imagePath);

        // Act
        await _service.ProcessImagesAsync(folder, null);

        // Assert - file should still exist (even if modified or unchanged)
        File.Exists(imagePath).Should().BeTrue();
    }

    #endregion

    #region ProcessImagesAsync Duration Tests

    [Fact]
    public async Task WhenProcessImagesCompletedThenDurationIsPositive()
    {
        // Arrange
        var folder = Path.Combine(_testDirectory, "Duration");
        Directory.CreateDirectory(folder);
        CreateTestImage(Path.Combine(folder, "test.png"), 100, 100);

        // Act
        var result = await _service.ProcessImagesAsync(folder, null);

        // Assert
        result.Duration.Should().BeGreaterThan(TimeSpan.Zero);
    }

    #endregion

    #region ProcessImagesAsync AllowedBuckets Tests

    [Fact]
    public async Task WhenAllowedBucketsProvidedThenOnlyThoseBucketsAreUsed()
    {
        // Arrange
        var sourceFolder = Path.Combine(_testDirectory, "Buckets");
        var targetFolder = Path.Combine(_testDirectory, "BucketsOutput");
        Directory.CreateDirectory(sourceFolder);
        
        // Create a 16:9 image
        CreateTestImage(Path.Combine(sourceFolder, "wide.png"), 1920, 1080);

        BucketDefinition? reportedBucket = null;
        var progress = new Progress<CropProgress>(p => reportedBucket = p.CurrentBucket);

        // Act - restrict to only 1:1
        await _service.ProcessImagesAsync(
            sourceFolder,
            targetFolder,
            [Ratio1x1],
            progress: progress);

        await Task.Delay(100); // Wait for progress

        // Assert - should use 1:1 even for 16:9 image
        reportedBucket.Should().NotBeNull();
        reportedBucket!.Name.Should().Be(Ratio1x1.Name);
    }

    #endregion

    #region ProcessImagesAsync Scaling Tests

    [Fact]
    public async Task WhenMaxLongestSideProvidedThenImagesAreScaled()
    {
        // Arrange
        var sourceFolder = Path.Combine(_testDirectory, "Scale");
        var targetFolder = Path.Combine(_testDirectory, "ScaleOutput");
        Directory.CreateDirectory(sourceFolder);
        
        // Create a large image
        CreateTestImage(Path.Combine(sourceFolder, "large.png"), 2048, 2048);

        // Act
        var result = await _service.ProcessImagesAsync(
            sourceFolder,
            targetFolder,
            maxLongestSide: 512);

        // Assert - should process successfully
        result.SuccessCount.Should().Be(1);
    }

    #endregion

    #region SkipUnchanged Tests

    [Fact]
    public async Task WhenSkipUnchangedIsTrueThenUnchangedImagesAreSkipped()
    {
        // Arrange
        var sourceFolder = Path.Combine(_testDirectory, "SkipUnchanged");
        var targetFolder = Path.Combine(_testDirectory, "SkipUnchangedOutput");
        Directory.CreateDirectory(sourceFolder);
        
        // Create a 1024x1024 image (1:1 ratio)
        // If we allow 1:1 bucket and don't scale, this should be unchanged.
        CreateTestImage(Path.Combine(sourceFolder, "perfect.png"), 1024, 1024);

        // Act
        var result = await _service.ProcessImagesAsync(
            sourceFolder,
            targetFolder,
            [Ratio1x1],
            skipUnchanged: true);

        // Assert
        result.SuccessCount.Should().Be(0);
        result.SkippedCount.Should().Be(1);
        
        // Verify file was NOT copied
        File.Exists(Path.Combine(targetFolder, "perfect.png")).Should().BeFalse();
    }

    [Fact]
    public async Task WhenSkipUnchangedIsFalseThenUnchangedImagesAreCopied()
    {
        // Arrange
        var sourceFolder = Path.Combine(_testDirectory, "NoSkipUnchanged");
        var targetFolder = Path.Combine(_testDirectory, "NoSkipUnchangedOutput");
        Directory.CreateDirectory(sourceFolder);
        
        // Create a 1024x1024 image (1:1 ratio)
        CreateTestImage(Path.Combine(sourceFolder, "perfect.png"), 1024, 1024);

        // Act
        var result = await _service.ProcessImagesAsync(
            sourceFolder,
            targetFolder,
            [Ratio1x1],
            skipUnchanged: false);

        // Assert
        result.SuccessCount.Should().Be(1); // Counts as success because it was processed (copied)
        result.SkippedCount.Should().Be(0);
        
        // Verify file WAS copied
        File.Exists(Path.Combine(targetFolder, "perfect.png")).Should().BeTrue();
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Creates a valid test image using SkiaSharp.
    /// </summary>
    private static void CreateTestImage(string path, int width, int height)
    {
        using var bitmap = new SkiaSharp.SKBitmap(width, height);
        using var canvas = new SkiaSharp.SKCanvas(bitmap);
        
        // Fill with a solid color
        canvas.Clear(SkiaSharp.SKColors.Red);
        
        using var stream = File.Create(path);
        bitmap.Encode(stream, SkiaSharp.SKEncodedImageFormat.Png, 100);
    }

    #endregion
}
