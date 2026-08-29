using System.Collections.ObjectModel;
using Avalonia.Threading;
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
/// Covers the Browse Civitai tab's saveable filter: <see cref="CivitaiBrowserViewModel.CaptureFilter"/> /
/// <see cref="CivitaiBrowserViewModel.ApplySavedFilter"/> round-trip the four Show flags and the
/// base-model selection, forward-compat (a saved filter predating the Show flags restores them
/// ticked), the base-model "not yet in the catalog" sticky-selection mechanism (mirrors
/// <c>LoraViewerFilterPersistenceTests.BrowserMirror_SelectionSurvivesSourceClearAndRefill</c>), and
/// the full Save-button-to-AppSettings-to-restore round trip via <see cref="CivitaiBrowserViewModel.EnsureLoadedAsync"/>.
/// </summary>
public sealed class CivitaiBrowserFilterPersistenceTests
{
    private static CivitaiBrowserViewModel CreateVm(
        ObservableCollection<BaseModelFilterItem>? source = null,
        IAppSettingsService? settingsService = null,
        ICivitaiClient? civitaiClient = null)
    {
        // Persist paths redirected into a temp dir so tests never read or clobber the real
        // LocalAppData queue/waitlist snapshots (see CivitaiBrowserClearQueueTests).
        var tempDir = Directory.CreateTempSubdirectory("dn-browser-filter-persist-tests").FullName;
        var queue = new CivitaiDownloadQueue(null, null, null, null,
            persistPathOverride: Path.Combine(tempDir, "queue.json"));
        var waitlist = new CivitaiWaitlist(null, null,
            persistPathOverride: Path.Combine(tempDir, "waitlist.json"));
        return new CivitaiBrowserViewModel(civitaiClient, settingsService, null, queue, waitlist, source);
    }

    [Fact]
    public void ApplySavedFilter_NullShowFlags_RestoreAsTicked()
    {
        // A filter saved before the Show flags existed (or any JSON simply omitting them)
        // must not silently hide anything — see CivitaiBrowserFilterData.ShowInstalled's doc.
        var vm = CreateVm();

        vm.ApplySavedFilter(new CivitaiBrowserFilterData { SelectedBaseModels = [] });

        vm.ShowInstalled.Should().BeTrue();
        vm.ShowEarlyAccess.Should().BeTrue();
        vm.ShowPaywalled.Should().BeTrue();
        vm.ShowNsfw.Should().BeTrue();
    }

    [Fact]
    public void CaptureFilter_RoundTripsShowFlagsAndBaseModelSelection()
    {
        var source = new ObservableCollection<BaseModelFilterItem> { new("SDXL 1.0"), new("Pony") };
        var vm = CreateVm(source);
        vm.AvailableBaseModels.Single(i => i.BaseModelRaw == "Pony").IsSelected = true;
        vm.ShowInstalled = false;
        vm.ShowPaywalled = false;

        var data = vm.CaptureFilter();

        data.SelectedBaseModels.Should().BeEquivalentTo(["Pony"]);
        data.ShowInstalled.Should().BeFalse();
        data.ShowEarlyAccess.Should().BeTrue();
        data.ShowPaywalled.Should().BeFalse();
        data.ShowNsfw.Should().BeTrue();

        var restored = CreateVm(new ObservableCollection<BaseModelFilterItem> { new("SDXL 1.0"), new("Pony") });
        restored.ApplySavedFilter(data);

        restored.ShowInstalled.Should().BeFalse();
        restored.ShowEarlyAccess.Should().BeTrue();
        restored.ShowPaywalled.Should().BeFalse();
        restored.ShowNsfw.Should().BeTrue();
        restored.AvailableBaseModels.Single(i => i.BaseModelRaw == "Pony").IsSelected.Should().BeTrue();
        restored.AvailableBaseModels.Single(i => i.BaseModelRaw == "SDXL 1.0").IsSelected.Should().BeFalse();
    }

