using DiffusionNexus.UI.ImageEditor;
using FluentAssertions;
using SkiaSharp;

namespace DiffusionNexus.Tests.ImageEditor;

/// <summary>
/// The Move / Transform tool: Shape-style handles on the active layer's bounds, body drag moves,
/// corners and edges scale, the top handle rotates; the state is one canvas-space matrix that
/// is never applied to pixels until Commit.
/// </summary>
public class LayerTransformToolTests : IDisposable
{
    // Canvas 200x100 shown 1:1 at screen origin (Scale = 1) so canvas px == screen px.
    private readonly Layer _layer = new(40, 20, "L");
    private readonly LayerTransformTool _sut = new() { IsActive = true, ImagePixelWidth = 200, ImagePixelHeight = 100 };

    public LayerTransformToolTests()
    {
        _sut.SetImageBounds(new SKRect(0, 0, 200, 100));
        _layer.SetOffset(60, 40); // bounds (60,40)-(100,60), centre (80,50)
        _sut.Arm(_layer);
    }

    public void Dispose() => _layer.Dispose();

    [Fact]
    public void Arm_CapturesSourceBounds_AndIsIdentity()
    {
        _sut.IsArmed.Should().BeTrue();
        _sut.SourceBounds.Should().Be(new SKRectI(60, 40, 100, 60));
        _sut.HasTransform.Should().BeFalse();
        _sut.TransformedBounds.Should().Be(new SKRect(60, 40, 100, 60));
    }

    [Fact]
    public void HitTest_FindsCornersEdgesRotateAndBody_InOrder()
    {
        _sut.HitTest(new SKPoint(60, 40)).Should().Be(TransformHandle.TopLeft);
        _sut.HitTest(new SKPoint(80, 40)).Should().Be(TransformHandle.Top);
        _sut.HitTest(new SKPoint(100, 50)).Should().Be(TransformHandle.Right);
        _sut.HitTest(new SKPoint(100, 60)).Should().Be(TransformHandle.BottomRight);
        _sut.HitTest(new SKPoint(80, 40 - 30)).Should().Be(TransformHandle.Rotate);
        _sut.HitTest(new SKPoint(80, 50)).Should().Be(TransformHandle.Body);
        _sut.HitTest(new SKPoint(150, 90)).Should().Be(TransformHandle.None);
        _sut.HitTest(new SKPoint(60 - 11, 40)).Should().Be(TransformHandle.TopLeft);   // inside hit radius 12
        _sut.HitTest(new SKPoint(60 - 13, 40 - 13)).Should().Be(TransformHandle.None);
    }

    [Fact]
    public void BodyDrag_Translates_AndRaisesTransformChanged()
    {
        var changed = 0;
        _sut.TransformChanged += (_, _) => changed++;

        _sut.OnPointerPressed(new SKPoint(80, 50)).Should().BeTrue();
        _sut.OnPointerMoved(new SKPoint(90, 55)).Should().BeTrue();
        _sut.OnPointerReleased().Should().BeTrue();

        _sut.Translation.Should().Be(new SKPoint(10, 5));
        _sut.TransformedBounds.Should().Be(new SKRect(70, 45, 110, 65));
        _sut.HasTransform.Should().BeTrue();
        changed.Should().BeGreaterThan(0);
    }

    [Fact]
    public void CornerDrag_WithoutAspect_ScalesAboutOppositeCorner()
    {
        _sut.KeepAspect = false;
        _sut.OnPointerPressed(new SKPoint(100, 60)); // bottom-right
        _sut.OnPointerMoved(new SKPoint(120, 80));   // +20, +20 => 60x40
        _sut.OnPointerReleased();

        _sut.TransformedBounds.Left.Should().BeApproximately(60, 0.01f);
        _sut.TransformedBounds.Top.Should().BeApproximately(40, 0.01f);
        _sut.TransformedBounds.Width.Should().BeApproximately(60, 0.01f);
        _sut.TransformedBounds.Height.Should().BeApproximately(40, 0.01f);
    }

    [Fact]
    public void CornerDrag_WithAspect_UsesUniformFactor()
    {
        _sut.KeepAspect = true;
        _sut.OnPointerPressed(new SKPoint(100, 60));
        _sut.OnPointerMoved(new SKPoint(120, 60)); // width factor 1.5, height factor 1 => 1.5 both
        _sut.OnPointerReleased();

        _sut.TransformedBounds.Width.Should().BeApproximately(60, 0.01f);
        _sut.TransformedBounds.Height.Should().BeApproximately(30, 0.01f);
        _sut.TransformedBounds.Left.Should().BeApproximately(60, 0.01f);
        _sut.TransformedBounds.Top.Should().BeApproximately(40, 0.01f);
    }

    [Fact]
    public void CtrlOverride_InvertsKeepAspectForTheDrag()
    {
        _sut.KeepAspect = true;
        _sut.ConstrainProportionsOverride = true; // Ctrl held => free scaling
        _sut.OnPointerPressed(new SKPoint(100, 60));
        _sut.OnPointerMoved(new SKPoint(120, 60));
        _sut.OnPointerReleased();

        _sut.TransformedBounds.Width.Should().BeApproximately(60, 0.01f);
        _sut.TransformedBounds.Height.Should().BeApproximately(20, 0.01f);
    }

