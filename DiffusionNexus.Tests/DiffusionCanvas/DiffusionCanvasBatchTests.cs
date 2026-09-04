using System.Runtime.CompilerServices;
using Avalonia.Media.Imaging;
using SkiaSharp;
using DiffusionNexus.Inference.Abstractions;
using DiffusionNexus.Inference.Models;
using DiffusionNexus.UI.ViewModels.DiffusionCanvas;
using FluentAssertions;

namespace DiffusionNexus.Tests.DiffusionCanvas;

/// <summary>
/// The batch runner and its cancel. Nothing in either test project faked
/// <see cref="IDiffusionBackend.GenerateAsync"/>'s async stream before — Moq cannot conveniently return
/// an async iterator — so this file hand-rolls the fake, following the repo's FakeDownloader convention.
/// </summary>
public class DiffusionCanvasBatchTests
{
    private static DiffusionCanvasViewModel Canvas(FakeDiffusionBackend backend)
    {
        var vm = new DiffusionCanvasViewModel(backend) { PromptText = "a lighthouse at dusk" };
        vm.SelectedModel.Should().NotBeNull("the engine catalog populates the dropdown on selection");

        // There is no Avalonia platform in this project, so a real Bitmap cannot be decoded. The view
        // model exposes its decoder as a test seam; the stand-in is the repo's uninitialised-instance
        // sentinel, used purely as an opaque non-null value that is never dereferenced.
        vm.BitmapDecoder = _ =>
        {
            var sentinel = (Bitmap)RuntimeHelpers.GetUninitializedObject(typeof(Bitmap));
            GC.SuppressFinalize(sentinel);
            return sentinel;
        };

        // Accepting a candidate writes a real PNG next to the test assembly. Stub the writer (the same
        // seam convention as BitmapDecoder) so the suite leaves no files behind and can still assert
        // that the accepted raster carries the path the writer returned.
        vm.OutputsWriter = (bytes, seed) => $"C:\\fake-outputs\\{seed}-{bytes.Length}.png";

        return vm;
    }

    [Fact]
    public async Task Generate_StagesOneCandidatePerBatchItemAndRunsThemSequentially()
    {
        var backend = new FakeDiffusionBackend();
        var vm = Canvas(backend);
        vm.BatchCount = 3;

        await vm.GenerateCommand.ExecuteAsync(null);

        backend.MaxConcurrentRuns.Should().Be(1, "the engine keeps a single model resident");
        backend.RunCount.Should().Be(3);
        vm.Staging.Candidates.Should().HaveCount(3);
        vm.Staging.Candidates.Should().OnlyContain(c => c.State == StagedCandidateState.Ready);
    }

    [Fact]
    public async Task Generate_LeavesTheCanvasUntouchedUntilACandidateIsAccepted()
    {
        var backend = new FakeDiffusionBackend();
        var vm = Canvas(backend);
        vm.BatchCount = 2;

        await vm.GenerateCommand.ExecuteAsync(null);

        vm.Frames.Should().BeEmpty("nothing touches the canvas unasked");

        vm.Staging.AcceptCommand.Execute(null);

        vm.Frames.Should().ContainSingle();
        vm.Staging.Candidates.Should().ContainSingle("one of the two candidates was accepted");
    }

    [Fact]
    public async Task AcceptedCandidateLandsAtTheBoxAsItWasWhenTheBatchStarted()
    {
        var backend = new FakeDiffusionBackend();
        var vm = Canvas(backend);
        vm.Box.SetSize(768, 512);
        vm.Box.SetPosition(1024, 2048);

        await vm.GenerateCommand.ExecuteAsync(null);

        // Moving the box after the batch must not retarget a result that was already generated.
        vm.Box.SetPosition(0, 0);
        vm.Staging.AcceptCommand.Execute(null);

        var frame = vm.Frames.Should().ContainSingle().Subject;
        frame.CanvasX.Should().Be(1024);
        frame.CanvasY.Should().Be(2048);
        frame.Width.Should().Be(768);
        frame.Height.Should().Be(512);
    }

    [Fact]
    public async Task Generate_SendsTheBoxSizeAsTheLatentSize()
    {
        var backend = new FakeDiffusionBackend();
        var vm = Canvas(backend);
        vm.Box.SetSize(1280, 768);

        await vm.GenerateCommand.ExecuteAsync(null);

        backend.LastRequest!.Width.Should().Be(1280);
        backend.LastRequest.Height.Should().Be(768);
    }

