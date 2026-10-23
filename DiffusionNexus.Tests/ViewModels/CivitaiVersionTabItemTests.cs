using DiffusionNexus.Civitai.Models;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;

namespace DiffusionNexus.Tests.ViewModels;

/// <summary>
/// Covers the early-access/paywall flags on the detail panel's version tabs. The rules
/// mirror <see cref="DiffusionNexus.UI.ViewModels.CivitaiBrowser.CivitaiVersionPickItemViewModel"/>
/// (purple "EA" only for a gate that expires, red "PAID" for a permanent one) so both
/// surfaces classify a version identically.
/// </summary>
public class CivitaiVersionTabItemTests
{
    private static readonly DateTimeOffset Now =
        new(DateTime.UtcNow.Date.AddHours(10), TimeSpan.Zero);

    private static CivitaiVersionTabItem Tab(CivitaiModelVersion version)
        => new(version, localVersion: null, label: "v1", onSelected: _ => { });

    [Fact]
    public void FutureDeadline_FlagsEarlyAccessWithEaBadge()
    {
        var tab = Tab(new CivitaiModelVersion { Id = 1, EarlyAccessDeadline = Now.AddDays(7) });

        tab.IsEarlyAccess.Should().BeTrue();
        tab.IsPermanentlyPaid.Should().BeFalse();
        tab.ShowEarlyAccessBadge.Should().BeTrue();
    }

    [Fact]
    public void PermanentPaywall_ShowsPaidBadgeNotEa()
    {
        var tab = Tab(new CivitaiModelVersion
        {
            Id = 2,
            PaidAccess = new CivitaiPaidAccess { Permanent = true }
        });

        tab.IsEarlyAccess.Should().BeTrue("a permanent paywall is gated right now too");
        tab.IsPermanentlyPaid.Should().BeTrue();
        tab.ShowEarlyAccessBadge.Should().BeFalse("the stronger PAID badge wins, never both");
    }

    [Fact]
    public void FreeVersion_HasNoGatingFlags()
    {
        var tab = Tab(new CivitaiModelVersion { Id = 3 });

        tab.IsEarlyAccess.Should().BeFalse();
        tab.IsPermanentlyPaid.Should().BeFalse();
        tab.ShowEarlyAccessBadge.Should().BeFalse();
    }

    [Fact]
    public void ExpiredDeadline_ReadsAsFree()
    {
        var tab = Tab(new CivitaiModelVersion { Id = 4, EarlyAccessDeadline = Now.AddDays(-7) });

        tab.IsEarlyAccess.Should().BeFalse("a lapsed early-access window is a normal public download");
        tab.ShowEarlyAccessBadge.Should().BeFalse();
    }
}
