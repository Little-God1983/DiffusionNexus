using DiffusionNexus.Civitai;
using FluentAssertions;

namespace DiffusionNexus.Tests.Civitai;

public class CivitaiResponseCacheTests
{
    private long _now;
    private CivitaiResponseCache Create(int capacity = 1000) => new(capacity, () => _now);

    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(15);

    [Fact]
    public async Task GetOrAddAsync_SecondCallForTheSameKey_DoesNotCallTheFactory()
    {
        var cache = Create();
        var calls = 0;

        var first = await cache.GetOrAddAsync(CivitaiResponseCache.ModelKey(7), Ttl,
            () => { calls++; return Task.FromResult<string?>("value"); });
        var second = await cache.GetOrAddAsync(CivitaiResponseCache.ModelKey(7), Ttl,
            () => { calls++; return Task.FromResult<string?>("value"); });

        calls.Should().Be(1);
        first.Should().Be("value");
        second.Should().Be("value");
    }

    [Fact]
    public async Task GetOrAddAsync_RefetchesAfterTheTtlExpires()
    {
        var cache = Create();
        var calls = 0;
        Task<string?> Factory() { calls++; return Task.FromResult<string?>("value"); }

        await cache.GetOrAddAsync(CivitaiResponseCache.ModelKey(7), Ttl, Factory);
        _now += (long)Ttl.TotalMilliseconds + 1;
        await cache.GetOrAddAsync(CivitaiResponseCache.ModelKey(7), Ttl, Factory);

        calls.Should().Be(2);
    }

    [Fact]
    public async Task GetOrAddAsync_CachesNull_SoA404IsNotReAsked()
    {
        var cache = Create();
        var calls = 0;
        Task<string?> Factory() { calls++; return Task.FromResult<string?>(null); }

        (await cache.GetOrAddAsync(CivitaiResponseCache.ModelKey(7), Ttl, Factory)).Should().BeNull();
        (await cache.GetOrAddAsync(CivitaiResponseCache.ModelKey(7), Ttl, Factory)).Should().BeNull();

        calls.Should().Be(1);
    }

    [Fact]
    public async Task GetOrAddAsync_DoesNotCacheExceptions()
    {
        var cache = Create();
        var calls = 0;

        Task<string?> Throwing() { calls++; throw new InvalidOperationException("boom"); }

        var act = async () => await cache.GetOrAddAsync(CivitaiResponseCache.ModelKey(7), Ttl, Throwing);
        await act.Should().ThrowAsync<InvalidOperationException>();

        var second = await cache.GetOrAddAsync(CivitaiResponseCache.ModelKey(7), Ttl,
            () => Task.FromResult<string?>("recovered"));

        calls.Should().Be(1);
        second.Should().Be("recovered");
    }

    [Fact]
    public async Task GetOrAddAsync_ConcurrentCallersForOneKey_ShareASingleFactoryCall()
    {
        var cache = Create();
        var calls = 0;
        var release = new TaskCompletionSource<string?>();

        Task<string?> Factory() { Interlocked.Increment(ref calls); return release.Task; }

        var a = cache.GetOrAddAsync(CivitaiResponseCache.ModelKey(7), Ttl, Factory);
        var b = cache.GetOrAddAsync(CivitaiResponseCache.ModelKey(7), Ttl, Factory);
        release.SetResult("value");

        (await a).Should().Be("value");
        (await b).Should().Be("value");
        calls.Should().Be(1);
    }

    [Fact]
    public async Task GetOrAddAsync_ManyRealThreadsRacingForOneKey_FactoryRunsExactlyOnce()
    {
        // Unlike GetOrAddAsync_ConcurrentCallersForOneKey_ShareASingleFactoryCall (which issues
        // both calls sequentially on the calling thread and only passes because GetOrAdd
        // synchronously registers the first task before that call ever yields), this test starts
        // several callers on real thread-pool threads so they can genuinely race inside
        // GetOrAddAsync before any of them has published an in-flight entry.
        var cache = Create();
        var calls = 0;
        var release = new TaskCompletionSource<string?>();
        const int callerCount = 8;

        Task<string?> Factory() { Interlocked.Increment(ref calls); return release.Task; }

        var barrier = new Barrier(callerCount);
        var callers = Enumerable.Range(0, callerCount)
            .Select(_ => Task.Run(async () =>
            {
                barrier.SignalAndWait();
                return await cache.GetOrAddAsync(CivitaiResponseCache.ModelKey(7), Ttl, Factory);
            }))
            .ToArray();

        // Give the racing threads a moment to actually reach GetOrAddAsync before releasing them,
        // so the race is against each other and not against this thread finishing first.
        await Task.Delay(50);
        release.SetResult("value");

        var results = await Task.WhenAll(callers);

        calls.Should().Be(1);
        results.Should().OnlyContain(r => r == "value");
    }

