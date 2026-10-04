using DiffusionNexus.UI.ViewModels.DiffusionCanvas;
using FluentAssertions;

namespace DiffusionNexus.Tests.DiffusionCanvas;

/// <summary>
/// The queue on its own (#598): order, cancel, removal, and the numbers the status bar shows. The batch
/// runner is a hand-held gate, so each test decides exactly when a batch ends.
/// </summary>
public class CanvasGenerationQueueTests
{
    /// <summary>A queue whose batches run until the test releases them, with a clock the test moves.</summary>
    private sealed class Harness
    {
        private readonly Dictionary<int, TaskCompletionSource> _gates = [];

        public Harness()
        {
            Queue = new CanvasGenerationQueue(Trace.Add, () => Now);
        }

        public CanvasGenerationQueue Queue { get; }

        public TimeSpan Now { get; set; }

        public List<string> Trace { get; } = [];

        public List<int> Started { get; } = [];

        public List<int> Dropped { get; } = [];

        public List<int> Cancelling { get; } = [];

        public Dictionary<int, CancellationToken> Tokens { get; } = [];

        public int? ThrowOn { get; set; }

        public CanvasQueuedBatchViewModel Batch(int number, int images = 1, int steps = 10, string paceKey = "") =>
            new(number, images, steps, $"prompt {number}", paceKey);

        public Task Enqueue(CanvasQueuedBatchViewModel batch, Func<int>? pendingImages = null) =>
            Queue.Enqueue(batch, new CanvasBatchWork(
                Run: token => RunAsync(batch, token),
                OnDropped: () => Dropped.Add(batch.Number),
                OnCancelling: () => Cancelling.Add(batch.Number),
                PendingImages: pendingImages));

        public void At(double seconds) => Now = TimeSpan.FromSeconds(seconds);

        /// <summary>Lets the running batch end.</summary>
        public void Finish(int number) => _gates[number].TrySetResult();

        private async Task RunAsync(CanvasQueuedBatchViewModel batch, CancellationToken token)
        {
            Started.Add(batch.Number);
            Tokens[batch.Number] = token;
            if (ThrowOn == batch.Number)
                throw new InvalidOperationException("the runner blew up");

            var gate = new TaskCompletionSource();
            _gates[batch.Number] = gate;
            using (token.Register(() => gate.TrySetResult()))
                await gate.Task;
        }
    }

    [Fact]
    public void Enqueue_StartsTheFirstBatchAtOnceAndKeepsTheRestWaiting()
    {
        var h = new Harness();

        h.Enqueue(h.Batch(1));
        h.Enqueue(h.Batch(2));

        h.Started.Should().Equal(1);
        h.Queue.IsBusy.Should().BeTrue();
        h.Queue.Running!.Number.Should().Be(1);
        h.Queue.QueuedCount.Should().Be(1);
        h.Queue.Batches.Select(b => b.IsRunning).Should().Equal(true, false);
    }

    [Fact]
    public async Task BatchesRunInTheOrderTheyWereAdded()
    {
        var h = new Harness();
        var first = h.Enqueue(h.Batch(1));
        var second = h.Enqueue(h.Batch(2));
        var third = h.Enqueue(h.Batch(3));

        h.Finish(1);
        await first;
        h.Started.Should().Equal(1, 2);
        h.Finish(2);
        await second;
        h.Finish(3);
        await third;

        h.Started.Should().Equal(1, 2, 3);
        h.Queue.IsBusy.Should().BeFalse();
        h.Queue.Batches.Should().BeEmpty();
    }

    [Fact]
    public async Task TheQueueIsIdleBeforeTheLastBatchsTaskCompletes()
    {
        // Whoever awaits a batch must see the queue already settled, or a Cancel button bound to IsBusy
        // is still live for a batch that is over.
        var h = new Harness();
        var task = h.Enqueue(h.Batch(1));
        bool? busyAtCompletion = null;
        var observer = task.ContinueWith(_ => busyAtCompletion = h.Queue.IsBusy, TaskContinuationOptions.ExecuteSynchronously);

        h.Finish(1);
        await observer;

        busyAtCompletion.Should().BeFalse();
    }

