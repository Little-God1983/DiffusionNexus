using DiffusionNexus.DataAccess.Data;
using DiffusionNexus.Domain.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DiffusionNexus.Tests.DataAccess;

/// <summary>
/// Guards the core-DB migration chain — in particular the AddDatabaseBackupSettings migration that
/// renames AutoBackupEnabled → BackupDatasetImagesEnabled and adds BackupDatabaseEnabled. The real
/// app applies these migrations at startup, so a broken migration or snapshot would stop the app
/// from launching; this test catches that on a throwaway file database.
/// </summary>
public class CoreDbMigrationTests
{
    [Fact]
    public void AllMigrations_ApplyCleanly_AndBackupFlagsRoundTrip()
    {
        var tempDir = Directory.CreateTempSubdirectory();
        var dbPath = Path.Combine(tempDir.FullName, "migration-test.db");
        try
        {
            var options = new DbContextOptionsBuilder<DiffusionNexusCoreDbContext>()
                .UseSqlite($"Data Source={dbPath};Pooling=False")
                .Options;

            using (var ctx = new DiffusionNexusCoreDbContext(options))
            {
                ctx.Database.Migrate();

                var settings = ctx.AppSettings.FirstOrDefault() ?? new AppSettings { Id = 1 };
                settings.BackupDatasetImagesEnabled = true;
                settings.BackupDatabaseEnabled = false;
                if (ctx.Entry(settings).State == EntityState.Detached)
                    ctx.AppSettings.Add(settings);
                ctx.SaveChanges();
            }

            using (var ctx = new DiffusionNexusCoreDbContext(options))
            {
                var settings = ctx.AppSettings.Single();
                Assert.True(settings.BackupDatasetImagesEnabled);
                Assert.False(settings.BackupDatabaseEnabled);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            tempDir.Delete(recursive: true);
        }
    }
}
