using System.IO.Compression;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Service.Services;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.Services;

/// <summary>
/// Covers the untested half of <see cref="DatasetBackupService"/> (issue #443): real ZIP creation,
/// the destructive restore (which wipes and repopulates the dataset folder), <c>MaxBackups</c>
/// retention pruning (which deletes archive files), and the <c>_isOperationInProgress</c>
/// re-entrancy guard.
///
/// DESTRUCTIVE-OP SAFETY: every test drives the service at throwaway temp directories created per
/// test and deleted in <see cref="Dispose"/>. Restore only ever wipes a temp DatasetStoragePath;
/// pruning only ever deletes DatasetBackup_*.zip files inside a temp backup folder.
/// (The existing <c>DatasetBackupAnalysisTests</c> already covers <c>AnalyzeBackupAsync</c>.)
/// </summary>
public class DatasetBackupServiceTests : IDisposable
{
    private readonly string _root;

    public DatasetBackupServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"dsbackup_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static Mock<IAppSettingsService> SettingsReturning(AppSettings settings)
    {
        var mock = new Mock<IAppSettingsService>();
        mock.Setup(s => s.GetSettingsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(settings);
        return mock;
    }

    private string NewDir(string name)
    {
        var p = Path.Combine(_root, name);
        Directory.CreateDirectory(p);
        return p;
    }

    // ── Backup: ZIP creation ──

    [Fact]
    public async Task BackupDatasetsAsync_CreatesTimestampedZip_WithRelativePaths_AndReportsCounts()
    {
        var storage = NewDir("data");
        var backupDir = NewDir("backups");
        Directory.CreateDirectory(Path.Combine(storage, "char"));
        await File.WriteAllTextAsync(Path.Combine(storage, "char", "img.txt"), "hello");   // 5 bytes
        await File.WriteAllTextAsync(Path.Combine(storage, "root.txt"), "world!!");         // 7 bytes

        var settings = new AppSettings { DatasetStoragePath = storage, AutoBackupLocation = backupDir, MaxBackups = 10 };
        var settingsMock = SettingsReturning(settings);
        var service = new DatasetBackupService(settingsMock.Object);

        var result = await service.BackupDatasetsAsync();

        result.Success.Should().BeTrue(result.ErrorMessage);
        result.FilesBackedUp.Should().Be(2);
        result.TotalSizeBytes.Should().Be(12);

        var zips = Directory.GetFiles(backupDir, "DatasetBackup_*.zip");
        zips.Should().ContainSingle();
        Path.GetFileName(result.BackupPath!).Should().MatchRegex(@"^DatasetBackup_\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}\.zip$");

        using var archive = ZipFile.OpenRead(zips[0]);
        var entryNames = archive.Entries.Select(e => e.FullName.Replace('\\', '/')).ToList();
        entryNames.Should().BeEquivalentTo(new[] { "char/img.txt", "root.txt" });

        settingsMock.Verify(s => s.UpdateLastBackupAtAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task BackupDatasetsAsync_RoundTripsThroughRestore_IntoAFreshFolder()
    {
        var storage = NewDir("src");
        var backupDir = NewDir("backups");
        Directory.CreateDirectory(Path.Combine(storage, "sub"));
        await File.WriteAllTextAsync(Path.Combine(storage, "sub", "a.txt"), "alpha");
        await File.WriteAllTextAsync(Path.Combine(storage, "b.png"), "PNGDATA");

        var settings = new AppSettings { DatasetStoragePath = storage, AutoBackupLocation = backupDir, MaxBackups = 10 };
        var service = new DatasetBackupService(SettingsReturning(settings).Object);

        var backup = await service.BackupDatasetsAsync();
        backup.Success.Should().BeTrue(backup.ErrorMessage);

        // Restore into a fresh, empty folder by pointing DatasetStoragePath at it.
        var restoreDir = NewDir("restored");
        settings.DatasetStoragePath = restoreDir;

        var restore = await service.RestoreBackupAsync(backup.BackupPath!);

        restore.Success.Should().BeTrue(restore.ErrorMessage);
        restore.FilesRestored.Should().Be(2);
        (await File.ReadAllTextAsync(Path.Combine(restoreDir, "sub", "a.txt"))).Should().Be("alpha");
        (await File.ReadAllTextAsync(Path.Combine(restoreDir, "b.png"))).Should().Be("PNGDATA");
    }

    [Fact]
    public async Task BackupDatasetsAsync_WhenDatasetPathNotConfigured_Fails()
    {
        var settings = new AppSettings { DatasetStoragePath = null, AutoBackupLocation = NewDir("b"), MaxBackups = 10 };
        var service = new DatasetBackupService(SettingsReturning(settings).Object);

        var result = await service.BackupDatasetsAsync();

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("not configured");
    }

    [Fact]
    public async Task BackupDatasetsAsync_WhenDatasetPathDoesNotExist_Fails()
    {
        var settings = new AppSettings
        {
            DatasetStoragePath = Path.Combine(_root, "missing"),
            AutoBackupLocation = NewDir("b"),
            MaxBackups = 10
        };
        var service = new DatasetBackupService(SettingsReturning(settings).Object);

        var result = await service.BackupDatasetsAsync();

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("does not exist");
    }

    [Fact]
    public async Task BackupDatasetsAsync_WhenBackupLocationNotConfigured_Fails()
    {
        var settings = new AppSettings { DatasetStoragePath = NewDir("data"), AutoBackupLocation = null, MaxBackups = 10 };
        var service = new DatasetBackupService(SettingsReturning(settings).Object);

        var result = await service.BackupDatasetsAsync();

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Backup location");
    }

    [Fact]
    public async Task BackupDatasetsAsync_WhenCancelled_Fails_AndDeletesPartialArchive()
    {
        var storage = NewDir("data");
        var backupDir = NewDir("backups");
        await File.WriteAllTextAsync(Path.Combine(storage, "one.txt"), "x"); // ensures the file loop runs

        var settings = new AppSettings { DatasetStoragePath = storage, AutoBackupLocation = backupDir, MaxBackups = 10 };
        var service = new DatasetBackupService(SettingsReturning(settings).Object);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await service.BackupDatasetsAsync(cancellationToken: cts.Token);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("cancelled");
        Directory.GetFiles(backupDir, "DatasetBackup_*.zip").Should().BeEmpty("the partial archive must be cleaned up");
    }

    // ── MaxBackups retention pruning (deletes archive files) ──

    [Fact]
    public async Task BackupDatasetsAsync_PrunesOldestArchives_KeepingNewestMaxBackups()
    {
        var storage = NewDir("data");
        var backupDir = NewDir("backups");
        await File.WriteAllTextAsync(Path.Combine(storage, "f.txt"), "content");

        // Three pre-existing archives; old0 is oldest by CreationTimeUtc (the prune's sort key).
        var now = DateTime.UtcNow;
        for (var i = 0; i < 3; i++)
        {
            var p = Path.Combine(backupDir, $"DatasetBackup_old{i}.zip");
            await File.WriteAllTextAsync(p, "x");
            File.SetCreationTimeUtc(p, now.AddHours(-10 + i));
        }

        var settings = new AppSettings { DatasetStoragePath = storage, AutoBackupLocation = backupDir, MaxBackups = 3 };
        var service = new DatasetBackupService(SettingsReturning(settings).Object);

        var result = await service.BackupDatasetsAsync();

        result.Success.Should().BeTrue(result.ErrorMessage);
        var remaining = Directory.GetFiles(backupDir, "DatasetBackup_*.zip").Select(Path.GetFileName).ToList();
        remaining.Should().HaveCount(3, "count is capped at MaxBackups after the new archive is added");
        remaining.Should().NotContain("DatasetBackup_old0.zip", "the single oldest archive is pruned");
        remaining.Should().Contain("DatasetBackup_old1.zip");
        remaining.Should().Contain("DatasetBackup_old2.zip");
        remaining.Should().Contain(Path.GetFileName(result.BackupPath));
    }

    [Fact]
    public async Task BackupDatasetsAsync_WhenMaxBackupsZero_PruningIsDisabled()
    {
        var storage = NewDir("data");
        var backupDir = NewDir("backups");
        await File.WriteAllTextAsync(Path.Combine(storage, "f.txt"), "content");
        for (var i = 0; i < 2; i++)
            await File.WriteAllTextAsync(Path.Combine(backupDir, $"DatasetBackup_old{i}.zip"), "x");

        var settings = new AppSettings { DatasetStoragePath = storage, AutoBackupLocation = backupDir, MaxBackups = 0 };
        var service = new DatasetBackupService(SettingsReturning(settings).Object);

        var result = await service.BackupDatasetsAsync();

        result.Success.Should().BeTrue(result.ErrorMessage);
        Directory.GetFiles(backupDir, "DatasetBackup_*.zip").Should().HaveCount(3, "MaxBackups <= 0 disables pruning");
    }

    // ── Restore: destructive replace ──

    [Fact]
    public async Task RestoreBackupAsync_WipesExistingContents_ThenExtractsArchive()
    {
        // Existing (soon-to-be-destroyed) contents in the throwaway target folder.
        var target = NewDir("target");
        Directory.CreateDirectory(Path.Combine(target, "oldsub"));
        await File.WriteAllTextAsync(Path.Combine(target, "oldsub", "old.txt"), "OLD");
        await File.WriteAllTextAsync(Path.Combine(target, "oldroot.txt"), "OLDROOT");

        // Independent archive with different contents.
        var staging = NewDir("staging");
        Directory.CreateDirectory(Path.Combine(staging, "newsub"));
        await File.WriteAllTextAsync(Path.Combine(staging, "newsub", "new.txt"), "NEW");
        await File.WriteAllTextAsync(Path.Combine(staging, "newroot.txt"), "NEWROOT");
        var zip = Path.Combine(_root, "DatasetBackup_x.zip");
        ZipFile.CreateFromDirectory(staging, zip);

        var settings = new AppSettings { DatasetStoragePath = target, AutoBackupLocation = NewDir("b"), MaxBackups = 10 };
        var service = new DatasetBackupService(SettingsReturning(settings).Object);

        var result = await service.RestoreBackupAsync(zip);

        result.Success.Should().BeTrue(result.ErrorMessage);
        result.FilesRestored.Should().Be(2);
        Directory.Exists(Path.Combine(target, "oldsub")).Should().BeFalse("old subfolders are wiped");
        File.Exists(Path.Combine(target, "oldroot.txt")).Should().BeFalse("old root files are wiped");
        (await File.ReadAllTextAsync(Path.Combine(target, "newsub", "new.txt"))).Should().Be("NEW");
        (await File.ReadAllTextAsync(Path.Combine(target, "newroot.txt"))).Should().Be("NEWROOT");
    }

    [Fact]
    public async Task RestoreBackupAsync_NullPath_Throws()
    {
        var service = new DatasetBackupService(SettingsReturning(new AppSettings()).Object);
        var act = async () => await service.RestoreBackupAsync(null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task RestoreBackupAsync_MissingArchive_Fails()
    {
        var service = new DatasetBackupService(SettingsReturning(new AppSettings { DatasetStoragePath = NewDir("d") }).Object);

        var result = await service.RestoreBackupAsync(Path.Combine(_root, "nope.zip"));

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("not found");
    }

    [Fact]
    public async Task RestoreBackupAsync_WhenDatasetPathNotConfigured_Fails()
    {
        var zip = Path.Combine(_root, "DatasetBackup_empty.zip");
        ZipFile.CreateFromDirectory(NewDir("emptysrc"), zip);
        var service = new DatasetBackupService(SettingsReturning(new AppSettings { DatasetStoragePath = null }).Object);

        var result = await service.RestoreBackupAsync(zip);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("not configured");
    }

    // ── Re-entrancy guard (_isOperationInProgress) ──

    [Fact]
    public async Task BackupDatasetsAsync_WhileAnotherOperationInProgress_Fails()
    {
        // Park the first backup at its very first await (GetSettingsAsync) so the guard is set but the
        // operation has not completed, then fire a second one deterministically.
        var gate = new TaskCompletionSource<AppSettings>();
        var mock = new Mock<IAppSettingsService>();
        mock.Setup(s => s.GetSettingsAsync(It.IsAny<CancellationToken>())).Returns(gate.Task);
        var service = new DatasetBackupService(mock.Object);

        var first = service.BackupDatasetsAsync();
        service.IsOperationInProgress.Should().BeTrue("the guard is set synchronously before the first await");

        var second = await service.BackupDatasetsAsync();
        second.Success.Should().BeFalse();
        second.ErrorMessage.Should().Contain("already in progress");

        // Release the first with an invalid config so it fails fast and clears the guard.
        gate.SetResult(new AppSettings { DatasetStoragePath = null });
        (await first).Success.Should().BeFalse();
        service.IsOperationInProgress.Should().BeFalse();
    }

    [Fact]
    public async Task RestoreBackupAsync_WhileBackupInProgress_Fails()
    {
        var gate = new TaskCompletionSource<AppSettings>();
        var mock = new Mock<IAppSettingsService>();
        mock.Setup(s => s.GetSettingsAsync(It.IsAny<CancellationToken>())).Returns(gate.Task);
        var service = new DatasetBackupService(mock.Object);

        var backup = service.BackupDatasetsAsync();
        service.IsOperationInProgress.Should().BeTrue();

        // A real, existing archive — proves the guard is checked BEFORE the file-exists check.
        var zip = Path.Combine(_root, "DatasetBackup_r.zip");
        ZipFile.CreateFromDirectory(NewDir("rsrc"), zip);

        var restore = await service.RestoreBackupAsync(zip);
        restore.Success.Should().BeFalse();
        restore.ErrorMessage.Should().Contain("already in progress");

        gate.SetResult(new AppSettings { DatasetStoragePath = null });
        await backup;
    }
}
