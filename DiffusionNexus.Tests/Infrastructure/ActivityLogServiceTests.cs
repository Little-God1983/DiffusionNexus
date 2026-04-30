using DiffusionNexus.Domain.Services;
using DiffusionNexus.Infrastructure.Services;
using FluentAssertions;

namespace DiffusionNexus.Tests.Infrastructure;

public class ActivityLogServiceTests
{
    private static ActivityLogService CreateSut(int? maxEntries = null)
    {
        var sut = new ActivityLogService();
        if (maxEntries.HasValue) sut.MaxEntries = maxEntries.Value;
        return sut;
    }

    [Fact]
    public void Log_AddsEntryFiresEntryAddedAndUpdatesStatus()
    {
        var sut = CreateSut();
        ActivityLogEntry? raised = null;
        sut.EntryAdded += (_, e) => raised = e;
        var statusEvents = 0;
        sut.StatusChanged += (_, _) => statusEvents++;

        sut.LogInfo("src", "hello");

        sut.GetEntries().Should().ContainSingle();
        raised.Should().NotBeNull();
        raised!.Message.Should().Be("hello");
        sut.CurrentStatus.Should().Be("hello");
        sut.CurrentStatusSeverity.Should().Be(ActivitySeverity.Info);
        statusEvents.Should().Be(1);
    }

    [Fact]
    public void Log_PrunesOldestEntries_WhenAboveMaxEntries()
    {
        var sut = CreateSut(maxEntries: 3);

        for (int i = 0; i < 10; i++)
            sut.LogInfo("src", $"m{i}");

        var entries = sut.GetEntries();
        entries.Should().HaveCount(3);
        entries.Select(e => e.Message).Should().Equal("m7", "m8", "m9");
    }

    [Fact]
    public void GetEntries_BySeverity_FiltersOutLowerLevels()
    {
        var sut = CreateSut();
        sut.LogInfo("s", "i");
        sut.LogWarning("s", "w");
        sut.LogError("s", "e", (string?)null);

        var filtered = sut.GetEntries(ActivitySeverity.Warning);

        filtered.Select(e => e.Message).Should().BeEquivalentTo(new[] { "w", "e" });
    }

    [Fact]
    public void GetEntries_BySource_IsCaseInsensitive()
    {
        var sut = CreateSut();
        sut.LogInfo("Backup", "a");
        sut.LogInfo("Import", "b");
        sut.LogInfo("backup", "c");

        var filtered = sut.GetEntries("BACKUP");

        filtered.Select(e => e.Message).Should().BeEquivalentTo(new[] { "a", "c" });
    }

    [Fact]
    public void LogError_WithException_RecordsExceptionMessageInDetails()
    {
        var sut = CreateSut();
        var ex = new InvalidOperationException("boom");

        sut.LogError("src", "failed", ex);

        var entry = sut.GetEntries().Single();
        entry.Severity.Should().Be(ActivitySeverity.Error);
        entry.Details.Should().Be("boom");
    }

    [Fact]
    public void ClearLog_RemovesAllEntriesAndFiresEvent()
    {
        var sut = CreateSut();
        sut.LogInfo("s", "m");
        var fired = false;
        sut.LogCleared += (_, _) => fired = true;

        sut.ClearLog();

        sut.GetEntries().Should().BeEmpty();
        fired.Should().BeTrue();
    }

    [Fact]
    public void StartOperation_FiresOperationStartedAndAddsToActiveList()
    {
        var sut = CreateSut();
        ProgressOperation? started = null;
        sut.OperationStarted += (_, op) => started = op;

        var op = sut.StartOperation("backup", "Backup");

        started.Should().BeSameAs(op);
        sut.GetActiveOperations().Should().ContainSingle().Which.Should().BeSameAs(op);
    }

    [Fact]
    public void StartOperation_DisposeFiresOperationCompletedAndRemovesFromActiveList()
    {
        var sut = CreateSut();
        ProgressOperation? completed = null;
        sut.OperationCompleted += (_, op) => completed = op;

        var op = sut.StartOperation("backup", "Backup");
        op.Dispose();

        completed.Should().BeSameAs(op);
        sut.GetActiveOperations().Should().BeEmpty();
    }

