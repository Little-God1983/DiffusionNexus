using DiffusionNexus.DataAccess.Data;
using DiffusionNexus.DataAccess.Recovery;
using DiffusionNexus.Domain.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DiffusionNexus.Tests.DataAccess.Recovery;

/// <summary>
/// Exercises <see cref="DatabaseRecoveryService"/> (extracted from App.axaml.cs, issue #436)
/// against throwaway <b>file-based</b> SQLite databases in a unique temp directory.
///
/// File-based (not in-memory) is required: the repair code cares about file locking/deletion,
/// WAL sidecars, and __EFMigrationsHistory rows — behaviors an in-memory DB does not exhibit.
///
/// SAFETY: every database lives under a per-test temp directory that is deleted on Dispose.
/// The service never resolves a path itself, so nothing here can touch the real user database
/// under %LOCALAPPDATA%.
/// </summary>
public sealed class DatabaseRecoveryServiceTests : IDisposable
{
    private readonly string _dir;

    public DatabaseRecoveryServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "dnx-dbrecovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        // Release any SQLite handles before deleting the temp directory (defensive; the app's
        // connection string sets Pooling=False, but WAL sidecars can linger otherwise).
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup; a locked handle from a deliberately-locked-file test may remain.
        }
    }

    // ---- helpers -----------------------------------------------------------

    private DbContextOptions<DiffusionNexusCoreDbContext> Options()
        => DiffusionNexusCoreDbContext.CreateOptions(_dir);

    private DiffusionNexusCoreDbContext NewContext() => new(Options());

    private string DbPath => DiffusionNexusCoreDbContext.GetDatabaseFilePath(_dir);
    private string ConnString => DiffusionNexusCoreDbContext.GetConnectionString(_dir);

    private void MigrateFresh()
    {
        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    private HashSet<string> Columns(string table)
    {
        var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var conn = new SqliteConnection(ConnString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info('{table}');";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var name = reader["name"].ToString();
            if (!string.IsNullOrEmpty(name)) cols.Add(name);
        }
        return cols;
    }

    private List<string> AppliedMigrations()
    {
        var list = new List<string>();
        using var conn = new SqliteConnection(ConnString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) list.Add(reader.GetString(0));
        return list;
    }

    private void Exec(string sql)
    {
        using var conn = new SqliteConnection(ConnString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private string? JournalMode()
    {
        using var conn = new SqliteConnection(ConnString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode;";
        return (string?)cmd.ExecuteScalar();
    }

    private sealed class CapturingRecoveryLogger : IDatabaseRecoveryLogger
    {
        public List<string> Infos { get; } = new();
        public List<string> Warnings { get; } = new();
        public List<(Exception Exception, string Message)> Errors { get; } = new();

        public void Information(string message) => Infos.Add(message);
        public void Warning(string message) => Warnings.Add(message);
        public void Error(Exception exception, string message) => Errors.Add((exception, message));
    }

    // ---- Scenario 1: stale __EFMigrationsHistory rows ----------------------

    [Fact]
    public void CleanStaleMigrationHistory_RemovesUnknownEntries_AndKeepsKnownOnes()
    {
        MigrateFresh();
        var known = AppliedMigrations();
        Exec("INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) " +
             "VALUES ('99999999999999_GhostMigrationThatNoLongerExists', '10.0.0');");

        var logger = new CapturingRecoveryLogger();
        var svc = new DatabaseRecoveryService(logger);
        using (var ctx = NewContext()) svc.CleanStaleMigrationHistory(ctx);

        var after = AppliedMigrations();
        Assert.DoesNotContain("99999999999999_GhostMigrationThatNoLongerExists", after);
        Assert.Equal(known, after); // every real migration entry preserved, only the ghost removed
        Assert.Contains(logger.Warnings, w => w.Contains("99999999999999_GhostMigrationThatNoLongerExists"));
    }

    [Fact]
    public void CleanStaleMigrationHistory_NoStaleRows_LeavesHistoryUntouched()
    {
        MigrateFresh();
        var before = AppliedMigrations();

        var svc = new DatabaseRecoveryService();
        using (var ctx = NewContext()) svc.CleanStaleMigrationHistory(ctx);

        Assert.Equal(before, AppliedMigrations());
    }

    // ---- Scenario 2: Models / AppSettings missing columns ------------------

    [Fact]
    public void CheckAndRepairSchema_ReAddsMissingModelsColumns()
    {
        MigrateFresh();
        // Simulate a migration marked applied whose ALTER TABLE never actually ran.
        Exec("ALTER TABLE Models DROP COLUMN TotalVersionCount;");
        Exec("ALTER TABLE Models DROP COLUMN LastCheckedForUpdatesUtc;");
        var corrupted = Columns("Models");
        Assert.DoesNotContain("TotalVersionCount", corrupted);
        Assert.DoesNotContain("LastCheckedForUpdatesUtc", corrupted);

        var logger = new CapturingRecoveryLogger();
        var svc = new DatabaseRecoveryService(logger);
        using (var ctx = NewContext()) svc.CheckAndRepairSchema(ctx);

        var repaired = Columns("Models");
        Assert.Contains("TotalVersionCount", repaired);
        Assert.Contains("LastCheckedForUpdatesUtc", repaired);
        Assert.Contains(logger.Warnings, w => w.Contains("TotalVersionCount"));
    }

    [Fact]
    public void CheckAndRepairSchema_ReAddsMissingAppSettingsColumn()
    {
        MigrateFresh();
        Exec("ALTER TABLE AppSettings DROP COLUMN EncryptedHuggingfaceApiKey;");
        Assert.DoesNotContain("EncryptedHuggingfaceApiKey", Columns("AppSettings"));

        var svc = new DatabaseRecoveryService();
        using (var ctx = NewContext()) svc.CheckAndRepairSchema(ctx);

        Assert.Contains("EncryptedHuggingfaceApiKey", Columns("AppSettings"));
    }

    [Fact]
    public void CheckAndRepairSchema_HealthySchema_MakesNoChanges()
    {
        MigrateFresh();
        var beforeModels = Columns("Models");
        var beforeAppSettings = Columns("AppSettings");

        var svc = new DatabaseRecoveryService();
        using (var ctx = NewContext()) svc.CheckAndRepairSchema(ctx);

        Assert.True(beforeModels.SetEquals(Columns("Models")));
        Assert.True(beforeAppSettings.SetEquals(Columns("AppSettings")));
    }

    // ---- Scenario 3: pending migrations but schema already correct ---------

    [Fact]
    public void MarkPendingMigrationsAsApplied_StampsPending_WithoutSchemaOrDataLoss()
    {
        MigrateFresh();
        var beforeModels = Columns("Models");
        var beforeAppSettings = Columns("AppSettings");

        // Seed a settings row so we can prove the stamp does not touch user data.
        using (var ctx = NewContext())
        {
            ctx.AppSettings.Add(new AppSettings { Id = 1, ComfyUiServerUrl = "http://seed/" });
            ctx.SaveChanges();
        }

        // Corruption: the schema is fully present, but the two newest migration rows are missing
        // from __EFMigrationsHistory (e.g. an earlier startup applied the schema but crashed before
        // stamping). EF now reports them as pending even though re-running them would fail.
        var applied = AppliedMigrations();
        var missing = applied.OrderByDescending(m => m, StringComparer.Ordinal).Take(2).ToList();
        foreach (var m in missing) Exec($"DELETE FROM __EFMigrationsHistory WHERE MigrationId = '{m}';");

        using (var ctx = NewContext())
            Assert.NotEmpty(ctx.Database.GetPendingMigrations());

        var svc = new DatabaseRecoveryService();
        using (var ctx = NewContext()) svc.MarkPendingMigrationsAsApplied(ctx);

        using (var ctx = NewContext())
            Assert.Empty(ctx.Database.GetPendingMigrations());

        // Schema and data untouched by the stamp.
        Assert.True(beforeModels.SetEquals(Columns("Models")));
        Assert.True(beforeAppSettings.SetEquals(Columns("AppSettings")));
        using (var ctx = NewContext())
            Assert.Equal("http://seed/", ctx.AppSettings.Single().ComfyUiServerUrl);
    }

    // ---- Scenario 4: locked / undeletable database file --------------------

    [Fact]
    public void TryDeleteLockedDatabase_ReturnsFalse_WhenFileIsLocked()
    {
        File.WriteAllText(DbPath, "pretend database contents");

        // Open the file WITHOUT FileShare.Delete so a delete attempt is blocked by the OS,
        // simulating a database locked by another handle.
        using var handle = new FileStream(DbPath, FileMode.Open, FileAccess.Read, FileShare.Read);

        var logger = new CapturingRecoveryLogger();
        var svc = new DatabaseRecoveryService(logger);

        var deleted = svc.TryDeleteLockedDatabase(DbPath);

        Assert.False(deleted);
        Assert.True(File.Exists(DbPath));            // still present — not silently lost
        Assert.NotEmpty(logger.Errors);              // failure was reported, not swallowed
    }

    [Fact]
    public void TryDeleteLockedDatabase_DeletesFileAndWalShmSidecars_WhenUnlocked()
    {
        File.WriteAllText(DbPath, "db");
        File.WriteAllText(DbPath + "-wal", "wal");
        File.WriteAllText(DbPath + "-shm", "shm");

        var svc = new DatabaseRecoveryService();
        var deleted = svc.TryDeleteLockedDatabase(DbPath);

        Assert.True(deleted);
        Assert.False(File.Exists(DbPath));
        Assert.False(File.Exists(DbPath + "-wal"));
        Assert.False(File.Exists(DbPath + "-shm"));
    }

    // ---- Scenario 5: valid database, no repair needed (the no-op path) -----

    [Fact]
    public void InitializeAndRepair_HealthyDatabase_IsNonDestructive()
    {
        MigrateFresh();

        int modelId;
        using (var ctx = NewContext())
        {
            ctx.AppSettings.Add(new AppSettings { Id = 1, ComfyUiServerUrl = "http://healthy/", MaxBackups = 7 });
            var model = new Model { Name = "Healthy Model", TotalVersionCount = 3 };
            ctx.Models.Add(model);
            ctx.SaveChanges();
            modelId = model.Id;
        }

        var beforeModels = Columns("Models");
        var beforeAppSettings = Columns("AppSettings");
        var beforeMigrations = AppliedMigrations();

        var svc = new DatabaseRecoveryService();
        using (var ctx = NewContext()) svc.InitializeAndRepair(ctx);

        // Schema, migration history and user data must be byte-for-byte intact.
        Assert.True(beforeModels.SetEquals(Columns("Models")));
        Assert.True(beforeAppSettings.SetEquals(Columns("AppSettings")));
        Assert.Equal(beforeMigrations, AppliedMigrations());

        using (var ctx = NewContext())
            Assert.Empty(ctx.Database.GetPendingMigrations());

        using (var ctx = NewContext())
        {
            var settings = ctx.AppSettings.Single();
            Assert.Equal("http://healthy/", settings.ComfyUiServerUrl);
            Assert.Equal(7, settings.MaxBackups);

            var model = ctx.Models.Single(m => m.Id == modelId);
            Assert.Equal("Healthy Model", model.Name);
            Assert.Equal(3, model.TotalVersionCount);
        }

        // WAL is enabled by the choreography and persists across connections.
        Assert.Equal("wal", JournalMode());
    }

    // ---- Scenario 6: ResetDatabase (destructive, opt-in, no production caller) ----

    [Fact]
    public void ResetDatabase_ExistingCorruptDatabase_RecreatesCleanMigratedSchemaWithNoUserRows()
    {
        MigrateFresh();

        // Seed user data that must NOT survive a reset.
        using (var ctx = NewContext())
        {
            ctx.AppSettings.Add(new AppSettings { Id = 1, ComfyUiServerUrl = "http://seed/" });
            ctx.Models.Add(new Model { Name = "Doomed Model", TotalVersionCount = 1 });
            ctx.SaveChanges();
        }

        // Bogus schema mutation, mirroring the other corruption scenarios above.
        Exec("ALTER TABLE Models DROP COLUMN TotalVersionCount;");
        Assert.DoesNotContain("TotalVersionCount", Columns("Models"));

        var svc = new DatabaseRecoveryService();
        using (var ctx = NewContext()) svc.ResetDatabase(ctx);

        Assert.True(File.Exists(DbPath));

        // Current migrated schema restored, including the column the mutation dropped.
        Assert.Contains("TotalVersionCount", Columns("Models"));

        // No pending migrations against the recreated database.
        using (var ctx = NewContext())
            Assert.Empty(ctx.Database.GetPendingMigrations());

        // Zero user rows — reset destroys all prior data.
        using (var ctx = NewContext())
        {
            Assert.Empty(ctx.AppSettings);
            Assert.Empty(ctx.Models);
        }
    }

    [Fact]
    public void ResetDatabase_NoExistingDatabaseFile_CreatesFreshMigratedDatabase()
    {
        // No MigrateFresh(): the directory has no database at all.
        Assert.False(File.Exists(DbPath));

        var svc = new DatabaseRecoveryService();
        using (var ctx = NewContext()) svc.ResetDatabase(ctx);

        Assert.True(File.Exists(DbPath));

        using (var ctx = NewContext())
            Assert.Empty(ctx.Database.GetPendingMigrations());

        var models = Columns("Models");
        Assert.Contains("Name", models);
        Assert.Contains("TotalVersionCount", models);

        using (var ctx = NewContext())
        {
            Assert.Empty(ctx.AppSettings);
            Assert.Empty(ctx.Models);
        }
    }

    // ---- Extra coverage: first-run + combined corruption -------------------

    [Fact]
    public void InitializeAndRepair_FreshEmptyDirectory_CreatesAndMigratesDatabase()
    {
        // No MigrateFresh(): the directory has no database at all (first launch).
        var svc = new DatabaseRecoveryService();
        using (var ctx = NewContext()) svc.InitializeAndRepair(ctx);

        Assert.True(File.Exists(DbPath));
        using (var ctx = NewContext())
            Assert.Empty(ctx.Database.GetPendingMigrations());

        var models = Columns("Models");
        Assert.Contains("Name", models);
        Assert.Contains("TotalVersionCount", models);
    }

    [Fact]
    public void InitializeAndRepair_StaleHistoryAndMissingColumn_RecoversToHealthyState()
    {
        MigrateFresh();
        Exec("INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) " +
             "VALUES ('88888888888888_AnotherGhost', '10.0.0');");
        Exec("ALTER TABLE Models DROP COLUMN TotalVersionCount;");

        var svc = new DatabaseRecoveryService();
        using (var ctx = NewContext()) svc.InitializeAndRepair(ctx);

        Assert.DoesNotContain("88888888888888_AnotherGhost", AppliedMigrations());
        Assert.Contains("TotalVersionCount", Columns("Models"));
        using (var ctx = NewContext())
            Assert.Empty(ctx.Database.GetPendingMigrations());
        Assert.Equal("wal", JournalMode());
    }
}
