using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services.Sync;
using FluentAssertions;

namespace DiffusionNexus.Tests.Sync.Domain;

public class SyncRetryPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);
    private readonly SyncRetryPolicy _p = SyncRetryPolicy.Default;

    [Fact] public void NeverCheckedIsDue() => _p.IsIdentifyDue(SyncOutcome.None, null, 0, Now, force: false).Should().BeTrue();
    [Fact] public void MatchedIsNeverDueWithoutForce() => _p.IsIdentifyDue(SyncOutcome.Matched, Now.AddYears(-5), 0, Now, false).Should().BeFalse();
    [Fact] public void MatchedIsDueWithForce() => _p.IsIdentifyDue(SyncOutcome.Matched, Now.AddDays(-1), 0, Now, true).Should().BeTrue();
    [Fact] public void SidecarAndHeaderAndHeuristicAreDueAfterLongWindow()
    {
        foreach (var o in new[] { SyncOutcome.Sidecar, SyncOutcome.Header, SyncOutcome.Heuristic, SyncOutcome.NotIdentified })
        {
            _p.IsIdentifyDue(o, Now.AddDays(-29), 0, Now, false).Should().BeFalse($"{o} within 30 days");
            _p.IsIdentifyDue(o, Now.AddDays(-31), 0, Now, false).Should().BeTrue($"{o} after 30 days");
        }
    }
    [Fact] public void ErrorIsDueAfterOneDayUntilAttemptsExhausted()
    {
        _p.IsIdentifyDue(SyncOutcome.Error, Now.AddHours(-23), 1, Now, false).Should().BeFalse();
        _p.IsIdentifyDue(SyncOutcome.Error, Now.AddHours(-25), 1, Now, false).Should().BeTrue();
        _p.IsIdentifyDue(SyncOutcome.Error, Now.AddDays(-10), 3, Now, false).Should().BeFalse("3 attempts exhausted");
        _p.IsIdentifyDue(SyncOutcome.Error, Now.AddDays(-10), 3, Now, true).Should().BeTrue("force resets");
    }
    [Fact] public void FetchOnceIsDueOnlyWhenNeverCheckedOrForced()
    {
        _p.IsFetchDue(null, false).Should().BeTrue();
        _p.IsFetchDue(Now.AddYears(-3), false).Should().BeFalse("checked-and-empty is final");
        _p.IsFetchDue(Now.AddYears(-3), true).Should().BeTrue();
    }
    [Fact] public void ScopeFactoriesCarryTheirArguments()
    {
        SyncScope.Library.Kind.Should().Be(SyncScopeKind.Library);
        SyncScope.ForFolder(@"E:\Loras").SourceFolder.Should().Be(@"E:\Loras");
        SyncScope.ForModels(1, 2).ModelIds.Should().Equal(1, 2);
    }
    [Fact] public void ReportSummaryListsNonEmptySteps()
    {
        var plan = new SyncPlan(SyncScope.Library, SyncOptions.All, new[]
        {
            new SyncPlanStep(SyncStepKind.DiscoverFiles, 0, TimeSpan.Zero, "Discover new files"),
            new SyncPlanStep(SyncStepKind.IdentifyModel, 3, TimeSpan.FromSeconds(6), "Identify"),
            new SyncPlanStep(SyncStepKind.FetchTags, 0, TimeSpan.Zero, "Tags"),
        }, Now);
        var report = new SyncReport(plan, new[]
        {
            new SyncStepReport(SyncStepKind.DiscoverFiles, 0, 0, 0, 0, 0),
            new SyncStepReport(SyncStepKind.IdentifyModel, 3, 3, 1, 1, 1),
            new SyncStepReport(SyncStepKind.FetchTags, 0, 0, 0, 0, 0),
        }, Array.Empty<SyncFailure>(), Cancelled: false, TimeSpan.FromSeconds(7), NewFilesDiscovered: 2);
        report.Summary.Should().Be("Discovered 2 · Identified 1/3");
        plan.HasWork.Should().BeTrue();
        plan.EstimatedDuration.Should().Be(TimeSpan.FromSeconds(6));
    }
}
