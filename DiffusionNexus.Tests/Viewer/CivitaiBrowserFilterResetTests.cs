using System.Collections.ObjectModel;
using DiffusionNexus.Civitai;
using DiffusionNexus.Civitai.Models;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.UI.Models;
using DiffusionNexus.UI.Services.CivitaiBrowser;
using DiffusionNexus.UI.ViewModels;
using DiffusionNexus.UI.ViewModels.CivitaiBrowser;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.Viewer;

/// <summary>
/// Covers the Browse Civitai tab's filter-bar Reset button: one click must return search text,
/// the four Show toggles, the base-model selection (including any
/// <c>_stickyBaseModelSelections</c>-parked name — the same "not just the live mirror" fix
/// <c>ClearBaseModelFilters</c> got, see <see cref="CivitaiBrowserFilterPersistenceTests.ClearBaseModelFilters_AlsoClearsStickySelections"/>),
/// and Sort/Period/Model type all the way back to the constructor's defaults, fire exactly one
/// search (not a burst — several of the reset properties each independently debounce or
/// immediately fire their own search when written one at a time), leave the saved filter
/// untouched, and drive <see cref="CivitaiBrowserViewModel.CanReset"/> so the button greys out
/// once the bar is already at rest.
/// </summary>
public sealed class CivitaiBrowserFilterResetTests
{
    private static (CivitaiBrowserViewModel Vm, ObservableCollection<BaseModelFilterItem> Source) CreateVm(
        ICivitaiClient? civitaiClient = null,
        IAppSettingsService? settingsService = null,
        ObservableCollection<BaseModelFilterItem>? source = null)
    {
        // Persist paths redirected into a temp dir so tests never read or clobber the real
        // LocalAppData queue/waitlist snapshots (see CivitaiBrowserClearQueueTests).
        var tempDir = Directory.CreateTempSubdirectory("dn-browser-reset-tests").FullName;
        var queue = new CivitaiDownloadQueue(null, null, null, null,
            persistPathOverride: Path.Combine(tempDir, "queue.json"));
        var waitlist = new CivitaiWaitlist(null, null,
            persistPathOverride: Path.Combine(tempDir, "waitlist.json"));
        var effectiveSource = source ?? new ObservableCollection<BaseModelFilterItem>
        {
            new("SDXL 1.0"),
            new("Pony"),
            new("Illustrious"),
        };
        var vm = new CivitaiBrowserViewModel(civitaiClient, settingsService, null, queue, waitlist, effectiveSource);
        return (vm, effectiveSource);
    }

