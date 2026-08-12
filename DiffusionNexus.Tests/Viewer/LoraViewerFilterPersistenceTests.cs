using System.Collections.ObjectModel;
using DiffusionNexus.UI.Models;
using DiffusionNexus.UI.Services.CivitaiBrowser;
using DiffusionNexus.UI.ViewModels;
using DiffusionNexus.UI.ViewModels.CivitaiBrowser;
using FluentAssertions;

namespace DiffusionNexus.Tests.Viewer;

/// <summary>
/// Covers base-model filter persistence in the LoRA viewer and its Browse Civitai
/// mirror (user-reported: "switching between tabs and/or refreshing deletes the
/// saved filter in the installed tab"). Selections used to be dropped permanently
/// whenever their name was momentarily absent from the rebuilt option list — the
/// viewer discarded not-in-source names instead of parking them as pending, and the
/// browser mirror snapshotted selections per rebuild while the shared source is
/// cleared-then-refilled (Reset + N Adds, one mirror rebuild each) during every
/// viewer-side rebuild. Also covers the saved filter carrying the Installed-tab
/// sort (field + direction).
/// </summary>
public sealed class LoraViewerFilterPersistenceTests
{
    private static BaseModelFilterItem ItemOf(LoraViewerViewModel vm, string raw)
        => vm.AvailableBaseModels.Single(i => i.BaseModelRaw == raw);

    [Fact]
    public void Selection_SurvivesSourceShrinkAndRegrow()
    {
        var vm = new LoraViewerViewModel();
        vm.ApplyCatalogBaseModels(["Alpha", "ZZ-Test"]);
        ItemOf(vm, "ZZ-Test").IsSelected = true;

        // Source transiently loses the entry (catalog swap, empty tile window, …).
        vm.ApplyCatalogBaseModels(["Alpha"]);
        vm.AvailableBaseModels.Should().NotContain(i => i.BaseModelRaw == "ZZ-Test");

        // When it rematerializes, the selection must come back.
        vm.ApplyCatalogBaseModels(["Alpha", "ZZ-Test"]);
        ItemOf(vm, "ZZ-Test").IsSelected.Should().BeTrue(
            "a selected name that drops out of the option list must stay pending, not vanish");
    }

    [Fact]
    public void Selection_MissingFromSource_IsStillPartOfCapturedFilter()
    {
        var vm = new LoraViewerViewModel();
        vm.ApplyCatalogBaseModels(["Alpha", "ZZ-Test"]);
        ItemOf(vm, "ZZ-Test").IsSelected = true;

        vm.ApplyCatalogBaseModels(["Alpha"]); // ZZ-Test currently not listed

        vm.CaptureFilter().SelectedBaseModels.Should().Contain("ZZ-Test",
            "saving the filter while an entry is temporarily unlisted must not truncate it");
    }

    [Fact]
    public void CaptureFilter_IncludesSort_AndRoundTrips()
    {
        var vm = new LoraViewerViewModel();
        vm.ApplyCatalogBaseModels(["Alpha", "Beta"]);
        ItemOf(vm, "Beta").IsSelected = true;
        vm.SelectedSortOption = vm.SortOptions.Single(o => o.Field == LoraSortField.DateAdded);
        vm.SortDescending = true;

        var data = vm.CaptureFilter();

        data.SortField.Should().Be(nameof(LoraSortField.DateAdded));
        data.SortDescending.Should().BeTrue();

        var restored = new LoraViewerViewModel();
        restored.ApplyCatalogBaseModels(["Alpha", "Beta"]);
        restored.ApplySavedFilter(data);

        restored.SelectedSortOption.Field.Should().Be(LoraSortField.DateAdded);
        restored.SortDescending.Should().BeTrue();
        ItemOf(restored, "Beta").IsSelected.Should().BeTrue();
    }

    [Fact]
    public void ApplySavedFilter_WithoutSortFields_LeavesCurrentSortUntouched()
    {
        // Filters saved before sort persistence existed carry no sort fields.
        var vm = new LoraViewerViewModel();
        vm.ApplyCatalogBaseModels(["Alpha"]);
        vm.SelectedSortOption = vm.SortOptions.Single(o => o.Field == LoraSortField.DateAdded);
        vm.SortDescending = true;

        vm.ApplySavedFilter(new LoraViewerFilterData { SelectedBaseModels = ["Alpha"] });

        vm.SelectedSortOption.Field.Should().Be(LoraSortField.DateAdded);
        vm.SortDescending.Should().BeTrue();
    }

    [Fact]
    public void BrowserMirror_SelectionSurvivesSourceClearAndRefill()
    {
        var source = new ObservableCollection<BaseModelFilterItem>
        {
            new("Krea 2"),
            new("SDXL 1.0"),
        };
        var queuePersist = Path.Combine(Path.GetTempPath(), $"dn-queue-{Guid.NewGuid():N}.json");
        var browser = new CivitaiBrowserViewModel(
            civitaiClient: null,
            settingsService: null,
            logger: null,
            queue: new CivitaiDownloadQueue(null, null, null, null, persistPathOverride: queuePersist),
            sharedBaseModelSource: source);

        browser.AvailableBaseModels.Single(i => i.BaseModelRaw == "Krea 2").IsSelected = true;

        // The owning viewer rebuilds its list: Clear() + re-Add — the mirror rebuilds
        // on every change notification, including the empty intermediate state.
        source.Clear();
        source.Add(new BaseModelFilterItem("Krea 2"));
        source.Add(new BaseModelFilterItem("SDXL 1.0"));

        browser.AvailableBaseModels.Single(i => i.BaseModelRaw == "Krea 2").IsSelected
            .Should().BeTrue("the browser's own selections must survive the source's clear-and-refill churn");
        browser.AvailableBaseModels.Single(i => i.BaseModelRaw == "SDXL 1.0").IsSelected
            .Should().BeFalse();
    }
}
