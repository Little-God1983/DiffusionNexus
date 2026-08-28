using DiffusionNexus.Civitai;
using DiffusionNexus.Civitai.Models;
using FluentAssertions;

namespace DiffusionNexus.Tests.Civitai;

public class CivitaiApiGatewayTests
{
    private sealed class CountingClient : ICivitaiClient
    {
        public int ModelCalls;
        public int VersionCalls;
        public int HashCalls;
        public int SearchCalls;
        public List<string?> ApiKeysSeen { get; } = [];
        public Func<int, CivitaiModel?>? ModelResponder;

        public Task<CivitaiPagedResponse<CivitaiModel>> GetModelsAsync(
            CivitaiModelsQuery? query = null, string? apiKey = null, CancellationToken cancellationToken = default)
        {
            SearchCalls++;
            ApiKeysSeen.Add(apiKey);
            return Task.FromResult(new CivitaiPagedResponse<CivitaiModel>());
        }

        public Task<CivitaiModel?> GetModelAsync(
            int modelId, string? apiKey = null, CancellationToken cancellationToken = default)
        {
            ModelCalls++;
            ApiKeysSeen.Add(apiKey);
            var model = ModelResponder is not null
                ? ModelResponder(modelId)
                : new CivitaiModel { Id = modelId, Name = $"model-{modelId}" };
            return Task.FromResult(model);
        }

        public Task<CivitaiModelVersion?> GetModelVersionAsync(
            int modelVersionId, string? apiKey = null, CancellationToken cancellationToken = default)
        {
            VersionCalls++;
            return Task.FromResult<CivitaiModelVersion?>(new CivitaiModelVersion { Id = modelVersionId });
        }

        public Task<CivitaiModelVersion?> GetModelVersionByHashAsync(
            string hash, string? apiKey = null, CancellationToken cancellationToken = default)
        {
            HashCalls++;
            return Task.FromResult<CivitaiModelVersion?>(new CivitaiModelVersion { Id = 1 });
        }

        public Task<CivitaiPagedResponse<CivitaiModelImage>> GetImagesAsync(
            CivitaiImagesQuery? query = null, string? apiKey = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new CivitaiPagedResponse<CivitaiModelImage>());

        public Task<CivitaiPagedResponse<CivitaiTag>> GetTagsAsync(
            int? limit = null, int? page = null, string? query = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new CivitaiPagedResponse<CivitaiTag>());

        public Task<CivitaiPagedResponse<CivitaiCreatorInfo>> GetCreatorsAsync(
            int? limit = null, int? page = null, string? query = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new CivitaiPagedResponse<CivitaiCreatorInfo>());
    }

    private long _now;
    private readonly List<TimeSpan> _waits = [];

    private (CivitaiApiGateway interactive, CivitaiApiGateway background, CountingClient inner,
             CivitaiRateLimitCooldown cooldown) CreateBoth()
    {
        var inner = new CountingClient();
        Task Delay(TimeSpan d, CancellationToken _) { _waits.Add(d); _now += (long)d.TotalMilliseconds; return Task.CompletedTask; }
        var pacer = new CivitaiRequestPacer(clock: () => _now, delay: Delay);
        var cooldown = new CivitaiRateLimitCooldown(clock: () => _now, delay: Delay);
        var cache = new CivitaiResponseCache(clock: () => _now);

        return (new CivitaiApiGateway(inner, pacer, cooldown, cache, CivitaiCallLane.Interactive),
                new CivitaiApiGateway(inner, pacer, cooldown, cache, CivitaiCallLane.Background),
                inner, cooldown);
    }

    [Fact]
    public async Task GetModelAsync_SecondCallForTheSameId_IsServedFromCache()
    {
        var (interactive, _, inner, _) = CreateBoth();

        await interactive.GetModelAsync(42);
        await interactive.GetModelAsync(42);

        inner.ModelCalls.Should().Be(1);
    }

    [Fact]
    public async Task GetModelAsync_CacheIsSharedBetweenLanes()
    {
        var (interactive, background, inner, _) = CreateBoth();

        await interactive.GetModelAsync(42);
        await background.GetModelAsync(42);

        inner.ModelCalls.Should().Be(1);
    }

