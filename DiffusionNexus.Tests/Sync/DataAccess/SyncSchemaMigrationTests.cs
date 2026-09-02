using DiffusionNexus.DataAccess.Data;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace DiffusionNexus.Tests.Sync.DataAccess;

public sealed class SyncSchemaMigrationTests : IDisposable
{
    private const string PreviousMigration = "20260816161430_AddInstallerPackageIsAppManaged";
    private const string NewMigration = "AddModelSyncStateAndThumbnailAttempts";
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("dnx-syncschema-");
    private string DbPath => Path.Combine(_dir.FullName, "core.db");

    private DiffusionNexusCoreDbContext NewContext() => new(
        new DbContextOptionsBuilder<DiffusionNexusCoreDbContext>()
            .UseSqlite($"Data Source={DbPath};Pooling=False").Options);

    private static Dictionary<string, List<string>> Columns(DiffusionNexusCoreDbContext ctx, params string[] tables)
    {
        var result = new Dictionary<string, List<string>>();
        var conn = ctx.Database.GetDbConnection();
        conn.Open();
        foreach (var t in tables)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"PRAGMA table_info('{t}');";
            using var r = cmd.ExecuteReader();
            var cols = new List<string>();
            while (r.Read()) cols.Add($"{r["name"]}:{r["type"]}:{r["notnull"]}");
            result[t] = cols;
        }
        conn.Close();
        return result;
    }

    private void MigrateTo(string target)
    {
        using var ctx = NewContext();
        ctx.GetService<IMigrator>().Migrate(target);
    }

    [Fact]
    public void MigrationIsAdditiveAndUppercasesHashes()
    {
        MigrateTo(PreviousMigration);

        // seed a legacy row set with a lowercase hash
        using (var ctx = NewContext())
        {
            ctx.Database.ExecuteSqlRaw(
                "INSERT INTO Models (Id, Name, Type, Mode, Source, IsNsfw, IsPoi, AllowNoCredit, AllowCommercialUse, AllowDerivatives, AllowDifferentLicense, CreatedAt, IsUserEdited, TotalVersionCount) " +
                "VALUES (1, 'legacy', 'LORA', 'Available', 'LocalFile', 0, 0, 0, 'None', 0, 0, '2026-01-01T00:00:00+00:00', 0, 0);");
            ctx.Database.ExecuteSqlRaw(
                "INSERT INTO ModelVersions (Id, ModelId, Name, BaseModel, EarlyAccessDays, CreatedAt, IsUserEdited, DownloadCount, RatingCount, Rating, ThumbsUpCount, ThumbsDownCount) " +
                "VALUES (1, 1, 'v1', 'Unknown', 0, '2026-01-01T00:00:00+00:00', 0, 0, 0, 0, 0, 0);");
            ctx.Database.ExecuteSqlRaw(
                "INSERT INTO ModelFiles (Id, ModelVersionId, FileName, SizeKB, FileType, IsPrimary, Format, Precision, SizeType, PickleScanResult, VirusScanResult, HashSHA256, LocalPath, IsLocalFileValid) " +
                "VALUES (1, 1, 'a.safetensors', 1, 'Model', 1, 'SafeTensor', 'Unknown', 'Unknown', 'Pending', 'Pending', 'abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789', 'C:\\l\\a.safetensors', 1);");
            ctx.Database.ExecuteSqlRaw(
                "INSERT INTO ModelImages (Id, ModelVersionId, Url, IsNsfw, NsfwLevel, Width, Height, SortOrder, IsLocalCacheValid, LikeCount, HeartCount, CommentCount) " +
                "VALUES (1, 1, 'https://x/y.jpeg', 0, 'None', 0, 0, 0, 0, 0, 0, 0);");
        }

        Dictionary<string, List<string>> before;
        using (var ctx = NewContext()) before = Columns(ctx, "Models", "ModelVersions", "ModelFiles", "ModelImages");

        using (var ctx = NewContext()) ctx.Database.Migrate();

        using (var ctx = NewContext())
        {
            var after = Columns(ctx, "Models", "ModelVersions", "ModelFiles", "ModelImages");
            foreach (var (table, cols) in before)
                after[table].Should().StartWith(cols, $"existing columns of {table} must be untouched (additive-only)");
            after["ModelImages"].Should().Contain("ThumbnailAttemptedAt:TEXT:0").And.Contain("ThumbnailFailure:TEXT:0");

            var syncStateCols = Columns(ctx, "ModelSyncStates")["ModelSyncStates"];
            syncStateCols.Should().Contain("ModelId:INTEGER:1").And.Contain("MetadataOutcome:TEXT:1");

            var hash = ctx.Set<DiffusionNexus.Domain.Entities.ModelFile>().Single().HashSHA256;
            hash.Should().Be("ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789");

            ctx.Set<DiffusionNexus.Domain.Entities.ModelImage>().Single().Url.Should().Be("https://x/y.jpeg", "rows survive");
            ctx.Set<DiffusionNexus.Domain.Entities.ModelSyncState>().Should().BeEmpty("the migration never invents state rows");
            ctx.Database.GetAppliedMigrations().Should().Contain(m => m.EndsWith(NewMigration));
        }
    }

    [Fact]
    public void MigrationIsIdempotentOnRerun()
    {
        using (var ctx = NewContext()) ctx.Database.Migrate();
        using (var ctx = NewContext()) ctx.Database.Migrate();
        using var check = NewContext();
        check.Database.GetPendingMigrations().Should().BeEmpty();
    }

    /// <summary>
    /// The production DbContext registration ignores <c>PendingModelChangesWarning</c>, so a model
    /// that drifts from the snapshot would migrate silently and only surface as a runtime SQL error
    /// for users. This is the guard the warning would have been. It also pins the #553 decision:
    /// the dead <c>ModelVersions.BaseModel</c> column is mapped by name from an obsolete property
    /// and deliberately has no migration — if that mapping ever stops matching the snapshot, this
    /// is where it shows.
    /// </summary>
    [Fact]
    public void ModelHasNoPendingChangesAgainstSnapshot()
    {
        using var ctx = NewContext();
        ctx.Database.HasPendingModelChanges().Should().BeFalse(
            "the EF model must match DiffusionNexusCoreDbContextModelSnapshot exactly — add a migration or fix the mapping");
    }

    /// <summary>
    /// #553 kept <c>ModelVersions.BaseModel</c> (TEXT NOT NULL, no DB default) for downgrade
    /// safety while removing every code path that wrote it. EF must therefore still send a value on
    /// INSERT, and it has to be one the pre-#553 enum converter can parse — "Unknown" — or a
    /// rollback fails on the first materialized version. This proves both halves without any
    /// production code touching the property.
    /// </summary>
    [Fact]
    public void EfInsertStillFillsTheKeptBaseModelColumnWithUnknown()
    {
        using (var ctx = NewContext()) ctx.Database.Migrate();

        using (var ctx = NewContext())
        {
            var model = new DiffusionNexus.Domain.Entities.Model { Name = "m", Type = DiffusionNexus.Domain.Enums.ModelType.LORA };
            model.Versions.Add(new DiffusionNexus.Domain.Entities.ModelVersion { Name = "v1", BaseModelRaw = "Pony" });
            ctx.Add(model);
            ctx.SaveChanges();
        }

        using (var ctx = NewContext())
        {
            var conn = ctx.Database.GetDbConnection();
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT BaseModel, BaseModelRaw FROM ModelVersions;";
            using var r = cmd.ExecuteReader();
            r.Read().Should().BeTrue();
            r["BaseModel"].Should().Be("Unknown", "the kept column must hold a value every older build's enum converter can parse");
            r["BaseModelRaw"].Should().Be("Pony");
            conn.Close();
        }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { _dir.Delete(recursive: true); } catch { }
    }
}
