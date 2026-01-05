using DiffusionNexus.DataAccess.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DiffusionNexus.DataAccess;

/// <summary>
/// Design-time factory for DiffusionNexusCoreDbContext.
/// Used by EF Core tools for migrations.
/// </summary>
/// <remarks>
/// To create migrations, run:
/// <code>
/// cd DiffusionNexus.DataAccess
/// dotnet ef migrations add InitialCreate --context DiffusionNexusCoreDbContext --output-dir Migrations/Core
/// </code>
/// 
/// To apply migrations:
/// <code>
/// dotnet ef database update --context DiffusionNexusCoreDbContext
/// </code>
/// </remarks>
public class DiffusionNexusCoreDbContextFactory : IDesignTimeDbContextFactory<DiffusionNexusCoreDbContext>
{
    public DiffusionNexusCoreDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<DiffusionNexusCoreDbContext>();
        
        // Use a local database file for design-time operations
        var dbPath = Path.Combine(Directory.GetCurrentDirectory(), DiffusionNexusCoreDbContext.DatabaseFileName);
        optionsBuilder.UseSqlite($"Data Source={dbPath}");

        return new DiffusionNexusCoreDbContext(optionsBuilder.Options);
    }
}
