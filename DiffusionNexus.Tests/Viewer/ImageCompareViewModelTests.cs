using System.Collections.ObjectModel;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.Viewer;

public class ImageCompareViewModelTests
{
    [Fact]
    public void AssignImageCommand_AssignsImageToSelectedSide()
    {
        var viewModel = BuildViewModel();
        
        // Manually add items to test with since we're using a mock
        var targetItem = new ImageCompareItem("target.png", "Target");
        viewModel.FilmstripItems.Add(targetItem);

        viewModel.AssignSide = CompareAssignSide.Right;
        viewModel.AssignImageCommand.Execute(targetItem);

        viewModel.SelectedRightImage.Should().Be(targetItem);
    }

    [Fact]
    public void SwapImagesCommand_SwapsLeftAndRight()
    {
        var viewModel = BuildViewModel();
        var leftItem = new ImageCompareItem("left.png", "Left");
        var rightItem = new ImageCompareItem("right.png", "Right");
        
        viewModel.SelectedLeftImage = leftItem;
        viewModel.SelectedRightImage = rightItem;

        viewModel.SwapCommand.Execute(null);

        viewModel.SelectedLeftImage.Should().Be(rightItem);
        viewModel.SelectedRightImage.Should().Be(leftItem);
    }

    [Fact]
    public void ResetSliderCommand_ResetsToDefault()
    {
        var viewModel = BuildViewModel();
        viewModel.SliderValue = 12d;

        viewModel.ResetSliderCommand.Execute(null);

        viewModel.SliderValue.Should().Be(50d);
    }

    [Fact]
    public void DefaultConstructor_InitializesEmptyCollections()
    {
        var viewModel = new ImageCompareViewModel();

        viewModel.DatasetOptions.Should().BeEmpty();
        viewModel.LeftVersionOptions.Should().BeEmpty();
        viewModel.RightVersionOptions.Should().BeEmpty();
        viewModel.FilmstripItems.Should().BeEmpty();
    }

    [Fact]
    public void Constructor_WithDatasetState_LoadsDatasets()
    {
        var mockState = new Mock<IDatasetState>();
        var datasets = new ObservableCollection<DatasetCardViewModel>
        {
            new() { Name = "Dataset A", FolderPath = "/path/a" },
            new() { Name = "Dataset B", FolderPath = "/path/b" }
        };
        mockState.Setup(s => s.Datasets).Returns(datasets);

        var viewModel = new ImageCompareViewModel(mockState.Object);

        viewModel.DatasetOptions.Should().HaveCount(2);
        viewModel.SelectedLeftDataset.Should().NotBeNull();
        viewModel.SelectedRightDataset.Should().NotBeNull();
    }

    private static ImageCompareViewModel BuildViewModel()
    {
        var mockState = new Mock<IDatasetState>();
        mockState.Setup(s => s.Datasets).Returns(new ObservableCollection<DatasetCardViewModel>());
        
        return new ImageCompareViewModel(mockState.Object);
    }
}
