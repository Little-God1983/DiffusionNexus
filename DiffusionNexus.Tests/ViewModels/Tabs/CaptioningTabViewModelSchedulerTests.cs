using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Tests.Helpers;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.ViewModels.Tabs;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.ViewModels.Tabs;

/// <summary>
/// Proves the <see cref="IUiScheduler"/> seam on <see cref="CaptioningTabViewModel"/>:
/// when a download reaches a terminal state, <c>OnDownloadCoordinatorStateChanged</c>
/// posts a per-model status refresh onto the UI thread. With
/// <see cref="ImmediateUiScheduler"/> that post runs inline, so the refreshed
/// readiness badge is observable synchronously — no Avalonia dispatcher required.
/// </summary>
public class CaptioningTabViewModelSchedulerTests
{
    private static CaptioningModelInfo Info(CaptioningModelType type, CaptioningModelStatus status)
        => new(type, status, FilePath: "", FileSizeBytes: 0, ExpectedSizeBytes: 0, DisplayName: "", Description: "");

    [Fact]
    public void WhenADownloadCompletesThenModelReadinessRefreshesSynchronouslyThroughTheScheduler()
    {
        // Mutable disk state the fake captioning service reports.
        var diskStatus = CaptioningModelStatus.NotDownloaded;
        var captioning = new Mock<ICaptioningService>();
        captioning.SetupGet(c => c.IsNativeLibraryLoaded).Returns(true);
        captioning
            .Setup(c => c.GetModelInfo(It.IsAny<CaptioningModelType>()))
            .Returns((CaptioningModelType t) => Info(t, diskStatus));

        // The coordinator snapshot: one in-flight download to begin with.
        var tasks = new List<DownloadTask>
        {
            new(Guid.NewGuid(), "Qwen3", DownloadTaskStatus.Active, 50, null)
        };
        var coordinator = new Mock<IDownloadCoordinator>();
        coordinator.SetupGet(c => c.All).Returns(() => tasks);

        var scheduler = new ImmediateUiScheduler();

        var vm = new CaptioningTabViewModel(
            new DiffusionNexus.UI.Services.DatasetEventAggregator(),
            new Mock<IDatasetState>().Object,
            captioningService: captioning.Object,
            downloadCoordinator: coordinator.Object,
            uiScheduler: scheduler);

        // Default selected model is Qwen3_VL_8B; it is not on disk yet.
        vm.IsModelReady.Should().BeFalse("the selected model is not on disk at construction");

        // The download finishes: the model is now present, and its task drops off
        // the coordinator's All snapshot (the terminal transition the VM reacts to).
        diskStatus = CaptioningModelStatus.Ready;
        tasks = new List<DownloadTask>();
        coordinator.Raise(c => c.StateChanged += null, EventArgs.Empty);

        // The posted refresh ran inline through the scheduler, so the readiness
        // badge already reflects the on-disk model.
        scheduler.PostCount.Should().Be(1);
        vm.IsModelReady.Should().BeTrue();
    }
}
