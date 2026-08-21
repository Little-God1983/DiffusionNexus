using DiffusionNexus.Domain.Services.Sync;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.Service.Services.Sync.Steps;
using Microsoft.Extensions.DependencyInjection;

namespace DiffusionNexus.Service.Services.Sync;

/// <summary>
/// Registers the library metadata sync pipeline (#521 WP2).
/// </summary>
public static class SyncServiceCollectionExtensions
{
    /// <summary>
    /// Adds the metadata appliers, the state backfill, the four sync steps and the orchestrating
    /// <see cref="ILibrarySyncService"/>.
    /// </summary>
    /// <remarks>
    /// Requires <c>ICivitaiClient</c> and <c>IAppSettingsService</c> to be registered by the host.
    /// Everything except the service itself is transient and stateless; the service is a singleton
    /// because it owns the single-flight gate that stops two concurrent runs from hammering Civitai
    /// with the same 2 500 requests.
    /// </remarks>
    public static IServiceCollection AddLibrarySync(this IServiceCollection services)
    {
        services.AddTransient<CivitaiMetadataApplier>();
        services.AddTransient<SidecarMetadataApplier>();
        services.AddTransient<SyncStateInitializer>();

        // Registration order IS execution order (IEnumerable<ISyncStep> preserves it): discovery
        // must find a file before it can be identified, and only an identified model has the
        // Civitai ids the tags and images steps need.
        services.AddTransient<ISyncStep, DiscoverFilesStep>();
        services.AddTransient<ISyncStep, IdentifyModelStep>();
        services.AddTransient<ISyncStep, FetchTagsStep>();
        services.AddTransient<ISyncStep, FetchImagesStep>();

        services.AddSingleton<ILibrarySyncService>(sp => new LibrarySyncService(
            sp.GetRequiredService<IEnumerable<ISyncStep>>(),
            sp.GetRequiredService<SyncStateInitializer>(),
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetService<IUnifiedLogger>()));

        return services;
    }
}
