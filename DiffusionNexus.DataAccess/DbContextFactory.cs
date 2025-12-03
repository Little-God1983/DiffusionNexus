using DiffusionNexus.DataAccess.Data;
using Microsoft.EntityFrameworkCore;

namespace DiffusionNexus.DataAccess;

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
