using Microsoft.Data.Sqlite;

namespace DiffusionNexus.Tests.DataAccess;

public class WalJournalModeTests
{
    [Fact]
    public void ConnectionString_DoesNotUseSharedCache()
    {
        // GetConnectionString(string? directory) treats its argument as a directory
        // (it Directory.CreateDirectory()s it and combines it with the fixed db
        // filename) — pass a directory here, same as JournalModePragma_PersistsWal,
        // rather than a bare temp-file path.
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            var cs = DiffusionNexus.DataAccess.Data.DiffusionNexusCoreDbContext.GetConnectionString(dir);
            Assert.DoesNotContain("Cache=Shared", cs, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void JournalModePragma_PersistsWal()
    {
        // WAL requires a file-backed DB (in-memory SQLite ignores it), so use a temp directory.
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            var cs = DiffusionNexus.DataAccess.Data.DiffusionNexusCoreDbContext.GetConnectionString(dir);

            using (var connection = new SqliteConnection(cs))
            {
                connection.Open();
                using var enable = connection.CreateCommand();
                enable.CommandText = "PRAGMA journal_mode=WAL;";
                enable.ExecuteScalar();
            }

            // New connection: WAL must persist in the database file.
            using (var connection = new SqliteConnection(cs))
            {
                connection.Open();
                using var query = connection.CreateCommand();
                query.CommandText = "PRAGMA journal_mode;";
                Assert.Equal("wal", (string?)query.ExecuteScalar());
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
