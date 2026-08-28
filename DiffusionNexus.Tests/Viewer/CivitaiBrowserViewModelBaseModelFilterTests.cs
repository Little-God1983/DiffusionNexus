using System.Collections.ObjectModel;
using Avalonia.Threading;
using DiffusionNexus.Civitai;
using DiffusionNexus.Civitai.Models;
using DiffusionNexus.UI.Services.CivitaiBrowser;
using DiffusionNexus.UI.ViewModels;
using DiffusionNexus.UI.ViewModels.CivitaiBrowser;
using FluentAssertions;
using Moq;

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
        var vm = new CivitaiBrowserViewModel(null, null, null, new CivitaiDownloadQueue(null),
            new CivitaiWaitlist(null, null, persistPathOverride: Path.Combine(Path.GetTempPath(), $"dn-waitlist-{Guid.NewGuid():N}.json")), source);
        return (vm, source);
    }

    /// <summary>
    /// Mirrors the construction at LoraViewerViewModel.cs:499, with a caller-supplied
    /// (typically mocked) <see cref="ICivitaiClient"/> so tests can assert on API calls.
    /// </summary>
    private static CivitaiBrowserViewModel CreateViewModel(ICivitaiClient client) =>
        new(client, null, null, new CivitaiDownloadQueue(null),
            new CivitaiWaitlist(null, null, persistPathOverride: Path.Combine(Path.GetTempPath(), $"dn-waitlist-{Guid.NewGuid():N}.json")), null);

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

    [Fact]
    public void Constructor_DoesNotSearchCivitai()
    {
        var client = new Mock<ICivitaiClient>();

        _ = CreateViewModel(client.Object);

        client.Verify(c => c.GetModelsAsync(
            It.IsAny<CivitaiModelsQuery>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EnsureLoadedAsync_SearchesOnce_HoweverOftenItIsCalled()
    {
        var client = new Mock<ICivitaiClient>();
        client.Setup(c => c.GetModelsAsync(
                It.IsAny<CivitaiModelsQuery>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CivitaiPagedResponse<CivitaiModel>());
        var vm = CreateViewModel(client.Object);

        // LoadNextAsync awaits Dispatcher.UIThread.InvokeAsync to add results on the UI thread.
        // This headless xUnit host never pumps that queue on its own (there is no running
        // Avalonia Application) — in the real app Application.Run() supplies the loop. This is
        // NOT the same situation as CivitaiDownloadQueueStartResumeTests' post-hoc
        // Dispatcher.UIThread.RunJobs() calls: those drain a queue that fire-and-forget
        // Dispatcher.UIThread.Post(...) filled, called once *after* the awaited method already
        // returned. Here the await is directly ON Dispatcher.UIThread.InvokeAsync, so nothing
        // can ever satisfy it unless something pumps concurrently, while it is in flight.
        await RunWithDispatcherPumpAsync(() => vm.EnsureLoadedAsync());
        await vm.EnsureLoadedAsync();

        client.Verify(c => c.GetModelsAsync(
            It.IsAny<CivitaiModelsQuery>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// Runs <paramref name="action"/> while pumping <see cref="Dispatcher.UIThread"/> just for
    /// its duration, then stops. <c>Dispatcher.UIThread</c> is a process-wide singleton and this
    /// test project runs collections in parallel with no `DisableTestParallelization` /
    /// `xunit.runner.json`, so a free-running pump thread would keep touching that singleton for
    /// as long as the test process is alive — contending with any other parallel test class that
    /// also touches the dispatcher. Scoped instead to the exact lifetime of
    /// <paramref name="action"/>'s task: the pump loop's own condition is the task's completion
    /// (so it stops itself the instant the awaited operation finishes, no external signal
    /// needed), and the <c>finally</c> cancellation is only a backstop for the case where the
    /// task throws before completing normally.
    ///
    /// Bounded by <paramref name="timeout"/> so a genuine future regression (the dispatcher call
    /// never gets satisfied) fails this test in seconds with a clear message, instead of hanging
    /// a CI run for minutes — which is exactly what happened twice while writing this test
    /// (~10 min and ~2.5 min) before this pump was added.
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
                    $"{effectiveTimeout} even while Dispatcher.UIThread was being pumped. This means the " +
                    "dispatcher call it depends on is stuck on something the pump doesn't satisfy — " +
                    "investigate before assuming this is the same class of hang the pump was built for.");
            }

            await task; // propagate the real result/exception, not just "it completed"
        }
        finally
        {
            pumpCts.Cancel();
            await pump.ConfigureAwait(false);
        }
    }
}
