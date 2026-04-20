using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.ViewModels;

/// <summary>
/// Tests for <see cref="ImageEditorViewModel.SendToBatchUpscaleCommand"/>.
/// </summary>
public class ImageEditorViewModelSendToBatchUpscaleTests
{
    private readonly Mock<IDatasetEventAggregator> _mockAggregator = new();

    private ImageEditorViewModel CreateSut(IDatasetEventAggregator? aggregator = null)
    {
        return new ImageEditorViewModel(eventAggregator: aggregator ?? _mockAggregator.Object);
    }

    [Fact]
    public void WhenSendToBatchUpscaleExecuted_PublishesNavigationWithImagePath()
    {
        const string imagePath = @"C:\datasets\test\image.png";
        NavigateToBatchUpscaleEventArgs? capturedArgs = null;

        _mockAggregator
            .Setup(a => a.PublishNavigateToBatchUpscale(It.IsAny<NavigateToBatchUpscaleEventArgs>()))
            .Callback<NavigateToBatchUpscaleEventArgs>(args => capturedArgs = args);

        var sut = CreateSut();
        sut.LoadImage(imagePath);

        sut.SendToBatchUpscaleCommand.Execute(null);

        capturedArgs.Should().NotBeNull();
        capturedArgs!.ImagePaths.Should().NotBeNull()
            .And.ContainSingle()
            .Which.Should().Be(imagePath);
    }

    [Fact]
    public void WhenNoImageLoaded_SendToBatchUpscaleCannotExecute()
    {
        var sut = CreateSut();

        sut.SendToBatchUpscaleCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void WhenImageLoaded_SendToBatchUpscaleCanExecute()
    {
        var sut = CreateSut();
        sut.LoadImage(@"C:\test\image.png");

        sut.SendToBatchUpscaleCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public void WhenNoImageLoaded_SendToBatchUpscaleDoesNotPublish()
    {
        var sut = CreateSut();

        sut.SendToBatchUpscaleCommand.Execute(null);

        _mockAggregator.Verify(
            a => a.PublishNavigateToBatchUpscale(It.IsAny<NavigateToBatchUpscaleEventArgs>()),
            Times.Never);
    }

    [Fact]
    public void WhenEventAggregatorIsNull_SendToBatchUpscaleDoesNotThrow()
    {
        var sut = CreateSut(aggregator: null!);
        sut.LoadImage(@"C:\test\image.png");

        var act = () => sut.SendToBatchUpscaleCommand.Execute(null);
        act.Should().NotThrow();
    }

    [Fact]
    public void WhenSaveImageFuncProvided_SendToBatchUpscaleExportsTempFile()
    {
        const string imagePath = @"C:\datasets\test\image.png";
        NavigateToBatchUpscaleEventArgs? capturedArgs = null;

        _mockAggregator
            .Setup(a => a.PublishNavigateToBatchUpscale(It.IsAny<NavigateToBatchUpscaleEventArgs>()))
            .Callback<NavigateToBatchUpscaleEventArgs>(args => capturedArgs = args);

        var sut = CreateSut();
        sut.LoadImage(imagePath);
        sut.SaveImageFunc = _ => true;

        sut.SendToBatchUpscaleCommand.Execute(null);

        capturedArgs.Should().NotBeNull();
        capturedArgs!.ImagePaths.Should().ContainSingle()
            .Which.Should().NotBe(imagePath, "the edited image should be exported to a temp file");
    }

    [Fact]
    public void WhenSaveImageFuncFails_SendToBatchUpscaleFallsBackToOriginalPath()
    {
        const string imagePath = @"C:\datasets\test\image.png";
        NavigateToBatchUpscaleEventArgs? capturedArgs = null;

        _mockAggregator
            .Setup(a => a.PublishNavigateToBatchUpscale(It.IsAny<NavigateToBatchUpscaleEventArgs>()))
            .Callback<NavigateToBatchUpscaleEventArgs>(args => capturedArgs = args);

        var sut = CreateSut();
        sut.LoadImage(imagePath);
        sut.SaveImageFunc = _ => false;

        sut.SendToBatchUpscaleCommand.Execute(null);

        capturedArgs.Should().NotBeNull();
        capturedArgs!.ImagePaths.Should().ContainSingle()
            .Which.Should().Be(imagePath);
    }
}
