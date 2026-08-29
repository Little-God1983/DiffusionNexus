using System.Collections.ObjectModel;
using System.Text.Json;
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

    [Fact]
    public async Task SaveThenRestore_RoundTripsThroughAppSettings()
    {
        string? stored = null;
        var settings = new Mock<IAppSettingsService>();
        settings.Setup(s => s.SetCivitaiBrowserFilterJsonAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<string?, CancellationToken>((json, _) => stored = json)
            .Returns(Task.CompletedTask);
        settings.Setup(s => s.GetCivitaiBrowserFilterJsonAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => stored);

        var source = new ObservableCollection<BaseModelFilterItem> { new("SDXL 1.0"), new("Pony") };
        var vm = CreateVm(source, settings.Object);
        vm.AvailableBaseModels.Single(i => i.BaseModelRaw == "Pony").IsSelected = true;
        vm.ShowEarlyAccess = false;
        vm.ShowNsfw = false;

        await vm.SaveFilterCommand.ExecuteAsync(null);
        stored.Should().NotBeNullOrWhiteSpace("Save must persist through the settings service");

        var restoredSource = new ObservableCollection<BaseModelFilterItem> { new("SDXL 1.0"), new("Pony") };
        var restoredVm = CreateVm(restoredSource, settings.Object);

        // EnsureLoadedAsync's restore step awaits Dispatcher.UIThread.InvokeAsync (inside
        // RestoreSavedFilterAsync's dispatch of ApplySavedFilter). This headless xUnit host never
        // pumps that queue on its own — the same situation
        // CivitaiBrowserViewModelBaseModelFilterTests.EnsureLoadedAsync_SearchesOnce... documents
        // for the deferred search's own UI-thread write — so something must pump concurrently
        // while the await is in flight.
        await RunWithDispatcherPumpAsync(() => restoredVm.EnsureLoadedAsync());

        restoredVm.ShowEarlyAccess.Should().BeFalse();
        restoredVm.ShowNsfw.Should().BeFalse();
        restoredVm.ShowInstalled.Should().BeTrue();
        restoredVm.ShowPaywalled.Should().BeTrue();
        restoredVm.AvailableBaseModels.Single(i => i.BaseModelRaw == "Pony").IsSelected.Should().BeTrue();
        restoredVm.AvailableBaseModels.Single(i => i.BaseModelRaw == "SDXL 1.0").IsSelected.Should().BeFalse();
    }

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

    /// <summary>
    /// Pins the exact failure mode <c>EnsureLoadedAsync</c> has regressed on before: a settings
    /// read that returns garbage, or throws outright, must not prevent the deferred first search
    /// from running. <see cref="CivitaiBrowserViewModel.RestoreSavedFilterAsync"/>'s try/catch
    /// degrades silently — this proves it actually does, through the public entry point, rather
    /// than trusting the catch block by inspection.
    /// </summary>
    [Fact]
    public async Task EnsureLoadedAsync_CorruptSavedFilterJson_StillRunsTheFirstSearch()
    {
        var settings = new Mock<IAppSettingsService>();
        settings.Setup(s => s.GetCivitaiBrowserFilterJsonAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("{ this is not valid json");

        var client = new Mock<ICivitaiClient>();
        client.Setup(c => c.GetModelsAsync(It.IsAny<CivitaiModelsQuery>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CivitaiPagedResponse<CivitaiModel>());
        var vm = CreateVm(settingsService: settings.Object, civitaiClient: client.Object);

        await RunWithDispatcherPumpAsync(() => vm.EnsureLoadedAsync());

        client.Verify(c => c.GetModelsAsync(It.IsAny<CivitaiModelsQuery>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once, "corrupt saved-filter JSON must not block the deferred first search");
    }

    /// <summary>Same guard as above, for the settings read itself throwing rather than
    /// returning unparseable data.</summary>
    [Fact]
    public async Task EnsureLoadedAsync_SettingsServiceThrows_StillRunsTheFirstSearch()
    {
        var settings = new Mock<IAppSettingsService>();
        settings.Setup(s => s.GetCivitaiBrowserFilterJsonAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("simulated settings failure"));

        var client = new Mock<ICivitaiClient>();
        client.Setup(c => c.GetModelsAsync(It.IsAny<CivitaiModelsQuery>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CivitaiPagedResponse<CivitaiModel>());
        var vm = CreateVm(settingsService: settings.Object, civitaiClient: client.Object);

        await RunWithDispatcherPumpAsync(() => vm.EnsureLoadedAsync());

        client.Verify(c => c.GetModelsAsync(It.IsAny<CivitaiModelsQuery>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once, "a throwing settings read must not block the deferred first search");
    }

    /// <summary>
    /// The state-mismatch this class was reviewed for: if the user starts their own search
    /// while <see cref="CivitaiBrowserViewModel.RestoreSavedFilterAsync"/>'s settings read is
    /// still in flight, that search's <c>BuildQuery</c> already ran without the restored base
    /// models. Applying the restored selection afterwards would move the badge
    /// (<c>ActiveBaseModelFilterCount</c>) without moving the grid — nothing re-queries — so the
    /// chosen fix is to skip the base-model half of the restore in that case (not force a second,
    /// user-clobbering search). The four Show flags stay safe to apply regardless, since they
    /// filter the already-fetched <c>Results</c> directly rather than the query.
    /// <see cref="CivitaiBrowserViewModel.SearchAsync"/> sets <c>_searchStarted</c> as its first
    /// statement, synchronously, before any await — so calling
    /// <see cref="CivitaiBrowserViewModel.SearchCommand"/> before <c>EnsureLoadedAsync</c> is a
    /// deterministic way to reproduce "the user already searched", no timing races needed.
    /// </summary>
    [Fact]
    public async Task RestoreSavedFilter_SkipsBaseModelSelection_WhenUserSearchAlreadyStarted()
    {
        var settings = new Mock<IAppSettingsService>();
        settings.Setup(s => s.GetCivitaiBrowserFilterJsonAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(JsonSerializer.Serialize(new CivitaiBrowserFilterData
            {
                SelectedBaseModels = ["Pony"],
                ShowEarlyAccess = false,
            }));

        var client = new Mock<ICivitaiClient>();
        client.Setup(c => c.GetModelsAsync(It.IsAny<CivitaiModelsQuery>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CivitaiPagedResponse<CivitaiModel>());

        var source = new ObservableCollection<BaseModelFilterItem> { new("SDXL 1.0"), new("Pony") };
        var vm = CreateVm(source, settings.Object, client.Object);

        // The user's own search starts FIRST — Interlocked.Exchange(ref _searchStarted, 1) runs
        // synchronously as SearchAsync's very first statement, so _searchStarted is already set
        // by the time this call returns control, exactly like a keystroke racing ahead of the
        // restore's settings read.
        var userSearchTask = vm.SearchCommand.ExecuteAsync(null);

        await RunWithDispatcherPumpAsync(() => Task.WhenAll(userSearchTask, vm.EnsureLoadedAsync()));

        vm.AvailableBaseModels.Single(i => i.BaseModelRaw == "Pony").IsSelected.Should().BeFalse(
            "the user's own search already ran without the restored base model — selecting it " +
            "now would only move the badge, not the grid, so the restore must skip it");
        vm.IsBaseModelFilterActive.Should().BeFalse();
        vm.ShowEarlyAccess.Should().BeFalse(
            "the four Show flags are safe to apply regardless of the race — they filter the " +
            "already-fetched Results directly, not the query");
    }

    /// <summary>
    /// Runs <paramref name="action"/> while pumping <see cref="Dispatcher.UIThread"/> just for its
    /// duration, then stops. Copied from
    /// <c>CivitaiBrowserViewModelBaseModelFilterTests.RunWithDispatcherPumpAsync</c> — see that
    /// copy's doc comment for the full rationale (scoped lifetime so a free-running pump thread
    /// doesn't contend with other parallel test classes touching the same process-wide
    /// <see cref="Dispatcher.UIThread"/> singleton; bounded timeout so a genuine regression fails
    /// fast instead of hanging CI).
    /// </summary>
    private static async Task RunWithDispatcherPumpAsync(Func<Task> action, TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(5);
        var task = action();

        using var pumpCts = new CancellationTokenSource();
        var pump = Task.Run(async () =>
        {
            while (!task.IsCompleted && !pumpCts.IsCancellationRequested)
            {
                Dispatcher.UIThread.RunJobs();
                try
                {
                    await Task.Delay(5, pumpCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        });

        try
        {
            var winner = await Task.WhenAny(task, Task.Delay(effectiveTimeout)).ConfigureAwait(false);
            if (winner != task)
            {
                throw new TimeoutException(
                    $"{nameof(RunWithDispatcherPumpAsync)}: awaited operation did not complete within " +
                    $"{effectiveTimeout} even while Dispatcher.UIThread was being pumped.");
            }

            await task;
        }
        finally
        {
            pumpCts.Cancel();
            await pump.ConfigureAwait(false);
        }
    }
}
