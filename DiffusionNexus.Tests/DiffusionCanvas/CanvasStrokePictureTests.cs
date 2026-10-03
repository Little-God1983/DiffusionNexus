using DiffusionNexus.UI.Views.Controls;
using FluentAssertions;
using SkiaSharp;

namespace DiffusionNexus.Tests.DiffusionCanvas;

/// <summary>
/// The mask overlay's recorded picture is released once the surface and every frame replaying it let go
/// (#595 review). Avalonia disposes a frame's custom draw operation when the frame is replaced.
/// </summary>
public class CanvasStrokePictureTests
{
    private static SKPicture Picture()
    {
        using var recorder = new SKPictureRecorder();
        var canvas = recorder.BeginRecording(new SKRect(0, 0, 10, 10));
        using var paint = new SKPaint { Color = SKColors.Red };
        canvas.DrawRect(0, 0, 5, 5, paint);
        return recorder.EndRecording();
    }

    [Fact]
    public void APictureAFrameStillHoldsIsNotReleased()
    {
        var shared = new DiffusionCanvasSurface.SharedPicture(Picture());
        var frame = shared.Acquire();

        shared.Release();               // the surface replaced it

        shared.IsReleased.Should().BeFalse("a frame is still replaying it");

        frame.Release();                // that frame was disposed

        shared.IsReleased.Should().BeTrue();
    }

    [Fact]
    public void APictureNoFrameHoldsIsReleasedWithTheSurfacesReference()
    {
        var shared = new DiffusionCanvasSurface.SharedPicture(Picture());

        shared.Release();

        shared.IsReleased.Should().BeTrue();
    }
}
