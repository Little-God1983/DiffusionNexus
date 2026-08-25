using DiffusionNexus.Domain.Services.Sync;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Tests.ViewModels;

public class SyncPlanDialogViewModelTests
{
    private static readonly IReadOnlySet<SyncStepKind> FourKinds = new HashSet<SyncStepKind>
    {
        SyncStepKind.IdentifyModel, SyncStepKind.FetchTags, SyncStepKind.FetchImages, SyncStepKind.Thumbnails,
    };

    private static SyncPlan PlanWith(int identify, int tags, int images, int thumbs, SyncOptions? options = null)
    {
        options ??= new SyncOptions(FourKinds);
        return new SyncPlan(SyncScope.Library, options, new[]
        {
            new SyncPlanStep(SyncStepKind.IdentifyModel, identify, TimeSpan.FromSeconds(3 * identify), "identify"),
            new SyncPlanStep(SyncStepKind.FetchTags, tags, TimeSpan.FromSeconds(1.6 * tags), "tags"),
            new SyncPlanStep(SyncStepKind.FetchImages, images, TimeSpan.FromSeconds(1.6 * images), "images"),
            new SyncPlanStep(SyncStepKind.Thumbnails, thumbs, TimeSpan.FromSeconds(0.4 * thumbs), "thumbs"),
        }, DateTimeOffset.UtcNow);
    }

    private static SyncPlanDialogViewModel Vm(
        SyncPlan plan,
        Func<SyncOptions, Task<SyncPlan>>? replan = null,
        DateTimeOffset? lastSync = null,
        int discovered = 0)
        => new(plan, new SyncOptions(FourKinds), replan ?? (_ => Task.FromResult(plan)), lastSync, discovered);

    [Fact]
    public void RowsWithWork_ArePreChecked_AndEmptyRowsAreDisabled()
    {
        var vm = Vm(PlanWith(identify: 3, tags: 68, images: 0, thumbs: 12));

        vm.Rows.Should().HaveCount(4);
        vm.Rows.Single(r => r.Kind == SyncStepKind.IdentifyModel).IsSelected.Should().BeTrue();
        vm.Rows.Single(r => r.Kind == SyncStepKind.FetchImages).IsSelected.Should().BeFalse();
        vm.Rows.Single(r => r.Kind == SyncStepKind.FetchImages).IsEnabled.Should().BeFalse();
        vm.IsUpToDate.Should().BeFalse();
        vm.CanStart.Should().BeTrue();
    }

    [Fact]
    public void AllZeroPlan_IsUpToDate_WithNoStart_AndShowsTheLastRun()
    {
        var last = new DateTimeOffset(2026, 8, 25, 14, 3, 0, TimeSpan.Zero);
        var vm = Vm(PlanWith(0, 0, 0, 0), lastSync: last);

        vm.IsUpToDate.Should().BeTrue();
        vm.CanStart.Should().BeFalse();
        vm.UpToDateText.Should().Be("Library is up to date — nothing to do");
        vm.LastRunText.Should().Contain("Last full sync:").And.NotContain("never");
    }

    [Fact]
    public void NoRecordedRun_SaysNever()
    {
        var vm = Vm(PlanWith(0, 0, 0, 0), lastSync: null);
        vm.LastRunText.Should().Be("Last full sync: never");
    }

    [Fact]
    public async Task TogglingAForce_Replans_AndAppliesTheNewCounts()
    {
        SyncOptions? seen = null;
        Task<SyncPlan> Replan(SyncOptions o)
        {
            seen = o;
            return Task.FromResult(PlanWith(0, 0, 0, thumbs: 40, options: o));
        }

        var vm = Vm(PlanWith(0, 0, 0, 0), Replan);
        vm.ForceThumbnails = true;
        await vm.WhenReplanSettles();

        seen.Should().NotBeNull();
        seen!.ForceThumbnails.Should().BeTrue();
        seen.Steps.Should().BeEquivalentTo(FourKinds);
        vm.Rows.Single(r => r.Kind == SyncStepKind.Thumbnails).Count.Should().Be(40);
        vm.Rows.Single(r => r.Kind == SyncStepKind.Thumbnails).IsSelected.Should().BeTrue();
        vm.IsUpToDate.Should().BeFalse();
        vm.CanStart.Should().BeTrue();
    }

