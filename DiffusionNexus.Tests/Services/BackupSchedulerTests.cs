using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Service.Services;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace DiffusionNexus.Tests.Services;

/// <summary>
/// Orchestration contract for <see cref="BackupScheduler"/>: which payloads run based on the two
/// toggles, LastBackupAt update, and next-backup-time computation. Services are mocked and resolved
/// through a real DI scope factory (mirroring the scheduler's own scoping).
/// </summary>
public class BackupSchedulerTests
{
    private static BackupResult Ok() => BackupResult.Succeeded("path", filesBackedUp: 1, totalSizeBytes: 100);

    private sealed class Harness
    {
        public Mock<IAppSettingsService> Settings { get; } = new();
        public Mock<IDatasetBackupService> Dataset { get; } = new();
        public Mock<IDatabaseBackupService> Database { get; } = new();
        public Mock<IActivityLogService> ActivityLog { get; } = new();
        public BackupScheduler Scheduler { get; }

        public Harness(AppSettings settings)
        {
            Settings.Setup(s => s.GetSettingsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(settings);
            Dataset.Setup(s => s.BackupDatasetsAsync(It.IsAny<IProgress<BackupProgress>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Ok());
            Database.Setup(s => s.BackupDatabaseAsync(It.IsAny<IProgress<BackupProgress>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Ok());

            var provider = new ServiceCollection()
                .AddScoped(_ => Settings.Object)
                .AddScoped(_ => Dataset.Object)
                .AddScoped(_ => Database.Object)
                .BuildServiceProvider();

            Scheduler = new BackupScheduler(
                provider.GetRequiredService<IServiceScopeFactory>(),
                ActivityLog.Object,
                unifiedLogger: null);
        }

        public void VerifyDatasetBackedUp(Times times) =>
            Dataset.Verify(s => s.BackupDatasetsAsync(It.IsAny<IProgress<BackupProgress>>(), It.IsAny<CancellationToken>()), times);

        public void VerifyDatabaseBackedUp(Times times) =>
            Database.Verify(s => s.BackupDatabaseAsync(It.IsAny<IProgress<BackupProgress>>(), It.IsAny<CancellationToken>()), times);

        public void VerifyLastBackupUpdated(Times times) =>
            Settings.Verify(s => s.UpdateLastBackupAtAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), times);
    }

    [Fact]
    public async Task RunBackupNowAsync_BothEnabled_BacksUpBoth_AndUpdatesLastBackupAt()
    {
        var h = new Harness(new AppSettings
        {
            BackupDatasetImagesEnabled = true,
            BackupDatabaseEnabled = true,
            DatasetStoragePath = @"C:\ds",
            AutoBackupLocation = @"C:\bk"
        });

        var result = await h.Scheduler.RunBackupNowAsync();

        Assert.True(result.Success, result.ErrorMessage);
        h.VerifyDatasetBackedUp(Times.Once());
        h.VerifyDatabaseBackedUp(Times.Once());
        h.VerifyLastBackupUpdated(Times.Once());
    }

    [Fact]
    public async Task RunBackupNowAsync_OnlyDatabaseEnabled_SkipsDatasets()
    {
        var h = new Harness(new AppSettings
        {
            BackupDatasetImagesEnabled = false,
            BackupDatabaseEnabled = true,
            AutoBackupLocation = @"C:\bk"
        });

        var result = await h.Scheduler.RunBackupNowAsync();

        Assert.True(result.Success, result.ErrorMessage);
        h.VerifyDatasetBackedUp(Times.Never());
        h.VerifyDatabaseBackedUp(Times.Once());
    }

    [Fact]
    public async Task RunBackupNowAsync_OnlyDatasetsEnabled_SkipsDatabase()
    {
        var h = new Harness(new AppSettings
        {
            BackupDatasetImagesEnabled = true,
            BackupDatabaseEnabled = false,
            DatasetStoragePath = @"C:\ds",
            AutoBackupLocation = @"C:\bk"
        });

        var result = await h.Scheduler.RunBackupNowAsync();

        Assert.True(result.Success, result.ErrorMessage);
        h.VerifyDatasetBackedUp(Times.Once());
        h.VerifyDatabaseBackedUp(Times.Never());
    }

    [Fact]
    public async Task RunBackupNowAsync_NothingEnabled_Fails_AndDoesNotTouchLastBackupAt()
    {
        var h = new Harness(new AppSettings
        {
            BackupDatasetImagesEnabled = false,
            BackupDatabaseEnabled = false,
            AutoBackupLocation = @"C:\bk"
        });

        var result = await h.Scheduler.RunBackupNowAsync();

        Assert.False(result.Success);
        h.VerifyLastBackupUpdated(Times.Never());
    }

    [Fact]
    public async Task GetNextBackupTimeAsync_NothingEnabled_ReturnsNull()
    {
        var h = new Harness(new AppSettings
        {
            BackupDatasetImagesEnabled = false,
            BackupDatabaseEnabled = false,
            AutoBackupLocation = @"C:\bk"
        });

        Assert.Null(await h.Scheduler.GetNextBackupTimeAsync());
    }

    [Fact]
    public async Task GetNextBackupTimeAsync_Enabled_ReturnsLastBackupPlusInterval()
    {
        var last = DateTimeOffset.UtcNow.AddHours(-5);
        var h = new Harness(new AppSettings
        {
            BackupDatabaseEnabled = true,
            AutoBackupLocation = @"C:\bk",
            AutoBackupIntervalDays = 0,
            AutoBackupIntervalHours = 2,
            LastBackupAt = last
        });

        var next = await h.Scheduler.GetNextBackupTimeAsync();

        Assert.NotNull(next);
        Assert.True(Math.Abs((next!.Value - last.AddHours(2)).TotalSeconds) < 1);
    }
}
