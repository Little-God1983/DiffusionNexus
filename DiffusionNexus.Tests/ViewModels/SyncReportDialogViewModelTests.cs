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
        int unexpected = 0)
    {
        var options = new SyncOptions(new HashSet<SyncStepKind> { SyncStepKind.IdentifyModel });
        var plan = new SyncPlan(SyncScope.Library, options, Array.Empty<SyncPlanStep>(), DateTimeOffset.UtcNow);
        return new SyncReport(plan, steps, failures ?? Array.Empty<SyncFailure>(), cancelled,
            TimeSpan.FromSeconds(90), NewFilesDiscovered: 0, UnexpectedFailures: unexpected);
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

    [Fact]
    public void UnexpectedFailures_AreCalledOut()
    {
        var vm = new SyncReportDialogViewModel(
            Report(new[] { new SyncStepReport(SyncStepKind.FetchTags, 10, 10, 9, 0, 1) }, unexpected: 1),
            newFilesDiscovered: 0);

        vm.UnexpectedText.Should().Contain("1").And.ContainEquivalentOf("log");
    }
}
