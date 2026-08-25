using DiffusionNexus.UI.ViewModels;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Tests.ViewModels;

/// <summary>
/// The shared sync wording and the two duration formats. Both dialogs and the viewer's status bar
/// read from here, so these are the assertions that keep the three surfaces saying one thing.
/// </summary>
public class SyncCopyTests
{
    /// <summary>
    /// The measured-duration format. No tilde — this is what a run cost, not what one was expected
    /// to cost — and a zero minor unit is dropped rather than padded.
    /// </summary>
    [Theory]
    [InlineData(0, "0 s")]
    [InlineData(-5, "0 s")]
    [InlineData(42, "42 s")]
    [InlineData(59, "59 s")]
    [InlineData(60, "1 min")]
    [InlineData(222, "3 min 42 s")]
    [InlineData(180, "3 min")]
    [InlineData(3600, "1 h")]
    [InlineData(3780, "1 h 3 min")]
    public void FormatElapsed_RendersTwoUnitsAtMostAndNoTilde(double seconds, string expected)
    {
        SyncCopy.FormatElapsed(TimeSpan.FromSeconds(seconds)).Should().Be(expected);
    }

    /// <summary>Past the hour mark the seconds are noise, not precision — they are dropped, not shown as 0.</summary>
    [Fact]
    public void FormatElapsed_DropsTheSecondsOnceThereAreHours()
    {
        SyncCopy.FormatElapsed(TimeSpan.FromSeconds(3642)).Should().Be("1 h");
    }

    /// <summary>Rounded to the whole second first, so a fraction cannot produce "0 min 60 s".</summary>
    [Fact]
    public void FormatElapsed_RoundsToTheNearestSecond()
    {
        SyncCopy.FormatElapsed(TimeSpan.FromSeconds(59.7)).Should().Be("1 min");
        SyncCopy.FormatElapsed(TimeSpan.FromSeconds(41.6)).Should().Be("42 s");
    }

    [Theory]
    [InlineData(0, "")]
    [InlineData(-1, "")]
    [InlineData(1, "1 new file discovered")]
    [InlineData(12, "12 new files discovered")]
    public void DescribeDiscovered_IsOnePhrasingForBothDialogs(int count, string expected)
    {
        SyncCopy.DescribeDiscovered(count).Should().Be(expected);
    }

    /// <summary>
    /// The verdict exists exactly once. The dialog and the status bar are on screen together, so
    /// two copies that merely happen to match today are a rewording away from contradicting.
    /// </summary>
    [Fact]
    public void UpToDate_IsTheOneVerdictBothSurfacesShow()
    {
        SyncCopy.UpToDate.Should().Be("Library is up to date — nothing to do");
        SyncPlanDialogViewModel.UpToDateMessage.Should().Be(SyncCopy.UpToDate,
            "the plan dialog reads the shared const rather than carrying its own copy");
    }
}
