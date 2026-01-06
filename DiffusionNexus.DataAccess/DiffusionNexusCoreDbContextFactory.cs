using DiffusionNexus.DataAccess.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DiffusionNexus.DataAccess;

/// <summary>
/// Design-time factory for DiffusionNexusCoreDbContext.
/// Used by EF Core tools for migrations.
/// </summary>
/// <remarks>
/// To create migrations, run from the solution root:
/// <code>
/// dotnet ef migrations add MigrationName --project DiffusionNexus.DataAccess --startup-project DiffusionNexus.UI-V2 --context DiffusionNexusCoreDbContext --output-dir Migrations/Core
/// </code>
/// 
/// To apply migrations:
/// <code>
/// dotnet ef database update --project DiffusionNexus.DataAccess --startup-project DiffusionNexus.UI-V2 --context DiffusionNexusCoreDbContext
/// </code>
/// </remarks>
public class DiffusionNexusCoreDbContextFactory : IDesignTimeDbContextFactory<DiffusionNexusCoreDbContext>
{
    public DiffusionNexusCoreDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<DiffusionNexusCoreDbContext>();
        
        // Use the same path as runtime to ensure consistency
        // This ensures migrations run against the actual app database
        optionsBuilder.UseSqlite(DiffusionNexusCoreDbContext.GetConnectionString());

        return new DiffusionNexusCoreDbContext(optionsBuilder.Options);
    }
}
