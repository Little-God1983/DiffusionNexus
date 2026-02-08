using DiffusionNexus.DataAccess.Data;
using DiffusionNexus.DataAccess.Repositories;
using DiffusionNexus.DataAccess.Repositories.Interfaces;
using DiffusionNexus.DataAccess.UnitOfWork;
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
    /// Adds the full data access layer: DbContext, repositories, and Unit of Work.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="databaseDirectory">Optional custom directory for the database file.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddDataAccessLayer(
        this IServiceCollection services,
        string? databaseDirectory = null)
    {
        services.AddDiffusionNexusCoreDatabase(databaseDirectory);

        // Unit of Work (scoped — same lifetime as DbContext)
        services.AddScoped<IUnitOfWork, DataAccess.UnitOfWork.UnitOfWork>();

        // Repositories (scoped — resolved through UoW or directly)
        services.AddScoped<IModelRepository, ModelRepository>();
        services.AddScoped<IModelFileRepository, ModelFileRepository>();
        services.AddScoped<IAppSettingsRepository, AppSettingsRepository>();
        services.AddScoped<IDisclaimerAcceptanceRepository, DisclaimerAcceptanceRepository>();

        return services;
    }

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
