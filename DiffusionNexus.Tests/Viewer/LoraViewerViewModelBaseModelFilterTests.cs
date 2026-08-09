using DiffusionNexus.UI.Models;
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

    [Fact]
    public void CaptureFilterSerializesSelectionUnknownAndOnlyInstalled()
    {
        var vm = CreateViewModel();
        vm.AvailableBaseModels.First(i => i.BaseModelRaw == "SDXL 1.0").IsSelected = true;
        vm.AvailableBaseModels.First(i => i.BaseModelRaw == "Pony").IsSelected = true;
        vm.UnknownBaseModelItem.IsSelected = true;
        vm.OnlyInstalledBaseModels = true;

        var data = vm.CaptureFilter();

        data.SelectedBaseModels.Should().BeEquivalentTo("SDXL 1.0", "Pony");
        data.IncludeUnknown.Should().BeTrue();
        data.OnlyInstalled.Should().BeTrue();
    }

    [Fact]
    public void ApplySavedFilterRestoresTheCapturedState()
    {
        var vm = CreateViewModel();
        var data = new LoraViewerFilterData
        {
            SelectedBaseModels = ["SDXL 1.0", "Pony"],
            IncludeUnknown = true,
            OnlyInstalled = true,
        };

        vm.ApplySavedFilter(data);

        vm.AvailableBaseModels.Where(i => i.IsSelected).Select(i => i.BaseModelRaw)
            .Should().BeEquivalentTo("SDXL 1.0", "Pony");
        vm.UnknownBaseModelItem.IsSelected.Should().BeTrue();
        vm.OnlyInstalledBaseModels.Should().BeTrue();
        vm.IsBaseModelFilterActive.Should().BeTrue();
    }

    [Fact]
    public void ApplySavedFilterIgnoresNamesNotInTheCurrentList()
    {
        var vm = CreateViewModel();
        var data = new LoraViewerFilterData
        {
            SelectedBaseModels = ["SDXL 1.0", "No Such Base Model 9000"],
        };

        vm.ApplySavedFilter(data);

        vm.ActiveBaseModelFilterCount.Should().Be(1, "unknown saved names are ignored silently");
    }

    [Fact]
    public void CaptureThenApplyRoundTripsThroughJson()
    {
        var vm = CreateViewModel();
        vm.AvailableBaseModels.First(i => i.BaseModelRaw == "Illustrious").IsSelected = true;
        vm.UnknownBaseModelItem.IsSelected = true;

        var json = System.Text.Json.JsonSerializer.Serialize(vm.CaptureFilter());

        var vm2 = CreateViewModel();
        vm2.ApplySavedFilter(
            System.Text.Json.JsonSerializer.Deserialize<LoraViewerFilterData>(json)!);

        vm2.AvailableBaseModels.Where(i => i.IsSelected).Select(i => i.BaseModelRaw)
            .Should().BeEquivalentTo("Illustrious");
        vm2.UnknownBaseModelItem.IsSelected.Should().BeTrue();
    }

    [Fact]
    public void SelectedItemsStayVisibleWhenNarrowedAway()
    {
        var vm = CreateViewModel();
        var sdxl = vm.AvailableBaseModels.First(i => i.BaseModelRaw == "SDXL 1.0");
        sdxl.IsSelected = true;

        vm.BaseModelFilterSearchText = "pony";

        vm.FlyoutBaseModels.Should().Contain(sdxl,
            "a selected item must stay visible while narrowed, or it becomes un-toggleable");

        sdxl.IsSelected = false;

        vm.FlyoutBaseModels.Should().NotContain(sdxl,
            "once deselected, the narrowing applies to it normally again");
    }

    [Fact]
    public void ApplySavedFilterReplacesTheExistingSelection()
    {
        var vm = CreateViewModel();
        vm.AvailableBaseModels.First(i => i.BaseModelRaw == "Illustrious").IsSelected = true;
        vm.UnknownBaseModelItem.IsSelected = true;

        vm.ApplySavedFilter(new LoraViewerFilterData { SelectedBaseModels = ["SDXL 1.0"] });

        vm.AvailableBaseModels.Where(i => i.IsSelected).Select(i => i.BaseModelRaw)
            .Should().BeEquivalentTo("SDXL 1.0");
        vm.UnknownBaseModelItem.IsSelected.Should().BeFalse(
            "the saved filter is applied as-is, not merged into the current selection");
    }

    [Fact]
    public void ApplySavedFilterToleratesANullSelectionList()
    {
        var vm = CreateViewModel();

        var act = () => vm.ApplySavedFilter(new LoraViewerFilterData
        {
            SelectedBaseModels = null!,
            IncludeUnknown = true,
        });

        act.Should().NotThrow("a hand-edited or legacy JSON blob must degrade, not crash the restore");
        vm.UnknownBaseModelItem.IsSelected.Should().BeTrue();
    }

    [Fact]
    public void ApplySavedFilterRunsASingleFilterPass()
    {
        var vm = CreateViewModel();
        var resets = 0;
        vm.FilteredTiles.CollectionChanged += (_, e) =>
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset) resets++;
        };

        vm.ApplySavedFilter(new LoraViewerFilterData
        {
            SelectedBaseModels = ["SDXL 1.0", "Pony", "Illustrious"],
            IncludeUnknown = true,
        });

        resets.Should().Be(1, "restoring N selections must not run N full filter passes");
    }

    [Fact]
    public void UnmatchedSavedNamesAreKeptAndNeverTruncatedOnResave()
    {
        var vm = CreateViewModel();

        vm.ApplySavedFilter(new LoraViewerFilterData
        {
            SelectedBaseModels = ["SDXL 1.0", "Some Future Base Model"],
        });

        vm.PendingRestoredBaseModels.Should().BeEquivalentTo("Some Future Base Model");
        vm.CaptureFilter().SelectedBaseModels
            .Should().BeEquivalentTo("SDXL 1.0", "Some Future Base Model");
    }

    [Fact]
    public void ClearAllDropsPendingSavedNames()
    {
        var vm = CreateViewModel();
        vm.ApplySavedFilter(new LoraViewerFilterData
        {
            SelectedBaseModels = ["Some Future Base Model"],
        });

        vm.ClearBaseModelFiltersCommand.Execute(null);

        vm.PendingRestoredBaseModels.Should().BeNull(
            "an explicit clear voids the not-yet-materialized saved intent too");
        vm.CaptureFilter().SelectedBaseModels.Should().BeEmpty();
    }
}
