using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DiffusionNexus.Domain.Autocropper;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.UI.ViewModels.Tabs;
using FluentAssertions;
using Moq;
using Xunit;

namespace DiffusionNexus.Tests.Autocropper;

/// <summary>
/// Unit tests for AutoScaleCropTabViewModel.
/// Tests ViewModel logic, property bindings, and command behavior.
/// </summary>
public class AutoScaleCropTabViewModelTests
{
    private readonly Mock<IImageCropperService> _mockCropperService;

    public AutoScaleCropTabViewModelTests()
    {
        _mockCropperService = new Mock<IImageCropperService>();
        
        // Default setup - empty folder
        _mockCropperService
            .Setup(s => s.ScanFolder(It.IsAny<string>()))
            .Returns(new FolderScanResult(0, 0, []));
    }

    private AutoScaleCropTabViewModel CreateViewModel() => new(null);

    #region Constructor Tests

    [Fact]
    public void WhenViewModelCreatedThenDefaultValuesAreSet()
    {
        // Act
        var viewModel = CreateViewModel();

        // Assert
        viewModel.SourceFolder.Should().BeEmpty();
        viewModel.TargetFolder.Should().BeEmpty();
        viewModel.IsProcessing.Should().BeFalse();
        viewModel.ProcessedCount.Should().Be(0);
        viewModel.SuccessCount.Should().Be(0);
        viewModel.FailedCount.Should().Be(0);
        viewModel.SkippedCount.Should().Be(0);
    }

    [Fact]
    public void WhenViewModelCreatedThenBucketOptionsAreInitialized()
    {
        // Act
        var viewModel = CreateViewModel();
        
        // Assert
        viewModel.BucketOptions.Should().NotBeEmpty();
        viewModel.BucketOptions.Should().HaveCount(7); // 7 standard buckets
    }

    [Fact]
    public void WhenViewModelCreatedThenAllBucketsAreSelectedByDefault()
    {
        // Act
        var viewModel = CreateViewModel();
        
        // Assert
        viewModel.BucketOptions.Should().OnlyContain(b => b.IsSelected);
        viewModel.HasSelectedBuckets.Should().BeTrue();
    }

    [Fact]
    public void WhenViewModelCreatedThenResolutionOptionsAreInitialized()
    {
        // Act
        var viewModel = CreateViewModel();
        
        // Assert
        viewModel.ResolutionOptions.Should().NotBeEmpty();
        viewModel.ResolutionOptions.Should().Contain(ResolutionOption.None);
        viewModel.ResolutionOptions.Should().Contain(ResolutionOption.Custom);
    }

    [Fact]
    public void WhenViewModelCreatedThenNoScalingIsSelectedByDefault()
    {
        // Act
        var viewModel = CreateViewModel();

        // Assert
        viewModel.SelectedResolution.Should().Be(ResolutionOption.None);
    }

    [Fact]
    public void WhenViewModelCreatedThenPaddingFillOptionsAreInitialized()
    {
        // Act
        var viewModel = CreateViewModel();
        
        // Assert
        viewModel.PaddingFillOptions.Should().NotBeEmpty();
        viewModel.PaddingFillOptions.Should().HaveCount(4);
    }

    [Fact]
    public void WhenViewModelCreatedThenUsePaddingIsFalseByDefault()
    {
        // Act
        var viewModel = CreateViewModel();

        // Assert
        viewModel.UsePadding.Should().BeFalse();
        viewModel.IsPadMode.Should().BeFalse();
    }

    #endregion

    #region IsOverwriteMode Tests

    [Fact]
    public void WhenTargetFolderIsEmptyThenIsOverwriteModeIsTrue()
    {
        // Arrange
        var viewModel = CreateViewModel();

        // Act & Assert
        viewModel.IsOverwriteMode.Should().BeTrue();
    }

    [Fact]
    public void WhenTargetFolderIsSetThenIsOverwriteModeIsFalse()
    {
        // Arrange
        var viewModel = CreateViewModel();

        // Act
        viewModel.TargetFolder = @"C:\Output";

        // Assert
        viewModel.IsOverwriteMode.Should().BeFalse();
    }

    [Fact]
    public void WhenTargetFolderIsWhitespaceThenIsOverwriteModeIsTrue()
    {
        // Arrange
        var viewModel = CreateViewModel();

        // Act
        viewModel.TargetFolder = "   ";

        // Assert
        viewModel.IsOverwriteMode.Should().BeTrue();
    }

    #endregion

    #region CanStart Tests

    [Fact]
    public void WhenNoSourceFolderThenCanStartIsFalse()
    {
        // Arrange
        var viewModel = CreateViewModel();

        // Assert
        viewModel.CanStart.Should().BeFalse();
    }

