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

    [Fact]
    public void FlyoutListContainsUnknownFirstThenAllSharedItems()
    {
        var vm = CreateViewModel();

        vm.FlyoutBaseModels.First().Should().BeSameAs(vm.UnknownBaseModelItem);
        vm.FlyoutBaseModels.Skip(1).Should().Equal(vm.AvailableBaseModels);
    }

    [Fact]
    public void FlyoutSearchNarrowsTheListCaseInsensitively()
    {
        var vm = CreateViewModel();

        vm.BaseModelFilterSearchText = "sdxl";

        vm.FlyoutBaseModels.Should().NotBeEmpty();
        vm.FlyoutBaseModels.Should().OnlyContain(i =>
            i.BaseModelRaw.Contains("SDXL", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FlyoutSearchMatchesTheUnknownEntryByLabel()
    {
        var vm = CreateViewModel();

        vm.BaseModelFilterSearchText = "unk";

        vm.FlyoutBaseModels.Should().ContainSingle()
            .Which.Should().BeSameAs(vm.UnknownBaseModelItem);
    }

    [Fact]
    public void ClearingTheFlyoutSearchRestoresTheFullList()
    {
        var vm = CreateViewModel();
        var fullCount = vm.FlyoutBaseModels.Count;

        vm.BaseModelFilterSearchText = "sdxl";
        vm.BaseModelFilterSearchText = null;

        vm.FlyoutBaseModels.Count.Should().Be(fullCount);
    }

    [Fact]
    public void OnlyInstalledNarrowsToBaseModelsPresentInTheLibrary()
    {
        var vm = CreateViewModel();

        vm.OnlyInstalledBaseModels = true;

        // Demo data installs SDXL 1.0 (among others) but the shared list may hold more.
        vm.FlyoutBaseModels.Should().Contain(i => i.BaseModelRaw == "SDXL 1.0");
        vm.FlyoutBaseModels.Where(i => i != vm.UnknownBaseModelItem)
            .Should().OnlyContain(i => vm.AllTiles.Any(t =>
                t.Versions.Any(v => string.Equals(v.BaseModelRaw, i.BaseModelRaw,
                    StringComparison.OrdinalIgnoreCase))));
    }

    [Fact]
    public void OnlyInstalledKeepsUnknownWhenPlaceholderTilesExist()
    {
        var vm = CreateViewModel();

        vm.OnlyInstalledBaseModels = true;

        vm.FlyoutBaseModels.Should().Contain(vm.UnknownBaseModelItem,
            "demo data contains a '???' tile, so Unknown is an installed option");
    }

    [Fact]
    public void SelectionSurvivesFlyoutNarrowing()
    {
        var vm = CreateViewModel();
        var sdxl = vm.AvailableBaseModels.First(i => i.BaseModelRaw == "SDXL 1.0");
        sdxl.IsSelected = true;

        vm.BaseModelFilterSearchText = "pony";
        vm.BaseModelFilterSearchText = null;

        sdxl.IsSelected.Should().BeTrue("narrowing the visible list must not touch selections");
        vm.ActiveBaseModelFilterCount.Should().Be(1);
    }
}
