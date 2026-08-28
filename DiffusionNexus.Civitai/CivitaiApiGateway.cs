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
    /// Cooldown first, then spacing: no point pacing into a wall. The single shared preamble for
    /// every call this gateway makes — <see cref="SendAsync{T}"/>'s factory and the three
    /// uncached methods below all funnel through here.
    /// </summary>
    /// <remarks>
    /// Same two waits, two different answers to "can a caller's cancellation tear this down?",
    /// and the rule is exactly the one <see cref="SendAsync{T}"/>'s remark below explains at
    /// length: shared, single-flighted work (<see cref="SendAsync{T}"/>'s factory) detaches by
    /// passing <see cref="CancellationToken.None"/>; exclusive, non-single-flighted work
    /// (<see cref="GetImagesAsync"/>, <see cref="GetTagsAsync"/>, <see cref="GetCreatorsAsync"/> —
    /// none of the three are cached, so nobody else is ever waiting on the same call) honours the
    /// caller's own token by passing it straight through.
    /// </remarks>
    private async Task GateAsync(CancellationToken ct)
    {
        await _cooldown.WaitAsync(ct).ConfigureAwait(false);
        await _pacer.WaitAsync(Interval, ct).ConfigureAwait(false);
    }

    /// <summary>Cooldown first, then spacing: no point pacing into a wall.</summary>
    /// <remarks>
    /// This calls <see cref="GateAsync"/> — the same preamble <see cref="GetImagesAsync"/>,
    /// <see cref="GetTagsAsync"/> and <see cref="GetCreatorsAsync"/> use — but with
    /// <see cref="CancellationToken.None"/>, not <paramref name="ct"/>.
    /// <see cref="CivitaiResponseCache.GetOrAddAsync{T}"/> single-flights concurrent callers for
    /// the same key onto one shared fetch — one of them becomes its "leader" (whichever call first
    /// registers the in-flight entry), and the rest join it. If the factory captured a caller's
    /// token, that caller's cancellation would tear down the cooldown wait, the pacer wait, or the
    /// underlying HTTP call — for EVERY caller sharing the fetch, including joiners who supplied
    /// their own, still-live token and never asked to be cancelled (see blocker 1 in the gateway
    /// fix-wave review: a page the update checker abandons on every pagination/filter change could
    /// otherwise cancel the detail panel's join to the same model). Detaching the shared work from
    /// any one caller's token means every caller — leader and joiners alike — can only ever abandon
    /// its OWN wait, via <c>ct</c> below, which <see cref="CivitaiResponseCache.GetOrAddAsync{T}"/>
    /// already applies with <c>Task.WaitAsync</c>.
    ///
    /// Consequences, considered deliberately:
    /// <list type="bullet">
    /// <item>An abandoned fetch now runs to completion instead of being torn down. That is cheap —
    /// one metadata GET — and still populates the cache, so the work is not wasted; it just outlives
    /// the caller that triggered it, the same way a fire-and-forget prefetch would.</item>
    /// <item>Nothing hangs app shutdown or a cancelled sync run over this: process exit does not
    /// wait for a detached background continuation, and the caller-side cancellation (this method's
    /// own <c>ct</c>) still resolves promptly regardless of what the shared fetch is doing.</item>
    /// <item>The cooldown and pacer waits no longer stop early for a caller's cancellation — a
    /// caller that starts a fetch during an active 429 cooldown could sit out the FULL cooldown in
    /// the background even after every caller waiting on it has bailed. Accepted: the cooldown is
    /// bounded (server-supplied Retry-After, or the 30 s default) and the same "let it finish, it's
    /// cheap" reasoning applies — the alternative (racing the shared work against an arbitrary
    /// caller's token) is exactly the bug this fixes.</item>
    /// </list>
    /// </remarks>
    private async Task<T?> SendAsync<T>(string cacheKey, TimeSpan ttl, string? apiKey,
        Func<CancellationToken, Task<T?>> call, CancellationToken ct)
        where T : class
    {
        // Delegated to the cache rather than tracked here: _cache is the ONE object shared by
        // both lane instances (interactive and background wrap the same cache), so the last-seen
        // key has to live where it is actually shared, not on a per-lane field that would leave
        // each lane with its own, inconsistent memory of a single store.
        _cache.NoteApiKey(apiKey);

        return await _cache.GetOrAddAsync(cacheKey, ttl, async () =>
        {
            await GateAsync(CancellationToken.None).ConfigureAwait(false);
            return await call(CancellationToken.None).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
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
        _cache.NoteApiKey(apiKey);
        await GateAsync(cancellationToken).ConfigureAwait(false);
        return await _inner.GetImagesAsync(query, apiKey, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<CivitaiPagedResponse<CivitaiTag>> GetTagsAsync(
        int? limit = null, int? page = null, string? query = null, CancellationToken cancellationToken = default)
    {
        await GateAsync(cancellationToken).ConfigureAwait(false);
        return await _inner.GetTagsAsync(limit, page, query, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<CivitaiPagedResponse<CivitaiCreatorInfo>> GetCreatorsAsync(
        int? limit = null, int? page = null, string? query = null, CancellationToken cancellationToken = default)
    {
        await GateAsync(cancellationToken).ConfigureAwait(false);
        return await _inner.GetCreatorsAsync(limit, page, query, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void InvalidateModel(int modelId) => _cache.InvalidateModel(modelId);

    /// <inheritdoc />
    public void InvalidateVersion(int modelVersionId) => _cache.InvalidateVersion(modelVersionId);

    /// <inheritdoc />
    public void Clear() => _cache.Clear();
}
