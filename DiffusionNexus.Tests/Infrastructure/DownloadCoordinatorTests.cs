using DiffusionNexus.Domain.Services;
using DiffusionNexus.Infrastructure.Services;
using FluentAssertions;

namespace DiffusionNexus.Tests.Infrastructure;

/// <summary>
/// Covers <see cref="DownloadCoordinator"/> (issue #443): semaphore-gated FIFO admission, per-task
/// cancellation, the activity-log aggregation shim, and the known-sharp live semaphore swap in the
/// <c>MaxConcurrent</c> setter.
///
/// All concurrency is driven deterministically with <see cref="TaskCompletionSource"/> gates (the
/// idiom used by <c>ComfyUIUpdateServiceTests</c>) — no <c>Thread.Sleep</c> races. Every wait is
/// bounded by a timeout so a regression fails fast instead of hanging the suite.
/// </summary>
public class DownloadCoordinatorTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>Download action that signals <paramref name="started"/> then blocks on <paramref name="release"/>.</summary>
    private static Func<IProgress<DownloadTaskProgress>, CancellationToken, Task<bool>> Gated(
        TaskCompletionSource started, Task release, bool result = true)
        => async (_, _) =>
        {
            started.TrySetResult();
            await release.ConfigureAwait(false);
            return result;
        };

    /// <summary>Download action that blocks until either released or its own token is cancelled.</summary>
    private static Func<IProgress<DownloadTaskProgress>, CancellationToken, Task<bool>> GatedCancellable(
        TaskCompletionSource started, Task release)
        => async (_, ct) =>
        {
            started.TrySetResult();
            await release.WaitAsync(ct).ConfigureAwait(false);
            return true;
        };

    // ── Happy-path result mapping ──

    [Fact]
    public async Task Enqueue_SuccessfulDownload_ReturnsTrue_AndLeavesNoTrackedTasks()
    {
        using var coordinator = new DownloadCoordinator();
        var stateChanges = 0;
        coordinator.StateChanged += (_, _) => Interlocked.Increment(ref stateChanges);

        var result = await coordinator.EnqueueAsync("model", (_, _) => Task.FromResult(true));

        result.Should().BeTrue();
        coordinator.All.Should().BeEmpty("completed tasks are removed from the tracked list");
        stateChanges.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Enqueue_ActionReturnsFalse_ReturnsFalse()
    {
        using var coordinator = new DownloadCoordinator();
        var result = await coordinator.EnqueueAsync("model", (_, _) => Task.FromResult(false));
        result.Should().BeFalse();
    }

    [Fact]
    public async Task Enqueue_ActionThrows_ReturnsFalse_WithoutPropagating()
    {
        using var coordinator = new DownloadCoordinator();
        var result = await coordinator.EnqueueAsync("model",
            (_, _) => throw new InvalidOperationException("boom"));
        result.Should().BeFalse();
        coordinator.All.Should().BeEmpty();
    }

    [Fact]
    public async Task Enqueue_NullAction_Throws()
    {
        using var coordinator = new DownloadCoordinator();
        var act = async () => await coordinator.EnqueueAsync("model", null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ── Semaphore-gated FIFO admission ──

    [Fact]
    public async Task Enqueue_BeyondLimit_QueuesExcess_ExposingActiveAndQueuedSnapshots()
    {
        using var coordinator = new DownloadCoordinator { MaxConcurrent = 1 };
        var startedA = new TaskCompletionSource();
        var releaseA = new TaskCompletionSource();

        var a = coordinator.EnqueueAsync("A", Gated(startedA, releaseA.Task));
        await startedA.Task.WaitAsync(Timeout);

        var b = coordinator.EnqueueAsync("B", (_, _) => Task.FromResult(true));

        coordinator.Active.Select(t => t.Name).Should().Equal("A");
        coordinator.Queued.Select(t => t.Name).Should().Equal("B");
        coordinator.All.Select(t => t.Name).Should().Equal("A", "B");

        releaseA.SetResult();
        (await a.WaitAsync(Timeout)).Should().BeTrue();
        (await b.WaitAsync(Timeout)).Should().BeTrue();
    }

    [Fact]
    public async Task Admission_IsFifo_WhenSlotFrees()
    {
        using var coordinator = new DownloadCoordinator { MaxConcurrent = 1 };
        var startedA = new TaskCompletionSource();
        var releaseA = new TaskCompletionSource();
        var startedB = new TaskCompletionSource();
        var releaseB = new TaskCompletionSource();
        var startedC = new TaskCompletionSource();
        var releaseC = new TaskCompletionSource();

        var a = coordinator.EnqueueAsync("A", Gated(startedA, releaseA.Task));
        await startedA.Task.WaitAsync(Timeout);

        var b = coordinator.EnqueueAsync("B", Gated(startedB, releaseB.Task));
        var c = coordinator.EnqueueAsync("C", Gated(startedC, releaseC.Task));

        // Free the single slot: the older-enqueued B must be admitted before C.
        releaseA.SetResult();
        await startedB.Task.WaitAsync(Timeout);
        startedC.Task.IsCompleted.Should().BeFalse("C waits until B releases the slot (FIFO)");

        releaseB.SetResult();
        await startedC.Task.WaitAsync(Timeout);

        releaseC.SetResult();
        await Task.WhenAll(a, b, c).WaitAsync(Timeout);
    }

    // ── Cancellation ──

    [Fact]
    public async Task Cancel_QueuedTask_MarksCancelled_WithoutConsumingASlot()
    {
        using var coordinator = new DownloadCoordinator { MaxConcurrent = 1 };
        var startedA = new TaskCompletionSource();
        var releaseA = new TaskCompletionSource();
        var startedB = new TaskCompletionSource();
        var releaseB = new TaskCompletionSource();

        var a = coordinator.EnqueueAsync("A", Gated(startedA, releaseA.Task));
        await startedA.Task.WaitAsync(Timeout);

        var b = coordinator.EnqueueAsync("B", Gated(startedB, releaseB.Task));
        var queuedId = coordinator.Queued.Single().Id;

        coordinator.Cancel(queuedId);

        (await b.WaitAsync(Timeout)).Should().BeFalse("a cancelled queued task never runs");
        startedB.Task.IsCompleted.Should().BeFalse("the cancelled task's action must never start");

        releaseA.SetResult();
        (await a.WaitAsync(Timeout)).Should().BeTrue();
    }

    [Fact]
    public async Task Cancel_ActiveTask_SignalsToken_AndReportsFalse()
    {
        using var coordinator = new DownloadCoordinator { MaxConcurrent = 1 };
        var startedA = new TaskCompletionSource();
        var releaseA = new TaskCompletionSource(); // never released — cancellation must end it

        var a = coordinator.EnqueueAsync("A", GatedCancellable(startedA, releaseA.Task));
        await startedA.Task.WaitAsync(Timeout);

        coordinator.Cancel(coordinator.Active.Single().Id);

        (await a.WaitAsync(Timeout)).Should().BeFalse("a cancelled active download reports failure");
    }

    [Fact]
    public void Cancel_UnknownId_IsNoOp()
    {
        using var coordinator = new DownloadCoordinator();
        var act = () => coordinator.Cancel(Guid.NewGuid());
        act.Should().NotThrow();
    }

    // ── MaxConcurrent setter ──

    [Fact]
    public void MaxConcurrent_BelowOne_Throws()
    {
        using var coordinator = new DownloadCoordinator();
        var act = () => coordinator.MaxConcurrent = 0;
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void MaxConcurrent_SameValue_IsNoOp_AndDoesNotRaiseStateChanged()
    {
        using var coordinator = new DownloadCoordinator { MaxConcurrent = 3 };
        var raised = 0;
        coordinator.StateChanged += (_, _) => raised++;

        coordinator.MaxConcurrent = 3;

        raised.Should().Be(0);
    }

    [Fact]
    public void MaxConcurrent_NewValue_RaisesStateChanged()
    {
        using var coordinator = new DownloadCoordinator { MaxConcurrent = 3 };
        var raised = 0;
        coordinator.StateChanged += (_, _) => raised++;

        coordinator.MaxConcurrent = 5;

        coordinator.MaxConcurrent.Should().Be(5);
        raised.Should().Be(1);
    }

    [Fact]
    public async Task MaxConcurrent_LiveSwap_LeaksTheInFlightPermit_AndOverAdmits()
    {
        // KNOWN SHARP EDGE (the setter's own comment: "we accept the leak"). Raising the limit while a
        // download is in flight swaps in a brand-new semaphore with a FULL set of permits — it has no
        // knowledge of the still-running download holding a permit on the discarded instance. So the
        // total number of simultaneously-active downloads can exceed MaxConcurrent right after a resize.
        using var coordinator = new DownloadCoordinator { MaxConcurrent = 1 };
        var startedA = new TaskCompletionSource();
        var releaseA = new TaskCompletionSource();
        var startedB = new TaskCompletionSource();
        var releaseB = new TaskCompletionSource();
        var startedC = new TaskCompletionSource();
        var releaseC = new TaskCompletionSource();

        var a = coordinator.EnqueueAsync("A", Gated(startedA, releaseA.Task));
        await startedA.Task.WaitAsync(Timeout); // A holds the only permit of the original semaphore

        coordinator.MaxConcurrent = 2;          // swaps in a fresh SemaphoreSlim(2, 2)

        var b = coordinator.EnqueueAsync("B", Gated(startedB, releaseB.Task));
        var c = coordinator.EnqueueAsync("C", Gated(startedC, releaseC.Task));
        await Task.WhenAll(startedB.Task, startedC.Task).WaitAsync(Timeout);

        coordinator.Active.Should().HaveCount(3, "A (old semaphore) + B + C (new semaphore) all run — above the limit of 2");

        // Draining also exposes the second half of the bug: the finally releases into the *field*
        // semaphore (the new one), not the instance each task acquired from, so the surplus release
        // pushes the new semaphore past its max count.
        releaseA.SetResult();
        releaseB.SetResult();
        releaseC.SetResult();
        var drain = async () => await Task.WhenAll(a, b, c).WaitAsync(Timeout);
        await drain.Should().ThrowAsync<SemaphoreFullException>();
    }

    // ── Activity-log aggregation shim (real ActivityLogService) ──

    [Fact]
    public async Task ActivityLog_SingleActiveDownload_ShowsItsName()
    {
        var log = new ActivityLogService();
        using var coordinator = new DownloadCoordinator(log) { MaxConcurrent = 2 };
        var startedA = new TaskCompletionSource();
        var releaseA = new TaskCompletionSource();

        var a = coordinator.EnqueueAsync("Model-A", Gated(startedA, releaseA.Task));
        await startedA.Task.WaitAsync(Timeout);

        log.IsDownloadInProgress.Should().BeTrue();
        log.DownloadOperationName.Should().Be("Model-A");

        releaseA.SetResult();
        await a.WaitAsync(Timeout);
    }

    [Fact]
    public async Task ActivityLog_TwoActiveDownloads_ShowAggregateSummary()
    {
        var log = new ActivityLogService();
        using var coordinator = new DownloadCoordinator(log) { MaxConcurrent = 2 };
        var startedA = new TaskCompletionSource();
        var releaseA = new TaskCompletionSource();
        var startedB = new TaskCompletionSource();
        var releaseB = new TaskCompletionSource();

        var a = coordinator.EnqueueAsync("Model-A", Gated(startedA, releaseA.Task));
        await startedA.Task.WaitAsync(Timeout);
        var b = coordinator.EnqueueAsync("Model-B", Gated(startedB, releaseB.Task));
        await startedB.Task.WaitAsync(Timeout); // B's admission is the last activity-log update

        log.DownloadOperationName.Should().Be("2 Downloads in progress");

        releaseA.SetResult();
        releaseB.SetResult();
        await Task.WhenAll(a, b).WaitAsync(Timeout);
    }

    [Fact]
    public async Task ActivityLog_WhenAllDownloadsComplete_ClosesTheDownloadSlot()
    {
        var log = new ActivityLogService();
        using var coordinator = new DownloadCoordinator(log);

        await coordinator.EnqueueAsync("Model-A", (_, _) => Task.FromResult(true)).WaitAsync(Timeout);

        log.IsDownloadInProgress.Should().BeFalse();
        log.DownloadOperationName.Should().BeNull();
    }
}
