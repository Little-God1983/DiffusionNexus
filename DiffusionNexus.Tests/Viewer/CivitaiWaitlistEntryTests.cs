using DiffusionNexus.Civitai.Models;
using DiffusionNexus.UI.Services.CivitaiBrowser;
using FluentAssertions;

namespace DiffusionNexus.Tests.Viewer;

/// <summary>
/// Covers the waitlist entry's local availability computation: countdown text,
/// deadline-passed promotion to Available, terminal statuses, and the
/// IsPermanentlyPaid extension that gates what may be waitlisted at all.
/// </summary>
public sealed class CivitaiWaitlistEntryTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 13, 10, 0, 0, TimeSpan.Zero);

    private static CivitaiWaitlistEntry Entry(DateTimeOffset? deadline, WaitlistEntryStatus status = WaitlistEntryStatus.Waiting) => new()
    {
        ModelId = 1,
        VersionId = 2,
        ModelName = "Model",
        VersionName = "v1",
        EarlyAccessDeadline = deadline,
        Status = status
    };

    [Fact]
    public void FutureDeadline_CountsDownInDaysAndHours()
    {
        var e = Entry(Now.AddDays(2).AddHours(4).AddMinutes(30));
        e.RefreshAvailability(Now);
        e.IsAvailable.Should().BeFalse();
        e.Status.Should().Be(WaitlistEntryStatus.Waiting);
        e.CountdownDisplay.Should().Be("free in 2d 4h");
    }

    [Fact]
    public void FutureDeadline_UnderADay_CountsDownInHoursAndMinutes()
    {
        var e = Entry(Now.AddHours(3).AddMinutes(12));
        e.RefreshAvailability(Now);
        e.CountdownDisplay.Should().Be("free in 3h 12m");
    }

    [Fact]
    public void FutureDeadline_UnderAnHour_CountsDownInMinutes()
    {
        var e = Entry(Now.AddMinutes(45));
        e.RefreshAvailability(Now);
        e.CountdownDisplay.Should().Be("free in 45m");
    }

    [Fact]
    public void PassedDeadline_BecomesAvailable()
    {
        var e = Entry(Now.AddMinutes(-1));
        e.RefreshAvailability(Now);
        e.IsAvailable.Should().BeTrue();
        e.Status.Should().Be(WaitlistEntryStatus.Available);
        e.CountdownDisplay.Should().Be("Available now");
    }

    [Fact]
    public void AvailableEntry_WithExtendedDeadline_DemotesBackToWaiting()
    {
        // Re-check discovered the creator extended early access.
        var e = Entry(Now.AddDays(3), WaitlistEntryStatus.Available);
        e.RefreshAvailability(Now);
        e.IsAvailable.Should().BeFalse();
        e.Status.Should().Be(WaitlistEntryStatus.Waiting);
    }

    [Fact]
    public void PermanentlyPaid_IsNeverAvailable_EvenWithPassedDeadline()
    {
        var e = Entry(Now.AddDays(-5), WaitlistEntryStatus.PermanentlyPaid);
        e.RefreshAvailability(Now);
        e.IsAvailable.Should().BeFalse();
        e.Status.Should().Be(WaitlistEntryStatus.PermanentlyPaid);
        e.CountdownDisplay.Should().Be("Permanently paid — won't become free");
    }

    [Fact]
    public void UnavailableEntry_StaysUnavailable()
    {
        var e = Entry(null, WaitlistEntryStatus.Unavailable);
        e.RefreshAvailability(Now);
        e.IsAvailable.Should().BeFalse();
        e.CountdownDisplay.Should().Be("No longer available on Civitai");
    }

    [Fact]
    public void CheckFailedEntry_WithPassedDeadline_StillBecomesAvailable()
    {
        // A stale network failure must not pin the entry — move-to-queue re-verifies anyway.
        var e = Entry(Now.AddMinutes(-1), WaitlistEntryStatus.CheckFailed);
        e.RefreshAvailability(Now);
        e.IsAvailable.Should().BeTrue();
        e.Status.Should().Be(WaitlistEntryStatus.Available);
    }

    [Fact]
    public void NoDeadline_NonAvailableStatus_ShowsUnknownEndDate()
    {
        var e = Entry(null, WaitlistEntryStatus.Waiting);
        e.RefreshAvailability(Now);
        e.IsAvailable.Should().BeFalse();
        e.CountdownDisplay.Should().Be("Early access — no end date published");
    }

    [Fact]
    public void NoDeadline_AvailableStatus_StaysAvailable()
    {
        // A re-check that confirmed "free" clears the deadline and sets Available.
        var e = Entry(null, WaitlistEntryStatus.Available);
        e.RefreshAvailability(Now);
        e.IsAvailable.Should().BeTrue();
        e.CountdownDisplay.Should().Be("Available now");
    }

    [Fact]
    public void IsPermanentlyPaid_TrueOnlyForPermanentPaidAccess()
    {
        new CivitaiModelVersion { Id = 1, PaidAccess = new CivitaiPaidAccess { Permanent = true } }
            .IsPermanentlyPaid().Should().BeTrue();
        new CivitaiModelVersion { Id = 2, PaidAccess = new CivitaiPaidAccess { Permanent = false, EndsAt = Now.AddDays(7) } }
            .IsPermanentlyPaid().Should().BeFalse();
        new CivitaiModelVersion { Id = 3 }.IsPermanentlyPaid().Should().BeFalse();
        ((CivitaiModelVersion?)null).IsPermanentlyPaid().Should().BeFalse();
    }
}
