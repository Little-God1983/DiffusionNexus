using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Media.Imaging;
using DiffusionNexus.UI.ViewModels.DiffusionCanvas;
using FluentAssertions;

namespace DiffusionNexus.Tests.DiffusionCanvas;

/// <summary>
/// Staging's contract: results are candidates, not commits. Nothing leaves the strip without an explicit
/// accept, and discarding follows the repo's detach-then-dispose ordering.
/// </summary>
public class CanvasStagingViewModelTests
{
    private static readonly Rect Box = new(256, 512, 1024, 1024);

    private static CanvasStagingViewModel WithReadyBatch(int count)
    {
        var staging = new CanvasStagingViewModel();
        foreach (var candidate in staging.BeginBatch(count, Box))
        {
            candidate.State = StagedCandidateState.Ready;
            candidate.StatusText = "Ready";
        }

        staging.Current = staging.Candidates[0];
        return staging;
    }

    [Fact]
    public void BeginBatch_CreatesOneDimmedSlotPerQueuedImage()
    {
        var staging = new CanvasStagingViewModel();

        var created = staging.BeginBatch(4, Box);

        created.Should().HaveCount(4);
        staging.Candidates.Should().HaveCount(4);
        staging.Candidates.Select(c => c.Ordinal).Should().Equal(1, 2, 3, 4);
        staging.Candidates.Should().OnlyContain(c => c.IsPending, "queued slots render dimmed");
        staging.Candidates.Should().OnlyContain(c => c.WorldRect == Box);
        staging.Current.Should().BeSameAs(staging.Candidates[0]);
        staging.HasCandidates.Should().BeTrue();
    }

    [Fact]
    public void BeginBatch_ClearsTheStripFromThePreviousRun()
    {
        var staging = WithReadyBatch(3);
        var stale = staging.Candidates.ToList();

        staging.BeginBatch(2, Box);

        staging.Candidates.Should().HaveCount(2);
        stale.Should().OnlyContain(c => c.IsDisposed);
    }

