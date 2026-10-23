using System.Globalization;
using Microsoft.Data.Sqlite;

namespace DiffusionNexus.DataAccess.Recovery;

/// <summary>
/// Takes a consistent copy of the core database immediately before pending EF migrations are applied.
/// Lives in DataAccess (not the Service-layer <c>DatabaseBackupService</c>) because
/// <see cref="DatabaseRecoveryService"/> must not depend upward on settings. <c>VACUUM INTO</c> is
/// safe against a live WAL database and produces a compact, standalone file.
/// </summary>
internal static class PreMigrationBackup
{
    private const string Marker = ".pre-";

    public static string BuildBackupPath(string databaseFilePath, string migrationName, DateTimeOffset now)
    {
        var dir = Path.GetDirectoryName(databaseFilePath) ?? ".";
        var stem = Path.GetFileNameWithoutExtension(databaseFilePath);
        // "20260821120000_AddModelSyncState" -> "AddModelSyncState"
        var underscore = migrationName.IndexOf('_');
        var shortName = underscore >= 0 && underscore < migrationName.Length - 1 ? migrationName[(underscore + 1)..] : migrationName;
        var stamp = now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        return Path.Combine(dir, $"{stem}{Marker}{shortName}-{stamp}.db");
    }

    /// <returns>The backup path, or null when nothing was backed up (no database yet, or the copy failed). Never throws.</returns>
    public static string? TryCreate(string databaseFilePath, string firstPendingMigration, IDatabaseRecoveryLogger log, int keep = 3)
    {
        try
        {
            if (!File.Exists(databaseFilePath))
            {
                log.Information("PreMigrationBackup: no existing database — nothing to back up");
                return null;
            }

            var backupPath = BuildBackupPath(databaseFilePath, firstPendingMigration, DateTimeOffset.Now);
            using (var connection = new SqliteConnection($"Data Source={databaseFilePath};Mode=ReadOnly;Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                // VACUUM INTO takes a string literal, not a bound parameter; escape embedded quotes.
                command.CommandText = $"VACUUM INTO '{backupPath.Replace("'", "''")}'";
                command.ExecuteNonQuery();
            }
            log.Information($"PreMigrationBackup: wrote {backupPath}");

            Prune(databaseFilePath, keep, log);
            return backupPath;
        }
        catch (Exception ex)
        {
            log.Error(ex, "PreMigrationBackup: backup failed — continuing with migration");
            return null;
        }
    }

    private static void Prune(string databaseFilePath, int keep, IDatabaseRecoveryLogger log)
    {
        if (keep <= 0) return;
        var dir = Path.GetDirectoryName(databaseFilePath) ?? ".";
        var stem = Path.GetFileNameWithoutExtension(databaseFilePath);
        var stale = Directory.GetFiles(dir, $"{stem}{Marker}*.db")
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Skip(keep);
        foreach (var file in stale)
        {
            try { file.Delete(); log.Information($"PreMigrationBackup: pruned {file.Name}"); }
            catch (Exception ex) { log.Warning($"PreMigrationBackup: could not prune {file.Name}: {ex.Message}"); }
        }
    }
}
