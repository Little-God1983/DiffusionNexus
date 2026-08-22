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
    /// Requires <c>ICivitaiClient</c> and <c>IAppSettingsService</c> to be registered by the host.
    /// Everything except the service and the request pacer is transient and stateless; the service
    /// is a singleton because it owns the single-flight gate that stops two concurrent runs from
    /// hammering Civitai with the same 2 500 requests, and the pacer because it holds the timestamp
    /// of the last request.
    /// </remarks>
    public static IServiceCollection AddLibrarySync(this IServiceCollection services)
    {
        // Singleton, and the only one: the pacer IS the process's memory of when it last spoke to
        // Civitai, so a second instance would pace nothing. It is awaited immediately before every
        // request the pipeline makes — inside the appliers and the identify step — because one
        // SyncItem is not one request (R4).
        services.AddSingleton<ICivitaiRequestPacer>(_ => new CivitaiRequestPacer());

        services.AddTransient<CivitaiMetadataApplier>();
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
        services.AddTransient<ISyncStep, IdentifyModelStep>();
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
