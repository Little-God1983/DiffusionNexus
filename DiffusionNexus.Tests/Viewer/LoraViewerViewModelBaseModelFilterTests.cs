using DiffusionNexus.UI.ViewModels;
using FluentAssertions;

namespace DiffusionNexus.Tests.Viewer;

/// <summary>
/// Covers the Installed-tab base-model filter: the "Unknown" sentinel that matches
/// tiles whose base model is the "???" placeholder, and its interaction with the
/// active-filter indicator and Clear/Reset. Uses the design-time constructor's demo
/// data, which includes one placeholder-base-model tile ("Legacy Style").
/// </summary>
public class LoraViewerViewModelBaseModelFilterTests
{
    private static LoraViewerViewModel CreateViewModel() => new()
    {
        SearchDebounceInterval = TimeSpan.FromMilliseconds(50),
    };

    [Fact]
    public void UnknownSentinelIsNotPartOfTheSharedBaseModelList()
    {
        var vm = CreateViewModel();

        vm.AvailableBaseModels.Should().NotContain(vm.UnknownBaseModelItem,
            "the sentinel must never leak into the collection the Civitai browser mirrors");
        vm.AvailableBaseModels.Should().NotContain(i => i.BaseModelRaw == "???",
            "placeholder values must not appear as a raw filter entry");
    }

    [Fact]
    public void SelectingUnknownShowsOnlyPlaceholderTiles()
    {
        var vm = CreateViewModel();

        vm.UnknownBaseModelItem.IsSelected = true;

        vm.FilteredTiles.Should().NotBeEmpty("demo data contains a '???' tile");
        vm.FilteredTiles.Should().OnlyContain(t => t.DisplayName == "Legacy Style");
    }

    [Fact]
    public void UnknownCombinesWithRegularSelectionsAsUnion()
    {
        var vm = CreateViewModel();
        var sdxl = vm.AvailableBaseModels.First(i => i.BaseModelRaw == "SDXL 1.0");

        vm.UnknownBaseModelItem.IsSelected = true;
        sdxl.IsSelected = true;

        vm.FilteredTiles.Should().Contain(t => t.DisplayName == "Legacy Style");
        vm.FilteredTiles.Should().Contain(t => t.DisplayName == "Realistic Portrait");
    }

    [Fact]
    public void UnknownCountsTowardTheActiveFilterIndicator()
    {
        var vm = CreateViewModel();

        vm.IsBaseModelFilterActive.Should().BeFalse();

        vm.UnknownBaseModelItem.IsSelected = true;

        vm.IsBaseModelFilterActive.Should().BeTrue();
        vm.ActiveBaseModelFilterCount.Should().Be(1);
    }

    [Fact]
    public void ClearBaseModelFiltersAlsoClearsUnknown()
    {
        var vm = CreateViewModel();
        var allCount = vm.FilteredTiles.Count;
        vm.UnknownBaseModelItem.IsSelected = true;

        vm.ClearBaseModelFiltersCommand.Execute(null);

        vm.UnknownBaseModelItem.IsSelected.Should().BeFalse();
        vm.IsBaseModelFilterActive.Should().BeFalse();
        vm.FilteredTiles.Count.Should().Be(allCount);
    }

    [Fact]
    public void ResetFiltersAlsoClearsUnknown()
    {
        var vm = CreateViewModel();
        vm.UnknownBaseModelItem.IsSelected = true;

        vm.ResetFiltersCommand.Execute(null);

        vm.UnknownBaseModelItem.IsSelected.Should().BeFalse();
        vm.IsBaseModelFilterActive.Should().BeFalse();
    }
}
