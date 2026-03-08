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
        return services.AddDataAccessLayerCore();
    }

    /// <summary>
    /// Adds the full data access layer with custom DbContext configuration.
    /// Useful for testing with in-memory databases.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configureOptions">Action to configure DbContext options.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddDataAccessLayer(
        this IServiceCollection services,
        Action<DbContextOptionsBuilder> configureOptions)
    {
        services.AddDiffusionNexusCoreDatabase(configureOptions);
        return services.AddDataAccessLayerCore();
    }

    /// <summary>
    /// Registers Unit of Work (shared between AddDataAccessLayer overloads).
    /// Transient so every consumer gets its own UoW with its own DbContext
    /// created by <see cref="IDbContextFactory{TContext}"/>.
    /// Repositories are accessed exclusively through <see cref="IUnitOfWork"/>.
    /// </summary>
    private static IServiceCollection AddDataAccessLayerCore(this IServiceCollection services)
    {
        services.AddTransient<IUnitOfWork, DataAccess.UnitOfWork.UnitOfWork>();
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
        // Factory registration: UoW creates its own context per instance.
        // AddDbContextFactory also registers DbContext as scoped for migration code.
        services.AddDbContextFactory<DiffusionNexusCoreDbContext>(options =>
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
        services.AddDbContextFactory<DiffusionNexusCoreDbContext>(configureOptions);
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
