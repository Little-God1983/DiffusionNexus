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

        /// <summary>
        /// When set, <see cref="GetModelAsync"/> awaits this instead of returning synchronously —
        /// simulates a genuinely in-flight HTTP call (like the real <c>HttpClient</c>, it observes
        /// the caller's cancellation token via <see cref="Task.WaitAsync(CancellationToken)"/>
        /// rather than ignoring it), so a single-flight leader/joiner scenario can be driven
        /// deterministically instead of racing real threads.
        /// </summary>
        public TaskCompletionSource<CivitaiModel?>? PendingModelResult;

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
            if (PendingModelResult is { } pending) return pending.Task.WaitAsync(cancellationToken);
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
    public async Task InvalidateHash_ForcesARefetch()
    {
        var (interactive, _, inner, _) = CreateBoth();

        await interactive.GetModelVersionByHashAsync("DEADBEEF");
        interactive.InvalidateHash("DEADBEEF");
        await interactive.GetModelVersionByHashAsync("DEADBEEF");

        inner.HashCalls.Should().Be(2);
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
    public async Task NullAndEmptyApiKey_AreTreatedAsTheSameUnauthenticatedKey()
    {
        // CivitaiClient treats null, "" and whitespace-only identically — it only attaches an
        // Authorization header when the key is non-whitespace. Settings-sourced call sites
        // genuinely alternate between null and "" for "no key yet". Without normalisation, every
        // alternation looks like a real key change, clears the shared cache on every call, and
        // (via the generation bump) can suppress the very write meant to populate it — defeating
        // caching entirely for any caller whose key spelling isn't perfectly consistent.
        var (interactive, _, inner, _) = CreateBoth();

        await interactive.GetModelAsync(42, apiKey: null);
        await interactive.GetModelAsync(42, apiKey: "");

        inner.ModelCalls.Should().Be(1);
    }

    [Fact]
    public async Task ApiKeyChangeAcrossLanes_IsDetectedByBothLanes()
    {
        // Regression test for the round-1 fix: before it, _lastApiKey lived on each
        // CivitaiApiGateway instance, so the background lane's first-ever call had its own
        // _apiKeySeen still false and skipped the clear entirely — it was served the interactive
        // lane's anonymous entry instead of making its own authenticated request. Both lanes now
        // share one CivitaiResponseCache.NoteApiKey, so the key change the interactive lane
        // recorded is visible to the background lane too.
        var (interactive, background, inner, _) = CreateBoth();

        await interactive.GetModelAsync(42, apiKey: null);
        await background.GetModelAsync(42, apiKey: "key-a");

        inner.ModelCalls.Should().Be(2);
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

    [Fact]
    public async Task ACacheHit_CostsNoPacingSlot()
    {
        // If a refactor ever hoisted the cooldown/pacer waits outside the cache factory (so they
        // ran before the cache lookup instead of only on an actual fetch), every existing cache
        // test would still pass — they only count calls into the inner client. This is the test
        // that would catch it: a cache hit must not consume the pacer's timestamp at all, so two
        // calls for the same id record only the ONE wait that separates the two DISTINCT fetches,
        // not one wait per call.
        var (interactive, _, _, _) = CreateBoth();

        await interactive.GetModelAsync(1);       // fetch 1: no prior call, no wait recorded
        await interactive.GetModelAsync(1);        // cache hit: must record no wait
        await interactive.GetModelAsync(2);        // fetch 2: distinct id, must record exactly one wait

        _waits.Should().ContainSingle().Which.Should().Be(TimeSpan.FromMilliseconds(750));
    }

    [Fact]
    public async Task AnAlreadyCancelledToken_StillAbandonsThisCallersOwnWaitPromptly()
    {
        // Superseded by the blocker-1 fix (see LeaderCancellation_DoesNotCancelAJoinerWaitingOnTheSameFetch):
        // this used to assert that an already-cancelled caller's token stopped the shared
        // cooldown/pacer/API-call work before it ever ran (ModelCalls staying at 1) — true only
        // because, pre-fix, that caller's own token WAS the token driving the shared fetch. Now the
        // shared fetch runs on a detached token so no single caller (leader or joiner) can tear it
        // down; a caller can only abandon its OWN wait for the result. So this caller still gets
        // its OperationCanceledException promptly — it does not hang waiting on work it no longer
        // owns — but the detached fetch is unaffected and reaches the real API call regardless.
        // That is the accepted trade-off: an abandoned fetch is cheap (one metadata GET) and still
        // populates the cache rather than being wasted.
        var (interactive, _, inner, _) = CreateBoth();
        // Prime the pacer so a second, distinct call is actually due to wait.
        await interactive.GetModelAsync(1);
        _waits.Clear();

        // Keeps the shared fetch genuinely pending rather than resolving synchronously against the
        // test's zero-latency fake pacer/cooldown, so what is exercised is this caller's own
        // join-wait noticing its cancellation — not an accidental synchronous race. A real HTTP
        // call never completes this fast, so this mirrors production, where the fetch is always
        // still in flight when a caller's own cancellation fires.
        inner.PendingModelResult = new TaskCompletionSource<CivitaiModel?>();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await interactive.GetModelAsync(2, cancellationToken: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        // The pacer wait ran (unaffected by this caller's cancellation) and the detached fetch
        // reached the real inner call — neither is torn down by this caller abandoning its wait.
        _waits.Should().ContainSingle().Which.Should().Be(TimeSpan.FromMilliseconds(750));
        inner.ModelCalls.Should().Be(2);
    }

    [Fact]
    public async Task LeaderCancellation_DoesNotCancelAJoinerWaitingOnTheSameFetch()
    {
        // Blocker-1 regression test. SendAsync's shared factory used to be built from whichever
        // caller's CancellationToken happened to become the single-flight leader's — so that
        // caller cancelling (e.g. LoraUpdateChecker dropping a stale page on "user paginated or
        // filter changed") tore down the in-flight fetch a joiner with its own live token (e.g.
        // ModelDetailViewModel, joining with CancellationToken.None) was still depending on. The
        // joiner must be able to receive the value regardless of what the leader does with ITS OWN
        // token.
        var (interactive, _, inner, _) = CreateBoth();
        inner.PendingModelResult = new TaskCompletionSource<CivitaiModel?>();

        using var leaderCts = new CancellationTokenSource();

        // Started (not awaited) so it registers the in-flight fetch and then suspends on the
        // pending inner call — deterministic on a single thread because CountingClient.GetModelAsync
        // only ever suspends there, never before, for this call shape.
        var leaderTask = interactive.GetModelAsync(42, cancellationToken: leaderCts.Token);
        var joinerTask = interactive.GetModelAsync(42, cancellationToken: CancellationToken.None);

        leaderCts.Cancel();

        // The leader only abandons ITS OWN wait — it must see its own cancellation promptly rather
        // than hang waiting for the still-pending shared fetch.
        var leaderAct = async () => await leaderTask;
        await leaderAct.Should().ThrowAsync<OperationCanceledException>();

        // The shared fetch is still alive: completing it now must resolve the joiner with the real
        // value, not the leader's cancellation. Bounded so a regression (the joiner hanging or
        // throwing) fails fast instead of hanging the whole test run.
        inner.PendingModelResult.SetResult(new CivitaiModel { Id = 42, Name = "model-42" });
        var finished = await Task.WhenAny(joinerTask, Task.Delay(TimeSpan.FromSeconds(2)));
        finished.Should().BeSameAs(joinerTask, "a leader cancelling must not cancel a joiner still waiting on the shared fetch");

        var joinerResult = await joinerTask;
        joinerResult.Should().NotBeNull();
        joinerResult!.Id.Should().Be(42);
    }
}
