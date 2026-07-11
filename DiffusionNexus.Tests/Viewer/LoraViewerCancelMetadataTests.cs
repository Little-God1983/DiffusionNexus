using System.Threading;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;

namespace DiffusionNexus.Tests.Viewer;

/// <summary>
/// Covers the abort affordance for the LoRA-viewer "Download Metadata" sync.
/// The full end-to-end abort path is exercised by a real-app run (see plan Task 3);
/// these tests lock down the command surface that does not require App.Services / DI.
/// </summary>
public class LoraViewerCancelMetadataTests
{
    private static LoraViewerViewModel CreateViewModel() => new();

    [Fact]
    public void IsCancellableIsFalseByDefault()
    {
        var vm = CreateViewModel();

        vm.IsCancellable.Should().BeFalse(
            "the Cancel button must stay hidden until a metadata sync is running");
    }

    [Fact]
    public void CancelWhenNoSyncRunningIsSafeNoOp()
    {
        var vm = CreateViewModel();

        var act = () => vm.CancelMetadataDownloadCommand.Execute(null);

        act.Should().NotThrow("cancelling with no sync in flight must be a no-op");
        vm.SyncStatus.Should().BeNull(
            "a no-op cancel must not post a misleading 'Cancelling…' status");
        vm.IsCancellable.Should().BeFalse();
    }

    [Fact]
    public void CancelSignalsTheActiveSyncTokenAndFlipsStatus()
    {
        var vm = CreateViewModel();
        using var cts = new CancellationTokenSource();
        vm.SetActiveMetadataSyncCtsForTest(cts); // simulates an in-flight sync
        vm.IsCancellable = true;

        vm.CancelMetadataDownloadCommand.Execute(null);

        cts.IsCancellationRequested.Should().BeTrue(
            "cancel must request cancellation on the running sync's token");
        vm.SyncStatus.Should().Be("Cancelling…");
        vm.IsCancellable.Should().BeFalse(
            "the Cancel button must hide the moment cancellation is requested");
    }
}
