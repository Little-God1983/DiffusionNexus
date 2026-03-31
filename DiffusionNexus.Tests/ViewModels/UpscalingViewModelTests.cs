using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;
using Moq;

#pragma warning disable CS0618 // UpscalingViewModel is intentionally deprecated

namespace DiffusionNexus.Tests.ViewModels;

/// <summary>
/// Unit tests for <see cref="UpscalingViewModel"/>.
/// Covers the "Send to Batch Upscale" navigation command and its unified pipeline usage.
/// </summary>
public class UpscalingViewModelTests
{
    private readonly Mock<IDatasetEventAggregator> _mockAggregator = new();
    private readonly List<string> _deactivatedTools = [];

    private UpscalingViewModel CreateSut(
        Func<bool>? hasImage = null,
        Func<string?>? getImagePath = null,
        IDatasetEventAggregator? aggregator = null)
    {
        return new UpscalingViewModel(
            hasImage ?? (() => true),
            getImagePath ?? (() => @"C:\test\image.png"),
            tool => _deactivatedTools.Add(tool),
            aggregator ?? _mockAggregator.Object);
    }

    #region Constructor

    [Fact]
    public void WhenCreatedWithNullHasImage_ThrowsArgumentNullException()
    {
        var act = () => new UpscalingViewModel(null!, () => null, _ => { }, null);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void WhenCreatedWithNullGetImagePath_ThrowsArgumentNullException()
    {
        var act = () => new UpscalingViewModel(() => true, null!, _ => { }, null);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void WhenCreatedWithNullDeactivateOtherTools_ThrowsArgumentNullException()
    {
        var act = () => new UpscalingViewModel(() => true, () => null, null!, null);
        act.Should().Throw<ArgumentNullException>();
    }

    #endregion

    #region SendToBatchUpscaleCommand

    [Fact]
    public void WhenSendToBatchUpscaleExecuted_PublishesNavigationWithImagePathsList()
    {
        const string imagePath = @"C:\datasets\test\image.png";
        NavigateToBatchUpscaleEventArgs? capturedArgs = null;

        _mockAggregator
            .Setup(a => a.PublishNavigateToBatchUpscale(It.IsAny<NavigateToBatchUpscaleEventArgs>()))
            .Callback<NavigateToBatchUpscaleEventArgs>(args => capturedArgs = args);

        var sut = CreateSut(getImagePath: () => imagePath);

        sut.SendToBatchUpscaleCommand.Execute(null);

        capturedArgs.Should().NotBeNull();
        capturedArgs!.ImagePaths.Should().NotBeNull()
            .And.ContainSingle()
            .Which.Should().Be(imagePath);
    }

    [Fact]
    public void WhenSendToBatchUpscaleExecutedWithNoImage_DoesNotPublish()
    {
        var sut = CreateSut(getImagePath: () => null);

        sut.SendToBatchUpscaleCommand.Execute(null);

        _mockAggregator.Verify(
            a => a.PublishNavigateToBatchUpscale(It.IsAny<NavigateToBatchUpscaleEventArgs>()),
            Times.Never);
    }

    [Fact]
    public void WhenSendToBatchUpscaleExecutedWithEmptyPath_DoesNotPublish()
    {
        var sut = CreateSut(getImagePath: () => string.Empty);

        sut.SendToBatchUpscaleCommand.Execute(null);

        _mockAggregator.Verify(
            a => a.PublishNavigateToBatchUpscale(It.IsAny<NavigateToBatchUpscaleEventArgs>()),
            Times.Never);
    }

    [Fact]
    public void WhenNoImageLoaded_SendToBatchUpscaleCannotExecute()
    {
        var sut = CreateSut(hasImage: () => false, getImagePath: () => null);

        sut.SendToBatchUpscaleCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void WhenImageLoaded_SendToBatchUpscaleCanExecute()
    {
        var sut = CreateSut(hasImage: () => true, getImagePath: () => @"C:\test\image.png");

        sut.SendToBatchUpscaleCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public void WhenEventAggregatorIsNull_SendToBatchUpscaleDoesNotThrow()
    {
        var sut = CreateSut(aggregator: null!);

        // Constructor allows null aggregator; executing should silently no-op
        var act = () => sut.SendToBatchUpscaleCommand.Execute(null);
        act.Should().NotThrow();
    }

    #endregion
}
