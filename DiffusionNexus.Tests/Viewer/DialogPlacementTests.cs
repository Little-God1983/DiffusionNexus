using Avalonia;
using DiffusionNexus.UI.Behaviors;
using FluentAssertions;

namespace DiffusionNexus.Tests.Viewer;

/// <summary>
/// Issue #630: an auto-height dialog that grows must stay inside the owner screen's working area
/// instead of growing off its bottom edge.
/// </summary>
public sealed class DialogPlacementTests
{
    // The case from the issue: a 2400×1350 px screen at 125 %, minus a 60 px taskbar.
    private static readonly PixelRect Work = new(-2400, 0, 2400, 1290);
    private static readonly PixelRect Owner = new(-2400, 0, 2400, 1290);

    [Fact]
    public void GrownDialog_IsRecentredOnTheOwner()
    {
        var pos = DialogPlacement.ComputePosition(Owner, new PixelSize(825, 900), Work, centreOnOwner: true,
            current: new PixelPoint(-1612, 500));

        pos.Should().Be(new PixelPoint(-2400 + (2400 - 825) / 2, (1290 - 900) / 2));
    }

    [Fact]
    public void DialogTallerThanTheScreen_KeepsItsTitleBarOnScreen()
    {
        var pos = DialogPlacement.ComputePosition(Owner, new PixelSize(825, 1500), Work, centreOnOwner: true,
            current: new PixelPoint(-1612, 300));

        pos.Y.Should().Be(0);
    }

    [Fact]
    public void DialogTheUserMoved_StaysPut_UnlessItsBottomLeavesTheScreen()
    {
        var stays = DialogPlacement.ComputePosition(Owner, new PixelSize(825, 400), Work, centreOnOwner: false,
            current: new PixelPoint(-2300, 100));
        stays.Should().Be(new PixelPoint(-2300, 100));

        var pulledUp = DialogPlacement.ComputePosition(Owner, new PixelSize(825, 900), Work, centreOnOwner: false,
            current: new PixelPoint(-2300, 700));
        pulledUp.Should().Be(new PixelPoint(-2300, 1290 - 900));
    }

    /// <summary>
    /// Review finding: the "user moved it" signal lasted one placement — after the clamp recorded the
    /// new spot, the next content change re-centred the dialog the user had dragged aside.
    /// </summary>
    [Fact]
    public void DialogTheUserMoved_IsNeverRecentredAgain()
    {
        var memory = new DialogPlacement.PlacementMemory();
        memory.ShouldCentre(new PixelPoint(100, 100)).Should().BeTrue("nothing placed yet");
        memory.Placed(new PixelPoint(100, 100));
        memory.ShouldCentre(new PixelPoint(100, 100)).Should().BeTrue("still where we put it");

        memory.ShouldCentre(new PixelPoint(300, 50)).Should().BeFalse("the user dragged it");
        memory.Placed(new PixelPoint(300, 50));

        memory.ShouldCentre(new PixelPoint(300, 50)).Should().BeFalse("a second expansion must not snap it back");
    }

    [Fact]
    public void MaxClientHeight_LeavesRoomForTheTitleBar()
    {
        DialogPlacement.MaxClientHeight(Work, scale: 1.25, chromeDip: 32).Should().BeApproximately(1290 / 1.25 - 32, 0.001);
    }
}
