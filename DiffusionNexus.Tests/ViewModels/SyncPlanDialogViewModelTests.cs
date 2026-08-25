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

    [Fact]
    public void AZeroCountRow_ShowsNoEstimate()
    {
        var vm = Vm(PlanWith(identify: 3, tags: 0, images: 0, thumbs: 12));

        vm.Rows.Single(r => r.Kind == SyncStepKind.FetchTags).EstimateText.Should().BeEmpty(
            "a disabled '0 Tags ~0 s' row reads like pending work — an empty cell does not");
        vm.Rows.Single(r => r.Kind == SyncStepKind.IdentifyModel).EstimateText.Should().NotBeEmpty();
    }
}
