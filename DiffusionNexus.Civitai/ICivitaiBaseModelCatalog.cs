namespace DiffusionNexus.Civitai;

/// <summary>
/// Provides the list of "base model" labels Civitai shows in its filter dropdown
/// (e.g. <c>"Pony"</c>, <c>"SDXL 1.0"</c>, <c>"Flux.1 D"</c>, <c>"Illustrious"</c>).
/// </summary>
/// <remarks>
/// Civitai does not expose a REST endpoint for this list, so the catalog is
/// sourced (in priority order) from:
/// <list type="number">
/// <item>An on-disk cache (<c>%LocalAppData%/DiffusionNexus/Cache/civitai-base-models.json</c>) within TTL.</item>
/// <item>A live fetch of <c>src/server/common/constants.ts</c> from the open-source
/// <see href="https://github.com/civitai/civitai">civitai/civitai</see> repository.</item>
/// <item>A bundled fallback snapshot shipped with the app (always available offline).</item>
/// </list>
/// Implementations are safe to call concurrently and should never throw — they
/// fall back to the bundled snapshot on any failure.
/// </remarks>
public interface ICivitaiBaseModelCatalog
{
    /// <summary>
    /// Returns the current list of Civitai base model labels. Cached after the
    /// first successful call. Pass <paramref name="forceRefresh"/> to bypass
    /// in-memory and on-disk caches and re-fetch from GitHub.
    /// </summary>
    Task<IReadOnlyList<string>> GetBaseModelsAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default);
}
