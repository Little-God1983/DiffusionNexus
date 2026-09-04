using System.Runtime.CompilerServices;
using Avalonia.Media.Imaging;
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
    private const string ModelKey = "fake-model";

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
        vm.Staging.Candidates.Should().NotContain(c => c.IsPending, "no slot is left spinning");
        vm.Staging.Candidates.Should().Contain(c => c.State == StagedCandidateState.Cancelled);
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
    public void Cancel_IsOnlyOfferedWhileABatchIsRunning()
    {
        var vm = Canvas(new FakeDiffusionBackend());

        vm.CancelCommand.CanExecute(null).Should().BeFalse();
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
    public async Task Generate_RefusesAnOffLatticeBoxRatherThanLettingTheBackendThrowLater()
    {
        // Holding Alt while dragging clears SnapToGrid, so the box can sit off the model's lattice.
        // The backend's own validation throws lazily, on the first MoveNextAsync inside the caller's
        // await foreach -- long after a candidate slot exists -- so the refusal has to happen here.
        var backend = new FakeDiffusionBackend { DimensionAlignment = 128 };
        var vm = Canvas(backend);
        vm.Box.SnapToGrid = false;
        vm.Box.SetSize(1090, 1024);

        await vm.GenerateCommand.ExecuteAsync(null);

        backend.RunCount.Should().Be(0);
        vm.StatusText.Should().Contain("128");
        vm.Staging.Candidates.Should().BeEmpty();
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

    /// <summary>
    /// A backend whose <c>GenerateAsync</c> is a real async iterator, so the view model's
    /// <c>await foreach</c>, its cancellation and its progress mapping are all exercised for real.
    /// </summary>
    private sealed class FakeDiffusionBackend : IDiffusionBackend
    {
        private int _concurrent;

        public string DisplayName => "Fake backend";

        public int DimensionAlignment { get; init; } = 64;

        public bool IsAvailable { get; init; } = true;

        public List<string> Missing { get; } = [];

        /// <summary>Run index (1-based) that should throw, simulating a backend blowing up.</summary>
        public int? FailOnRun { get; init; }

        /// <summary>Run index (1-based) that should report a failure message with no result.</summary>
        public int? ReportFailureMessageOnRun { get; init; }

        /// <summary>Called at the start of each run with its 1-based index.</summary>
        public Action<int>? BeforeRun { get; set; }

        public int RunCount { get; private set; }

        public int MaxConcurrentRuns { get; private set; }

        public DiffusionRequest? LastRequest { get; private set; }

        public List<long?> RequestedSeeds { get; } = [];

        public IModelCatalog Catalog => new FakeCatalog(DimensionAlignment);

        public IReadOnlyList<string> MissingRequirements => Missing;

        public IReadOnlyList<string> Warnings => [];

        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(IsAvailable);

        public async IAsyncEnumerable<DiffusionStreamItem> GenerateAsync(
            DiffusionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var run = ++RunCount;
            LastRequest = request;
            RequestedSeeds.Add(request.Seed);

            MaxConcurrentRuns = Math.Max(MaxConcurrentRuns, ++_concurrent);
            try
            {
                BeforeRun?.Invoke(run);
                cancellationToken.ThrowIfCancellationRequested();

                yield return new DiffusionStreamItem(new DiffusionProgress
                {
                    Phase = DiffusionPhase.Loading,
                    Message = "Loading…",
                });

                if (FailOnRun == run)
                    throw new InvalidOperationException("the fake backend exploded");

                yield return new DiffusionStreamItem(new DiffusionProgress
                {
                    Phase = DiffusionPhase.Sampling,
                    Step = 1,
                    TotalSteps = 2,
                });

                cancellationToken.ThrowIfCancellationRequested();

                if (ReportFailureMessageOnRun == run)
                {
                    yield return new DiffusionStreamItem(new DiffusionProgress
                    {
                        Phase = DiffusionPhase.Completed,
                        Message = "the engine said no",
                    });
                    yield break;
                }

                yield return new DiffusionStreamItem(
                    new DiffusionProgress { Phase = DiffusionPhase.Completed, Step = 2, TotalSteps = 2 },
                    new DiffusionResult(
                        // Not a decodable PNG: the view model must survive a decode failure, and there
                        // is no Avalonia platform here to decode a real one anyway.
                        [1, 2, 3, 4],
                        request.Width,
                        request.Height,
                        request.Seed ?? 42,
                        TimeSpan.FromSeconds(1)));
            }
            finally
            {
                _concurrent--;
            }
        }

        private sealed class FakeCatalog(int alignment) : IModelCatalog
        {
            private readonly ModelDescriptor _descriptor = new()
            {
                Key = ModelKey,
                DisplayName = "Fake Model",
                Kind = ModelKind.Krea2,
                DimensionAlignment = alignment,
                DefaultWidth = 1024,
                DefaultHeight = 1024,
            };

            public IReadOnlyList<ModelDescriptor> ListAvailable() => [_descriptor];

            public ModelDescriptor? TryGet(string key) =>
                string.Equals(key, ModelKey, StringComparison.Ordinal) ? _descriptor : null;
        }
    }
}