    [Fact]
    public async Task Generate_OverEmptyCanvasSendsNoInitImage()
    {
        var backend = new FakeDiffusionBackend();
        var vm = Canvas(backend);

        await vm.GenerateCommand.ExecuteAsync(null);

        backend.LastRequest!.InitImage.Should().BeNull("an empty region is a plain text-to-image run");
        vm.RegionModeText.Should().Contain("Text to image");
        vm.IsRegionOccupied.Should().BeFalse();
    }

    [Fact]
    public async Task Generate_OverAnAcceptedResultSendsItAsTheInitImage()
    {
        using var canvas = new TempCanvasFile(1024, 1024, SKColors.White);
        var backend = new FakeDiffusionBackend();
        var vm = Canvas(backend);
        vm.DenoiseStrength = 0.42;
        vm.Box.SetSize(1024, 1024);
        vm.Box.SetPosition(0, 0);
        vm.Frames.Add(canvas.AsFrame(0, 0, 1024, 1024));

        await vm.GenerateCommand.ExecuteAsync(null);

        var init = backend.LastRequest!.InitImage;
        init.Should().NotBeNull("the box sits on an accepted result, so this is an image-to-image run");
        init!.Strength.Should().BeApproximately(0.42f, 0.0001f, "the denoise slider drives the strength");
        backend.InitImageBytesAtCallTime.Should().NotBeNull("the scratch file must exist while the backend reads it");
        using var sent = SKBitmap.Decode(backend.InitImageBytesAtCallTime);
        sent.Should().NotBeNull();
        sent!.Width.Should().Be(1024);
        sent.GetPixel(512, 512).Red.Should().BeGreaterThan(200, "the accepted result's own pixels were composited");
    }

    [Fact]
    public async Task Generate_DeletesTheRegionScratchFileAfterTheBatch()
    {
        using var canvas = new TempCanvasFile(512, 512, SKColors.White);
        var backend = new FakeDiffusionBackend();
        var vm = Canvas(backend);
        vm.Box.SetSize(512, 512);
        vm.Box.SetPosition(0, 0);
        vm.Frames.Add(canvas.AsFrame(0, 0, 512, 512));

        await vm.GenerateCommand.ExecuteAsync(null);

        var scratch = backend.LastRequest!.InitImage!.FilePath;
        File.Exists(scratch).Should().BeFalse("the scratch PNG is cleaned up in the generate finally");
    }

    [Fact]
    public async Task Generate_PartiallyCoveredRegionStillRunsAsImageToImage()
    {
        using var canvas = new TempCanvasFile(512, 512, SKColors.White);
        var backend = new FakeDiffusionBackend();
        var vm = Canvas(backend);
        vm.Box.SetSize(1024, 512);
        vm.Box.SetPosition(0, 0);
        // The raster covers only the left half of the box.
        vm.Frames.Add(canvas.AsFrame(0, 0, 512, 512));

        await vm.GenerateCommand.ExecuteAsync(null);

        backend.LastRequest!.InitImage.Should().NotBeNull();
        using var sent = SKBitmap.Decode(backend.InitImageBytesAtCallTime);
        sent!.GetPixel(100, 256).Red.Should().BeGreaterThan(200, "the covered half carries real pixels");
        // Without mask support the uncovered half is flattened onto neutral grey rather than left clear.
        sent.GetPixel(900, 256).Red.Should().BeCloseTo(0x80, 4);
        sent.GetPixel(900, 256).Alpha.Should().Be(255);
    }