    [Fact]
    public void EdgeDrag_ScalesOneAxisAboutOppositeEdge()
    {
        _sut.OnPointerPressed(new SKPoint(100, 50)); // right edge
        _sut.OnPointerMoved(new SKPoint(140, 50));
        _sut.OnPointerReleased();

        _sut.TransformedBounds.Should().Be(new SKRect(60, 40, 140, 60));
    }

    [Fact]
    public void RotateDrag_SetsAngle_AndShiftSnapsToFifteen()
    {
        _sut.OnPointerPressed(new SKPoint(80, 10)); // rotate handle (centre 80,50 => angle -90°)
        _sut.OnPointerMoved(new SKPoint(120, 50)); // angle 0° => +90°
        _sut.OnPointerReleased();
        _sut.RotationDegrees.Should().BeApproximately(90f, 0.01f);

        _sut.Reset();
        _sut.SnapRotation = true;
        _sut.OnPointerPressed(new SKPoint(80, 10));
        _sut.OnPointerMoved(new SKPoint(80 + 40 * MathF.Cos(-1.1f), 50 + 40 * MathF.Sin(-1.1f))); // ~27°
        _sut.OnPointerReleased();
        _sut.RotationDegrees.Should().BeApproximately(30f, 0.01f);
    }

    [Fact]
    public void RotatedBox_HitTestsInLocalSpace()
    {
        _sut.SetRotation(90f); // 40x20 box becomes 20 wide, 40 tall around (80,50)
        _sut.HitTest(new SKPoint(80, 30)).Should().NotBe(TransformHandle.None); // top of rotated box
        _sut.HitTest(new SKPoint(80 + 30, 50)).Should().Be(TransformHandle.Rotate); // rotate handle swings to the right
    }

    [Fact]
    public void Flips_NegateScale_AndKeepCentre()
    {
        _sut.FlipHorizontal();
        _sut.ScaleX.Should().Be(-1f);
        _sut.TransformedBounds.Should().Be(new SKRect(60, 40, 100, 60));
        _sut.HasTransform.Should().BeTrue();
        _sut.FlipVertical();
        _sut.ScaleY.Should().Be(-1f);
    }

    [Fact]
    public void SetPositionSizeRotation_RoundTrip()
    {
        _sut.SetPosition(10, 20);
        _sut.TransformedBounds.Left.Should().BeApproximately(10, 0.01f);
        _sut.TransformedBounds.Top.Should().BeApproximately(20, 0.01f);

        _sut.KeepAspect = false;
        _sut.SetSize(80, 10);
        _sut.TransformedBounds.Width.Should().BeApproximately(80, 0.01f);
        _sut.TransformedBounds.Height.Should().BeApproximately(10, 0.01f);
        _sut.TransformedBounds.Left.Should().BeApproximately(10, 0.01f); // size keeps top-left

        _sut.KeepAspect = true;
        _sut.SetSize(40, 999); // width wins, height follows the source aspect (2:1)
        _sut.TransformedBounds.Height.Should().BeApproximately(20, 0.01f);

        _sut.SetRotation(45f);
        _sut.RotationDegrees.Should().Be(45f);
    }

    [Fact]
    public void Nudge_MovesByCanvasPixels()
    {
        _sut.Nudge(1, 0);
        _sut.Nudge(0, -10);
        _sut.Translation.Should().Be(new SKPoint(1, -10));
    }

    [Fact]
    public void HasTransform_IgnoresSubPixelNoise()
    {
        _sut.Nudge(0, 0);
        _sut.HasTransform.Should().BeFalse();
        _sut.SetRotation(0.0001f);
        _sut.HasTransform.Should().BeFalse();
    }

    [Fact]
    public void Commit_RaisesOnlyWithTransform_AndResetClearsState()
    {
        var commits = 0;
        _sut.CommitRequested += (_, _) => commits++;

        _sut.Commit().Should().BeFalse();
        commits.Should().Be(0);

        _sut.Nudge(5, 0);
        _sut.Commit().Should().BeTrue();
        commits.Should().Be(1);

        _sut.Nudge(5, 0);
        _sut.Reset();
        _sut.HasTransform.Should().BeFalse();
        _sut.IsArmed.Should().BeTrue(); // Reset keeps the layer armed
    }

    [Fact]
    public void DeactivatingWithPendingTransform_CommitsThenDisarms()
    {
        var commits = 0;
        _sut.CommitRequested += (_, _) => commits++;
        _sut.Nudge(3, 3);

        _sut.IsActive = false;

        commits.Should().Be(1);
        _sut.IsArmed.Should().BeFalse();
    }

    [Fact]
    public void Scale_MapsScreenToCanvas()
    {
        _sut.SetImageBounds(new SKRect(0, 0, 400, 200)); // 2 screen px per canvas px
        _sut.Scale.Should().Be(2f);
        _sut.OnPointerPressed(new SKPoint(160, 100)); // body (canvas 80,50)
        _sut.OnPointerMoved(new SKPoint(180, 100));
        _sut.OnPointerReleased();
        _sut.Translation.Should().Be(new SKPoint(10, 0));
    }

    [Fact]
    public void Render_DoesNotThrow_WhileArmedAndTransformed()
    {
        using var bmp = new SKBitmap(200, 100);
        using var canvas = new SKCanvas(bmp);
        _sut.Nudge(150, 0); // pushes part of the layer off canvas => dimmed preview path runs
        _sut.SetRotation(20f);
        var act = () => _sut.Render(canvas, new SKRect(0, 0, 200, 100));
        act.Should().NotThrow();
    }
}