    [Fact]
    public async Task InvalidateModel_ForcesTheNextCallToRefetch()
    {
        var cache = Create();
        var calls = 0;
        Task<string?> Factory() { calls++; return Task.FromResult<string?>("value"); }

        await cache.GetOrAddAsync(CivitaiResponseCache.ModelKey(7), Ttl, Factory);
        cache.InvalidateModel(7);
        await cache.GetOrAddAsync(CivitaiResponseCache.ModelKey(7), Ttl, Factory);

        calls.Should().Be(2);
    }

    [Fact]
    public async Task Capacity_EvictsTheOldestEntryFirst()
    {
        var cache = Create(capacity: 2);
        var calls = 0;
        Task<string?> Factory() { calls++; return Task.FromResult<string?>("value"); }

        await cache.GetOrAddAsync(CivitaiResponseCache.ModelKey(1), Ttl, Factory);
        await cache.GetOrAddAsync(CivitaiResponseCache.ModelKey(2), Ttl, Factory);
        await cache.GetOrAddAsync(CivitaiResponseCache.ModelKey(3), Ttl, Factory); // evicts key 1
        await cache.GetOrAddAsync(CivitaiResponseCache.ModelKey(1), Ttl, Factory); // refetch

        calls.Should().Be(4);
    }

    [Fact]
    public void HashKey_IsCaseInsensitive()
    {
        CivitaiResponseCache.HashKey("abcDEF").Should().Be(CivitaiResponseCache.HashKey("ABCdef"));
    }

    [Fact]
    public async Task ClearDuringAnInFlightFetch_PreventsThatFetchFromPopulatingTheCache()
    {
        // Regression test for the round-1 generation-counter fix. Before it, RunAsync wrote its
        // result into _entries unconditionally, so a fetch that was already running when Clear()
        // ran would land its (by-then-stale) answer back into the just-cleared cache with a fresh
        // TTL, as if nothing had happened.
        var cache = Create();
        var calls = 0;
        var release = new TaskCompletionSource<string?>();
        Task<string?> Factory() { calls++; return release.Task; }

        var first = cache.GetOrAddAsync(CivitaiResponseCache.ModelKey(7), Ttl, Factory);
        cache.Clear();
        release.SetResult("stale");

        // The caller that started the fetch still gets its answer — only the cache write is
        // suppressed, not the result flowing back to whoever was already waiting for it.
        (await first).Should().Be("stale");
        calls.Should().Be(1);

        // A subsequent call must go back to the factory: if the completed fetch above had written
        // "stale" into _entries, this would be served from cache instead and calls would stay 1.
        var second = await cache.GetOrAddAsync(CivitaiResponseCache.ModelKey(7), Ttl, Factory);
        second.Should().Be("stale");
        calls.Should().Be(2);
    }

    [Fact]
    public async Task Clear_DropsInFlightEntries_SoALaterCallerStartsItsOwnFetchRatherThanJoining()
    {
        // Regression test for the round-1 fix's other half. Before it, Clear() emptied _entries
        // but left _inFlight untouched, so a caller arriving after Clear() would still find and
        // join the pre-Clear fetch's Lazy — receiving whatever that older fetch answers (e.g. an
        // anonymous answer) instead of starting its own, freshly-keyed one.
        var cache = Create();
        var release = new TaskCompletionSource<string?>();
        var firstCalls = 0;
        var secondCalls = 0;
        Task<string?> FirstFactory() { firstCalls++; return release.Task; }
        Task<string?> SecondFactory() { secondCalls++; return Task.FromResult<string?>("fresh"); }

        var first = cache.GetOrAddAsync(CivitaiResponseCache.ModelKey(7), Ttl, FirstFactory);
        cache.Clear();
        var second = cache.GetOrAddAsync(CivitaiResponseCache.ModelKey(7), Ttl, SecondFactory);

        // If Clear() had left the in-flight entry in place, `second` would join `first`'s Lazy and
        // block on `release` (not set yet) instead of running SecondFactory. Bounded so a
        // regression fails fast within this test instead of hanging the whole run.
        var finished = await Task.WhenAny(second, Task.Delay(TimeSpan.FromSeconds(2)));
        finished.Should().BeSameAs(second, "a caller arriving after Clear() must start its own fetch, not join one Clear() should have dropped");

        (await second).Should().Be("fresh");
        secondCalls.Should().Be(1);

        release.SetResult("stale");
        (await first).Should().Be("stale");
        firstCalls.Should().Be(1);
    }
}
