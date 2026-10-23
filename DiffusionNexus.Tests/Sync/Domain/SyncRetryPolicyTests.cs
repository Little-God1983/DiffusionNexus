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

    /// <summary>
    /// Discovery is not work in the sense this property answers. It used to be special-cased as
    /// always-work, which made <c>HasWork</c> constant-true for every full plan and every
    /// "nothing to do" branch behind it dead code — including the viewer's up-to-date early-out.
    /// A scan whose result nobody can count in advance is executed on its own terms instead.
    /// </summary>
    [Fact]
    public void PlanHasWorkIgnoresTheUncountableDiscoveryStep()
    {
        var discoveryOnly = new SyncPlan(SyncScope.Library, SyncOptions.All, new[]
        {
            new SyncPlanStep(SyncStepKind.DiscoverFiles, 0, TimeSpan.FromSeconds(2), "Discover new files"),
        }, Now);

        discoveryOnly.HasWork.Should().BeFalse("a scan that has counted nothing is not counted work");

        var withIdentify = discoveryOnly with
        {
            Steps = discoveryOnly.Steps
                .Append(new SyncPlanStep(SyncStepKind.IdentifyModel, 1, TimeSpan.FromSeconds(3), "Identify"))
                .ToList(),
        };

        withIdentify.HasWork.Should().BeTrue("one counted item in any step is work");
    }

    [Fact]
    public void FromDays_BuildsWindowsFromSettingsValues()
    {
        var policy = SyncRetryPolicy.FromDays(notIdentifiedDays: 14, errorDays: 3);

        policy.NotIdentifiedRetryAfter.Should().Be(TimeSpan.FromDays(14));
        policy.ErrorRetryAfter.Should().Be(TimeSpan.FromDays(3));
        policy.MaxErrorAttempts.Should().Be(SyncRetryPolicy.Default.MaxErrorAttempts);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void FromDays_FloorsAtOneDay_BecauseZeroWouldMeanAlwaysDue(int days)
    {
        var policy = SyncRetryPolicy.FromDays(days, days);

        policy.NotIdentifiedRetryAfter.Should().Be(TimeSpan.FromDays(1));
        policy.ErrorRetryAfter.Should().Be(TimeSpan.FromDays(1));
    }

    /// <summary>
    /// These values are not guaranteed to come from the settings combo boxes: the settings importer
    /// copies them straight out of a JSON file with no range check, and the repository persists them
    /// unvalidated. <c>TimeSpan.FromDays</c> throws above ~10 675 199 days, so a corrupted
    /// <c>settings.json</c> carrying <c>2147483647</c> made every Download Metadata press die with
    /// "TimeSpan overflowed because the duration is too long" — an unusable button, no way to see
    /// why. Ten years is past every meaningful horizon, so it degrades instead.
    /// </summary>
    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(3651)]
    public void FromDays_CapsAtTenYears_BecauseTheValuesAreNotValidatedOnTheWayIn(int days)
    {
        var policy = SyncRetryPolicy.FromDays(days, days);

        policy.NotIdentifiedRetryAfter.Should().Be(TimeSpan.FromDays(3650));
        policy.ErrorRetryAfter.Should().Be(TimeSpan.FromDays(3650));
    }

    /// <summary>
    /// "Everything but discovery" is derived, not listed. A sixth step kind therefore reaches the
    /// bulk button's plan and the plan dialog's rows on its own — the hand-written copies this
    /// replaced would have left it silently absent from both, with no compile error to say so.
    /// </summary>
    [Fact]
    public void AllStepsExcept_IsEveryEnumMemberMinusTheExcludedOne()
    {
        var all = Enum.GetValues<SyncStepKind>();

        SyncOptions.AllStepsExcept(SyncStepKind.DiscoverFiles).Should()
            .BeEquivalentTo(all.Where(k => k != SyncStepKind.DiscoverFiles));
        SyncOptions.AllStepsExcept(SyncStepKind.Thumbnails).Should()
            .BeEquivalentTo(all.Where(k => k != SyncStepKind.Thumbnails));
    }
}
