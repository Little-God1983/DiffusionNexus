using DiffusionNexus.Civitai.Models;

namespace DiffusionNexus.Civitai;

/// <summary>
/// Which spacing a caller gets. Chosen by which registration you resolve, not by an ambient
/// context — the lane is a property of the surface making the call, and surfaces are wired once.
/// </summary>
public enum CivitaiCallLane
{
    /// <summary>A user is waiting: the browser, a detail panel, a dialog, a download.</summary>
    Interactive,

    /// <summary>Nobody is waiting: library sync, the visible-tile update sweep.</summary>
    Background
}

/// <summary>
/// The one door to the Civitai API: paces every request, waits out a rate limit anybody drew,
/// and serves repeats from a short-lived cache.
/// </summary>
/// <remarks>
/// <para>
/// Registered <i>as</i> <see cref="ICivitaiClient"/>, so the seventeen call sites across the app
/// get all of this without knowing it exists. That is the point: pacing used to live at four
/// hand-picked call sites inside the sync pipeline, which meant every surface added since —
/// the browser, the update checker, the detail panel, the waitlist, the sorter, the download
/// path — hammered Civitai unpaced and discovered the 429 on its own.
/// </para>
/// <para>
/// Two instances share one pacer, one cooldown and one cache; they differ only in lane. A
/// background sync therefore cannot outrun a user, and a user never waits behind a sync's
/// longer interval.
/// </para>
/// </remarks>
public sealed class CivitaiApiGateway : ICivitaiClient, ICivitaiApiCache
{
    /// <summary>Spacing for a call a user is waiting on.</summary>
    public static readonly TimeSpan InteractiveInterval = TimeSpan.FromMilliseconds(750);

    /// <summary>Spacing for background work.</summary>
    public static readonly TimeSpan BackgroundInterval = TimeSpan.FromMilliseconds(1500);

    private static readonly TimeSpan ModelTtl = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan VersionTtl = TimeSpan.FromMinutes(15);

    /// <summary>A hash maps to a version forever; only the record it points at can change.</summary>
    private static readonly TimeSpan HashTtl = TimeSpan.FromMinutes(60);

    /// <summary>Long enough to absorb a filter toggled off and on, short enough to feel live.</summary>
    private static readonly TimeSpan SearchTtl = TimeSpan.FromMinutes(2);

    private readonly ICivitaiClient _inner;
    private readonly ICivitaiRequestPacer _pacer;
    private readonly CivitaiRateLimitCooldown _cooldown;
    private readonly CivitaiResponseCache _cache;
    private readonly CivitaiCallLane _lane;

    private readonly object _apiKeyLock = new();
    private string? _lastApiKey;
    private bool _apiKeySeen;

