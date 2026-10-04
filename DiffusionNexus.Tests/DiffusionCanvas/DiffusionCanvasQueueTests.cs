using System.Runtime.CompilerServices;
using Avalonia.Media.Imaging;
using DiffusionNexus.UI.ViewModels.DiffusionCanvas;
using FluentAssertions;
using SkiaSharp;

namespace DiffusionNexus.Tests.DiffusionCanvas;

/// <summary>
/// The canvas on its queue (#598): Generate enqueues, a queued batch runs what was on screen at its own
/// press, the strip appends, and Cancel stops one batch of several. A second batch is queued from inside
/// the fake backend's run hook, which is where a user pressing Generate mid-run lands.
/// </summary>
public class DiffusionCanvasQueueTests
{
    private static DiffusionCanvasViewModel Canvas(FakeDiffusionBackend backend)
    {
        var vm = new DiffusionCanvasViewModel(backend)
        {
            PromptText = "first prompt",
            ScratchDirectory = CanvasScratch.NewDirectory(),
        };

        vm.BitmapDecoder = _ =>
        {
            var sentinel = (Bitmap)RuntimeHelpers.GetUninitializedObject(typeof(Bitmap));
            GC.SuppressFinalize(sentinel);
            return sentinel;
        };
        vm.OutputsWriter = (bytes, seed) => $"C:\\fake-outputs\\{seed}-{bytes.Length}.png";
        return vm;
    }

    [Fact]
    public async Task Generate_StaysEnabledWhileABatchRuns()
    {
        var backend = new FakeDiffusionBackend();
        var vm = Canvas(backend);
        bool? offeredMidBatch = null;
        backend.BeforeRun = _ => offeredMidBatch ??= vm.GenerateCommand.CanExecute(null);

        await vm.GenerateCommand.ExecuteAsync(null);

        offeredMidBatch.Should().BeTrue("a press while a batch runs queues another instead of being refused");
    }

    [Fact]
    public async Task ASecondGenerateWaitsItsTurnAndThenRuns()
    {
        var backend = new FakeDiffusionBackend();
        var vm = Canvas(backend);
        vm.BatchCount = 2;
        Task? second = null;
        int? queuedWhileFirstRan = null;
        backend.BeforeRun = run =>
        {
            if (run != 1)
                return;

            second = vm.GenerateCommand.ExecuteAsync(null);
            queuedWhileFirstRan = vm.Queue.QueuedCount;
        };

        await vm.GenerateCommand.ExecuteAsync(null);
        await second!;

        queuedWhileFirstRan.Should().Be(1);
        backend.RunCount.Should().Be(4);
        backend.MaxConcurrentRuns.Should().Be(1, "batches run one after another: the GPU holds one model");
        vm.IsGenerating.Should().BeFalse();
        vm.Queue.Batches.Should().BeEmpty();
    }

    [Fact]
    public async Task TheStripAppendsInsteadOfDiscardingWhatIsStaged()
    {
        var backend = new FakeDiffusionBackend();
        var vm = Canvas(backend);
        vm.BatchCount = 2;
        await vm.GenerateCommand.ExecuteAsync(null);
        var earlier = vm.Staging.Candidates.ToList();

        await vm.GenerateCommand.ExecuteAsync(null);

        vm.Staging.Candidates.Should().HaveCount(4);
        vm.Staging.Candidates.Take(2).Should().Equal(earlier);
        earlier.Should().NotContain(c => c.IsDisposed, "nothing unjudged is thrown away unasked");
    }

    [Fact]
    public async Task AQueuedBatchRunsWhatWasOnScreenAtItsOwnPress()
    {
        var backend = new FakeDiffusionBackend();
        var vm = Canvas(backend);
        vm.Box.SetSize(1024, 1024);
        vm.Box.SetPosition(0, 0);
        Task? second = null;
        backend.BeforeRun = run =>
        {
            if (run != 1)
                return;

            // The second press, with its own prompt, box and settings...
            vm.PromptText = "second prompt";
            vm.Box.SetSize(512, 768);
            vm.Box.SetPosition(2048, 2048);
            vm.Steps = 33;
            second = vm.GenerateCommand.ExecuteAsync(null);

            // ...and then the user moves on while it waits.
            vm.PromptText = "third idea, still being typed";
            vm.Box.SetSize(1280, 1280);
            vm.Box.SetPosition(4096, 0);
            vm.Steps = 5;
        };

        await vm.GenerateCommand.ExecuteAsync(null);
        await second!;

        backend.Requests.Should().HaveCount(2);
        backend.Requests[0].Prompt.Should().Be("first prompt");
        backend.Requests[0].Width.Should().Be(1024);
        var queued = backend.Requests[1];
        queued.Prompt.Should().Be("second prompt");
        queued.Width.Should().Be(512);
        queued.Height.Should().Be(768);
        queued.Steps.Should().Be(33);
        vm.Staging.Candidates[1].WorldRect.X.Should().Be(2048, "the result lands where the box was at the press");
    }

