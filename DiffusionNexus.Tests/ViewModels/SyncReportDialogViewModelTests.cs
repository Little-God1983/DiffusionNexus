using DiffusionNexus.Domain.Services.Sync;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Tests.ViewModels;

public class SyncReportDialogViewModelTests
{
    private static SyncReport Report(
        IReadOnlyList<SyncStepReport> steps,
        IReadOnlyList<SyncFailure>? failures = null,
        bool cancelled = false,
        int unexpected = 0,
        string? abortReason = null)
    {
        var options = new SyncOptions(new HashSet<SyncStepKind> { SyncStepKind.IdentifyModel });
        var plan = new SyncPlan(SyncScope.Library, options, Array.Empty<SyncPlanStep>(), DateTimeOffset.UtcNow);
        return new SyncReport(plan, steps, failures ?? Array.Empty<SyncFailure>(), cancelled,
            TimeSpan.FromSeconds(90), NewFilesDiscovered: 0, UnexpectedFailures: unexpected,
            AbortReason: abortReason);
    }

    [Fact]
    public void FailuresAreGroupedByStep_WithNameAndReasonPerRow()
    {
        var report = Report(
            new[] { new SyncStepReport(SyncStepKind.FetchTags, 10, 10, 7, 0, 3),
                    new SyncStepReport(SyncStepKind.Thumbnails, 5, 5, 4, 0, 1) },
            new[]
            {
                new SyncFailure(SyncStepKind.FetchTags, 1, "ModelA", "Timeout"),
                new SyncFailure(SyncStepKind.FetchTags, 2, "ModelB", "Timeout"),
                new SyncFailure(SyncStepKind.FetchTags, 3, "ModelC", "Http500"),
                new SyncFailure(SyncStepKind.Thumbnails, 4, "ModelD", "Http404"),
            });

        var vm = new SyncReportDialogViewModel(report, newFilesDiscovered: 0);

        vm.FailureGroups.Should().HaveCount(2);
        var tags = vm.FailureGroups.Single(g => g.Kind == SyncStepKind.FetchTags);
        tags.Header.Should().Be("Tags — 3 failed");
        tags.Items.Should().HaveCount(3);
        tags.Items[0].Name.Should().Be("ModelA");
        tags.Items[0].Reason.Should().Be("Timeout");
        vm.HasFailures.Should().BeTrue();
    }

    [Fact]
    public void ACleanRun_ShowsNoFailureGroups()
    {
        var vm = new SyncReportDialogViewModel(
            Report(new[] { new SyncStepReport(SyncStepKind.FetchTags, 10, 10, 10, 0, 0) }),
            newFilesDiscovered: 3);

        vm.HasFailures.Should().BeFalse();
        vm.FailureGroups.Should().BeEmpty();
        vm.DiscoveredText.Should().Contain("3");
    }

    [Fact]
    public void ACancelledRun_SaysPartial()
    {
        var vm = new SyncReportDialogViewModel(
            Report(new[] { new SyncStepReport(SyncStepKind.FetchTags, 10, 4, 4, 0, 0) }, cancelled: true),
            newFilesDiscovered: 0);

        vm.IsPartial.Should().BeTrue();
        vm.PartialText.Should().Contain("Cancelled");
    }

    /// <summary>
    /// #535. A run that aborted midway (an exception escaped outside the item loop) is partial in
    /// the same sense a cancelled one is — the completed items are recorded — but the banner must
    /// name the failure rather than claim anybody pressed Cancel.
    /// </summary>
    [Fact]
    public void AnAbortedRun_SaysPartialAndNamesTheReason()
    {
        var vm = new SyncReportDialogViewModel(
            Report(new[] { new SyncStepReport(SyncStepKind.IdentifyModel, 10, 10, 10, 0, 0) },
                abortReason: "Unexpected InvalidOperationException: database is locked"),
            newFilesDiscovered: 0);

        vm.IsPartial.Should().BeTrue();
        vm.PartialText.Should().Contain("database is locked").And.NotContain("Cancelled");
    }

    /// <summary>
    /// The report shows a measurement, so it is rendered by the exact formatter and not the plan
    /// dialog's estimate one. A "~40 s" under a table of finished work claims the stopwatch was
    /// guessing — and the estimate formatter is also where the missing scan time used to hide.
    /// </summary>
    [Fact]
    public void ElapsedText_IsTheExactDuration_WithNoEstimateTilde()
    {
        var vm = new SyncReportDialogViewModel(
            Report(new[] { new SyncStepReport(SyncStepKind.FetchTags, 10, 10, 10, 0, 0) }),
            newFilesDiscovered: 0);

        vm.ElapsedText.Should().Be("1 min 30 s", "the report's stopwatch read 90 seconds");
        vm.ElapsedText.Should().NotContain("~");
    }

    [Fact]
    public void UnexpectedFailures_AreCalledOut()
    {
        var vm = new SyncReportDialogViewModel(
            Report(new[] { new SyncStepReport(SyncStepKind.FetchTags, 10, 10, 9, 0, 1) }, unexpected: 1),
            newFilesDiscovered: 0);

        vm.UnexpectedText.Should().Contain("1").And.ContainEquivalentOf("log");
    }
}
