using DiffusionNexus.DataAccess.Recovery;
using FluentAssertions;
using Microsoft.Data.Sqlite;

namespace DiffusionNexus.Tests.DataAccess.Recovery;

public sealed class PreMigrationBackupTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("dnx-premig-");
    private string DbPath => Path.Combine(_dir.FullName, "Diffusion_Nexus-core.db");

    private void CreateDbWithRow()
    {
        using var c = new SqliteConnection($"Data Source={DbPath};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "CREATE TABLE T (X INTEGER); INSERT INTO T VALUES (42);";
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void BuildBackupPathUsesMigrationNameAndTimestampNextToTheDatabase()
    {
        var path = PreMigrationBackup.BuildBackupPath(@"C:\d\Diffusion_Nexus-core.db", "20260821120000_AddModelSyncState",
            new DateTimeOffset(2026, 8, 21, 13, 5, 9, TimeSpan.Zero));
        path.Should().Be(@"C:\d\Diffusion_Nexus-core.pre-AddModelSyncState-20260821-130509.db");
    }

    [Fact]
    public void TryCreateWritesAReadableCopy()
    {
        CreateDbWithRow();
        var backup = PreMigrationBackup.TryCreate(DbPath, "20260821120000_AddModelSyncState", NullDatabaseRecoveryLogger.Instance);
        backup.Should().NotBeNull();
        File.Exists(backup).Should().BeTrue();
        using var c = new SqliteConnection($"Data Source={backup};Mode=ReadOnly;Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT X FROM T";
        cmd.ExecuteScalar().Should().Be(42L);
    }

    [Fact]
    public void TryCreateKeepsOnlyTheNewestN()
    {
        CreateDbWithRow();
        for (var i = 0; i < 5; i++)
        {
            PreMigrationBackup.TryCreate(DbPath, $"2026082112000{i}_M{i}", NullDatabaseRecoveryLogger.Instance, keep: 3);
            Thread.Sleep(1100); // timestamp resolution is seconds
        }
        Directory.GetFiles(_dir.FullName, "Diffusion_Nexus-core.pre-*.db").Should().HaveCount(3);
    }

    [Fact]
    public void TryCreateReturnsNullWhenTheDatabaseDoesNotExistYet()
    {
        PreMigrationBackup.TryCreate(DbPath, "x", NullDatabaseRecoveryLogger.Instance).Should().BeNull();
        Directory.GetFiles(_dir.FullName).Should().BeEmpty();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { _dir.Delete(recursive: true); } catch { }
    }
}
