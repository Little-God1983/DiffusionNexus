using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia.Headless.XUnit;
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

namespace DiffusionNexus.IntegrationTests;

/// <summary>
/// Covers the handful of <c>CivitaiBrowserViewModel</c> regressions whose test bodies must
/// actually await <c>Dispatcher.UIThread.InvokeAsync(...)</c> mid-flight — <c>LoadNextAsync</c>
/// writes its results on the UI thread, and <c>RestoreSavedFilterAsync</c> applies a restored
/// filter the same way. These tests originally lived in
/// <c>DiffusionNexus.Tests.Viewer.CivitaiBrowserViewModelBaseModelFilterTests</c> and
/// <c>CivitaiBrowserFilterPersistenceTests</c>, driven by a hand-rolled
/// <c>RunWithDispatcherPumpAsync</c> helper that spun a background <c>Task.Run</c> loop calling
/// <c>Dispatcher.UIThread.RunJobs()</c> while the awaited operation was in flight, bounded by a
/// 5-second timeout.
/// <para>
/// That helper only worked by accident of run order. <c>DiffusionNexus.Tests</c> never
/// initializes an Avalonia platform, so <c>Dispatcher.UIThread</c> falls back to Avalonia's
/// internal <c>NullDispatcherImpl</c>: <c>CheckAccess()</c> is unconditionally <c>true</c> and
/// <c>Signal()</c>/<c>Signaled</c> are no-ops, so nothing ever wakes a waiting pump — jobs only
/// ever get drained when <em>something, somewhere</em> happens to call
/// <c>Dispatcher.UIThread.RunJobs()</c> again. <c>Dispatcher.UIThread</c> is a single
/// process-wide static shared by every concurrently-running test in the assembly (xUnit
/// parallelizes across test classes by default, and this project never disabled that), so in the
/// full 4989-test run there is always some other class's pump loop churning the shared queue and
/// incidentally draining a given test's pending operation. Filtered down to just the two
/// `CivitaiBrowser*` classes, there is far less of that ambient pumping pressure, and the
/// `RunContinuationsAsynchronously` completion of a `TaskCompletionSource`-backed
/// `DispatcherOperation` sometimes has no thread pick it up before the 5-second bound elapses —
/// reproduced directly: a failing run showed the background pump looping 318 times over the
/// full 5 seconds, `RunJobs()` never throwing, yet the awaited operation never completing. This
/// is a genuine timing race on a shared, unsignaled queue, not a "which test ran first"
/// ordering bug — an isolated control test proved `Dispatcher.UIThread.RunJobs()` drains
/// correctly from any thread once nothing else is contending for the same queue.
/// </para>
/// <para>
/// This project already carries a real headless Avalonia platform (see <c>TestAppHost</c> and
/// every other <c>[AvaloniaFact]</c> test here) with a genuine <c>ManagedDispatcherImpl</c> —
/// real thread affinity, a real <c>AutoResetEvent</c>-based wakeup, and reentrant dispatcher-frame
/// pumping for an in-flight <c>await</c> on the dispatcher's own thread. Confirmed empirically
/// (a throwaway spike run 10/10 green): under <c>[AvaloniaFact]</c>, a plain
/// <c>await vm.EnsureLoadedAsync()</c> — including the harder <c>Task.WhenAll</c> race shape used
/// by <see cref="DeferredInitialSearch_DoesNotClobberAUserInitiatedSearch"/> — resolves the
/// dispatcher hop with no manual pump helper at all. <c>[assembly: CollectionBehavior(DisableTestParallelization = true)]</c>
/// (see <c>TestAppHost.cs</c>) also means no other test class can be concurrently hammering the
/// same dispatcher singleton while these run, which removes the whole class of race this file
/// exists to route around.
/// </para>
/// <para>
/// These are pure view-model tests — no view, no visual tree, no <c>TabControl</c> — so the
/// "themeless headless session" caveat that matters for <c>CivitaiBrowserViewDeferredLoadTests</c>
/// (a real <c>TabControl</c> never resolving a template under this project's unstyled
/// <c>Application</c>) does not apply here.
/// </para>
/// </summary>
public class CivitaiBrowserViewModelDispatcherTests
{
    private static CivitaiBrowserViewModel CreateViewModel(ICivitaiClient client) =>
        new(client, null, null, new CivitaiDownloadQueue(null),
            new CivitaiWaitlist(null, null, persistPathOverride: Path.Combine(Path.GetTempPath(), $"dn-waitlist-{Guid.NewGuid():N}.json")), null);

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