    [Fact]
    public async Task AQueuedBatchKeepsTheRegionItWasQueuedOver()
    {
        // Queued over empty canvas, so it is a text-to-image batch. A result accepted under that spot
        // while it waits must not turn it into image-to-image behind the user's back.
        using var file = new TempCanvasFile(1024, 1024, SKColors.White);
        var backend = new FakeDiffusionBackend();
        var vm = Canvas(backend);
        vm.Box.SetSize(1024, 1024);
        vm.Box.SetPosition(0, 0);
        Task? second = null;
        backend.BeforeRun = run =>
        {
            if (run != 1)
                return;

            second = vm.GenerateCommand.ExecuteAsync(null);
            vm.Frames.Add(file.AsFrame(0, 0, 1024, 1024));
        };

        await vm.GenerateCommand.ExecuteAsync(null);
        await second!;

        backend.Requests[1].InitImage.Should().BeNull();
    }

    [Fact]
    public async Task AQueuedBatchRunsOnTheModelItWasQueuedWith()
    {
        var backend = new FakeDiffusionBackend();
        var vm = Canvas(backend);
        Task? second = null;
        backend.BeforeRun = run =>
        {
            if (run != 1)
                return;

            second = vm.GenerateCommand.ExecuteAsync(null);
            vm.SelectedModel = null;
        };

        await vm.GenerateCommand.ExecuteAsync(null);
        await second!;

        backend.Requests.Should().HaveCount(2);
        backend.Requests[1].ModelKey.Should().Be(FakeDiffusionBackend.ModelKey);
    }

    [Fact]
    public async Task AWaitingBatchShowsItsSlotsWithoutMovingTheSelection()
    {
        var backend = new FakeDiffusionBackend();
        var vm = Canvas(backend);
        vm.BatchCount = 2;
        Task? second = null;
        int? slotsAfterSecondPress = null;
        StagedCandidateViewModel? selectedAfterSecondPress = null;
        backend.BeforeRun = run =>
        {
            if (run != 1)
                return;

            second = vm.GenerateCommand.ExecuteAsync(null);
            slotsAfterSecondPress = vm.Staging.Candidates.Count;
            selectedAfterSecondPress = vm.Staging.Current;
        };

        await vm.GenerateCommand.ExecuteAsync(null);
        await second!;

        slotsAfterSecondPress.Should().Be(4, "the strip shows the queue's shape at the press");
        selectedAfterSecondPress.Should().BeSameAs(vm.Staging.Candidates[0]);
    }

    [Fact]
    public async Task Cancel_StopsTheRunningBatchAndTheQueuedOneStillRuns()
    {
        var backend = new FakeDiffusionBackend();
        var vm = Canvas(backend);
        vm.BatchCount = 3;
        Task? second = null;
        backend.BeforeRun = run =>
        {
            if (run != 1)
                return;

            vm.PromptText = "second prompt";
            second = vm.GenerateCommand.ExecuteAsync(null);
            vm.CancelCommand.Execute(null);
        };

        await vm.GenerateCommand.ExecuteAsync(null);
        await second!;

        backend.Requests.Count(r => r.Prompt == "first prompt").Should().Be(1, "the cancel dropped the rest of batch 1");
        backend.Requests.Count(r => r.Prompt == "second prompt").Should().Be(3, "the queued batch is not the cancel's");
        vm.Staging.Candidates.Should().HaveCount(3);
        vm.Staging.Candidates.Should().OnlyContain(c => c.IsReady && c.Prompt == "second prompt");
    }