    [Fact]
    public async Task CancelRunning_StopsThatBatchAndTheNextOneStarts()
    {
        var h = new Harness();
        var first = h.Enqueue(h.Batch(1));
        h.Enqueue(h.Batch(2));

        var cancelled = h.Queue.CancelRunning();
        await first;

        cancelled!.Number.Should().Be(1);
        h.Cancelling.Should().Equal(1);
        h.Tokens[1].IsCancellationRequested.Should().BeTrue();
        h.Started.Should().Equal(1, 2);
        h.Tokens[2].IsCancellationRequested.Should().BeFalse("each batch has its own epoch");
        h.Queue.Running!.Number.Should().Be(2);
    }

    [Fact]
    public void CancelRunning_WithNothingRunningDoesNothing()
    {
        var h = new Harness();

        h.Queue.CancelRunning().Should().BeNull();
    }

    [Fact]
    public async Task ASecondCancelDoesNotHitTheNextBatch()
    {
        // The epoch invariant: never cancel without nulling. A cancelled source left installed would be
        // cancelled again while the next batch's own source stayed untouched, or the reverse.
        var h = new Harness();
        var first = h.Enqueue(h.Batch(1));
        h.Enqueue(h.Batch(2));

        h.Queue.CancelRunning();
        await first;
        h.Tokens[2].IsCancellationRequested.Should().BeFalse();

        h.Queue.CancelRunning();

        h.Tokens[2].IsCancellationRequested.Should().BeTrue();
        h.Cancelling.Should().Equal(1, 2);
    }

    [Fact]
    public async Task Remove_DropsAQueuedBatchWithoutRunningIt()
    {
        var h = new Harness();
        var first = h.Enqueue(h.Batch(1));
        var queued = h.Batch(2);
        var second = h.Enqueue(queued);

        h.Queue.Remove(queued);

        second.IsCompleted.Should().BeTrue("whoever awaits a removed batch must not wait forever");
        h.Dropped.Should().Equal(2);
        h.Finish(1);
        await first;
        h.Started.Should().Equal(1);
    }

    [Fact]
    public async Task Remove_OnTheRunningBatchCancelsItWithoutTheOwnersHelp()
    {
        // Review finding: this used to raise an event and rely on a subscriber to do the cancelling, so
        // the row's Cancel button did nothing for an owner that had not wired it.
        var h = new Harness();
        var running = h.Batch(1);
        var task = h.Enqueue(running);

        running.RemoveCommand!.Execute(null);
        await task;

        h.Tokens[1].IsCancellationRequested.Should().BeTrue();
        h.Cancelling.Should().Equal(1);
        h.Dropped.Should().BeEmpty("a cancelled batch ran; it was not dropped");
        h.Queue.IsBusy.Should().BeFalse();
    }

