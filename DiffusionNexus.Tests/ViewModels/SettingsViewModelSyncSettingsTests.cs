using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.ViewModels;

/// <summary>
/// Covers the "Metadata Sync" section of the Settings page: load mapping of the
/// three Task 1 sync columns (<see cref="AppSettings.SyncNotIdentifiedRetryDays"/>,
/// <see cref="AppSettings.SyncErrorRetryDays"/>, <see cref="AppSettings.SyncThumbnailConcurrency"/>),
/// their <c>HasChanges</c> wiring, and — critically — that saving settings does not
/// clobber <see cref="AppSettings.LastLibrarySyncAt"/>. That timestamp is not itself
/// user-editable on this page (Task 5's flow stamps it separately via
/// <c>UpdateLastLibrarySyncAtAsync</c>) but the save command still builds a detached
/// <c>new AppSettings { ... }</c> snapshot, and a field the snapshot forgets is silently
/// defaulted — so it must be carried through explicitly.
/// </summary>
public sealed class SettingsViewModelSyncSettingsTests
{
    private readonly Mock<IAppSettingsService> _settingsService = new();
    private AppSettings _stored = new() { Id = 1 };

    private SettingsViewModel CreateSut()
    {
        _settingsService
            .Setup(s => s.GetSettingsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _stored);

        return new SettingsViewModel(
            _settingsService.Object,
            new Mock<ISecureStorage>().Object);
    }

    [Fact]
    public async Task Load_MapsSyncSettingsColumnsOntoTheViewModel()
    {
        _stored = new AppSettings
        {
            Id = 1,
            SyncNotIdentifiedRetryDays = 60,
            SyncErrorRetryDays = 7,
            SyncThumbnailConcurrency = 8,
        };

        var sut = CreateSut();
        await sut.LoadCommand.ExecuteAsync(null);

        sut.SyncNotIdentifiedRetryDays.Should().Be(60);
        sut.SyncErrorRetryDays.Should().Be(7);
        sut.SyncThumbnailConcurrency.Should().Be(8);
    }

    [Fact]
    public async Task ChangingEachSyncSetting_FlagsHasChanges()
    {
        var sut = CreateSut();
        await sut.LoadCommand.ExecuteAsync(null);
        sut.HasChanges = false;

        sut.SyncNotIdentifiedRetryDays = 60;
        sut.HasChanges.Should().BeTrue("changing the not-identified retry window is a pending edit");
        sut.HasChanges = false;

        sut.SyncErrorRetryDays = 7;
        sut.HasChanges.Should().BeTrue("changing the error retry window is a pending edit");
        sut.HasChanges = false;

        sut.SyncThumbnailConcurrency = 8;
        sut.HasChanges.Should().BeTrue("changing the thumbnail concurrency is a pending edit");
    }

    [Fact]
    public async Task SavingSettings_PreservesLastLibrarySyncAt()
    {
        var knownSyncStamp = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        _stored = new AppSettings
        {
            Id = 1,
            // Disable backups so SaveAsync's backup validation doesn't veto the save.
            BackupDatabaseEnabled = false,
            BackupDatasetImagesEnabled = false,
            LastLibrarySyncAt = knownSyncStamp,
            SyncErrorRetryDays = 1,
        };

        AppSettings? saved = null;
        _settingsService
            .Setup(s => s.SaveSettingsAsync(It.IsAny<AppSettings>(), It.IsAny<CancellationToken>()))
            .Callback<AppSettings, CancellationToken>((s, _) => saved = s)
            .Returns(Task.CompletedTask);

        var sut = CreateSut();
        await sut.LoadCommand.ExecuteAsync(null);

        // An unrelated edit — proves the stamp survives even when a sync setting changes.
        sut.SyncErrorRetryDays = 3;

        await sut.SaveCommand.ExecuteAsync(null);

        saved.Should().NotBeNull();
        saved!.LastLibrarySyncAt.Should().Be(knownSyncStamp,
            "LastLibrarySyncAt is stamped by the sync flow, not this page, and a settings save must not null it out");
        saved.SyncErrorRetryDays.Should().Be(3, "the changed sync setting must still reach the save snapshot");
    }
}