    [Fact]
    public async Task AUserUntick_SurvivesAReplan()
    {
        var vm = Vm(PlanWith(identify: 3, tags: 68, images: 0, thumbs: 12),
            o => Task.FromResult(PlanWith(3, 68, 0, 40, o)));

        vm.Rows.Single(r => r.Kind == SyncStepKind.FetchTags).IsSelected = false;
        vm.ForceThumbnails = true;
        await vm.WhenReplanSettles();

        vm.Rows.Single(r => r.Kind == SyncStepKind.FetchTags).IsSelected.Should().BeFalse();
    }

    [Fact]
    public void BuildResult_CarriesTheCheckedKindsAndForces()
    {
        var vm = Vm(PlanWith(identify: 3, tags: 68, images: 2, thumbs: 12));
        vm.Rows.Single(r => r.Kind == SyncStepKind.FetchTags).IsSelected = false;
        vm.ForceIdentify = true;

        var result = vm.BuildResult();

        result.Confirmed.Should().BeTrue();
        result.Options!.Steps.Should().BeEquivalentTo(new[]
        {
            SyncStepKind.IdentifyModel, SyncStepKind.FetchImages, SyncStepKind.Thumbnails,
        });
        result.Options.ForceIdentify.Should().BeTrue();
        result.Options.ForceTags.Should().BeFalse();
    }

    /// <summary>
    /// Pins the three unit branches and their boundaries — this string feeds the row estimates and
    /// the footer total. 89.5 s reading "~90 s" while still in the seconds branch is the documented
    /// quirk, not a bug. The report's elapsed line no longer comes through here: a measurement gets
    /// <see cref="SyncCopy.FormatElapsed"/>, which wears no tilde.
    /// </summary>
    [Theory]
    [InlineData(0, "~0 s")]
    [InlineData(-5, "~0 s")]
    [InlineData(89, "~89 s")]
    [InlineData(89.5, "~90 s")]
    [InlineData(90, "~2 min")]
    [InlineData(60 * 89, "~89 min")]
    [InlineData(60 * 90, "~1.5 h")]
    [InlineData(60 * 60 * 3, "~3 h")]
    public void FormatDuration_RendersEachUnitBranch(double seconds, string expected)
    {
        SyncCopy.FormatEstimate(TimeSpan.FromSeconds(seconds)).Should().Be(expected);
    }

    /// <summary>
    /// F4. Two quick toggles queue two re-plans. The flag used to be raised inside the queued work,
    /// so the first link's finally lowered it and the second link's continuation raised it again a
    /// dispatcher turn later — and in that turn the UI was live with the first plan's counts. Tick
    /// Force tags, tick Force image records, press Start in the gap: <c>BuildResult</c> filters by
    /// <c>Count &gt; 0</c>, the Images row was still 0, and FetchImages was silently dropped from
    /// the run the user had just asked for.
    /// </summary>
    [Fact]
    public async Task StartStaysDisabledAcrossAWholeChainOfQueuedReplans()
    {
        var gateA = new TaskCompletionSource();
        var gateB = new TaskCompletionSource();
        var secondEntered = new TaskCompletionSource();
        var calls = 0;

        var vm = Vm(PlanWith(identify: 3, tags: 0, images: 0, thumbs: 0), async o =>
        {
            var call = Interlocked.Increment(ref calls);
            if (call == 2) secondEntered.TrySetResult();
            await (call == 1 ? gateA.Task : gateB.Task);
            return PlanWith(3, 0, 0, thumbs: call == 1 ? 0 : 40, options: o);
        });

        var flag = new List<bool>();
        var thumbsWhenStartCameBack = -1;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(vm.IsReplanning)) return;

