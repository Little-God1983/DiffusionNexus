using DiffusionNexus.Domain.Entities;
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
    /// <summary>
    /// The per-image thumbnail window. Unlike the model-level steps this one is keyed on a failure
    /// <i>reason</i> rather than an outcome enum, and the reasons split three ways: none (nothing
    /// went wrong — the row is not selected anyway), soft (retry once the error window has passed),
    /// and hard (a final answer that only a force overturns).
    /// </summary>
    [Theory]
    [InlineData(null, null, false, true)]                                       // never attempted
    [InlineData(null, 0, false, false)]                                         // attempted, nothing recorded against it
    [InlineData(ThumbnailFailureReason.HttpError, 0, false, false)]             // soft, inside the window
    [InlineData(ThumbnailFailureReason.HttpError, -2, false, true)]             // soft, window passed
    [InlineData(ThumbnailFailureReason.VideoNoPoster, -2, false, true)]         // soft, window passed
    [InlineData(ThumbnailFailureReason.Http404, -2, false, false)]              // hard: a final answer
    [InlineData(ThumbnailFailureReason.NotDecodable, -2, false, false)]         // hard
    [InlineData(ThumbnailFailureReason.LocalFileMissing, -2, false, false)]     // hard
    [InlineData(ThumbnailFailureReason.UnsupportedScheme, -2, false, false)]    // hard
    [InlineData(ThumbnailFailureReason.Corrupt, 0, false, true)]                // self-heal: the BLOB was nulled, refetch now
    [InlineData(ThumbnailFailureReason.Http404, 0, true, true)]                 // force overturns a hard failure
    [InlineData(null, 0, true, true)]                                           // force overturns a success
    public void ThumbnailDueness(string? failure, int? attemptedDaysAgo, bool force, bool expected)
    {
        DateTimeOffset? attemptedAt = attemptedDaysAgo is null ? null : Now.AddDays(attemptedDaysAgo.Value);

        _p.IsThumbnailDue(attemptedAt, failure, Now, force).Should().Be(expected);
    }

    /// <summary>
    /// The soft-failure boundary is the shared <c>ErrorRetryAfter</c> window rather than a second
    /// constant — asserted against the policy's own value, so tuning it cannot silently
    /// desynchronise the two.
    /// </summary>
    [Fact]
    public void ThumbnailSoftFailureUsesTheSharedErrorWindow()
    {
        _p.IsThumbnailDue(Now - _p.ErrorRetryAfter + TimeSpan.FromMinutes(1), ThumbnailFailureReason.HttpError, Now, false)
            .Should().BeFalse();
        _p.IsThumbnailDue(Now - _p.ErrorRetryAfter, ThumbnailFailureReason.HttpError, Now, false)
            .Should().BeTrue("the window is inclusive");
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