    [Fact]
    public void WhenOverwriteModeAndNotConfirmedThenCanStartIsFalse()
    {
        // Arrange
        var viewModel = CreateViewModel();
        viewModel.SourceFolder = @"C:\Source";
        viewModel.OverwriteConfirmed = false;

        // Assert
        viewModel.CanStart.Should().BeFalse();
    }

    [Fact]
    public void WhenNoBucketsSelectedThenCanStartIsFalse()
    {
        // Arrange
        var viewModel = CreateViewModel();
        viewModel.SourceFolder = @"C:\Source";
        viewModel.TargetFolder = @"C:\Output";

        // Deselect all buckets
        foreach (var bucket in viewModel.BucketOptions)
        {
            bucket.IsSelected = false;
        }

        // Assert
        viewModel.CanStart.Should().BeFalse();
    }

    #endregion

    #region BucketOption Tests

    [Fact]
    public void WhenBucketDeselectedThenHasSelectedBucketsUpdates()
    {
        // Arrange
        var viewModel = CreateViewModel();

        // Act - deselect all but one
        foreach (var bucket in viewModel.BucketOptions.Skip(1))
        {
            bucket.IsSelected = false;
        }

        // Assert
        viewModel.HasSelectedBuckets.Should().BeTrue();
        viewModel.SelectedBuckets.Should().HaveCount(1);
    }

    [Fact]
    public void WhenAllBucketsDeselectedThenHasSelectedBucketsIsFalse()
    {
        // Arrange
        var viewModel = CreateViewModel();

        // Act
        foreach (var bucket in viewModel.BucketOptions)
        {
            bucket.IsSelected = false;
        }

        // Assert
        viewModel.HasSelectedBuckets.Should().BeFalse();
        viewModel.SelectedBuckets.Should().BeEmpty();
    }

    [Fact]
    public void WhenSelectAllBucketsCalledThenAllBucketsAreSelected()
    {
        // Arrange
        var viewModel = CreateViewModel();
        foreach (var bucket in viewModel.BucketOptions)
        {
            bucket.IsSelected = false;
        }

        // Act
        viewModel.SelectAllBucketsCommand.Execute(null);

        // Assert
        viewModel.BucketOptions.Should().OnlyContain(b => b.IsSelected);
    }

    [Fact]
    public void WhenDeselectAllBucketsCalledThenNoBucketsAreSelected()
    {
        // Arrange
        var viewModel = CreateViewModel();

        // Act
        viewModel.DeselectAllBucketsCommand.Execute(null);

        // Assert
        viewModel.BucketOptions.Should().OnlyContain(b => !b.IsSelected);
    }

    #endregion

    #region FitMode Tests

    [Fact]
    public void WhenUsePaddingSetToTrueThenIsPadModeIsTrue()
    {
        // Arrange
        var viewModel = CreateViewModel();

        // Act
        viewModel.UsePadding = true;

        // Assert
        viewModel.IsPadMode.Should().BeTrue();
    }

    [Fact]
    public void WhenUsePaddingSetToFalseThenIsPadModeIsFalse()
    {
        // Arrange
        var viewModel = CreateViewModel();
        viewModel.UsePadding = true;

        // Act
        viewModel.UsePadding = false;

        // Assert
        viewModel.IsPadMode.Should().BeFalse();
    }

    [Fact]
    public void WhenSelectedPaddingFillChangedThenPropertyIsUpdated()
    {
        // Arrange
        var viewModel = CreateViewModel();
        var whiteFill = viewModel.PaddingFillOptions.First(p => p.Mode == PaddingFillMode.White);
        
        // Act
        viewModel.SelectedPaddingFill = whiteFill;
        
        // Assert
        viewModel.SelectedPaddingFill.Mode.Should().Be(PaddingFillMode.White);
    }

    #endregion

    #region ProgressPercentage Tests

    [Fact]
    public void WhenNoImagesThenProgressPercentageIsZero()
    {
        // Arrange
        var viewModel = CreateViewModel();

        // Assert
        viewModel.ProgressPercentage.Should().Be(0);
    }

    #endregion

    #region ClearTargetFolder Command Tests

    [Fact]
    public void WhenClearTargetFolderCalledThenTargetFolderIsEmpty()
    {
        // Arrange
        var viewModel = CreateViewModel();
        viewModel.TargetFolder = @"C:\Output";

        // Act
        viewModel.ClearTargetFolderCommand.Execute(null);

        // Assert
        viewModel.TargetFolder.Should().BeEmpty();
    }

    #endregion

    #region Cancel Command Tests

    [Fact]
    public void WhenCancelCalledThenStatusMessageIsUpdated()
    {
        // Arrange
        var viewModel = CreateViewModel();

        // Act
        viewModel.CancelCommand.Execute(null);

        // Assert
        viewModel.StatusMessage.Should().Contain("Cancelling");
    }

    #endregion
}
