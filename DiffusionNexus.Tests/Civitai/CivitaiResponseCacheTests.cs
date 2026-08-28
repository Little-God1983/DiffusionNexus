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
}
