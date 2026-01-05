using DiffusionNexus.DataAccess;
using DiffusionNexus.DataAccess.Data;
using DiffusionNexus.Service.Services;
using System.Net.Http;

namespace DiffusionNexus.Service;

/// <summary>
/// Factory for creating properly configured service instances
/// </summary>
public static class ServiceFactory
{
    private static DiffusionNexusDbContext? _sharedDbContext;
    private static bool _databaseInitialized = false;

    /// <summary>
    /// Initialize the database (call once on application startup)
    /// </summary>
    public static async Task InitializeDatabaseAsync(string? databasePath = null)
    {
        await DbContextFactory.EnsureDatabaseCreatedAsync(databasePath);
        _databaseInitialized = true;
    }

    /// <summary>
    /// Get or create shared database context (use for dependency injection scenarios)
    /// </summary>
    public static DiffusionNexusDbContext GetOrCreateDbContext(string? databasePath = null)
    {
        if (_sharedDbContext == null)
        {
            _sharedDbContext = DbContextFactory.CreateDbContext(databasePath);
        }
        return _sharedDbContext;
    }

    /// <summary>
    /// Create a new database context (use when you need a fresh instance)
    /// </summary>
    public static DiffusionNexusDbContext CreateDbContext(string? databasePath = null)
    {
        return DbContextFactory.CreateDbContext(databasePath);
    }

    /// <summary>
    /// Create metadata provider chain with optional database support
    /// </summary>
    public static IModelMetadataProvider[] CreateMetadataProviders(
        string apiKey = "",
        bool useDatabase = true,
        DiffusionNexusDbContext? customContext = null)
    {
        var providers = new List<IModelMetadataProvider>();

        if (useDatabase && _databaseInitialized)
        {
            var context = customContext ?? GetOrCreateDbContext();
            providers.Add(new DatabaseMetadataProvider(context));
        }

        providers.Add(new LocalFileMetadataProvider());
        providers.Add(new CivitaiApiMetadataProvider(new CivitaiApiClient(new HttpClient()), apiKey));

        return providers.ToArray();
    }

    /// <summary>
    /// Create FileControllerService with optional database support
    /// </summary>
    public static FileControllerService CreateFileController(
        string apiKey = "",
        bool useDatabase = true,
        DiffusionNexusDbContext? customContext = null)
    {
        if (useDatabase && _databaseInitialized)
        {
            var context = customContext ?? GetOrCreateDbContext();
            var providers = CreateMetadataProviders(apiKey, useDatabase, context);
            return new FileControllerService(context, null, providers);
        }
        else
        {
            var providers = CreateMetadataProviders(apiKey, useDatabase: false);
            return new FileControllerService(providers);
        }
    }

    /// <summary>
    /// Create composite metadata provider (database + fallbacks)
    /// </summary>
    public static CompositeMetadataProvider CreateCompositeProvider(
        string apiKey = "",
        bool useDatabase = true,
        DiffusionNexusDbContext? customContext = null)
    {
        var context = useDatabase && _databaseInitialized ? (customContext ?? GetOrCreateDbContext()) : null;
        
        var fallbackProviders = new IModelMetadataProvider[]
        {
            new LocalFileMetadataProvider(),
            new CivitaiApiMetadataProvider(new CivitaiApiClient(new HttpClient()), apiKey)
        };

        return new CompositeMetadataProvider(context, fallbackProviders);
    }

    /// <summary>
    /// Create ModelSyncService for syncing files with database
    /// </summary>
    public static ModelSyncService CreateSyncService(
        string apiKey = "",
        DiffusionNexusDbContext? customContext = null)
    {
        if (!_databaseInitialized)
            throw new InvalidOperationException("Database not initialized. Call InitializeDatabaseAsync first.");

        var context = customContext ?? GetOrCreateDbContext();
        var apiClient = new CivitaiApiClient(new HttpClient());
        return new ModelSyncService(context, apiClient, apiKey);
    }

    /// <summary>
    /// Create LocalFileImportService for importing directories
    /// </summary>
    public static LocalFileImportService CreateImportService(
        string apiKey = "",
        DiffusionNexusDbContext? customContext = null)
    {
        if (!_databaseInitialized)
            throw new InvalidOperationException("Database not initialized. Call InitializeDatabaseAsync first.");

        var context = customContext ?? GetOrCreateDbContext();
        var apiClient = new CivitaiApiClient(new HttpClient());
        return new LocalFileImportService(context, apiClient, apiKey);
    }

    /// <summary>
    /// Cleanup shared resources (call on application shutdown)
    /// </summary>
    public static void Cleanup()
    {
        _sharedDbContext?.Dispose();
        _sharedDbContext = null;
        _databaseInitialized = false;
    }
}
