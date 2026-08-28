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
    public async Task InvalidateHash_ForcesTheNextCallToRefetch()
    {
        // Item 1 of the follow-up review: a forced per-tile identify re-asks Civitai by file hash
        // (GetModelVersionByHashAsync, cached under hash:{HASH} for 60 minutes — the longest TTL of
        // any entry) before it ever reaches the model page. InvalidateModel alone left that answer
        // able to defeat the forced press for up to an hour; ICivitaiApiCache had no way to drop a
        // hash entry at all.
        var cache = Create();
        var calls = 0;
        Task<string?> Factory() { calls++; return Task.FromResult<string?>("value"); }

        await cache.GetOrAddAsync(CivitaiResponseCache.HashKey("ABCDEF"), Ttl, Factory);
        cache.InvalidateHash("ABCDEF");
        await cache.GetOrAddAsync(CivitaiResponseCache.HashKey("ABCDEF"), Ttl, Factory);

        calls.Should().Be(2);
    }

    [Fact]
    public async Task InvalidateHash_IsCaseInsensitive()
    {
        var cache = Create();
        var calls = 0;
        Task<string?> Factory() { calls++; return Task.FromResult<string?>("value"); }

        await cache.GetOrAddAsync(CivitaiResponseCache.HashKey("abcDEF"), Ttl, Factory);
        cache.InvalidateHash("ABCdef");
        await cache.GetOrAddAsync(CivitaiResponseCache.HashKey("ABCdef"), Ttl, Factory);

        calls.Should().Be(2, "HashKey upper-cases both the stored key and the invalidated one, so the two must agree");
    }

    [Fact]
    public async Task InvalidateModel_DuringAnInFlightFetch_SuppressesItsWrite_WithoutSpawningADuplicateRequest()
    {
        // Regression test for item 2 of the follow-up review. InvalidateModel/InvalidateVersion/
        // InvalidateHash removed from _entries only; a fetch already in flight for that key kept
        // the unchanged global generation, so RunAsync would still write its answer back once it
        // completed — silently repopulating the very entry the caller just asked to be forgotten,
        // defeating the waitlist's and the detail panel's user-pressed re-check fixes in exactly
        // the case they exist for.
        //
        // The fix must not evict the live _inFlight entry to get there — that would reintroduce
        // finding 1's duplicate-request bug (a caller arriving while the original fetch is still
        // running must join it, not start a second one). So this test drives both invariants at
        // once: a caller that joins the pre-invalidation fetch still gets that fetch's own answer
        // without triggering a second factory call, and a caller arriving only after that fetch
        // completes gets a genuine cache miss — proof the invalidated fetch's answer was never
        // written to _entries.
        var cache = Create();
        var leaderCalls = 0;
        var release = new TaskCompletionSource<string?>();
        Task<string?> LeaderFactory() { leaderCalls++; return release.Task; }

        var leader = cache.GetOrAddAsync(CivitaiResponseCache.ModelKey(7), Ttl, LeaderFactory);

        cache.InvalidateModel(7);

        var joinerCalls = 0;
        Task<string?> JoinerFactory() { joinerCalls++; return Task.FromResult<string?>("should not run"); }
        var joiner = cache.GetOrAddAsync(CivitaiResponseCache.ModelKey(7), Ttl, JoinerFactory);

        release.SetResult("pre-invalidation-answer");

        (await leader).Should().Be("pre-invalidation-answer");
        (await joiner).Should().Be("pre-invalidation-answer", "a caller joining a still-live fetch gets that fetch's own answer");
        leaderCalls.Should().Be(1);
        joinerCalls.Should().Be(0, "the in-flight fetch must be joined, not duplicated, even though it was invalidated mid-flight");

        var freshCalls = 0;
        Task<string?> FreshFactory() { freshCalls++; return Task.FromResult<string?>("fresh"); }
        var afterward = await cache.GetOrAddAsync(CivitaiResponseCache.ModelKey(7), Ttl, FreshFactory);

        afterward.Should().Be("fresh");
        freshCalls.Should().Be(1,
            "the invalidated fetch's answer must not have been cached, so this call has to reach the factory");
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

    [Fact]
    public async Task ACancelledAwaiter_DoesNotEvictALiveInFlightEntry()
    {
        // Regression test for finding 1 of the gateway fix-wave review. The old code removed the
        // _inFlight entry in GetOrAddAsync's own `finally`, which runs for EVERY awaiter — including
        // one whose WaitAsync(ct) just threw from cancellation, even while the shared fetch it
        // started is still running. The next caller for that key would then miss TryGet (nothing
        // written yet), miss _inFlight too (just evicted), and start a SECOND concurrent fetch for
        // the same key — exactly the duplicate-request bug single-flight exists to prevent. This is
        // routine, not exotic, in the cancel-heavy paths this PR targets: an update checker starting
        // a fetch and then abandoning it on the next pagination/filter change.
        var cache = Create();
        var calls = 0;
        var release = new TaskCompletionSource<string?>();
        Task<string?> Factory() { Interlocked.Increment(ref calls); return release.Task; }

        using var cts = new CancellationTokenSource();
        var cancelling = cache.GetOrAddAsync(CivitaiResponseCache.ModelKey(7), Ttl, Factory, cts.Token);
        cts.Cancel();
        var cancelAct = async () => await cancelling;
        await cancelAct.Should().ThrowAsync<OperationCanceledException>();

        // A third caller arrives while the fetch the (now-cancelled) first caller started is still
        // running. If the cancelled caller's `finally` had removed the still-live in-flight entry,
        // this call would find nothing in either _entries or _inFlight and start a duplicate fetch.
        var third = cache.GetOrAddAsync(CivitaiResponseCache.ModelKey(7), Ttl, Factory);

        release.SetResult("value");
        (await third).Should().Be("value");
        calls.Should().Be(1, "the still-live in-flight fetch must be joined, not duplicated");
    }

    // AllCallersAbandoning_ThenTheFetchFaulting_DoesNotRaiseAnUnobservedTaskException (finding 6's
    // regression test) was removed here (item 5 of the follow-up review). It asserted that a
    // fault-observing handler never fires after GC.Collect()/WaitForPendingFinalizers()/GC.Collect(),
    // which depends on the faulted continuation's Task actually being unrooted and collected within
    // those two passes — never verified to fail against the pre-fix code (unlike its siblings in
    // this file, all of which were confirmed red via an isolated revert). Verified now, deliberately:
    // with the fault-observing ContinueWith temporarily removed from CivitaiResponseCache.GetOrAddAsync,
    // this test still PASSED — it never actually exercised the bug it claimed to guard. A test that
    // cannot fail is worse than no test, so it is deleted rather than kept and left green by luck.
    // The production fix it was meant to cover (GetOrAddAsync's ContinueWith(observeTask => { _ =
    // observeTask.Exception; }, ...)) is untouched and still in place.
}
