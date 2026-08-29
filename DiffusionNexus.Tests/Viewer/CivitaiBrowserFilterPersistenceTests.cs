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
