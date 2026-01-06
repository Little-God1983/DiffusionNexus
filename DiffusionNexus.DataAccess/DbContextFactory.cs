using DiffusionNexus.DataAccess.Data;
using Microsoft.EntityFrameworkCore;

namespace DiffusionNexus.DataAccess;

/// <summary>
/// Factory for creating legacy DiffusionNexusDbContext instances.
/// Database: diffusion_nexus.db
/// </summary>
/// <remarks>
/// This factory is used by DiffusionNexus.UI (V1) and related services.
/// For new development, use <see cref="DiffusionNexusCoreDbContext"/> instead.
/// </remarks>
[Obsolete("Use DiffusionNexusCoreDbContext and AddDiffusionNexusCoreDatabase for new development.")]
public static class DbContextFactory
{
    public static DiffusionNexusDbContext CreateDbContext(string? databasePath = null)
    {
        databasePath ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DiffusionNexus",
            "diffusion_nexus.db");

        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var optionsBuilder = new DbContextOptionsBuilder<DiffusionNexusDbContext>();
        optionsBuilder.UseSqlite($"Data Source={databasePath}");

        return new DiffusionNexusDbContext(optionsBuilder.Options);
    }

    public static async Task EnsureDatabaseCreatedAsync(string? databasePath = null)
    {
        using var context = CreateDbContext(databasePath);
        await context.Database.EnsureCreatedAsync();
    }

    public static async Task MigrateDatabaseAsync(string? databasePath = null)
    {
        using var context = CreateDbContext(databasePath);
        await context.Database.MigrateAsync();
    }
}
