using DiffusionNexus.Domain.Services;
using DiffusionNexus.Infrastructure.Services;
using FluentAssertions;

namespace DiffusionNexus.Tests.Infrastructure;

/// <summary>
/// Covers <see cref="DownloadCoordinator"/> (issue #443): semaphore-gated FIFO admission, per-task
/// cancellation, the activity-log aggregation shim, and the live semaphore swap in the
/// <c>MaxConcurrent</c> setter (issue #467: capture-at-acquisition so in-flight downloads always
/// wait/release on the same semaphore instance, even across a resize).
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
    public async Task MaxConcurrent_LiveSwap_RaisingLimit_OldInFlightTaskReleasesToOwnSemaphore_DrainsCleanly()
    {
        // Raising the limit while a download is in flight swaps in a brand-new semaphore. The
        // FIX: EnqueueAsync captures the semaphore instance into a local once, before WaitAsync,
        // and releases through that SAME local — so A's release goes back to the OLD semaphore
        // it acquired from, never into the new one. Total active count can still transiently
        // exceed the new limit while A (old semaphore) overlaps B/C (new semaphore) during the
        // swap window — that is accepted, documented behavior (see the MaxConcurrent XML doc),
        // not a leak: nothing throws and nothing is double-counted on any single instance.
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

        coordinator.Active.Should().HaveCount(3, "A (old semaphore) overlaps B + C (new semaphore) during the swap window — accepted, transient");

        releaseA.SetResult();
        releaseB.SetResult();
        releaseC.SetResult();

        // Fixed behavior: nothing throws, nothing leaks — each task's release lands on the
        // instance it acquired from.
        var results = await Task.WhenAll(a, b, c).WaitAsync(Timeout);
        results.Should().AllSatisfy(r => r.Should().BeTrue());

        // Post-swap: new admissions are governed strictly by the new limit (2 concurrent).
        var startedD = new TaskCompletionSource();
        var releaseD = new TaskCompletionSource();
        var startedE = new TaskCompletionSource();
        var releaseE = new TaskCompletionSource();
        var startedF = new TaskCompletionSource();
        var releaseF = new TaskCompletionSource();

        var d = coordinator.EnqueueAsync("D", Gated(startedD, releaseD.Task));
        var e = coordinator.EnqueueAsync("E", Gated(startedE, releaseE.Task));
        await Task.WhenAll(startedD.Task, startedE.Task).WaitAsync(Timeout);

        var f = coordinator.EnqueueAsync("F", Gated(startedF, releaseF.Task));
        coordinator.Active.Should().HaveCount(2, "the new limit of 2 governs steady-state admission after the swap");
        coordinator.Queued.Select(t => t.Name).Should().Equal("F");

        releaseD.SetResult();
        await startedF.Task.WaitAsync(Timeout); // F is admitted only once D frees a permit on the new semaphore
        releaseE.SetResult();
        releaseF.SetResult();
        (await Task.WhenAll(d, e, f).WaitAsync(Timeout)).Should().AllSatisfy(r => r.Should().BeTrue());
    }

    [Fact]
    public async Task MaxConcurrent_LiveSwap_LoweringLimit_OldInFlightTasksDrainCleanly_NewAdmissionsRespectLoweredLimit()
    {
        // Lowering the limit while two downloads are in flight swaps in a smaller semaphore that
        // starts fully available — it knows nothing about A/B's permits already held on the old
        // instance. The FIX ensures A and B still release into the OLD semaphore they acquired
        // from, so their completion must NOT free up a slot on the NEW (lowered) one; only C
        // releasing does that. This is the sharpest regression signal for the bug: with the old
        // field-based release, A/B's finally would erroneously admit D early (or overflow the
        // new semaphore, depending on scheduling).
        using var coordinator = new DownloadCoordinator { MaxConcurrent = 2 };
        var startedA = new TaskCompletionSource();
        var releaseA = new TaskCompletionSource();
        var startedB = new TaskCompletionSource();
        var releaseB = new TaskCompletionSource();

        var a = coordinator.EnqueueAsync("A", Gated(startedA, releaseA.Task));
        var b = coordinator.EnqueueAsync("B", Gated(startedB, releaseB.Task));
        await Task.WhenAll(startedA.Task, startedB.Task).WaitAsync(Timeout); // A + B hold both old permits

        coordinator.MaxConcurrent = 1;          // swaps in a fresh SemaphoreSlim(1, 1)

        var startedC = new TaskCompletionSource();
        var releaseC = new TaskCompletionSource();
        var startedD = new TaskCompletionSource();
        var releaseD = new TaskCompletionSource();

        var c = coordinator.EnqueueAsync("C", Gated(startedC, releaseC.Task));
        await startedC.Task.WaitAsync(Timeout); // C acquires the new semaphore's sole free permit

        var d = coordinator.EnqueueAsync("D", Gated(startedD, releaseD.Task));

        coordinator.Active.Should().HaveCount(3, "A+B (old semaphore) overlap C (new semaphore) — accepted, transient");
        // D must wait: the new semaphore has only 1 permit and C already holds it.
        coordinator.Queued.Select(t => t.Name).Should().Equal("D");

        // Drain the old in-flight downloads. Fixed behavior: their release goes back to the OLD
        // semaphore, never throws, and — critically — never frees a slot on the NEW one.
        releaseA.SetResult();
        releaseB.SetResult();
        (await Task.WhenAll(a, b).WaitAsync(Timeout)).Should().AllSatisfy(r => r.Should().BeTrue());

        startedD.Task.IsCompleted.Should().BeFalse("A/B releasing into the OLD semaphore must not admit D on the NEW one");

        releaseC.SetResult();
        (await c.WaitAsync(Timeout)).Should().BeTrue();
        await startedD.Task.WaitAsync(Timeout); // only now — after C frees the new semaphore's single permit

        releaseD.SetResult();
        (await d.WaitAsync(Timeout)).Should().BeTrue();
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