    [Fact]
    public void ClearQueued_RemovesEveryWaitingBatchAndLeavesTheRunningOne()
    {
        var h = new Harness();
        h.Queue.ClearQueuedCommand.CanExecute(null).Should().BeFalse();
        h.Enqueue(h.Batch(1));
        h.Enqueue(h.Batch(2));
        h.Enqueue(h.Batch(3));
        h.Queue.ClearQueuedCommand.CanExecute(null).Should().BeTrue();

        h.Queue.ClearQueuedCommand.Execute(null);

        h.Queue.Batches.Should().ContainSingle().Which.Number.Should().Be(1);
        h.Dropped.Should().Equal(2, 3);
        h.Queue.ClearQueuedCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task ABatchThatThrowsDoesNotStrandTheOnesBehindIt()
    {
        var h = new Harness { ThrowOn = 1 };

        var first = h.Enqueue(h.Batch(1));
        var second = h.Enqueue(h.Batch(2));
        await first;

        h.Started.Should().Equal(1, 2);
        h.Trace.Should().Contain(line => line.Contains("unhandled error") && line.Contains("blew up"));
        h.Finish(2);
        await second;
        h.Queue.IsBusy.Should().BeFalse();
    }

    [Fact]
    public async Task AThrowingListenerCannotWedgeTheQueue()
    {
        // Review finding: the worker is fire-and-forget. A handler that threw while the queue went idle
        // left the worker flag set with no worker behind it, and every later batch staged and never ran.
        var h = new Harness();
        var armed = true;
        h.Queue.PropertyChanged += (_, e) =>
        {
            if (armed && e.PropertyName == nameof(CanvasGenerationQueue.IsBusy) && !h.Queue.IsBusy)
                throw new InvalidOperationException("a view handler blew up");
        };
        var first = h.Enqueue(h.Batch(1));

        h.Finish(1);
        await first;
        armed = false;
        var second = h.Enqueue(h.Batch(2));

        h.Started.Should().Equal(1, 2);
        h.Finish(2);
        await second;
        h.Queue.IsBusy.Should().BeFalse();
    }

    [Fact]
    public async Task AThrowingListenerAtBatchStartDoesNotStrandTheBatchesBehindIt()
    {
        var h = new Harness();
        var throwOnce = true;
        h.Queue.PropertyChanged += (_, e) =>
        {
            if (throwOnce && e.PropertyName == nameof(CanvasGenerationQueue.IsBusy) && h.Queue.IsBusy)
            {
                throwOnce = false;
                throw new InvalidOperationException("a view handler blew up");
            }
        };

        var first = h.Enqueue(h.Batch(1));
        var second = h.Enqueue(h.Batch(2));

        // Review finding: the fault used to skip batch 1's run, and with it the runner's clean-up of
        // the batch's slots, which then stayed in the strip for good.
        h.Started.Should().Equal([1], "a listener's fault must not cost the batch its run");
        h.Finish(1);
        await first;
        h.Started.Should().Equal(1, 2);
        h.Finish(2);
        await second;
        h.Queue.Batches.Should().BeEmpty();
    }

    [Fact]
    public async Task AThrowingListenerAtEnqueueStillStartsTheWorker()
    {
        // Review finding: the readout was raised between setting the worker flag and starting the
        // worker. A listener that threw there left the flag set with nothing running.
        var h = new Harness();
        var armed = true;
        h.Queue.PropertyChanged += (_, e) =>
        {
            if (armed && e.PropertyName == nameof(CanvasGenerationQueue.QueueText))
                throw new InvalidOperationException("a view handler blew up");
        };

        var first = h.Enqueue(h.Batch(1));
        armed = false;

        h.Started.Should().Equal(1);
        h.Finish(1);
        await first;
        h.Queue.IsBusy.Should().BeFalse();
    }

    [Fact]
    public void AThrowingListenerDoesNotBreakOutOfSlotsChanged()
    {
        // It is called from the strip's own change event; a throw would stop a Discard all part-way.
        var h = new Harness();
        h.Enqueue(h.Batch(1));
        h.Queue.PropertyChanged += (_, _) => throw new InvalidOperationException("a view handler blew up");

        var act = h.Queue.SlotsChanged;

        act.Should().NotThrow();
    }

    [Fact]
    public async Task ACancelledBatchSaysSoUntilItHasUnwound()
    {
        // Review finding: the local backend cannot stop a sampling call, so a cancelled batch stays the
        // running one for a while. Its row kept saying Running with a live Cancel that did nothing.
        var h = new Harness();
        var batch = h.Batch(1);
        var gate = new TaskCompletionSource();
        var task = h.Queue.Enqueue(batch, new CanvasBatchWork(Run: _ => gate.Task));
        batch.RemoveCommand!.CanExecute(null).Should().BeTrue();

        h.Queue.CancelRunning();

        batch.IsCancelling.Should().BeTrue();
        batch.StateText.Should().Be("Cancelling…");
        batch.RemoveCommand.CanExecute(null).Should().BeFalse("a second Cancel has nothing left to stop");
        h.Queue.Running.Should().BeSameAs(batch, "the backend has not unwound yet");

        gate.SetResult();
        await task;
        h.Queue.Batches.Should().BeEmpty();
    }

    [Fact]
    public async Task EnqueueAfterTheQueueWentIdleStartsItAgain()
    {
        var h = new Harness();
        var first = h.Enqueue(h.Batch(1));
        h.Finish(1);
        await first;

        h.Enqueue(h.Batch(2));

        h.Started.Should().Equal(1, 2);
        h.Queue.IsBusy.Should().BeTrue();
    }

    [Fact]
    public void Shutdown_CancelsTheRunningBatchAndDropsTheRest()
    {
        var h = new Harness();
        h.Enqueue(h.Batch(1));
        var second = h.Enqueue(h.Batch(2));

        h.Queue.Shutdown();

        h.Tokens[1].IsCancellationRequested.Should().BeTrue();
        second.IsCompleted.Should().BeTrue();
        h.Started.Should().Equal(1);
    }

    [Fact]
    public void EveryTransitionIsTraced()
    {
        var h = new Harness();
        h.Enqueue(h.Batch(1));
        var queued = h.Batch(2);
        h.Enqueue(queued);
        h.Queue.Remove(queued);
        h.Finish(1);

        h.Trace.Should().Contain(l => l.Contains("#1 enqueued"));
        h.Trace.Should().Contain(l => l.Contains("#1 started"));
        h.Trace.Should().Contain(l => l.Contains("#2 enqueued"));
        h.Trace.Should().Contain(l => l.Contains("#2 removed"));
        h.Trace.Should().Contain(l => l.Contains("#1 finished"));
        h.Trace.Should().Contain(l => l.Contains("queue is empty"));
    }

    // ────────────────────────────── Discarded slots ──────────────────────────────

    [Fact]
    public void TheCountAndTheEtaLeaveOutSlotsTheUserDiscarded()
    {
        // Review finding: both used the size the batch was queued with.
        var h = new Harness();
        var left = 8;
        h.Enqueue(h.Batch(1, images: 1, steps: 10));
        h.Enqueue(h.Batch(2, images: 8, steps: 10), () => left);
        h.Queue.ImageStarted();
        h.Queue.ReportStep(5, 10, 1.0);
        h.Queue.QueueText.Should().Be("Image 1/1 · 1 batch queued (8 images)");
        h.Queue.EstimateRemaining().Should().Be(TimeSpan.FromSeconds(85));

        left = 3;
        h.Queue.SlotsChanged();

        h.Queue.QueueText.Should().Be("Image 1/1 · 1 batch queued (3 images)");
        h.Queue.EstimateRemaining().Should().Be(TimeSpan.FromSeconds(35));
    }

    [Fact]
    public void AWaitingBatchWithNoSlotLeftLeavesTheQueue()
    {
        var h = new Harness();
        var left = 2;
        h.Enqueue(h.Batch(1));
        var second = h.Enqueue(h.Batch(2, images: 2), () => left);

        left = 0;
        h.Queue.SlotsChanged();

        h.Queue.Batches.Should().ContainSingle().Which.Number.Should().Be(1);
        h.Dropped.Should().Equal(2);
        second.IsCompleted.Should().BeTrue();
        h.Queue.QueueText.Should().Be("Image 1/1");
    }

    [Fact]
    public void TheRunningBatchIsNeverDroppedForHavingNoSlotLeft()
    {
        // Its last image may be the one in flight; the worker ends it, not the slot count.
        var h = new Harness();
        h.Enqueue(h.Batch(1), () => 0);

        h.Queue.SlotsChanged();

        h.Queue.Running!.Number.Should().Be(1);
        h.Dropped.Should().BeEmpty();
    }

    [Fact]
    public void TheEtaLeavesOutDiscardedSlotsOfTheRunningBatch()
    {
        var h = new Harness();
        var left = 3;
        h.Enqueue(h.Batch(1, images: 4, steps: 10), () => left);
        h.Queue.ImageStarted();
        h.Queue.ReportStep(0, 10, 1.0);
        h.Queue.EstimateRemaining().Should().Be(TimeSpan.FromSeconds(40));

        left = 1;
        h.Queue.SlotsChanged();

        h.Queue.EstimateRemaining().Should().Be(TimeSpan.FromSeconds(20));
    }

    // ────────────────────────────── Readout ──────────────────────────────

    [Fact]
    public void TheReadoutIsEmptyWhileIdle()
    {
        var h = new Harness();

        h.Queue.QueueText.Should().BeEmpty();
        h.Queue.ThroughputText.Should().BeEmpty();
        h.Queue.EtaText.Should().BeEmpty();
    }

    [Fact]
    public void QueueText_NamesTheImageAndWhatWaits()
    {
        var h = new Harness();
        h.Enqueue(h.Batch(1, images: 4));
        h.Queue.QueueText.Should().Be("Image 1/4");

        h.Queue.ImageStarted();
        h.Queue.ImageFinished(succeeded: true);
        h.Enqueue(h.Batch(2, images: 3));
        h.Enqueue(h.Batch(3, images: 5));

        h.Queue.QueueText.Should().Be("Image 2/4 · 2 batches queued (8 images)");
    }

    [Fact]
    public void QueueText_UsesTheSingularForOneBatchOfOneImage()
    {
        var h = new Harness();
        h.Enqueue(h.Batch(1));
        h.Enqueue(h.Batch(2));

        h.Queue.QueueText.Should().Be("Image 1/1 · 1 batch queued (1 image)");
    }

    [Fact]
    public void Eta_IsUnknownUntilSomethingWasMeasured()
    {
        var h = new Harness();
        h.Enqueue(h.Batch(1, images: 2));
        h.Queue.ImageStarted();

        h.Queue.EtaText.Should().Be("ETA —", "a guess before the first measurement would be a made-up number");
        h.Queue.ThroughputText.Should().BeEmpty();
    }

    [Fact]
    public void Eta_FromTheStepRateCoversTheImageInFlightAndEverythingBehindIt()
    {
        var h = new Harness();
        h.Enqueue(h.Batch(1, images: 2, steps: 10));
        h.Enqueue(h.Batch(2, images: 1, steps: 20));
        h.Queue.ImageStarted();

        h.Queue.ReportStep(step: 4, totalSteps: 10, iterationsPerSecond: 2.0);

        // 6 steps left in this image, 10 for the batch's second image, 20 for the queued batch: 36 steps
        // at 2 it/s.
        h.Queue.EstimateRemaining().Should().Be(TimeSpan.FromSeconds(18));
        h.Queue.EtaText.Should().Be("ETA 0:18");
        h.Queue.ThroughputText.Should().Be("2.0 it/s");
    }

    [Fact]
    public void TheMeasuredPaceLeavesOutTheModelLoad()
    {
        // Review finding: the pace was measured from the image's start, and the first image of a cold
        // run spends a minute loading the model before its first step. That made every image behind it
        // look several times slower than it is.
        var h = new Harness();
        h.Enqueue(h.Batch(1, images: 3, steps: 10));
        h.Queue.ImageStarted();
        h.At(60);                                          // a minute of loading
        h.Queue.ReportStep(1, 10, 2.0);
        h.At(69);                                          // nine more steps and the decode: 1 s each
        h.Queue.ReportStep(10, 10, 2.0);
        h.Queue.ImageFinished(succeeded: true);
        h.Queue.ImageStarted();

        h.Queue.ReportStep(step: 5, totalSteps: 10, iterationsPerSecond: 2.0);

        // 5 steps left at 2 it/s = 2.5 s, plus one image not started: the 9 s measured and half a
        // second for the first step, which ran before its report = 9.5 s. Counting the load would have
        // made that image 69 s.
        h.Queue.EstimateRemaining().Should().Be(TimeSpan.FromSeconds(12));
        h.Queue.EtaText.Should().Be("ETA 0:12");
    }

    [Fact]
    public void Eta_ForImageToImageCountsTheStepsTheBackendReallySamples()
    {
        // Review finding: with an init image the local backend samples only steps × denoise and reports
        // that smaller total. The pace was per reported step but the ETA multiplied it by the steps
        // asked for, so 20 steps at denoise 0.4 came out 2.5 times too long.
        var h = new Harness();
        h.Enqueue(h.Batch(1, images: 3, steps: 20));
        h.Enqueue(h.Batch(2, images: 1, steps: 20));
        h.Queue.ImageStarted();
        h.Queue.ReportStep(1, 8, 2.0);
        h.At(7.5);                                         // with the first step's 0.5 s: 8 s an image
        h.Queue.ReportStep(8, 8, 2.0);
        h.Queue.ImageFinished(succeeded: true);
        h.Queue.ImageStarted();

        // Three images to go (this one, one not started, one queued), 8 sampled steps each at 1 s.
        h.Queue.EstimateRemaining().Should().Be(TimeSpan.FromSeconds(24));

        h.Queue.ReportStep(4, 8, 2.0);

        // 4 steps left at 2 it/s = 2 s, plus two images of 8 sampled steps at the measured 1 s.
        h.Queue.EstimateRemaining().Should().Be(TimeSpan.FromSeconds(18));
    }

    [Fact]
    public async Task TheSampledShareIsNotCarriedIntoTheNextBatch()
    {
        // Text to image after image to image samples every step again.
        var h = new Harness();
        var first = h.Enqueue(h.Batch(1, images: 1, steps: 20));
        h.Enqueue(h.Batch(2, images: 1, steps: 20));
        h.Queue.ImageStarted();
        h.Queue.ReportStep(1, 8, 2.0);
        h.At(7.5);
        h.Queue.ImageFinished(succeeded: true);
        h.Finish(1);
        await first;

        h.Queue.EstimateRemaining().Should().Be(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task SecondsPerImageFollowTheRunningBatchsSteps()
    {
        // Review finding: the s/image figure carried into a batch with twice the steps while the ETA
        // already counted per step, so the two readouts contradicted each other.
        var h = new Harness();
        var first = h.Enqueue(h.Batch(1, images: 1, steps: 20));
        h.Enqueue(h.Batch(2, images: 2, steps: 40));
        h.Queue.ImageStarted();
        h.At(10);
        h.Queue.ImageFinished(succeeded: true);
        h.Queue.ThroughputText.Should().Be("10 s/image");
        h.Finish(1);
        await first;

        h.Queue.ThroughputText.Should().Be("20 s/image");
        h.Queue.EstimateRemaining().Should().Be(TimeSpan.FromSeconds(40));
    }

    [Fact]
    public void TheMeasuredPaceCountsTheStepThatRanBeforeItsReport()
    {
        // Review finding: a step is reported after it has run. Measuring from the first report left
        // that step out, which for a one-step model is the whole sampling: only the decode was counted.
        var h = new Harness();
        h.Enqueue(h.Batch(1, images: 2, steps: 1));
        h.Queue.ImageStarted();
        h.At(2);                                           // the one step: 2 s
        h.Queue.ReportStep(1, 1, 0.5);
        h.At(3);                                           // the decode: 1 s
        h.Queue.ImageFinished(succeeded: true);
        h.Queue.ImageStarted();

        h.Queue.EstimateRemaining().Should().Be(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void Eta_WithoutAStepRateUsesTheLastMeasuredImage()
    {
        // The engine reports no steps: the pace is whole images.
        var h = new Harness();
        h.Enqueue(h.Batch(1, images: 3, steps: 8));
        h.Queue.ImageStarted();
        h.At(16);
        h.Queue.ImageFinished(succeeded: true);
        h.Queue.ThroughputText.Should().Be("16 s/image");

        h.Queue.ImageStarted();
        h.At(20);                                          // 4 s into the second image

        // 12 s left of the image in flight, 16 s for the one not started.
        h.Queue.EstimateRemaining().Should().Be(TimeSpan.FromSeconds(28));
    }

    [Fact]
    public void AColdFirstImageStopsCountingOnceAWarmOneHasFinished()
    {
        // Without steps the load cannot be told from the sampling, so the first image is slow by the
        // load. The latest image replaces it instead of being averaged with it.
        var h = new Harness();
        h.Enqueue(h.Batch(1, images: 3, steps: 8));
        h.Queue.ImageStarted();
        h.At(70);
        h.Queue.ImageFinished(succeeded: true);
        h.Queue.ImageStarted();
        h.At(80);
        h.Queue.ImageFinished(succeeded: true);

        h.Queue.ThroughputText.Should().Be("10 s/image");
        h.Queue.EstimateRemaining().Should().Be(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void Eta_CountsDownByTheClockAndTickAnnouncesIt()
    {
        // Review finding: without a step rate nothing re-raised the ETA while an image was in flight.
        var h = new Harness();
        h.Enqueue(h.Batch(1, images: 2, steps: 8));
        h.Queue.ImageStarted();
        h.At(16);
        h.Queue.ImageFinished(succeeded: true);
        h.Queue.ImageStarted();
        h.Queue.EtaText.Should().Be("ETA 0:16");
        var raised = new List<string?>();
        h.Queue.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        h.At(21);
        h.Queue.Tick();

        raised.Should().Equal(nameof(CanvasGenerationQueue.EtaText));
        h.Queue.EtaText.Should().Be("ETA 0:11");
    }

    [Fact]
    public void Tick_IsSilentWhileIdle()
    {
        var h = new Harness();
        var raised = 0;
        h.Queue.PropertyChanged += (_, _) => raised++;

        h.Queue.Tick();

        raised.Should().Be(0);
    }

    [Fact]
    public void Eta_NeverGoesNegativeWhenAnImageOverruns()
    {
        var h = new Harness();
        h.Enqueue(h.Batch(1, images: 2, steps: 8));
        h.Queue.ImageStarted();
        h.At(10);
        h.Queue.ImageFinished(succeeded: true);
        h.Queue.ImageStarted();
        h.At(60);

        h.Queue.EstimateRemaining().Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void AFailedImageDoesNotFeedTheMeasuredPace()
    {
        // A failure usually dies in a second; counting it would make the ETA wildly optimistic.
        var h = new Harness();
        h.Enqueue(h.Batch(1, images: 3, steps: 8));
        h.Queue.ImageStarted();
        h.At(1);
        h.Queue.ImageFinished(succeeded: false);
        h.Queue.ImageStarted();

        h.Queue.EtaText.Should().Be("ETA —");
        h.Queue.Running!.ImagesDone.Should().Be(1);
    }

    [Fact]
    public void Throughput_BelowOneStepPerSecondReadsAsSecondsPerStep()
    {
        var h = new Harness();
        h.Enqueue(h.Batch(1));
        h.Queue.ImageStarted();

        h.Queue.ReportStep(1, 10, 0.5);

        h.Queue.ThroughputText.Should().Be("2.0 s/it");
    }

    [Fact]
    public void Throughput_KeepsTheLastRateBetweenImages()
    {
        var h = new Harness();
        h.Enqueue(h.Batch(1, images: 2));
        h.Queue.ImageStarted();
        h.Queue.ReportStep(10, 10, 3.0);
        h.Queue.ImageFinished(succeeded: true);

        h.Queue.ThroughputText.Should().Be("3.0 it/s", "a readout that blinks between images reads as a stall");

        // Found in the GUI smoke: the next image starting wiped the rate until its first step arrived.
        h.Queue.ImageStarted();
        h.Queue.ThroughputText.Should().Be("3.0 it/s");
    }

    [Fact]
    public async Task ThePaceCarriesIntoABatchThatRunsTheSameWay()
    {
        var h = new Harness();
        var first = h.Enqueue(h.Batch(1, paceKey: "local|flux|1024x1024"));
        h.Enqueue(h.Batch(2, paceKey: "local|flux|1024x1024"));
        h.Queue.ImageStarted();
        h.Queue.ReportStep(10, 10, 3.0);
        h.Queue.ImageFinished(succeeded: true);

        h.Finish(1);
        await first;

        h.Queue.Running!.Number.Should().Be(2);
        h.Queue.ThroughputText.Should().Be("3.0 it/s");
    }

    [Fact]
    public async Task ThePaceIsDroppedForABatchWithAnotherModelSizeOrBackend()
    {
        // Review finding: batch 1's 8 it/s stayed on the bar, and in the ETA, while a 2048px batch on
        // another backend ran.
        var h = new Harness();
        var first = h.Enqueue(h.Batch(1, paceKey: "local|zimage|1024x1024"));
        h.Enqueue(h.Batch(2, images: 2, paceKey: "engine|krea2|2048x2048"));
        h.Queue.ImageStarted();
        h.Queue.ReportStep(1, 10, 8.0);
        h.At(2);
        h.Queue.ImageFinished(succeeded: true);

        h.Finish(1);
        await first;
        h.Queue.ImageStarted();

        h.Queue.ThroughputText.Should().BeEmpty();
        h.Queue.EtaText.Should().Be("ETA —");
    }

    [Fact]
    public async Task ThePaceIsDroppedWhenTheQueueGoesIdle()
    {
        var h = new Harness();
        var first = h.Enqueue(h.Batch(1));
        h.Queue.ImageStarted();
        h.Queue.ReportStep(10, 10, 3.0);
        h.Queue.ImageFinished(succeeded: true);
        h.Finish(1);
        await first;

        h.Enqueue(h.Batch(2));
        h.Queue.ImageStarted();

        h.Queue.ThroughputText.Should().BeEmpty("a new session of the queue must not show the last one's pace");
    }

    [Fact]
    public void Eta_FormatsHours()
    {
        var h = new Harness();
        h.Enqueue(h.Batch(1, images: 1, steps: 4000));
        h.Queue.ImageStarted();

        h.Queue.ReportStep(0, 4000, 1.0);

        h.Queue.EtaText.Should().Be("ETA 1:06:40");
    }

    [Fact]
    public void ASkippedImageCountsAsDoneWithoutBeingMeasured()
    {
        var h = new Harness();
        h.Enqueue(h.Batch(1, images: 2));

        h.Queue.ImageSkipped();

        h.Queue.Running!.ImagesDone.Should().Be(1);
        h.Queue.QueueText.Should().Be("Image 2/2");
        h.Queue.EtaText.Should().Be("ETA —");
    }

    [Fact]
    public void TheReadoutRaisesChangeNotifications()
    {
        var h = new Harness();
        h.Enqueue(h.Batch(1));
        h.Queue.ImageStarted();
        var raised = new List<string?>();
        h.Queue.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        h.Queue.ReportStep(1, 10, 2.0);

        raised.Should().Contain([
            nameof(CanvasGenerationQueue.QueueText),
            nameof(CanvasGenerationQueue.ThroughputText),
            nameof(CanvasGenerationQueue.EtaText)]);
    }
}
