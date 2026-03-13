using DiffusionNexus.Domain.Services;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.Infrastructure.Services;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.Infrastructure;

#pragma warning disable CS0618 // ActivityLogServiceBridge is intentionally Obsolete

public class ActivityLogServiceBridgeTests
{
    private readonly Mock<IUnifiedLogger> _loggerMock = new();
    private readonly Mock<ITaskTracker> _taskTrackerMock = new();

    private ActivityLogServiceBridge CreateSut() => new(_loggerMock.Object, _taskTrackerMock.Object);

    [Fact]
    public void WhenDownloadCompletesThenStatusBarShowsCompletionMessage()
    {
        // Arrange
        var sut = CreateSut();
        sut.StartDownloadProgress("test-file.safetensors");

        // Simulate progress update that sets status to progress text
        sut.ReportDownloadProgress(99, "291,9 / 292,2 MB");
        sut.CurrentStatus.Should().Be("291,9 / 292,2 MB");

        // Act
        sut.CompleteDownloadProgress(true, "test-file.safetensors downloaded complete — 292,2 / 292,2 MB");

        // Assert
        sut.CurrentStatus.Should().Be("test-file.safetensors downloaded complete — 292,2 / 292,2 MB");
        sut.CurrentStatusSeverity.Should().Be(ActivitySeverity.Success);
        sut.IsDownloadInProgress.Should().BeFalse();
    }

    [Fact]
    public void WhenDownloadFailsThenStatusBarShowsFailureMessage()
    {
        // Arrange
        var sut = CreateSut();
        sut.StartDownloadProgress("test-file.safetensors");
        sut.ReportDownloadProgress(50, "146,1 / 292,2 MB");

        // Act
        sut.CompleteDownloadProgress(false, "Download failed: test-file.safetensors");

        // Assert
        sut.CurrentStatus.Should().Be("Download failed: test-file.safetensors");
        sut.CurrentStatusSeverity.Should().Be(ActivitySeverity.Error);
        sut.IsDownloadInProgress.Should().BeFalse();
    }

    [Fact]
    public void WhenBackupCompletesThenStatusBarShowsCompletionMessage()
    {
        // Arrange
        var sut = CreateSut();
        sut.StartBackupProgress("Backup datasets");
        sut.ReportBackupProgress(99, "Backing up file 23 of 24");

        // Act
        sut.CompleteBackupProgress(true, "Backup completed successfully");

        // Assert
        sut.CurrentStatus.Should().Be("Backup completed successfully");
        sut.CurrentStatusSeverity.Should().Be(ActivitySeverity.Success);
        sut.IsBackupInProgress.Should().BeFalse();
    }

    [Fact]
    public void WhenBackupFailsThenStatusBarShowsFailureMessage()
    {
        // Arrange
        var sut = CreateSut();
        sut.StartBackupProgress("Backup datasets");
        sut.ReportBackupProgress(50, "Backing up file 12 of 24");

        // Act
        sut.CompleteBackupProgress(false, "Backup failed: disk full");

        // Assert
        sut.CurrentStatus.Should().Be("Backup failed: disk full");
        sut.CurrentStatusSeverity.Should().Be(ActivitySeverity.Error);
        sut.IsBackupInProgress.Should().BeFalse();
    }
}
