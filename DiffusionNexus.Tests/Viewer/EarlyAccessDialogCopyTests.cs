using DiffusionNexus.UI.Views.Dialogs;
using FluentAssertions;

namespace DiffusionNexus.Tests.Viewer;

/// <summary>
/// The early-access dialog must never describe a permanently paid version as
/// time-limited: those are paywalled indefinitely and the waitlist cannot help.
/// It must also not offer to "add the rest" when the selection holds nothing else.
/// </summary>
public sealed class EarlyAccessDialogCopyTests
{
    [Fact]
    public void PermanentOnly_NeverClaimsTheGateIsTimeLimited()
    {
        var text = EarlyAccessDialogCopy.SelectionLead(1) + "permanently paid" + EarlyAccessDialogCopy.PermanentTail(1);

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
        EarlyAccessDialogCopy.TemporaryTail(1).Should().Contain("for a limited time")
            .And.Contain("it becomes free for everyone");
        EarlyAccessDialogCopy.TemporaryTail(3).Should().Contain("they become free for everyone");
    }

    [Fact]
    public void SelectionLead_AgreesWithTheCount()
    {
        EarlyAccessDialogCopy.SelectionLead(1).Should().Be("1 version in your selection is ");
        EarlyAccessDialogCopy.SelectionLead(2).Should().Be("2 versions in your selection are ");
    }

    [Theory]
    [InlineData(1, 0, "Early Access model detected")]
    [InlineData(2, 0, "Early Access models detected")]
    [InlineData(0, 1, "Paywalled model detected")]
    [InlineData(0, 2, "Paywalled models detected")]
    [InlineData(1, 1, "Paid access detected")]
    public void Header_NamesOnlyTheKindsPresent(int temporary, int permanent, string expected)
    {
        EarlyAccessDialogCopy.Header(temporary, permanent).Should().Be(expected);
    }

    [Theory]
    [InlineData(1, 0, "Early Access models in selection")]
    [InlineData(0, 1, "Paywalled models in selection")]
    [InlineData(1, 1, "Paid models in selection")]
    public void WindowTitle_NamesOnlyTheKindsPresent(int temporary, int permanent, string expected)
    {
        EarlyAccessDialogCopy.WindowTitle(temporary, permanent).Should().Be(expected);
    }

    [Fact]
    public void SkipButtonText_MatchesWhatWouldBeSkipped()
    {
        EarlyAccessDialogCopy.SkipButtonText(1, 0).Should().Be("Skip Early Access, add the rest");
        EarlyAccessDialogCopy.SkipButtonText(0, 1).Should().Be("Skip paywalled, add the rest");
        EarlyAccessDialogCopy.SkipButtonText(1, 1).Should().Be("Skip paid items, add the rest");
    }

    [Fact]
    public void WaitlistTooltip_OnlyPromisesAnImmediateDownloadWhenSomethingElseIsSelected()
    {
        EarlyAccessDialogCopy.WaitlistButtonTooltip(0).Should().NotContain("the rest");
        EarlyAccessDialogCopy.WaitlistButtonTooltip(2).Should().Contain("the rest of the selection downloads now");
    }
}
