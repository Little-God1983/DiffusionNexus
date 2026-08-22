using DiffusionNexus.Service.Services.Sync;
using FluentAssertions;

namespace DiffusionNexus.Tests.Sync.Service;

/// <summary>
/// Covers <see cref="CivitaiRequestPacer"/> — the per-request courtesy interval (R4). The clock
/// and the sleep are both seams, so these tests measure the pacing decision rather than waiting
/// for it: a test that actually slept 1.5 s to prove a 1.5 s pause is a test nobody runs.
/// </summary>
public sealed class CivitaiRequestPacerTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(1500);

    /// <summary>A pacer over a hand-cranked clock, recording every sleep it asks for.</summary>
    private static (CivitaiRequestPacer Pacer, List<TimeSpan> Waits, Action<long> Advance) NewPacer()
    {
        var now = 0L;
        var waits = new List<TimeSpan>();

        var pacer = new CivitaiRequestPacer(
            Interval,
            clock: () => now,
            delay: (d, _) => { waits.Add(d); now += (long)d.TotalMilliseconds; return Task.CompletedTask; });

        return (pacer, waits, ms => now += ms);
    }

    [Fact]
    public async Task FirstCallDoesNotWait()
    {
        var (pacer, waits, _) = NewPacer();

        await pacer.WaitAsync();

        // Timestamp-based, not tick-based: nothing has been asked of Civitai in this process yet,
        // so the first request of a run goes out immediately.
        waits.Should().BeEmpty();
    }

    [Fact]
    public async Task SecondCallWaitsTheFullInterval()
    {
        var (pacer, waits, _) = NewPacer();

        await pacer.WaitAsync();
        await pacer.WaitAsync();

        waits.Should().ContainSingle().Which.Should().BeGreaterThanOrEqualTo(Interval);
    }

    [Fact]
    public async Task ACallAfterTheIntervalDoesNotWait()
    {
        var (pacer, waits, advance) = NewPacer();

        await pacer.WaitAsync();
        advance(2000);
        await pacer.WaitAsync();

        waits.Should().BeEmpty("the interval had already elapsed on its own");
    }

    [Fact]
    public async Task APartlyElapsedIntervalWaitsOnlyTheRemainder()
    {
        var (pacer, waits, advance) = NewPacer();

        await pacer.WaitAsync();
        advance(500);
        await pacer.WaitAsync();

        waits.Should().ContainSingle().Which.Should().Be(TimeSpan.FromMilliseconds(1000));
    }

    [Fact]
    public async Task ConcurrentCallersAreSerializedIntoSuccessiveSlots()
    {
        var (pacer, waits, _) = NewPacer();

        // Three callers at once must not all decide "1.5 s since the last call" against the same
        // stale timestamp and then fire together.
        await Task.WhenAll(pacer.WaitAsync(), pacer.WaitAsync(), pacer.WaitAsync());

        waits.Should().HaveCount(2);
        waits.Should().AllSatisfy(w => w.Should().BeGreaterThanOrEqualTo(Interval));
    }

    [Fact]
    public async Task CancellationPropagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var pacer = new CivitaiRequestPacer(Interval);

        var act = () => pacer.WaitAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
