using System.Collections.Concurrent;

namespace DiffusionNexus.Civitai;

/// <summary>
/// Lets a caller drop what the gateway remembers about a model or version, for the paths where
/// the user has explicitly asked for fresh data.
/// </summary>
public interface ICivitaiApiCache
{
    /// <summary>Forgets the model page for <paramref name="modelId"/> (a Civitai model id).</summary>
    void InvalidateModel(int modelId);

    /// <summary>Forgets the version record for <paramref name="modelVersionId"/> (a Civitai version id).</summary>
    void InvalidateVersion(int modelVersionId);

    /// <summary>Forgets everything.</summary>
    void Clear();
}

/// <summary>
/// A small bounded store of Civitai responses, with single-flight so N concurrent callers asking
/// for the same model page make one request rather than N.
/// </summary>
/// <remarks>
/// In-memory and process-lifetime on purpose. A disk cache would have to answer questions about
/// staleness that <c>ModelSyncState</c> already answers for the long term; what is missing is
/// only the short window in which several surfaces ask for the same page within seconds — a
/// download's persist and its completion sync, an update check and the detail panel the user
/// then opens.
/// </remarks>
public sealed class CivitaiResponseCache : ICivitaiApiCache
{
    private sealed record Entry(object? Value, long ExpiresAt, long InsertedSequence);

    private readonly int _capacity;
    private readonly Func<long> _clock;
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task<object?>>> _inFlight = new(StringComparer.Ordinal);
    private long _sequence;

    /// <summary>
    /// Bumped by <see cref="Clear"/>. A fetch captures the generation in effect when it starts and
    /// re-checks it before writing its answer back — a fetch that began in an older generation must
    /// not resurrect an entry the newer generation's <see cref="Clear"/> just erased.
    /// </summary>
    private long _generation;

    private readonly object _apiKeyLock = new();
    private string? _lastApiKey;
    private bool _apiKeySeen;

