using System.Collections.ObjectModel;
using DiffusionNexus.UI.Services.CivitaiBrowser;
using DiffusionNexus.UI.ViewModels;
using DiffusionNexus.UI.ViewModels.CivitaiBrowser;
using FluentAssertions;

namespace DiffusionNexus.Tests.Viewer;

/// <summary>
/// Covers the Browse Civitai tab's base-model flyout: the in-flyout search box, the
/// pinning of selected items while narrowed, and the exclusion of installed-only
/// labels (union-appended in the Installed tab) from the mirror — those labels are
/// not part of Civitai's catalog and must never reach the API query.
/// </summary>
public class CivitaiBrowserViewModelBaseModelFilterTests
{
    private static (CivitaiBrowserViewModel Vm, ObservableCollection<BaseModelFilterItem> Source) Create()
    {
        var source = new ObservableCollection<BaseModelFilterItem>
        {
            new("SDXL 1.0"),
            new("Pony"),
            new("Illustrious"),
            new("Krea 2") { IsInstalledOnly = true },
        };
        var vm = new CivitaiBrowserViewModel(null, null, null, new CivitaiDownloadQueue(null), source);
        return (vm, source);
    }

    [Fact]
    public void MirrorExcludesInstalledOnlyLabels()
    {
        var (vm, _) = Create();

        vm.AvailableBaseModels.Select(i => i.BaseModelRaw)
            .Should().BeEquivalentTo(
                ["SDXL 1.0", "Pony", "Illustrious"],
                "installed-only labels are not Civitai catalog values and must not reach the API");
    }

    [Fact]
    public void FlyoutListMatchesTheMirrorByDefault()
    {
        var (vm, _) = Create();

        vm.FlyoutBaseModels.Should().Equal(vm.AvailableBaseModels);
    }

    [Fact]
    public void FlyoutSearchNarrowsTheListCaseInsensitively()
    {
        var (vm, _) = Create();

        vm.BaseModelFilterSearchText = "pony";

        vm.FlyoutBaseModels.Should().ContainSingle()
            .Which.BaseModelRaw.Should().Be("Pony");
    }

    [Fact]
    public void SelectedItemsStayVisibleWhenNarrowedAway()
    {
        var (vm, _) = Create();
        var sdxl = vm.AvailableBaseModels.First(i => i.BaseModelRaw == "SDXL 1.0");
        sdxl.IsSelected = true;

        vm.BaseModelFilterSearchText = "pony";

        vm.FlyoutBaseModels.Should().Contain(sdxl,
            "a selected item must stay visible while narrowed, or it becomes un-toggleable");

        sdxl.IsSelected = false;

        vm.FlyoutBaseModels.Should().NotContain(sdxl);
    }

    [Fact]
    public void ClearingTheSearchRestoresTheFullMirror()
    {
        var (vm, _) = Create();

        vm.BaseModelFilterSearchText = "pony";
        vm.BaseModelFilterSearchText = null;

        vm.FlyoutBaseModels.Should().Equal(vm.AvailableBaseModels);
    }
}
