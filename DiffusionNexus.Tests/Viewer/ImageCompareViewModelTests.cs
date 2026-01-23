using DiffusionNexus.UI.ViewModels;
using FluentAssertions;

namespace DiffusionNexus.Tests.Viewer;

public class ImageCompareViewModelTests
{
    [Fact]
    public void AssignImageCommand_AssignsImageToSelectedSide()
    {
        var viewModel = BuildViewModel();
        var targetItem = viewModel.FilmstripItems.Last();

        viewModel.AssignSide = CompareAssignSide.After;
        viewModel.AssignImageCommand.Execute(targetItem);

        viewModel.SelectedAfterImage.Should().Be(targetItem);
    }

    [Fact]
    public void SwapImagesCommand_SwapsBeforeAndAfter()
    {
        var viewModel = BuildViewModel();
        var initialBefore = viewModel.SelectedBeforeImage;
        var initialAfter = viewModel.SelectedAfterImage;

        viewModel.SwapCommand.Execute(null);

        viewModel.SelectedBeforeImage.Should().Be(initialAfter);
        viewModel.SelectedAfterImage.Should().Be(initialBefore);
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
    public void SwitchingDataset_RefreshesFilmstripForAssignSide()
    {
        var viewModel = BuildViewModel();

        viewModel.AssignSide = CompareAssignSide.Before;
        viewModel.SelectedBeforeDataset = "Dataset B";

        viewModel.FilmstripItems.Should().OnlyContain(item => item.DisplayName.StartsWith("B"));
    }

    private static ImageCompareViewModel BuildViewModel()
    {
        var datasets = new Dictionary<string, IEnumerable<ImageCompareItem>>
        {
            {
                "Dataset A",
                new List<ImageCompareItem>
                {
                    new("a1.png", "A-1"),
                    new("a2.png", "A-2")
                }
            },
            {
                "Dataset B",
                new List<ImageCompareItem>
                {
                    new("b1.png", "B-1"),
                    new("b2.png", "B-2")
                }
            }
        };

        return new ImageCompareViewModel(datasets);
    }
}