    [Fact]
    public async Task GetModelAsync_A404IsCached()
    {
        var (interactive, _, inner, _) = CreateBoth();
        inner.ModelResponder = _ => null;

        (await interactive.GetModelAsync(42)).Should().BeNull();
        (await interactive.GetModelAsync(42)).Should().BeNull();

        inner.ModelCalls.Should().Be(1);
    }

    [Fact]
    public async Task InteractiveLane_SpacesRequestsBy750ms()
    {
        var (interactive, _, _, _) = CreateBoth();

        await interactive.GetModelAsync(1);
        await interactive.GetModelAsync(2);

        _waits.Should().ContainSingle().Which.Should().Be(TimeSpan.FromMilliseconds(750));
    }

    [Fact]
    public async Task BackgroundLane_SpacesRequestsBy1500ms()
    {
        var (_, background, _, _) = CreateBoth();

        await background.GetModelAsync(1);
        await background.GetModelAsync(2);

        _waits.Should().ContainSingle().Which.Should().Be(TimeSpan.FromMilliseconds(1500));
    }

    [Fact]
    public async Task BackgroundLane_YieldsToAnInteractiveCall_ViaTheSharedTimestamp()
    {
        var (interactive, background, _, _) = CreateBoth();

        await background.GetModelAsync(1);
        await interactive.GetModelAsync(2);

        // The interactive caller waits only its own 750 ms, not the background 1500 ms.
        _waits.Should().ContainSingle().Which.Should().Be(TimeSpan.FromMilliseconds(750));
    }

    [Fact]
    public async Task ARateLimitSeenByOneLane_DelaysTheNextCallOnTheOther()
    {
        var (interactive, background, _, cooldown) = CreateBoth();
        await interactive.GetModelAsync(1);
        _waits.Clear();

        cooldown.OnRateLimited(TimeSpan.FromSeconds(20));
        await background.GetModelAsync(2);

        _waits.Should().Contain(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task RateLimitMultiplier_WidensTheLaneInterval()
    {
        var (interactive, _, _, cooldown) = CreateBoth();
        cooldown.OnRateLimited(TimeSpan.Zero);   // multiplier 2, no cooldown wait
        await interactive.GetModelAsync(1);
        _waits.Clear();

        await interactive.GetModelAsync(2);

        _waits.Should().ContainSingle().Which.Should().Be(TimeSpan.FromMilliseconds(1500));
    }

    [Fact]
    public async Task InvalidateModel_ForcesARefetch()
    {
        var (interactive, _, inner, _) = CreateBoth();

        await interactive.GetModelAsync(42);
        interactive.InvalidateModel(42);
        await interactive.GetModelAsync(42);

        inner.ModelCalls.Should().Be(2);
    }

    [Fact]
    public async Task ChangingTheApiKey_ClearsTheCache()
    {
        var (interactive, _, inner, _) = CreateBoth();

        await interactive.GetModelAsync(42, apiKey: null);
        await interactive.GetModelAsync(42, apiKey: "key-a");

        inner.ModelCalls.Should().Be(2);
        inner.ApiKeysSeen.Should().Equal(null, "key-a");
    }

    [Fact]
    public async Task SameApiKeyTwice_StillHitsTheCache()
    {
        var (interactive, _, inner, _) = CreateBoth();

        await interactive.GetModelAsync(42, apiKey: "key-a");
        await interactive.GetModelAsync(42, apiKey: "key-a");

        inner.ModelCalls.Should().Be(1);
    }

    [Fact]
    public async Task GetModelsAsync_CachesBySerializedQuery()
    {
        var (interactive, _, inner, _) = CreateBoth();
        var query = new CivitaiModelsQuery { Limit = 50, Query = "anime" };

        await interactive.GetModelsAsync(query);
        await interactive.GetModelsAsync(new CivitaiModelsQuery { Limit = 50, Query = "anime" });
        await interactive.GetModelsAsync(new CivitaiModelsQuery { Limit = 50, Query = "realistic" });

        inner.SearchCalls.Should().Be(2);
    }

    [Fact]
    public async Task GetModelVersionByHashAsync_IsCachedCaseInsensitively()
    {
        var (interactive, _, inner, _) = CreateBoth();

        await interactive.GetModelVersionByHashAsync("abcDEF");
        await interactive.GetModelVersionByHashAsync("ABCdef");

        inner.HashCalls.Should().Be(1);
    }
}
