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
    public void IntervalMultiplier_DoublesPerRateLimit_CappedAtFour()
    {
        var cooldown = Create();

        cooldown.OnRateLimited(null);
        cooldown.IntervalMultiplier.Should().Be(2);

        cooldown.OnRateLimited(null);
        cooldown.IntervalMultiplier.Should().Be(4);

        cooldown.OnRateLimited(null);
        cooldown.IntervalMultiplier.Should().Be(4);
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
