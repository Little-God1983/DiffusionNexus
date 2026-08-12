using System.Collections.ObjectModel;
using DiffusionNexus.UI.Services.CivitaiBrowser;
using DiffusionNexus.UI.ViewModels;
using DiffusionNexus.UI.ViewModels.CivitaiBrowser;
using FluentAssertions;

namespace DiffusionNexus.Tests.Viewer;

/// <summary>
/// Covers the Browse Civitai tab's base-model flyout: the in-flyout search box, the
/// pinning of selected items while narrowed, and the single-source-of-truth mirror —
/// the browser renders exactly the labels the Installed tab has, including
/// installed-only ones like "Krea 2" (verified live: Civitai's API accepts any
/// baseModels value, returning 200 with zero items for unknown labels).
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
            new("Krea 2"),
        };
        var vm = new CivitaiBrowserViewModel(null, null, null, new CivitaiDownloadQueue(null), source);
        return (vm, source);
    }

    [Fact]
    public void MirrorContainsExactlyTheSharedList()
    {
        var (vm, source) = Create();

        vm.AvailableBaseModels.Select(i => i.BaseModelRaw)
            .Should().Equal(source.Select(i => i.BaseModelRaw),
                "both tabs must show one source of truth — same labels, same order");
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
