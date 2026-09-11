using System.Collections.ObjectModel;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.ViewModels;
using DiffusionNexus.UI.ViewModels.Tabs;
using FluentAssertions;
using Moq;
using Xunit;

namespace DiffusionNexus.Tests.ViewModels;

/// <summary>
/// The Image Edit tab hands Send destinations a Skia re-encoded temp copy of the canvas, which
/// carries none of the original PNG text chunks — so the Batch Metadata Distiller (which exists to
/// read exactly those chunks) must not be offered there.
/// </summary>
public sealed class ImageEditTabSendMenuTests
{
    [Fact]
    public void SendMenu_HidesBatchMetadataDistiller()
    {
        var state = new Mock<IDatasetState>();
        state.Setup(s => s.Datasets).Returns(new ObservableCollection<DatasetCardViewModel>());
        var vm = new ImageEditTabViewModel(Mock.Of<IDatasetEventAggregator>(), state.Object);

        vm.ImageActions.ShowSendToMetadataDistiller.Should().BeFalse();
        vm.ImageActions.ShowWorkflowsMenu.Should().BeTrue("the generation workflows still apply to an edited image");
    }
}
