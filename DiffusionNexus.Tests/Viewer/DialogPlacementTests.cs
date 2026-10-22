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

    [Fact]
    public void MaxClientHeight_LeavesRoomForTheTitleBar()
    {
        DialogPlacement.MaxClientHeight(Work, scale: 1.25, chromeDip: 32).Should().BeApproximately(1290 / 1.25 - 32, 0.001);
    }
}