    /// <param name="capacity">Maximum entries before the oldest-inserted are evicted.</param>
    /// <param name="clock">Monotonic millisecond clock. Test seam.</param>
    public CivitaiResponseCache(int capacity = 1000, Func<long>? clock = null)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
        _clock = clock ?? (() => Environment.TickCount64);
    }

    public static string ModelKey(int modelId) => $"model:{modelId}";
    public static string VersionKey(int modelVersionId) => $"version:{modelVersionId}";
    public static string HashKey(string hash) => $"hash:{hash.ToUpperInvariant()}";
    public static string SearchKey(string queryString) => $"search:{queryString}";

    /// <summary>
    /// Records the API key a caller is about to use. Lives here — not on whichever caller happens
    /// to invoke it — because the cache is the shared object multiple lanes read from; tracking
    /// the last-seen key anywhere else lets one lane clear on a change the other lane has already
    /// accounted for, or miss a change the other lane made. Cache keys deliberately omit the API
    /// key — an authenticated and an anonymous request for the same public model return the same
    /// page, and keying by secret would halve the hit rate for nothing — so what must not happen
    /// is an anonymous answer being served to a caller that has since supplied a key (gated models
    /// answer differently). A change of key therefore empties the store.
    /// </summary>
    /// <remarks>
    /// The key is normalised before comparing or storing: <see cref="CivitaiClient"/> treats
    /// <c>null</c>, <c>""</c> and whitespace-only as identically unauthenticated (it only attaches
    /// an Authorization header when the key is non-whitespace), and these values genuinely
    /// alternate call to call — they come from settings, where a not-yet-typed key is <c>""</c> in
    /// one code path and <c>null</c> in another. Comparing them as literally different strings
    /// would make every such alternation look like a real key change: each call would
    /// <see cref="Clear"/> the shared cache, and because <see cref="Clear"/> also bumps the
    /// generation, the concurrent in-flight write from the previous call would be suppressed too
    /// — the cache could end up never actually populating.
    /// </remarks>
    public void NoteApiKey(string? apiKey)
    {
        var normalized = NormalizeApiKey(apiKey);
        lock (_apiKeyLock)
        {
            if (_apiKeySeen && string.Equals(_lastApiKey, normalized, StringComparison.Ordinal)) return;
            if (_apiKeySeen) Clear();
            _lastApiKey = normalized;
            _apiKeySeen = true;
        }
    }

    /// <summary>Collapses every unauthenticated spelling (<c>null</c>, <c>""</c>, whitespace) to one canonical value.</summary>
    private static string? NormalizeApiKey(string? apiKey) => string.IsNullOrWhiteSpace(apiKey) ? null : apiKey;

    /// <summary>
    /// Returns the cached value for <paramref name="key"/>, or awaits <paramref name="factory"/>
    /// once and caches its result. A <c>null</c> result IS cached — a 404 is an answer. An
    /// exception is not: a transient failure must not become a fifteen-minute one.
    /// </summary>
    /// <remarks>
    /// <paramref name="ct"/> governs only THIS caller's wait on the shared result. A caller that
    /// joins an in-flight fetch (rather than starting it) does not inherit the leader's token or
    /// its cancellation — <see cref="Task.WaitAsync(CancellationToken)"/> lets a joiner abandon its
    /// own wait without tearing down the fetch other callers, including the leader, are still
    /// waiting on. Removing the <c>_inFlight</c> entry is likewise owned by the fetch itself (see
    /// <see cref="RunAsync{T}"/>'s <c>finally</c>), not by this method — a caller cancelling its own
    /// <c>WaitAsync(ct)</c> must not evict an entry a still-running fetch, and every other caller
    /// waiting on it, still depends on.
    /// </remarks>
    public async Task<T?> GetOrAddAsync<T>(string key, TimeSpan ttl, Func<Task<T?>> factory, CancellationToken ct = default)
        where T : class
    {
        if (TryGet(key, out var cached)) return (T?)cached;

        var generation = Interlocked.Read(ref _generation);

        // ConcurrentDictionary.GetOrAdd can invoke its value factory on more than one racing
        // thread even though only one result is ever stored — so the value factory here must not
        // do any work itself beyond constructing the Lazy. The Lazy captures a reference to
        // ITSELF (the `self` local, assigned before the factory can possibly be invoked, since
        // Lazy is — by definition — not evaluated until something later reads .Value) so that
        // RunAsync can remove its own _inFlight entry without racing whichever caller happens to
        // trigger the fetch first. Whichever Lazy instance GetOrAdd ends up publishing is decided
        // atomically; only that one is ever forced (via .Value, below), so factory() runs exactly
        // once even under a genuine race, and every caller — winner and losers alike — awaits the
        // same task.
        var lazy = _inFlight.GetOrAdd(key, ignoredKey =>
        {
            Lazy<Task<object?>>? self = null;
            self = new Lazy<Task<object?>>(() =>
            {
                var task = RunAsync(key, ttl, factory, generation, self!);

                // The fetch can outlive every caller (each caller only owns its own WaitAsync(ct)
                // wait, per the remark above) and can still fault after the last one has abandoned
                // it — a rate limit, a DNS blip, a bad JSON shape. Touch .Exception so a fault
                // nobody is left to await does not surface as an UNOBSERVED TASK EXCEPTION at GC
                // time (App.axaml.cs logs those as errors); it should stay a quiet, uncached miss.
                task.ContinueWith(observeTask => { _ = observeTask.Exception; },
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);

                return task;
            }, LazyThreadSafetyMode.ExecutionAndPublication);
            return self;
        });

        return (T?)await lazy.Value.WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task<object?> RunAsync<T>(string key, TimeSpan ttl, Func<Task<T?>> factory, long generation,
        Lazy<Task<object?>> self)
        where T : class
    {
        try
        {
            var value = await factory().ConfigureAwait(false);

            // A Clear() while this fetch was in flight (an API key change, an explicit reset) means
            // the world moved on before this answer arrived. Writing it now would put a stale value
            // back into a store that was just emptied specifically to get rid of stale values.
            if (Interlocked.Read(ref _generation) == generation)
            {
                var now = _clock();
                var sequence = Interlocked.Increment(ref _sequence);
                _entries[key] = new Entry(value, now + (long)ttl.TotalMilliseconds, sequence);
                Trim();
            }

            return value;
        }
        finally
        {
            // Owned by the fetch, not by any individual awaiter (see the remark on
            // GetOrAddAsync). The KeyValuePair overload only removes THIS fetch's own entry, so a
            // newer fetch for the same key that has since replaced it (e.g. after a Clear(),
            // whose blanket _inFlight.Clear() may already have dropped this one) is left alone.
            _inFlight.TryRemove(new KeyValuePair<string, Lazy<Task<object?>>>(key, self));
        }
    }

    private bool TryGet(string key, out object? value)
    {
        value = null;
        if (!_entries.TryGetValue(key, out var entry)) return false;
        if (_clock() >= entry.ExpiresAt)
        {
            _entries.TryRemove(key, out _);
            return false;
        }

        value = entry.Value;
        return true;
    }

    /// <summary>
    /// Oldest-inserted eviction rather than least-recently-used: entries expire on their own
    /// within minutes, so recency buys nothing an LRU's extra bookkeeping would pay for.
    /// Ordered by a monotonic insertion sequence number owned by the cache, not by the clock —
    /// the clock is a test seam that can stand still across many inserts, which would otherwise
    /// leave ties broken by non-deterministic ConcurrentDictionary enumeration order.
    /// </summary>
    private void Trim()
    {
        if (_entries.Count <= _capacity) return;

        foreach (var key in _entries
                     .OrderBy(kv => kv.Value.InsertedSequence)
                     .Take(_entries.Count - _capacity)
                     .Select(kv => kv.Key)
                     .ToList())
        {
            _entries.TryRemove(key, out _);
        }
    }

    /// <inheritdoc />
    public void InvalidateModel(int modelId) => _entries.TryRemove(ModelKey(modelId), out _);

    /// <inheritdoc />
    public void InvalidateVersion(int modelVersionId) => _entries.TryRemove(VersionKey(modelVersionId), out _);

    /// <inheritdoc />
    /// <remarks>
    /// Bumps the generation before clearing so any fetch already in flight — whose result has not
    /// been written yet — is stamped as stale and will not repopulate the store it started
    /// against. In-flight entries are dropped too: a caller that arrives after this Clear() must
    /// start its own fetch (able to see whatever changed, e.g. a new API key) rather than join a
    /// fetch that began before the change and carries the old answer.
    /// </remarks>
    public void Clear()
    {
        Interlocked.Increment(ref _generation);
        _entries.Clear();
        _inFlight.Clear();
    }
}