    [Fact]
    public async Task AcceptedCandidateCarriesTheSavedFilePath()
    {
        var backend = new FakeDiffusionBackend();
        var vm = Canvas(backend);

        await vm.GenerateCommand.ExecuteAsync(null);
        vm.Staging.AcceptCommand.Execute(null);

        // The path is what CanvasRegionCompositor reads back when a later box overlaps this raster, so
        // a frame accepted without one contributes nothing to a subsequent image-to-image run.
        vm.Frames.Should().ContainSingle().Which.ImagePath.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Generate_GivesEachBatchItemItsOwnSeedWhenTheSeedIsPinned()
    {
        var backend = new FakeDiffusionBackend();
        var vm = Canvas(backend);
        vm.UseRandomSeed = false;
        vm.Seed = 5000;
        vm.BatchCount = 3;

        await vm.GenerateCommand.ExecuteAsync(null);

        // A fixed seed reused across a batch would return three identical images.
        backend.RequestedSeeds.Should().Equal(new long?[] { 5000, 5001, 5002 });
    }

    [Fact]
    public async Task RegionModeTracksWhereTheBoxSits()
    {
        var backend = new FakeDiffusionBackend();
        var vm = Canvas(backend);
        vm.Box.SetSize(1024, 1024);
        vm.Box.SetPosition(0, 0);

        await vm.GenerateCommand.ExecuteAsync(null);
        vm.Staging.AcceptCommand.Execute(null);

        vm.IsRegionOccupied.Should().BeTrue("the box still sits on the result it just produced");
        vm.RegionModeText.Should().Contain("Image to image");

        vm.Box.SetPosition(4096, 4096);

        vm.IsRegionOccupied.Should().BeFalse();
        vm.RegionModeText.Should().Contain("Text to image");
    }

    [Fact]
    public async Task Cancel_DuringBackendResolutionStopsTheRunBeforeItStarts()
    {
        // Pre-flight is the longest part of a cold engine run -- EnsureRunningAsync spawns python and
        // polls readiness for up to two minutes -- and Cancel is enabled for all of it. The run epoch
        // therefore has to exist before the first await, not after the resolve.
        var backend = new FakeDiffusionBackend();
        var vm = Canvas(backend);
        vm.BatchCount = 3;
        backend.BeforeAvailabilityCheck = () => vm.CancelCommand.Execute(null);

        await vm.GenerateCommand.ExecuteAsync(null);

        backend.RunCount.Should().Be(0, "nothing should have been generated");
        vm.Staging.Candidates.Should().BeEmpty("no slots are staged for a run that never started");
        vm.StatusText.Should().Be("Cancelled.");
        vm.IsGenerating.Should().BeFalse();
    }

    [Fact]
    public async Task Cancel_DuringPreflightIsObservedByTheBackend()
    {
        var backend = new FakeDiffusionBackend();
        var vm = Canvas(backend);
        backend.BeforeAvailabilityCheck = () => vm.CancelCommand.Execute(null);

        await vm.GenerateCommand.ExecuteAsync(null);

        backend.AvailabilityTokenWasCancellable.Should().BeTrue(
            "the pre-flight token must be the run epoch's, so a long engine start can be aborted");
    }

    [Fact]
    public async Task Cancel_StopsTheRestOfTheBatch()
    {
        var backend = new FakeDiffusionBackend();
        var vm = Canvas(backend);
        vm.BatchCount = 5;

        // Cancel from inside the second run, the way a user clicking Cancel mid-batch does.
        backend.BeforeRun = run =>
        {
            if (run == 2)
                vm.CancelCommand.Execute(null);
        };

        await vm.GenerateCommand.ExecuteAsync(null);

        backend.RunCount.Should().BeLessThan(5, "the queued remainder is dropped");
        vm.StatusText.Should().Be("Cancelled.");
        // Reported from the GUI smoke: cancelled slots used to linger as blank tiles in the strip.
        vm.Staging.Candidates.Should().NotContain(c => c.IsPending, "no slot is left spinning");
        vm.Staging.Candidates.Should().NotContain(c => c.State == StagedCandidateState.Cancelled,
            "a slot that can never hold an image has no business in the strip");
        vm.Staging.Candidates.Should().OnlyContain(c => c.IsReady, "the one finished result survives");
    }

    [Fact]
    public async Task Cancel_IsReportedAsCancelledRatherThanFailed()
    {
        var backend = new FakeDiffusionBackend();
        var vm = Canvas(backend);
        vm.BatchCount = 3;
        backend.BeforeRun = run => { if (run == 1) vm.CancelCommand.Execute(null); };

        await vm.GenerateCommand.ExecuteAsync(null);

        vm.StatusText.Should().NotContain("Error");
        vm.Staging.Candidates.Should().NotContain(c => c.State == StagedCandidateState.Failed);
    }

    [Fact]
    public async Task GenerateAfterACancelRunsOnAFreshEpoch()
    {
        var backend = new FakeDiffusionBackend();
        var vm = Canvas(backend);
        vm.BatchCount = 2;
        backend.BeforeRun = run => { if (run == 1) vm.CancelCommand.Execute(null); };

        await vm.GenerateCommand.ExecuteAsync(null);
        var cancelledRuns = backend.RunCount;

        // The run-epoch invariant: cancelling nulls the source, so the next Generate must not join a
        // dead token and abort instantly.
        backend.BeforeRun = null;
        await vm.GenerateCommand.ExecuteAsync(null);

        backend.RunCount.Should().Be(cancelledRuns + 2);
        vm.Staging.Candidates.Should().HaveCount(2);
        vm.Staging.Candidates.Should().OnlyContain(c => c.State == StagedCandidateState.Ready);
    }

    [Fact]
    public async Task Cancel_IsOfferedWhileABatchIsRunningAndNotBefore()
    {
        var backend = new FakeDiffusionBackend();
        var vm = Canvas(backend);
        bool? offeredMidBatch = null;
        backend.BeforeRun = _ => offeredMidBatch ??= vm.CancelCommand.CanExecute(null);

        vm.CancelCommand.CanExecute(null).Should().BeFalse("nothing is running yet");

        await vm.GenerateCommand.ExecuteAsync(null);

        offeredMidBatch.Should().BeTrue("the button has to be live while there is something to cancel");
        vm.CancelCommand.CanExecute(null).Should().BeFalse("the batch is over");
    }

    [Fact]
    public async Task Cancel_WithNothingRunningIsHarmless()
    {
        var backend = new FakeDiffusionBackend();
        var vm = Canvas(backend);

        vm.CancelCommand.Execute(null);
        await vm.GenerateCommand.ExecuteAsync(null);

        backend.RunCount.Should().Be(1, "a stray cancel must not poison the next batch");
        vm.Staging.Candidates.Should().OnlyContain(c => c.State == StagedCandidateState.Ready);
    }

    [Fact]
    public async Task DiscardingTheStripMidBatchDoesNotResurrectDisposedCandidates()
    {
        var backend = new FakeDiffusionBackend();
        var vm = Canvas(backend);
        vm.BatchCount = 4;
        backend.BeforeRun = run =>
        {
            if (run == 1)
                vm.Staging.DiscardAllCommand.Execute(null);
        };

        await vm.GenerateCommand.ExecuteAsync(null);

        vm.Staging.Candidates.Should().BeEmpty("the user threw the whole strip away");
        vm.Staging.Current.Should().BeNull("a disposed candidate must never come back as the preview");
        vm.Staging.CurrentImage.Should().BeNull();
        vm.Frames.Should().BeEmpty();
    }

    [Fact]
    public async Task ResultsArrivingForADiscardedCandidateAreDropped()
    {
        var backend = new FakeDiffusionBackend();
        var vm = Canvas(backend);
        vm.BatchCount = 1;
        backend.BeforeRun = _ => vm.Staging.DiscardAllCommand.Execute(null);

        await vm.GenerateCommand.ExecuteAsync(null);

        // Writing a freshly decoded bitmap into a detached, disposed candidate leaks it: nothing holds
        // that candidate any more, so nothing will ever dispose it again.
        vm.Staging.Candidates.Should().BeEmpty();
        vm.Frames.Should().BeEmpty();
    }

    [Fact]
    public async Task AFailedCandidateDoesNotAbortTheRestOfTheBatch()
    {
        var backend = new FakeDiffusionBackend { FailOnRun = 1 };
        var vm = Canvas(backend);
        vm.BatchCount = 3;

        await vm.GenerateCommand.ExecuteAsync(null);

        backend.RunCount.Should().Be(3);
        vm.Staging.Candidates[0].State.Should().Be(StagedCandidateState.Failed);
        vm.Staging.Candidates.Skip(1).Should().OnlyContain(c => c.State == StagedCandidateState.Ready);
    }

    [Fact]
    public async Task BackendReportedFailureBecomesAFailedCandidateNotAThrow()
    {
        var backend = new FakeDiffusionBackend { ReportFailureMessageOnRun = 1 };
        var vm = Canvas(backend);

        await vm.GenerateCommand.ExecuteAsync(null);

        var candidate = vm.Staging.Candidates.Should().ContainSingle().Subject;
        candidate.State.Should().Be(StagedCandidateState.Failed);
        candidate.StatusText.Should().Contain("engine said no");
    }

    [Fact]
    public async Task Generate_SnapsTheBoxOntoTheSelectedModelsLattice()
    {
        var backend = new FakeDiffusionBackend { DimensionAlignment = 128 };
        var vm = Canvas(backend);
        vm.Box.SetSize(1088, 1024);

        await vm.GenerateCommand.ExecuteAsync(null);

        vm.Box.Alignment.Should().Be(128);
        vm.Box.Width.Should().Be(1024, "1088 is not a multiple of 128, so the box is re-snapped");
        backend.LastRequest!.Width.Should().Be(1024);
    }

    [Fact]
    public async Task Generate_NormalisesTheBoxEvenWhenTheAlignmentIsUnchanged()
    {
        // Generate assigns the model's alignment on every run precisely so the box is normalised before
        // the request is built. The backend validates lazily -- inside the caller's await foreach, long
        // after a candidate slot exists -- so a box that slipped off the lattice must be repaired here
        // rather than discovered there. SetSize always snaps, so the only way to seed a misaligned box
        // is through a lattice that accepts the size: 1000 is on a 100-lattice and off a 64-lattice.
        var backend = new FakeDiffusionBackend { DimensionAlignment = 64 };
        var vm = Canvas(backend);
        vm.Box.Alignment = 100;
        vm.Box.SetSize(1000, 1000);
        vm.Box.Width.Should().Be(1000, "the seed must actually be off the model's lattice");

        await vm.GenerateCommand.ExecuteAsync(null);

        backend.RunCount.Should().Be(1);
        vm.Box.Width.Should().Be(1024, "1000 rounds to the nearest multiple of 64");
        vm.Box.Height.Should().Be(1024);
        backend.LastRequest!.Width.Should().Be(1024);
        backend.LastRequest.Height.Should().Be(1024);
    }

    [Fact]
    public async Task Generate_SurvivesACatalogEntryWithZeroAlignment()
    {
        // DimensionAlignment is init-only with a default, so an explicit 0 is one typo away in a catalog
        // entry. The box sanitises it; Generate has to read the sanitised value back rather than divide
        // by the raw field.
        var backend = new FakeDiffusionBackend { DimensionAlignment = 0 };
        var vm = Canvas(backend);

        var act = () => vm.GenerateCommand.ExecuteAsync(null);

        await act.Should().NotThrowAsync();
        vm.StatusText.Should().NotContain("divide by zero");
        backend.RunCount.Should().Be(1);
    }

    [Fact]
    public async Task ABackendTimeoutDuringPreflightIsAnErrorNotACancel()
    {
        // HttpClient reports its own timeout as a TaskCanceledException. An engine that is alive but
        // wedged used to surface as "Cancelled." with the real cause swallowed.
        var backend = new FakeDiffusionBackend();
        var vm = Canvas(backend);
        backend.BeforeAvailabilityCheck = () =>
            throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout");

        await vm.GenerateCommand.ExecuteAsync(null);

        vm.StatusText.Should().StartWith("Error:");
        vm.StatusText.Should().Contain("HttpClient.Timeout");
        vm.IsGenerating.Should().BeFalse();
    }

    [Fact]
    public async Task ABackendTimeoutMidBatchFailsThatCandidateAndTheBatchCarriesOn()
    {
        var backend = new FakeDiffusionBackend();
        var vm = Canvas(backend);
        vm.BatchCount = 3;
        backend.BeforeRun = run =>
        {
            if (run == 2)
                throw new TaskCanceledException("timed out waiting for the engine");
        };

        await vm.GenerateCommand.ExecuteAsync(null);

        backend.RunCount.Should().Be(3, "a timeout on one candidate is not a cancel of the batch");
        vm.Staging.Candidates[1].State.Should().Be(StagedCandidateState.Failed);
        vm.Staging.Candidates[1].StatusText.Should().Contain("timed out");
        vm.Staging.Candidates[0].State.Should().Be(StagedCandidateState.Ready);
        vm.Staging.Candidates[2].State.Should().Be(StagedCandidateState.Ready);
        vm.StatusText.Should().NotBe("Cancelled.");
    }

    [Fact]
    public async Task AcceptedFrameRecordsThePromptTheCandidateWasGeneratedFrom()
    {
        var backend = new FakeDiffusionBackend();
        var vm = Canvas(backend);
        vm.BatchCount = 2;
        // The user starts typing the next idea while the batch is still running.
        backend.BeforeRun = run =>
        {
            if (run == 1)
                vm.PromptText = "a lighthouse at dawn, next idea";
        };

        await vm.GenerateCommand.ExecuteAsync(null);
        vm.Staging.AcceptCommand.Execute(null);

        vm.Frames.Should().ContainSingle().Which.Prompt.Should().Be("a lighthouse at dusk",
            "the frame's prompt is its provenance, not whatever is in the box at accept time");
        backend.LastRequest!.Prompt.Should().Be("a lighthouse at dusk",
            "the second candidate belongs to the batch that was started, not to the prompt being typed");
    }

    [Fact]
    public async Task AFrameWithoutASavedFileDoesNotCountAsImageToImage()
    {
        // A frame whose save failed is still drawn, but the compositor has nothing to read back from it.
        // Counting it would promise image-to-image in the readout for a run that executes as text-to-image.
        var backend = new FakeDiffusionBackend();
        var vm = Canvas(backend);
        vm.Box.SetSize(1024, 1024);
        vm.Box.SetPosition(0, 0);
        vm.Frames.Add(new GenerationFrameViewModel
        {
            CanvasX = 0, CanvasY = 0, Width = 1024, Height = 1024, ImagePath = null,
            State = GenerationFrameState.Completed,
        });

        vm.IsRegionOccupied.Should().BeFalse();
        vm.RegionModeText.Should().Contain("Text to image");

        await vm.GenerateCommand.ExecuteAsync(null);

        backend.RunCount.Should().Be(1);
        backend.LastRequest!.InitImage.Should().BeNull();
    }

    [Fact]
    public async Task Generate_RefusesWhenTheResultUnderTheBoxCannotBeReadBack()
    {
        // The readout counts the frame (it has a path), but the file is gone. Running would silently
        // execute as text-to-image with the denoise slider meaning nothing.
        var backend = new FakeDiffusionBackend();
        var vm = Canvas(backend);
        vm.Box.SetSize(1024, 1024);
        vm.Box.SetPosition(0, 0);
        var missing = Path.Combine(Path.GetTempPath(), $"dn-missing-{Guid.NewGuid():N}.png");
        vm.Frames.Add(new GenerationFrameViewModel
        {
            CanvasX = 0, CanvasY = 0, Width = 1024, Height = 1024, ImagePath = missing,
            State = GenerationFrameState.Completed,
        });
        vm.IsRegionOccupied.Should().BeTrue("the readout cannot afford a disk check per box move");

        await vm.GenerateCommand.ExecuteAsync(null);

        backend.RunCount.Should().Be(0, "a run that would not do what the readout says must not start");
        vm.Staging.Candidates.Should().BeEmpty();
        vm.StatusText.Should().Contain("could not be read back");
        vm.IsGenerating.Should().BeFalse();
    }

    [Fact]
    public async Task Generate_WritesTheRegionUnderOneFixedFileName()
    {
        // The engine backend uploads this file into ComfyUI's input folder under its own name and nothing
        // deletes that copy, so a fresh name per run (or per launch) is an unbounded leak on the server.
        using var canvas = new TempCanvasFile(512, 512, SKColors.White);
        var backend = new FakeDiffusionBackend();
        var vm = Canvas(backend);
        vm.Box.SetSize(512, 512);
        vm.Box.SetPosition(0, 0);
        vm.Frames.Add(canvas.AsFrame(0, 0, 512, 512));

        await vm.GenerateCommand.ExecuteAsync(null);
        var first = backend.LastRequest!.InitImage!.FilePath;
        await vm.GenerateCommand.ExecuteAsync(null);
        var second = backend.LastRequest!.InitImage!.FilePath;

        Path.GetFileName(first).Should().Be(DiffusionCanvasViewModel.RegionScratchFileName);
        second.Should().Be(first, "every run of this process reuses the same scratch path");
    }

    [Fact]
    public async Task Generate_DoesNothingWithoutAPrompt()
    {
        var backend = new FakeDiffusionBackend();
        var vm = Canvas(backend);
        vm.PromptText = "   ";

        await vm.GenerateCommand.ExecuteAsync(null);

        backend.RunCount.Should().Be(0);
        vm.StatusText.Should().Contain("prompt");
    }

    [Fact]
    public async Task Generate_ReportsAnUnavailableBackendInsteadOfStagingSlots()
    {
        var backend = new FakeDiffusionBackend { IsAvailable = false };
        backend.Missing.Add("The engine is not installed.");
        var vm = Canvas(backend);

        await vm.GenerateCommand.ExecuteAsync(null);

        vm.BackendUnavailableMessage.Should().Contain("not installed");
        vm.Staging.Candidates.Should().BeEmpty();
        backend.RunCount.Should().Be(0);
    }

    [Fact]
    public async Task Dispose_ReleasesTheStripAndTheCanvas()
    {
        var backend = new FakeDiffusionBackend();
        var vm = Canvas(backend);
        vm.BatchCount = 2;
        await vm.GenerateCommand.ExecuteAsync(null);
        vm.Staging.AcceptCommand.Execute(null);

        vm.Dispose();

        vm.Staging.Candidates.Should().BeEmpty();
        vm.Frames.Should().BeEmpty("the view model is a DI singleton, so nothing else tears it down");
    }
}
