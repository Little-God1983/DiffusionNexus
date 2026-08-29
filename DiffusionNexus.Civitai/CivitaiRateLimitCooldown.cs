namespace DiffusionNexus.Civitai;

/// <summary>
/// The process's memory of the last 429: a deadline everybody waits for, and a multiplier that
/// widens the request spacing while Civitai is unhappy with us.
/// </summary>
/// <remarks>
/// A singleton on purpose. The bug this exists to fix is that each surface used to discover the
/// rate limit on its own and keep digging in the meantime; a second instance would restore it.
/// The clock is <see cref="Environment.TickCount64"/> — monotonic, so an NTP correction cannot
/// turn a 30 s cooldown into an hour.
/// </remarks>
public sealed class CivitaiRateLimitCooldown : ICivitaiRateLimitObserver
{
    /// <summary>How long to stand down when the server named no wait of its own.</summary>
    public static readonly TimeSpan DefaultCooldown = TimeSpan.FromSeconds(30);

    /// <summary>Widest the spacing may get: 4× turns 1.5 s into 6 s, not into a stall.</summary>
    public const int MaxIntervalMultiplier = 4;

    /// <summary>Quiet time after which the widened spacing goes back to normal.</summary>
    public static readonly TimeSpan MultiplierDecay = TimeSpan.FromMinutes(5);

    private readonly Func<long> _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly object _lock = new();

    private long _cooldownUntil;
    private long _lastRateLimit;
    private int _multiplier = 1;
    private bool _everRateLimited;

    public CivitaiRateLimitCooldown(
        Func<long>? clock = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _clock = clock ?? (() => Environment.TickCount64);
        _delay = delay ?? Task.Delay;
    }

    /// <summary>
    /// The factor the caller should multiply its request spacing by. Decays back to 1 once
    /// <see cref="MultiplierDecay"/> has passed without a 429.
    /// </summary>
    public int IntervalMultiplier
    {
        get
        {
            lock (_lock)
            {
                if (!_everRateLimited) return 1;
                var quietFor = TimeSpan.FromMilliseconds(_clock() - _lastRateLimit);
                return quietFor > MultiplierDecay ? 1 : _multiplier;
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// The multiplier tracks rate-limit EPISODES, not individual reports. <c>CivitaiClient.GetAsync</c>
    /// calls this once per 429 response, and its own single in-call retry means a call that is
    /// refused twice reports twice — escalating on every report used to walk the multiplier
    /// 1 -> 2 -> 4 inside one request, so the first rate limit a user ever hit jumped straight to
    /// the 4x cap.
    /// <para>
    /// <paramref name="isRetryOfReportedCall"/> is the authoritative signal for that specific case:
    /// <c>CivitaiClient</c> passes true for a report that is its own in-call retry re-hitting the
    /// limit, and such a report never escalates, however the clock lines up. That distinction
    /// matters because a purely time-based fallback — "did an earlier cooldown deadline already
    /// pass?" — only works when the caller's retry sleep is SHORTER than the cooldown it just set.
    /// With a server <c>Retry-After</c> the client's own <c>RateLimitDelay</c> sleeps exactly that
    /// long, so the second report can land at (or a hair past) the first report's own deadline —
    /// indistinguishable, by time alone, from a genuinely new episode starting at that instant. The
    /// time-based check (a report arriving while a previous report's cooldown is still counting
    /// down is the same episode) is kept only as a fallback for reports that do NOT carry the
    /// call-level signal — e.g. two independently concurrent calls that both happen to hit the
    /// limit in the same window. A report that escalates does so from 1 again if the quiet spell
    /// since the last one exceeded <see cref="MultiplierDecay"/>. The cooldown deadline itself
    /// still only ever extends, regardless of episode.
    /// </para>
    /// </remarks>
    public void OnRateLimited(TimeSpan? retryAfter, bool isRetryOfReportedCall = false)
    {
        lock (_lock)
        {
            var now = _clock();
            var wait = retryAfter ?? DefaultCooldown;

            // Captured before _cooldownUntil is possibly extended below, so the timing fallback
            // reflects whether A COOLDOWN FROM AN EARLIER REPORT was still active when THIS report
            // arrived. The call-level signal, when the caller supplies it, overrides this outright.
            var sameEpisode = isRetryOfReportedCall || (_everRateLimited && now < _cooldownUntil);

            // Extend, never shorten: a second 429 while a longer cooldown is running must not
            // release everyone early.
            var deadline = now + (long)wait.TotalMilliseconds;
            if (!_everRateLimited || deadline > _cooldownUntil) _cooldownUntil = deadline;

            if (!sameEpisode)
            {
                // Read through the property's decay rule so a limit after a long quiet spell starts
                // from 1 again rather than resuming yesterday's penalty.
                var current = !_everRateLimited || TimeSpan.FromMilliseconds(now - _lastRateLimit) > MultiplierDecay
                    ? 1
                    : _multiplier;
                _multiplier = Math.Min(current * 2, MaxIntervalMultiplier);
            }

            _lastRateLimit = now;
            _everRateLimited = true;
        }
    }

    /// <summary>
    /// Returns once any active cooldown has elapsed. Honours <paramref name="ct"/> so a cancelled
    /// download or a closed dialog does not sit out the full wait.
    /// </summary>
    public async Task WaitAsync(CancellationToken ct = default)
    {
        while (true)
        {
            TimeSpan remaining;
            lock (_lock)
            {
                if (!_everRateLimited) return;
                remaining = TimeSpan.FromMilliseconds(_cooldownUntil - _clock());
            }

            if (remaining <= TimeSpan.Zero) return;
            await _delay(remaining, ct).ConfigureAwait(false);
        }
    }
}
