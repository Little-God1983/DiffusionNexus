using DiffusionNexus.UI.Views.Dialogs;
using FluentAssertions;

namespace DiffusionNexus.Tests.Viewer;

/// <summary>
/// The pre-download dialog must never describe a permanently paid version as
/// time-limited: those are paywalled indefinitely and the waitlist cannot help.
/// It must also not offer to "add the rest" when the selection holds nothing else.
/// </summary>
public sealed class DownloadPreflightCopyTests
{
    [Fact]
    public void PermanentOnly_NeverClaimsTheGateIsTimeLimited()
    {
        var text = DownloadPreflightCopy.SelectionLead(1) + "permanently paid" + DownloadPreflightCopy.PermanentTail(1);

        text.Should().Be(
            "1 version in your selection is permanently paid — the creator has paywalled it on Civitai "
            + "indefinitely, with no end date. It will never become free, so the waitlist can't help: "
            + "manually buying and downloading it on Civitai is the only way.");
        text.Should().NotContain("limited time");
        text.Should().NotContain("Early Access");
    }

    [Fact]
    public void TemporaryTail_KeepsTheLimitedTimeExplanation()
    {
        DownloadPreflightCopy.TemporaryTail(1).Should().Contain("for a limited time")
            .And.Contain("it becomes free for everyone");
        DownloadPreflightCopy.TemporaryTail(3).Should().Contain("they become free for everyone");
    }

    [Fact]
    public void TemporaryTail_NeverImpliesBuyingAccessMakesTheAppDownloadWork()
    {
        // Civitai serves paid files through the website only — the app appends the API
        // key on a 401 retry and is still refused, so promising a download to account
        // holders is a promise the app cannot keep.
        foreach (var text in new[] { DownloadPreflightCopy.TemporaryTail(1), DownloadPreflightCopy.TemporaryTail(4) })
        {
            text.Should().Contain("cannot download").And.Contain("even with your API key set");
            text.Should().NotContain("unless your Civitai account");
        }
    }

    [Fact]
    public void InstalledTail_ExplainsTheRepeatDownload()
    {
        DownloadPreflightCopy.InstalledTail(1).Should().Contain("already in your library")
            .And.NotContain("paywalled");
        DownloadPreflightCopy.InstalledTail(2).Should().Contain("the files are already in your library");
    }

    [Fact]
    public void SelectionLead_AgreesWithTheCount()
    {
        DownloadPreflightCopy.SelectionLead(1).Should().Be("1 version in your selection is ");
        DownloadPreflightCopy.SelectionLead(2).Should().Be("2 versions in your selection are ");
    }

    [Theory]
    [InlineData(1, 0, 0, "Early Access model detected")]
    [InlineData(2, 0, 0, "Early Access models detected")]
    [InlineData(0, 1, 0, "Paywalled model detected")]
    [InlineData(0, 2, 0, "Paywalled models detected")]
    [InlineData(0, 0, 1, "Already installed")]
    [InlineData(0, 0, 2, "Already in your library")]
    [InlineData(1, 1, 0, "Check your selection")]
    [InlineData(1, 0, 1, "Check your selection")]
    public void Header_NamesOnlyTheKindsPresent(int temporary, int permanent, int installed, string expected)
    {
        DownloadPreflightCopy.Header(temporary, permanent, installed).Should().Be(expected);
    }

    [Theory]
    [InlineData(1, 0, 0, "Early Access models in selection")]
    [InlineData(0, 1, 0, "Paywalled models in selection")]
    [InlineData(0, 0, 1, "Already installed")]
    [InlineData(1, 1, 1, "Check your selection")]
    public void WindowTitle_NamesOnlyTheKindsPresent(int temporary, int permanent, int installed, string expected)
    {
        DownloadPreflightCopy.WindowTitle(temporary, permanent, installed).Should().Be(expected);
    }

    [Fact]
    public void SkipButtonText_MatchesWhatWouldBeSkipped()
    {
        DownloadPreflightCopy.SkipButtonText(1, 0, 0, otherCount: 2).Should().Be("Skip Early Access, add the rest");
        DownloadPreflightCopy.SkipButtonText(0, 1, 0, otherCount: 2).Should().Be("Skip paywalled, add the rest");
        DownloadPreflightCopy.SkipButtonText(0, 0, 1, otherCount: 2).Should().Be("Skip installed, add the rest");
        DownloadPreflightCopy.SkipButtonText(1, 0, 1, otherCount: 2).Should().Be("Skip flagged, add the rest");
    }

    [Fact]
    public void SkipButtonText_DropsTheRestPromiseWhenThereIsNoRest()
    {
        DownloadPreflightCopy.SkipButtonText(0, 1, 0, otherCount: 0).Should().Be("Skip paywalled");
        DownloadPreflightCopy.SkipButtonText(1, 0, 0, otherCount: 0).Should().Be("Skip Early Access");
    }

    [Fact]
    public void WaitlistTooltip_OnlyPromisesAnImmediateDownloadWhenSomethingElseIsSelected()
    {
        DownloadPreflightCopy.WaitlistButtonTooltip(0).Should().NotContain("the rest");
        DownloadPreflightCopy.WaitlistButtonTooltip(2).Should().Contain("the rest of the selection downloads now");
    }
}
