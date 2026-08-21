namespace DiffusionNexus.Service.Services.Sync;

/// <summary>
/// Keeps a minimum interval between two Civitai API requests, whoever makes them.
/// </summary>
/// <remarks>
/// Pacing belongs at the call site, not at the item: one <c>SyncItem</c> is not one request.
/// The images step calls Civitai once per <i>version</i> while grouping its items per model
/// (six versions were six back-to-back calls), and the identify step makes two — the hash
/// lookup and the model page. Pacing between items left both bursts unpaced and then
/// apologised for them afterwards.
/// </remarks>
public interface ICivitaiRequestPacer
{
    /// <summary>
    /// Returns once the caller may issue its request, having waited out whatever is left of the
    /// interval since the previous call. Call it immediately before the request, never after.
    /// </summary>
    Task WaitAsync(CancellationToken ct = default);
}

/// <summary>
/// The real <see cref="ICivitaiRequestPacer"/>: a single-slot gate plus the timestamp of the last
/// call, so the wait is "how long since the previous request" rather than a blind sleep per call.
/// </summary>
/// <remarks>
/// <para>
/// Timestamp-based on purpose. A blind <c>Task.Delay</c> per call charges the first request of a
/// run for the sins of a run that finished an hour ago; measuring from the last call means an idle
/// pacer lets a request through instantly and a busy one still spaces requests exactly.
/// </para>
/// <para>
/// The clock is <see cref="Environment.TickCount64"/> — monotonic, so a wall-clock adjustment (NTP,
/// a DST jump, the user setting the time) cannot turn the interval into an hour-long stall or
/// nothing at all. Registered as a singleton: it is the process's opinion about Civitai, and two
/// pacers would pace nothing.
/// </para>
/// </remarks>
public sealed class CivitaiRequestPacer : ICivitaiRequestPacer
{
    /// <summary>Civitai's own courtesy interval between calls.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(1500);

    private readonly TimeSpan _minInterval;
    private readonly Func<long> _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    // One slot: two callers must not both measure against the same stale timestamp and then fire
    // together. The wait itself happens inside the gate, which is what turns concurrent callers
    // into successive slots rather than a burst.
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Milliseconds (monotonic) at which the last request was released; null before the first.</summary>
    private long? _lastCall;

    /// <param name="minInterval">Minimum spacing between two requests; defaults to <see cref="DefaultInterval"/>.</param>
    /// <param name="clock">Monotonic millisecond clock. Test seam — defaults to <see cref="Environment.TickCount64"/>.</param>
    /// <param name="delay">How to wait. Test seam — defaults to <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</param>
    public CivitaiRequestPacer(
        TimeSpan? minInterval = null,
        Func<long>? clock = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _minInterval = minInterval ?? DefaultInterval;
        _clock = clock ?? (() => Environment.TickCount64);
        _delay = delay ?? Task.Delay;
    }

    /// <inheritdoc />
    public async Task WaitAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_lastCall is { } last)
            {
                var remaining = _minInterval - TimeSpan.FromMilliseconds(_clock() - last);
                if (remaining > TimeSpan.Zero) await _delay(remaining, ct).ConfigureAwait(false);
            }

            // Stamped after the wait, not before it: this is the moment the request is released,
            // and the next caller measures from here.
            _lastCall = _clock();
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>
/// A pacer that never waits — for tests and for the applier's optional-dependency default, where
/// no request is actually leaving the machine.
/// </summary>
public sealed class NoCivitaiRequestPacer : ICivitaiRequestPacer
{
    public static readonly NoCivitaiRequestPacer Instance = new();

    /// <inheritdoc />
    public Task WaitAsync(CancellationToken ct = default) => Task.CompletedTask;
}