    private static Mock<ICivitaiClient> CreateClientMock()
    {
        var client = new Mock<ICivitaiClient>();
        client.Setup(c => c.GetModelsAsync(
                It.IsAny<CivitaiModelsQuery>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CivitaiPagedResponse<CivitaiModel>());
        return client;
    }

    /// <summary>
    /// Dirties every part of the filter bar Reset is responsible for restoring, including
    /// parking a base-model name in the sticky set the way a saved filter would (a name the
    /// live mirror doesn't currently contain — <see cref="CivitaiBrowserViewModel.ApplySavedFilter"/>'s
    /// mechanism).
    /// </summary>
    private static void DirtyEverything(CivitaiBrowserViewModel vm)
    {
        vm.SearchText = "latex";

        // Park a sticky base-model selection the way a saved filter would (a name absent from
        // the live mirror) BEFORE the live dirtying below: ApplySavedFilter overwrites the four
        // Show flags (defaults to ticked when the saved data omits them, same as a filter saved
        // before they existed) and every live item's IsSelected, so doing it first keeps the
        // live dirtying that follows from being clobbered by it.
        vm.ApplySavedFilter(new CivitaiBrowserFilterData { SelectedBaseModels = ["Krea 2"] });

        vm.ShowInstalled = false;
        vm.ShowEarlyAccess = false;
        vm.ShowPaywalled = false;
        vm.ShowNsfw = false;
        vm.AvailableBaseModels.Single(i => i.BaseModelRaw == "Pony").IsSelected = true;
        vm.SelectedSort = CivitaiModelSort.HighestRated;
        vm.SelectedPeriod = CivitaiPeriod.Month;
        vm.SelectedModelType = vm.ModelTypeOptions.Single(o => o.Label == "All models");
    }

    [Fact]
    public void ResetFilter_ReturnsEveryPropertyToItsDefault_IncludingParkedStickySelections()
    {
        var (vm, _) = CreateVm();
        DirtyEverything(vm);
        // Sanity: the sticky "Krea 2" name is actually parked before Reset runs.
        vm.CaptureFilter().SelectedBaseModels.Should().Contain("Krea 2");

        vm.ResetFilterCommand.Execute(null);

        vm.SearchText.Should().BeEmpty();
        vm.ShowInstalled.Should().BeTrue();
        vm.ShowEarlyAccess.Should().BeTrue();
        vm.ShowPaywalled.Should().BeTrue();
        vm.ShowNsfw.Should().BeTrue();
        vm.AvailableBaseModels.Should().OnlyContain(i => !i.IsSelected);
        vm.IsBaseModelFilterActive.Should().BeFalse();
        vm.CaptureFilter().SelectedBaseModels.Should().BeEmpty(
            "the parked sticky selection must be cleared too, or it silently re-filters once the " +
            "name materializes in the mirror");
        vm.SelectedSort.Should().Be(CivitaiModelSort.Newest);
        vm.SelectedPeriod.Should().Be(CivitaiPeriod.AllTime);
        vm.SelectedModelType.Should().BeSameAs(vm.ModelTypeOptions.Single(o => o.Label == "All LoRA types"));
    }

    [Fact]
    public void ResetFilter_FiresExactlyOneSearch_NotOnePerRestoredProperty()
    {
        var client = CreateClientMock();
        var (vm, _) = CreateVm(client.Object);
        DirtyEverything(vm);
        client.Invocations.Clear(); // drop whatever DirtyEverything's own changes triggered

        vm.ResetFilterCommand.Execute(null);

        client.Verify(c => c.GetModelsAsync(
                It.IsAny<CivitaiModelsQuery>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "resetting search text, four show flags, a base-model selection, sort, period and " +
            "model type together must fire one search, not a burst — one per property that " +
            "independently searches on its own change hook");
    }

    /// <summary>
    /// Regression guard for the trailing-debounce trap: dirtying <see cref="CivitaiBrowserViewModel.SearchText"/>
    /// starts a 400ms debounce timer. If Reset merely suppresses ITS OWN write of
    /// <c>SearchText = ""</c> without cancelling that pre-existing timer, the timer fires its own
    /// <c>SearchAsync</c> later — a second, delayed search on top of Reset's own explicit one.
    /// </summary>
    [Fact]
    public async Task ResetFilter_CancelsAPendingDebounce_SoNoSearchArrivesLater()
    {
        var client = CreateClientMock();
        var (vm, _) = CreateVm(client.Object);

        vm.SearchText = "started just before reset"; // schedules DebouncedSearchAsync (400ms)
        vm.ResetFilterCommand.Execute(null); // fires the one immediate search synchronously

        client.Invocations.Clear(); // only care about what arrives AFTER this point

        // Long enough for the original 400ms debounce to have fired if it survived Reset.
        await Task.Delay(600);

        client.Verify(c => c.GetModelsAsync(
                It.IsAny<CivitaiModelsQuery>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "the pre-Reset debounce timer must be cancelled by Reset, or it fires a stray second " +
            "search well after the one Reset already issued");
    }

    [Fact]
    public void ResetFilter_DoesNotWriteToSettings()
    {
        var settings = new Mock<IAppSettingsService>();
        var (vm, _) = CreateVm(settingsService: settings.Object);
        DirtyEverything(vm);

        vm.ResetFilterCommand.Execute(null);

        settings.Verify(s => s.SetCivitaiBrowserFilterJsonAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "Reset must touch only the live view — the persisted filter stays until the user " +
            "explicitly hits Save again");
    }

    [Fact]
    public void CanReset_IsFalseAtDefaults()
    {
        var (vm, _) = CreateVm();

        vm.CanReset.Should().BeFalse();
    }

    [Fact]
    public void CanReset_IsTrueAfterChangingSearchText()
    {
        var (vm, _) = CreateVm();

        vm.SearchText = "latex";

        vm.CanReset.Should().BeTrue();
    }

    [Theory]
    [InlineData(nameof(CivitaiBrowserViewModel.ShowInstalled))]
    [InlineData(nameof(CivitaiBrowserViewModel.ShowEarlyAccess))]
    [InlineData(nameof(CivitaiBrowserViewModel.ShowPaywalled))]
    [InlineData(nameof(CivitaiBrowserViewModel.ShowNsfw))]
    public void CanReset_IsTrueAfterUntickingAnyShowFlag(string propertyName)
    {
        var (vm, _) = CreateVm();

        switch (propertyName)
        {
            case nameof(CivitaiBrowserViewModel.ShowInstalled): vm.ShowInstalled = false; break;
            case nameof(CivitaiBrowserViewModel.ShowEarlyAccess): vm.ShowEarlyAccess = false; break;
            case nameof(CivitaiBrowserViewModel.ShowPaywalled): vm.ShowPaywalled = false; break;
            case nameof(CivitaiBrowserViewModel.ShowNsfw): vm.ShowNsfw = false; break;
        }

        vm.CanReset.Should().BeTrue();
    }

    [Fact]
    public void CanReset_IsTrueAfterSelectingABaseModel()
    {
        var (vm, _) = CreateVm();

        vm.AvailableBaseModels.Single(i => i.BaseModelRaw == "Pony").IsSelected = true;

        vm.CanReset.Should().BeTrue();
    }

    /// <summary>
    /// A base-model selection can be true even with nothing selected in the live mirror — a
    /// parked sticky name from a saved filter the mirror doesn't list yet. Reset must clear
    /// that too (see <see cref="ResetFilter_ReturnsEveryPropertyToItsDefault_IncludingParkedStickySelections"/>),
    /// and this pins that the badge property agrees it isn't "at rest" while one is parked.
    /// </summary>
    [Fact]
    public void CanReset_IsTrueWithOnlyAParkedStickySelection_NoLiveSelectionAtAll()
    {
        var (vm, _) = CreateVm();

        vm.ApplySavedFilter(new CivitaiBrowserFilterData { SelectedBaseModels = ["Krea 2"] });

        vm.IsBaseModelFilterActive.Should().BeFalse("no live mirror item is selected");
        vm.CanReset.Should().BeTrue("a parked sticky selection still means the bar isn't at rest");
    }

    [Fact]
    public void CanReset_IsTrueAfterChangingSort()
    {
        var (vm, _) = CreateVm();

        vm.SelectedSort = CivitaiModelSort.HighestRated;

        vm.CanReset.Should().BeTrue();
    }

    [Fact]
    public void CanReset_IsTrueAfterChangingPeriod()
    {
        var (vm, _) = CreateVm();

        vm.SelectedPeriod = CivitaiPeriod.Month;

        vm.CanReset.Should().BeTrue();
    }

    [Fact]
    public void CanReset_IsTrueAfterChangingModelType()
    {
        var (vm, _) = CreateVm();

        vm.SelectedModelType = vm.ModelTypeOptions.Single(o => o.Label == "All models");

        vm.CanReset.Should().BeTrue();
    }

    [Fact]
    public void CanReset_GoesFalseAgain_AfterReset()
    {
        var (vm, _) = CreateVm();
        DirtyEverything(vm);
        vm.CanReset.Should().BeTrue();

        vm.ResetFilterCommand.Execute(null);

        vm.CanReset.Should().BeFalse();
    }
}
