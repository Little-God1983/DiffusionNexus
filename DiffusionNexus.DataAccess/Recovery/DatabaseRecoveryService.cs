using System.Data;
using System.Data.Common;
using DiffusionNexus.DataAccess.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DiffusionNexus.DataAccess.Recovery;

/// <summary>
/// Hand-rolled database repair / recovery choreography for the DiffusionNexus core database,
/// extracted verbatim from <c>App.axaml.cs</c> (issue #436).
/// <para>
/// The service is deliberately dependency-light: it operates on a caller-supplied
/// <see cref="DiffusionNexusCoreDbContext"/> (or an explicit file path for
/// <see cref="TryDeleteLockedDatabase"/>) and logs through an injected
/// <see cref="IDatabaseRecoveryLogger"/>. It never resolves a database location itself —
/// the caller decides which database it operates on, so it can never target the wrong file
/// or the real user database during tests.
/// </para>
/// <para>
/// This extraction is behavior-preserving: identical call order, identical decisions, and the
/// same log message text as the original App.axaml.cs methods (Serilog structured properties are
/// rendered to plain strings — see <see cref="IDatabaseRecoveryLogger"/>).
/// </para>
/// </summary>
public sealed class DatabaseRecoveryService
{
    private readonly IDatabaseRecoveryLogger _log;

    /// <summary>
    /// Creates a recovery service.
    /// </summary>
    /// <param name="logger">Logging sink; when <c>null</c> a no-op logger is used.</param>
    public DatabaseRecoveryService(IDatabaseRecoveryLogger? logger = null)
        => _log = logger ?? NullDatabaseRecoveryLogger.Instance;

    /// <summary>
    /// Runs the full initialize-and-repair choreography against <paramref name="dbContext"/>.
    /// Mirrors the body of the former <c>App.InitializeDatabase</c> (minus DI scope creation and
    /// activity-log wiring, which remain in the caller): clean stale migration history, apply
    /// pending migrations when present, verify/repair the schema, enable WAL, and recover from
    /// the specific "already exists" / "duplicate column" / "no such column" migration failures.
    /// </summary>
    public void InitializeAndRepair(DiffusionNexusCoreDbContext dbContext)
    {
        try
        {
            // Read the connection string from the context itself rather than re-resolving a path.
            // This keeps the service path-agnostic and matches the file the context actually opened.
            var dbPath = dbContext.Database.GetConnectionString();
            _log.Information($"InitializeDatabase: Connection string: {dbPath}");

            // First verify we can connect
            _log.Information("InitializeDatabase: Testing connection...");
            if (!dbContext.Database.CanConnect())
            {
                _log.Warning("InitializeDatabase: Cannot connect to database - will try to create it");
            }

            // Remove migration history entries for migrations that no longer exist in the codebase
            _log.Information("InitializeDatabase: Cleaning stale migration history entries...");
            CleanStaleMigrationHistory(dbContext);

            _log.Information("InitializeDatabase: Getting pending migrations...");
            var pendingMigrations = dbContext.Database.GetPendingMigrations().ToList();

            if (pendingMigrations.Count > 0)
            {
                _log.Information($"InitializeDatabase: Applying {pendingMigrations.Count} pending migrations...");
                foreach (var migration in pendingMigrations)
                {
                    _log.Information($"InitializeDatabase:   - {migration}");
                }

                _log.Information("InitializeDatabase: Running Migrate()...");
                dbContext.Database.Migrate();
                _log.Information("InitializeDatabase: Migration completed successfully");
            }
            else
            {
                _log.Information("InitializeDatabase: No pending migrations - SKIPPING Migrate()");
            }

            // Post-migration verification to catch schema gaps
            _log.Information("InitializeDatabase: Post-migration schema verification...");
            CheckAndRepairSchema(dbContext);

            // WAL: readers no longer block behind writers (e.g. the end-of-backup
            // LastBackupAt write). Persistent — set once per launch is idempotent.
            _log.Information("InitializeDatabase: Ensuring WAL journal mode...");
            dbContext.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
        }
        catch (SqliteException ex) when (ex.Message.Contains("already exists"))
        {
            _log.Warning($"InitializeDatabase: Table/column already exists (continuing): {ex.Message}");
            CheckAndRepairSchema(dbContext);
            MarkPendingMigrationsAsApplied(dbContext);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException sqlEx && sqlEx.Message.Contains("already exists"))
        {
            _log.Warning($"InitializeDatabase: Table/column already exists (continuing): {ex.Message}");
            CheckAndRepairSchema(dbContext);
            MarkPendingMigrationsAsApplied(dbContext);
        }
        catch (SqliteException ex) when (ex.Message.Contains("duplicate column"))
        {
            // Migration ADD COLUMN ran against a schema where the column was already present
            // — typically because an earlier startup applied the schema but failed before
            // stamping __EFMigrationsHistory. Repair + stamp so the next startup is clean.
            _log.Warning($"InitializeDatabase: Column already present from a prior partial migration (continuing): {ex.Message}");
            CheckAndRepairSchema(dbContext);
            MarkPendingMigrationsAsApplied(dbContext);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException sqlEx && sqlEx.Message.Contains("duplicate column"))
        {
            _log.Warning($"InitializeDatabase: Column already present from a prior partial migration (continuing): {ex.Message}");
            CheckAndRepairSchema(dbContext);
            MarkPendingMigrationsAsApplied(dbContext);
        }
        catch (SqliteException ex) when (ex.Message.Contains("no such column"))
        {
            _log.Warning($"InitializeDatabase: Schema mismatch detected: {ex.Message}");
            CheckAndRepairSchema(dbContext);
            MarkPendingMigrationsAsApplied(dbContext);
        }
        catch (SqliteException ex) when (ex.Message.Contains("database is locked") || ex.Message.Contains("busy"))
        {
            _log.Error(ex, "InitializeDatabase: Database is locked/busy - this may indicate another process is using the database");
        }
        catch (Exception ex)
        {
            _log.Error(ex, "InitializeDatabase: Unexpected error during migration");
            try
            {
                CheckAndRepairSchema(dbContext);
                // After repairing the schema, stamp any still-pending migrations as applied
                // so the next startup doesn't re-attempt them and hit the same exception.
                // Defense-in-depth for schema errors not matched by the specific filters above.
                MarkPendingMigrationsAsApplied(dbContext);
            }
            catch (Exception repairEx)
            {
                _log.Error(repairEx, "InitializeDatabase: Schema repair also failed");
            }
        }

        _log.Information("InitializeDatabase: Completed");
    }