    [Fact]
    public async Task RemovingAQueuedBatchRemovesItsSlotsAndItNeverRuns()
    {
        var backend = new FakeDiffusionBackend();
        var vm = Canvas(backend);
        vm.BatchCount = 2;
        Task? second = null;
        backend.BeforeRun = run =>
        {
            if (run != 1)
                return;

            second = vm.GenerateCommand.ExecuteAsync(null);
            vm.Queue.Batches[1].RemoveCommand!.Execute(null);
        };

        await vm.GenerateCommand.ExecuteAsync(null);
        await second!;

        backend.RunCount.Should().Be(2);
        vm.Staging.Candidates.Should().HaveCount(2);
        vm.Staging.Candidates.Should().OnlyContain(c => c.IsReady);
    }

    [Fact]
    public async Task ClearQueue_LeavesTheRunningBatchAlone()
    {
        var backend = new FakeDiffusionBackend();
        var vm = Canvas(backend);
        var later = new List<Task>();
        backend.BeforeRun = run =>
        {
            if (run != 1)
                return;

            later.Add(vm.GenerateCommand.ExecuteAsync(null));
            later.Add(vm.GenerateCommand.ExecuteAsync(null));
            vm.Queue.ClearQueuedCommand.Execute(null);
        };

        await vm.GenerateCommand.ExecuteAsync(null);
        await Task.WhenAll(later);

        backend.RunCount.Should().Be(1);
        vm.Staging.Candidates.Should().ContainSingle().Which.IsReady.Should().BeTrue();
    }

    [Fact]
    public async Task TheRunningBatchsOwnRowCancelsIt()
    {
        var backend = new FakeDiffusionBackend();
        var vm = Canvas(backend);
        vm.BatchCount = 3;
        backend.BeforeRun = run =>
        {
            if (run == 1)
                vm.Queue.Batches[0].RemoveCommand!.Execute(null);
        };

        await vm.GenerateCommand.ExecuteAsync(null);

        backend.RunCount.Should().Be(1);
        vm.StatusText.Should().Be("Cancelled.");
        vm.Staging.Candidates.Should().BeEmpty("the strip is tidied the same way as with the Cancel button");
    }

    [Fact]
    public async Task ARefusedQueuedBatchRemovesOnlyItsOwnSlots()
    {
        // The second batch is queued over a result whose file is gone, so it is refused when it starts.
        var backend = new FakeDiffusionBackend();
        var vm = Canvas(backend);
        vm.Box.SetSize(1024, 1024);
        vm.Box.SetPosition(0, 0);
        Task? second = null;
        backend.BeforeRun = run =>
        {
            if (run != 1)
                return;

            vm.Frames.Add(new GenerationFrameViewModel
            {
                CanvasX = 0, CanvasY = 0, Width = 1024, Height = 1024,
                ImagePath = Path.Combine(Path.GetTempPath(), $"dn-missing-{Guid.NewGuid():N}.png"),
                State = GenerationFrameState.Completed,
            });
            second = vm.GenerateCommand.ExecuteAsync(null);
        };

        await vm.GenerateCommand.ExecuteAsync(null);
        await second!;

        backend.RunCount.Should().Be(1);
        vm.StatusText.Should().Contain("could not be read back");
        vm.Staging.Candidates.Should().ContainSingle("the first batch's result stays; the refused batch's slot goes")
            .Which.IsReady.Should().BeTrue();
    }

    [Fact]
    public async Task SteppingBackToCompareIsNotUndoneByTheNextImage()
    {
        // The fix-while-there in #598: RunBatchAsync used to reassign Staging.Current every iteration.
        var backend = new FakeDiffusionBackend();
        var vm = Canvas(backend);
        vm.BatchCount = 3;
        backend.BeforeRun = run =>
        {
            if (run == 2)
                vm.Staging.Current = vm.Staging.Candidates[0];
        };

        await vm.GenerateCommand.ExecuteAsync(null);

        vm.Staging.Current.Should().BeSameAs(vm.Staging.Candidates[0]);
    }

    [Fact]
    public async Task TheSelectionFollowsTheRunningImageWhileTheUserLeavesItAlone()
    {
        var backend = new FakeDiffusionBackend();
        var vm = Canvas(backend);
        vm.BatchCount = 3;

        await vm.GenerateCommand.ExecuteAsync(null);

        vm.Staging.Current.Should().BeSameAs(vm.Staging.Candidates[2]);
    }

