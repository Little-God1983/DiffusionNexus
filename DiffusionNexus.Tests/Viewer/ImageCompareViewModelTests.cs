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

    [Fact]
    public async Task LoadLeftFolderCommand_WhenFolderHasImages_CreatesTempDatasetAndSelectsIt()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllBytes(Path.Combine(tempDir, "a.png"), [0x89, 0x50, 0x4E, 0x47]);
            File.WriteAllBytes(Path.Combine(tempDir, "b.png"), [0x89, 0x50, 0x4E, 0x47]);

            var mockDialog = new Mock<IDialogService>();
            mockDialog.Setup(d => d.ShowOpenFolderDialogAsync(It.IsAny<string>()))
                      .ReturnsAsync(tempDir);

            var viewModel = BuildViewModel(mockDialog.Object);

            // Act
            await viewModel.LoadLeftFolderCommand.ExecuteAsync(null);

            // Assert
            viewModel.SelectedLeftDataset.Should().NotBeNull();
            viewModel.SelectedLeftDataset!.IsTemporary.Should().BeTrue();
            viewModel.SelectedLeftDataset.Name.Should().Contain(Path.GetFileName(tempDir));
            viewModel.IsTrayOpen.Should().BeTrue();
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task LoadRightFolderCommand_WhenFolderHasImages_SwitchesToDifferentDatasetsMode()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllBytes(Path.Combine(tempDir, "c.png"), [0x89, 0x50, 0x4E, 0x47]);

            var mockDialog = new Mock<IDialogService>();
            mockDialog.Setup(d => d.ShowOpenFolderDialogAsync(It.IsAny<string>()))
                      .ReturnsAsync(tempDir);

            var viewModel = BuildViewModel(mockDialog.Object);
            viewModel.IsSingleDatasetMode.Should().BeTrue();

            // Act
            await viewModel.LoadRightFolderCommand.ExecuteAsync(null);

            // Assert
            viewModel.IsSingleDatasetMode.Should().BeFalse();
            viewModel.SelectedRightDataset.Should().NotBeNull();
            viewModel.SelectedRightDataset!.IsTemporary.Should().BeTrue();
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task LoadLeftFolderCommand_WhenDialogCancelled_DoesNothing()
    {
        // Arrange
        var mockDialog = new Mock<IDialogService>();
        mockDialog.Setup(d => d.ShowOpenFolderDialogAsync(It.IsAny<string>()))
                  .ReturnsAsync((string?)null);

        var viewModel = BuildViewModel(mockDialog.Object);
        var originalDataset = viewModel.SelectedLeftDataset;

        // Act
        await viewModel.LoadLeftFolderCommand.ExecuteAsync(null);

        // Assert
        viewModel.SelectedLeftDataset.Should().Be(originalDataset);
    }

    [Fact]
    public async Task LoadLeftFolderCommand_WhenFolderEmpty_DoesNothing()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        try
        {
            var mockDialog = new Mock<IDialogService>();
            mockDialog.Setup(d => d.ShowOpenFolderDialogAsync(It.IsAny<string>()))
                      .ReturnsAsync(tempDir);

            var viewModel = BuildViewModel(mockDialog.Object);
            var originalDataset = viewModel.SelectedLeftDataset;

            // Act
            await viewModel.LoadLeftFolderCommand.ExecuteAsync(null);

            // Assert
            viewModel.SelectedLeftDataset.Should().Be(originalDataset);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void GetFolderImages_WhenNoFolderLoaded_ReturnsNull()
    {
        var viewModel = BuildViewModel();
        var result = viewModel.GetFolderImages(null);
        result.Should().BeNull();
    }

    private static ImageCompareViewModel BuildViewModel(IDialogService? dialogService = null)
    {
        var mockState = new Mock<IDatasetState>();
        mockState.Setup(s => s.Datasets).Returns(new ObservableCollection<DatasetCardViewModel>());

        return new ImageCompareViewModel(mockState.Object, dialogService: dialogService);
    }
}
