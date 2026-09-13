using DiffusionNexus.UI.ImageEditor.Services;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.ViewModels;

/// <summary>Opening the Move panel is mutually exclusive with the other tools, like every panel.</summary>
public class ImageEditorViewModelLayerTransformTests
{
    private readonly Mock<IDatasetEventAggregator> _mockAggregator = new();

    private ImageEditorViewModel CreateViewModel()
    {
        var sut = new ImageEditorViewModel(eventAggregator: _mockAggregator.Object);
        sut.LoadImage(@"C:\datasets\test\original.png");
        return sut;
    }

    [Fact]
    public void OpeningMove_ClosesCrop_AndActivatesTheToolId()
    {
        var vm = CreateViewModel();
        vm.IsCropToolActive = true;

        vm.LayerTransform.IsPanelOpen = true;

        vm.IsCropToolActive.Should().BeFalse();
        vm.Services.Tools.ActiveToolId.Should().Be(ToolIds.LayerTransform);
    }

    [Fact]
    public void OpeningCrop_ClosesMove()
    {
        var vm = CreateViewModel();
        vm.LayerTransform.IsPanelOpen = true;

        vm.IsCropToolActive = true;

        vm.LayerTransform.IsPanelOpen.Should().BeFalse();
    }
}