    [Fact]
    public void BeginBatch_RejectsAnEmptyBatch()
    {
        var staging = new CanvasStagingViewModel();

        var act = () => staging.BeginBatch(0, Box);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void NextAndPreviousStepWithoutWrapping()
    {
        var staging = WithReadyBatch(3);

        staging.PreviousCommand.CanExecute(null).Should().BeFalse("already at the first candidate");

        staging.NextCommand.Execute(null);
        staging.Current!.Ordinal.Should().Be(2);
        staging.NextCommand.Execute(null);
        staging.Current!.Ordinal.Should().Be(3);
        staging.NextCommand.CanExecute(null).Should().BeFalse("already at the last candidate");

        staging.PreviousCommand.Execute(null);
        staging.Current!.Ordinal.Should().Be(2);
    }

    [Fact]
    public void PositionTextReportsWhereTheUserIs()
    {
        var staging = WithReadyBatch(4);

        staging.PositionText.Should().Be("1 / 4");
        staging.NextCommand.Execute(null);
        staging.PositionText.Should().Be("2 / 4");
    }

    [Fact]
    public void Accept_RaisesTheEventAndRemovesTheCandidateFromTheStrip()
    {
        var staging = WithReadyBatch(2);
        var accepted = new List<StagedCandidateViewModel>();
        staging.CandidateAccepted += (_, c) => accepted.Add(c);
        var first = staging.Candidates[0];

        staging.AcceptCommand.Execute(null);

        accepted.Should().ContainSingle().Which.Should().BeSameAs(first);
        staging.Candidates.Should().NotContain(first);
        staging.Candidates.Should().HaveCount(1);
    }

    [Fact]
    public void Accept_DoesNotDisposeTheCandidateBecauseTheCanvasTakesItsBitmap()
    {
        var staging = WithReadyBatch(1);
        var candidate = staging.Candidates[0];

        staging.AcceptCommand.Execute(null);

        candidate.IsDisposed.Should().BeFalse(
            "the accepted raster takes ownership of the bitmap rather than the strip releasing it");
    }

    [Fact]
    public void Accept_IsRefusedWhileTheCandidateIsStillRendering()
    {
        var staging = new CanvasStagingViewModel();
        staging.BeginBatch(1, Box);

        staging.AcceptCommand.CanExecute(null).Should().BeFalse();

        staging.Candidates[0].State = StagedCandidateState.Ready;
        staging.RefreshCommands();

        staging.AcceptCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public void Discard_DetachesTheCandidateBeforeDisposingIt()
    {
        var staging = WithReadyBatch(2);
        var candidate = staging.Candidates[0];
        bool? wasStillAttachedWhenDisposed = null;
        candidate.Disposed += (_, _) => wasStillAttachedWhenDisposed = staging.Candidates.Contains(candidate);

        staging.DiscardCommand.Execute(null);

        wasStillAttachedWhenDisposed.Should().BeFalse(
            "disposing a bitmap still bound into the visual tree faults the render");
        candidate.IsDisposed.Should().BeTrue();
    }

    [Fact]
    public void DiscardingTheLastCandidateSelectsTheNewLastOne()
    {
        var staging = WithReadyBatch(3);
        staging.Current = staging.Candidates[2];

        staging.DiscardCommand.Execute(null);

        staging.Candidates.Should().HaveCount(2);
        staging.Current.Should().BeSameAs(staging.Candidates[1]);
    }

    [Fact]
    public void DiscardingEverythingLeavesNoSelection()
    {
        var staging = WithReadyBatch(2);

        staging.DiscardAllCommand.Execute(null);

        staging.Candidates.Should().BeEmpty();
        staging.Current.Should().BeNull();
        staging.HasCandidates.Should().BeFalse();
        staging.PositionText.Should().BeEmpty();
    }

    [Fact]
    public void AcceptAll_AcceptsEveryReadyCandidateAndLeavesPendingSlotsAlone()
    {
        var staging = new CanvasStagingViewModel();
        var candidates = staging.BeginBatch(3, Box);
        candidates[0].State = StagedCandidateState.Ready;
        candidates[2].State = StagedCandidateState.Ready;
        var accepted = new List<int>();
        staging.CandidateAccepted += (_, c) => accepted.Add(c.Ordinal);

        staging.AcceptAllCommand.Execute(null);

        accepted.Should().Equal(1, 3);
        staging.Candidates.Should().ContainSingle().Which.Ordinal.Should().Be(2);
    }

    [Fact]
    public void PruneAfterCancel_RemovesEverySlotThatCanNeverHoldAnImage()
    {
        var staging = new CanvasStagingViewModel();
        var candidates = staging.BeginBatch(4, Box);
        candidates[0].State = StagedCandidateState.Ready;
        candidates[1].State = StagedCandidateState.Cancelled;   // the one that was in flight
        // [2] and [3] never started.

        var removed = staging.PruneAfterCancel();

        removed.Should().Be(3);
        staging.Candidates.Should().ContainSingle().Which.Should().BeSameAs(candidates[0],
            "a finished result survives the cancel");
        candidates[1].IsDisposed.Should().BeTrue();
        candidates[2].IsDisposed.Should().BeTrue();
        candidates[3].IsDisposed.Should().BeTrue();
        staging.Current.Should().BeSameAs(candidates[0]);
    }

    [Fact]
    public void PruneAfterCancel_KeepsFailedSlotsBecauseTheirErrorIsWorthReading()
    {
        var staging = new CanvasStagingViewModel();
        var candidates = staging.BeginBatch(2, Box);
        candidates[0].State = StagedCandidateState.Failed;
        candidates[0].StatusText = "the engine said no";

        staging.PruneAfterCancel();

        staging.Candidates.Should().ContainSingle().Which.State.Should().Be(StagedCandidateState.Failed);
    }

    [Fact]
    public void CurrentRectFollowsTheSelection()
    {
        var staging = new CanvasStagingViewModel();
        staging.BeginBatch(2, Box);

        staging.CurrentRect.Should().Be(Box);

        staging.DiscardAllCommand.Execute(null);
        staging.CurrentRect.Should().Be(default(Rect));
    }

    [Fact]
    public void CurrentImageIsRaisedWhenTheSelectedCandidatesImageArrives()
    {
        var staging = new CanvasStagingViewModel();
        staging.BeginBatch(1, Box);
        var raised = new List<string?>();
        staging.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        // There is no Avalonia platform in this project, so a real Bitmap cannot be constructed. The
        // repo's established stand-in is an uninitialised instance used purely as an opaque non-null
        // value that is never dereferenced (BatchUpscaleTabViewModelSchedulerTests does the same).
        var sentinel = (Bitmap)RuntimeHelpers.GetUninitializedObject(typeof(Bitmap));
        GC.SuppressFinalize(sentinel);

        // The image lands long after the slot is on screen, so the preview binding must be re-raised.
        staging.Candidates[0].Image = sentinel;

        raised.Should().Contain(nameof(CanvasStagingViewModel.CurrentImage));
    }

    [Fact]
    public void IsComparingRaisesChangeNotificationForTheSurfaceBinding()
    {
        // The compare gesture reaches the surface only through the OneWay binding on IsPreviewHidden,
        // so the notification -- not the stored value -- is the part that has to work.
        var staging = WithReadyBatch(1);
        var raised = new List<string?>();
        staging.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        staging.IsComparing = true;

        raised.Should().Contain(nameof(CanvasStagingViewModel.IsComparing));
        staging.IsComparing.Should().BeTrue();
    }
}