    [Fact]
    public void SetStatus_UpdatesCurrentStatusAndFiresEvent()
    {
        var sut = CreateSut();
        var fired = 0;
        sut.StatusChanged += (_, _) => fired++;

        sut.SetStatus("hello", ActivitySeverity.Warning);

        sut.CurrentStatus.Should().Be("hello");
        sut.CurrentStatusSeverity.Should().Be(ActivitySeverity.Warning);
        fired.Should().Be(1);
    }

    [Fact]
    public void ClearStatus_ResetsToReady()
    {
        var sut = CreateSut();
        sut.SetStatus("busy", ActivitySeverity.Warning);

        sut.ClearStatus();

        sut.CurrentStatus.Should().Be("Ready");
        sut.CurrentStatusSeverity.Should().Be(ActivitySeverity.Info);
    }

    [Fact]
    public void StartBackupProgress_SetsStateAndFiresEvent()
    {
        var sut = CreateSut();
        var fired = 0;
        sut.BackupProgressChanged += (_, _) => fired++;

        sut.StartBackupProgress("Datasets");

        sut.IsBackupInProgress.Should().BeTrue();
        sut.BackupProgressPercent.Should().Be(0);
        sut.BackupOperationName.Should().Be("Datasets");
        fired.Should().Be(1);
    }

    [Fact]
    public void ReportBackupProgress_ClampsPercentTo0To100()
    {
        var sut = CreateSut();
        sut.StartBackupProgress("Datasets");

        sut.ReportBackupProgress(150);
        sut.BackupProgressPercent.Should().Be(100);

        sut.ReportBackupProgress(-5);
        sut.BackupProgressPercent.Should().Be(0);
    }

    [Fact]
    public void CompleteBackupProgress_ResetsStateAndLogs()
    {
        var sut = CreateSut();
        sut.StartBackupProgress("Datasets");

        sut.CompleteBackupProgress(true, "done");

        sut.IsBackupInProgress.Should().BeFalse();
        sut.BackupProgressPercent.Should().BeNull();
        sut.BackupOperationName.Should().BeNull();
        sut.GetEntries().Should().Contain(e => e.Message == "done" && e.Severity == ActivitySeverity.Success);
    }

    [Fact]
    public void CompleteBackupProgress_FailureLogsError()
    {
        var sut = CreateSut();
        sut.StartBackupProgress("Datasets");

        sut.CompleteBackupProgress(false, "boom");

        sut.GetEntries().Should().Contain(e => e.Message == "boom" && e.Severity == ActivitySeverity.Error);
    }

    [Fact]
    public void StartDownloadProgress_SetsStateAndFiresEvent()
    {
        var sut = CreateSut();
        var fired = 0;
        sut.DownloadProgressChanged += (_, _) => fired++;

        sut.StartDownloadProgress("file.bin");

        sut.IsDownloadInProgress.Should().BeTrue();
        sut.DownloadProgressPercent.Should().Be(0);
        sut.DownloadOperationName.Should().Be("file.bin");
        fired.Should().Be(1);
    }

    [Fact]
    public void ReportDownloadProgress_ClampsPercentTo0To100()
    {
        var sut = CreateSut();
        sut.StartDownloadProgress("file.bin");

        sut.ReportDownloadProgress(200);
        sut.DownloadProgressPercent.Should().Be(100);

        sut.ReportDownloadProgress(-10);
        sut.DownloadProgressPercent.Should().Be(0);
    }

    [Fact]
    public void CompleteDownloadProgress_ResetsStateAndLogsSuccess()
    {
        var sut = CreateSut();
        sut.StartDownloadProgress("file.bin");

        sut.CompleteDownloadProgress(true, "complete");

        sut.IsDownloadInProgress.Should().BeFalse();
        sut.DownloadProgressPercent.Should().BeNull();
        sut.DownloadOperationName.Should().BeNull();
        sut.GetEntries().Should().Contain(e => e.Message == "complete" && e.Severity == ActivitySeverity.Success);
    }

    [Fact]
    public void Log_NullEntry_Throws()
    {
        var sut = CreateSut();

        var act = () => sut.Log(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
