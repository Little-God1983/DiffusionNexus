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
            Queue = new CanvasGenerationQueue(RunAsync, Trace.Add, () => Now);
        }

        public CanvasGenerationQueue Queue { get; }

        public TimeSpan Now { get; set; }

        public List<string> Trace { get; } = [];

        public List<int> Started { get; } = [];

        public Dictionary<int, CancellationToken> Tokens { get; } = [];

        public int? ThrowOn { get; set; }

        public CanvasQueuedBatchViewModel Batch(int number, int images = 1, int steps = 10) =>
            new(number, images, steps, $"prompt {number}");

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

        h.Queue.Enqueue(h.Batch(1));
        h.Queue.Enqueue(h.Batch(2));

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
        var first = h.Queue.Enqueue(h.Batch(1));
        var second = h.Queue.Enqueue(h.Batch(2));
        var third = h.Queue.Enqueue(h.Batch(3));

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
        var task = h.Queue.Enqueue(h.Batch(1));
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
        var first = h.Queue.Enqueue(h.Batch(1));
        h.Queue.Enqueue(h.Batch(2));

        var cancelled = h.Queue.CancelRunning();
        await first;

        cancelled!.Number.Should().Be(1);
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
        var first = h.Queue.Enqueue(h.Batch(1));
        h.Queue.Enqueue(h.Batch(2));

        h.Queue.CancelRunning();
        await first;
        h.Tokens[2].IsCancellationRequested.Should().BeFalse();

        h.Queue.CancelRunning();

        h.Tokens[2].IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public async Task Remove_DropsAQueuedBatchWithoutRunningIt()
    {
        var h = new Harness();
        var first = h.Queue.Enqueue(h.Batch(1));
        var queued = h.Batch(2);
        var second = h.Queue.Enqueue(queued);
        var dropped = new List<int>();
        h.Queue.BatchDropped += (_, b) => dropped.Add(b.Number);

        h.Queue.Remove(queued);

        second.IsCompleted.Should().BeTrue("whoever awaits a removed batch must not wait forever");
        dropped.Should().Equal(2);
        h.Finish(1);
        await first;
        h.Started.Should().Equal(1);
    }

    [Fact]
    public void Remove_OnTheRunningBatchAsksTheOwnerToCancel()
    {
        var h = new Harness();
        var running = h.Batch(1);
        h.Queue.Enqueue(running);
        var asked = 0;
        h.Queue.RunningCancelRequested += (_, _) => asked++;

        running.RemoveCommand!.Execute(null);

        asked.Should().Be(1);
        h.Queue.Batches.Should().Contain(running, "the owner cancels it through its own path");
    }

    [Fact]
    public void ClearQueued_RemovesEveryWaitingBatchAndLeavesTheRunningOne()
    {
        var h = new Harness();
        h.Queue.ClearQueuedCommand.CanExecute(null).Should().BeFalse();
        h.Queue.Enqueue(h.Batch(1));
        h.Queue.Enqueue(h.Batch(2));
        h.Queue.Enqueue(h.Batch(3));
        h.Queue.ClearQueuedCommand.CanExecute(null).Should().BeTrue();

        h.Queue.ClearQueuedCommand.Execute(null);

        h.Queue.Batches.Should().ContainSingle().Which.Number.Should().Be(1);
        h.Queue.ClearQueuedCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task ABatchThatThrowsDoesNotStrandTheOnesBehindIt()
    {
        var h = new Harness { ThrowOn = 1 };

        var first = h.Queue.Enqueue(h.Batch(1));
        var second = h.Queue.Enqueue(h.Batch(2));
        await first;

        h.Started.Should().Equal(1, 2);
        h.Trace.Should().Contain(line => line.Contains("unhandled error") && line.Contains("blew up"));
        h.Finish(2);
        await second;
        h.Queue.IsBusy.Should().BeFalse();
    }

    [Fact]
    public async Task EnqueueAfterTheQueueWentIdleStartsItAgain()
    {
        var h = new Harness();
        var first = h.Queue.Enqueue(h.Batch(1));
        h.Finish(1);
        await first;

        h.Queue.Enqueue(h.Batch(2));

        h.Started.Should().Equal(1, 2);
        h.Queue.IsBusy.Should().BeTrue();
    }

    [Fact]
    public void Shutdown_CancelsTheRunningBatchAndDropsTheRest()
    {
        var h = new Harness();
        h.Queue.Enqueue(h.Batch(1));
        var second = h.Queue.Enqueue(h.Batch(2));

        h.Queue.Shutdown();

        h.Tokens[1].IsCancellationRequested.Should().BeTrue();
        second.IsCompleted.Should().BeTrue();
        h.Started.Should().Equal(1);
    }

    [Fact]
    public void EveryTransitionIsTraced()
    {
        var h = new Harness();
        h.Queue.Enqueue(h.Batch(1));
        var queued = h.Batch(2);
        h.Queue.Enqueue(queued);
        h.Queue.Remove(queued);
        h.Finish(1);

        h.Trace.Should().Contain(l => l.Contains("#1 enqueued"));
        h.Trace.Should().Contain(l => l.Contains("#1 started"));
        h.Trace.Should().Contain(l => l.Contains("#2 enqueued"));
        h.Trace.Should().Contain(l => l.Contains("#2 removed"));
        h.Trace.Should().Contain(l => l.Contains("#1 finished"));
        h.Trace.Should().Contain(l => l.Contains("queue is empty"));
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
        h.Queue.Enqueue(h.Batch(1, images: 4));
        h.Queue.QueueText.Should().Be("Image 1/4");

        h.Queue.ImageStarted();
        h.Queue.ImageFinished(succeeded: true);
        h.Queue.Enqueue(h.Batch(2, images: 3));
        h.Queue.Enqueue(h.Batch(3, images: 5));

        h.Queue.QueueText.Should().Be("Image 2/4 · 2 batches queued (8 images)");
    }

    [Fact]
    public void QueueText_UsesTheSingularForOneBatchOfOneImage()
    {
        var h = new Harness();
        h.Queue.Enqueue(h.Batch(1));
        h.Queue.Enqueue(h.Batch(2));

        h.Queue.QueueText.Should().Be("Image 1/1 · 1 batch queued (1 image)");
    }

    [Fact]
    public void Eta_IsUnknownUntilSomethingWasMeasured()
    {
        var h = new Harness();
        h.Queue.Enqueue(h.Batch(1, images: 2));
        h.Queue.ImageStarted();

        h.Queue.EtaText.Should().Be("ETA —", "a guess before the first measurement would be a made-up number");
        h.Queue.ThroughputText.Should().BeEmpty();
    }

    [Fact]
    public void Eta_FromTheStepRateCoversTheImageInFlightAndEverythingBehindIt()
    {
        var h = new Harness();
        h.Queue.Enqueue(h.Batch(1, images: 2, steps: 10));
        h.Queue.Enqueue(h.Batch(2, images: 1, steps: 20));
        h.Queue.ImageStarted();

        h.Queue.ReportStep(step: 4, totalSteps: 10, iterationsPerSecond: 2.0);

        // 6 steps left in this image, 10 for the batch's second image, 20 for the queued batch: 36 steps
        // at 2 it/s.
        h.Queue.EstimateRemaining().Should().Be(TimeSpan.FromSeconds(18));
        h.Queue.EtaText.Should().Be("ETA 0:18");
        h.Queue.ThroughputText.Should().Be("2.0 it/s");
    }

    [Fact]
    public void Eta_PrefersTheMeasuredTimePerStepForImagesNotStarted()
    {
        // A step rate leaves out the load, encode and decode time; a finished image has measured them.
        var h = new Harness();
        h.Queue.Enqueue(h.Batch(1, images: 3, steps: 10));
        h.Queue.ImageStarted();
        h.Queue.ReportStep(10, 10, 2.0);
        h.Now = TimeSpan.FromSeconds(20);
        h.Queue.ImageFinished(succeeded: true);          // 20 s for 10 steps: 2 s per step, all in
        h.Queue.ImageStarted();

        h.Queue.ReportStep(step: 5, totalSteps: 10, iterationsPerSecond: 2.0);

        // 5 steps left at 2 it/s = 2.5 s, plus one image not started at the measured 2 s per step = 20 s.
        h.Queue.EstimateRemaining().Should().Be(TimeSpan.FromSeconds(22.5));
        h.Queue.EtaText.Should().Be("ETA 0:23");
    }

    [Fact]
    public void Eta_WithoutAStepRateUsesTheMeasuredImages()
    {
        // The engine reports no steps: the pace is whole images.
        var h = new Harness();
        h.Queue.Enqueue(h.Batch(1, images: 3, steps: 8));
        h.Queue.ImageStarted();
        h.Now = TimeSpan.FromSeconds(16);
        h.Queue.ImageFinished(succeeded: true);
        h.Queue.ThroughputText.Should().Be("16 s/image");

        h.Queue.ImageStarted();
        h.Now = TimeSpan.FromSeconds(20);                // 4 s into the second image

        // 12 s left of the image in flight, 16 s for the one not started.
        h.Queue.EstimateRemaining().Should().Be(TimeSpan.FromSeconds(28));
    }

    [Fact]
    public void Eta_NeverGoesNegativeWhenAnImageOverruns()
    {
        var h = new Harness();
        h.Queue.Enqueue(h.Batch(1, images: 2, steps: 8));
        h.Queue.ImageStarted();
        h.Now = TimeSpan.FromSeconds(10);
        h.Queue.ImageFinished(succeeded: true);
        h.Queue.ImageStarted();
        h.Now = TimeSpan.FromSeconds(60);

        h.Queue.EstimateRemaining().Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void AFailedImageDoesNotFeedTheMeasuredPace()
    {
        // A failure usually dies in a second; counting it would make the ETA wildly optimistic.
        var h = new Harness();
        h.Queue.Enqueue(h.Batch(1, images: 3, steps: 8));
        h.Queue.ImageStarted();
        h.Now = TimeSpan.FromSeconds(1);
        h.Queue.ImageFinished(succeeded: false);
        h.Queue.ImageStarted();

        h.Queue.EtaText.Should().Be("ETA —");
        h.Queue.Running!.ImagesDone.Should().Be(1);
    }

    [Fact]
    public void Throughput_BelowOneStepPerSecondReadsAsSecondsPerStep()
    {
        var h = new Harness();
        h.Queue.Enqueue(h.Batch(1));
        h.Queue.ImageStarted();

        h.Queue.ReportStep(1, 10, 0.5);

        h.Queue.ThroughputText.Should().Be("2.0 s/it");
    }

    [Fact]
    public void Throughput_KeepsTheLastRateBetweenImages()
    {
        var h = new Harness();
        h.Queue.Enqueue(h.Batch(1, images: 2));
        h.Queue.ImageStarted();
        h.Queue.ReportStep(10, 10, 3.0);
        h.Queue.ImageFinished(succeeded: true);

        h.Queue.ThroughputText.Should().Be("3.0 it/s", "a readout that blinks between images reads as a stall");

        // Found in the GUI smoke: the next image starting wiped the rate until its first step arrived.
        h.Queue.ImageStarted();
        h.Queue.ThroughputText.Should().Be("3.0 it/s");
    }

    [Fact]
    public async Task Throughput_KeepsTheLastRateIntoTheNextBatchAndDropsItWhenIdle()
    {
        var h = new Harness();
        var first = h.Queue.Enqueue(h.Batch(1));
        var second = h.Queue.Enqueue(h.Batch(2));
        h.Queue.ImageStarted();
        h.Queue.ReportStep(10, 10, 3.0);
        h.Queue.ImageFinished(succeeded: true);

        h.Finish(1);
        await first;
        h.Queue.ThroughputText.Should().Be("3.0 it/s");

        h.Finish(2);
        await second;
        h.Queue.Enqueue(h.Batch(3));
        h.Queue.ImageStarted();
        h.Queue.ThroughputText.Should().BeEmpty("a new session of the queue must not show the last one's pace");
    }

    [Fact]
    public void Eta_FormatsHours()
    {
        var h = new Harness();
        h.Queue.Enqueue(h.Batch(1, images: 1, steps: 4000));
        h.Queue.ImageStarted();

        h.Queue.ReportStep(0, 4000, 1.0);

        h.Queue.EtaText.Should().Be("ETA 1:06:40");
    }

    [Fact]
    public void ASkippedImageCountsAsDoneWithoutBeingMeasured()
    {
        var h = new Harness();
        h.Queue.Enqueue(h.Batch(1, images: 2));

        h.Queue.ImageSkipped();

        h.Queue.Running!.ImagesDone.Should().Be(1);
        h.Queue.QueueText.Should().Be("Image 2/2");
        h.Queue.EtaText.Should().Be("ETA —");
    }

    [Fact]
    public void TheReadoutRaisesChangeNotifications()
    {
        var h = new Harness();
        h.Queue.Enqueue(h.Batch(1));
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
