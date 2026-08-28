using DiffusionNexus.Civitai;
using DiffusionNexus.Domain.Services.Sync;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.Service.Services.Sync.Steps;
using DiffusionNexus.Service.Services.Sync.Thumbnails;
using Microsoft.Extensions.DependencyInjection;

namespace DiffusionNexus.Service.Services.Sync;

/// <summary>
/// Registers the library metadata sync pipeline (#521 WP2).
/// </summary>
public static class SyncServiceCollectionExtensions
{
    /// <summary>
    /// Adds the metadata appliers, the state backfill, the five sync steps (the thumbnail provider
    /// and its typed client included) and the orchestrating <see cref="ILibrarySyncService"/>.
    /// </summary>
    /// <remarks>
    /// Requires <c>ICivitaiClient</c> (including its keyed <c>"background"</c> registration) and
    /// <c>IAppSettingsService</c> to be registered by the host. Everything except the service is
    /// transient and stateless; the service is a singleton because it owns the single-flight gate
    /// that stops two concurrent runs from hammering Civitai with the same 2 500 requests. Pacing
    /// is no longer this pipeline's business — it takes the background lane of the Civitai gateway,
    /// which paces, cools down and caches for every surface in the app, not just this one.
    /// </remarks>
    public static IServiceCollection AddLibrarySync(this IServiceCollection services)
    {
        // Nobody is waiting on a sync, so it takes the background lane: 1.5 s spacing, yielding
        // to any interactive call. The pacing itself now lives in the gateway — this pipeline used
        // to be the only thing in the app that paced at all.
        services.AddTransient(sp => new CivitaiMetadataApplier(
            sp.GetRequiredKeyedService<ICivitaiClient>("background"),
            sp.GetService<IUnifiedLogger>()));
        services.AddTransient<SidecarMetadataApplier>();
        services.AddTransient<SyncStateInitializer>();

        // The thumbnail ladder's HTTP is the image CDN, not the Civitai API: a different host with
        // different rules, hence its own typed client rather than ICivitaiClient's. The response
        // cap is the one thing here that is not a default — a "video in disguise" (an image record
        // whose URL serves an MP4) must not be buffered as an unbounded clip; 64 MB covers every
        // legitimate poster or still while bounding the pathological case.
        services.AddHttpClient<IThumbnailProvider, ThumbnailProvider>(c =>
        {
            c.Timeout = TimeSpan.FromSeconds(30);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("DiffusionNexus/1.0");
            c.MaxResponseContentBufferSize = 64 * 1024 * 1024;
        });

        // Registration order IS execution order (IEnumerable<ISyncStep> preserves it): discovery
        // must find a file before it can be identified, only an identified model has the Civitai
        // ids the tags and images steps need, and only a fetched image record has a URL to make a
        // thumbnail from.
        services.AddTransient<ISyncStep, DiscoverFilesStep>();

        // The only step holding a client of its own — the by-hash lookup. Background lane, and
        // without the pacer parameter the gateway has made redundant.
        services.AddTransient<ISyncStep>(sp => new IdentifyModelStep(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredKeyedService<ICivitaiClient>("background"),
            sp.GetRequiredService<CivitaiMetadataApplier>(),
            sp.GetRequiredService<SidecarMetadataApplier>(),
            sp.GetService<IUnifiedLogger>()));

        services.AddTransient<ISyncStep, FetchTagsStep>();
        services.AddTransient<ISyncStep, FetchImagesStep>();
        services.AddTransient<ISyncStep, ThumbnailsStep>();

        services.AddSingleton<ILibrarySyncService>(sp => new LibrarySyncService(
            sp.GetRequiredService<IEnumerable<ISyncStep>>(),
            sp.GetRequiredService<SyncStateInitializer>(),
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetService<IUnifiedLogger>()));

        return services;
    }
}
