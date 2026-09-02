# Canvas Extend Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an "Extend" tool to the Image Editor that grows the canvas around the image (transparent new area) by dragging handles, typing a size, or pressing 2×/3× buttons, with the view zooming out to keep the frame visible.

**Architecture:** The frame state and outward-only drag math are extracted from `OutpaintTool` into an abstract `CanvasExtensionTool`; `OutpaintTool` (arrows, AI severity) and the new `CanvasExtendTool` (crop-style round handles, checkerboard preview) subclass it. `ImageEditorCore` gains a fit rule that includes the active extension tool's frame and an `ApplyCanvasExtend()` that reuses the existing `ResizeLayerCanvas` path. A `CanvasExtendViewModel` + inline XAML panel mirror the existing Outpaint tool's MVVM wiring (ViewModel events → view code-behind → control → core).

**Tech Stack:** C# / .NET 10, Avalonia 11 (Fluent, dark), SkiaSharp, CommunityToolkit.Mvvm, xUnit + FluentAssertions.

**Spec:** `docs/superpowers/specs/2026-09-02-canvas-extend-design.md`

## Global Constraints

- Repo: `e:\Repos\DiffusionNexus`, branch `feature/canvas-extend` (already created from `develop`). Never commit to `develop` directly; the pre-push hook rejects it.
- Test command (run from the repo root): `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~<Name>"`; full run before the PR: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj`.
- Build the whole solution before every push: `dotnet build DiffusionNexus.sln -c Debug` (the UI project is not covered by tests; XAML errors only show at build).
- Outpaint behaviour must not change: handle centres 36 px outside the frame, hit radius 40, corner drags extend two edges, severity colours, label offset.
- Tool id string: `ToolIds.CanvasExtend = "CanvasExtend"`. Toolbar label: `Extend`. Panel title: `Extend Canvas`. Shrink hint text (verbatim): `The canvas can only grow here. To cut the image down, use the Crop tool.`
- Colours: frame/tint green `SKColor(76,175,80)`, amber `#FFC107`, handle fill white with `#505050` stroke, checker `#3B3B3B` / `#2B2B2B` at 16 px, panel chrome as the Crop panel (`#2A2A2A`, `#444`, `#4CAF50`, apply `#2D7D46`).
- Fit margins: `CanvasExtendTool.FitMargin = 32`, `OutpaintTool.FitMargin = 72`.
- Every new feature logs its steps to the Unified Console (`IUnifiedLogger`, `LogCategory.Configuration`, source `"CanvasExtend"`), same `EmitInfo` pattern as `OutpaintingViewModel`.
- Keyboard shortcuts must be documented in `DiffusionNexus.UI/Doc/Shortcuts.md`. Check `DiffusionNexus.UI/REUSABLES.md` before adding UI; this feature adds no reusable control, so no row is added.
- Commit message trailer on every commit: `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`.

---

## File map

| File | Responsibility |
|---|---|
| `DiffusionNexus.UI/ImageEditor/CanvasExtensionTool.cs` (new) | Abstract base: extension state, outward-only drag math, `SetTargetSize`, `SetAspectRatio`, `ShrinkAttempted`, hit test through subclass handle centres. |
| `DiffusionNexus.UI/ImageEditor/OutpaintTool.cs` (modify) | Becomes `OutpaintTool : CanvasExtensionTool`; keeps arrows, severity, its render code. `OutpaintHandle` / `OutpaintSeverity` enums stay here. |
| `DiffusionNexus.UI/ImageEditor/CanvasExtendTool.cs` (new) | Round handles on the frame, checkerboard + tint preview, dashed frame, label. |
| `DiffusionNexus.UI/ImageEditor/Services/ToolIds.cs` (modify) | `CanvasExtend` id. |
| `DiffusionNexus.UI/ImageEditor/ImageEditorCore.cs` (modify) | `CanvasExtendTool` instance, fit-with-extension rule, `ApplyCanvasExtend()`. |
| `DiffusionNexus.UI/ViewModels/CanvasExtendViewModel.cs` (new) | Panel state, commands, events to the view. |
| `DiffusionNexus.UI/ViewModels/ImageEditorViewModel.cs` (modify) | Owns `CanvasExtend`, wires it into tool coordination. |
| `DiffusionNexus.UI/Controls/ImageEditorControl.cs` (modify) | Active flag, pointer/cursor/key routing, events, `ApplyCanvasExtend()`. |
| `DiffusionNexus.UI/Views/Tabs/ImageEditView.axaml` (modify) | Toolbar toggle + panel. |
| `DiffusionNexus.UI/Views/Tabs/ImageEditView.axaml.cs` (modify) | `WireCanvasExtendEvents`. |
| `DiffusionNexus.UI/Doc/Shortcuts.md`, `DiffusionNexus.UI/ImageEditor/ARCHITECTURE.md` (modify) | Docs. |
| `DiffusionNexus.Tests/ImageEditor/OutpaintToolRegressionTests.cs`, `CanvasExtendToolTests.cs`, `ViewportFitTests.cs`, `ImageEditorCoreCanvasExtendTests.cs` (new); `DiffusionNexus.Tests/ViewModels/CanvasExtendViewModelTests.cs` (new); `DiffusionNexus.Tests/ImageEditor/Services/ToolManagerTests.cs` (modify) | Tests. |

---

### Task 1: Extract `CanvasExtensionTool` from `OutpaintTool`

**Files:**
- Create: `DiffusionNexus.UI/ImageEditor/CanvasExtensionTool.cs`
- Modify: `DiffusionNexus.UI/ImageEditor/OutpaintTool.cs` (whole file, currently 669 lines)
- Test: `DiffusionNexus.Tests/ImageEditor/OutpaintToolRegressionTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: `abstract class CanvasExtensionTool` with `bool IsActive`, `int ImagePixelWidth/Height`, `int ExtendTop/Right/Bottom/Left`, `bool HasExtension`, `bool IsDragging`, `bool IsShrinkBlocked`, `abstract float FitMargin`, `(int Width, int Height) GetNewDimensions()`, `void SetImageBounds(SKRect)`, `void Reset()`, `void SetExtension(int top, int right, int bottom, int left)`, `void SetAspectRatio(float ratioW, float ratioH)`, `void SetTargetSize(int width, int height)`, `bool OnPointerPressed(SKPoint)`, `bool OnPointerMoved(SKPoint)`, `bool OnPointerReleased()`, `OutpaintHandle GetCursorForPoint(SKPoint)`, `abstract void Render(SKCanvas, SKRect)`, events `RegionChanged`, `ShrinkAttempted`; protected `SKRect ImageRect`, `OutpaintHandle ActiveHandle`, `SKRect GetExtendedScreenRect()`, `abstract float HandleHitRadius`, `abstract SKPoint GetHandleCenter(OutpaintHandle)`.

- [ ] **Step 1: Write the regression tests against the CURRENT `OutpaintTool` (they must pass before the refactor)**

```csharp
// DiffusionNexus.Tests/ImageEditor/OutpaintToolRegressionTests.cs
using DiffusionNexus.UI.ImageEditor;
using FluentAssertions;
using SkiaSharp;

namespace DiffusionNexus.Tests.ImageEditor;

/// <summary>
/// Pins the Outpaint tool's observable behaviour across the extraction of the
/// CanvasExtensionTool base class: arrow handles 36 px outside the frame with a
/// 40 px hit radius, corner drags extending two edges, outward-only extension.
/// </summary>
public class OutpaintToolRegressionTests
{
    private const int Size = 1000; // 1000x1000 image rendered at 100% => screen px == image px

    private static OutpaintTool CreateActive()
    {
        var tool = new OutpaintTool { IsActive = true, ImagePixelWidth = Size, ImagePixelHeight = Size };
        tool.SetImageBounds(new SKRect(0, 0, Size, Size));
        return tool;
    }

    [Fact]
    public void RightArrow_SitsThirtySixPixelsOutsideFrame_WithFortyPixelHitRadius()
    {
        var tool = CreateActive();

        // Centre of the right arrow: (Size + 36, Size/2). 39 px away still hits, 41 px misses.
        tool.GetCursorForPoint(new SKPoint(Size + 36, Size / 2f)).Should().Be(OutpaintHandle.Right);
        tool.GetCursorForPoint(new SKPoint(Size + 36 + 39, Size / 2f)).Should().Be(OutpaintHandle.Right);
        tool.GetCursorForPoint(new SKPoint(Size + 36 + 41, Size / 2f)).Should().Be(OutpaintHandle.None);
    }

    [Fact]
    public void TopLeftArrow_SitsDiagonallyOutsideFrame()
    {
        var tool = CreateActive();

        tool.GetCursorForPoint(new SKPoint(-36, -36)).Should().Be(OutpaintHandle.TopLeft);
    }

    [Fact]
    public void DraggingCornerOutward_ExtendsTwoEdges()
    {
        var tool = CreateActive();

        tool.OnPointerPressed(new SKPoint(Size + 36, Size + 36)).Should().BeTrue(); // bottom-right arrow
        tool.OnPointerMoved(new SKPoint(Size + 36 + 100, Size + 36 + 50));
        tool.OnPointerReleased();

        tool.ExtendRight.Should().Be(100);
        tool.ExtendBottom.Should().Be(50);
        tool.ExtendLeft.Should().Be(0);
        tool.ExtendTop.Should().Be(0);
        tool.GetNewDimensions().Should().Be((Size + 100, Size + 50));
    }

    [Fact]
    public void DraggingEdgeInward_ClampsAtZero()
    {
        var tool = CreateActive();

        tool.OnPointerPressed(new SKPoint(Size + 36, Size / 2f));
        tool.OnPointerMoved(new SKPoint(Size + 36 - 300, Size / 2f));
        tool.OnPointerReleased();

        tool.ExtendRight.Should().Be(0);
        tool.HasExtension.Should().BeFalse();
    }

    [Fact]
    public void SetAspectRatio_ExtendsSymmetrically_NeverShrinks()
    {
        var tool = CreateActive();

        tool.SetAspectRatio(2, 1); // 1000x1000 -> 2000x1000

        tool.ExtendLeft.Should().Be(500);
        tool.ExtendRight.Should().Be(500);
        tool.ExtendTop.Should().Be(0);
        tool.ExtendBottom.Should().Be(0);
    }