    /// <summary>
    /// Removes entries from <c>__EFMigrationsHistory</c> that no longer have a corresponding
    /// migration class in the assembly, preventing EF Core from failing when migrations are removed.
    /// </summary>
    public void CleanStaleMigrationHistory(DiffusionNexusCoreDbContext dbContext)
    {
        try
        {
            var connection = dbContext.Database.GetDbConnection();
            var wasOpen = connection.State == ConnectionState.Open;
            if (!wasOpen) connection.Open();

            try
            {
                // Check if __EFMigrationsHistory table exists
                using var checkCmd = connection.CreateCommand();
                checkCmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='__EFMigrationsHistory';";
                var tableExists = checkCmd.ExecuteScalar() is not null;
                if (!tableExists) return;

                // Get all migration IDs known to EF Core from the assembly
                var knownMigrations = dbContext.Database.GetMigrations().ToHashSet(StringComparer.OrdinalIgnoreCase);

                // Get all migration IDs from the history table
                var appliedMigrations = new List<string>();
                using var listCmd = connection.CreateCommand();
                listCmd.CommandText = "SELECT MigrationId FROM __EFMigrationsHistory;";
                using var reader = listCmd.ExecuteReader();
                while (reader.Read())
                {
                    appliedMigrations.Add(reader.GetString(0));
                }

                // Remove stale entries (applied but no longer in codebase)
                foreach (var migrationId in appliedMigrations)
                {
                    if (!knownMigrations.Contains(migrationId))
                    {
                        _log.Warning($"CleanStaleMigrationHistory: Removing stale entry '{migrationId}'");
                        dbContext.Database.ExecuteSqlRaw(
                            "DELETE FROM __EFMigrationsHistory WHERE MigrationId = {0}", migrationId);
                    }
                }
            }
            finally
            {
                if (!wasOpen) connection.Close();
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex, "CleanStaleMigrationHistory: Failed to clean stale entries");
        }
    }