    [Fact]
    public async Task ABatchWhoseSlotsWereAllDiscardedIsSkippedWithoutTouchingTheBackend()
    {
        var backend = new FakeDiffusionBackend();
        var vm = Canvas(backend);
        Task? second = null;
        var availabilityChecks = 0;
        backend.BeforeAvailabilityCheck = () => availabilityChecks++;
        backend.BeforeRun = run =>
        {
            if (run != 1)
                return;

            second = vm.GenerateCommand.ExecuteAsync(null);
            vm.Staging.Current = vm.Staging.Candidates[1];
            vm.Staging.DiscardCommand.Execute(null);
        };

        await vm.GenerateCommand.ExecuteAsync(null);
        await second!;

        backend.RunCount.Should().Be(1);
        availabilityChecks.Should().Be(1, "a batch with nothing left to make must not wake the backend");
    }

    [Fact]
    public async Task DiscardingEverySlotOfABatchThatIsStartingUpStopsIt()
    {
        // Review finding: only waiting batches were dropped for having no slot left. The running one
        // carried on through a start-up that can take two minutes on a cold engine, for no image.
        var backend = new FakeDiffusionBackend();
        var vm = Canvas(backend);
        vm.BatchCount = 2;
        backend.BeforeAvailabilityCheck = () => vm.Staging.DiscardAllCommand.Execute(null);

        await vm.GenerateCommand.ExecuteAsync(null);

        backend.RunCount.Should().Be(0, "nothing was left to make");
        vm.StatusText.Should().Be("Cancelled.");
        vm.IsGenerating.Should().BeFalse();
        vm.Staging.Candidates.Should().BeEmpty();
    }

    [Fact]
    public async Task ARefusedBatchIsNotReportedAsCancelled()
    {
        // The batch removes its own slots when it is refused; that must not read as the user
        // discarding them, or the reason for the refusal would be replaced by "Cancelled.".
        var backend = new FakeDiffusionBackend { IsAvailable = false };
        var vm = Canvas(backend);

        await vm.GenerateCommand.ExecuteAsync(null);

        vm.StatusText.Should().Be("Backend unavailable");
        vm.Staging.Candidates.Should().BeEmpty();
    }

    [Fact]
    public async Task BatchesQueuedBehindAnUnavailableBackendDoNotEachAskAgain()
    {
        // Review finding: every waiting batch repeated the availability probe, which on a dead engine
        // is a start attempt of up to two minutes each, for the same answer.
        var backend = new FakeDiffusionBackend { IsAvailable = false };
        var vm = Canvas(backend);
        var probes = 0;
        Task? second = null, third = null;
        backend.BeforeAvailabilityCheck = () =>
        {
            if (++probes != 1)
                return;

            second = vm.GenerateCommand.ExecuteAsync(null);
            third = vm.GenerateCommand.ExecuteAsync(null);
        };

        await vm.GenerateCommand.ExecuteAsync(null);
        await second!;
        await third!;

        probes.Should().Be(1);
        vm.Staging.Candidates.Should().BeEmpty();
        vm.StatusText.Should().Be("Backend unavailable");

        await vm.GenerateCommand.ExecuteAsync(null);
        probes.Should().Be(2, "a new press asks again: the user may have fixed it");
    }

    [Fact]
    public async Task APressRefusedWhileABatchRunsKeepsItsReasonReadable()
    {
        // Review finding: the reason went to the status line, which the running batch's progress
        // overwrites within a second.
        var backend = new FakeDiffusionBackend();
        var vm = Canvas(backend);
        backend.BeforeRun = run =>
        {
            if (run != 1)
                return;

            vm.PromptText = " ";
            vm.GenerateCommand.Execute(null);
            vm.PromptText = "first prompt";
        };

        await vm.GenerateCommand.ExecuteAsync(null);

        vm.BatchNotice.Should().Be("Please enter a prompt before generating.");
        vm.StatusText.Should().NotContain("enter a prompt", "the batch's own progress took the status line");

        await vm.GenerateCommand.ExecuteAsync(null);
        vm.BatchNotice.Should().BeNull("an accepted press clears it");
    }