    [Fact]
    public void Deactivating_ResetsExtension()
    {
        var tool = CreateActive();
        tool.SetExtension(10, 20, 30, 40);

        tool.IsActive = false;

        tool.HasExtension.Should().BeFalse();
    }
}
```

- [ ] **Step 2: Run the regression tests against the unchanged tool**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~OutpaintToolRegressionTests"`
Expected: all 6 PASS (this proves the tests describe today's behaviour; the refactor must keep them green).

- [ ] **Step 3: Add the base-feature tests that fail today (`FitMargin`, `SetTargetSize`, `ShrinkAttempted`, `IsShrinkBlocked`)**

Append to `OutpaintToolRegressionTests.cs` inside the class:

```csharp
    [Fact]
    public void FitMargin_IsSeventyTwo()
    {
        CreateActive().FitMargin.Should().Be(72f);
    }

    [Fact]
    public void SetTargetSize_SplitsExtensionSymmetrically_OddPixelGoesRightAndBottom()
    {
        var tool = CreateActive();

        tool.SetTargetSize(2049, 1001);

        tool.ExtendLeft.Should().Be(524);
        tool.ExtendRight.Should().Be(525);
        tool.ExtendTop.Should().Be(0);
        tool.ExtendBottom.Should().Be(1);
    }

    [Fact]
    public void SetTargetSize_BelowImage_ClampsAndRaisesShrinkAttempted()
    {
        var tool = CreateActive();
        var shrinkRaised = 0;
        tool.ShrinkAttempted += (_, _) => shrinkRaised++;

        tool.SetTargetSize(800, 1200);

        tool.ExtendLeft.Should().Be(0);
        tool.ExtendRight.Should().Be(0);
        tool.ExtendTop.Should().Be(100);
        tool.ExtendBottom.Should().Be(100);
        shrinkRaised.Should().Be(1);
    }

    [Fact]
    public void InwardDrag_RaisesShrinkAttemptedOncePerGesture_AndFlagsBlocked()
    {
        var tool = CreateActive();
        var shrinkRaised = 0;
        tool.ShrinkAttempted += (_, _) => shrinkRaised++;

        tool.OnPointerPressed(new SKPoint(Size + 36, Size / 2f));
        tool.OnPointerMoved(new SKPoint(Size + 36 - 10, Size / 2f));
        tool.IsShrinkBlocked.Should().BeTrue();
        tool.OnPointerMoved(new SKPoint(Size + 36 - 20, Size / 2f));
        tool.OnPointerMoved(new SKPoint(Size + 36 + 20, Size / 2f)); // back outward
        tool.IsShrinkBlocked.Should().BeFalse();
        tool.OnPointerReleased();

        shrinkRaised.Should().Be(1);
        tool.ExtendRight.Should().Be(20);
        tool.IsShrinkBlocked.Should().BeFalse();
    }
```

- [ ] **Step 4: Run the new tests to verify they fail**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~OutpaintToolRegressionTests"`
Expected: build FAILS with `'OutpaintTool' does not contain a definition for 'FitMargin'` (and `SetTargetSize`, `ShrinkAttempted`, `IsShrinkBlocked`).

- [ ] **Step 5: Create the base class**

```csharp
// DiffusionNexus.UI/ImageEditor/CanvasExtensionTool.cs
using SkiaSharp;

namespace DiffusionNexus.UI.ImageEditor;

/// <summary>
/// Shared state and drag math for tools that grow the canvas outward from the image:
/// per-edge pixel extension, outward-only handle dragging, aspect-ratio and target-size
/// presets. Subclasses decide where the handles sit (<see cref="GetHandleCenter"/>),
/// how big their hit zone is, how the frame is drawn, and how much room the viewport
/// must reserve around the frame (<see cref="FitMargin"/>).
/// </summary>
public abstract class CanvasExtensionTool
{
    private static readonly OutpaintHandle[] HitTestOrder =
    [
        // Corners first: they are the "more specific" choice when a click lands in the diagonal area.
        OutpaintHandle.TopLeft, OutpaintHandle.TopRight, OutpaintHandle.BottomLeft, OutpaintHandle.BottomRight,
        OutpaintHandle.Top, OutpaintHandle.Bottom, OutpaintHandle.Left, OutpaintHandle.Right
    ];

    private SKRect _imageRect;
    private OutpaintHandle _activeHandle = OutpaintHandle.None;
    private SKPoint _dragStartPoint;

    // Extension stored in image pixels (how many pixels to add on each side)
    private int _extendTop;
    private int _extendRight;
    private int _extendBottom;
    private int _extendLeft;

    // Drag start state
    private int _dragStartExtendTop;
    private int _dragStartExtendRight;
    private int _dragStartExtendBottom;
    private int _dragStartExtendLeft;

    private bool _isActive;
    private bool _shrinkRaisedThisGesture;
    private bool _isShrinkBlocked;

    /// <summary>Gets or sets whether the tool is active. Deactivating resets the extension.</summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            _isActive = value;
            if (!value)
            {
                _activeHandle = OutpaintHandle.None;
                Reset();
            }
        }
    }

    /// <summary>The original image width in pixels.</summary>
    public int ImagePixelWidth { get; set; }

    /// <summary>The original image height in pixels.</summary>
    public int ImagePixelHeight { get; set; }

    /// <summary>Pixel extension for the top edge.</summary>
    public int ExtendTop => _extendTop;

    /// <summary>Pixel extension for the right edge.</summary>
    public int ExtendRight => _extendRight;

    /// <summary>Pixel extension for the bottom edge.</summary>
    public int ExtendBottom => _extendBottom;

    /// <summary>Pixel extension for the left edge.</summary>
    public int ExtendLeft => _extendLeft;

    /// <summary>Whether any extension has been applied.</summary>
    public bool HasExtension => _extendTop > 0 || _extendRight > 0 || _extendBottom > 0 || _extendLeft > 0;

    /// <summary>Whether a handle is currently being dragged.</summary>
    public bool IsDragging => _activeHandle != OutpaintHandle.None;

    /// <summary>
    /// True while the last pointer move of the current gesture tried to pull a handle past the
    /// image edge and was clamped. False once the pointer is released or moves outward again.
    /// </summary>
    public bool IsShrinkBlocked => _isShrinkBlocked;

    /// <summary>
    /// Screen pixels the viewport reserves on each side of the extended frame so the
    /// handles and the size label stay visible while the tool is active.
    /// </summary>
    public abstract float FitMargin { get; }

    /// <summary>Hit radius in screen pixels around each handle centre.</summary>
    protected abstract float HandleHitRadius { get; }

    /// <summary>The image rectangle in screen coordinates, as last set by <see cref="SetImageBounds"/>.</summary>
    protected SKRect ImageRect => _imageRect;

    /// <summary>The handle being dragged, or <see cref="OutpaintHandle.None"/>.</summary>
    protected OutpaintHandle ActiveHandle => _activeHandle;

    /// <summary>Raised when the extension amounts change.</summary>
    public event EventHandler? RegionChanged;

    /// <summary>
    /// Raised when the user tries to make the canvas smaller than the image: an inward
    /// handle drag (once per gesture) or a target size below the image size (per call).
    /// </summary>
    public event EventHandler? ShrinkAttempted;

    /// <summary>Screen-space centre of the given handle.</summary>
    protected abstract SKPoint GetHandleCenter(OutpaintHandle handle);

    /// <summary>Renders the tool's overlay.</summary>
    public abstract void Render(SKCanvas canvas, SKRect canvasBounds);

    /// <summary>Gets the new total resolution including extensions.</summary>
    public (int Width, int Height) GetNewDimensions()
    {
        if (ImagePixelWidth <= 0 || ImagePixelHeight <= 0)
            return (0, 0);

        return (ImagePixelWidth + _extendLeft + _extendRight,
                ImagePixelHeight + _extendTop + _extendBottom);
    }

    /// <summary>Sets the image bounds (screen coordinates) used for rendering and drag scaling.</summary>
    public void SetImageBounds(SKRect imageRect)
    {
        _imageRect = imageRect;
    }

    /// <summary>Resets all extensions to zero.</summary>
    public void Reset()
    {
        _extendTop = 0;
        _extendRight = 0;
        _extendBottom = 0;
        _extendLeft = 0;
        _activeHandle = OutpaintHandle.None;
        _isShrinkBlocked = false;
        RegionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Sets the extension amounts for each edge in image pixels. Negative values clamp to zero.</summary>
    public void SetExtension(int top, int right, int bottom, int left)
    {
        _extendTop = Math.Max(0, top);
        _extendRight = Math.Max(0, right);
        _extendBottom = Math.Max(0, bottom);
        _extendLeft = Math.Max(0, left);
        RegionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Sets extension to match a target aspect ratio, expanding symmetrically.
    /// The image is never made smaller, only extended on the necessary sides.
    /// </summary>
    public void SetAspectRatio(float ratioW, float ratioH)
    {
        if (ratioW <= 0 || ratioH <= 0 || ImagePixelWidth <= 0 || ImagePixelHeight <= 0)
            return;

        var currentW = ImagePixelWidth;
        var currentH = ImagePixelHeight;
        var targetRatio = ratioW / ratioH;
        var currentRatio = (float)currentW / currentH;

        int newW, newH;
        if (targetRatio > currentRatio)
        {
            newW = (int)Math.Round(currentH * targetRatio);
            newH = currentH;
        }
        else
        {
            newW = currentW;
            newH = (int)Math.Round(currentW / targetRatio);
        }

        ApplySymmetricTarget(newW, newH);
        RegionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Sets the total canvas size. Extra pixels are split evenly between left/right and
    /// top/bottom (the odd pixel goes right / bottom). A dimension below the image size is
    /// clamped to the image size and <see cref="ShrinkAttempted"/> is raised.
    /// </summary>
    public void SetTargetSize(int width, int height)
    {
        if (ImagePixelWidth <= 0 || ImagePixelHeight <= 0)
            return;

        var shrinkRequested = width < ImagePixelWidth || height < ImagePixelHeight;
        ApplySymmetricTarget(width, height);

        if (shrinkRequested)
            ShrinkAttempted?.Invoke(this, EventArgs.Empty);
        RegionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ApplySymmetricTarget(int width, int height)
    {
        var totalExtendX = Math.Max(0, width - ImagePixelWidth);
        var totalExtendY = Math.Max(0, height - ImagePixelHeight);

        _extendLeft = totalExtendX / 2;
        _extendRight = totalExtendX - _extendLeft;
        _extendTop = totalExtendY / 2;
        _extendBottom = totalExtendY - _extendTop;
    }

    /// <summary>Handles pointer pressed. Returns true when a handle was grabbed.</summary>
    public bool OnPointerPressed(SKPoint point)
    {
        if (!_isActive) return false;

        _activeHandle = HitTestHandle(point);
        if (_activeHandle == OutpaintHandle.None)
            return false;

        _dragStartPoint = point;
        _dragStartExtendTop = _extendTop;
        _dragStartExtendRight = _extendRight;
        _dragStartExtendBottom = _extendBottom;
        _dragStartExtendLeft = _extendLeft;
        _shrinkRaisedThisGesture = false;
        _isShrinkBlocked = false;
        return true;
    }

    /// <summary>Handles pointer moved. Returns true when a drag is in progress.</summary>
    public bool OnPointerMoved(SKPoint point)
    {
        if (!_isActive || _activeHandle == OutpaintHandle.None) return false;

        var deltaX = point.X - _dragStartPoint.X;
        var deltaY = point.Y - _dragStartPoint.Y;

        // Convert screen delta to pixel delta based on image-to-screen scale
        var scaleX = _imageRect.Width > 0 ? ImagePixelWidth / _imageRect.Width : 1f;
        var scaleY = _imageRect.Height > 0 ? ImagePixelHeight / _imageRect.Height : 1f;

        // Corner handles extend two adjacent edges simultaneously from one drag.
        var extendTopDelta = -(int)(deltaY * scaleY);
        var extendBottomDelta = (int)(deltaY * scaleY);
        var extendLeftDelta = -(int)(deltaX * scaleX);
        var extendRightDelta = (int)(deltaX * scaleX);

        var clamped = false;
        int ClampToZero(int requested)
        {
            if (requested >= 0) return requested;
            clamped = true;
            return 0;
        }

        switch (_activeHandle)
        {
            case OutpaintHandle.Top:
                _extendTop = ClampToZero(_dragStartExtendTop + extendTopDelta);
                break;
            case OutpaintHandle.Bottom:
                _extendBottom = ClampToZero(_dragStartExtendBottom + extendBottomDelta);
                break;
            case OutpaintHandle.Left:
                _extendLeft = ClampToZero(_dragStartExtendLeft + extendLeftDelta);
                break;
            case OutpaintHandle.Right:
                _extendRight = ClampToZero(_dragStartExtendRight + extendRightDelta);
                break;
            case OutpaintHandle.TopLeft:
                _extendTop = ClampToZero(_dragStartExtendTop + extendTopDelta);
                _extendLeft = ClampToZero(_dragStartExtendLeft + extendLeftDelta);
                break;
            case OutpaintHandle.TopRight:
                _extendTop = ClampToZero(_dragStartExtendTop + extendTopDelta);
                _extendRight = ClampToZero(_dragStartExtendRight + extendRightDelta);
                break;
            case OutpaintHandle.BottomLeft:
                _extendBottom = ClampToZero(_dragStartExtendBottom + extendBottomDelta);
                _extendLeft = ClampToZero(_dragStartExtendLeft + extendLeftDelta);
                break;
            case OutpaintHandle.BottomRight:
                _extendBottom = ClampToZero(_dragStartExtendBottom + extendBottomDelta);
                _extendRight = ClampToZero(_dragStartExtendRight + extendRightDelta);
                break;
        }

        _isShrinkBlocked = clamped;
        if (clamped && !_shrinkRaisedThisGesture)
        {
            _shrinkRaisedThisGesture = true;
            ShrinkAttempted?.Invoke(this, EventArgs.Empty);
        }

        RegionChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Handles pointer released.</summary>
    public bool OnPointerReleased()
    {
        if (!_isActive) return false;
        _activeHandle = OutpaintHandle.None;
        _isShrinkBlocked = false;
        return true;
    }

    /// <summary>Gets the handle under the point, for cursor selection.</summary>
    public OutpaintHandle GetCursorForPoint(SKPoint point)
    {
        if (!_isActive) return OutpaintHandle.None;
        return HitTestHandle(point);
    }

    /// <summary>The extended frame in screen coordinates.</summary>
    protected SKRect GetExtendedScreenRect()
    {
        if (ImagePixelWidth <= 0 || ImagePixelHeight <= 0)
            return _imageRect;

        var pixelsPerScreenX = _imageRect.Width / ImagePixelWidth;
        var pixelsPerScreenY = _imageRect.Height / ImagePixelHeight;

        return new SKRect(
            _imageRect.Left - _extendLeft * pixelsPerScreenX,
            _imageRect.Top - _extendTop * pixelsPerScreenY,
            _imageRect.Right + _extendRight * pixelsPerScreenX,
            _imageRect.Bottom + _extendBottom * pixelsPerScreenY);
    }

    private OutpaintHandle HitTestHandle(SKPoint point)
    {
        var hitRadius = HandleHitRadius;
        foreach (var handle in HitTestOrder)
        {
            var center = GetHandleCenter(handle);
            var dx = point.X - center.X;
            var dy = point.Y - center.Y;
            if (dx * dx + dy * dy <= hitRadius * hitRadius)
                return handle;
        }
        return OutpaintHandle.None;
    }
}
```

- [ ] **Step 6: Rewrite `OutpaintTool.cs` as a subclass**

Replace the whole file with the following. The two enums stay at the top; the render code is the existing code with `_imageRect` → `ImageRect`, `_activeHandle` → `ActiveHandle`, `_extendX` → `ExtendX`.

```csharp
// DiffusionNexus.UI/ImageEditor/OutpaintTool.cs
using SkiaSharp;

namespace DiffusionNexus.UI.ImageEditor;

/// <summary>
/// Represents which directional handle is being dragged during an outward canvas resize.
/// Shared by <see cref="OutpaintTool"/> and <see cref="CanvasExtendTool"/>.
/// </summary>
public enum OutpaintHandle
{
    None,
    Top,
    Right,
    Bottom,
    Left,
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight
}

/// <summary>
/// How aggressive the current outpaint extension is, relative to the source image area.
/// Drives the canvas accent color and the panel warning.
/// </summary>
public enum OutpaintSeverity
{
    None,
    Caution,
    Strong
}

/// <summary>
/// Outpainting tool: extends the canvas beyond the original image so an AI workflow can
/// fill the new area. Renders directional arrow handles outside each edge and corner and
/// tints the frame by <see cref="Severity"/>. All extension state and drag math live in
/// <see cref="CanvasExtensionTool"/>.
/// </summary>
public class OutpaintTool : CanvasExtensionTool
{
    private const float ArrowSize = 32f;
    private const float ArrowHitSize = 40f;
    private const float HandleGap = 4f;

    /// <inheritdoc />
    public override float FitMargin => 72f;

    /// <inheritdoc />
    protected override float HandleHitRadius => ArrowHitSize;

    /// <summary>
    /// Area of the extended canvas divided by the area of the original image. Returns 1.0 when
    /// no extension is present or the source dimensions are unknown.
    /// </summary>
    public float AreaRatio
    {
        get
        {
            if (ImagePixelWidth <= 0 || ImagePixelHeight <= 0) return 1f;
            var (newW, newH) = GetNewDimensions();
            var orig = (long)ImagePixelWidth * ImagePixelHeight;
            if (orig <= 0) return 1f;
            return (float)((long)newW * newH) / orig;
        }
    }

    /// <summary>
    /// Severity tier based on <see cref="AreaRatio"/>: ≥2.00 → Strong, ≥1.50 → Caution, otherwise None.
    /// </summary>
    public OutpaintSeverity Severity
    {
        get
        {
            var ratio = AreaRatio;
            if (ratio >= 2.00f) return OutpaintSeverity.Strong;
            if (ratio >= 1.50f) return OutpaintSeverity.Caution;
            return OutpaintSeverity.None;
        }
    }

    /// <summary>
    /// Handle positions are anchored to the extended rect so they ride along with the
    /// outpaint frame as the user drags. Edge handles are centered on each side;
    /// corner handles sit diagonally outside the rect corners.
    /// </summary>
    protected override SKPoint GetHandleCenter(OutpaintHandle handle)
    {
        var rect = GetExtendedScreenRect();
        var outX = ArrowSize + HandleGap;
        var outY = ArrowSize + HandleGap;

        return handle switch
        {
            OutpaintHandle.Top => new SKPoint(rect.MidX, rect.Top - outY),
            OutpaintHandle.Right => new SKPoint(rect.Right + outX, rect.MidY),
            OutpaintHandle.Bottom => new SKPoint(rect.MidX, rect.Bottom + outY),
            OutpaintHandle.Left => new SKPoint(rect.Left - outX, rect.MidY),
            OutpaintHandle.TopLeft => new SKPoint(rect.Left - outX, rect.Top - outY),
            OutpaintHandle.TopRight => new SKPoint(rect.Right + outX, rect.Top - outY),
            OutpaintHandle.BottomLeft => new SKPoint(rect.Left - outX, rect.Bottom + outY),
            OutpaintHandle.BottomRight => new SKPoint(rect.Right + outX, rect.Bottom + outY),
            _ => SKPoint.Empty
        };
    }

    /// <summary>
    /// Renders the outpaint overlay with extension region and arrow handles at the image edges.
    /// </summary>
    public override void Render(SKCanvas canvas, SKRect canvasBounds)
    {
        if (!IsActive || ImageRect.Width <= 0 || ImageRect.Height <= 0) return;

        var extendedRect = GetExtendedScreenRect();

        if (HasExtension)
        {
            DrawExtensionRegion(canvas, extendedRect);
        }

        DrawArrowHandles(canvas);

        if (HasExtension)
        {
            DrawResolutionLabel(canvas, extendedRect);
        }
    }

    /// <summary>
    /// Base accent color (RGB only, alpha is applied at the call site) that reflects the
    /// current severity tier: green / amber / red‑orange.
    /// </summary>
    private SKColor GetAccentBaseColor() => Severity switch
    {
        OutpaintSeverity.Strong => new SKColor(255, 87, 34),    // red‑orange
        OutpaintSeverity.Caution => new SKColor(255, 193, 7),   // amber
        _ => new SKColor(76, 175, 80),                          // green
    };

    private void DrawExtensionRegion(SKCanvas canvas, SKRect extendedRect)
    {
        var accent = GetAccentBaseColor();
        var imageRect = ImageRect;

        using var borderPaint = new SKPaint
        {
            Color = accent.WithAlpha(200),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2f,
            IsAntialias = true,
            PathEffect = SKPathEffect.CreateDash([8f, 4f], 0f)
        };
        canvas.DrawRect(extendedRect, borderPaint);

        using var fillPaint = new SKPaint
        {
            Color = accent.WithAlpha(40),
            Style = SKPaintStyle.Fill
        };

        if (ExtendTop > 0)
            canvas.DrawRect(new SKRect(extendedRect.Left, extendedRect.Top, extendedRect.Right, imageRect.Top), fillPaint);
        if (ExtendBottom > 0)
            canvas.DrawRect(new SKRect(extendedRect.Left, imageRect.Bottom, extendedRect.Right, extendedRect.Bottom), fillPaint);
        if (ExtendLeft > 0)
            canvas.DrawRect(new SKRect(extendedRect.Left, imageRect.Top, imageRect.Left, imageRect.Bottom), fillPaint);
        if (ExtendRight > 0)
            canvas.DrawRect(new SKRect(imageRect.Right, imageRect.Top, extendedRect.Right, imageRect.Bottom), fillPaint);

        using var imageBorderPaint = new SKPaint
        {
            Color = new SKColor(255, 255, 255, 100),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1f,
            IsAntialias = true
        };
        canvas.DrawRect(imageRect, imageBorderPaint);
    }

    private void DrawArrowHandles(SKCanvas canvas)
    {
        DrawArrow(canvas, GetHandleCenter(OutpaintHandle.Top), Direction.Up, ActiveHandle == OutpaintHandle.Top);
        DrawArrow(canvas, GetHandleCenter(OutpaintHandle.Bottom), Direction.Down, ActiveHandle == OutpaintHandle.Bottom);
        DrawArrow(canvas, GetHandleCenter(OutpaintHandle.Left), Direction.Left, ActiveHandle == OutpaintHandle.Left);
        DrawArrow(canvas, GetHandleCenter(OutpaintHandle.Right), Direction.Right, ActiveHandle == OutpaintHandle.Right);
        DrawArrow(canvas, GetHandleCenter(OutpaintHandle.TopLeft), Direction.UpLeft, ActiveHandle == OutpaintHandle.TopLeft);
        DrawArrow(canvas, GetHandleCenter(OutpaintHandle.TopRight), Direction.UpRight, ActiveHandle == OutpaintHandle.TopRight);
        DrawArrow(canvas, GetHandleCenter(OutpaintHandle.BottomLeft), Direction.DownLeft, ActiveHandle == OutpaintHandle.BottomLeft);
        DrawArrow(canvas, GetHandleCenter(OutpaintHandle.BottomRight), Direction.DownRight, ActiveHandle == OutpaintHandle.BottomRight);
    }

    private static void DrawArrow(SKCanvas canvas, SKPoint center, Direction direction, bool isActive)
    {
        // >>> KEEP THE EXISTING BODY OF DrawArrow UNCHANGED (circle background, stroke, arrow path per Direction). <<<
    }

    private void DrawResolutionLabel(SKCanvas canvas, SKRect extendedRect)
    {
        // >>> KEEP THE EXISTING BODY OF DrawResolutionLabel UNCHANGED. <<<
    }

    private enum Direction
    {
        Up,
        Down,
        Left,
        Right,
        UpLeft,
        UpRight,
        DownLeft,
        DownRight
    }
}
```

The two `KEEP THE EXISTING BODY` markers mean: copy the existing method bodies from the current file verbatim (they reference only `ArrowSize`, `Severity`, `GetAccentBaseColor`, `GetNewDimensions` — all still available). Delete everything else from the old file: the state fields, `IsActive`, `ImagePixel*`, `Extend*`, `HasExtension`, `IsDragging`, `RegionChanged`, `GetNewDimensions`, `SetImageBounds`, `Reset`, `SetExtension`, `SetAspectRatio`, `OnPointer*`, `GetCursorForPoint`, `GetExtendedScreenRect`, `GetHandleCenters`, `HitTestHandle`, `IsPointNearHandle` — they now live in the base.

- [ ] **Step 7: Run the regression tests again**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~OutpaintToolRegressionTests"`
Expected: all 10 PASS.

- [ ] **Step 8: Build the solution and run the whole editor test folder**

Run: `dotnet build DiffusionNexus.sln -c Debug` then `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~DiffusionNexus.Tests.ImageEditor"`
Expected: build succeeds with no new warnings in `OutpaintTool.cs`; all tests PASS. (`ImageEditorControl` still compiles because every member it uses — `IsActive`, `OnPointer*`, `GetCursorForPoint`, `RegionChanged`, `Reset`, `SetAspectRatio`, `Extend*`, `GetNewDimensions`, `ImagePixel*`, `HasExtension` — is public on the base.)

- [ ] **Step 9: Commit**

```bash
git add DiffusionNexus.UI/ImageEditor/CanvasExtensionTool.cs DiffusionNexus.UI/ImageEditor/OutpaintTool.cs DiffusionNexus.Tests/ImageEditor/OutpaintToolRegressionTests.cs
git commit -m "refactor(editor): extract CanvasExtensionTool base from OutpaintTool

Outward-only frame state, drag math, aspect/target-size presets and
ShrinkAttempted move to an abstract base so the Canvas Extend tool can
share them. Outpaint behaviour pinned by regression tests.

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 2: `CanvasExtendTool` + tool id + core instance and render hook

**Files:**
- Create: `DiffusionNexus.UI/ImageEditor/CanvasExtendTool.cs`
- Modify: `DiffusionNexus.UI/ImageEditor/Services/ToolIds.cs`
- Modify: `DiffusionNexus.UI/ImageEditor/ImageEditorCore.cs` (property near line 124; `RenderWithZoom` near line 990)
- Test: `DiffusionNexus.Tests/ImageEditor/CanvasExtendToolTests.cs`

**Interfaces:**
- Consumes: `CanvasExtensionTool` (Task 1).
- Produces: `sealed class CanvasExtendTool : CanvasExtensionTool` (`FitMargin` 32, hit radius 12, handles on the frame); `ToolIds.CanvasExtend`; `ImageEditorCore.CanvasExtendTool` property.

- [ ] **Step 1: Write the failing tests**

```csharp
// DiffusionNexus.Tests/ImageEditor/CanvasExtendToolTests.cs
using DiffusionNexus.UI.ImageEditor;
using FluentAssertions;
using SkiaSharp;

namespace DiffusionNexus.Tests.ImageEditor;

/// <summary>
/// The Canvas Extend tool: crop-style round handles that sit ON the extended frame,
/// outward-only dragging, a checkerboard preview of the new (transparent) area, and a
/// frame that is visible from the moment the tool activates.
/// </summary>
public class CanvasExtendToolTests
{
    private const int Size = 1000;

    private static CanvasExtendTool CreateActive(float scale = 1f)
    {
        var tool = new CanvasExtendTool { IsActive = true, ImagePixelWidth = Size, ImagePixelHeight = Size };
        tool.SetImageBounds(new SKRect(0, 0, Size * scale, Size * scale));
        return tool;
    }

    [Fact]
    public void FitMargin_IsThirtyTwo()
    {
        CreateActive().FitMargin.Should().Be(32f);
    }

    [Fact]
    public void Handles_SitOnFrameCornersAndEdgeMidpoints_WithTwelvePixelHitRadius()
    {
        var tool = CreateActive();

        tool.GetCursorForPoint(new SKPoint(0, 0)).Should().Be(OutpaintHandle.TopLeft);
        tool.GetCursorForPoint(new SKPoint(Size / 2f, 0)).Should().Be(OutpaintHandle.Top);
        tool.GetCursorForPoint(new SKPoint(Size, Size / 2f)).Should().Be(OutpaintHandle.Right);
        tool.GetCursorForPoint(new SKPoint(Size, Size)).Should().Be(OutpaintHandle.BottomRight);
        tool.GetCursorForPoint(new SKPoint(Size + 11, Size / 2f)).Should().Be(OutpaintHandle.Right);
        tool.GetCursorForPoint(new SKPoint(Size + 13, Size / 2f)).Should().Be(OutpaintHandle.None);
        tool.GetCursorForPoint(new SKPoint(Size / 2f, Size / 2f)).Should().Be(OutpaintHandle.None); // no "move" inside
    }

    [Fact]
    public void Handles_FollowTheExtendedFrame()
    {
        var tool = CreateActive();
        tool.SetExtension(0, 200, 0, 0);

        tool.GetCursorForPoint(new SKPoint(Size + 200, Size / 2f)).Should().Be(OutpaintHandle.Right);
        tool.GetCursorForPoint(new SKPoint(Size, Size / 2f)).Should().Be(OutpaintHandle.None);
    }

    [Fact]
    public void DraggingRightHandle_AtHalfZoom_AddsTwoImagePixelsPerScreenPixel()
    {
        var tool = CreateActive(scale: 0.5f); // 1000 px image drawn 500 px wide

        tool.OnPointerPressed(new SKPoint(500, 250)).Should().BeTrue();
        tool.OnPointerMoved(new SKPoint(550, 250));
        tool.OnPointerReleased();

        tool.ExtendRight.Should().Be(100);
        tool.GetNewDimensions().Should().Be((1100, 1000));
    }

    [Fact]
    public void InactiveTool_IgnoresPointer()
    {
        var tool = CreateActive();
        tool.IsActive = false;

        tool.OnPointerPressed(new SKPoint(Size, Size / 2f)).Should().BeFalse();
        tool.GetCursorForPoint(new SKPoint(Size, Size / 2f)).Should().Be(OutpaintHandle.None);
    }

    [Fact]
    public void Render_WhenActiveWithoutExtension_DrawsTheFrameOnTheImageEdge()
    {
        var tool = CreateActive();
        using var bitmap = new SKBitmap(1200, 1200, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Black);

        tool.Render(canvas, new SKRect(0, 0, 1200, 1200));

        // A handle is drawn at the top-left corner of the frame (white fill).
        bitmap.GetPixel(0, 0).Should().Be(SKColors.White);
        // Nothing is drawn far away from the frame.
        bitmap.GetPixel(1150, 1150).Should().Be(SKColors.Black);
    }

    [Fact]
    public void Render_WithExtension_PaintsTheNewAreaAndLeavesTheImageAlone()
    {
        var tool = CreateActive();
        tool.SetExtension(0, 100, 0, 0); // right strip: x in [1000, 1100)
        using var bitmap = new SKBitmap(1200, 1200, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Black);

        tool.Render(canvas, new SKRect(0, 0, 1200, 1200));

        bitmap.GetPixel(1050, 500).Should().NotBe(SKColors.Black, "the new area gets the checker + tint");
        bitmap.GetPixel(500, 500).Should().Be(SKColors.Black, "the image area is not painted over");
    }

    [Fact]
    public void ToolId_IsRegistered()
    {
        DiffusionNexus.UI.ImageEditor.Services.ToolIds.CanvasExtend.Should().Be("CanvasExtend");
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~CanvasExtendToolTests"`
Expected: build FAILS with `The type or namespace name 'CanvasExtendTool' could not be found` and `'ToolIds' does not contain a definition for 'CanvasExtend'`.

- [ ] **Step 3: Add the tool id**

In `DiffusionNexus.UI/ImageEditor/Services/ToolIds.cs`, after `public const string Outpainting = "Outpainting";` add:

```csharp
    public const string CanvasExtend = "CanvasExtend";
```

- [ ] **Step 4: Create the tool**

```csharp
// DiffusionNexus.UI/ImageEditor/CanvasExtendTool.cs
using SkiaSharp;

namespace DiffusionNexus.UI.ImageEditor;

/// <summary>
/// Canvas Extend tool: grows the canvas around the image without generating content.
/// Behaves like the crop tool visually — round handles on the frame's corners and edge
/// midpoints — but the handles only move outward. The new area is previewed as a
/// checkerboard (it stays transparent when applied) with a green tint and a dashed frame.
/// The frame is drawn from the moment the tool activates, so activating it "selects the
/// whole canvas".
/// </summary>
public sealed class CanvasExtendTool : CanvasExtensionTool
{
    private const float HandleRadius = 6f;
    private const float HandleHitRadiusPixels = 12f;
    private const int CheckerCell = 16;

    private static readonly SKColor Accent = new(76, 175, 80);
    private static readonly SKColor Amber = new(255, 193, 7);
    private static readonly SKBitmap CheckerTile = BuildCheckerTile();

    private static readonly OutpaintHandle[] AllHandles =
    [
        OutpaintHandle.TopLeft, OutpaintHandle.Top, OutpaintHandle.TopRight, OutpaintHandle.Right,
        OutpaintHandle.BottomRight, OutpaintHandle.Bottom, OutpaintHandle.BottomLeft, OutpaintHandle.Left
    ];

    /// <inheritdoc />
    public override float FitMargin => 32f;

    /// <inheritdoc />
    protected override float HandleHitRadius => HandleHitRadiusPixels;

    /// <inheritdoc />
    protected override SKPoint GetHandleCenter(OutpaintHandle handle)
    {
        var rect = GetExtendedScreenRect();
        return handle switch
        {
            OutpaintHandle.TopLeft => new SKPoint(rect.Left, rect.Top),
            OutpaintHandle.Top => new SKPoint(rect.MidX, rect.Top),
            OutpaintHandle.TopRight => new SKPoint(rect.Right, rect.Top),
            OutpaintHandle.Right => new SKPoint(rect.Right, rect.MidY),
            OutpaintHandle.BottomRight => new SKPoint(rect.Right, rect.Bottom),
            OutpaintHandle.Bottom => new SKPoint(rect.MidX, rect.Bottom),
            OutpaintHandle.BottomLeft => new SKPoint(rect.Left, rect.Bottom),
            OutpaintHandle.Left => new SKPoint(rect.Left, rect.MidY),
            _ => SKPoint.Empty
        };
    }

    /// <inheritdoc />
    public override void Render(SKCanvas canvas, SKRect canvasBounds)
    {
        if (!IsActive || ImageRect.Width <= 0 || ImageRect.Height <= 0) return;

        var frame = GetExtendedScreenRect();

        if (HasExtension)
        {
            DrawNewArea(canvas, frame);
            DrawImageOutline(canvas);
        }

        DrawFrame(canvas, frame);
        DrawHandles(canvas);
        DrawResolutionLabel(canvas, frame);
    }

    private void DrawNewArea(SKCanvas canvas, SKRect frame)
    {
        using var shader = SKShader.CreateBitmap(CheckerTile, SKShaderTileMode.Repeat, SKShaderTileMode.Repeat);
        using var checkerPaint = new SKPaint { Shader = shader };
        using var tintPaint = new SKPaint { Color = Accent.WithAlpha(40), Style = SKPaintStyle.Fill };

        foreach (var strip in GetExtensionStrips(frame))
        {
            canvas.DrawRect(strip, checkerPaint);
            canvas.DrawRect(strip, tintPaint);
        }
    }

    private IEnumerable<SKRect> GetExtensionStrips(SKRect frame)
    {
        var image = ImageRect;
        if (ExtendTop > 0) yield return new SKRect(frame.Left, frame.Top, frame.Right, image.Top);
        if (ExtendBottom > 0) yield return new SKRect(frame.Left, image.Bottom, frame.Right, frame.Bottom);
        if (ExtendLeft > 0) yield return new SKRect(frame.Left, image.Top, image.Left, image.Bottom);
        if (ExtendRight > 0) yield return new SKRect(image.Right, image.Top, frame.Right, image.Bottom);
    }

    private void DrawImageOutline(SKCanvas canvas)
    {
        using var paint = new SKPaint
        {
            Color = new SKColor(255, 255, 255, 100),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1f,
            IsAntialias = true
        };
        canvas.DrawRect(ImageRect, paint);
    }

    private static void DrawFrame(SKCanvas canvas, SKRect frame)
    {
        using var paint = new SKPaint
        {
            Color = Accent.WithAlpha(200),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2f,
            IsAntialias = true,
            PathEffect = SKPathEffect.CreateDash([8f, 4f], 0f)
        };
        canvas.DrawRect(frame, paint);
    }

    private void DrawHandles(SKCanvas canvas)
    {
        using var fillPaint = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Fill, IsAntialias = true };
        using var activePaint = new SKPaint { Color = IsShrinkBlocked ? Amber : Accent, Style = SKPaintStyle.Fill, IsAntialias = true };
        using var strokePaint = new SKPaint { Color = new SKColor(80, 80, 80), Style = SKPaintStyle.Stroke, StrokeWidth = 1f, IsAntialias = true };

        foreach (var handle in AllHandles)
        {
            var center = GetHandleCenter(handle);
            canvas.DrawCircle(center, HandleRadius, handle == ActiveHandle ? activePaint : fillPaint);
            canvas.DrawCircle(center, HandleRadius, strokePaint);
        }
    }

    private void DrawResolutionLabel(SKCanvas canvas, SKRect frame)
    {
        var (newW, newH) = GetNewDimensions();
        if (newW <= 0 || newH <= 0) return;

        var text = $"{newW} x {newH}";

        using var font = new SKFont(SKTypeface.Default, 12f);
        using var textPaint = new SKPaint { Color = SKColors.White, IsAntialias = true };

        font.MeasureText(text, out var textBounds, textPaint);

        var labelX = frame.MidX - textBounds.Width / 2f;
        var labelY = frame.Top - 8f - HandleRadius;

        // If the label would go above the canvas, place it inside the frame at the top
        if (labelY - textBounds.Height < 0)
            labelY = frame.Top + textBounds.Height + 6f + HandleRadius;

        var bgRect = new SKRect(
            labelX - 6f,
            labelY - textBounds.Height - 2f,
            labelX + textBounds.Width + 6f,
            labelY + 4f);

        using var bgPaint = new SKPaint { Color = new SKColor(0, 0, 0, 180), Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawRoundRect(bgRect, 4f, 4f, bgPaint);
        canvas.DrawText(text, labelX, labelY, font, textPaint);
    }

    private static SKBitmap BuildCheckerTile()
    {
        var tile = new SKBitmap(CheckerCell * 2, CheckerCell * 2, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(tile);
        canvas.Clear(new SKColor(0x2B, 0x2B, 0x2B));
        using var light = new SKPaint { Color = new SKColor(0x3B, 0x3B, 0x3B), Style = SKPaintStyle.Fill };
        canvas.DrawRect(new SKRect(0, 0, CheckerCell, CheckerCell), light);
        canvas.DrawRect(new SKRect(CheckerCell, CheckerCell, CheckerCell * 2, CheckerCell * 2), light);
        return tile;
    }
}
```

- [ ] **Step 5: Add the instance and the render hook to `ImageEditorCore`**

In `DiffusionNexus.UI/ImageEditor/ImageEditorCore.cs`, directly after the `OutpaintTool` property (near line 124):

```csharp
    /// <summary>
    /// Gets the canvas extend tool instance (grow the canvas without generating content).
    /// </summary>
    public CanvasExtendTool CanvasExtendTool { get; } = new();
```

In `RenderWithZoom`, directly after the block that ends with `OutpaintTool.Render(canvas, new SKRect(0, 0, canvasWidth, canvasHeight));` (near line 994), add:

```csharp
            // Update canvas extend tool with current image bounds and render overlay
            CanvasExtendTool.SetImageBounds(imageRect);
            CanvasExtendTool.ImagePixelWidth = imageWidth;
            CanvasExtendTool.ImagePixelHeight = imageHeight;
            CanvasExtendTool.Render(canvas, new SKRect(0, 0, canvasWidth, canvasHeight));
```

- [ ] **Step 6: Run the tests**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~CanvasExtendToolTests"`
Expected: all 8 PASS. If `Render_WhenActiveWithoutExtension_DrawsTheFrameOnTheImageEdge` fails on the `(0,0)` pixel because anti-aliasing blends the handle edge, change the probe to `bitmap.GetPixel(2, 2)` (inside the 6 px radius) — the assertion is "a handle is painted there", not a specific edge pixel.

- [ ] **Step 7: Commit**

```bash
git add DiffusionNexus.UI/ImageEditor/CanvasExtendTool.cs DiffusionNexus.UI/ImageEditor/Services/ToolIds.cs DiffusionNexus.UI/ImageEditor/ImageEditorCore.cs DiffusionNexus.Tests/ImageEditor/CanvasExtendToolTests.cs
git commit -m "feat(editor): CanvasExtendTool with frame handles and transparent-area preview

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 3: Viewport fit includes the active extension frame

**Files:**
- Modify: `DiffusionNexus.UI/ImageEditor/ImageEditorCore.cs` (`CalculateFitRect` near line 607, `RenderWithZoom` near line 929, new statics next to `CalculateFitRectInternal` near line 1004)
- Test: `DiffusionNexus.Tests/ImageEditor/ViewportFitTests.cs`

**Interfaces:**
- Consumes: `CanvasExtensionTool.FitMargin`, `Extend*`, `IsActive`; `ImageEditorCore.CanvasExtendTool` / `OutpaintTool`.
- Produces: `internal static (SKRect ImageRect, float Scale) ImageEditorCore.CalculateFitRectWithExtension(int imageWidth, int imageHeight, int extendLeft, int extendTop, int extendRight, int extendBottom, float margin, float containerWidth, float containerHeight)`.

Check `DiffusionNexus.UI/DiffusionNexus.UI.csproj` (or `Properties/AssemblyInfo.cs`) for `InternalsVisibleTo("DiffusionNexus.Tests")`; if absent, make the new function `public static` instead of `internal static`.

- [ ] **Step 1: Write the failing tests**

```csharp
// DiffusionNexus.Tests/ImageEditor/ViewportFitTests.cs
using DiffusionNexus.UI.ImageEditor;
using DiffusionNexus.UI.ImageEditor.Services;
using FluentAssertions;
using SkiaSharp;

namespace DiffusionNexus.Tests.ImageEditor;

/// <summary>
/// Fit mode must keep an extension tool's frame (image + extension + handle margin) on
/// screen, and must reduce to the plain image fit when nothing is extended.
/// </summary>
public class ViewportFitTests
{
    [Fact]
    public void NoExtensionNoMargin_EqualsPlainFit()
    {
        var (rect, scale) = ImageEditorCore.CalculateFitRectWithExtension(1000, 500, 0, 0, 0, 0, 0f, 800f, 800f);

        scale.Should().BeApproximately(0.8f, 0.0001f);
        rect.Left.Should().BeApproximately(0f, 0.001f);
        rect.Top.Should().BeApproximately(200f, 0.001f);
        rect.Width.Should().BeApproximately(800f, 0.001f);
        rect.Height.Should().BeApproximately(400f, 0.001f);
    }

    [Fact]
    public void Extension_ShrinksScaleSoTheFrameFits_AndOffsetsTheImage()
    {
        // 1000x1000 image + 500 px on the right => 1500x1000 frame in an 800x800 box with a 32 px margin
        var (rect, scale) = ImageEditorCore.CalculateFitRectWithExtension(1000, 1000, 0, 0, 500, 0, 32f, 800f, 800f);

        scale.Should().BeApproximately(736f / 1500f, 0.0001f);
        var frameWidth = 1500f * scale;
        var frameLeft = (800f - frameWidth) / 2f;
        rect.Left.Should().BeApproximately(frameLeft, 0.001f);           // no left extension: image starts at the frame
        rect.Right.Should().BeApproximately(frameLeft + 1000f * scale, 0.001f);
        (frameLeft + frameWidth).Should().BeLessThanOrEqualTo(800f - 32f + 0.001f);
    }

    [Fact]
    public void LeftAndTopExtension_MoveTheImageInsideTheFrame()
    {
        var (rect, scale) = ImageEditorCore.CalculateFitRectWithExtension(1000, 1000, 200, 100, 0, 0, 0f, 600f, 600f);

        // frame 1200x1100 in 600x600 => scale 0.5, frame is 600x550 at (0, 25)
        scale.Should().BeApproximately(0.5f, 0.0001f);
        rect.Left.Should().BeApproximately(100f, 0.001f);  // 0 + 200*0.5
        rect.Top.Should().BeApproximately(75f, 0.001f);    // 25 + 100*0.5
    }

    [Fact]
    public void RenderWithZoom_InFitMode_KeepsFitModeOn()
    {
        // Pre-existing defect: the fit branch wrote through Viewport.ZoomLevel, whose setter
        // clears IsFitMode, so fit mode died on the first render. The extend rule needs it alive.
        using var core = new ImageEditorCore();
        var services = EditorServiceFactory.Create();
        core.SetServices(services);
        using (var bitmap = new SKBitmap(100, 100, SKColorType.Rgba8888, SKAlphaType.Premul))
        {
            bitmap.Erase(SKColors.Red);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            core.LoadImage(data.ToArray());
        }
        using var surface = new SKBitmap(400, 400, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(surface);

        core.RenderWithZoom(canvas, 400, 400, SKColors.Black);
        core.RenderWithZoom(canvas, 400, 400, SKColors.Black);

        services.Viewport.IsFitMode.Should().BeTrue();
        core.ZoomLevel.Should().BeApproximately(4f, 0.0001f);
    }

    [Fact]
    public void RenderWithZoom_InFitMode_ZoomsOutWhenTheExtendToolGrowsTheFrame()
    {
        using var core = new ImageEditorCore();
        core.SetServices(EditorServiceFactory.Create());
        using (var bitmap = new SKBitmap(100, 100, SKColorType.Rgba8888, SKAlphaType.Premul))
        {
            bitmap.Erase(SKColors.Red);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            core.LoadImage(data.ToArray());
        }
        using var surface = new SKBitmap(400, 400, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(surface);

        var plain = core.RenderWithZoom(canvas, 400, 400, SKColors.Black);
        var plainZoom = core.ZoomLevel;

        core.CanvasExtendTool.IsActive = true;
        core.CanvasExtendTool.ImagePixelWidth = 100;
        core.CanvasExtendTool.ImagePixelHeight = 100;
        core.CanvasExtendTool.SetExtension(0, 100, 0, 0); // 200x100 frame

        var extended = core.RenderWithZoom(canvas, 400, 400, SKColors.Black);

        plain.Width.Should().BeApproximately(400f, 0.001f);
        extended.Width.Should().BeLessThan(plain.Width);
        core.ZoomLevel.Should().BeLessThan(plainZoom);
        // frame = image rect + 100 px * scale to the right must stay inside 400 - 32
        (extended.Right + 100f * core.ZoomLevel).Should().BeLessThanOrEqualTo(400f - 32f + 0.001f);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~ViewportFitTests"`
Expected: build FAILS with `'ImageEditorCore' does not contain a definition for 'CalculateFitRectWithExtension'`.

- [ ] **Step 3: Add the pure function and the active-tool lookup**

In `ImageEditorCore.cs`, directly before `private static SKRect CalculateFitRectInternal(int imageWidth, int imageHeight, float containerWidth, float containerHeight)` (near line 1004), add:

```csharp
    /// <summary>
    /// Fits the virtual canvas (image plus the extension on each side) into the container
    /// shrunk by <paramref name="margin"/> on every side, centred, and returns the rectangle
    /// the <b>image</b> occupies inside that frame plus the scale (image px → screen px).
    /// With zero extension and zero margin it equals the plain image fit.
    /// </summary>
    internal static (SKRect ImageRect, float Scale) CalculateFitRectWithExtension(
        int imageWidth, int imageHeight,
        int extendLeft, int extendTop, int extendRight, int extendBottom,
        float margin, float containerWidth, float containerHeight)
    {
        var virtualWidth = imageWidth + extendLeft + extendRight;
        var virtualHeight = imageHeight + extendTop + extendBottom;
        var availableWidth = Math.Max(1f, containerWidth - 2f * margin);
        var availableHeight = Math.Max(1f, containerHeight - 2f * margin);

        var scale = Math.Min(availableWidth / virtualWidth, availableHeight / virtualHeight);

        var frameWidth = virtualWidth * scale;
        var frameHeight = virtualHeight * scale;
        var frameX = (containerWidth - frameWidth) / 2f;
        var frameY = (containerHeight - frameHeight) / 2f;

        var x = frameX + extendLeft * scale;
        var y = frameY + extendTop * scale;
        return (new SKRect(x, y, x + imageWidth * scale, y + imageHeight * scale), scale);
    }

    /// <summary>The extension tool whose frame the viewport must keep on screen, if any.</summary>
    private CanvasExtensionTool? ActiveExtensionTool =>
        CanvasExtendTool.IsActive ? CanvasExtendTool
        : OutpaintTool.IsActive ? OutpaintTool
        : null;

    /// <summary>
    /// Fit rectangle for the image, honouring the active extension tool's frame and margin.
    /// </summary>
    private SKRect FitImageRect(int imageWidth, int imageHeight, float containerWidth, float containerHeight, out float scale)
    {
        var tool = ActiveExtensionTool;
        if (tool is null)
        {
            var plain = CalculateFitRectInternal(imageWidth, imageHeight, containerWidth, containerHeight);
            scale = plain.Width / imageWidth;
            return plain;
        }

        var (rect, s) = CalculateFitRectWithExtension(
            imageWidth, imageHeight,
            tool.ExtendLeft, tool.ExtendTop, tool.ExtendRight, tool.ExtendBottom,
            tool.FitMargin, containerWidth, containerHeight);
        scale = s;
        return rect;
    }
```

- [ ] **Step 4: Use it in `RenderWithZoom` and `CalculateFitRect`**

In `RenderWithZoom`, replace

```csharp
            if (_isFitMode)
            {
                imageRect = CalculateFitRectInternal(imageWidth, imageHeight, canvasWidth, canvasHeight);
                // Update zoom level to reflect fit
                var fitScale = imageRect.Width / imageWidth;
                _zoomLevel = fitScale;
            }
```

with

```csharp
            if (_isFitMode)
            {
                // Fit honours an active extension tool's frame so it never runs off screen.
                imageRect = FitImageRect(imageWidth, imageHeight, canvasWidth, canvasHeight, out var fitScale);
                // Write through SetFitModeWithZoom, NOT the _zoomLevel setter: that setter goes to
                // Viewport.ZoomLevel, which clears IsFitMode, so fit mode used to switch itself off
                // on the very first render after load.
                if (Math.Abs(_zoomLevel - fitScale) > 0.0001f)
                    _services?.Viewport.SetFitModeWithZoom(fitScale);
            }
```

Replace the body of `public SKRect CalculateFitRect(float containerWidth, float containerHeight)` with:

```csharp
        lock (_bitmapLock)
        {
            int imageWidth, imageHeight;
            if (_isLayerMode && _layers != null && _layers.Count > 0)
            {
                imageWidth = _layers.Width;
                imageHeight = _layers.Height;
            }
            else if (_workingBitmap is not null)
            {
                imageWidth = _workingBitmap.Width;
                imageHeight = _workingBitmap.Height;
            }
            else
            {
                return SKRect.Empty;
            }

            return FitImageRect(imageWidth, imageHeight, containerWidth, containerHeight, out _);
        }
```

If, after this, the old `private static SKRect CalculateFitRectInternal(SKBitmap bitmap, float containerWidth, float containerHeight)` overload has no remaining callers, delete it.

- [ ] **Step 5: Run the tests**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~ViewportFitTests|FullyQualifiedName~ViewportManagerTests|FullyQualifiedName~ImageEditorCoreRenderRaceTests"`
Expected: all PASS.

- [ ] **Step 6: Commit**

```bash
git add DiffusionNexus.UI/ImageEditor/ImageEditorCore.cs DiffusionNexus.Tests/ImageEditor/ViewportFitTests.cs
git commit -m "feat(editor): fit mode keeps an extension tool's frame on screen

Applies to Canvas Extend and Outpaint (whose frame ran off screen before).

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 4: `ImageEditorCore.ApplyCanvasExtend()`

**Files:**
- Modify: `DiffusionNexus.UI/ImageEditor/ImageEditorCore.cs` (add after `ApplyCrop()` near line 809)
- Test: `DiffusionNexus.Tests/ImageEditor/ImageEditorCoreCanvasExtendTests.cs`

**Interfaces:**
- Consumes: `CanvasExtendTool` (Task 2), `ILayerManager.ResizeCanvas(int, int, int, int)` (existing), `FileLogger.LogError(string, Exception)` (existing).
- Produces: `public bool ImageEditorCore.ApplyCanvasExtend()`.

- [ ] **Step 1: Write the failing tests**

```csharp
// DiffusionNexus.Tests/ImageEditor/ImageEditorCoreCanvasExtendTests.cs
using DiffusionNexus.UI.ImageEditor;
using DiffusionNexus.UI.ImageEditor.Services;
using FluentAssertions;
using SkiaSharp;

namespace DiffusionNexus.Tests.ImageEditor;

/// <summary>
/// Applying a canvas extension grows every layer and the working bitmap, keeps the old
/// content at the offset, leaves the new pixels transparent, and resets the tool.
/// </summary>
public class ImageEditorCoreCanvasExtendTests : IDisposable
{
    private readonly ImageEditorCore _sut;

    public ImageEditorCoreCanvasExtendTests()
    {
        _sut = new ImageEditorCore();
        _sut.SetServices(EditorServiceFactory.Create());

        using var bitmap = new SKBitmap(100, 80, SKColorType.Rgba8888, SKAlphaType.Premul);
        bitmap.Erase(SKColors.Red);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        _sut.LoadImage(data.ToArray());

        _sut.CanvasExtendTool.IsActive = true;
        _sut.CanvasExtendTool.ImagePixelWidth = 100;
        _sut.CanvasExtendTool.ImagePixelHeight = 80;
    }

    public void Dispose() => _sut.Dispose();

    [Fact]
    public void WithoutExtension_ReturnsFalse_AndChangesNothing()
    {
        _sut.ApplyCanvasExtend().Should().BeFalse();

        _sut.Width.Should().Be(100);
        _sut.Height.Should().Be(80);
    }

    [Fact]
    public void WithExtension_GrowsTheCanvas_KeepsContentAtOffset_NewPixelsTransparent()
    {
        _sut.CanvasExtendTool.SetExtension(top: 10, right: 20, bottom: 30, left: 40);
        var changed = 0;
        _sut.ImageChanged += (_, _) => changed++;

        _sut.ApplyCanvasExtend().Should().BeTrue();

        _sut.Width.Should().Be(160);
        _sut.Height.Should().Be(120);
        changed.Should().BeGreaterThanOrEqualTo(1);

        var layerBitmap = _sut.Layers![0].Bitmap!;
        layerBitmap.Width.Should().Be(160);
        layerBitmap.GetPixel(40, 10).Should().Be(SKColors.Red);      // old (0,0) moved to the offset
        layerBitmap.GetPixel(139, 89).Should().Be(SKColors.Red);     // old (99,79)
        layerBitmap.GetPixel(0, 0).Alpha.Should().Be(0);             // new area transparent
        layerBitmap.GetPixel(159, 119).Alpha.Should().Be(0);
    }

    [Fact]
    public void AfterApply_ToolIsReset()
    {
        _sut.CanvasExtendTool.SetExtension(0, 50, 0, 0);

        _sut.ApplyCanvasExtend();

        _sut.CanvasExtendTool.HasExtension.Should().BeFalse();
    }

    [Fact]
    public void ApplyTwice_Accumulates()
    {
        _sut.CanvasExtendTool.SetExtension(0, 50, 0, 0);
        _sut.ApplyCanvasExtend();
        _sut.CanvasExtendTool.ImagePixelWidth = _sut.Width; // the view refreshes this on every render
        _sut.CanvasExtendTool.SetExtension(0, 0, 0, 50);

        _sut.ApplyCanvasExtend().Should().BeTrue();

        _sut.Width.Should().Be(200);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~ImageEditorCoreCanvasExtendTests"`
Expected: build FAILS with `'ImageEditorCore' does not contain a definition for 'ApplyCanvasExtend'`.

- [ ] **Step 3: Implement**

In `ImageEditorCore.cs`, directly after `ApplyCrop()`:

```csharp
    /// <summary>
    /// Grows the canvas by the <see cref="CanvasExtendTool"/>'s current extension. Every
    /// layer and the working bitmap are resized; existing content keeps its position
    /// relative to the new top-left offset; new pixels are transparent. Resets the tool.
    /// </summary>
    /// <returns>True when the canvas was extended.</returns>
    public bool ApplyCanvasExtend()
    {
        var tool = CanvasExtendTool;
        if (!HasImage || !tool.HasExtension)
            return false;

        var offsetX = tool.ExtendLeft;
        var offsetY = tool.ExtendTop;
        var newWidth = Width + tool.ExtendLeft + tool.ExtendRight;
        var newHeight = Height + tool.ExtendTop + tool.ExtendBottom;
        FileLogger.Log($"Canvas extend: {Width}x{Height} -> {newWidth}x{newHeight} (offset {offsetX},{offsetY})");

        SKBitmap? replacedWorking = null;
        try
        {
            lock (_bitmapLock)
            {
                if (_isLayerMode && _layers != null)
                {
                    _services?.Layers.ResizeCanvas(newWidth, newHeight, offsetX, offsetY);
                }

                if (_workingBitmap is not null)
                {
                    var grown = new SKBitmap(newWidth, newHeight, SKColorType.Rgba8888, SKAlphaType.Premul);
                    grown.Erase(SKColors.Transparent);
                    using (var canvas = new SKCanvas(grown))
                    {
                        canvas.DrawBitmap(_workingBitmap, offsetX, offsetY);
                    }
                    replacedWorking = _workingBitmap;
                    _workingBitmap = grown;
                }
            }
        }
        catch (OutOfMemoryException ex)
        {
            FileLogger.LogError($"Canvas extend to {newWidth}x{newHeight} ran out of memory", ex);
            return false;
        }

        replacedWorking?.Dispose();
        tool.Reset();
        OnImageChanged();
        return true;
    }
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~ImageEditorCoreCanvasExtendTests"`
Expected: all 4 PASS. If `_sut.Layers![0]` fails because `LoadImage` does not enter layer mode in this configuration, replace the pixel assertions' source with `_sut.Layers!.Flatten()!` (dispose it after) — the behaviour under test is the same.

- [ ] **Step 5: Commit**

```bash
git add DiffusionNexus.UI/ImageEditor/ImageEditorCore.cs DiffusionNexus.Tests/ImageEditor/ImageEditorCoreCanvasExtendTests.cs
git commit -m "feat(editor): ApplyCanvasExtend grows layers and working bitmap with transparent pixels

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 5: `CanvasExtendViewModel` and `ImageEditorViewModel` wiring

**Files:**
- Create: `DiffusionNexus.UI/ViewModels/CanvasExtendViewModel.cs`
- Modify: `DiffusionNexus.UI/ViewModels/ImageEditorViewModel.cs` (property near line 106, constructor near line 377 and 472–478, `DeactivateOtherTools` near line 486, `CloseAllTools` near line 518, `NotifyToolCommandsCanExecuteChanged` near line 536)
- Modify: `DiffusionNexus.Tests/ImageEditor/Services/ToolManagerTests.cs`
- Test: `DiffusionNexus.Tests/ViewModels/CanvasExtendViewModelTests.cs`

**Interfaces:**
- Consumes: `ToolIds.CanvasExtend` (Task 2), `IUnifiedLogger.Info(LogCategory, string, string)` (existing).
- Produces: `CanvasExtendViewModel` with `bool IsPanelOpen`, `string ResolutionText`, `string OriginalSizeText`, `bool HasExtension`, `int TargetWidth`, `int TargetHeight`, `bool IsShrinkHintVisible`, `const string ShrinkHintText`; commands `ToggleCommand`, `CancelCommand`, `ApplyCommand`, `MultiplyCommand` (`IRelayCommand<string>`, parameters `"2xW" "3xW" "2xH" "3xH"`), `SetAspectRatioCommand` (`IRelayCommand<string>`, `"16:9"` form), `OpenCropCommand`; events `ToolActivated`, `ToolDeactivated`, `ToolToggled` (`(string ToolId, bool IsActive)`), `ToolStateChanged`, `StatusMessageChanged` (`string?`), `ApplyRequested`, `TargetSizeRequested` (`(int Width, int Height)`), `SetAspectRatioRequested` (`(float W, float H)`), `OpenCropRequested`; methods `UpdateResolution(int newWidth, int newHeight, bool hasExtension)`, `OnShrinkAttempted()`, `OnApplied(int newWidth, int newHeight)`, `ClosePanel()`, `RefreshCommandStates()`. `ImageEditorViewModel.CanvasExtend` property.

- [ ] **Step 1: Write the failing ViewModel tests**

```csharp
// DiffusionNexus.Tests/ViewModels/CanvasExtendViewModelTests.cs
using DiffusionNexus.UI.ImageEditor.Services;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;

namespace DiffusionNexus.Tests.ViewModels;

/// <summary>
/// Panel state for the Canvas Extend tool: mutual exclusion on open, Apply gated on an
/// actual extension, typed sizes clamped to the image with the shrink hint, multipliers
/// and presets forwarded as requests, and no echo when the tool reports back.
/// </summary>
public class CanvasExtendViewModelTests
{
    private readonly List<string> _deactivated = [];
    private readonly CanvasExtendViewModel _sut;

    public CanvasExtendViewModelTests()
    {
        _sut = new CanvasExtendViewModel(() => true, () => 1024, () => 768, id => _deactivated.Add(id));
    }

    [Fact]
    public void Opening_DeactivatesOtherTools_AndRaisesToggleForItsOwnId()
    {
        (string ToolId, bool IsActive)? toggled = null;
        _sut.ToolToggled += (_, args) => toggled = args;
        var activated = 0;
        _sut.ToolActivated += (_, _) => activated++;

        _sut.IsPanelOpen = true;

        _deactivated.Should().ContainSingle().Which.Should().Be(ToolIds.CanvasExtend);
        toggled.Should().Be((ToolIds.CanvasExtend, true));
        activated.Should().Be(1);
    }

    [Fact]
    public void Apply_IsDisabledUntilThereIsAnExtension()
    {
        _sut.IsPanelOpen = true;
        _sut.ApplyCommand.CanExecute(null).Should().BeFalse();

        _sut.UpdateResolution(2048, 768, hasExtension: true);

        _sut.ApplyCommand.CanExecute(null).Should().BeTrue();
        _sut.ResolutionText.Should().Be("2048 x 768");
        _sut.OriginalSizeText.Should().Be("from 1024 x 768");
    }

    [Fact]
    public void TypedWidth_AtOrAboveImage_RaisesTargetSizeRequested()
    {
        _sut.IsPanelOpen = true;
        _sut.UpdateResolution(1024, 768, hasExtension: false);
        (int Width, int Height)? requested = null;
        _sut.TargetSizeRequested += (_, args) => requested = args;

        _sut.TargetWidth = 1500;

        requested.Should().Be((1500, 768));
        _sut.IsShrinkHintVisible.Should().BeFalse();
    }

    [Fact]
    public void TypedWidth_BelowImage_ClampsShowsHint_AndRaisesNothing()
    {
        _sut.IsPanelOpen = true;
        _sut.UpdateResolution(1024, 768, hasExtension: false);
        var requests = 0;
        _sut.TargetSizeRequested += (_, _) => requests++;

        _sut.TargetWidth = 800;

        _sut.TargetWidth.Should().Be(1024);
        _sut.IsShrinkHintVisible.Should().BeTrue();
        requests.Should().Be(0);
    }

    [Fact]
    public void Multiplier_UsesTheImageDimension_AndKeepsTheOtherTarget()
    {
        _sut.IsPanelOpen = true;
        _sut.UpdateResolution(1024, 900, hasExtension: true); // height already extended to 900
        (int Width, int Height)? requested = null;
        _sut.TargetSizeRequested += (_, args) => requested = args;

        _sut.MultiplyCommand.Execute("2xW");
        requested.Should().Be((2048, 900));

        _sut.MultiplyCommand.Execute("3xH");
        requested.Should().Be((1024, 2304)); // width target is still the reported 1024
    }

    [Fact]
    public void AspectPreset_IsForwarded()
    {
        _sut.IsPanelOpen = true;
        (float W, float H)? requested = null;
        _sut.SetAspectRatioRequested += (_, args) => requested = args;

        _sut.SetAspectRatioCommand.Execute("16:9");

        requested.Should().Be((16f, 9f));
    }

    [Fact]
    public void UpdateResolution_DoesNotEchoATargetSizeRequest()
    {
        _sut.IsPanelOpen = true;
        var requests = 0;
        _sut.TargetSizeRequested += (_, _) => requests++;

        _sut.UpdateResolution(1300, 768, hasExtension: true);

        _sut.TargetWidth.Should().Be(1300);
        requests.Should().Be(0);
    }

    [Fact]
    public void ShrinkHint_ClearsWhenTheCanvasGrows_AndOnApplied()
    {
        _sut.IsPanelOpen = true;
        _sut.UpdateResolution(1024, 768, hasExtension: false);
        _sut.OnShrinkAttempted();
        _sut.IsShrinkHintVisible.Should().BeTrue();

        _sut.UpdateResolution(1100, 768, hasExtension: true);
        _sut.IsShrinkHintVisible.Should().BeFalse();

        _sut.OnShrinkAttempted();
        _sut.OnApplied(1100, 768);

        _sut.IsShrinkHintVisible.Should().BeFalse();
        _sut.IsPanelOpen.Should().BeFalse();
    }

    [Fact]
    public void OpenCrop_RaisesRequest()
    {
        _sut.IsPanelOpen = true;
        var raised = 0;
        _sut.OpenCropRequested += (_, _) => raised++;

        _sut.OpenCropCommand.Execute(null);

        raised.Should().Be(1);
    }

    [Fact]
    public void ClosePanel_RaisesDeactivated_WithoutTouchingOtherTools()
    {
        _sut.IsPanelOpen = true;
        _deactivated.Clear();
        var deactivatedEvents = 0;
        _sut.ToolDeactivated += (_, _) => deactivatedEvents++;

        _sut.ClosePanel();

        _sut.IsPanelOpen.Should().BeFalse();
        deactivatedEvents.Should().Be(1);
        _deactivated.Should().BeEmpty();
    }
}
```

Append to `DiffusionNexus.Tests/ImageEditor/Services/ToolManagerTests.cs` (inside the class, in the mutual-exclusion region):

```csharp
    [Fact]
    public void WhenCanvasExtendActivates_CropIsDeactivated()
    {
        _sut.Activate(ToolIds.Crop);

        _sut.Activate(ToolIds.CanvasExtend);

        _sut.ActiveToolId.Should().Be(ToolIds.CanvasExtend);
        _sut.IsActive(ToolIds.Crop).Should().BeFalse();
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~CanvasExtendViewModelTests"`
Expected: build FAILS with `The type or namespace name 'CanvasExtendViewModel' could not be found`.

- [ ] **Step 3: Create the ViewModel**

```csharp
// DiffusionNexus.UI/ViewModels/CanvasExtendViewModel.cs
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.UI.ImageEditor.Services;
using Serilog;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// Panel state for the Canvas Extend tool. Owns nothing pixel-related: it raises requests
/// (target size, aspect preset, apply) that the view forwards to
/// <see cref="ImageEditor.CanvasExtendTool"/>, and it is told the result through
/// <see cref="UpdateResolution"/> / <see cref="OnShrinkAttempted"/> / <see cref="OnApplied"/>.
/// </summary>
public partial class CanvasExtendViewModel : ObservableObject
{
    private static readonly ILogger Logger = Log.ForContext<CanvasExtendViewModel>();
    private const string LogSource = "CanvasExtend";

    /// <summary>Shown when the user tries to make the canvas smaller than the image.</summary>
    public const string ShrinkHintText = "The canvas can only grow here. To cut the image down, use the Crop tool.";

    private readonly Func<bool> _hasImage;
    private readonly Func<int> _getImageWidth;
    private readonly Func<int> _getImageHeight;
    private readonly Action<string> _deactivateOtherTools;
    private readonly IUnifiedLogger? _unifiedLogger;

    private bool _isPanelOpen;
    private string _resolutionText = string.Empty;
    private string _originalSizeText = string.Empty;
    private bool _hasExtension;
    private int _targetWidth;
    private int _targetHeight;
    private bool _isShrinkHintVisible;
    private bool _syncing;
    private long _lastReportedArea;

    public CanvasExtendViewModel(
        Func<bool> hasImage,
        Func<int> getImageWidth,
        Func<int> getImageHeight,
        Action<string> deactivateOtherTools,
        IUnifiedLogger? unifiedLogger = null)
    {
        ArgumentNullException.ThrowIfNull(hasImage);
        ArgumentNullException.ThrowIfNull(getImageWidth);
        ArgumentNullException.ThrowIfNull(getImageHeight);
        ArgumentNullException.ThrowIfNull(deactivateOtherTools);

        _hasImage = hasImage;
        _getImageWidth = getImageWidth;
        _getImageHeight = getImageHeight;
        _deactivateOtherTools = deactivateOtherTools;
        _unifiedLogger = unifiedLogger;

        ToggleCommand = new RelayCommand(() => IsPanelOpen = !IsPanelOpen, () => _hasImage());
        CancelCommand = new RelayCommand(() => IsPanelOpen = false, () => IsPanelOpen);
        ApplyCommand = new RelayCommand(ExecuteApply, () => _hasImage() && IsPanelOpen && HasExtension);
        MultiplyCommand = new RelayCommand<string>(ExecuteMultiply, _ => _hasImage() && IsPanelOpen);
        SetAspectRatioCommand = new RelayCommand<string>(ExecuteSetAspectRatio, _ => _hasImage() && IsPanelOpen);
        OpenCropCommand = new RelayCommand(() => OpenCropRequested?.Invoke(this, EventArgs.Empty), () => IsPanelOpen);
    }

    #region Properties

    /// <summary>Whether the Extend panel (and tool) is open.</summary>
    public bool IsPanelOpen
    {
        get => _isPanelOpen;
        set
        {
            if (!SetProperty(ref _isPanelOpen, value)) return;

            IsShrinkHintVisible = false;
            if (value)
            {
                _deactivateOtherTools(ToolIds.CanvasExtend);
                EmitInfo($"panel opened for {_getImageWidth()}x{_getImageHeight()}");
                ToolActivated?.Invoke(this, EventArgs.Empty);
                StatusMessageChanged?.Invoke(this, "Extend: Drag a handle outward or type a size. Press Enter to apply, Escape to reset.");
            }
            else
            {
                EmitInfo("panel closed");
                ToolDeactivated?.Invoke(this, EventArgs.Empty);
                StatusMessageChanged?.Invoke(this, null);
            }

            RefreshCommandStates();
            ToolToggled?.Invoke(this, (ToolIds.CanvasExtend, value));
            ToolStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Resolution text for the extended canvas (e.g. "2048 x 1024").</summary>
    public string ResolutionText
    {
        get => _resolutionText;
        private set => SetProperty(ref _resolutionText, value);
    }

    /// <summary>"from W x H" line under the resolution.</summary>
    public string OriginalSizeText
    {
        get => _originalSizeText;
        private set => SetProperty(ref _originalSizeText, value);
    }

    /// <summary>True once the tool reports any extension; gates Apply.</summary>
    public bool HasExtension
    {
        get => _hasExtension;
        private set
        {
            if (SetProperty(ref _hasExtension, value))
                ApplyCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>Target canvas width in pixels (two-way bound to the W field).</summary>
    public int TargetWidth
    {
        get => _targetWidth;
        set => SetTarget(ref _targetWidth, value, _getImageWidth(), nameof(TargetWidth));
    }

    /// <summary>Target canvas height in pixels (two-way bound to the H field).</summary>
    public int TargetHeight
    {
        get => _targetHeight;
        set => SetTarget(ref _targetHeight, value, _getImageHeight(), nameof(TargetHeight));
    }

    /// <summary>Whether the "canvas can only grow, use Crop" hint is showing.</summary>
    public bool IsShrinkHintVisible
    {
        get => _isShrinkHintVisible;
        private set => SetProperty(ref _isShrinkHintVisible, value);
    }

    #endregion

    #region Commands

    public IRelayCommand ToggleCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public IRelayCommand ApplyCommand { get; }
    /// <summary>Parameter: "2xW", "3xW", "2xH" or "3xH".</summary>
    public IRelayCommand<string> MultiplyCommand { get; }
    /// <summary>Parameter: "W:H", e.g. "16:9".</summary>
    public IRelayCommand<string> SetAspectRatioCommand { get; }
    public IRelayCommand OpenCropCommand { get; }

    #endregion

    #region Events

    public event EventHandler? ToolActivated;
    public event EventHandler? ToolDeactivated;
    public event EventHandler<(string ToolId, bool IsActive)>? ToolToggled;
    public event EventHandler? ToolStateChanged;
    public event EventHandler<string?>? StatusMessageChanged;
    public event EventHandler? ApplyRequested;
    public event EventHandler<(int Width, int Height)>? TargetSizeRequested;
    public event EventHandler<(float W, float H)>? SetAspectRatioRequested;
    public event EventHandler? OpenCropRequested;

    #endregion

    #region Public Methods

    /// <summary>Notifies all commands that their CanExecute state may have changed.</summary>
    public void RefreshCommandStates()
    {
        ToggleCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        ApplyCommand.NotifyCanExecuteChanged();
        MultiplyCommand.NotifyCanExecuteChanged();
        SetAspectRatioCommand.NotifyCanExecuteChanged();
        OpenCropCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Closes the panel without deactivating other tools (called by tool coordination).</summary>
    public void ClosePanel()
    {
        if (!_isPanelOpen) return;

        _isPanelOpen = false;
        OnPropertyChanged(nameof(IsPanelOpen));
        IsShrinkHintVisible = false;
        ToolDeactivated?.Invoke(this, EventArgs.Empty);
        RefreshCommandStates();
    }

    /// <summary>
    /// Called by the view whenever the tool's region changes (and once on activation).
    /// Syncs the text, the Apply gate and the size fields without echoing a request back.
    /// </summary>
    public void UpdateResolution(int newWidth, int newHeight, bool hasExtension)
    {
        ResolutionText = newWidth > 0 && newHeight > 0 ? $"{newWidth} x {newHeight}" : string.Empty;
        OriginalSizeText = $"from {_getImageWidth()} x {_getImageHeight()}";
        HasExtension = hasExtension;

        var area = (long)Math.Max(0, newWidth) * Math.Max(0, newHeight);
        if (_lastReportedArea > 0 && area > _lastReportedArea)
            IsShrinkHintVisible = false;
        _lastReportedArea = area;

        _syncing = true;
        try
        {
            TargetWidth = newWidth;
            TargetHeight = newHeight;
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>Called by the view when the tool blocked an inward drag.</summary>
    public void OnShrinkAttempted()
    {
        EmitInfo("shrink attempt blocked (handle pulled inside the image)");
        IsShrinkHintVisible = true;
    }

    /// <summary>Called by the view after the core applied the extension.</summary>
    public void OnApplied(int newWidth, int newHeight)
    {
        EmitInfo($"applied -> {newWidth}x{newHeight}");
        ResolutionText = string.Empty;
        HasExtension = false;
        _lastReportedArea = 0;
        IsPanelOpen = false;
        StatusMessageChanged?.Invoke(this, $"Canvas extended to {newWidth} x {newHeight}");
    }

    #endregion

    #region Command Implementations

    private void ExecuteApply()
    {
        EmitInfo($"apply requested ({ResolutionText})");
        ApplyRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ExecuteMultiply(string? factor)
    {
        if (string.IsNullOrWhiteSpace(factor) || factor.Length != 3) return;
        if (!int.TryParse(factor[..1], out var k) || k < 1) return;

        var width = _targetWidth > 0 ? _targetWidth : _getImageWidth();
        var height = _targetHeight > 0 ? _targetHeight : _getImageHeight();

        switch (char.ToUpperInvariant(factor[2]))
        {
            case 'W': width = k * _getImageWidth(); break;
            case 'H': height = k * _getImageHeight(); break;
            default: return;
        }

        EmitInfo($"multiplier {factor} -> target {width}x{height}");
        TargetSizeRequested?.Invoke(this, (width, height));
    }

    private void ExecuteSetAspectRatio(string? ratio)
    {
        if (string.IsNullOrWhiteSpace(ratio)) return;

        var parts = ratio.Split(':');
        if (parts.Length != 2 ||
            !float.TryParse(parts[0], out var w) ||
            !float.TryParse(parts[1], out var h))
            return;

        EmitInfo($"aspect preset {ratio}");
        SetAspectRatioRequested?.Invoke(this, (w, h));
    }

    #endregion

    private void SetTarget(ref int field, int value, int imageDimension, string propertyName)
    {
        if (_syncing)
        {
            SetProperty(ref field, value, propertyName);
            return;
        }

        var minimum = Math.Max(1, imageDimension);
        if (value < minimum)
        {
            EmitInfo($"typed {propertyName} {value} is below the image ({minimum}); clamped");
            field = minimum;
            OnPropertyChanged(propertyName);
            IsShrinkHintVisible = true;
            return;
        }

        if (!SetProperty(ref field, value, propertyName)) return;

        EmitInfo($"target size {_targetWidth}x{_targetHeight}");
        TargetSizeRequested?.Invoke(this, (_targetWidth, _targetHeight));
    }

    private void EmitInfo(string message)
    {
        Logger.Information("CanvasExtend: {Message}", message);
        _unifiedLogger?.Info(LogCategory.Configuration, LogSource, message);
    }
}
```

If `LogCategory` does not resolve, add `using DiffusionNexus.Domain.Enums;` (copy the `using` block of `OutpaintingViewModel.cs`).

- [ ] **Step 4: Wire it into `ImageEditorViewModel`**

Property, after `public OutpaintingViewModel Outpainting { get; }` (near line 106):

```csharp
    /// <summary>Sub-ViewModel for the Canvas Extend tool.</summary>
    public CanvasExtendViewModel CanvasExtend { get; }
```

Constructor, directly after the `Outpainting = new OutpaintingViewModel(...)` line (near line 377):

```csharp
        CanvasExtend = new CanvasExtendViewModel(() => HasImage, () => ImageWidth, () => ImageHeight, DeactivateOtherTools, unifiedLogger);
```

Constructor, directly after the `Outpainting.ToolToggled += ...` block (ends near line 478):

```csharp
        CanvasExtend.ToolStateChanged += (_, _) => NotifyToolCommandsCanExecuteChanged();
        CanvasExtend.StatusMessageChanged += (_, msg) => StatusMessage = msg;
        CanvasExtend.ToolToggled += (_, args) =>
        {
            if (args.IsActive) _services.Tools.Activate(args.ToolId);
            else _services.Tools.Deactivate(args.ToolId);
        };
        // "Open Crop" from the shrink hint: same path as the Crop toolbar toggle.
        CanvasExtend.OpenCropRequested += (_, _) =>
        {
            if (!IsCropToolActive) ExecuteToggleCropTool();
        };
```

`DeactivateOtherTools`, after the `Outpainting.ClosePanel();` line:

```csharp
        if (exceptToolId != ToolIds.CanvasExtend)
            CanvasExtend.ClosePanel();
```

`CloseAllTools`, after `Outpainting.ClosePanel();`:

```csharp
        CanvasExtend.ClosePanel();
```

`NotifyToolCommandsCanExecuteChanged`, after `Outpainting.RefreshCommandStates();`:

```csharp
        CanvasExtend.RefreshCommandStates();
```

- [ ] **Step 5: Run the tests**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj --filter "FullyQualifiedName~CanvasExtendViewModelTests|FullyQualifiedName~ToolManagerTests"`
Expected: all PASS (10 new + the ToolManager suite).

- [ ] **Step 6: Build and commit**

Run: `dotnet build DiffusionNexus.sln -c Debug` — Expected: success.

```bash
git add DiffusionNexus.UI/ViewModels/CanvasExtendViewModel.cs DiffusionNexus.UI/ViewModels/ImageEditorViewModel.cs DiffusionNexus.Tests/ViewModels/CanvasExtendViewModelTests.cs DiffusionNexus.Tests/ImageEditor/Services/ToolManagerTests.cs
git commit -m "feat(editor): CanvasExtendViewModel wired into tool coordination

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 6: `ImageEditorControl` integration

**Files:**
- Modify: `DiffusionNexus.UI/Controls/ImageEditorControl.cs` — fields near line 40, `IsOutpaintToolActive` near line 226, pointer pressed near line 582, pointer moved near line 691, pointer released near line 784, keys near line 910, cursor near line 1010, `ApplyCrop` near line 1219, `OnOutpaintRegionChanged` near line 1383, attach/detach near line 1411 / 1431.

**Interfaces:**
- Consumes: `ImageEditorCore.CanvasExtendTool`, `ImageEditorCore.ApplyCanvasExtend()`, `CanvasExtensionTool.ShrinkAttempted`.
- Produces: `bool IsCanvasExtendToolActive`, `bool ApplyCanvasExtend()`, events `CanvasExtendRegionChanged`, `CanvasExtendShrinkAttempted`, `CanvasExtendApplied`.

No unit test: the control is Avalonia UI without existing test coverage; it is verified by the solution build here and by the manual smoke in Task 8.

- [ ] **Step 1: Field and property**

After `private bool _isOutpaintToolActive;` (near line 41):

```csharp
    // Canvas extend tool state
    private bool _isCanvasExtendToolActive;
```

After the `IsOutpaintToolActive` property (ends near line 240):

```csharp
    /// <summary>
    /// Gets or sets whether the canvas extend tool is active.
    /// </summary>
    public bool IsCanvasExtendToolActive
    {
        get => _isCanvasExtendToolActive;
        set
        {
            _isCanvasExtendToolActive = value;
            _editorCore.CanvasExtendTool.IsActive = value;
            InvalidateVisual();
        }
    }
```

- [ ] **Step 2: Pointer routing**

In `OnPointerPressed`, directly after the Outpaint block (`// Outpaint tool takes priority when active ... }`), before `if (_editorCore.CropTool.OnPointerPressed(skPoint))`:

```csharp
        // Canvas extend tool takes priority when active
        if (_isCanvasExtendToolActive && props.IsLeftButtonPressed)
        {
            if (_editorCore.CanvasExtendTool.OnPointerPressed(skPoint))
            {
                e.Handled = true;
                InvalidateVisual();
                Focus();
                return;
            }
        }
```

In `OnPointerMoved`, directly after the `// Outpaint tool pointer tracking` block:

```csharp
        // Canvas extend tool pointer tracking
        if (_isCanvasExtendToolActive)
        {
            if (_editorCore.CanvasExtendTool.OnPointerMoved(skPoint))
            {
                e.Handled = true;
                InvalidateVisual();
                return;
            }
        }
```

In `OnPointerReleased`, directly after the `// Outpaint tool release` block:

```csharp
        // Canvas extend tool release
        if (_isCanvasExtendToolActive)
        {
            if (_editorCore.CanvasExtendTool.OnPointerReleased())
            {
                e.Handled = true;
                InvalidateVisual();
                return;
            }
        }
```

- [ ] **Step 3: Cursor**

In the cursor update method, directly after the `// Outpaint tool cursors` block (ends with `return;` near line 1024):

```csharp
        // Canvas extend tool cursors (handles sit on the frame)
        if (_isCanvasExtendToolActive)
        {
            var extendHandle = _editorCore.CanvasExtendTool.GetCursorForPoint(point);
            Cursor = extendHandle switch
            {
                ImageEditor.OutpaintHandle.Top or ImageEditor.OutpaintHandle.Bottom => new Cursor(StandardCursorType.SizeNorthSouth),
                ImageEditor.OutpaintHandle.Left or ImageEditor.OutpaintHandle.Right => new Cursor(StandardCursorType.SizeWestEast),
                ImageEditor.OutpaintHandle.TopLeft or ImageEditor.OutpaintHandle.TopRight
                    or ImageEditor.OutpaintHandle.BottomLeft or ImageEditor.OutpaintHandle.BottomRight
                    => new Cursor(StandardCursorType.SizeAll),
                _ => Cursor.Default
            };
            return;
        }
```

- [ ] **Step 4: Keys**

In `OnKeyDown`, directly before the `// Apply crop with C or Enter when crop tool is active` block:

```csharp
        // Canvas extend: Enter applies, Escape resets the extension (tool stays open, like Crop)
        if (_isCanvasExtendToolActive)
        {
            if (e.Key == Key.Enter)
            {
                ApplyCanvasExtend();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Escape)
            {
                _editorCore.CanvasExtendTool.Reset();
                InvalidateVisual();
                e.Handled = true;
                return;
            }
        }
```

- [ ] **Step 5: Apply method and events**

After `ApplyCrop()` (near line 1228):

```csharp
    /// <summary>
    /// Event raised when a canvas extension has been applied.
    /// </summary>
    public event EventHandler? CanvasExtendApplied;

    /// <summary>
    /// Applies the canvas extend tool's current extension.
    /// </summary>
    public bool ApplyCanvasExtend()
    {
        var result = _editorCore.ApplyCanvasExtend();
        if (result)
        {
            CanvasExtendApplied?.Invoke(this, EventArgs.Empty);
        }
        InvalidateVisual();
        return result;
    }
```

After `OnOutpaintRegionChanged` (near line 1387):

```csharp
    /// <summary>
    /// Event raised when the canvas extend region changes.
    /// </summary>
    public event EventHandler? CanvasExtendRegionChanged;

    /// <summary>
    /// Event raised when the canvas extend tool blocked an attempt to shrink the canvas.
    /// </summary>
    public event EventHandler? CanvasExtendShrinkAttempted;

    private void OnCanvasExtendRegionChanged(object? sender, EventArgs e)
    {
        CanvasExtendRegionChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    private void OnCanvasExtendShrinkAttempted(object? sender, EventArgs e)
    {
        CanvasExtendShrinkAttempted?.Invoke(this, EventArgs.Empty);
    }
```

In `OnAttachedToVisualTree`, after `_editorCore.OutpaintTool.RegionChanged += OnOutpaintRegionChanged;`:

```csharp
        _editorCore.CanvasExtendTool.RegionChanged += OnCanvasExtendRegionChanged;
        _editorCore.CanvasExtendTool.ShrinkAttempted += OnCanvasExtendShrinkAttempted;
```

In `OnDetachedFromVisualTree`, after `_editorCore.OutpaintTool.RegionChanged -= OnOutpaintRegionChanged;`:

```csharp
        _editorCore.CanvasExtendTool.RegionChanged -= OnCanvasExtendRegionChanged;
        _editorCore.CanvasExtendTool.ShrinkAttempted -= OnCanvasExtendShrinkAttempted;
```

- [ ] **Step 6: Build**

Run: `dotnet build DiffusionNexus.sln -c Debug`
Expected: success, no new warnings.

- [ ] **Step 7: Commit**

```bash
git add DiffusionNexus.UI/Controls/ImageEditorControl.cs
git commit -m "feat(editor): route pointer, cursor and keys to the canvas extend tool

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 7: Toolbar toggle, panel XAML and view wiring

**Files:**
- Modify: `DiffusionNexus.UI/Views/Tabs/ImageEditView.axaml` (toolbar near line 137; insert the panel after the Outpaint panel, i.e. before the `<!-- Color Balance Panel -->` comment near line 531)
- Modify: `DiffusionNexus.UI/Views/Tabs/ImageEditView.axaml.cs` (constructor wiring near line 235; new method after `WireOutpaintingEvents`, which ends near line 501)

**Interfaces:**
- Consumes: `ImageEditorViewModel.CanvasExtend` (Task 5), `ImageEditorControl.IsCanvasExtendToolActive` / `ApplyCanvasExtend()` / events (Task 6), `ImageEditorCore.CanvasExtendTool` (Task 2).

- [ ] **Step 1: Toolbar toggle**

Directly after the `Outpaint` `ToggleButton` (the element ending with `IsEnabled="{Binding ImageEditor.HasImage}"/>` near line 139), add:

```xml
            <ToggleButton Content="Extend" IsChecked="{Binding ImageEditor.CanvasExtend.IsPanelOpen, Mode=TwoWay}" Padding="12,6"
                          ToolTip.Tip="Extend Canvas - Grow the canvas without generating content. The new area stays transparent"
                          IsEnabled="{Binding ImageEditor.HasImage}"/>
```

- [ ] **Step 2: Panel**

Directly before `<!-- Color Balance Panel -->` (near line 531), add:

```xml
          <!-- Canvas Extend Panel -->
          <StackPanel Spacing="8" IsVisible="{Binding ImageEditor.CanvasExtend.IsPanelOpen, FallbackValue=False}">
            <TextBlock Text="Extend Canvas" FontWeight="SemiBold" FontSize="14"/>
            <Border Background="#2A2A2A" CornerRadius="4" Padding="8">
              <StackPanel Spacing="8">
                <TextBlock Text="Drag a handle outward or type a new size. The new area stays transparent, so use Fill afterwards to colour it."
                           FontSize="11" Opacity="0.7" TextWrapping="Wrap"/>

                <!-- New size -->
                <TextBlock Text="{Binding ImageEditor.CanvasExtend.ResolutionText}" FontSize="12" FontWeight="SemiBold"
                           Foreground="#4CAF50" HorizontalAlignment="Center"
                           IsVisible="{Binding ImageEditor.CanvasExtend.ResolutionText, Converter={x:Static StringConverters.IsNotNullOrEmpty}}"/>
                <TextBlock Text="{Binding ImageEditor.CanvasExtend.OriginalSizeText}" FontSize="11" Opacity="0.7"
                           HorizontalAlignment="Center" Margin="0,-4,0,0"
                           IsVisible="{Binding ImageEditor.CanvasExtend.HasExtension}"/>

                <Border Height="1" Background="#444" Margin="0,4"/>

                <!-- Canvas size -->
                <TextBlock Text="Canvas size" FontSize="11" Opacity="0.7"/>
                <Grid ColumnDefinitions="Auto,*,8,Auto,*">
                  <TextBlock Grid.Column="0" Text="W" FontSize="11" Opacity="0.7" VerticalAlignment="Center" Margin="0,0,6,0"/>
                  <NumericUpDown Grid.Column="1" Value="{Binding ImageEditor.CanvasExtend.TargetWidth, Mode=TwoWay}"
                                 Minimum="{Binding ImageEditor.ImageWidth}" Maximum="32768" Increment="1"
                                 FormatString="0" ClipValueToMinMax="True" FontSize="12" MinWidth="0"
                                 ToolTip.Tip="New canvas width in pixels (cannot go below the image width)"/>
                  <TextBlock Grid.Column="3" Text="H" FontSize="11" Opacity="0.7" VerticalAlignment="Center" Margin="0,0,6,0"/>
                  <NumericUpDown Grid.Column="4" Value="{Binding ImageEditor.CanvasExtend.TargetHeight, Mode=TwoWay}"
                                 Minimum="{Binding ImageEditor.ImageHeight}" Maximum="32768" Increment="1"
                                 FormatString="0" ClipValueToMinMax="True" FontSize="12" MinWidth="0"
                                 ToolTip.Tip="New canvas height in pixels (cannot go below the image height)"/>
                </Grid>
                <StackPanel Orientation="Horizontal" Spacing="4" HorizontalAlignment="Center">
                  <Button Content="2× W" Command="{Binding ImageEditor.CanvasExtend.MultiplyCommand}" CommandParameter="2xW"
                          Padding="8,4" FontSize="10" ToolTip.Tip="Double the width"/>
                  <Button Content="3× W" Command="{Binding ImageEditor.CanvasExtend.MultiplyCommand}" CommandParameter="3xW"
                          Padding="8,4" FontSize="10" ToolTip.Tip="Triple the width"/>
                  <Button Content="2× H" Command="{Binding ImageEditor.CanvasExtend.MultiplyCommand}" CommandParameter="2xH"
                          Padding="8,4" FontSize="10" ToolTip.Tip="Double the height"/>
                  <Button Content="3× H" Command="{Binding ImageEditor.CanvasExtend.MultiplyCommand}" CommandParameter="3xH"
                          Padding="8,4" FontSize="10" ToolTip.Tip="Triple the height"/>
                </StackPanel>

                <!-- Aspect Ratio Presets -->
                <TextBlock Text="Extend to aspect ratio" FontSize="11" Opacity="0.7"/>
                <StackPanel Orientation="Horizontal" Spacing="4" HorizontalAlignment="Center">
                  <Button Content="16:9" Command="{Binding ImageEditor.CanvasExtend.SetAspectRatioCommand}" CommandParameter="16:9"
                          Padding="8,4" FontSize="10" ToolTip.Tip="Extend to 16:9 widescreen"/>
                  <Button Content="9:16" Command="{Binding ImageEditor.CanvasExtend.SetAspectRatioCommand}" CommandParameter="9:16"
                          Padding="8,4" FontSize="10" ToolTip.Tip="Extend to 9:16 portrait"/>
                  <Button Content="4:3" Command="{Binding ImageEditor.CanvasExtend.SetAspectRatioCommand}" CommandParameter="4:3"
                          Padding="8,4" FontSize="10" ToolTip.Tip="Extend to 4:3"/>
                  <Button Content="3:4" Command="{Binding ImageEditor.CanvasExtend.SetAspectRatioCommand}" CommandParameter="3:4"
                          Padding="8,4" FontSize="10" ToolTip.Tip="Extend to 3:4"/>
                  <Button Content="1:1" Command="{Binding ImageEditor.CanvasExtend.SetAspectRatioCommand}" CommandParameter="1:1"
                          Padding="8,4" FontSize="10" ToolTip.Tip="Extend to 1:1 square"/>
                </StackPanel>

                <Border Height="1" Background="#444" Margin="0,4"/>

                <!-- Shrink attempt: the canvas can only grow here -->
                <Border Background="#2A2210" CornerRadius="6" Padding="10,8"
                        IsVisible="{Binding ImageEditor.CanvasExtend.IsShrinkHintVisible}">
                  <StackPanel Orientation="Horizontal" Spacing="8">
                    <Ellipse Width="10" Height="10" Fill="#FFC107" VerticalAlignment="Top" Margin="0,3,0,0"/>
                    <StackPanel Spacing="6">
                      <TextBlock Text="{x:Static vm:CanvasExtendViewModel.ShrinkHintText}"
                                 FontSize="11" Foreground="#FFD54F" TextWrapping="Wrap" MaxWidth="220"/>
                      <Button Content="Open Crop" Command="{Binding ImageEditor.CanvasExtend.OpenCropCommand}"
                              Padding="8,4" FontSize="10" HorizontalAlignment="Left"/>
                    </StackPanel>
                  </StackPanel>
                </Border>

                <!-- Action Buttons -->
                <Grid ColumnDefinitions="*,*" Margin="0,4,0,0">
                  <Button Grid.Column="0" Content="Cancel" Command="{Binding ImageEditor.CanvasExtend.CancelCommand}" HorizontalAlignment="Stretch" Margin="0,0,2,0"/>
                  <Button Grid.Column="1" Content="Apply" Command="{Binding ImageEditor.CanvasExtend.ApplyCommand}" Background="#2D7D46" HorizontalAlignment="Stretch" Margin="2,0,0,0"/>
                </Grid>
              </StackPanel>
            </Border>
          </StackPanel>
```

Check the `xmlns:vm` prefix at the top of `ImageEditView.axaml`: it must map to `using:DiffusionNexus.UI.ViewModels` for the `x:Static vm:CanvasExtendViewModel.ShrinkHintText` binding. If the file uses a different prefix for that namespace, use that prefix.

- [ ] **Step 3: Code-behind wiring**

In the constructor, after `WireOutpaintingEvents(imageEditor);` (near line 235):

```csharp
        WireCanvasExtendEvents(imageEditor);
```

After the `WireOutpaintingEvents` method (ends near line 501), add:

```csharp
    private void WireCanvasExtendEvents(ImageEditorViewModel imageEditor)
    {
        EventHandler onActivated = (_, _) =>
        {
            _imageEditorCanvas!.IsCanvasExtendToolActive = true;
            // Push the initial state so the panel shows the current size before any drag.
            var tool = _imageEditorCanvas.EditorCore.CanvasExtendTool;
            tool.ImagePixelWidth = _imageEditorCanvas.EditorCore.Width;
            tool.ImagePixelHeight = _imageEditorCanvas.EditorCore.Height;
            var (w, h) = tool.GetNewDimensions();
            imageEditor.CanvasExtend.UpdateResolution(w, h, tool.HasExtension);
        };
        imageEditor.CanvasExtend.ToolActivated += onActivated;
        _eventCleanup.Add(() => imageEditor.CanvasExtend.ToolActivated -= onActivated);

        EventHandler onDeactivated = (_, _) =>
        {
            _imageEditorCanvas!.IsCanvasExtendToolActive = false;
            _imageEditorCanvas.EditorCore.CanvasExtendTool.Reset();
            _imageEditorCanvas.InvalidateVisual();
        };
        imageEditor.CanvasExtend.ToolDeactivated += onDeactivated;
        _eventCleanup.Add(() => imageEditor.CanvasExtend.ToolDeactivated -= onDeactivated);

        EventHandler<(int Width, int Height)> onTargetSize = (_, size) =>
        {
            _imageEditorCanvas!.EditorCore.CanvasExtendTool.SetTargetSize(size.Width, size.Height);
            _imageEditorCanvas.InvalidateVisual();
        };
        imageEditor.CanvasExtend.TargetSizeRequested += onTargetSize;
        _eventCleanup.Add(() => imageEditor.CanvasExtend.TargetSizeRequested -= onTargetSize);

        EventHandler<(float W, float H)> onAspect = (_, ratio) =>
        {
            _imageEditorCanvas!.EditorCore.CanvasExtendTool.SetAspectRatio(ratio.W, ratio.H);
            _imageEditorCanvas.InvalidateVisual();
        };
        imageEditor.CanvasExtend.SetAspectRatioRequested += onAspect;
        _eventCleanup.Add(() => imageEditor.CanvasExtend.SetAspectRatioRequested -= onAspect);

        EventHandler onApply = (_, _) => _imageEditorCanvas!.ApplyCanvasExtend();
        imageEditor.CanvasExtend.ApplyRequested += onApply;
        _eventCleanup.Add(() => imageEditor.CanvasExtend.ApplyRequested -= onApply);

        EventHandler onRegionChanged = (_, _) =>
        {
            var tool = _imageEditorCanvas!.EditorCore.CanvasExtendTool;
            var (w, h) = tool.GetNewDimensions();
            imageEditor.CanvasExtend.UpdateResolution(w, h, tool.HasExtension);
        };
        _imageEditorCanvas!.CanvasExtendRegionChanged += onRegionChanged;
        _eventCleanup.Add(() => _imageEditorCanvas!.CanvasExtendRegionChanged -= onRegionChanged);

        EventHandler onShrink = (_, _) => imageEditor.CanvasExtend.OnShrinkAttempted();
        _imageEditorCanvas.CanvasExtendShrinkAttempted += onShrink;
        _eventCleanup.Add(() => _imageEditorCanvas!.CanvasExtendShrinkAttempted -= onShrink);

        EventHandler onApplied = (_, _) =>
        {
            var core = _imageEditorCanvas!.EditorCore;
            imageEditor.CanvasExtend.OnApplied(core.Width, core.Height);
            imageEditor.LayerPanel.SyncLayers(core.Layers);
            imageEditor.UpdateDimensions(core.Width, core.Height);
        };
        _imageEditorCanvas.CanvasExtendApplied += onApplied;
        _eventCleanup.Add(() => _imageEditorCanvas!.CanvasExtendApplied -= onApplied);
    }
```

- [ ] **Step 4: Build and run the app once**

Run: `dotnet build DiffusionNexus.sln -c Debug` — Expected: success (XAML compiles; a wrong `vm:` prefix or property name fails here).

Run the app, open the Image Editor with any image, click **Extend**: the frame with eight round handles appears around the image, the panel shows "W x H" and the size fields hold the image size. Drag the right handle outward: the frame grows, the view zooms out, the label updates. Click **Cancel**: everything returns to normal.

- [ ] **Step 5: Commit**

```bash
git add DiffusionNexus.UI/Views/Tabs/ImageEditView.axaml DiffusionNexus.UI/Views/Tabs/ImageEditView.axaml.cs
git commit -m "feat(editor): Extend toolbar toggle and Extend Canvas panel

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 8: Docs, full test run, manual smoke, PR

**Files:**
- Modify: `DiffusionNexus.UI/Doc/Shortcuts.md`
- Modify: `DiffusionNexus.UI/ImageEditor/ARCHITECTURE.md`

- [ ] **Step 1: Shortcuts**

Append to `DiffusionNexus.UI/Doc/Shortcuts.md`:

```markdown

## Image Editor

| Key | Action | Context |
|-----|--------|---------|
| Enter | Apply canvas extension | Extend tool active |
| Escape | Reset canvas extension (tool stays open) | Extend tool active |
```

- [ ] **Step 2: Architecture doc**

In `DiffusionNexus.UI/ImageEditor/ARCHITECTURE.md`, in the "`ImageEditor/` — Core Types" table, after the `CropTool.cs` row add:

```markdown
| `CanvasExtensionTool.cs` | Abstract base for tools that grow the canvas outward: extension state, outward-only drag math, aspect/target-size presets, `ShrinkAttempted` |
| `OutpaintTool.cs` | Outpaint frame (arrow handles, AI severity tint) on top of `CanvasExtensionTool` |
| `CanvasExtendTool.cs` | Canvas Extend frame (round handles on the frame, checkerboard preview of the transparent new area) |
```

And after the "Zoom In" data-flow example add:

```markdown
### Canvas Extend
```
User → Extend toggle → CanvasExtendViewModel.IsPanelOpen = true
     → View sets ImageEditorControl.IsCanvasExtendToolActive → CanvasExtendTool.IsActive
     → drag / SetTargetSize / SetAspectRatio → RegionChanged → View → ViewModel.UpdateResolution
     → RenderWithZoom fit includes the frame + FitMargin while any extension tool is active
     → Apply (button / Enter) → ImageEditorCore.ApplyCanvasExtend()
     → LayerManager.ResizeCanvas + working bitmap grown, transparent new pixels → ImageChanged
```
```

- [ ] **Step 3: Full test run and build**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj`
Expected: all PASS (pre-existing flakes, if any, are documented in memory; re-run once before treating a failure as new).

Run: `dotnet build DiffusionNexus.sln -c Release` — Expected: success.

- [ ] **Step 4: Manual smoke (record results in the PR description)**

With a 1024×1024 PNG loaded in the Image Editor:

1. Click **Extend** → frame with 8 round handles on the image edge, panel shows `1024 x 1024`, fields show 1024 / 1024, Apply disabled.
2. Drag the right handle outward → frame grows, view zooms out, label and fields update, Apply enabled.
3. Drag the same handle back past the image edge → handle turns amber, stops at the edge, the amber hint with **Open Crop** appears; drag outward again → hint disappears.
4. Click **2× W** → `2048 x 1024`, symmetric strips left and right with checkerboard + green tint.
5. Type `1500` in H → `2048 x 1500`; type `500` in H → field snaps back to 1024 and the hint shows.
6. Click **16:9** → symmetric extension to 16:9.
7. Press **Escape** → extension reset, tool still open. Click **2× W**, press **Enter** → canvas is 2048×1024, panel closes, status says "Canvas extended to 2048 x 1024", Layers panel shows the same layers, view re-fits.
8. Click **Fill** and fill the transparent area with a colour → strips are coloured.
9. Reset, click **2× W**, **Apply**, then **Save / Export → Export as PNG**; open the PNG: the side strips are transparent.
10. Click **Outpaint**, drag to 3× width → the frame stays on screen (previously ran off screen).
11. Click **Open Crop** from the hint (after step 3) → Extend closes, Crop opens.

- [ ] **Step 5: Commit and open the PR**

```bash
git add DiffusionNexus.UI/Doc/Shortcuts.md DiffusionNexus.UI/ImageEditor/ARCHITECTURE.md
git commit -m "docs(editor): Canvas Extend shortcuts and architecture notes

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
git push -u origin feature/canvas-extend
gh pr create --base develop --title "feat(editor): Canvas Extend tool" --body "$(cat <<'EOF'
## Summary
- New **Extend** tool in the Image Editor: grows the canvas around the image with transparent new pixels. Drag the frame handles outward, type W/H, 2x/3x buttons, aspect presets. Enter applies, Escape resets.
- `OutpaintTool` refactored onto a shared `CanvasExtensionTool` base (behaviour pinned by regression tests).
- Fit mode now keeps an active extension frame on screen (also fixes the Outpaint frame running off screen).

Spec: `docs/superpowers/specs/2026-09-02-canvas-extend-design.md`. Mockups: https://claude.ai/code/artifact/3e066a50-4879-422d-b184-58d37aa57e17

## Test plan
- [ ] `dotnet test` green (new: OutpaintToolRegressionTests, CanvasExtendToolTests, ViewportFitTests, ImageEditorCoreCanvasExtendTests, CanvasExtendViewModelTests)
- [ ] Manual smoke steps 1-11 from the plan (`docs/superpowers/plans/2026-09-02-canvas-extend.md`, Task 8)

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```
