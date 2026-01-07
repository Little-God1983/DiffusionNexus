using System;
using System.Collections.Generic;
using DiffusionNexus.Domain.Autocropper;
using DiffusionNexus.Service.Services;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Tests.Autocropper;

/// <summary>
/// Unit tests for AspectRatioCropper.
/// Tests aspect ratio calculation, bucket selection, and dimension rounding.
/// </summary>
public class AspectRatioCropperTests
{
    private static readonly BucketDefinition Ratio16x9 = new() { Name = "16:9", Width = 16, Height = 9 };
    private static readonly BucketDefinition Ratio9x16 = new() { Name = "9:16", Width = 9, Height = 16 };
    private static readonly BucketDefinition Ratio1x1 = new() { Name = "1:1", Width = 1, Height = 1 };
    private static readonly BucketDefinition Ratio4x3 = new() { Name = "4:3", Width = 4, Height = 3 };
    private static readonly BucketDefinition Ratio3x4 = new() { Name = "3:4", Width = 3, Height = 4 };
    private static readonly BucketDefinition Ratio5x4 = new() { Name = "5:4", Width = 5, Height = 4 };
    private static readonly BucketDefinition Ratio4x5 = new() { Name = "4:5", Width = 4, Height = 5 };

    #region Constructor Tests

    [Fact]
    public void WhenCropperCreatedThenAllBucketsAreAllowedByDefault()
    {
        // Arrange
        var cropper = new AspectRatioCropper();

        // Act - test with images that would match each bucket if available
        var result1x1 = cropper.CalculateCrop(1024, 1024);

        // Assert - should match 1:1 bucket
        result1x1.Bucket.Name.Should().Be(Ratio1x1.Name);
    }

    #endregion

    #region CalculateCrop Validation Tests