    /// <summary>
    /// Regression guard: without suppressing <c>OnBaseModelFilterToggled</c> during the restore
    /// loop, each restored selection fires it — and that handler unconditionally re-searches — so
    /// a multi-base-model saved filter would fire one Civitai request per name instead of letting
    /// <see cref="CivitaiBrowserViewModel.EnsureLoadedAsync"/>'s single deferred search run afterwards.
    /// </summary>
    [Fact]
    public void ApplySavedFilter_DoesNotFireASearch_ForEachRestoredBaseModel()
    {
        var client = new Mock<ICivitaiClient>();
        var source = new ObservableCollection<BaseModelFilterItem>
        {
            new("SDXL 1.0"), new("Pony"), new("Illustrious")
        };
        var vm = CreateVm(source, civitaiClient: client.Object);

        vm.ApplySavedFilter(new CivitaiBrowserFilterData
        {
            SelectedBaseModels = ["SDXL 1.0", "Pony", "Illustrious"]
        });

        client.Verify(c => c.GetModelsAsync(
            It.IsAny<CivitaiModelsQuery>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// A saved base-model name absent from the shared source (catalog hasn't produced it yet,
    /// or names an installed-only label like "Krea 2") must select itself the instant the source
    /// materializes it — the same sticky mechanism a live toggle relies on when its name later
    /// drops out of the mirror. <see cref="Dispatcher.UIThread.RunJobs"/> drains the
    /// fire-and-forget <c>Dispatcher.UIThread.Post(RebuildBaseModelMirror)</c> that
    /// <c>OnBaseModelSourceChanged</c> queues — same post-hoc-drain idiom
    /// <c>CivitaiDownloadQueueStartResumeTests</c> uses (this is NOT the "await directly on
    /// InvokeAsync" situation <c>CivitaiBrowserViewModelBaseModelFilterTests</c> documents; a plain
    /// synchronous drain after the fact is enough here).
    /// </summary>
    [Fact]
    public void ApplySavedFilter_NameAbsentFromMirror_SelectsOnceTheSourceMaterializesIt()
    {
        var source = new ObservableCollection<BaseModelFilterItem> { new("SDXL 1.0") };
        var vm = CreateVm(source);

        vm.ApplySavedFilter(new CivitaiBrowserFilterData { SelectedBaseModels = ["Krea 2", "SDXL 1.0"] });

        vm.AvailableBaseModels.Should().NotContain(i => i.BaseModelRaw == "Krea 2");
        vm.AvailableBaseModels.Single(i => i.BaseModelRaw == "SDXL 1.0").IsSelected.Should().BeTrue();

        source.Add(new BaseModelFilterItem("Krea 2"));
        Dispatcher.UIThread.RunJobs();

        vm.AvailableBaseModels.Single(i => i.BaseModelRaw == "Krea 2").IsSelected.Should().BeTrue(
            "a saved base model the catalog didn't know yet must select itself once it materializes");
    }

    // SaveThenRestore_RoundTripsThroughAppSettings moved to
    // DiffusionNexus.IntegrationTests.CivitaiBrowserViewModelDispatcherTests: EnsureLoadedAsync's
    // restore step awaits Dispatcher.UIThread.InvokeAsync mid-flight, which this project cannot
    // pump reliably — see that class's doc comment for the full story.

    /// <summary>
    /// Regression guard for the review finding: <c>ClearBaseModelFilters</c> used to wipe only
    /// the live mirror items, leaving a name <see cref="CivitaiBrowserViewModel.ApplySavedFilter"/>
    /// had parked in the sticky set (because the catalog didn't list it yet) untouched. Since
    /// <see cref="CivitaiBrowserViewModel.CaptureFilter"/> now persists the whole sticky set, a
    /// Save right after "Clear all" would have silently re-persisted a base model the user just
    /// explicitly cleared.
    /// </summary>
    [Fact]
    public void ClearBaseModelFilters_AlsoClearsStickySelections()
    {
        var source = new ObservableCollection<BaseModelFilterItem> { new("SDXL 1.0") };
        var vm = CreateVm(source);
        vm.ApplySavedFilter(new CivitaiBrowserFilterData { SelectedBaseModels = ["Krea 2", "SDXL 1.0"] });
        vm.CaptureFilter().SelectedBaseModels.Should().BeEquivalentTo(["Krea 2", "SDXL 1.0"],
            "sanity check — the sticky-parked name must be part of the filter before Clear runs");

        vm.ClearBaseModelFiltersCommand.Execute(null);

        vm.CaptureFilter().SelectedBaseModels.Should().BeEmpty(
            "Clear all must wipe the sticky set too, not just the live mirror items — otherwise " +
            "a Save right after Clear would silently re-persist the cleared name");
    }

    /// <summary>
    /// Regression guard: <c>ClearBaseModelFilters</c> used to set N items false without
    /// suppressing <c>OnBaseModelFilterToggled</c>, so clearing 3 selections fired 3 searches,
    /// each cancelling the last, instead of the one explicit search the method already issues.
    /// </summary>
    [Fact]
    public void ClearBaseModelFilters_FiresExactlyOneSearch_NotOnePerClearedSelection()
    {
        var client = new Mock<ICivitaiClient>();
        client.Setup(c => c.GetModelsAsync(It.IsAny<CivitaiModelsQuery>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CivitaiPagedResponse<CivitaiModel>());
        var source = new ObservableCollection<BaseModelFilterItem> { new("SDXL 1.0"), new("Pony"), new("Illustrious") };
        var vm = CreateVm(source, civitaiClient: client.Object);
        foreach (var item in vm.AvailableBaseModels) item.IsSelected = true;
        client.Invocations.Clear(); // drop the 3 calls from selecting them above — only Clear matters here

        vm.ClearBaseModelFiltersCommand.Execute(null);

        client.Verify(c => c.GetModelsAsync(It.IsAny<CivitaiModelsQuery>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once, "clearing 3 selections must fire one search, not one per cleared item");
    }

    // EnsureLoadedAsync_CorruptSavedFilterJson_StillRunsTheFirstSearch,
    // EnsureLoadedAsync_SettingsServiceThrows_StillRunsTheFirstSearch, and
    // RestoreSavedFilter_SkipsBaseModelSelection_WhenUserSearchAlreadyStarted moved to
    // DiffusionNexus.IntegrationTests.CivitaiBrowserViewModelDispatcherTests: all three await
    // Dispatcher.UIThread.InvokeAsync(...) mid-flight (inside EnsureLoadedAsync/
    // RestoreSavedFilterAsync), which this project cannot pump reliably — see that class's doc
    // comment for the full story. The RunWithDispatcherPumpAsync helper that used to live here
    // moved with them (it only ever existed to route around that same unreliability).
}
