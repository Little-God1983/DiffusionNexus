using DiffusionNexus.Civitai;
using FluentAssertions;

namespace DiffusionNexus.Tests.Civitai;

public class CivitaiRateLimitCooldownTests
{
    private long _now;
    private readonly List<TimeSpan> _waits = [];

    private CivitaiRateLimitCooldown Create() => new(
        clock: () => _now,
        delay: (d, _) => { _waits.Add(d); _now += (long)d.TotalMilliseconds; return Task.CompletedTask; });

    [Fact]
    public async Task WaitAsync_DoesNotWait_WhenNoRateLimitSeen()
    {
        var cooldown = Create();

        await cooldown.WaitAsync(CancellationToken.None);

        _waits.Should().BeEmpty();
        cooldown.IntervalMultiplier.Should().Be(1);
    }

    [Fact]
    public async Task WaitAsync_HonoursTheServersRetryAfter()
    {
        var cooldown = Create();
        cooldown.OnRateLimited(TimeSpan.FromSeconds(12));

        await cooldown.WaitAsync(CancellationToken.None);

        _waits.Should().ContainSingle().Which.Should().Be(TimeSpan.FromSeconds(12));
    }

    [Fact]
    public async Task WaitAsync_FallsBackToThirtySeconds_WhenNoRetryAfterGiven()
    {
        var cooldown = Create();
        cooldown.OnRateLimited(null);

        await cooldown.WaitAsync(CancellationToken.None);

        _waits.Should().ContainSingle().Which.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task WaitAsync_WaitsOnlyTheRemainder_ForALaterCaller()
    {
        var cooldown = Create();
        cooldown.OnRateLimited(TimeSpan.FromSeconds(10));
        _now += 4000;

        await cooldown.WaitAsync(CancellationToken.None);

        _waits.Should().ContainSingle().Which.Should().Be(TimeSpan.FromSeconds(6));
    }

    [Fact]
    public void IntervalMultiplier_ASameCallRetry_DoesNotEscalate_EvenAfterItsOwnCooldownElapses()
    {
        // Regression test for finding A (PR #547 round 3 review). This USED TO be named
        // IntervalMultiplier_DoublesPerRateLimitEpisode_CappedAtFour and asserted the multiplier
        // reached 4 here, on the theory that a report arriving after the previous report's own
        // cooldown elapsed always starts a genuinely new episode. That theory is wrong: in
        // production, CivitaiClient's RateLimitDelay sleeps exactly the server's Retry-After
        // between a call's two reports (its own one-retry budget means a call refused twice
        // reports twice), so the second report lands exactly at — not comfortably before — the
        // first report's own cooldown deadline. A purely time-based "is this still the same
        // episode" check cannot tell that apart from a genuinely new episode starting at that same
        // instant, so it escalated the multiplier straight to the 4x cap on the very first rate
        // limit a user ever hit. The old test was inadvertently PINNING that bug rather than
        // guarding against it. isRetryOfReportedCall is CivitaiClient's fix: it tells the cooldown,
        // authoritatively, that this report belongs to a call already reported — see
        // CivitaiClientTests.GetAsync_TwoRateLimitsInOneCall_EscalatesTheSharedCooldownMultiplierOnlyOnce
        // for the same sequence driven through the real client end to end. Contrast with
        // IntervalMultiplier_DoublesPerGenuinelySeparateEpisode_CappedAtFour below, which uses the
        // identical timing but marks each report as a fresh call and correctly keeps escalating.
        var cooldown = Create();

        cooldown.OnRateLimited(TimeSpan.FromSeconds(1));
        cooldown.IntervalMultiplier.Should().Be(2);

        _now += 1001; // past the 1s cooldown the first report set, exactly like a Retry-After sleep
        cooldown.OnRateLimited(TimeSpan.FromSeconds(1), isRetryOfReportedCall: true);
        cooldown.IntervalMultiplier.Should().Be(2, "a call's own in-call retry must not escalate a second time");
    }

    [Fact]
    public void IntervalMultiplier_DoublesPerGenuinelySeparateEpisode_CappedAtFour()
    {
        // Three INDEPENDENT calls — isRetryOfReportedCall: false (the default) for every one of
        // them, meaning each is a fresh call's own first 429 — each arriving after the previous
        // one's cooldown has fully elapsed are genuinely separate rate-limit episodes and must
        // still escalate. Same timing as IntervalMultiplier_ASameCallRetry_DoesNotEscalate...
        // above, deliberately: the flag, not the clock, is what disambiguates the two scenarios.
        var cooldown = Create();

        cooldown.OnRateLimited(TimeSpan.FromSeconds(1));
        cooldown.IntervalMultiplier.Should().Be(2);

        _now += 1001;
        cooldown.OnRateLimited(TimeSpan.FromSeconds(1));
        cooldown.IntervalMultiplier.Should().Be(4);

        _now += 1001;
        cooldown.OnRateLimited(TimeSpan.FromSeconds(1));
        cooldown.IntervalMultiplier.Should().Be(4);
    }

    [Fact]
    public void IntervalMultiplier_TwoReportsWithinTheSameCooldown_StaysAtTwo()
    {
        // Regression test for finding 10. CivitaiClient.GetAsync calls OnRateLimited before
        // deciding whether to retry, and its own maxRateLimitRetries = 1 means a call that is
        // refused twice reports twice for what the user experiences as ONE rate-limit event. Before
        // that fix the multiplier escalated on every report rather than every episode, so the very
        // first rate limit a user ever hit jumped 1 -> 2 -> 4 within a single call. Marked
        // isRetryOfReportedCall: true on the second call to mirror what CivitaiClient actually
        // passes — see the finding-A fix above for the case (equal timing) this alone does not
        // cover.
        var cooldown = Create();

        cooldown.OnRateLimited(TimeSpan.FromSeconds(10));
        cooldown.OnRateLimited(TimeSpan.FromSeconds(10), isRetryOfReportedCall: true); // second report, same clock tick, cooldown from the first still active

        cooldown.IntervalMultiplier.Should().Be(2);
    }

    [Fact]
    public void IntervalMultiplier_DecaysToOne_AfterFiveQuietMinutes()
    {
        var cooldown = Create();
        cooldown.OnRateLimited(null);

        _now += (long)TimeSpan.FromMinutes(5).TotalMilliseconds + 1;

        cooldown.IntervalMultiplier.Should().Be(1);
    }

    [Fact]
    public async Task WaitAsync_ExtendsNeverShortens_WhenSecondRateLimitHasShorterWait()
    {
        var cooldown = Create();
        cooldown.OnRateLimited(TimeSpan.FromSeconds(30));
        cooldown.OnRateLimited(TimeSpan.FromSeconds(5));  // Second call with shorter wait, same clock time

        await cooldown.WaitAsync(CancellationToken.None);

        _waits.Should().ContainSingle().Which.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task WaitAsync_HonoursTheCancellationToken()
    {
        var cooldown = new CivitaiRateLimitCooldown(
            clock: () => _now,
            delay: async (d, ct) =>
            {
                _waits.Add(d);
                await Task.Delay(d, ct).ConfigureAwait(false);
            });

        cooldown.OnRateLimited(TimeSpan.FromSeconds(30));
        var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => cooldown.WaitAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
