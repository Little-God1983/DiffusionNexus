using DiffusionNexus.UI.ViewModels.CivitaiBrowser;
using FluentAssertions;

namespace DiffusionNexus.Tests.Viewer;

/// <summary>
/// Covers the card-level paywall flags: "Paywalled" shows only for permanently
/// paid latest versions and suppresses the "Early Access" badge (never both).
/// </summary>
public sealed class CivitaiResultPaywalledBadgeTests
{
    private static readonly DateTimeOffset Future = DateTimeOffset.UtcNow.AddDays(7);

    [Fact]
    public void PermanentLatestVersion_ShowsPaywalledInsteadOfEarlyAccess()
    {
        var (result, _) = CivitaiWaitlistTests.Card(
            CivitaiWaitlistTests.Version(1, deadline: null, permanent: true));

        result.IsPermanentlyPaid.Should().BeTrue();
        result.IsEarlyAccess.Should().BeTrue("permanent paid access is an active gate");
        result.ShowEarlyAccessBadge.Should().BeFalse("Paywalled replaces the EA badge");
    }

    [Fact]
    public void TemporaryEaLatestVersion_ShowsEarlyAccessBadgeOnly()
    {
        var (result, _) = CivitaiWaitlistTests.Card(CivitaiWaitlistTests.Version(2, Future));

        result.IsPermanentlyPaid.Should().BeFalse();
        result.ShowEarlyAccessBadge.Should().BeTrue();
    }

    [Fact]
    public void FreeLatestVersion_ShowsNeitherBadge()
    {
        var (result, _) = CivitaiWaitlistTests.Card(CivitaiWaitlistTests.Version(3, deadline: null));

        result.IsPermanentlyPaid.Should().BeFalse();
        result.ShowEarlyAccessBadge.Should().BeFalse();
    }
}
