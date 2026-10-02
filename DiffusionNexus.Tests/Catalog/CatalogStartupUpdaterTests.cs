using DiffusionNexus.Domain.Services;
using DiffusionNexus.Infrastructure.Services;
using DiffusionNexus.Installer.SDK.Catalog.Packaging;
using DiffusionNexus.Installer.SDK.Catalog.Updates;
using DiffusionNexus.UI.Services.Catalog;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.Catalog;

/// <summary>
/// The main app has no catalog-update UI: it checks once in the background after startup, applies
/// what it finds, and logs every outcome to the Unified Console. Nothing may escape the task.
/// </summary>
public class CatalogStartupUpdaterTests
{
    private readonly Mock<ICatalogUpdateService> _updates = new();
    private readonly ActivityLogService _log = new();

    private CatalogStartupUpdater Updater() => new(_updates.Object, _log);

    private static CatalogUpdateCheck Check(
        CatalogUpdateOutcome outcome,
        string? error = null,
        IReadOnlyList<WorkloadChange>? workloads = null,
        int remoteVersion = 6)
        => new(outcome, CatalogChannel.Stable,
            new CatalogManifest { CatalogVersion = remoteVersion },
            new LocalCatalogState(),
            workloads ?? [], [], error);

    private void CheckReturns(CatalogUpdateCheck check)
        => _updates.Setup(u => u.CheckAsync(It.IsAny<CancellationToken>())).ReturnsAsync(check);

    private void VerifyNoApply()
        => _updates.Verify(u => u.ApplyAsync(It.IsAny<CatalogUpdateCheck>(), It.IsAny<CatalogSections>(),
            It.IsAny<IProgress<CatalogDownloadProgress>?>(), It.IsAny<CancellationToken>()), Times.Never);

    private IReadOnlyList<ActivityLogEntry> Entries => _log.GetEntries();

    [Fact]
    public async Task Failed_logs_a_warning_with_the_error_and_does_not_apply()
    {
        CheckReturns(Check(CatalogUpdateOutcome.Failed, error: "no network"));

        await Updater().RunAsync();

        Entries.Should().ContainSingle(e => e.Severity == ActivitySeverity.Warning)
            .Which.Should().Match<ActivityLogEntry>(e => (e.Message + e.Details).Contains("no network"));
        VerifyNoApply();
    }

    [Fact]
    public async Task OverrideActive_logs_that_updates_are_off_and_does_not_apply()
    {
        CheckReturns(Check(CatalogUpdateOutcome.OverrideActive));

        await Updater().RunAsync();

        Entries.Should().ContainSingle(e => e.Severity == ActivitySeverity.Info && e.Message.Contains("override active"));
        VerifyNoApply();
    }

    [Fact]
    public async Task UpToDate_logs_info_and_does_not_apply()
    {
        CheckReturns(Check(CatalogUpdateOutcome.UpToDate));

        await Updater().RunAsync();

        Entries.Should().ContainSingle(e => e.Severity == ActivitySeverity.Info && e.Message.Contains("up to date"));
        VerifyNoApply();
    }

    [Fact]
    public async Task RequiresNewerSoftware_warns_and_does_not_apply()
    {
        CheckReturns(Check(CatalogUpdateOutcome.RequiresNewerSoftware));

        await Updater().RunAsync();

        Entries.Should().ContainSingle(e => e.Severity == ActivitySeverity.Warning
            && e.Message.Contains("needs a newer Diffusion Nexus"));
        VerifyNoApply();
    }

    [Fact]
    public async Task UpdatesAvailable_applies_every_section_once_and_logs_each_change()
    {
        var check = Check(CatalogUpdateOutcome.UpdatesAvailable, workloads:
        [
            new WorkloadChange(Guid.NewGuid(), "Krea-2-Turbo", ChangeKind.Updated, "2.1", "2.2"),
            new WorkloadChange(Guid.NewGuid(), "Brand New", ChangeKind.Added, null, "1.0")
        ]);
        CheckReturns(check);
        _updates.Setup(u => u.ApplyAsync(check, CatalogSections.All, It.IsAny<IProgress<CatalogDownloadProgress>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CatalogApplyResult(CatalogSections.All, CatalogSections.None, null));

        await Updater().RunAsync();

        _updates.Verify(u => u.ApplyAsync(check, CatalogSections.All, It.IsAny<IProgress<CatalogDownloadProgress>?>(), It.IsAny<CancellationToken>()), Times.Once);
        Entries.Should().Contain(e => e.Message.Contains("Updated") && e.Message.Contains("Krea-2-Turbo") && e.Message.Contains("2.1") && e.Message.Contains("2.2"));
        Entries.Should().Contain(e => e.Message.Contains("Added") && e.Message.Contains("Brand New") && e.Message.Contains("1.0"));
        Entries.Should().ContainSingle(e => e.Severity == ActivitySeverity.Success && e.Message.Contains("v6"));
    }

    [Fact]
    public async Task A_partly_failed_apply_warns_with_the_failed_sections_and_the_error()
    {
        var check = Check(CatalogUpdateOutcome.UpdatesAvailable);
        CheckReturns(check);
        _updates.Setup(u => u.ApplyAsync(check, CatalogSections.All, It.IsAny<IProgress<CatalogDownloadProgress>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CatalogApplyResult(CatalogSections.Workloads, CatalogSections.Workflows, "file locked"));

        await Updater().RunAsync();

        Entries.Should().ContainSingle(e => e.Severity == ActivitySeverity.Warning)
            .Which.Should().Match<ActivityLogEntry>(e => (e.Message + e.Details).Contains("Workflows") && (e.Message + e.Details).Contains("file locked"));
        Entries.Should().NotContain(e => e.Severity == ActivitySeverity.Success);
    }

    [Fact]
    public async Task A_throwing_check_is_logged_and_nothing_escapes()
    {
        _updates.Setup(u => u.CheckAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException("boom"));

        var act = () => Updater().RunAsync();

        await act.Should().NotThrowAsync();
        Entries.Should().ContainSingle(e => e.Severity == ActivitySeverity.Warning);
        VerifyNoApply();
    }

    [Fact]
    public async Task A_throwing_apply_is_logged_and_nothing_escapes()
    {
        var check = Check(CatalogUpdateOutcome.UpdatesAvailable);
        CheckReturns(check);
        _updates.Setup(u => u.ApplyAsync(check, CatalogSections.All, It.IsAny<IProgress<CatalogDownloadProgress>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("disk full"));

        var act = () => Updater().RunAsync();

        await act.Should().NotThrowAsync();
        Entries.Should().Contain(e => e.Severity == ActivitySeverity.Warning && (e.Message + e.Details).Contains("disk full"));
    }

    [Fact]
    public async Task Every_entry_uses_the_catalog_source()
    {
        CheckReturns(Check(CatalogUpdateOutcome.UpToDate));

        await Updater().RunAsync();

        Entries.Should().OnlyContain(e => e.Source == CatalogStartupUpdater.LogSource);
        CatalogStartupUpdater.LogSource.Should().Be("Catalog");
    }
}
