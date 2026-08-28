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
    /// Returns the cached value for <paramref name="key"/>, or awaits <paramref name="factory"/>
    /// once and caches its result. A <c>null</c> result IS cached — a 404 is an answer. An
    /// exception is not: a transient failure must not become a fifteen-minute one.
    /// </summary>
    public async Task<T?> GetOrAddAsync<T>(string key, TimeSpan ttl, Func<Task<T?>> factory)
        where T : class
    {
        if (TryGet(key, out var cached)) return (T?)cached;

        // ConcurrentDictionary.GetOrAdd can invoke its value factory on more than one racing
        // thread even though only one result is ever stored — so the value factory here must not
        // do any work itself. It only constructs a Lazy<Task<object?>>, which is cheap and starts
        // nothing. Whichever Lazy instance GetOrAdd ends up publishing is decided atomically; only
        // that one is ever forced (via .Value, below), so factory() runs exactly once even under a
        // genuine race, and every caller — winner and losers alike — awaits the same task.
        var lazy = _inFlight.GetOrAdd(key, _ => new Lazy<Task<object?>>(
            () => RunAsync(key, ttl, factory), LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            return (T?)await lazy.Value.ConfigureAwait(false);
        }
        finally
        {
            _inFlight.TryRemove(new KeyValuePair<string, Lazy<Task<object?>>>(key, lazy));
        }
    }

    private async Task<object?> RunAsync<T>(string key, TimeSpan ttl, Func<Task<T?>> factory)
        where T : class
    {
        var value = await factory().ConfigureAwait(false);

        var now = _clock();
        var sequence = Interlocked.Increment(ref _sequence);
        _entries[key] = new Entry(value, now + (long)ttl.TotalMilliseconds, sequence);
        Trim();
        return value;
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
    public void Clear() => _entries.Clear();
}
