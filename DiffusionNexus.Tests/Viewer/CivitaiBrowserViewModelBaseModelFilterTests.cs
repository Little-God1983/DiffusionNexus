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
        // Avalonia Application), so — same rationale as CivitaiDownloadQueueStartResumeTests'
        // Dispatcher.UIThread.RunJobs() calls — a background pump keeps draining it for the
        // duration of the await, or the awaited InvokeAsync task never completes.
        await RunWithDispatcherPumpAsync(() => vm.EnsureLoadedAsync());
        await vm.EnsureLoadedAsync();

        client.Verify(c => c.GetModelsAsync(
            It.IsAny<CivitaiModelsQuery>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    private static async Task RunWithDispatcherPumpAsync(Func<Task> action)
    {
        using var pumpCts = new CancellationTokenSource();
        var pump = Task.Run(() =>
        {
            while (!pumpCts.IsCancellationRequested)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(5);
            }
        });
        try
        {
            await action();
        }
        finally
        {
            pumpCts.Cancel();
            await pump;
        }
    }
}
