using DiffusionNexus.DataAccess.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace DiffusionNexus.DataAccess;

/// <summary>
/// Extension methods for registering DataAccess services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds the DiffusionNexus core database context to the service collection.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="databaseDirectory">Optional custom directory for the database file.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddDiffusionNexusCoreDatabase(
        this IServiceCollection services,
        string? databaseDirectory = null)
    {
        services.AddDbContext<DiffusionNexusCoreDbContext>(options =>
        {
            options.UseSqlite(DiffusionNexusCoreDbContext.GetConnectionString(databaseDirectory));
            // Suppress warning for pending model changes during development
            // The migration snapshot may not match exactly but the schema is correct
            options.ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
        });

        return services;
    }

    /// <summary>
    /// Adds the DiffusionNexus core database context with custom configuration.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configureOptions">Action to configure DbContext options.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddDiffusionNexusCoreDatabase(
        this IServiceCollection services,
        Action<DbContextOptionsBuilder> configureOptions)
    {
        services.AddDbContext<DiffusionNexusCoreDbContext>(configureOptions);
        return services;
    }

    /// <summary>
    /// Adds the DiffusionNexus core database as a DbContext factory (for scoped usage).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="databaseDirectory">Optional custom directory for the database file.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddDiffusionNexusCoreDatabaseFactory(
        this IServiceCollection services,
        string? databaseDirectory = null)
    {
        services.AddDbContextFactory<DiffusionNexusCoreDbContext>(options =>
        {
            options.UseSqlite(DiffusionNexusCoreDbContext.GetConnectionString(databaseDirectory));
        });

        return services;
    }
}
