using DiffusionNexus.DataAccess.Data;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

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

    /// <summary>
    /// A database that already has a settings row belongs to someone whose Inpaint and Outpaint ran on
    /// their own ComfyUI. The AddComfyUiServerMode migration must keep them there (CustomUrl), while a
    /// row created after the migration - a fresh install, or publish.ps1's seed - gets Engine.
    /// </summary>
    [Fact]
    public void AddComfyUiServerMode_ExistingRowBecomesCustomUrl_RowInsertedAfterwardsIsEngine()
    {
        var tempDir = Directory.CreateTempSubdirectory();
        var dbPath = Path.Combine(tempDir.FullName, "servermode-test.db");
        const string insertRow =
            "INSERT INTO [AppSettings] (Id, ShowNsfw, GenerateVideoThumbnails, ShowVideoPreview, " +
            "UseForgeStylePrompts, MergeLoraSources, DeleteEmptySourceFolders, BackupDatasetImagesEnabled, " +
            "BackupDatabaseEnabled, AutoBackupIntervalDays, AutoBackupIntervalHours, MaxBackups, " +
            "ComfyUiServerUrl, UpdatedAt) VALUES (1, 0, 1, 0, 1, 0, 0, 0, 1, 1, 0, 10, " +
            "'http://127.0.0.1:8188/', datetime('now'))";
        try
        {
            var options = new DbContextOptionsBuilder<DiffusionNexusCoreDbContext>()
                .UseSqlite($"Data Source={dbPath};Pooling=False")
                .Options;

            using (var ctx = new DiffusionNexusCoreDbContext(options))
            {
                var migrator = ctx.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
                migrator.Migrate("20260829104419_AddCivitaiBrowserFilterJson");

                // Same column list publish.ps1 seeds with; every later column has a default.
                ctx.Database.ExecuteSqlRaw(insertRow);

                ctx.Database.Migrate();
            }

            using (var ctx = new DiffusionNexusCoreDbContext(options))
            {
                ctx.AppSettings.Single().ComfyUiServerMode.Should().Be(ComfyUiServerMode.CustomUrl,
                    "an upgrading user's editor must keep running on their own ComfyUI");

                ctx.Database.ExecuteSqlRaw("DELETE FROM [AppSettings]");
                ctx.Database.ExecuteSqlRaw(insertRow);
            }

            using (var ctx = new DiffusionNexusCoreDbContext(options))
            {
                ctx.AppSettings.Single().ComfyUiServerMode.Should().Be(ComfyUiServerMode.Engine,
                    "a row created after the migration (fresh install / publish seed) defaults to the Engine");
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            tempDir.Delete(recursive: true);
        }
    }

    /// <summary>
    /// #618: an upgrading database gets no catalog channel (the app copies the installer's once) and
    /// automatic updates off.
    /// </summary>
    [Fact]
    public void AddCatalogUpdateSettings_ExistingRowHasNoChannelAndAutomaticUpdatesOff()
    {
        var tempDir = Directory.CreateTempSubdirectory();
        var dbPath = Path.Combine(tempDir.FullName, "catalog-settings-test.db");
        const string insertRow =
            "INSERT INTO [AppSettings] (Id, ShowNsfw, GenerateVideoThumbnails, ShowVideoPreview, " +
            "UseForgeStylePrompts, MergeLoraSources, DeleteEmptySourceFolders, BackupDatasetImagesEnabled, " +
            "BackupDatabaseEnabled, AutoBackupIntervalDays, AutoBackupIntervalHours, MaxBackups, " +
            "ComfyUiServerUrl, UpdatedAt) VALUES (1, 0, 1, 0, 1, 0, 0, 0, 1, 1, 0, 10, " +
            "'http://127.0.0.1:8188/', datetime('now'))";
        try
        {
            var options = new DbContextOptionsBuilder<DiffusionNexusCoreDbContext>()
                .UseSqlite($"Data Source={dbPath};Pooling=False")
                .Options;

            using (var ctx = new DiffusionNexusCoreDbContext(options))
            {
                var migrator = ctx.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
                migrator.Migrate("20261008172424_AddComfyUiServerMode");
                ctx.Database.ExecuteSqlRaw(insertRow);
                ctx.Database.Migrate();
            }

            using (var ctx = new DiffusionNexusCoreDbContext(options))
            {
                var row = ctx.AppSettings.Single();
                row.CatalogChannel.Should().BeNull();
                row.ApplyCatalogUpdatesAutomatically.Should().BeFalse();
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            tempDir.Delete(recursive: true);
        }
    }

    /// <summary>
    /// #631: the rating gets a new integer column, NsfwRating (0 = unrated). The old IsNsfw and
    /// NsfwLevel columns stay untouched — every older build still maps them, so dropping or retyping
    /// them would break a rollback (see ModelVersion.BaseModel). Every model that holds a Civitai
    /// image has its images step un-stamped so the next Download Metadata run rates them; a model
    /// whose only image is a local file has nothing to fetch and keeps its stamp.
    /// </summary>
    [Fact]
    public void AddNsfwRating_IsAdditive_AndCivitaiImagesAreRecheckedOnce()
    {
        var tempDir = Directory.CreateTempSubdirectory();
        var dbPath = Path.Combine(tempDir.FullName, "nsfw-level-test.db");
        const string stamp = "2026-10-01T00:00:00+00:00";
        string[] seed =
        [
            "INSERT INTO Models (Id, Name, Type, Mode, Source, IsNsfw, IsPoi, AllowNoCredit, AllowCommercialUse, AllowDerivatives, AllowDifferentLicense, CreatedAt, IsUserEdited, TotalVersionCount) " +
            "VALUES (1, 'remote', 'LORA', 'Available', 'CivitaiApi', 0, 0, 0, 'None', 0, 0, '" + stamp + "', 0, 0), " +
            "(2, 'local', 'LORA', 'Available', 'LocalFile', 0, 0, 0, 'None', 0, 0, '" + stamp + "', 0, 0);",
            "INSERT INTO ModelVersions (Id, ModelId, Name, BaseModel, EarlyAccessDays, CreatedAt, IsUserEdited, DownloadCount, RatingCount, Rating, ThumbsUpCount, ThumbsDownCount) " +
            "VALUES (1, 1, 'v1', 'Unknown', 0, '" + stamp + "', 0, 0, 0, 0, 0, 0), (2, 2, 'v1', 'Unknown', 0, '" + stamp + "', 0, 0, 0, 0, 0, 0);",
            "INSERT INTO ModelImages (Id, ModelVersionId, Url, IsNsfw, NsfwLevel, Width, Height, SortOrder, IsLocalCacheValid, LikeCount, HeartCount, CommentCount) " +
            "VALUES (1, 1, 'https://image.civitai.com/x/a.jpeg', 1, 'None', 0, 0, 0, 0, 0, 0, 0), " +
            "(2, 2, 'file:///C:/l/b.preview.png', 0, 'None', 0, 0, 0, 0, 0, 0, 0);",
            "INSERT INTO ModelSyncStates (ModelId, MetadataOutcome, MetadataAttempts, ImagesCheckedAt, UpdatedAt) " +
            "VALUES (1, 'Matched', 1, '" + stamp + "', '" + stamp + "'), (2, 'Matched', 1, '" + stamp + "', '" + stamp + "');",
        ];
        try
        {
            var options = new DbContextOptionsBuilder<DiffusionNexusCoreDbContext>()
                .UseSqlite($"Data Source={dbPath};Pooling=False")
                .Options;

            using (var ctx = new DiffusionNexusCoreDbContext(options))
            {
                var migrator = ctx.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
                migrator.Migrate("20261011112158_AddCatalogUpdateSettings");
                foreach (var sql in seed) ctx.Database.ExecuteSqlRaw(sql);
                ctx.Database.Migrate();
            }

            using (var ctx = new DiffusionNexusCoreDbContext(options))
            {
                var images = ctx.ModelImages.AsNoTracking().OrderBy(i => i.Id).ToList();
                images.Select(i => i.NsfwLevel).Should().Equal(NsfwLevel.None, NsfwLevel.None);
                // Each image is alone in its version, so no context changes the verdict.
                NsfwPolicy.IsNsfwImage(images[0].NsfwLevel, images[0].Url)
                    .Should().BeTrue("an unrated Civitai image is not known to be safe");
                NsfwPolicy.IsNsfwImage(images[1].NsfwLevel, images[1].Url)
                    .Should().BeFalse("a local-only preview cannot be rated and stays shown");

                var states = ctx.ModelSyncStates.AsNoTracking().OrderBy(s => s.ModelId).ToList();
                states[0].ImagesCheckedAt.Should().BeNull("its Civitai images must be fetched again to be rated");
                states[1].ImagesCheckedAt.Should().NotBeNull("a local image has nothing to fetch");

                var columns = ctx.Database
                    .SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('ModelImages')")
                    .ToList();
                columns.Should().Contain(new[] { "IsNsfw", "NsfwLevel", "NsfwRating" });
                ctx.Database
                    .SqlQueryRaw<string>("SELECT NsfwLevel || '/' || IsNsfw || '/' || typeof(NsfwRating) AS Value FROM ModelImages ORDER BY Id")
                    .ToList()
                    .Should().Equal(new[] { "None/1/integer", "None/0/integer" }, "the old columns keep what older builds wrote");
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            tempDir.Delete(recursive: true);
        }
    }

    /// <summary>
    /// #647: the user's NSFW verdict per LoRA is a new nullable column; every existing LoRA starts
    /// automatic (null), and nothing else in Models changes.
    /// </summary>
    [Fact]
    public void AddModelNsfwOverride_ExistingLorasStartAutomatic()
    {
        var tempDir = Directory.CreateTempSubdirectory();
        var dbPath = Path.Combine(tempDir.FullName, "nsfw-override-test.db");
        try
        {
            var options = new DbContextOptionsBuilder<DiffusionNexusCoreDbContext>()
                .UseSqlite($"Data Source={dbPath};Pooling=False")
                .Options;

            using (var ctx = new DiffusionNexusCoreDbContext(options))
            {
                ctx.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>()
                    .Migrate("20261023081307_AddNsfwRating");
                ctx.Database.ExecuteSqlRaw(
                    "INSERT INTO Models (Id, Name, Type, Mode, Source, IsNsfw, IsPoi, AllowNoCredit, AllowCommercialUse, AllowDerivatives, AllowDifferentLicense, CreatedAt, IsUserEdited, TotalVersionCount) " +
                    "VALUES (1, 'existing', 'LORA', 'Available', 'CivitaiApi', 1, 0, 0, 'None', 0, 0, '2026-10-01T00:00:00+00:00', 0, 0);");
                ctx.Database.Migrate();
            }

            using (var ctx = new DiffusionNexusCoreDbContext(options))
            {
                var model = ctx.Models.AsNoTracking().Single();
                model.NsfwOverride.Should().BeNull("an existing LoRA keeps the automatic verdict");
                model.IsNsfw.Should().BeTrue("the creator flag is untouched");
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            tempDir.Delete(recursive: true);
        }
    }
}