    public CivitaiApiGateway(
        ICivitaiClient inner,
        ICivitaiRequestPacer pacer,
        CivitaiRateLimitCooldown cooldown,
        CivitaiResponseCache cache,
        CivitaiCallLane lane = CivitaiCallLane.Interactive)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _pacer = pacer ?? throw new ArgumentNullException(nameof(pacer));
        _cooldown = cooldown ?? throw new ArgumentNullException(nameof(cooldown));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _lane = lane;
    }

    private TimeSpan Interval =>
        (_lane == CivitaiCallLane.Background ? BackgroundInterval : InteractiveInterval)
        * _cooldown.IntervalMultiplier;

    /// <summary>
    /// Cache keys deliberately omit the API key — an authenticated and an anonymous request for
    /// the same public model return the same page, and keying by secret would halve the hit rate
    /// for nothing. What must not happen is an anonymous answer being served to a caller that has
    /// since supplied a key (gated models answer differently), so a change of key empties the store.
    /// </summary>
    private void NoteApiKey(string? apiKey)
    {
        lock (_apiKeyLock)
        {
            if (_apiKeySeen && string.Equals(_lastApiKey, apiKey, StringComparison.Ordinal)) return;
            if (_apiKeySeen) _cache.Clear();
            _lastApiKey = apiKey;
            _apiKeySeen = true;
        }
    }

    /// <summary>Cooldown first, then spacing: no point pacing into a wall.</summary>
    private async Task<T?> SendAsync<T>(string cacheKey, TimeSpan ttl, string? apiKey,
        Func<CancellationToken, Task<T?>> call, CancellationToken ct)
        where T : class
    {
        NoteApiKey(apiKey);

        return await _cache.GetOrAddAsync(cacheKey, ttl, async () =>
        {
            await _cooldown.WaitAsync(ct).ConfigureAwait(false);
            await _pacer.WaitAsync(Interval, ct).ConfigureAwait(false);
            return await call(ct).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<CivitaiModel?> GetModelAsync(int modelId, string? apiKey = null, CancellationToken cancellationToken = default)
        => SendAsync(CivitaiResponseCache.ModelKey(modelId), ModelTtl, apiKey,
            ct => _inner.GetModelAsync(modelId, apiKey, ct), cancellationToken);

    /// <inheritdoc />
    public Task<CivitaiModelVersion?> GetModelVersionAsync(int modelVersionId, string? apiKey = null, CancellationToken cancellationToken = default)
        => SendAsync(CivitaiResponseCache.VersionKey(modelVersionId), VersionTtl, apiKey,
            ct => _inner.GetModelVersionAsync(modelVersionId, apiKey, ct), cancellationToken);

    /// <inheritdoc />
    public Task<CivitaiModelVersion?> GetModelVersionByHashAsync(string hash, string? apiKey = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);
        return SendAsync(CivitaiResponseCache.HashKey(hash), HashTtl, apiKey,
            ct => _inner.GetModelVersionByHashAsync(hash, apiKey, ct), cancellationToken);
    }

    /// <inheritdoc />
    public async Task<CivitaiPagedResponse<CivitaiModel>> GetModelsAsync(
        CivitaiModelsQuery? query = null, string? apiKey = null, CancellationToken cancellationToken = default)
    {
        var key = CivitaiResponseCache.SearchKey(query?.ToQueryString() ?? string.Empty);
        var page = await SendAsync(key, SearchTtl, apiKey,
            async ct =>
            {
                // _inner.GetModelsAsync returns a non-nullable Task<CivitaiPagedResponse<CivitaiModel>>,
                // while SendAsync's factory wants Task<T?>. An explicit nullable-typed local satisfies
                // the compiler without a null-forgiving cast and without changing runtime behaviour —
                // the value is never actually null here, only its static type widens.
                CivitaiPagedResponse<CivitaiModel>? result = await _inner.GetModelsAsync(query, apiKey, ct).ConfigureAwait(false);
                return result;
            }, cancellationToken).ConfigureAwait(false);
        return page ?? new CivitaiPagedResponse<CivitaiModel>();
    }

    // Not cached: unused in production, and an images/tags/creators listing is a browse of a
    // moving target rather than a record we would want to hold on to.

    /// <inheritdoc />
    public async Task<CivitaiPagedResponse<CivitaiModelImage>> GetImagesAsync(
        CivitaiImagesQuery? query = null, string? apiKey = null, CancellationToken cancellationToken = default)
    {
        NoteApiKey(apiKey);
        await _cooldown.WaitAsync(cancellationToken).ConfigureAwait(false);
        await _pacer.WaitAsync(Interval, cancellationToken).ConfigureAwait(false);
        return await _inner.GetImagesAsync(query, apiKey, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<CivitaiPagedResponse<CivitaiTag>> GetTagsAsync(
        int? limit = null, int? page = null, string? query = null, CancellationToken cancellationToken = default)
    {
        await _cooldown.WaitAsync(cancellationToken).ConfigureAwait(false);
        await _pacer.WaitAsync(Interval, cancellationToken).ConfigureAwait(false);
        return await _inner.GetTagsAsync(limit, page, query, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<CivitaiPagedResponse<CivitaiCreatorInfo>> GetCreatorsAsync(
        int? limit = null, int? page = null, string? query = null, CancellationToken cancellationToken = default)
    {
        await _cooldown.WaitAsync(cancellationToken).ConfigureAwait(false);
        await _pacer.WaitAsync(Interval, cancellationToken).ConfigureAwait(false);
        return await _inner.GetCreatorsAsync(limit, page, query, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void InvalidateModel(int modelId) => _cache.InvalidateModel(modelId);

    /// <inheritdoc />
    public void InvalidateVersion(int modelVersionId) => _cache.InvalidateVersion(modelVersionId);

    /// <inheritdoc />
    public void Clear() => _cache.Clear();
}