    private static CivitaiPagedResponse<CivitaiModel> SingleModelResponse(int modelId, string name) =>
        new()
        {
            Items =
            [
                new CivitaiModel
                {
                    Id = modelId,
                    Name = name,
                    ModelVersions = [new CivitaiModelVersion { Id = modelId * 100 }]
                }
            ],
            Metadata = new CivitaiPaginationMetadata()
        };

    [AvaloniaFact]
    public async Task EnsureLoadedAsync_SearchesOnce_HoweverOftenItIsCalled()
    {
        var client = new Mock<ICivitaiClient>();
        client.Setup(c => c.GetModelsAsync(
                It.IsAny<CivitaiModelsQuery>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CivitaiPagedResponse<CivitaiModel>());
        var vm = CreateViewModel(client.Object);

        // LoadNextAsync awaits Dispatcher.UIThread.InvokeAsync to add results on the UI thread.
        // Under [AvaloniaFact] that resolves on its own — no manual pump needed, see the class doc.
        await vm.EnsureLoadedAsync();
        await vm.EnsureLoadedAsync();

        client.Verify(c => c.GetModelsAsync(
            It.IsAny<CivitaiModelsQuery>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// Reproduces the browser-empty-on-open / "no results then refreshes" regression:
    /// <see cref="CivitaiBrowserViewModel.EnsureLoadedAsync"/> defers the first search to
    /// after the (now-async) installed-set load, so a user who searches before that
    /// deferred search reaches Civitai races it. Neither pipeline has a re-entrancy guard,
    /// and the token check in <c>LoadNextAsync</c> runs BEFORE the UI-thread write — a
    /// cancellation landing after that check (but before the dispatched write executes)
    /// still lets the cancelled pipeline's stale response land in <see cref="CivitaiBrowserViewModel.Results"/>.
    ///
    /// The two <see cref="ICivitaiClient.GetModelsAsync"/> calls are driven by separate
    /// <see cref="TaskCompletionSource{T}"/>s so the race is deterministic instead of
    /// timing-dependent:
    /// 1. The deferred load's call (tcsA) is released FIRST, letting it pass its
    ///    cancellation check and queue its UI-thread write — but that write is not yet
    ///    pumped, so it sits pending exactly like a real dispatcher hop would.
    /// 2. Only THEN does the user's own search start (<c>SearchCommand</c>), which cancels
    ///    the deferred load's token — after its check already passed.
    /// 3. The user's own call (tcsB) is released, queuing its own write.
    /// 4. Both queued UI-thread writes are drained together.
    ///
    /// Before the fix: the deferred load's stale "StaleModel" (id 1) leaks into
    /// <c>Results</c> even though it was cancelled by the user's search. After the fix
    /// (token re-checked inside the dispatcher callback, plus not clobbering an
    /// already-started user search), only the user's own "RealModel" (id 2) survives.
    /// </summary>
    [AvaloniaFact]
    public async Task DeferredInitialSearch_DoesNotClobberAUserInitiatedSearch()
    {
        var tcsA = new TaskCompletionSource<CivitaiPagedResponse<CivitaiModel>>();
        var tcsB = new TaskCompletionSource<CivitaiPagedResponse<CivitaiModel>>();
        var callCount = 0;

        var client = new Mock<ICivitaiClient>();
        client.Setup(c => c.GetModelsAsync(
                It.IsAny<CivitaiModelsQuery>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(() => Interlocked.Increment(ref callCount) == 1 ? tcsA.Task : tcsB.Task);

        var vm = CreateViewModel(client.Object);

        // The view's OnAttachedToVisualTree calls this without awaiting it.
        var loadTask = vm.EnsureLoadedAsync();

        // Release the deferred load's API call. TaskCompletionSource continuations run
        // synchronously on this thread by default, so this drives LoadNextAsync forward
        // past its `if (ct.IsCancellationRequested) return;` check and up to (but not
        // through, since nothing is pumping the dispatcher yet) its queued UI-thread write.
        tcsA.SetResult(SingleModelResponse(modelId: 1, name: "StaleModel"));

        // The user searches now — after the deferred load's check already passed.
        var userSearchTask = vm.SearchCommand.ExecuteAsync(null);

        // Release the user's own API call the same way; it queues its own UI-thread write.
        tcsB.SetResult(SingleModelResponse(modelId: 2, name: "RealModel"));

        // Drain both queued writes together — this is the exact interleave described above.
        Dispatcher.UIThread.RunJobs();

        await Task.WhenAll(loadTask, userSearchTask);

        vm.Results.Select(r => r.Model!.Id).Should().Equal(
            new[] { 2 },
            "the deferred load was cancelled by the user's own search (after its check had " +
            "already passed) and must not write its stale response into Results");
        vm.StatusMessage.Should().NotBe("No results.");
    }

    [AvaloniaFact]
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
        // RestoreSavedFilterAsync's dispatch of ApplySavedFilter). Under [AvaloniaFact] that
        // resolves on its own — no manual pump needed, see the class doc.
        await restoredVm.EnsureLoadedAsync();

        restoredVm.ShowEarlyAccess.Should().BeFalse();
        restoredVm.ShowNsfw.Should().BeFalse();
        restoredVm.ShowInstalled.Should().BeTrue();
        restoredVm.ShowPaywalled.Should().BeTrue();
        restoredVm.AvailableBaseModels.Single(i => i.BaseModelRaw == "Pony").IsSelected.Should().BeTrue();
        restoredVm.AvailableBaseModels.Single(i => i.BaseModelRaw == "SDXL 1.0").IsSelected.Should().BeFalse();
    }

    /// <summary>
    /// Pins the exact failure mode <c>EnsureLoadedAsync</c> has regressed on before: a settings
    /// read that returns garbage, or throws outright, must not prevent the deferred first search
    /// from running. <see cref="CivitaiBrowserViewModel.RestoreSavedFilterAsync"/>'s try/catch
    /// degrades silently — this proves it actually does, through the public entry point, rather
    /// than trusting the catch block by inspection.
    /// </summary>
    [AvaloniaFact]
    public async Task EnsureLoadedAsync_CorruptSavedFilterJson_StillRunsTheFirstSearch()
    {
        var settings = new Mock<IAppSettingsService>();
        settings.Setup(s => s.GetCivitaiBrowserFilterJsonAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("{ this is not valid json");

        var client = new Mock<ICivitaiClient>();
        client.Setup(c => c.GetModelsAsync(It.IsAny<CivitaiModelsQuery>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CivitaiPagedResponse<CivitaiModel>());
        var vm = CreateVm(settingsService: settings.Object, civitaiClient: client.Object);

        await vm.EnsureLoadedAsync();

        client.Verify(c => c.GetModelsAsync(It.IsAny<CivitaiModelsQuery>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once, "corrupt saved-filter JSON must not block the deferred first search");
    }

    /// <summary>Same guard as above, for the settings read itself throwing rather than
    /// returning unparseable data.</summary>
    [AvaloniaFact]
    public async Task EnsureLoadedAsync_SettingsServiceThrows_StillRunsTheFirstSearch()
    {
        var settings = new Mock<IAppSettingsService>();
        settings.Setup(s => s.GetCivitaiBrowserFilterJsonAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("simulated settings failure"));

        var client = new Mock<ICivitaiClient>();
        client.Setup(c => c.GetModelsAsync(It.IsAny<CivitaiModelsQuery>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CivitaiPagedResponse<CivitaiModel>());
        var vm = CreateVm(settingsService: settings.Object, civitaiClient: client.Object);

        await vm.EnsureLoadedAsync();

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
    [AvaloniaFact]
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

        await Task.WhenAll(userSearchTask, vm.EnsureLoadedAsync());

        vm.AvailableBaseModels.Single(i => i.BaseModelRaw == "Pony").IsSelected.Should().BeFalse(
            "the user's own search already ran without the restored base model — selecting it " +
            "now would only move the badge, not the grid, so the restore must skip it");
        vm.IsBaseModelFilterActive.Should().BeFalse();
        vm.ShowEarlyAccess.Should().BeFalse(
            "the four Show flags are safe to apply regardless of the race — they filter the " +
            "already-fetched Results directly, not the query");
    }
}