    [Fact]
    public async Task DiscardingAWaitingBatchsSlotsTakesItOutOfTheQueueAtOnce()
    {
        // Review finding: the bar kept saying "1 batch queued (8 images)" and the list kept the batch
        // until the worker reached it.
        var backend = new FakeDiffusionBackend();
        var vm = Canvas(backend);
        Task? second = null;
        string? afterOneDiscard = null;
        int? queuedAfterAllDiscarded = null;
        backend.BeforeRun = run =>
        {
            if (run != 1)
                return;

            vm.BatchCount = 3;
            second = vm.GenerateCommand.ExecuteAsync(null);

            vm.Staging.Current = vm.Staging.Candidates[3];
            vm.Staging.DiscardCommand.Execute(null);
            afterOneDiscard = vm.Queue.QueueText;

            while (vm.Staging.Candidates.Count > 1)
            {
                vm.Staging.Current = vm.Staging.Candidates[^1];
                vm.Staging.DiscardCommand.Execute(null);
            }

            queuedAfterAllDiscarded = vm.Queue.QueuedCount;
        };

        await vm.GenerateCommand.ExecuteAsync(null);
        await second!;

        afterOneDiscard.Should().Be("Image 1/1 · 1 batch queued (2 images)");
        queuedAfterAllDiscarded.Should().Be(0);
        second!.IsCompleted.Should().BeTrue();
        backend.RunCount.Should().Be(1);
    }

    [Fact]
    public async Task RemovingAWaitingBatchLeavesTheSelectionWhereItWas()
    {
        var backend = new FakeDiffusionBackend();
        var vm = Canvas(backend);
        await vm.GenerateCommand.ExecuteAsync(null);
        var judged = vm.Staging.Candidates[0];
        Task? third = null;
        StagedCandidateViewModel? selectedAfterRemoval = null;
        backend.BeforeRun = run =>
        {
            if (run != 2)
                return;

            // The user goes back to the first result while batch 2 renders, queues a third batch and
            // then removes it again.
            vm.Staging.Current = judged;
            vm.BatchCount = 2;
            third = vm.GenerateCommand.ExecuteAsync(null);
            vm.Queue.Batches[1].RemoveCommand!.Execute(null);
            selectedAfterRemoval = vm.Staging.Current;
        };

        await vm.GenerateCommand.ExecuteAsync(null);
        await third!;

        selectedAfterRemoval.Should().BeSameAs(judged);
    }

    [Fact]
    public async Task CancellingTheRunningBatchLeavesTheSelectionOnAnEarlierResult()
    {
        var backend = new FakeDiffusionBackend();
        var vm = Canvas(backend);
        await vm.GenerateCommand.ExecuteAsync(null);
        var judged = vm.Staging.Candidates[0];
        vm.BatchCount = 3;
        backend.BeforeRun = run =>
        {
            if (run != 2)
                return;

            vm.Staging.Current = judged;
            vm.CancelCommand.Execute(null);
        };

        await vm.GenerateCommand.ExecuteAsync(null);

        vm.Staging.Current.Should().BeSameAs(judged);
        vm.Staging.Candidates.Should().ContainSingle();
    }

    [Fact]
    public async Task TheQueueReadoutIsLiveDuringABatchAndEmptyAfterIt()
    {
        var backend = new FakeDiffusionBackend();
        var vm = Canvas(backend);
        vm.BatchCount = 2;
        string? midBatch = null;
        backend.BeforeRun = run =>
        {
            if (run == 2)
                midBatch = vm.Queue.QueueText;
        };

        await vm.GenerateCommand.ExecuteAsync(null);

        midBatch.Should().Be("Image 2/2");
        vm.Queue.QueueText.Should().BeEmpty();
        vm.Queue.EtaText.Should().BeEmpty();
    }

    [Fact]
    public async Task Dispose_DropsQueuedBatchesAndCancelsTheRunningOne()
    {
        var backend = new FakeDiffusionBackend();
        var vm = Canvas(backend);
        vm.BatchCount = 2;
        Task? second = null;
        backend.BeforeRun = run =>
        {
            if (run != 1)
                return;

            second = vm.GenerateCommand.ExecuteAsync(null);
            vm.Dispose();
        };

        await vm.GenerateCommand.ExecuteAsync(null);
        await second!;

        backend.RunCount.Should().Be(1);
        vm.Queue.Batches.Should().BeEmpty();
        vm.Staging.Candidates.Should().BeEmpty();
    }
}
