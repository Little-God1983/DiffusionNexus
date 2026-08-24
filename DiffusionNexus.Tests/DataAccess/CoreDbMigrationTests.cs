using DiffusionNexus.DataAccess.Data;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using FluentAssertions;
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

    /// <summary>
    /// The ModelSyncStates table added by AddModelSyncStateAndThumbnailAttempts is reachable through
    /// the DbSet, stores the outcome enum as text, and disappears with its model (PK == FK, cascade).
    /// </summary>
    [Fact]
    public void ModelSyncState_RoundTripsThroughDbSet_AndCascadeDeletesWithItsModel()
    {
        var tempDir = Directory.CreateTempSubdirectory();
        var dbPath = Path.Combine(tempDir.FullName, "syncstate-test.db");
        try
        {
            var options = new DbContextOptionsBuilder<DiffusionNexusCoreDbContext>()
                .UseSqlite($"Data Source={dbPath};Pooling=False")
                .Options;

            using (var ctx = new DiffusionNexusCoreDbContext(options))
            {
                ctx.Database.Migrate();
                ctx.Models.Add(new Model
                {
                    Name = "sync-state-model",
                    SyncState = new ModelSyncState
                    {
                        MetadataOutcome = SyncOutcome.NotIdentified,
                        MetadataAttempts = 2,
                        LastError = "no source identified the model",
                    }
                });
                ctx.SaveChanges();
            }

            int modelId;
            using (var ctx = new DiffusionNexusCoreDbContext(options))
            {
                var model = ctx.Models.Include(m => m.SyncState).Single();
                modelId = model.Id;
                model.SyncState.Should().NotBeNull();
                model.SyncState!.ModelId.Should().Be(modelId);
                model.SyncState.MetadataOutcome.Should().Be(SyncOutcome.NotIdentified);
                model.SyncState.MetadataAttempts.Should().Be(2);
                model.SyncState.LastError.Should().Be("no source identified the model");

                // Stored as text, not as the enum's ordinal.
                var stored = ctx.Database.SqlQueryRaw<string>(
                    "SELECT MetadataOutcome AS Value FROM ModelSyncStates").Single();
                stored.Should().Be(nameof(SyncOutcome.NotIdentified));
            }

            using (var ctx = new DiffusionNexusCoreDbContext(options))
            {
                ctx.Models.Remove(ctx.Models.Single(m => m.Id == modelId));
                ctx.SaveChanges();
            }

            using (var ctx = new DiffusionNexusCoreDbContext(options))
            {
                ctx.ModelSyncStates.Should().BeEmpty("the state row cascades with its model");
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            tempDir.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Mirrors the <see cref="SyncOutcome.NotIdentified"/> string round-trip above for
    /// <see cref="SyncOutcome.Header"/> (Plan C) — the identify chain's safetensors-header rung —
    /// so a future member rename cannot silently change what is stored on disk for it either.
    /// </summary>
    [Fact]
    public void ModelSyncState_HeaderOutcome_RoundTripsAsStoredString()
    {
        var tempDir = Directory.CreateTempSubdirectory();
        var dbPath = Path.Combine(tempDir.FullName, "syncstate-header-test.db");
        try
        {
            var options = new DbContextOptionsBuilder<DiffusionNexusCoreDbContext>()
                .UseSqlite($"Data Source={dbPath};Pooling=False")
                .Options;

            using (var ctx = new DiffusionNexusCoreDbContext(options))
            {
                ctx.Database.Migrate();
                ctx.Models.Add(new Model
                {
                    Name = "header-outcome-model",
                    SyncState = new ModelSyncState
                    {
                        MetadataOutcome = SyncOutcome.Header,
                        HeaderCheckedAt = DateTimeOffset.UtcNow,
                    }
                });
                ctx.SaveChanges();
            }

            using (var ctx = new DiffusionNexusCoreDbContext(options))
            {
                var model = ctx.Models.Include(m => m.SyncState).Single();
                model.SyncState.Should().NotBeNull();
                model.SyncState!.MetadataOutcome.Should().Be(SyncOutcome.Header);
                model.SyncState.HeaderCheckedAt.Should().NotBeNull();

                // Stored as text, not as the enum's ordinal.
                var stored = ctx.Database.SqlQueryRaw<string>(
                    "SELECT MetadataOutcome AS Value FROM ModelSyncStates").Single();
                stored.Should().Be(nameof(SyncOutcome.Header));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            tempDir.Delete(recursive: true);
        }
    }
}
