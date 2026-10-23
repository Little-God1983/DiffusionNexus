using System.Collections.ObjectModel;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.ViewModels;
using DiffusionNexus.UI.ViewModels.Tabs;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.ViewModels;

/// <summary>
/// The Image Edit tab's Add To… / Send To… bar hands destinations a temp copy of the canvas. For a
/// JPEG original whose canvas gained transparency (extended canvas, removed background), that copy
/// must be a PNG: a JPEG would turn those areas black, and Add To… keeps the file for good (#584).
/// </summary>
public sealed class ImageEditTabHandOffTests
{
    [Theory]
    [InlineData(true, ".png")]
    [InlineData(false, ".jpg")]
    public async Task WhenAJpegCanvasIsSentOnThenItGoesOutAsPngOnlyIfItHasTransparency(
        bool hasTransparency, string expectedExtension)
    {
        var aggregator = new Mock<IDatasetEventAggregator>();
        NavigateToBatchUpscaleEventArgs? published = null;
        aggregator
            .Setup(a => a.PublishNavigateToBatchUpscale(It.IsAny<NavigateToBatchUpscaleEventArgs>()))
            .Callback<NavigateToBatchUpscaleEventArgs>(args => published = args);
        var state = new Mock<IDatasetState>();
        state.Setup(s => s.Datasets).Returns(new ObservableCollection<DatasetCardViewModel>());

        var vm = new ImageEditTabViewModel(aggregator.Object, state.Object);
        var written = new List<string>();
        vm.ImageEditor.LoadImage(@"C:\in\photo.jpg");
        vm.ImageEditor.SaveImageFunc = (path, _) => { written.Add(path); return true; };
        vm.ImageEditor.HasTransparencyFunc = () => hasTransparency;

        await vm.ImageActions.SendToBatchUpscaleCommand.ExecuteAsync(null);

        published.Should().NotBeNull();
        var handedOver = published!.ImagePaths.Should().ContainSingle().Subject;
        written.Should().Equal(handedOver);
        Path.GetExtension(handedOver).Should().Be(expectedExtension);
    }
}
