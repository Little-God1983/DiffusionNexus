using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Service.Services;
using Microsoft.Data.Sqlite;
using Moq;

namespace DiffusionNexus.Tests.Services;

/// <summary>
/// Behavior of <see cref="DatabaseBackupService"/> — the core-DB backup added alongside the
/// dataset-image backup. Uses a real temp SQLite file so VACUUM INTO is exercised for real.
/// </summary>
public class DatabaseBackupServiceTests
{
    private static void CreateSourceDb(string path)
    {
        using var conn = new SqliteConnection($"Data Source={path};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "CREATE TABLE Widgets(Id INTEGER PRIMARY KEY, Name TEXT);" +
            "INSERT INTO Widgets(Name) VALUES('alpha');";
        cmd.ExecuteNonQuery();
    }

    private static Mock<IAppSettingsService> SettingsReturning(AppSettings settings)
    {
        var mock = new Mock<IAppSettingsService>();
        mock.Setup(s => s.GetSettingsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(settings);
        return mock;
    }

    [Fact]
    public async Task BackupDatabaseAsync_WritesConsistentCopy_ContainingSourceData()
    {
        var work = Directory.CreateTempSubdirectory();
        try
        {
            var srcDb = Path.Combine(work.FullName, "source.db");
            var backupDir = Path.Combine(work.FullName, "backups");
            Directory.CreateDirectory(backupDir);
            CreateSourceDb(srcDb);

            var settings = new AppSettings { AutoBackupLocation = backupDir, MaxBackups = 10 };
            var service = new DatabaseBackupService(SettingsReturning(settings).Object, () => srcDb);

            var result = await service.BackupDatabaseAsync();

            Assert.True(result.Success, result.ErrorMessage);

            var backups = Directory.GetFiles(backupDir, "DatabaseBackup_*.db");
            Assert.Single(backups);

            using var conn = new SqliteConnection($"Data Source={backups[0]};Mode=ReadOnly;Pooling=False");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Name FROM Widgets WHERE Id = 1";
            Assert.Equal("alpha", (string?)cmd.ExecuteScalar());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            work.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task BackupDatabaseAsync_EnforcesMaxBackups_DeletingOldest()
    {
        var work = Directory.CreateTempSubdirectory();
        try
        {
            var srcDb = Path.Combine(work.FullName, "source.db");
            var backupDir = Path.Combine(work.FullName, "backups");
            Directory.CreateDirectory(backupDir);
            CreateSourceDb(srcDb);

            // Three pre-existing DB backups; old0 is the oldest.
            var now = DateTime.UtcNow;
            for (var i = 0; i < 3; i++)
            {
                var p = Path.Combine(backupDir, $"DatabaseBackup_old{i}.db");
                await File.WriteAllTextAsync(p, "x");
                File.SetCreationTimeUtc(p, now.AddHours(-10 + i));
            }

            var settings = new AppSettings { AutoBackupLocation = backupDir, MaxBackups = 3 };
            var service = new DatabaseBackupService(SettingsReturning(settings).Object, () => srcDb);

            var result = await service.BackupDatabaseAsync();

            Assert.True(result.Success, result.ErrorMessage);
            var remaining = Directory.GetFiles(backupDir, "DatabaseBackup_*.db");
            Assert.Equal(3, remaining.Length);
            Assert.DoesNotContain(remaining, f => Path.GetFileName(f) == "DatabaseBackup_old0.db");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            work.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task BackupDatabaseAsync_WhenBackupLocationMissing_Fails()
    {
        var settings = new AppSettings { AutoBackupLocation = null, MaxBackups = 10 };
        var service = new DatabaseBackupService(SettingsReturning(settings).Object, () => "ignored.db");

        var result = await service.BackupDatabaseAsync();

        Assert.False(result.Success);
    }

    [Fact]
    public async Task BackupDatabaseAsync_WhenSourceDatabaseMissing_Fails()
    {
        var work = Directory.CreateTempSubdirectory();
        try
        {
            var backupDir = Path.Combine(work.FullName, "backups");
            Directory.CreateDirectory(backupDir);
            var settings = new AppSettings { AutoBackupLocation = backupDir, MaxBackups = 10 };
            var missing = Path.Combine(work.FullName, "does-not-exist.db");
            var service = new DatabaseBackupService(SettingsReturning(settings).Object, () => missing);

            var result = await service.BackupDatabaseAsync();

            Assert.False(result.Success);
            Assert.Empty(Directory.GetFiles(backupDir, "DatabaseBackup_*.db"));
        }
        finally
        {
            work.Delete(recursive: true);
        }
    }
}