            flag.Add(vm.IsReplanning);
            if (!vm.IsReplanning && thumbsWhenStartCameBack < 0)
                thumbsWhenStartCameBack = vm.Rows.Single(r => r.Kind == SyncStepKind.Thumbnails).Count;
        };

        vm.ForceTags = true;         // queues the first re-plan
        vm.ForceThumbnails = true;   // queues the second behind it

        vm.IsReplanning.Should().BeTrue("the flag goes up at toggle time, not when the work is pumped");
        vm.CanStart.Should().BeFalse();

        gateA.SetResult();
        await secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        gateB.SetResult();
        await vm.WhenReplanSettles();

        flag.Should().Equal(new[] { true, false },
            "one rise and one fall for the whole chain — a fall in between is the dispatcher turn " +
            "in which Start was live over counts the user had already superseded");
        thumbsWhenStartCameBack.Should().Be(40,
            "Start came back only once the LAST queued plan's counts were the ones on screen");
        vm.CanStart.Should().BeTrue("the chain settled and the plan that landed has work");
    }

    /// <summary>
    /// F13. Every toggle already re-planned with the exact options <c>BuildResult</c> returns, so
    /// the caller need not pay for a third full selection pass over the library. The plan comes
    /// back filtered to the ticked kinds and re-labelled with the chosen options.
    /// </summary>
    [Fact]
    public void BuildResult_HandsBackThePlanTheDialogWasShowing()
    {
        var vm = Vm(PlanWith(identify: 3, tags: 68, images: 2, thumbs: 12));
        vm.Rows.Single(r => r.Kind == SyncStepKind.FetchTags).IsSelected = false;

        var result = vm.BuildResult();

        result.Plan.Should().NotBeNull("nothing was forced, so the counts on screen are still the plan's");
        result.Plan!.Options.Should().BeSameAs(result.Options,
            "the plan is re-labelled with the chosen options so ExecuteAsync runs the ticked steps with the right forces");
        result.Plan.Steps.Select(s => s.Kind).Should().BeEquivalentTo(new[]
        {
            SyncStepKind.IdentifyModel, SyncStepKind.FetchImages, SyncStepKind.Thumbnails,
        }, "the unticked row's step is not part of this run");
        result.Plan.Steps.Single(s => s.Kind == SyncStepKind.IdentifyModel).Count.Should().Be(3,
            "the counts are the ones the dialog was showing");
    }

    /// <summary>
    /// A failed re-plan deliberately keeps the previous counts — which now describe a different
    /// item set than the toggles do. The dialog says so by withholding its plan, and the caller
    /// plans again rather than running the wrong selection.
    /// </summary>
    [Fact]
    public async Task BuildResult_WithholdsThePlanWhenAReplanFailedAndLeftTheCountsStale()
    {
        var vm = Vm(PlanWith(identify: 3, tags: 0, images: 0, thumbs: 0),
            _ => Task.FromException<SyncPlan>(new InvalidOperationException("database is locked")));

        vm.ForceThumbnails = true;
        await vm.WhenReplanSettles();

        vm.Rows.Single(r => r.Kind == SyncStepKind.IdentifyModel).Count.Should().Be(3,
            "the dialog keeps what it had rather than blanking itself");

        var result = vm.BuildResult();

        result.Options!.ForceThumbnails.Should().BeTrue("the toggle is still the user's choice");
        result.Plan.Should().BeNull("these counts were never computed with that force");
    }

    /// <summary>A successful re-plan brings the two back into step, so the plan travels again.</summary>
    [Fact]
    public async Task BuildResult_HandsBackTheReplannedPlanAfterAForce()
    {
        var vm = Vm(PlanWith(identify: 3, tags: 0, images: 0, thumbs: 0),
            o => Task.FromResult(PlanWith(3, 0, 0, thumbs: 40, options: o)));

        vm.ForceThumbnails = true;
        await vm.WhenReplanSettles();

        var result = vm.BuildResult();

        result.Plan.Should().NotBeNull();
        result.Plan!.Steps.Single(s => s.Kind == SyncStepKind.Thumbnails).Count.Should().Be(40);
        result.Plan.Options.ForceThumbnails.Should().BeTrue();
    }

    [Fact]
    public void AZeroCountRow_ShowsNoEstimate()
    {
        var vm = Vm(PlanWith(identify: 3, tags: 0, images: 0, thumbs: 12));

        vm.Rows.Single(r => r.Kind == SyncStepKind.FetchTags).EstimateText.Should().BeEmpty(
            "a disabled '0 Tags ~0 s' row reads like pending work — an empty cell does not");
        vm.Rows.Single(r => r.Kind == SyncStepKind.IdentifyModel).EstimateText.Should().NotBeEmpty();
    }
}