    /// <summary>
    /// Verifies and repairs the schema by ensuring every column EF Core expects on the
    /// <c>AppSettings</c> and <c>Models</c> tables actually exists, adding any that are missing.
    /// This is safer than waiting for a crash.
    /// </summary>
    public void CheckAndRepairSchema(DiffusionNexusCoreDbContext dbContext)
    {
        try
        {
            _log.Information("CheckAndRepairSchema: Checking table schema...");

            var connection = dbContext.Database.GetDbConnection();
            var wasOpen = connection.State == ConnectionState.Open;
            if (!wasOpen) connection.Open();

            var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA table_info('AppSettings');";
                using var reader = command.ExecuteReader();

                while (reader.Read())
                {
                    var name = reader["name"].ToString();
                    if (!string.IsNullOrEmpty(name)) existingColumns.Add(name);
                }
            }
            finally
            {
                if (!wasOpen) connection.Close();
            }

            _log.Information($"CheckAndRepairSchema: Found AppSettings columns: {string.Join(", ", existingColumns)}");

            // List of columns to verify and their add scripts
            var requiredColumns = new Dictionary<string, string>
            {
                { "MaxBackups", "ALTER TABLE AppSettings ADD COLUMN MaxBackups INTEGER NOT NULL DEFAULT 10" },
                { "LastBackupAt", "ALTER TABLE AppSettings ADD COLUMN LastBackupAt TEXT" },
                { "ComfyUiServerUrl", "ALTER TABLE AppSettings ADD COLUMN ComfyUiServerUrl TEXT NOT NULL DEFAULT 'http://127.0.0.1:8188/'" },
                { "LoraUpdateCheckStalenessDays", "ALTER TABLE AppSettings ADD COLUMN LoraUpdateCheckStalenessDays INTEGER NOT NULL DEFAULT 3" },
                { "FavoriteLoraSourcePath", "ALTER TABLE AppSettings ADD COLUMN FavoriteLoraSourcePath TEXT" },
                { "EncryptedHuggingfaceApiKey", "ALTER TABLE AppSettings ADD COLUMN EncryptedHuggingfaceApiKey TEXT" }
            };

            foreach (var col in requiredColumns)
            {
                if (!existingColumns.Contains(col.Key))
                {
                    _log.Warning($"CheckAndRepairSchema: Missing '{col.Key}' column. Attempting to add...");
                    try
                    {
                        dbContext.Database.ExecuteSqlRaw(col.Value);
                        _log.Information($"CheckAndRepairSchema: Successfully added '{col.Key}'");
                    }
                    catch (Exception ex)
                    {
                        _log.Error(ex, $"CheckAndRepairSchema: Failed to add '{col.Key}'");
                        // Don't throw, try next
                    }
                }
            }

            // Self-heal Models columns added by later migrations. Needed when a previous
            // run marked the migration as applied (MarkPendingMigrationsAsApplied) but the
            // ALTER TABLE never actually executed — leaving queries to fail with
            // "no such column: …". Mirrors the AppSettings repair above.
            RepairModelsTableColumns(dbContext, connection);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "CheckAndRepairSchema: Fatal error during check");
        }
    }

    /// <summary>
    /// Ensures every column EF expects on the <c>Models</c> table actually exists,
    /// applying ALTER TABLE statements for any that were lost when a migration row
    /// was force-marked as applied without the schema actually being updated.
    /// Add new entries here whenever a migration adds nullable / defaulted columns
    /// to the Models table.
    /// </summary>
    private void RepairModelsTableColumns(DiffusionNexusCoreDbContext dbContext, DbConnection connection)
    {
        var wasOpen = connection.State == ConnectionState.Open;
        if (!wasOpen) connection.Open();

        var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA table_info('Models');";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var name = reader["name"].ToString();
                if (!string.IsNullOrEmpty(name)) existingColumns.Add(name);
            }
        }
        finally
        {
            if (!wasOpen) connection.Close();
        }

        if (existingColumns.Count == 0)
        {
            // Table doesn't exist yet — initial migration will create it.
            return;
        }

        // Migration 20260509105621_AddLoraUpdateCheckFields
        var requiredModelsColumns = new Dictionary<string, string>
        {
            { "LastCheckedForUpdatesUtc", "ALTER TABLE Models ADD COLUMN LastCheckedForUpdatesUtc TEXT" },
            { "TotalVersionCount",        "ALTER TABLE Models ADD COLUMN TotalVersionCount INTEGER NOT NULL DEFAULT 0" },
        };

        foreach (var col in requiredModelsColumns)
        {
            if (existingColumns.Contains(col.Key)) continue;

            _log.Warning($"CheckAndRepairSchema: Missing Models.'{col.Key}' column. Attempting to add...");
            try
            {
                dbContext.Database.ExecuteSqlRaw(col.Value);
                _log.Information($"CheckAndRepairSchema: Successfully added Models.'{col.Key}'");
            }
            catch (Exception ex)
            {
                _log.Error(ex, $"CheckAndRepairSchema: Failed to add Models.'{col.Key}'");
            }
        }
    }

    /// <summary>
    /// Marks any pending migrations as applied in <c>__EFMigrationsHistory</c> without running them.
    /// Used after schema repair when migrations failed due to "already exists" errors.
    /// </summary>
    public void MarkPendingMigrationsAsApplied(DiffusionNexusCoreDbContext dbContext)
    {
        try
        {
            var pending = dbContext.Database.GetPendingMigrations().ToList();
            if (pending.Count == 0) return;

            foreach (var migrationId in pending)
            {
                _log.Information($"MarkPendingMigrationsAsApplied: Marking '{migrationId}' as applied");
                dbContext.Database.ExecuteSqlRaw(
                    "INSERT OR IGNORE INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES ({0}, {1})",
                    migrationId,
                    typeof(DbContext).Assembly.GetName().Version?.ToString() ?? "9.0.0");
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex, "MarkPendingMigrationsAsApplied: Failed");
        }
    }

    /// <summary>
    /// Attempts to delete a (possibly locked) database file and its <c>-wal</c>/<c>-shm</c>
    /// sidecars so it can be recreated fresh.
    /// <para>
    /// The caller supplies the exact path — the service never resolves it — so this can never
    /// target the wrong file. This fixes the original App.axaml.cs implementation, which hardcoded
    /// the WRONG filename ("DiffusionNexusCore.sqlite" instead of
    /// <see cref="DiffusionNexusCoreDbContext.DatabaseFileName"/> = "Diffusion_Nexus-core.db") and
    /// had zero call sites. There is still no caller in the current recovery choreography — the
    /// locked/busy path only logs (matching the original behavior) — so this remains an explicit,
    /// opt-in recovery operation rather than something the startup flow triggers automatically.
    /// </para>
    /// </summary>
    /// <returns><c>true</c> if the main database file no longer exists after the attempt.</returns>
    public bool TryDeleteLockedDatabase(string databaseFilePath)
    {
        if (string.IsNullOrWhiteSpace(databaseFilePath))
            throw new ArgumentException("Database file path must be provided.", nameof(databaseFilePath));

        try
        {
            _log.Warning($"TryDeleteLockedDatabase: Attempting to delete locked database at {databaseFilePath}");

            // Release any pooled SQLite handles before deleting so a stale pool entry doesn't keep
            // the file locked. The app opens with Pooling=False, so this is defensive, but it lets
            // the delete succeed if an EF connection was the only thing holding the file.
            SqliteConnection.ClearAllPools();

            if (File.Exists(databaseFilePath))
            {
                // Try to delete the database file
                File.Delete(databaseFilePath);
                _log.Information("TryDeleteLockedDatabase: Database file deleted successfully");
            }

            // Also delete journal/wal files if they exist
            var walFile = databaseFilePath + "-wal";
            var shmFile = databaseFilePath + "-shm";

            if (File.Exists(walFile)) File.Delete(walFile);
            if (File.Exists(shmFile)) File.Delete(shmFile);

            return !File.Exists(databaseFilePath);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "TryDeleteLockedDatabase: Failed to delete database file");
            return false;
        }
    }

    /// <summary>
    /// Deletes and recreates the database from scratch. WARNING: destroys ALL user data including
    /// settings; intended only for extreme recovery. No caller in the current startup choreography.
    /// </summary>
    public void ResetDatabase(DiffusionNexusCoreDbContext dbContext)
    {
        // WARNING: This deletes ALL user data including settings!
        // Only called in extreme cases - should rarely be needed
        dbContext.Database.EnsureDeleted();
        dbContext.Database.Migrate();
    }
}
