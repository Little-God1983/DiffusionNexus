using DiffusionNexus.UI.DiffusionCanvas;
using DiffusionNexus.UI.ViewModels.DiffusionCanvas;
using FluentAssertions;

namespace DiffusionNexus.Tests.DiffusionCanvas;

/// <summary>
/// Generate composites off the UI thread. The snapshot is what keeps a mid-run hide or opacity drag
/// from changing the input of a run that already started.
/// </summary>
public class CanvasRasterSnapshotTests
{
    [Fact]
    public void Of_CopiesEveryValueSoLaterChangesDoNotReachIt()
    {
        var frame = new GenerationFrameViewModel
        {
            CanvasX = 64, CanvasY = 128, Width = 512, Height = 256, ImagePath = "a.png", Opacity = 0.5,
        };

        var snapshot = CanvasRasterSnapshot.Of(frame);
        frame.IsVisible = false;
        frame.Opacity = 1;
        frame.CanvasX = 0;

        snapshot.Should().Be(new CanvasRasterSnapshot(64, 128, 512, 256, "a.png", true, 0.5));
        snapshot.FrameImage.Should().BeNull("a snapshot carries no Avalonia bitmap off the UI thread");
    }
}
