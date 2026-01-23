using DiffusionNexus.UI.Controls;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;

namespace DiffusionNexus.Tests.Viewer;

public class ImageCompareViewModelTests
{
    [Fact]
    public void ResetSlider_SetsSliderBackToHalf()
    {
        var viewModel = CreateViewModel();
        viewModel.SliderValue = 0.9;

        viewModel.ResetSliderCommand.Execute(null);

        viewModel.SliderValue.Should().Be(0.5);
    }

    [Fact]
    public void AssignFilmstripItem_AssignsBeforeSelection()
    {
        var viewModel = CreateViewModel();
        var item = viewModel.BeforeDataset!.Items.Last();

        viewModel.AssignTarget = ImageCompareAssignTarget.Before;
        viewModel.AssignFilmstripItemCommand.Execute(item);

        viewModel.BeforeImagePath.Should().Be(item.ImagePath);
        viewModel.SelectedBeforeItem.Should().Be(item);
        item.IsBeforeSelected.Should().BeTrue();
    }

    [Fact]
    public void AssignTargetSwitch_UsesAfterDatasetFilmstrip()
    {
        var viewModel = CreateViewModel();

        viewModel.AssignTarget = ImageCompareAssignTarget.After;

        viewModel.FilmstripItems.Should().BeEquivalentTo(viewModel.AfterDataset!.Items);
    }

    [Fact]
    public void SwapImages_SwapsDatasetsAndSelections()
    {
        var viewModel = CreateViewModel();
        var originalBefore = viewModel.SelectedBeforeItem;
        var originalAfter = viewModel.SelectedAfterItem;
        var originalBeforeDataset = viewModel.BeforeDataset;
        var originalAfterDataset = viewModel.AfterDataset;

        viewModel.SwapImagesCommand.Execute(null);

        viewModel.SelectedBeforeItem.Should().Be(originalAfter);
        viewModel.SelectedAfterItem.Should().Be(originalBefore);
        viewModel.BeforeDataset.Should().Be(originalAfterDataset);
        viewModel.AfterDataset.Should().Be(originalBeforeDataset);
    }

    private static ImageCompareViewModel CreateViewModel()
    {
        var beforeDataset = new ImageCompareDataset("Before",
            new ImageCompareItem("Before 1", "/tmp/before-1.png"),
            new ImageCompareItem("Before 2", "/tmp/before-2.png"));

        var afterDataset = new ImageCompareDataset("After",
            new ImageCompareItem("After 1", "/tmp/after-1.png"),
            new ImageCompareItem("After 2", "/tmp/after-2.png"));

        return new ImageCompareViewModel(new[] { beforeDataset, afterDataset });
    }
}