    [Theory]
    [InlineData(0, 100)]
    [InlineData(100, 0)]
    [InlineData(-1, 100)]
    [InlineData(100, -1)]
    [InlineData(0, 0)]
    public void WhenCalculateCropCalledWithInvalidDimensionsThenThrowsException(int width, int height)
    {
        // Arrange
        var cropper = new AspectRatioCropper();

        // Act
        var action = () => cropper.CalculateCrop(width, height);

        // Assert
        action.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void WhenCalculateCropCalledWithValidDimensionsThenReturnsResult()
    {
        // Arrange
        var cropper = new AspectRatioCropper();

        // Act
        var result = cropper.CalculateCrop(1920, 1080);

        // Assert
        result.Should().NotBeNull();
        result.TargetWidth.Should().BeGreaterThan(0);
        result.TargetHeight.Should().BeGreaterThan(0);
    }

    #endregion

    #region CalculatePad Validation Tests

    [Theory]
    [InlineData(0, 100)]
    [InlineData(100, 0)]
    [InlineData(-1, 100)]
    [InlineData(100, -1)]
    public void WhenCalculatePadCalledWithInvalidDimensionsThenThrowsException(int width, int height)
    {
        // Arrange
        var cropper = new AspectRatioCropper();

        // Act
        var action = () => cropper.CalculatePad(width, height);

        // Assert
        action.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void WhenCalculatePadCalledWithValidDimensionsThenReturnsResult()
    {
        // Arrange
        var cropper = new AspectRatioCropper();

        // Act
        var result = cropper.CalculatePad(1920, 1080);

        // Assert
        result.Should().NotBeNull();
        result.CanvasWidth.Should().BeGreaterThanOrEqualTo(1920);
        result.CanvasHeight.Should().BeGreaterThanOrEqualTo(1080);
    }

    #endregion

    #region Aspect Ratio Bucket Matching Tests (Crop)

    [Fact]
    public void WhenImageIs16x9ThenReturns16x9Bucket()
    {
        // Arrange
        var cropper = new AspectRatioCropper();

        // 1920x1080 is exactly 16:9
        // Act
        var result = cropper.CalculateCrop(1920, 1080);

        // Assert
        result.Bucket.Name.Should().Be(Ratio16x9.Name);
    }

    [Fact]
    public void WhenImageIs9x16ThenReturns9x16Bucket()
    {
        // Arrange
        var cropper = new AspectRatioCropper();

        // 1080x1920 is exactly 9:16
        // Act
        var result = cropper.CalculateCrop(1080, 1920);

        // Assert
        result.Bucket.Name.Should().Be(Ratio9x16.Name);
    }

    [Fact]
    public void WhenImageIs1x1ThenReturns1x1Bucket()
    {
        // Arrange
        var cropper = new AspectRatioCropper();

        // Act
        var result = cropper.CalculateCrop(1024, 1024);

        // Assert
        result.Bucket.Name.Should().Be(Ratio1x1.Name);
    }

    [Fact]
    public void WhenImageIs4x3ThenReturns4x3Bucket()
    {
        // Arrange
        var cropper = new AspectRatioCropper();

        // 1600x1200 is exactly 4:3
        // Act
        var result = cropper.CalculateCrop(1600, 1200);

        // Assert
        result.Bucket.Name.Should().Be(Ratio4x3.Name);
    }

    [Fact]
    public void WhenImageIs3x4ThenReturns3x4Bucket()
    {
        // Arrange
        var cropper = new AspectRatioCropper();

        // 1200x1600 is exactly 3:4
        // Act
        var result = cropper.CalculateCrop(1200, 1600);

        // Assert
        result.Bucket.Name.Should().Be(Ratio3x4.Name);
    }

    [Fact]
    public void WhenImageIs5x4ThenReturns5x4Bucket()
    {
        // Arrange
        var cropper = new AspectRatioCropper();

        // 1280x1024 is exactly 5:4
        // Act
        var result = cropper.CalculateCrop(1280, 1024);

        // Assert
        result.Bucket.Name.Should().Be(Ratio5x4.Name);
    }

    [Fact]
    public void WhenImageIs4x5ThenReturns4x5Bucket()
    {
        // Arrange
        var cropper = new AspectRatioCropper();

        // 1024x1280 is exactly 4:5
        // Act
        var result = cropper.CalculateCrop(1024, 1280);

        // Assert
        result.Bucket.Name.Should().Be(Ratio4x5.Name);
    }

    [Fact]
    public void WhenImageIsCloserTo16x9ThenReturns16x9Bucket()
    {
        // Arrange
        var cropper = new AspectRatioCropper();

        // 1900x1080 is close to 16:9 (ratio ~1.76 vs 1.78)
        // Act
        var result = cropper.CalculateCrop(1900, 1080);

        // Assert
        result.Bucket.Name.Should().Be(Ratio16x9.Name);
    }

    #endregion

    #region Aspect Ratio Bucket Matching Tests (Pad)

    [Fact]
    public void WhenPadding16x9ImageThenReturns16x9Bucket()
    {
        // Arrange
        var cropper = new AspectRatioCropper();

        // Act
        var result = cropper.CalculatePad(1920, 1080);

        // Assert
        result.Bucket.Name.Should().Be(Ratio16x9.Name);
    }

    [Fact]
    public void WhenPadding1x1ImageThenReturns1x1Bucket()
    {
        // Arrange
        var cropper = new AspectRatioCropper();

        // Act
        var result = cropper.CalculatePad(1024, 1024);

        // Assert
        result.Bucket.Name.Should().Be(Ratio1x1.Name);
    }

    #endregion

    #region Dimension Rounding Tests

    [Fact]
    public void WhenCropCalculatedThenTargetWidthIsMultipleOf8()
    {
        // Arrange
        var cropper = new AspectRatioCropper();

        // Act
        var result = cropper.CalculateCrop(1920, 1080);

        // Assert
        (result.TargetWidth % 8).Should().Be(0);
    }

    [Fact]
    public void WhenCropCalculatedThenTargetHeightIsMultipleOf8()
    {
        // Arrange
        var cropper = new AspectRatioCropper();

        // Act
        var result = cropper.CalculateCrop(1920, 1080);

        // Assert
        (result.TargetHeight % 8).Should().Be(0);
    }

    [Fact]
    public void WhenPadCalculatedThenCanvasWidthIsMultipleOf8()
    {
        // Arrange
        var cropper = new AspectRatioCropper();

        // Act
        var result = cropper.CalculatePad(1920, 1080);

        // Assert
        (result.CanvasWidth % 8).Should().Be(0);
    }

    [Fact]
    public void WhenPadCalculatedThenCanvasHeightIsMultipleOf8()
    {
        // Arrange
        var cropper = new AspectRatioCropper();

        // Act
        var result = cropper.CalculatePad(1920, 1080);

        // Assert
        (result.CanvasHeight % 8).Should().Be(0);
    }

    [Theory]
    [InlineData(1000, 1000)]
    [InlineData(1001, 1001)]
    [InlineData(1005, 1005)]
    [InlineData(1007, 1007)]
    public void WhenDimensionsNotMultipleOf8ThenCropRoundsDown(int width, int height)
    {
        // Arrange
        var cropper = new AspectRatioCropper();

        // Act
        var result = cropper.CalculateCrop(width, height);

        // Assert
        (result.TargetWidth % 8).Should().Be(0);
        (result.TargetHeight % 8).Should().Be(0);
        result.TargetWidth.Should().BeLessThanOrEqualTo(width);
        result.TargetHeight.Should().BeLessThanOrEqualTo(height);
    }

    [Theory]
    [InlineData(1000, 1000)]
    [InlineData(1001, 1001)]
    [InlineData(1005, 1005)]
    public void WhenDimensionsNotMultipleOf8ThenPadRoundsUp(int width, int height)
    {
        // Arrange
        var cropper = new AspectRatioCropper();

        // Act
        var result = cropper.CalculatePad(width, height);

        // Assert
        (result.CanvasWidth % 8).Should().Be(0);
        (result.CanvasHeight % 8).Should().Be(0);
        result.CanvasWidth.Should().BeGreaterThanOrEqualTo(width);
        result.CanvasHeight.Should().BeGreaterThanOrEqualTo(height);
    }

    #endregion

    #region Crop Offset Tests

    [Fact]
    public void WhenImageAlreadyMatchesBucketThenCropOffsetsAreZero()
    {
        // Arrange
        var cropper = new AspectRatioCropper();

        // 1024x1024 is exactly 1:1 and multiples of 8
        // Act
        var result = cropper.CalculateCrop(1024, 1024);

        // Assert
        result.CropX.Should().Be(0);
        result.CropY.Should().Be(0);
    }

    [Fact]
    public void WhenCroppingWiderImageThenCropXIsPositive()
    {
        // Arrange
        var cropper = new AspectRatioCropper();
        cropper.SetAllowedBuckets([Ratio1x1]);

        // 1200x1000 is wider than 1:1, should crop horizontally
        // Act
        var result = cropper.CalculateCrop(1200, 1000);

        // Assert
        result.CropX.Should().BeGreaterThan(0);
        result.CropY.Should().Be(0);
    }

    [Fact]
    public void WhenCroppingTallerImageThenCropYIsPositive()
    {
        // Arrange
        var cropper = new AspectRatioCropper();
        cropper.SetAllowedBuckets([Ratio1x1]);

        // 1000x1200 is taller than 1:1, should crop vertically
        // Act
        var result = cropper.CalculateCrop(1000, 1200);

        // Assert
        result.CropX.Should().Be(0);
        result.CropY.Should().BeGreaterThan(0);
    }

    [Fact]
    public void WhenCroppingThenOffsetsAreCentered()
    {
        // Arrange
        var cropper = new AspectRatioCropper();
        cropper.SetAllowedBuckets([Ratio1x1]);

        // 1200x1000 - crop to 1:1 should center the crop
        // Act
        var result = cropper.CalculateCrop(1200, 1000);

        // Assert - offset should be approximately half the difference
        int expectedCropX = (1200 - result.TargetWidth) / 2;
        result.CropX.Should().Be(expectedCropX);
    }

    #endregion

    #region Pad Offset Tests

    [Fact]
    public void WhenImageAlreadyMatchesBucketThenPadOffsetsAreZero()
    {
        // Arrange
        var cropper = new AspectRatioCropper();

        // Act
        var result = cropper.CalculatePad(1024, 1024);

        // Assert
        result.ImageX.Should().Be(0);
        result.ImageY.Should().Be(0);
    }

    [Fact]
    public void WhenPaddingWiderImageThenImageYIsPositive()
    {
        // Arrange
        var cropper = new AspectRatioCropper();
        cropper.SetAllowedBuckets([Ratio1x1]);

        // Act
        var result = cropper.CalculatePad(1200, 1000);

        // Assert
        // For 1:1, we need to pad vertically, so ImageY > 0
        result.ImageY.Should().BeGreaterThan(0);
    }

    [Fact]
    public void WhenPaddingTallerImageThenImageXIsPositive()
    {
        // Arrange
        var cropper = new AspectRatioCropper();
        cropper.SetAllowedBuckets([Ratio1x1]);

        // Act
        var result = cropper.CalculatePad(1000, 1200);

        // Assert
        // For 1:1, we need to pad horizontally, so ImageX > 0
        result.ImageX.Should().BeGreaterThan(0);
    }

    [Fact]
    public void WhenPaddingThenImageIsCentered()
    {
        // Arrange
        var cropper = new AspectRatioCropper();
        cropper.SetAllowedBuckets([Ratio1x1]);

        // Act
        var result = cropper.CalculatePad(1000, 1200);

        // Assert - image offset should be approximately half the difference
        int expectedImageX = (result.CanvasWidth - 1000) / 2;
        result.ImageX.Should().Be(expectedImageX);
    }

    #endregion

    #region SetAllowedBuckets Tests

    [Fact]
    public void WhenSetAllowedBucketsWithNullThenAllBucketsAreUsed()
    {
        // Arrange
        var cropper = new AspectRatioCropper();

        // Act
        cropper.SetAllowedBuckets(null);
        var result = cropper.CalculateCrop(1920, 1080);

        // Assert - should find 16:9 as it's the best match
        result.Bucket.Name.Should().Be(Ratio16x9.Name);
    }

    [Fact]
    public void WhenSetAllowedBucketsWithEmptyArrayThenAllBucketsAreUsed()
    {
        // Arrange
        var cropper = new AspectRatioCropper();

        // Act
        cropper.SetAllowedBuckets([]);
        var result = cropper.CalculateCrop(1920, 1080);

        // Assert - should find 16:9 as it's the best match
        result.Bucket.Name.Should().Be(Ratio16x9.Name);
    }

    [Fact]
    public void WhenSetAllowedBucketsWithSingleBucketThenOnlyThatBucketIsUsed()
    {
        // Arrange
        var cropper = new AspectRatioCropper();

        // Act - restrict to only 1:1
        cropper.SetAllowedBuckets([Ratio1x1]);
        var result = cropper.CalculateCrop(1920, 1080);

        // Assert - must use 1:1 even though 16:9 is closer
        result.Bucket.Name.Should().Be(Ratio1x1.Name);
    }

    [Fact]
    public void WhenSetAllowedBucketsWithMultipleBucketsThenBestMatchIsUsed()
    {
        // Arrange
        var cropper = new AspectRatioCropper();

        // Act - restrict to 1:1 and 4:3
        cropper.SetAllowedBuckets([Ratio1x1, Ratio4x3]);
        var result = cropper.CalculateCrop(1600, 1200);

        // Assert - should match 4:3 as it's exact
        result.Bucket.Name.Should().Be(Ratio4x3.Name);
    }

    #endregion

    #region Target Dimensions Constraint Tests

    [Fact]
    public void WhenCropCalculatedThenTargetWidthDoesNotExceedSource()
    {
        // Arrange
        var cropper = new AspectRatioCropper();

        // Act
        var result = cropper.CalculateCrop(1920, 1080);

        // Assert
        result.TargetWidth.Should().BeLessThanOrEqualTo(1920);
    }

    [Fact]
    public void WhenCropCalculatedThenTargetHeightDoesNotExceedSource()
    {
        // Arrange
        var cropper = new AspectRatioCropper();

        // Act
        var result = cropper.CalculateCrop(1920, 1080);

        // Assert
        result.TargetHeight.Should().BeLessThanOrEqualTo(1080);
    }

    [Fact]
    public void WhenCropCalculatedThenCropXPlusWidthDoesNotExceedSource()
    {
        // Arrange
        var cropper = new AspectRatioCropper();

        // Act
        var result = cropper.CalculateCrop(1920, 1080);

        // Assert
        (result.CropX + result.TargetWidth).Should().BeLessThanOrEqualTo(1920);
    }

    [Fact]
    public void WhenCropCalculatedThenCropYPlusHeightDoesNotExceedSource()
    {
        // Arrange
        var cropper = new AspectRatioCropper();

        // Act
        var result = cropper.CalculateCrop(1920, 1080);

        // Assert
        (result.CropY + result.TargetHeight).Should().BeLessThanOrEqualTo(1080);
    }

    [Fact]
    public void WhenPadCalculatedThenCanvasContainsOriginalImage()
    {
        // Arrange
        var cropper = new AspectRatioCropper();

        // Act
        var result = cropper.CalculatePad(1920, 1080);

        // Assert
        result.CanvasWidth.Should().BeGreaterThanOrEqualTo(1920);
        result.CanvasHeight.Should().BeGreaterThanOrEqualTo(1080);
        (result.ImageX + 1920).Should().BeLessThanOrEqualTo(result.CanvasWidth);
        (result.ImageY + 1080).Should().BeLessThanOrEqualTo(result.CanvasHeight);
    }

    #endregion

    #region Minimal Pixel Loss/Addition Tests

    [Fact]
    public void WhenImageMatchesBucketExactlyThenNoPixelsAreLost()
    {
        // Arrange
        var cropper = new AspectRatioCropper();

        // 1024x1024 is exactly 1:1 and multiples of 8
        // Act
        var result = cropper.CalculateCrop(1024, 1024);

        // Assert
        result.TargetWidth.Should().Be(1024);
        result.TargetHeight.Should().Be(1024);
    }

    [Fact]
    public void WhenImageMatchesBucketExactlyThenNoPaddingAdded()
    {
        // Arrange
        var cropper = new AspectRatioCropper();

        // Act
        var result = cropper.CalculatePad(1024, 1024);

        // Assert
        result.CanvasWidth.Should().Be(1024);
        result.CanvasHeight.Should().Be(1024);
    }

    [Fact]
    public void WhenChoosingBetweenBucketsThenMinimalPixelLossIsPreferred()
    {
        // Arrange
        var cropper = new AspectRatioCropper();
        cropper.SetAllowedBuckets([Ratio1x1, Ratio16x9]);

        // 1920x1080 should prefer 16:9 (minimal loss) over 1:1 (large loss)
        // Act
        var result = cropper.CalculateCrop(1920, 1080);

        // Assert
        result.Bucket.Name.Should().Be(Ratio16x9.Name);
    }

    [Fact]
    public void WhenChoosingBetweenBucketsForPadThenMinimalCanvasAdditionIsPreferred()
    {
        // Arrange
        var cropper = new AspectRatioCropper();
        cropper.SetAllowedBuckets([Ratio1x1, Ratio16x9]);

        // 1920x1080 should prefer 16:9 (minimal addition) over 1:1 (large addition)
        // Act
        var result = cropper.CalculatePad(1920, 1080);

        // Assert
        result.Bucket.Name.Should().Be(Ratio16x9.Name);
    }

    #endregion

    #region Edge Case Tests

    [Fact]
    public void WhenImageIsVerySmallThenCropStillWorks()
    {
        // Arrange
        var cropper = new AspectRatioCropper();

        // Very small image
        // Act
        var result = cropper.CalculateCrop(16, 16);

        // Assert
        result.Should().NotBeNull();
        result.TargetWidth.Should().BeGreaterThan(0);
        result.TargetHeight.Should().BeGreaterThan(0);
    }

    [Fact]
    public void WhenImageIsVerySmallThenPadStillWorks()
    {
        // Arrange
        var cropper = new AspectRatioCropper();

        // Very small image
        // Act
        var result = cropper.CalculatePad(16, 16);

        // Assert
        result.Should().NotBeNull();
        result.CanvasWidth.Should().BeGreaterThan(0);
        result.CanvasHeight.Should().BeGreaterThan(0);
    }

    [Fact]
    public void WhenImageIsVeryLargeThenCropStillWorks()
    {
        // Arrange
        var cropper = new AspectRatioCropper();

        // Very large image
        // Act
        var result = cropper.CalculateCrop(8000, 6000);

        // Assert
        result.Should().NotBeNull();
        (result.TargetWidth % 8).Should().Be(0);
        (result.TargetHeight % 8).Should().Be(0);
    }

    [Fact]
    public void WhenImageHasUnusualAspectRatioThenNearestBucketIsChosen()
    {
        // Arrange
        var cropper = new AspectRatioCropper();

        // 3:1 aspect ratio - very wide, should match 16:9 as closest
        // Act
        var result = cropper.CalculateCrop(3000, 1000);

        // Assert
        result.Bucket.Name.Should().Be(Ratio16x9.Name);
    }

    [Fact]
    public void WhenImageHasUnusualTallAspectRatioThenNearestBucketIsChosen()
    {
        // Arrange
        var cropper = new AspectRatioCropper();

        // 1:3 aspect ratio - very tall, should match 9:16 as closest
        // Act
        var result = cropper.CalculateCrop(1000, 3000);

        // Assert
        result.Bucket.Name.Should().Be(Ratio9x16.Name);
    }

    #endregion

    #region Static Helper Tests

    [Theory]
    [InlineData(8, 8)]
    [InlineData(9, 8)]
    [InlineData(15, 8)]
    [InlineData(16, 16)]
    [InlineData(17, 16)]
    public void WhenRoundDownToMultipleThenRoundsCorrectly(int input, int expected)
    {
        AspectRatioCropper.RoundDownToMultiple(input).Should().Be(expected);
    }

    [Theory]
    [InlineData(8, 8)]
    [InlineData(9, 16)]
    [InlineData(15, 16)]
    [InlineData(16, 16)]
    [InlineData(17, 24)]
    public void WhenRoundUpToMultipleThenRoundsCorrectly(int input, int expected)
    {
        AspectRatioCropper.RoundUpToMultiple(input).Should().Be(expected);
    }

    #endregion
}
